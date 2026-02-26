using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlazorOptions.Services;

public sealed class AmberdataDerivativesGraphQl
{
    private const string Endpoint = "https://derivatives-graphql.amberdata.com/graphql";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };
    private readonly IHttpClientFactory _httpClientFactory;

    public AmberdataDerivativesGraphQl(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<List<TopTradesItem>> GetDeribitEthTopTradesAsync(DateTime dateStart, string? blockAmount = null)
    {
        var request = new GraphQlRequest(
            "TopTrades",
            TopTradesQuery,
            new GraphQlVariables(
                Exchange: "deribit",
                Symbol: "ETH",
                DateStart: dateStart.ToString("yyyy-MM-dd"),
                BlockAmount: blockAmount));

        var client = _httpClientFactory.CreateClient("External");
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(request, JsonOptions), Encoding.UTF8, "application/json")
        };
        ApplyBrowserLikeHeaders(httpRequest);

        using var response = await client.SendAsync(httpRequest);
        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Amberdata GraphQL HTTP {(int)response.StatusCode} {response.ReasonPhrase}. Body: {json}");
        }

        var payload = JsonSerializer.Deserialize<GraphQlEnvelope>(json, JsonOptions)
                      ?? throw new InvalidOperationException("Failed to deserialize GraphQL response.");
        if (payload.Errors is { Length: > 0 })
        {
            var message = string.Join("; ", payload.Errors.Select(e => e.Message).Where(m => !string.IsNullOrWhiteSpace(m)));
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(message) ? "Amberdata returned GraphQL errors." : message);
        }

        return payload.Data?.TopTrades?.Where(item => !string.IsNullOrWhiteSpace(item.Instrument)).ToList()
               ?? new List<TopTradesItem>();
    }

    public async Task<List<BlockTradesItem>> GetDeribitEthBlockTradesAsync(DateTime date1Utc, DateTime date2Utc)
    {
        var request = new BlockTradesGraphQlRequest(
            "BlockTrades",
            BlockTradesQuery,
            new BlockTradesVariables(
                Exchange: "deribit",
                Symbol: "ETH",
                Date1: date1Utc.ToString("O"),
                Date2: date2Utc.ToString("O")));

        var client = _httpClientFactory.CreateClient("External");
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(request, JsonOptions), Encoding.UTF8, "application/json")
        };
        ApplyBrowserLikeHeaders(httpRequest);

        using var response = await client.SendAsync(httpRequest);
        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Amberdata GraphQL HTTP {(int)response.StatusCode} {response.ReasonPhrase}. Body: {json}");
        }

        var payload = JsonSerializer.Deserialize<BlockTradesEnvelope>(json, JsonOptions)
                      ?? throw new InvalidOperationException("Failed to deserialize GraphQL response.");
        if (payload.Errors is { Length: > 0 })
        {
            var message = string.Join("; ", payload.Errors.Select(e => e.Message).Where(m => !string.IsNullOrWhiteSpace(m)));
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(message) ? "Amberdata returned GraphQL errors." : message);
        }

        return payload.Data?.BlockTrades?.Where(item => !string.IsNullOrWhiteSpace(item.UniqueTrade)).ToList()
               ?? new List<BlockTradesItem>();
    }

    public async Task<List<TopTradesItem>> GetDeribitEthTopTradesByUniqueTradeAsync(
        DateTime date1Utc,
        DateTime date2Utc,
        string uniqueTrade,
        bool blockTradeId = true)
    {
        var request = new TopTradesByUniqueTradeRequest(
            "TopTradesByUniqueTrade",
            TopTradesByUniqueTradeQuery,
            new TopTradesByUniqueTradeVariables(
                Exchange: "deribit",
                Symbol: "ETH",
                StartDate: date1Utc.ToString("O"),
                EndDate: date2Utc.ToString("O"),
                UniTrade: uniqueTrade,
                BlockTradeId: blockTradeId ? "true" : "false"));

        var client = _httpClientFactory.CreateClient("External");
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(request, JsonOptions), Encoding.UTF8, "application/json")
        };
        ApplyBrowserLikeHeaders(httpRequest);

        using var response = await client.SendAsync(httpRequest);
        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Amberdata GraphQL HTTP {(int)response.StatusCode} {response.ReasonPhrase}. Body: {json}");
        }

        var payload = JsonSerializer.Deserialize<TopTradesByUniqueTradeEnvelope>(json, JsonOptions)
                      ?? throw new InvalidOperationException("Failed to deserialize GraphQL response.");
        if (payload.Errors is { Length: > 0 })
        {
            var message = string.Join("; ", payload.Errors.Select(e => e.Message).Where(m => !string.IsNullOrWhiteSpace(m)));
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(message) ? "Amberdata returned GraphQL errors." : message);
        }

        return payload.Data?.TopTradesByUniqueTrade?.Where(item => !string.IsNullOrWhiteSpace(item.Instrument)).ToList()
               ?? new List<TopTradesItem>();
    }

    private static void ApplyBrowserLikeHeaders(HttpRequestMessage message)
    {
        message.Headers.Accept.Clear();
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Headers.TryAddWithoutValidation("Origin", "https://pro.amberdata.io");
        message.Headers.TryAddWithoutValidation("Referer", "https://pro.amberdata.io/options/deribit/eth/options-scanner/");
        message.Headers.TryAddWithoutValidation("x-amberdata-client", "derivatives-gui");
        message.Headers.TryAddWithoutValidation("x-amberdata-client-id", "undefined");
        message.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
    }

    private const string TopTradesQuery = """
            query TopTrades($dateStart: String, $symbol: SymbolEnumType, $exchange: ExchangeEnumType, $blockAmount: String) {
              TopTrades(
                startDate: $dateStart
                currency: $symbol
                exchange: $exchange
                blockAmount: $blockAmount
              ) {
                instrument
                tradeAmount
                blockAmount
                price
                priceUsd
                sizeUSD
                sizeDelta
                sizeVega
                sizeGamma
                sizeTheta
                date
                amberdataDirection
                exchangeDirection
              }
            }
            """;
    private const string BlockTradesQuery = """
            query BlockTrades($date1: String, $date2: String, $symbol: SymbolEnumType, $exchange: ExchangeEnumType) {
              BlockTrades(
                startDate: $date1
                endDate: $date2
                currency: $symbol
                exchange: $exchange
              ) {
                uniqueTrade
                indexPrice
                tradeAmount
                netPremium
                numTrades
              }
            }
            """;
    private const string TopTradesByUniqueTradeQuery = """
            query TopTradesByUniqueTrade($startDate: String, $endDate: String, $symbol: SymbolEnumType, $exchange: ExchangeEnumType, $uniTrade: String, $blockTradeId: String) {
              TopTradesByUniqueTrade(
                blockTradeId: $blockTradeId
                startDate: $startDate
                endDate: $endDate
                currency: $symbol
                exchange: $exchange
                uniqueTrade: $uniTrade
              ) {
                exchange
                currency
                date
                indexPrice
                instrument
                exchangeDirection
                amberdataDirection
                blockTradeId
                numberOfLegs
                tradeAmount
                blockAmount
                tradeIv
                price
                priceUsd
                openInterestChange
                sizeUSD
                sizeDelta
                sizeVega
                sizeGamma
                sizeTheta
                hedgeInstrument
                hedgeIsBuySide
                hedgePrice
                hedgeVolume
              }
            }
            """;

    private sealed record GraphQlRequest(
        [property: JsonPropertyName("operationName")] string OperationName,
        [property: JsonPropertyName("query")] string Query,
        [property: JsonPropertyName("variables")] GraphQlVariables Variables);

    private sealed record GraphQlVariables(
        [property: JsonPropertyName("exchange")] string Exchange,
        [property: JsonPropertyName("symbol")] string Symbol,
        [property: JsonPropertyName("dateStart")] string DateStart,
        [property: JsonPropertyName("blockAmount")] string? BlockAmount);
    private sealed record BlockTradesGraphQlRequest(
        [property: JsonPropertyName("operationName")] string OperationName,
        [property: JsonPropertyName("query")] string Query,
        [property: JsonPropertyName("variables")] BlockTradesVariables Variables);
    private sealed record TopTradesByUniqueTradeRequest(
        [property: JsonPropertyName("operationName")] string OperationName,
        [property: JsonPropertyName("query")] string Query,
        [property: JsonPropertyName("variables")] TopTradesByUniqueTradeVariables Variables);

    private sealed record BlockTradesVariables(
        [property: JsonPropertyName("exchange")] string Exchange,
        [property: JsonPropertyName("symbol")] string Symbol,
        [property: JsonPropertyName("date1")] string Date1,
        [property: JsonPropertyName("date2")] string Date2);
    private sealed record TopTradesByUniqueTradeVariables(
        [property: JsonPropertyName("exchange")] string Exchange,
        [property: JsonPropertyName("symbol")] string Symbol,
        [property: JsonPropertyName("startDate")] string StartDate,
        [property: JsonPropertyName("endDate")] string EndDate,
        [property: JsonPropertyName("uniTrade")] string UniTrade,
        [property: JsonPropertyName("blockTradeId")] string BlockTradeId);

    private sealed class GraphQlEnvelope
    {
        [JsonPropertyName("data")] public GraphQlData? Data { get; set; }
        [JsonPropertyName("errors")] public GraphQlError[]? Errors { get; set; }
    }

    private sealed class BlockTradesEnvelope
    {
        [JsonPropertyName("data")] public BlockTradesData? Data { get; set; }
        [JsonPropertyName("errors")] public GraphQlError[]? Errors { get; set; }
    }
    private sealed class TopTradesByUniqueTradeEnvelope
    {
        [JsonPropertyName("data")] public TopTradesByUniqueTradeData? Data { get; set; }
        [JsonPropertyName("errors")] public GraphQlError[]? Errors { get; set; }
    }

    private sealed class GraphQlData
    {
        [JsonPropertyName("TopTrades")] public TopTradesItem[]? TopTrades { get; set; }
    }
    private sealed class BlockTradesData
    {
        [JsonPropertyName("BlockTrades")] public BlockTradesItem[]? BlockTrades { get; set; }
    }
    private sealed class TopTradesByUniqueTradeData
    {
        [JsonPropertyName("TopTradesByUniqueTrade")] public TopTradesItem[]? TopTradesByUniqueTrade { get; set; }
    }

    private sealed class GraphQlError
    {
        [JsonPropertyName("message")] public string? Message { get; set; }
    }
}
