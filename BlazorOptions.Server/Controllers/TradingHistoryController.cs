using System.Security.Claims;
using BlazorOptions.API.TradingHistory;
using BlazorOptions.Server.Authentication;
using BlazorOptions.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorOptions.Server.Controllers;

[ApiController]
[Route("api/trading-history")]
[Authorize(AuthenticationSchemes = UserTokenAuthenticationOptions.SchemeName)]
public sealed class TradingHistoryController : ControllerBase, ITradingHistoryPort
{
    private readonly TradingHistoryStore _store;

    public TradingHistoryController(TradingHistoryStore store)
    {
        _store = store;
    }

    [HttpPost("trades/bulk")]
    public async Task<IActionResult> SaveTradesAsync([FromBody] TradingHistoryEntry[] entries)
    {
        var userId = ResolveUserId();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized");
        }

        await _store.SaveTradesAsync(userId, entries);
        return Ok(new { count = entries.Length });
    }

    [HttpGet("entries")]
    public async Task<IActionResult> LoadEntriesAsync([FromQuery] string? baseAsset, [FromQuery] int startIndex, [FromQuery] int limit)
    {
        if (startIndex < 0 || limit <= 0)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid paging arguments.");
        }

        var userId = ResolveUserId();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized");
        }

        var result = await _store.LoadEntriesAsync(userId, baseAsset, startIndex, limit);
        var mapped = new TradingHistoryResult
        {
            Entries = result.Entries.Select(MapForGrid).ToList(),
            TotalEntries = result.TotalEntries
        };
        return Ok(mapped);
    }

    [HttpGet("all")]
    public async Task<IActionResult> LoadAllAscAsync()
    {
        var userId = ResolveUserId();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized");
        }

        var entries = await _store.LoadAllAscAsync(userId);
        return Ok(entries);
    }

    [HttpGet("by-symbol")]
    public async Task<IActionResult> LoadBySymbolAsync(
        [FromQuery] string symbol,
        [FromQuery] string? category,
        [FromQuery] long? sinceTimestamp)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Symbol is required.");
        }

        var userId = ResolveUserId();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized");
        }

        var entries = await _store.LoadBySymbolAsync(userId, symbol, category, sinceTimestamp);
        return Ok(entries);
    }

    [HttpGet("summary/by-symbol")]
    public async Task<IActionResult> LoadSummaryBySymbolAsync()
    {
        var userId = ResolveUserId();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized");
        }

        var summary = await _store.LoadSummaryBySymbolAsync(userId);
        return Ok(summary);
    }

    [HttpGet("summary/by-settle-coin")]
    public async Task<IActionResult> LoadPnlBySettleCoinAsync()
    {
        var userId = ResolveUserId();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized");
        }

        var summary = await _store.LoadPnlBySettleCoinAsync(userId);
        return Ok(summary);
    }

    [HttpGet("daily-pnl")]
    public async Task<IActionResult> LoadDailyPnlAsync(
        [FromQuery] long fromTimestamp,
        [FromQuery] long toTimestamp)
    {
        if (fromTimestamp <= 0 || toTimestamp <= 0)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid date range.");
        }

        var userId = ResolveUserId();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized");
        }

        var rows = await _store.LoadDailyPnlAsync(userId, fromTimestamp, toTimestamp);
        return Ok(rows);
    }

    [HttpGet("latest-meta")]
    public async Task<IActionResult> LoadLatestMetaAsync(
        [FromQuery] string symbol,
        [FromQuery] string? category)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Symbol is required.");
        }

        var userId = ResolveUserId();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized");
        }

        var meta = await _store.LoadLatestBySymbolMetaAsync(userId, symbol, category);
        return Ok(meta);
    }

    [HttpGet("meta")]
    public async Task<IActionResult> LoadMetaAsync()
    {
        var userId = ResolveUserId();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized");
        }

        var meta = await _store.LoadMetaAsync(userId);
        return Ok(meta);
    }

    [HttpPost("meta")]
    public async Task<IActionResult> SaveMetaAsync([FromBody] TradingHistoryMeta meta)
    {
        var userId = ResolveUserId();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized");
        }

        await _store.SaveMetaAsync(userId, meta);
        return Ok();
    }

    [HttpPost("recalculate")]
    public async Task<IActionResult> RecalculateAsync([FromQuery] long? fromTimestamp)
    {
        var userId = ResolveUserId();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized");
        }

        await _store.RecalculateAsync(userId, fromTimestamp);
        return Ok();
    }

    async Task ITradingHistoryPort.SaveTradesAsync(IReadOnlyList<TradingHistoryEntry> entries)
    {
        var userId = ResolveUserIdOrThrow();
        await _store.SaveTradesAsync(userId, entries);
    }

    async Task<TradingHistoryResult> ITradingHistoryPort.LoadEntriesAsync(string? baseAsset, int startIndex, int limit)
    {
        var userId = ResolveUserIdOrThrow();
        var result = await _store.LoadEntriesAsync(userId, baseAsset, startIndex, limit);
        return new TradingHistoryResult
        {
            Entries = result.Entries.Select(MapForGrid).ToList(),
            TotalEntries = result.TotalEntries
        };
    }


    async Task<IReadOnlyList<TradingHistoryEntry>> ITradingHistoryPort.LoadBySymbolAsync(string symbol, string? category, long? sinceTimestamp)
    {
        var userId = ResolveUserIdOrThrow();
        return await _store.LoadBySymbolAsync(userId, symbol, category, sinceTimestamp);
    }

    async Task<IReadOnlyList<TradingSummaryBySymbolRow>> ITradingHistoryPort.LoadSummaryBySymbolAsync()
    {
        var userId = ResolveUserIdOrThrow();
        return await _store.LoadSummaryBySymbolAsync(userId);
    }

    async Task<IReadOnlyList<TradingPnlByCoinRow>> ITradingHistoryPort.LoadPnlBySettleCoinAsync()
    {
        var userId = ResolveUserIdOrThrow();
        return await _store.LoadPnlBySettleCoinAsync(userId);
    }

    async Task<IReadOnlyList<TradingDailyPnlRow>> ITradingHistoryPort.LoadDailyPnlAsync(long fromTimestamp, long toTimestamp)
    {
        var userId = ResolveUserIdOrThrow();
        return await _store.LoadDailyPnlAsync(userId, fromTimestamp, toTimestamp);
    }

    async Task<TradingHistoryLatestInfo> ITradingHistoryPort.LoadLatestBySymbolMetaAsync(string symbol, string? category)
    {
        var userId = ResolveUserIdOrThrow();
        return await _store.LoadLatestBySymbolMetaAsync(userId, symbol, category);
    }

    async Task<TradingHistoryMeta> ITradingHistoryPort.LoadMetaAsync()
    {
        var userId = ResolveUserIdOrThrow();
        return await _store.LoadMetaAsync(userId);
    }

    async Task ITradingHistoryPort.SaveMetaAsync(TradingHistoryMeta meta)
    {
        var userId = ResolveUserIdOrThrow();
        await _store.SaveMetaAsync(userId, meta);
    }

    async Task ITradingHistoryPort.RecalculateAsync(long? fromTimestamp)
    {
        var userId = ResolveUserIdOrThrow();
        await _store.RecalculateAsync(userId, fromTimestamp);
    }

    private string? ResolveUserId()
    {
        return User.FindFirstValue(ClaimTypes.NameIdentifier);
    }

    private string ResolveUserIdOrThrow()
    {
        var userId = ResolveUserId();
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new InvalidOperationException("No authenticated user is available for trading history operations.");
        }

        return userId;
    }

    private static TradingHistoryEntry MapForGrid(TradingHistoryEntry entry)
    {
        return new TradingHistoryEntry
        {
            Id = entry.Id,
            Timestamp = entry.Timestamp,
            Symbol = entry.Symbol,
            Category = entry.Category,
            TransactionType = entry.TransactionType,
            Side = entry.Side,
            Size = entry.Size,
            Price = entry.Price,
            Fee = entry.Fee,
            Currency = entry.Currency,
            ChangedAt = entry.ChangedAt,
            Calculated = entry.Calculated
        };
    }
}
