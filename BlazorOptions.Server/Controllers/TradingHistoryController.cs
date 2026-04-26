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
    public async Task<IActionResult> SaveTradesAsync([FromBody] TradingHistoryEntry[] entries, [FromQuery] string? exchangeConnectionId)
    {
        var userId = ResolveUserId();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized");
        }

        await _store.SaveTradesAsync(userId, entries, exchangeConnectionId);
        return Ok(new { count = entries.Length });
    }

    [HttpGet("entries")]
    public async Task<IActionResult> LoadEntriesAsync([FromQuery] string? baseAsset, [FromQuery] int startIndex, [FromQuery] int limit, [FromQuery] string? exchangeConnectionId)
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

        var result = await _store.LoadEntriesAsync(userId, baseAsset, startIndex, limit, exchangeConnectionId);
        var mapped = new TradingHistoryResult
        {
            Entries = result.Entries.Select(MapForGrid).ToList(),
            TotalEntries = result.TotalEntries
        };
        return Ok(mapped);
    }

    [HttpGet("all")]
    public async Task<IActionResult> LoadAllAscAsync([FromQuery] string? exchangeConnectionId)
    {
        var userId = ResolveUserId();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized");
        }

        var entries = await _store.LoadAllAscAsync(userId, exchangeConnectionId);
        return Ok(entries);
    }

    [HttpGet("by-symbol")]
    public async Task<IActionResult> LoadBySymbolAsync(
        [FromQuery] string symbol,
        [FromQuery] string? category,
        [FromQuery] long? sinceTimestamp,
        [FromQuery] string? exchangeConnectionId)
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

        var entries = await _store.LoadBySymbolAsync(userId, symbol, category, sinceTimestamp, exchangeConnectionId);
        return Ok(entries);
    }

    [HttpGet("summary/by-symbol")]
    public async Task<IActionResult> LoadSummaryBySymbolAsync([FromQuery] string? exchangeConnectionId)
    {
        var userId = ResolveUserId();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized");
        }

        var summary = await _store.LoadSummaryBySymbolAsync(userId, exchangeConnectionId);
        return Ok(summary);
    }

    [HttpGet("summary/by-settle-coin")]
    public async Task<IActionResult> LoadPnlBySettleCoinAsync([FromQuery] string? exchangeConnectionId)
    {
        var userId = ResolveUserId();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized");
        }

        var summary = await _store.LoadPnlBySettleCoinAsync(userId, exchangeConnectionId);
        return Ok(summary);
    }

    [HttpGet("daily-pnl")]
    public async Task<IActionResult> LoadDailyPnlAsync(
        [FromQuery] long fromTimestamp,
        [FromQuery] long toTimestamp,
        [FromQuery] string? exchangeConnectionId)
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

        var rows = await _store.LoadDailyPnlAsync(userId, fromTimestamp, toTimestamp, exchangeConnectionId);
        return Ok(rows);
    }

    [HttpGet("latest-meta")]
    public async Task<IActionResult> LoadLatestMetaAsync(
        [FromQuery] string symbol,
        [FromQuery] string? category,
        [FromQuery] string? exchangeConnectionId)
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

        var meta = await _store.LoadLatestBySymbolMetaAsync(userId, symbol, category, exchangeConnectionId);
        return Ok(meta);
    }

    [HttpGet("meta")]
    public async Task<IActionResult> LoadMetaAsync([FromQuery] string? exchangeConnectionId)
    {
        var userId = ResolveUserId();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized");
        }

        var meta = await _store.LoadMetaAsync(userId, exchangeConnectionId);
        return Ok(meta);
    }

    [HttpPost("meta")]
    public async Task<IActionResult> SaveMetaAsync([FromBody] TradingHistoryMeta meta, [FromQuery] string? exchangeConnectionId)
    {
        var userId = ResolveUserId();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized");
        }

        await _store.SaveMetaAsync(userId, meta, exchangeConnectionId);
        return Ok();
    }

    [HttpPost("recalculate")]
    public async Task<IActionResult> RecalculateAsync([FromQuery] long? fromTimestamp, [FromQuery] string? exchangeConnectionId)
    {
        var userId = ResolveUserId();
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized");
        }

        await _store.RecalculateAsync(userId, fromTimestamp, exchangeConnectionId);
        return Ok();
    }

    async Task ITradingHistoryPort.SaveTradesAsync(IReadOnlyList<TradingHistoryEntry> entries, string? exchangeConnectionId)
    {
        var userId = ResolveUserIdOrThrow();
        await _store.SaveTradesAsync(userId, entries, exchangeConnectionId);
    }

    async Task<TradingHistoryResult> ITradingHistoryPort.LoadEntriesAsync(string? baseAsset, int startIndex, int limit, string? exchangeConnectionId)
    {
        var userId = ResolveUserIdOrThrow();
        var result = await _store.LoadEntriesAsync(userId, baseAsset, startIndex, limit, exchangeConnectionId);
        return new TradingHistoryResult
        {
            Entries = result.Entries.Select(MapForGrid).ToList(),
            TotalEntries = result.TotalEntries
        };
    }


    async Task<IReadOnlyList<TradingHistoryEntry>> ITradingHistoryPort.LoadBySymbolAsync(string symbol, string? category, long? sinceTimestamp, string? exchangeConnectionId)
    {
        var userId = ResolveUserIdOrThrow();
        return await _store.LoadBySymbolAsync(userId, symbol, category, sinceTimestamp, exchangeConnectionId);
    }

    async Task<IReadOnlyList<TradingSummaryBySymbolRow>> ITradingHistoryPort.LoadSummaryBySymbolAsync(string? exchangeConnectionId)
    {
        var userId = ResolveUserIdOrThrow();
        return await _store.LoadSummaryBySymbolAsync(userId, exchangeConnectionId);
    }

    async Task<IReadOnlyList<TradingPnlByCoinRow>> ITradingHistoryPort.LoadPnlBySettleCoinAsync(string? exchangeConnectionId)
    {
        var userId = ResolveUserIdOrThrow();
        return await _store.LoadPnlBySettleCoinAsync(userId, exchangeConnectionId);
    }

    async Task<IReadOnlyList<TradingDailyPnlRow>> ITradingHistoryPort.LoadDailyPnlAsync(long fromTimestamp, long toTimestamp, string? exchangeConnectionId)
    {
        var userId = ResolveUserIdOrThrow();
        return await _store.LoadDailyPnlAsync(userId, fromTimestamp, toTimestamp, exchangeConnectionId);
    }

    async Task<TradingHistoryLatestInfo> ITradingHistoryPort.LoadLatestBySymbolMetaAsync(string symbol, string? category, string? exchangeConnectionId)
    {
        var userId = ResolveUserIdOrThrow();
        return await _store.LoadLatestBySymbolMetaAsync(userId, symbol, category, exchangeConnectionId);
    }

    async Task<TradingHistoryMeta> ITradingHistoryPort.LoadMetaAsync(string? exchangeConnectionId)
    {
        var userId = ResolveUserIdOrThrow();
        return await _store.LoadMetaAsync(userId, exchangeConnectionId);
    }

    async Task ITradingHistoryPort.SaveMetaAsync(TradingHistoryMeta meta, string? exchangeConnectionId)
    {
        var userId = ResolveUserIdOrThrow();
        await _store.SaveMetaAsync(userId, meta, exchangeConnectionId);
    }

    async Task ITradingHistoryPort.RecalculateAsync(long? fromTimestamp, string? exchangeConnectionId)
    {
        var userId = ResolveUserIdOrThrow();
        await _store.RecalculateAsync(userId, fromTimestamp, exchangeConnectionId);
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
