using Microsoft.Data.Sqlite;

namespace BlazorOptions.Server.Services;

public sealed class TradingHistoryMigration
{
    private readonly string _userRoot;

    public TradingHistoryMigration(string userRoot)
    {
        _userRoot = userRoot;
    }

    public async Task MigrateLegacyTradingTablesAsync(
        SqliteConnection targetConnection,
        string userId,
        string? exchangeConnectionId,
        string targetPath)
    {
        if (!string.Equals(NormalizeExchangeConnectionId(exchangeConnectionId), "bybit-main", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var legacyPath = Path.Combine(_userRoot, $"{userId}.db");
        if (!File.Exists(legacyPath) || string.Equals(legacyPath, targetPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var attachCommand = targetConnection.CreateCommand();
        attachCommand.CommandText = "ATTACH DATABASE $legacyPath AS legacy";
        attachCommand.Parameters.AddWithValue("$legacyPath", legacyPath);
        await attachCommand.ExecuteNonQueryAsync();

        try
        {
            if (!await TableExistsAsync(targetConnection, "legacy", "TradingHistoryEntries"))
            {
                return;
            }

            var targetHasTradingData = await HasTradingDataAsync(targetConnection);
            if (!targetHasTradingData)
            {
                await CopyTradingTableAsync(
                    targetConnection,
                    "TradingHistoryEntries",
                    """
                    Id,
                    Timestamp,
                    Symbol,
                    Category,
                    TransactionType,
                    Side,
                    Size,
                    Price,
                    Fee,
                    Currency,
                    Change,
                    CashFlow,
                    OrderId,
                    OrderLinkId,
                    TradeId,
                    RawJson,
                    ChangedAt,
                    CalculatedSizeAfter,
                    CalculatedAvgPriceAfter,
                    CalculatedRealizedPnl,
                    CalculatedCumulativePnl
                    """);

                if (await TableExistsAsync(targetConnection, "legacy", "TradingHistoryMeta"))
                {
                    await CopyTradingTableAsync(targetConnection, "TradingHistoryMeta", "Key, Payload");
                }

                if (await TableExistsAsync(targetConnection, "legacy", "TradingDailySummaries"))
                {
                    await CopyTradingTableAsync(
                        targetConnection,
                        "TradingDailySummaries",
                        "Key, Day, SymbolKey, Symbol, Category, TotalSize, TotalValue, TotalFee");
                }

                targetHasTradingData = await HasTradingDataAsync(targetConnection);
            }

            if (targetHasTradingData)
            {
                await DropLegacyTradingTablesAsync(targetConnection);
            }
        }
        finally
        {
            var detachCommand = targetConnection.CreateCommand();
            detachCommand.CommandText = "DETACH DATABASE legacy";
            await detachCommand.ExecuteNonQueryAsync();
        }
    }

    private static string NormalizeExchangeConnectionId(string? exchangeConnectionId)
    {
        return string.IsNullOrWhiteSpace(exchangeConnectionId)
            ? "bybit-main"
            : exchangeConnectionId.Trim();
    }

    private static async Task<bool> HasTradingDataAsync(SqliteConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM TradingHistoryEntries LIMIT 1)";
        var result = await command.ExecuteScalarAsync();
        return result is long count && count != 0;
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string schemaName, string tableName)
    {
        var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {schemaName}.sqlite_master WHERE type = 'table' AND name = $tableName";
        command.Parameters.AddWithValue("$tableName", tableName);
        var result = await command.ExecuteScalarAsync();
        return result is long count && count > 0;
    }

    private static async Task CopyTradingTableAsync(SqliteConnection connection, string tableName, string columnList)
    {
        var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO {tableName} ({columnList})
            SELECT {columnList}
            FROM legacy.{tableName}
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropLegacyTradingTablesAsync(SqliteConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            DROP TABLE IF EXISTS legacy.TradingDailySummaries;
            DROP TABLE IF EXISTS legacy.TradingHistoryMeta;
            DROP TABLE IF EXISTS legacy.TradingHistoryEntries;
            """;
        await command.ExecuteNonQueryAsync();
    }
}
