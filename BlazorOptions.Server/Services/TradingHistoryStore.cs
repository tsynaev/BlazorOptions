using System.Globalization;
using System.Text.Json;
using BlazorOptions.API.TradingHistory;
using BlazorOptions.Server.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace BlazorOptions.Server.Services;

public sealed class TradingHistoryStore
{
    private const string MetaKey = "state";
    private readonly string _userRoot;
    private readonly SemaphoreSlim _readLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _activeReaders;

    public TradingHistoryStore(IWebHostEnvironment environment, IOptions<DataStorageOptions> dataOptions)
    {
        var dataRoot = ResolveDataRoot(environment.ContentRootPath, dataOptions.Value.Path);
        _userRoot = Path.Combine(dataRoot, "Users");
        Directory.CreateDirectory(_userRoot);
    }

    public async Task SaveTradesAsync(string userId, IReadOnlyList<TradingHistoryEntry> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        await AcquireWriteAsync();
        try
        {
            await using var connection = await OpenConnectionAsync(userId);
            await using var transaction = connection.BeginTransaction();
            var meta = await LoadMetaInternalAsync(connection, transaction);
            var state = new TradingHistoryCalculator.CalculationState(meta);
            var ordered = entries
                .OrderBy(entry => entry.Timestamp ?? 0)
                .ThenBy(entry => entry.Id, StringComparer.Ordinal)
                .ToList();

            TradingHistoryCalculator.ApplyCalculatedFields(ordered, state);
            state.ApplyToMeta(meta);
            if (ordered.Count > 0)
            {
                var maxTime = ordered.Max(entry => entry.Timestamp ?? 0);
                if (!meta.CalculatedThroughTimestamp.HasValue || maxTime > meta.CalculatedThroughTimestamp)
                {
                    meta.CalculatedThroughTimestamp = maxTime;
                }
            }
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO TradingHistoryEntries (
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
                )
                VALUES (
                    $id,
                    $timestamp,
                    $symbol,
                    $category,
                    $transactionType,
                    $side,
                    $size,
                    $price,
                    $fee,
                    $currency,
                    $change,
                    $cashFlow,
                    $orderId,
                    $orderLinkId,
                    $tradeId,
                    $rawJson,
                    $changedAt,
                    $calculatedSizeAfter,
                    $calculatedAvgPriceAfter,
                    $calculatedRealizedPnl,
                    $calculatedCumulativePnl
                )
                ON CONFLICT(Id) DO UPDATE SET
                    Timestamp = $timestamp,
                    Symbol = $symbol,
                    Category = $category,
                    TransactionType = $transactionType,
                    Side = $side,
                    Size = $size,
                    Price = $price,
                    Fee = $fee,
                    Currency = $currency,
                    Change = $change,
                    CashFlow = $cashFlow,
                    OrderId = $orderId,
                    OrderLinkId = $orderLinkId,
                    TradeId = $tradeId,
                    RawJson = $rawJson,
                    ChangedAt = $changedAt,
                    CalculatedSizeAfter = $calculatedSizeAfter,
                    CalculatedAvgPriceAfter = $calculatedAvgPriceAfter,
                    CalculatedRealizedPnl = $calculatedRealizedPnl,
                    CalculatedCumulativePnl = $calculatedCumulativePnl
                """;

            var idParam = command.Parameters.Add("$id", SqliteType.Text);
            var timestampParam = command.Parameters.Add("$timestamp", SqliteType.Integer);
            var symbolParam = command.Parameters.Add("$symbol", SqliteType.Text);
            var categoryParam = command.Parameters.Add("$category", SqliteType.Text);
            var transactionTypeParam = command.Parameters.Add("$transactionType", SqliteType.Text);
            var sideParam = command.Parameters.Add("$side", SqliteType.Text);
            var sizeParam = command.Parameters.Add("$size", SqliteType.Text);
            var priceParam = command.Parameters.Add("$price", SqliteType.Text);
            var feeParam = command.Parameters.Add("$fee", SqliteType.Text);
            var currencyParam = command.Parameters.Add("$currency", SqliteType.Text);
            var changeParam = command.Parameters.Add("$change", SqliteType.Text);
            var cashFlowParam = command.Parameters.Add("$cashFlow", SqliteType.Text);
            var orderIdParam = command.Parameters.Add("$orderId", SqliteType.Text);
            var orderLinkIdParam = command.Parameters.Add("$orderLinkId", SqliteType.Text);
            var tradeIdParam = command.Parameters.Add("$tradeId", SqliteType.Text);
            var rawJsonParam = command.Parameters.Add("$rawJson", SqliteType.Text);
            var changedAtParam = command.Parameters.Add("$changedAt", SqliteType.Integer);
            var calculatedSizeAfterParam = command.Parameters.Add("$calculatedSizeAfter", SqliteType.Text);
            var calculatedAvgPriceAfterParam = command.Parameters.Add("$calculatedAvgPriceAfter", SqliteType.Text);
            var calculatedRealizedPnlParam = command.Parameters.Add("$calculatedRealizedPnl", SqliteType.Text);
            var calculatedCumulativePnlParam = command.Parameters.Add("$calculatedCumulativePnl", SqliteType.Text);

            foreach (var entry in ordered)
            {
                var prepared = PrepareEntry(entry);
                idParam.Value = prepared.Id;
                timestampParam.Value = prepared.Timestamp ?? 0;
                symbolParam.Value = prepared.Symbol ?? string.Empty;
                categoryParam.Value = prepared.Category ?? string.Empty;
                transactionTypeParam.Value = prepared.TransactionType ?? string.Empty;
                sideParam.Value = prepared.Side ?? string.Empty;
                sizeParam.Value = ToDbDecimal(prepared.Size);
                priceParam.Value = ToDbDecimal(prepared.Price);
                feeParam.Value = ToDbDecimal(prepared.Fee);
                currencyParam.Value = prepared.Currency ?? string.Empty;
                changeParam.Value = ToDbDecimal(prepared.Change);
                cashFlowParam.Value = ToDbDecimal(prepared.CashFlow);
                orderIdParam.Value = prepared.OrderId ?? string.Empty;
                orderLinkIdParam.Value = prepared.OrderLinkId ?? string.Empty;
                tradeIdParam.Value = prepared.TradeId ?? string.Empty;
                rawJsonParam.Value = prepared.RawJson ?? string.Empty;
                changedAtParam.Value = prepared.ChangedAt;
                calculatedSizeAfterParam.Value = ToDbDecimal(prepared.Calculated?.SizeAfter ?? 0m);
                calculatedAvgPriceAfterParam.Value = ToDbDecimal(prepared.Calculated?.AvgPriceAfter ?? 0m);
                calculatedRealizedPnlParam.Value = ToDbDecimal(prepared.Calculated?.RealizedPnl ?? 0m);
                calculatedCumulativePnlParam.Value = ToDbDecimal(prepared.Calculated?.CumulativePnl ?? 0m);
                await command.ExecuteNonQueryAsync();
            }

            await SaveMetaInternalAsync(connection, transaction, meta);
            await transaction.CommitAsync();
        }
        finally
        {
            ReleaseWrite();
        }
    }

    public async Task<TradingHistoryResult> LoadEntriesAsync(string userId, int startIndex, int limit)
    {
        if (startIndex < 0 || limit <= 0)
        {
            return new TradingHistoryResult();
        }

        await AcquireReadAsync();
        try
        {
            await using var connection = await OpenConnectionAsync(userId);
            var meta = await LoadMetaInternalAsync(connection, null);

            var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
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
                FROM TradingHistoryEntries
                ORDER BY Timestamp DESC, Id DESC
                LIMIT $limit OFFSET $offset
                """;

            command.Parameters.AddWithValue("$limit", limit);
            command.Parameters.AddWithValue("$offset", startIndex);

            var entries = new List<TradingHistoryEntry>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                entries.Add(ReadEntry(reader));
            }

            var total = await CountEntriesAsync(connection, null);

            return new TradingHistoryResult
            {
                Entries = entries,
                TotalEntries = total
            };
        }
        finally
        {
            ReleaseRead();
        }
    }

    public async Task<IReadOnlyList<TradingHistoryEntry>> LoadLatestAsync(string userId, int limit)
    {
        return await LoadRangeAsync(userId, null, null, null, null, null, "DESC", limit);
    }

    public async Task<IReadOnlyList<TradingHistoryEntry>> LoadAllAscAsync(string userId)
    {
        return await LoadRangeAsync(userId, null, null, null, null, null, "ASC", null);
    }

    //public async Task<IReadOnlyList<TradingHistoryEntry>> LoadBeforeAsync(string userId, long? beforeTimestamp, string? beforeId, int limit)
    //{
    //    return await LoadRangeAsync(userId, null, null, null, beforeTimestamp, beforeId, "DESC", limit);
    //}

    public async Task<IReadOnlyList<TradingHistoryEntry>> LoadBySymbolAsync(string userId, string symbol, string? category, long? sinceTimestamp)
    {
        return await LoadRangeAsync(userId, symbol, category, sinceTimestamp, null, null, "DESC", null);
    }

    public async Task<IReadOnlyList<TradingSummaryBySymbolRow>> LoadSummaryBySymbolAsync(string userId)
    {
        await AcquireReadAsync();
        try
        {
            await using var connection = await OpenConnectionAsync(userId);
            var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    Category,
                    Symbol,
                    Currency,
                    Size,
                    Price,
                    Fee,
                    CalculatedRealizedPnl
                FROM TradingHistoryEntries
                """;

            var summary = new Dictionary<string, SummaryAccumulator>(StringComparer.OrdinalIgnoreCase);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var category = reader.GetString(0);
                var symbol = reader.GetString(1);
                var settleCoin = reader.GetString(2);
                var size = ReadDecimal(reader, 3);
                var price = ReadDecimal(reader, 4);
                var fee = ReadDecimal(reader, 5);
                var realized = ReadDecimal(reader, 6);

                var key = $"{category}|{symbol}|{settleCoin}";
                if (!summary.TryGetValue(key, out var accumulator))
                {
                    accumulator = new SummaryAccumulator(category, symbol, settleCoin);
                }

                accumulator.Trades += 1;
                accumulator.TotalQty += size;
                accumulator.TotalValue += size * price;
                accumulator.TotalFees += fee;
                accumulator.RealizedPnl += realized;
                summary[key] = accumulator;
            }

            return summary.Values
                .Select(accumulator => accumulator.ToRow())
                .OrderByDescending(row => row.Trades)
                .ThenBy(row => row.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Symbol, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        finally
        {
            ReleaseRead();
        }
    }

    public async Task<IReadOnlyList<TradingPnlByCoinRow>> LoadPnlBySettleCoinAsync(string userId)
    {
        await AcquireReadAsync();
        try
        {
            await using var connection = await OpenConnectionAsync(userId);
            var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    Currency,
                    Fee,
                    CalculatedRealizedPnl
                FROM TradingHistoryEntries
                """;

            var totals = new Dictionary<string, CoinAccumulator>(StringComparer.OrdinalIgnoreCase);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var settleCoin = reader.GetString(0);
                var fee = ReadDecimal(reader, 1);
                var realized = ReadDecimal(reader, 2);

                if (!totals.TryGetValue(settleCoin, out var accumulator))
                {
                    accumulator = new CoinAccumulator(settleCoin);
                }

                accumulator.RealizedPnl += realized;
                accumulator.Fees += fee;
                totals[settleCoin] = accumulator;
            }

            return totals.Values
                .Select(accumulator => accumulator.ToRow())
                .OrderBy(row => row.SettleCoin, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        finally
        {
            ReleaseRead();
        }
    }

    public async Task<IReadOnlyList<TradingDailyPnlRow>> LoadDailyPnlAsync(
        string userId,
        long fromTimestamp,
        long toTimestamp)
    {
        if (toTimestamp < fromTimestamp)
        {
            return Array.Empty<TradingDailyPnlRow>();
        }

        await AcquireReadAsync();
        try
        {
            await using var connection = await OpenConnectionAsync(userId);
            var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    Timestamp,
                    Currency,
                    CalculatedRealizedPnl
                FROM TradingHistoryEntries
                WHERE Timestamp >= $fromTimestamp AND Timestamp <= $toTimestamp
                """;
            command.Parameters.AddWithValue("$fromTimestamp", fromTimestamp);
            command.Parameters.AddWithValue("$toTimestamp", toTimestamp);

            var totals = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var timestamp = reader.GetInt64(0);
                var settleCoin = reader.GetString(1);
                var realized = ReadDecimal(reader, 2);

                var dayKey = GetDayKey(timestamp);
                if (string.IsNullOrWhiteSpace(dayKey))
                {
                    continue;
                }

                var key = $"{dayKey}|{settleCoin}";
                totals.TryGetValue(key, out var current);
                totals[key] = current + realized;
            }

            return totals
                .Select(entry =>
                {
                    var parts = entry.Key.Split('|');
                    return new TradingDailyPnlRow
                    {
                        Day = parts.Length > 0 ? parts[0] : string.Empty,
                        SettleCoin = parts.Length > 1 ? parts[1] : string.Empty,
                        RealizedPnl = entry.Value
                    };
                })
                .OrderBy(row => row.Day, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.SettleCoin, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        finally
        {
            ReleaseRead();
        }
    }

    public async Task<TradingHistoryLatestInfo> LoadLatestBySymbolMetaAsync(string userId, string symbol, string? category)
    {
        await AcquireReadAsync();
        try
        {
            await using var connection = await OpenConnectionAsync(userId);
            var maxCommand = connection.CreateCommand();
            if (string.IsNullOrWhiteSpace(category))
            {
                maxCommand.CommandText = "SELECT MAX(Timestamp) FROM TradingHistoryEntries WHERE Symbol = $symbol";
                maxCommand.Parameters.AddWithValue("$symbol", symbol);
            }
            else
            {
                maxCommand.CommandText = "SELECT MAX(Timestamp) FROM TradingHistoryEntries WHERE Symbol = $symbol AND Category = $category";
                maxCommand.Parameters.AddWithValue("$symbol", symbol);
                maxCommand.Parameters.AddWithValue("$category", category);
            }

            var maxResult = await maxCommand.ExecuteScalarAsync();
            if (maxResult is not long maxTimestamp)
            {
                return new TradingHistoryLatestInfo();
            }

            var ids = new List<string>();
            var idsCommand = connection.CreateCommand();
            if (string.IsNullOrWhiteSpace(category))
            {
                idsCommand.CommandText = """
                    SELECT Id FROM TradingHistoryEntries
                    WHERE Symbol = $symbol AND Timestamp = $timestamp
                    """;
                idsCommand.Parameters.AddWithValue("$symbol", symbol);
                idsCommand.Parameters.AddWithValue("$timestamp", maxTimestamp);
            }
            else
            {
                idsCommand.CommandText = """
                    SELECT Id FROM TradingHistoryEntries
                    WHERE Symbol = $symbol AND Category = $category AND Timestamp = $timestamp
                    """;
                idsCommand.Parameters.AddWithValue("$symbol", symbol);
                idsCommand.Parameters.AddWithValue("$category", category);
                idsCommand.Parameters.AddWithValue("$timestamp", maxTimestamp);
            }

            await using var reader = await idsCommand.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                ids.Add(reader.GetString(0));
            }

            return new TradingHistoryLatestInfo
            {
                Timestamp = maxTimestamp,
                Ids = ids
            };
        }
        finally
        {
            ReleaseRead();
        }
    }

    public async Task<TradingHistoryMeta> LoadMetaAsync(string userId)
    {
        await AcquireReadAsync();
        try
        {
            await using var connection = await OpenConnectionAsync(userId);
            return await LoadMetaInternalAsync(connection, null);
        }
        finally
        {
            ReleaseRead();
        }
    }

    public async Task SaveMetaAsync(string userId, TradingHistoryMeta meta)
    {
        await AcquireWriteAsync();
        try
        {
            await using var connection = await OpenConnectionAsync(userId);
            await SaveMetaInternalAsync(connection, null, meta);
        }
        finally
        {
            ReleaseWrite();
        }
    }

    public async Task RecalculateAsync(string userId, long? fromTimestamp)
    {
        await AcquireWriteAsync();
        try
        {
            await using var connection = await OpenConnectionAsync(userId);
            await using var transaction = connection.BeginTransaction();
            var meta = await LoadMetaInternalAsync(connection, transaction);
            var entries = await LoadAllAscInternalAsync(connection, transaction);
            var state = new TradingHistoryCalculator.CalculationState();
            var updateFrom = fromTimestamp ?? 0;
            var updateAll = !fromTimestamp.HasValue;
            var updated = new List<TradingHistoryEntry>();

            foreach (var entry in entries)
            {
                var shouldUpdate = updateAll || (entry.Timestamp ?? 0) >= updateFrom;
                // Always advance the calculation state; only persist updates after the selected timestamp.
                TradingHistoryCalculator.ApplyCalculatedFields(new[] { entry }, state, shouldUpdate);
                if (shouldUpdate)
                {
                    updated.Add(entry);
                }
            }

            if (entries.Count > 0)
            {
                meta.CalculatedThroughTimestamp = entries[^1].Timestamp;
            }

            state.ApplyToMeta(meta);
            meta.RequiresRecalculation = false;
          

            await UpdateCalculatedFieldsAsync(connection, transaction, updated);
            await SaveMetaInternalAsync(connection, transaction, meta);
            await transaction.CommitAsync();
        }
        finally
        {
            ReleaseWrite();
        }
    }

    public async Task SaveDailySummariesAsync(string userId, IReadOnlyList<TradingDailySummary> summaries)
    {
        await AcquireWriteAsync();
        try
        {
            await using var connection = await OpenConnectionAsync(userId);
            await using var transaction = connection.BeginTransaction();
            var clear = connection.CreateCommand();
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM TradingDailySummaries";
            await clear.ExecuteNonQueryAsync();

            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO TradingDailySummaries (
                    Key,
                    Day,
                    SymbolKey,
                    Symbol,
                    Category,
                    TotalSize,
                    TotalValue,
                    TotalFee
                )
                VALUES (
                    $key,
                    $day,
                    $symbolKey,
                    $symbol,
                    $category,
                    $totalSize,
                    $totalValue,
                    $totalFee
                )
                ON CONFLICT(Key) DO UPDATE SET
                    Day = $day,
                    SymbolKey = $symbolKey,
                    Symbol = $symbol,
                    Category = $category,
                    TotalSize = $totalSize,
                    TotalValue = $totalValue,
                    TotalFee = $totalFee
                """;

            var keyParam = command.Parameters.Add("$key", SqliteType.Text);
            var dayParam = command.Parameters.Add("$day", SqliteType.Text);
            var symbolKeyParam = command.Parameters.Add("$symbolKey", SqliteType.Text);
            var symbolParam = command.Parameters.Add("$symbol", SqliteType.Text);
            var categoryParam = command.Parameters.Add("$category", SqliteType.Text);
            var totalSizeParam = command.Parameters.Add("$totalSize", SqliteType.Text);
            var totalValueParam = command.Parameters.Add("$totalValue", SqliteType.Text);
            var totalFeeParam = command.Parameters.Add("$totalFee", SqliteType.Text);

            foreach (var summary in summaries)
            {
                keyParam.Value = summary.Key ?? string.Empty;
                dayParam.Value = summary.Day ?? string.Empty;
                symbolKeyParam.Value = summary.SymbolKey ?? string.Empty;
                symbolParam.Value = summary.Symbol ?? string.Empty;
                categoryParam.Value = summary.Category ?? string.Empty;
                totalSizeParam.Value = ToDbDecimal(summary.TotalSize);
                totalValueParam.Value = ToDbDecimal(summary.TotalValue);
                totalFeeParam.Value = ToDbDecimal(summary.TotalFee);
                await command.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }
        finally
        {
            ReleaseWrite();
        }
    }

    private async Task<List<TradingHistoryEntry>> LoadRangeAsync(
        string userId,
        string? symbol,
        string? category,
        long? sinceTimestamp,
        long? beforeTimestamp,
        string? beforeId,
        string direction,
        int? limit)
    {
        await AcquireReadAsync();
        try
        {
            await using var connection = await OpenConnectionAsync(userId);
            var command = connection.CreateCommand();
            var filters = new List<string>();

            if (!string.IsNullOrWhiteSpace(symbol))
            {
                filters.Add("Symbol = $symbol");
                command.Parameters.AddWithValue("$symbol", symbol);
            }

            if (!string.IsNullOrWhiteSpace(category))
            {
                filters.Add("Category = $category");
                command.Parameters.AddWithValue("$category", category);
            }

            if (sinceTimestamp.HasValue)
            {
                filters.Add("Timestamp >= $sinceTimestamp");
                command.Parameters.AddWithValue("$sinceTimestamp", sinceTimestamp.Value);
            }

            if (beforeTimestamp.HasValue)
            {
                if (!string.IsNullOrWhiteSpace(beforeId))
                {
                    filters.Add("(Timestamp < $beforeTimestamp OR (Timestamp = $beforeTimestamp AND Id < $beforeId))");
                    command.Parameters.AddWithValue("$beforeId", beforeId);
                }
                else
                {
                    filters.Add("Timestamp < $beforeTimestamp");
                }

                command.Parameters.AddWithValue("$beforeTimestamp", beforeTimestamp.Value);
            }

            var where = filters.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", filters);
            var limitClause = limit.HasValue ? "LIMIT $limit" : string.Empty;
            if (limit.HasValue)
            {
                command.Parameters.AddWithValue("$limit", limit.Value);
            }

            command.CommandText = $"""
                SELECT
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
                FROM TradingHistoryEntries
                {where}
                ORDER BY Timestamp {direction}, Id {direction}
                {limitClause}
                """;

            var entries = new List<TradingHistoryEntry>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                entries.Add(ReadEntry(reader));
            }

            return entries;
        }
        finally
        {
            ReleaseRead();
        }
    }

    private static async Task<List<TradingHistoryEntry>> LoadAllAscInternalAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
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
            FROM TradingHistoryEntries
            ORDER BY Timestamp ASC, Id ASC
            """;

        var entries = new List<TradingHistoryEntry>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            entries.Add(ReadEntry(reader));
        }

        return entries;
    }

    private static async Task UpdateCalculatedFieldsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        IReadOnlyList<TradingHistoryEntry> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE TradingHistoryEntries
            SET
                CalculatedSizeAfter = $calculatedSizeAfter,
                CalculatedAvgPriceAfter = $calculatedAvgPriceAfter,
                CalculatedRealizedPnl = $calculatedRealizedPnl,
                CalculatedCumulativePnl = $calculatedCumulativePnl,
                ChangedAt = $changedAt
            WHERE Id = $id
            """;

        var idParam = command.Parameters.Add("$id", SqliteType.Text);
        var calculatedSizeAfterParam = command.Parameters.Add("$calculatedSizeAfter", SqliteType.Text);
        var calculatedAvgPriceAfterParam = command.Parameters.Add("$calculatedAvgPriceAfter", SqliteType.Text);
        var calculatedRealizedPnlParam = command.Parameters.Add("$calculatedRealizedPnl", SqliteType.Text);
        var calculatedCumulativePnlParam = command.Parameters.Add("$calculatedCumulativePnl", SqliteType.Text);
        var changedAtParam = command.Parameters.Add("$changedAt", SqliteType.Integer);

        foreach (var entry in entries)
        {
            idParam.Value = entry.Id;
            calculatedSizeAfterParam.Value = ToDbDecimal(entry.Calculated?.SizeAfter ?? 0m);
            calculatedAvgPriceAfterParam.Value = ToDbDecimal(entry.Calculated?.AvgPriceAfter ?? 0m);
            calculatedRealizedPnlParam.Value = ToDbDecimal(entry.Calculated?.RealizedPnl ?? 0m);
            calculatedCumulativePnlParam.Value = ToDbDecimal(entry.Calculated?.CumulativePnl ?? 0m);
            changedAtParam.Value = entry.ChangedAt;
            await command.ExecuteNonQueryAsync();
        }
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
        var pragmaCommand = connection.CreateCommand();
        pragmaCommand.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
        await pragmaCommand.ExecuteNonQueryAsync();

        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS TradingHistoryEntries (
                Id TEXT PRIMARY KEY,
                Timestamp INTEGER NOT NULL,
                Symbol TEXT NOT NULL,
                Category TEXT NOT NULL,
                TransactionType TEXT NOT NULL,
                Side TEXT NOT NULL,
                Size TEXT NOT NULL,
                Price TEXT NOT NULL,
                Fee TEXT NOT NULL,
                Currency TEXT NOT NULL,
                Change TEXT NOT NULL,
                CashFlow TEXT NOT NULL,
                OrderId TEXT NOT NULL,
                OrderLinkId TEXT NOT NULL,
                TradeId TEXT NOT NULL,
                RawJson TEXT NOT NULL,
                ChangedAt INTEGER NOT NULL,
                CalculatedSizeAfter TEXT NOT NULL,
                CalculatedAvgPriceAfter TEXT NOT NULL,
                CalculatedRealizedPnl TEXT NOT NULL,
                CalculatedCumulativePnl TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IDX_TradingHistoryEntries_Timestamp
                ON TradingHistoryEntries (Timestamp);

            CREATE INDEX IF NOT EXISTS IDX_TradingHistoryEntries_SymbolTimestamp
                ON TradingHistoryEntries (Symbol, Timestamp);

            CREATE INDEX IF NOT EXISTS IDX_TradingHistoryEntries_SymbolCategoryTimestamp
                ON TradingHistoryEntries (Symbol, Category, Timestamp);

            CREATE TABLE IF NOT EXISTS TradingHistoryMeta (
                Key TEXT PRIMARY KEY,
                Payload TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS TradingDailySummaries (
                Key TEXT PRIMARY KEY,
                Day TEXT NOT NULL,
                SymbolKey TEXT NOT NULL,
                Symbol TEXT NOT NULL,
                Category TEXT NOT NULL,
                TotalSize TEXT NOT NULL,
                TotalValue TEXT NOT NULL,
                TotalFee TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync();
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

    private static TradingHistoryEntry PrepareEntry(TradingHistoryEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Id))
        {
            entry.Id = Guid.NewGuid().ToString("N");
        }

        if (!entry.Timestamp.HasValue || entry.Timestamp <= 0)
        {
            entry.Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        entry.Symbol ??= string.Empty;
        entry.Category ??= string.Empty;
        entry.TransactionType ??= string.Empty;
        entry.Side ??= string.Empty;
        entry.Currency ??= string.Empty;
        entry.OrderId ??= string.Empty;
        entry.OrderLinkId ??= string.Empty;
        entry.TradeId ??= string.Empty;
        entry.RawJson ??= string.Empty;
        entry.Calculated ??= new TradingTransactionCalculated();

        return entry;
    }

    private static TradingHistoryEntry ReadEntry(SqliteDataReader reader)
    {
        return new TradingHistoryEntry
        {
            Id = reader.GetString(0),
            Timestamp = reader.GetInt64(1),
            Symbol = reader.GetString(2),
            Category = reader.GetString(3),
            TransactionType = reader.GetString(4),
            Side = reader.GetString(5),
            Size = ReadDecimal(reader, 6),
            Price = ReadDecimal(reader, 7),
            Fee = ReadDecimal(reader, 8),
            Currency = reader.GetString(9),
            Change = ReadDecimal(reader, 10),
            CashFlow = ReadDecimal(reader, 11),
            OrderId = reader.GetString(12),
            OrderLinkId = reader.GetString(13),
            TradeId = reader.GetString(14),
            RawJson = reader.GetString(15),
            ChangedAt = reader.GetInt64(16),
            Calculated = new TradingTransactionCalculated
            {
                SizeAfter = ReadDecimal(reader, 17),
                AvgPriceAfter = ReadDecimal(reader, 18),
                RealizedPnl = ReadDecimal(reader, 19),
                CumulativePnl = ReadDecimal(reader, 20)
            }
        };
    }

    private static decimal ReadDecimal(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return 0m;
        }

        var raw = reader.GetString(ordinal);
        return decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : 0m;
    }

    private static string ToDbDecimal(decimal value)
    {
        return value.ToString("G29", CultureInfo.InvariantCulture);
    }

    private static async Task<int> CountEntriesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM TradingHistoryEntries";
        var result = await command.ExecuteScalarAsync();
        return result is long count ? (int)count : 0;
    }

    private static async Task<TradingHistoryMeta> LoadMetaInternalAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Payload FROM TradingHistoryMeta WHERE Key = $key";
        command.Parameters.AddWithValue("$key", MetaKey);
        var payload = await command.ExecuteScalarAsync();
        if (payload is string raw && !string.IsNullOrWhiteSpace(raw))
        {
            try
            {
                var meta = JsonSerializer.Deserialize<TradingHistoryMeta>(raw);
                if (meta is not null)
                {
                    return meta;
                }
            }
            catch
            {
            }
        }

        return new TradingHistoryMeta();
    }

    private static async Task SaveMetaInternalAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        TradingHistoryMeta meta)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO TradingHistoryMeta (Key, Payload)
            VALUES ($key, $payload)
            ON CONFLICT(Key) DO UPDATE SET Payload = $payload
            """;
        command.Parameters.AddWithValue("$key", MetaKey);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(meta));
        await command.ExecuteNonQueryAsync();
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

    private static string GetDayKey(long timestamp)
    {
        if (timestamp <= 0)
        {
            return string.Empty;
        }

        try
        {
            var date = DateTimeOffset.FromUnixTimeMilliseconds(timestamp).UtcDateTime;
            return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        catch
        {
            return string.Empty;
        }
    }

    private sealed class SummaryAccumulator
    {
        public SummaryAccumulator(string category, string symbol, string settleCoin)
        {
            Category = category;
            Symbol = symbol;
            SettleCoin = settleCoin;
        }

        public string Category { get; }
        public string Symbol { get; }
        public string SettleCoin { get; }
        public int Trades { get; set; }
        public decimal TotalQty { get; set; }
        public decimal TotalValue { get; set; }
        public decimal TotalFees { get; set; }
        public decimal RealizedPnl { get; set; }

        public TradingSummaryBySymbolRow ToRow()
        {
            return new TradingSummaryBySymbolRow
            {
                Category = Category,
                Symbol = Symbol,
                SettleCoin = SettleCoin,
                Trades = Trades,
                TotalQty = TotalQty,
                TotalValue = TotalValue,
                TotalFees = TotalFees,
                RealizedPnl = RealizedPnl
            };
        }
    }

    private sealed class CoinAccumulator
    {
        public CoinAccumulator(string settleCoin)
        {
            SettleCoin = settleCoin;
        }

        public string SettleCoin { get; }
        public decimal RealizedPnl { get; set; }
        public decimal Fees { get; set; }

        public TradingPnlByCoinRow ToRow()
        {
            return new TradingPnlByCoinRow
            {
                SettleCoin = SettleCoin,
                RealizedPnl = RealizedPnl,
                Fees = Fees,
                NetPnl = RealizedPnl - Fees
            };
        }
    }
}
