using System.Net.Http.Json;

namespace BlazorOptions.Services;

public class AuthApiService
{
    private readonly HttpClient _httpClient;
    private readonly AuthSessionService _sessionService;

    public AuthApiService(HttpClient httpClient, AuthSessionService sessionService)
    {
        _httpClient = httpClient;
        _sessionService = sessionService;
    }

    public async Task<(bool Success, string? Error)> RegisterAsync(string userName, string password)
    {
        var response = await _httpClient.PostAsJsonAsync("api/auth/register", new AuthRequest(userName, password));
        if (!response.IsSuccessStatusCode)
        {
            return (false, await ReadErrorAsync(response));
        }

        var authResponse = await response.Content.ReadFromJsonAsync<AuthResponse>();
        if (authResponse is null)
        {
            return (false, "Unexpected response from server.");
        }

        await _sessionService.SetSessionAsync(authResponse.UserName, authResponse.Token);
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> LoginAsync(string userName, string password)
    {
        var response = await _httpClient.PostAsJsonAsync("api/auth/login", new AuthRequest(userName, password));
        if (!response.IsSuccessStatusCode)
        {
            return (false, await ReadErrorAsync(response));
        }

        var authResponse = await response.Content.ReadFromJsonAsync<AuthResponse>();
        if (authResponse is null)
        {
            return (false, "Unexpected response from server.");
        }

        await _sessionService.SetSessionAsync(authResponse.UserName, authResponse.Token);
        return (true, null);
    }

    public async Task LogoutAsync()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "api/auth/logout");
        if (!string.IsNullOrWhiteSpace(_sessionService.Token))
        {
            request.Headers.Add("X-User-Token", _sessionService.Token);
        }

        _ = await _httpClient.SendAsync(request);
        await _sessionService.ClearAsync();
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
            return "Request failed.";
        }

        return "Request failed.";
    }

    private sealed record AuthRequest(string UserName, string Password);

    private sealed record AuthResponse(string UserName, string Token);
}
