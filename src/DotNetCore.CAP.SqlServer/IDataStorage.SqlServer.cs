// Copyright (c) .NET Core Community. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using DotNetCore.CAP.Internal;
using DotNetCore.CAP.Messages;
using DotNetCore.CAP.Monitoring;
using DotNetCore.CAP.Persistence;
using DotNetCore.CAP.Serialization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace DotNetCore.CAP.SqlServer;

public class SqlServerDataStorage : IDataStorage
{
    private readonly IOptions<CapOptions> _capOptions;
    private readonly IStorageInitializer _initializer;
    private readonly IOptions<SqlServerOptions> _options;
    private readonly string _pubName;
    private readonly string _recName;
    private readonly ISerializer _serializer;

    public SqlServerDataStorage(
        IOptions<CapOptions> capOptions,
        IOptions<SqlServerOptions> options,
        IStorageInitializer initializer,
        ISerializer serializer)
    {
        _options = options;
        _initializer = initializer;
        _capOptions = capOptions;
        _serializer = serializer;
        _pubName = initializer.GetPublishedTableName();
        _recName = initializer.GetReceivedTableName();
    }

    public async Task ChangePublishStateAsync(MediumMessage message, StatusName state)
    {
        await ChangeMessageStateAsync(_pubName, message, state).ConfigureAwait(false);
    }

    public async Task ChangeReceiveStateAsync(MediumMessage message, StatusName state)
    {
        await ChangeMessageStateAsync(_recName, message, state).ConfigureAwait(false);
    }

    public async Task<MediumMessage> StoreMessageAsync(string name, Message content, object? dbTransaction = null)
    {
        var sql =
            $"INSERT INTO {_pubName} ([Id],[Version],[Name],[Content],[Retries],[Added],[ExpiresAt],[StatusName])" +
            $"VALUES(@Id,'{_options.Value.Version}',@Name,@Content,@Retries,@Added,@ExpiresAt,@StatusName);";

        var message = new MediumMessage
        {
            DbId = content.GetId(),
            Origin = content,
            Content = _serializer.Serialize(content),
            Added = DateTime.Now,
            ExpiresAt = null,
            Retries = 0
        };

        object[] sqlParams =
        {
            new SqlParameter("@Id", message.DbId),
            new SqlParameter("@Name", name),
            new SqlParameter("@Content", message.Content),
            new SqlParameter("@Retries", message.Retries),
            new SqlParameter("@Added", message.Added),
            new SqlParameter("@ExpiresAt", message.ExpiresAt.HasValue ? message.ExpiresAt.Value : DBNull.Value),
            new SqlParameter("@StatusName", nameof(StatusName.Scheduled))
        };

        if (dbTransaction == null)
        {
            var connection = new SqlConnection(_options.Value.ConnectionString);
            await using var _ = connection.ConfigureAwait(false);
            await connection.ExecuteNonQueryAsync(sql, sqlParams: sqlParams).ConfigureAwait(false);
        }
        else
        {
            var dbTrans = dbTransaction as DbTransaction;
            if (dbTrans == null && dbTransaction is IDbContextTransaction dbContextTrans)
                dbTrans = dbContextTrans.GetDbTransaction();

            var conn = dbTrans?.Connection;
            await conn!.ExecuteNonQueryAsync(sql, dbTrans, sqlParams).ConfigureAwait(false);
        }

        return message;
    }

    public async Task StoreReceivedExceptionMessageAsync(string name, string group, string content)
    {
        object[] sqlParams =
        {
            new SqlParameter("@Id", SnowflakeId.Default().NextId().ToString()),
            new SqlParameter("@Name", name),
            new SqlParameter("@Group", group),
            new SqlParameter("@Content", content),
            new SqlParameter("@Retries", _capOptions.Value.FailedRetryCount),
            new SqlParameter("@Added", DateTime.Now),
            new SqlParameter("@ExpiresAt", DateTime.Now.AddSeconds(_capOptions.Value.FailedMessageExpiredAfter)),
            new SqlParameter("@StatusName", nameof(StatusName.Failed))
        };

        await StoreReceivedMessage(sqlParams).ConfigureAwait(false);
    }

    public async Task<MediumMessage> StoreReceivedMessageAsync(string name, string group, Message message)
    {
        var mdMessage = new MediumMessage
        {
            DbId = SnowflakeId.Default().NextId().ToString(),
            Origin = message,
            Added = DateTime.Now,
            ExpiresAt = null,
            Retries = 0
        };

        object[] sqlParams =
        {
            new SqlParameter("@Id", mdMessage.DbId),
            new SqlParameter("@Name", name),
            new SqlParameter("@Group", group),
            new SqlParameter("@Content", _serializer.Serialize(mdMessage.Origin)),
            new SqlParameter("@Retries", mdMessage.Retries),
            new SqlParameter("@Added", mdMessage.Added),
            new SqlParameter("@ExpiresAt", mdMessage.ExpiresAt.HasValue ? mdMessage.ExpiresAt.Value : DBNull.Value),
            new SqlParameter("@StatusName", nameof(StatusName.Scheduled))
        };

        await StoreReceivedMessage(sqlParams).ConfigureAwait(false);

        return mdMessage;
    }

    public async Task<int> DeleteExpiresAsync(string table, DateTime timeout, int batchCount = 1000,
        CancellationToken token = default)
    {
        var connection = new SqlConnection(_options.Value.ConnectionString);
        await using var _ = connection.ConfigureAwait(false);
        return await connection.ExecuteNonQueryAsync(
            $"DELETE TOP (@batchCount) FROM {table} WITH (readpast) WHERE ExpiresAt < @timeout AND (StatusName='{StatusName.Succeeded}' OR StatusName='{StatusName.Failed}');", null,
            new SqlParameter("@timeout", timeout), new SqlParameter("@batchCount", batchCount)).ConfigureAwait(false);
    }

    public async Task<IEnumerable<MediumMessage>> GetPublishedMessagesOfNeedRetry()
    {
        return await GetMessagesOfNeedRetryAsync(_pubName).ConfigureAwait(false);
    }

    public async Task<IEnumerable<MediumMessage>> GetReceivedMessagesOfNeedRetry()
    {
        return await GetMessagesOfNeedRetryAsync(_recName).ConfigureAwait(false);
    }

    public async Task<IEnumerable<MediumMessage>> GetPublishedMessagesOfDelayed()
    {
        var sql =
            $"SELECT TOP 200 Id,Content,Retries,Added,ExpiresAt FROM `{_pubName}` WHERE Version=@Version " +
            $"AND ((ExpiresAt< @TwoMinutesLater AND StatusName = '{StatusName.Delayed}') OR (ExpiresAt< @OneMinutesAgo AND StatusName = '{StatusName.Queued}'))";

        object[] sqlParams =
        {
            new SqlParameter("@Version", _capOptions.Value.Version),
            new SqlParameter("@TwoMinutesLater", DateTime.Now.AddMinutes(2)),
            new SqlParameter("@OneMinutesAgo", DateTime.Now.AddMinutes(-1)),
        };

        var connection = new SqlConnection(_options.Value.ConnectionString);
        await using var _ = connection.ConfigureAwait(false);
        var result = await connection.ExecuteReaderAsync(sql, async reader =>
        {
            var messages = new List<MediumMessage>();
            while (await reader.ReadAsync().ConfigureAwait(false))
                messages.Add(new MediumMessage
                {
                    DbId = reader.GetInt64(0).ToString(),
                    Origin = _serializer.Deserialize(reader.GetString(1))!,
                    Retries = reader.GetInt32(2),
                    Added = reader.GetDateTime(3),
                    ExpiresAt = reader.GetDateTime(4)
                });

            return messages;
        }, sqlParams).ConfigureAwait(false);

        return result;
    }

    public IMonitoringApi GetMonitoringApi()
    {
        return new SqlServerMonitoringApi(_options, _initializer);
    }

    private async Task ChangeMessageStateAsync(string tableName, MediumMessage message, StatusName state)
    {
        var sql =
            $"UPDATE {tableName} SET Content=@Content, Retries=@Retries,ExpiresAt=@ExpiresAt,StatusName=@StatusName WHERE Id=@Id";

        object[] sqlParams =
        {
            new SqlParameter("@Id", message.DbId),
            new SqlParameter("@Content", _serializer.Serialize(message.Origin)),
            new SqlParameter("@Retries", message.Retries),
            new SqlParameter("@ExpiresAt", message.ExpiresAt),
            new SqlParameter("@StatusName", state.ToString("G"))
        };

        var connection = new SqlConnection(_options.Value.ConnectionString);
        await using var _ = connection.ConfigureAwait(false);
        await connection.ExecuteNonQueryAsync(sql, sqlParams: sqlParams).ConfigureAwait(false);
    }

    private async Task StoreReceivedMessage(object[] sqlParams)
    {
        var sql =
            $"INSERT INTO {_recName}([Id],[Version],[Name],[Group],[Content],[Retries],[Added],[ExpiresAt],[StatusName])" +
            $"VALUES(@Id,'{_capOptions.Value.Version}',@Name,@Group,@Content,@Retries,@Added,@ExpiresAt,@StatusName);";

        var connection = new SqlConnection(_options.Value.ConnectionString);
        await using var _ = connection.ConfigureAwait(false);
        await connection.ExecuteNonQueryAsync(sql, sqlParams: sqlParams).ConfigureAwait(false);
    }

    private async Task<IEnumerable<MediumMessage>> GetMessagesOfNeedRetryAsync(string tableName)
    {
        var fourMinAgo = DateTime.Now.AddMinutes(-4);
        var sql =
            $"SELECT TOP (200) Id, Content, Retries, Added FROM {tableName} WITH (readpast) WHERE Retries<@Retries " +
            $"AND Version=@Version AND Added<@Added AND (StatusName = '{StatusName.Failed}' OR StatusName = '{StatusName.Scheduled}')";

        object[] sqlParams =
        {
            new SqlParameter("@Retries", _capOptions.Value.FailedRetryCount),
            new SqlParameter("@Version", _capOptions.Value.Version),
            new SqlParameter("@Added", fourMinAgo)
        };

        var connection = new SqlConnection(_options.Value.ConnectionString);
        await using var _ = connection.ConfigureAwait(false);
        var result = await connection.ExecuteReaderAsync(sql, async reader =>
        {
            var messages = new List<MediumMessage>();
            while (await reader.ReadAsync().ConfigureAwait(false))
                messages.Add(new MediumMessage
                {
                    DbId = reader.GetInt64(0).ToString(),
                    Origin = _serializer.Deserialize(reader.GetString(1))!,
                    Retries = reader.GetInt32(2),
                    Added = reader.GetDateTime(3)
                });

            return messages;
        }, sqlParams).ConfigureAwait(false);

        return result;
    }

   
}