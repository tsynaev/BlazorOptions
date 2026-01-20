namespace BlazorOptions.Server.Models;

public sealed record AuthResponse(string UserName, string Token, string? DeviceId);
