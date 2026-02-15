using System.Globalization;
using System.Text.Json;
using BlazorOptions.API.Positions;
using BlazorOptions.Server.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace BlazorOptions.Server.Services;

public sealed class PositionsStore
{
    private const string PositionsTable = "Positions";
    private readonly string _userRoot;
    private readonly SemaphoreSlim _readLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _activeReaders;
    private readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web);

    public PositionsStore(IWebHostEnvironment environment, IOptions<DataStorageOptions> dataOptions)
    {
        var dataRoot = ResolveDataRoot(environment.ContentRootPath, dataOptions.Value.Path);
        _userRoot = Path.Combine(dataRoot, "Users");
        Directory.CreateDirectory(_userRoot);
    }

    public async Task<IReadOnlyList<PositionModel>> LoadPositionsAsync(string userId)
    {
        await AcquireReadAsync();
        try
        {
            await using var connection = await OpenConnectionAsync(userId);
            var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT Payload
                FROM {PositionsTable}
                ORDER BY SortIndex ASC, UpdatedUtc ASC
                """;

            var items = new List<PositionModel>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var payload = reader.GetString(0);
                try
                {
                    var item = JsonSerializer.Deserialize<PositionModel>(payload, _serializerOptions);
                    if (item is not null)
                    {
                        items.Add(item);
                    }
                }
                catch
                {
                }
            }

            return items;
        }
        finally
        {
            ReleaseRead();
        }
    }

    public async Task<PositionModel?> LoadPositionAsync(string userId, Guid positionId)
    {
        await AcquireReadAsync();
        try
        {
            await using var connection = await OpenConnectionAsync(userId);
            var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT Payload
                FROM {PositionsTable}
                WHERE PositionId = $positionId
                """;
            command.Parameters.AddWithValue("$positionId", positionId.ToString("N"));
            await using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var payload = reader.GetString(0);
                return JsonSerializer.Deserialize<PositionModel>(payload, _serializerOptions);
            }

            return null;
        }
        finally
        {
            ReleaseRead();
        }
    }

    public async Task SavePositionsAsync(string userId, IReadOnlyList<PositionModel> positions)
    {
        await AcquireWriteAsync();
        try
        {
            await using var connection = await OpenConnectionAsync(userId);
            await using var transaction = connection.BeginTransaction();

            var clear = connection.CreateCommand();
            clear.Transaction = transaction;
            clear.CommandText = $"DELETE FROM {PositionsTable}";
            await clear.ExecuteNonQueryAsync();

            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                INSERT INTO {PositionsTable} (
                    PositionId,
                    Payload,
                    UpdatedUtc,
                    SortIndex
                )
                VALUES (
                    $positionId,
                    $payload,
                    $updatedUtc,
                    $sortIndex
                )
                """;

            var idParam = command.Parameters.Add("$positionId", SqliteType.Text);
            var payloadParam = command.Parameters.Add("$payload", SqliteType.Text);
            var updatedParam = command.Parameters.Add("$updatedUtc", SqliteType.Text);
            var sortParam = command.Parameters.Add("$sortIndex", SqliteType.Integer);
            var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

            for (var i = 0; i < positions.Count; i++)
            {
                var position = positions[i];
                var id = position.Id == Guid.Empty ? Guid.NewGuid() : position.Id;
                position.Id = id;

                idParam.Value = id.ToString("N");
                payloadParam.Value = JsonSerializer.Serialize(position, _serializerOptions);
                updatedParam.Value = now;
                sortParam.Value = i;
                await command.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }
        finally
        {
            ReleaseWrite();
        }
    }

    public async Task SavePositionAsync(string userId, PositionModel position)
    {
        await AcquireWriteAsync();
        try
        {
            await using var connection = await OpenConnectionAsync(userId);
            await using var transaction = connection.BeginTransaction();
            var id = position.Id == Guid.Empty ? Guid.NewGuid() : position.Id;
            position.Id = id;

            var sortIndex = await ResolveSortIndexAsync(connection, transaction, id);
            var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                INSERT INTO {PositionsTable} (PositionId, Payload, UpdatedUtc, SortIndex)
                VALUES ($positionId, $payload, $updatedUtc, $sortIndex)
                ON CONFLICT(PositionId) DO UPDATE SET
                    Payload = $payload,
                    UpdatedUtc = $updatedUtc,
                    SortIndex = $sortIndex
                """;
            command.Parameters.AddWithValue("$positionId", id.ToString("N"));
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(position, _serializerOptions));
            command.Parameters.AddWithValue("$updatedUtc", now);
            command.Parameters.AddWithValue("$sortIndex", sortIndex);
            await command.ExecuteNonQueryAsync();

            await transaction.CommitAsync();
        }
        finally
        {
            ReleaseWrite();
        }
    }

    public async Task DeletePositionAsync(string userId, Guid positionId)
    {
        await AcquireWriteAsync();
        try
        {
            await using var connection = await OpenConnectionAsync(userId);
            var command = connection.CreateCommand();
            command.CommandText = $"""
                DELETE FROM {PositionsTable}
                WHERE PositionId = $positionId
                """;
            command.Parameters.AddWithValue("$positionId", positionId.ToString("N"));
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            ReleaseWrite();
        }
    }

    private async Task<int> ResolveSortIndexAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid positionId)
    {
        var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = $"""
            SELECT SortIndex
            FROM {PositionsTable}
            WHERE PositionId = $positionId
            """;
        select.Parameters.AddWithValue("$positionId", positionId.ToString("N"));
        var result = await select.ExecuteScalarAsync();
        if (result is long existing)
        {
            return (int)existing;
        }

        var max = connection.CreateCommand();
        max.Transaction = transaction;
        max.CommandText = $"""
            SELECT MAX(SortIndex)
            FROM {PositionsTable}
            """;
        var maxResult = await max.ExecuteScalarAsync();
        return maxResult is long maxIndex ? (int)maxIndex + 1 : 0;
    }

    private async Task<SqliteConnection> OpenConnectionAsync(string userId)
    {
        var dbPath = Path.Combine(_userRoot, $"{userId}.db");
        var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        await EnsureDatabaseAsync(connection);
        return connection;
    }

    private static async Task EnsureDatabaseAsync(SqliteConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE TABLE IF NOT EXISTS {PositionsTable} (
                PositionId TEXT PRIMARY KEY,
                Payload TEXT NOT NULL,
                UpdatedUtc TEXT NOT NULL,
                SortIndex INTEGER NOT NULL DEFAULT 0
            );
            """;
        await command.ExecuteNonQueryAsync();

        var columnsCommand = connection.CreateCommand();
        columnsCommand.CommandText = $"PRAGMA table_info('{PositionsTable}');";
        await using var reader = await columnsCommand.ExecuteReaderAsync();
        var hasSortIndex = false;
        while (await reader.ReadAsync())
        {
            var name = reader.GetString(1);
            if (string.Equals(name, "SortIndex", StringComparison.OrdinalIgnoreCase))
            {
                hasSortIndex = true;
                break;
            }
        }

        if (!hasSortIndex)
        {
            var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE {PositionsTable} ADD COLUMN SortIndex INTEGER NOT NULL DEFAULT 0";
            await alter.ExecuteNonQueryAsync();
        }
    }

    private async Task AcquireReadAsync()
    {
        await _readLock.WaitAsync();
        try
        {
            _activeReaders++;
            if (_activeReaders == 1)
            {
                await _writeLock.WaitAsync();
            }
        }
        finally
        {
            _readLock.Release();
        }
    }

    private void ReleaseRead()
    {
        _readLock.Wait();
        try
        {
            _activeReaders--;
            if (_activeReaders == 0)
            {
                _writeLock.Release();
            }
        }
        finally
        {
            _readLock.Release();
        }
    }

    private async Task AcquireWriteAsync()
    {
        await _writeLock.WaitAsync();
    }

    private void ReleaseWrite()
    {
        _writeLock.Release();
    }

    private static string ResolveDataRoot(string contentRootPath, string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.Combine(contentRootPath, "Data");
        }

        var trimmed = configuredPath.Trim();
        return Path.IsPathRooted(trimmed)
            ? trimmed
            : Path.GetFullPath(Path.Combine(contentRootPath, trimmed));
    }

   
}
