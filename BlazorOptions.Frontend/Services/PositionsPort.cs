using System.Net.Http.Json;
using System.Text.Json;
using BlazorOptions.API.Positions;
using BlazorOptions.API.TradingHistory;

namespace BlazorOptions.Services;

public sealed class PositionsPort : IPositionsPort
{
    private readonly HttpClient _httpClient;
    private readonly Microsoft.Extensions.Options.IOptions<AuthSessionState> _sessionState;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public PositionsPort(
        HttpClient httpClient,
        Microsoft.Extensions.Options.IOptions<AuthSessionState> sessionState)
    {
        _httpClient = httpClient;
        _sessionState = sessionState;
    }

    public async Task<IReadOnlyList<PositionDto>> LoadPositionsAsync()
    {
        var response = await SendAsync(HttpMethod.Get, "api/positions");
        var items = await response.Content.ReadFromJsonAsync<PositionDto[]>(JsonOptions);
        return items ?? Array.Empty<PositionDto>();
    }

    public async Task SavePositionsAsync(IReadOnlyList<PositionDto> positions)
    {
        await SendAsync(HttpMethod.Post, "api/positions", positions);
    }

    public async Task SavePositionAsync(PositionDto position)
    {
        await SendAsync(HttpMethod.Put, $"api/positions/{position.Id}", position);
    }

    public async Task DeletePositionAsync(Guid positionId)
    {
        await SendAsync(HttpMethod.Delete, $"api/positions/{positionId}");
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string uri, object? payload = null)
    {
        var request = new HttpRequestMessage(method, uri);
        var token = _sessionState.Value.Token;
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Add("X-User-Token", token);
        }

        if (payload is not null)
        {
            request.Content = JsonContent.Create(payload, options: JsonOptions);
        }

        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var problem = await ReadProblemDetailsAsync(response);
            if (problem is not null)
            {
                if (problem.Status == 401)
                {
                    throw new UnauthorizedAccessException(problem.Title ?? "Sign in to access positions.");
                }

                throw new ProblemDetailsException(problem);
            }

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                throw new UnauthorizedAccessException("Sign in to access positions.");
            }

            var error = await ReadErrorAsync(response);
            throw new HttpRequestException(error ?? $"Request to '{uri}' failed.");
        }

        return response;
    }

    private static async Task<string?> ReadErrorAsync(HttpResponseMessage response)
    {
        try
        {
            var payload = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
            if (payload is not null && payload.TryGetValue("error", out var error))
            {
                return error;
            }
        }
        catch
        {
            return null;
        }

        var text = await response.Content.ReadAsStringAsync();
        if (!string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        return null;
    }

    private static async Task<ProblemDetails?> ReadProblemDetailsAsync(HttpResponseMessage response)
    {
        if (response.Content is null)
        {
            return null;
        }

        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return null;
        }

        if (!contentType.Contains("application/problem+json", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            return await response.Content.ReadFromJsonAsync<ProblemDetails>(JsonOptions);
        }
        catch
        {
            return null;
        }
    }
}
