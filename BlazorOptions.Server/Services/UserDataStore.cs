using System.Globalization;
using System.Text.Json;
using BlazorOptions.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using BlazorOptions.Server.Options;
using BlazorOptions.ViewModels;

namespace BlazorOptions.Server.Services;

public class UserDataStore
{
    private readonly string _userRoot;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public UserDataStore(IWebHostEnvironment environment, IOptions<DataStorageOptions> dataOptions)
    {
        var dataRoot = ResolveDataRoot(environment.ContentRootPath, dataOptions.Value.Path);
        _userRoot = Path.Combine(dataRoot, "Users");
        Directory.CreateDirectory(_userRoot);
    }

    public async Task<IReadOnlyList<EventEnvelope>> AppendEventsAsync(string userId, IReadOnlyList<EventEnvelope> events)
    {
        if (events.Count == 0)
        {
            return Array.Empty<EventEnvelope>();
        }

        await _mutex.WaitAsync();
        try
        {
            await using var connection = await OpenConnectionAsync(userId);
            await using var transaction = connection.BeginTransaction();
            var accepted = new List<EventEnvelope>();

            foreach (var envelope in events)
            {
                if (envelope.EventId == Guid.Empty || string.IsNullOrWhiteSpace(envelope.Kind))
                {
                    continue;
                }

                if (string.Equals(envelope.Kind, EventKinds.PositionSnapshot, StringComparison.OrdinalIgnoreCase))
                {
                    accepted.Add(envelope);
                    await ApplyPositionSnapshotAsync(connection, transaction, envelope);
                }
            }

            await transaction.CommitAsync();
            return accepted;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<PositionSnapshotResponse?> GetPositionSnapshotAsync(string userId)
    {
        await _mutex.WaitAsync();
        try
        {
            await using var connection = await OpenConnectionAsync(userId);
            var selectCommand = connection.CreateCommand();
            selectCommand.CommandText = "SELECT OccurredUtc, Payload, IsDeleted FROM PositionSnapshotsByPosition";
            await using var reader = await selectCommand.ExecuteReaderAsync();
            var positions = new List<PositionModel>();
            var maxOccurredUtc = DateTime.MinValue;
            var hasRows = false;

            while (await reader.ReadAsync())
            {
                hasRows = true;
                var occurredRaw = reader.GetString(0);
                if (DateTime.TryParse(occurredRaw, null, DateTimeStyles.RoundtripKind, out var occurredUtc))
                {
                    if (occurredUtc > maxOccurredUtc)
                    {
                        maxOccurredUtc = occurredUtc;
                    }
                }

                var isDeleted = reader.GetInt32(2) != 0;
                if (isDeleted)
                {
                    continue;
                }

                var payloadRaw = reader.GetString(1);
                var itemPayload = JsonSerializer.Deserialize<PositionItemSnapshotPayload>(payloadRaw, SyncJson.SerializerOptions);
                if (itemPayload?.Position is not null)
                {
                    positions.Add(itemPayload.Position);
                }
            }

            if (!hasRows)
            {
                return null;
            }

            if (maxOccurredUtc == DateTime.MinValue)
            {
                maxOccurredUtc = DateTime.UtcNow;
            }

            return new PositionSnapshotResponse(maxOccurredUtc, new PositionSnapshotPayload { Positions = positions });
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async Task ApplyPositionSnapshotAsync(SqliteConnection connection, SqliteTransaction transaction, EventEnvelope envelope)
    {
        if (envelope.Payload.ValueKind == JsonValueKind.Undefined)
        {
            return;
        }

        PositionItemSnapshotPayload? payload;
        try
        {
            payload = envelope.Payload.Deserialize<PositionItemSnapshotPayload>(SyncJson.SerializerOptions);
        }
        catch
        {
            return;
        }

        if (payload is null || payload.PositionId == Guid.Empty)
        {
            return;
        }

        var selectCommand = connection.CreateCommand();
        selectCommand.Transaction = transaction;
        selectCommand.CommandText = "SELECT OccurredUtc FROM PositionSnapshotsByPosition WHERE PositionId = $positionId";
        selectCommand.Parameters.AddWithValue("$positionId", payload.PositionId.ToString("N"));
        var existingValue = await selectCommand.ExecuteScalarAsync();
        if (existingValue is string existingText &&
            DateTime.TryParse(existingText, null, DateTimeStyles.RoundtripKind, out var existingUtc) &&
            envelope.OccurredUtc <= existingUtc)
        {
            return;
        }

        var upsertCommand = connection.CreateCommand();
        upsertCommand.Transaction = transaction;
        upsertCommand.CommandText = """
            INSERT INTO PositionSnapshotsByPosition (PositionId, OccurredUtc, Payload, IsDeleted)
            VALUES ($positionId, $occurredUtc, $payload, $isDeleted)
            ON CONFLICT(PositionId) DO UPDATE SET OccurredUtc = $occurredUtc, Payload = $payload, IsDeleted = $isDeleted
            """;
        upsertCommand.Parameters.AddWithValue("$positionId", payload.PositionId.ToString("N"));
        upsertCommand.Parameters.AddWithValue("$occurredUtc", envelope.OccurredUtc.ToString("O"));
        upsertCommand.Parameters.AddWithValue("$payload", envelope.Payload.GetRawText());
        upsertCommand.Parameters.AddWithValue("$isDeleted", payload.IsDeleted ? 1 : 0);
        await upsertCommand.ExecuteNonQueryAsync();
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
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS PositionSnapshotsByPosition (
                PositionId TEXT PRIMARY KEY,
                OccurredUtc TEXT NOT NULL,
                Payload TEXT NOT NULL,
                IsDeleted INTEGER NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync();

        var columnsCommand = connection.CreateCommand();
        columnsCommand.CommandText = "PRAGMA table_info('EventStream');";
        await using var reader = await columnsCommand.ExecuteReaderAsync();
        var hasPayload = false;
        while (await reader.ReadAsync())
        {
            var name = reader.GetString(1);
            if (string.Equals(name, "Payload", StringComparison.OrdinalIgnoreCase))
            {
                hasPayload = true;
                break;
            }
        }

        if (!hasPayload)
        {
            var alterCommand = connection.CreateCommand();
            alterCommand.CommandText = "ALTER TABLE EventStream ADD COLUMN Payload TEXT NOT NULL DEFAULT ''";
            await alterCommand.ExecuteNonQueryAsync();
        }
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
