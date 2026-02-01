namespace BlazorOptions.Services;

public sealed class AuthSessionOptions
{
    public const string SectionName = "AuthSession";

    public string TokenKey { get; set; } = "blazor-options-auth-token";

    public string UserKey { get; set; } = "blazor-options-auth-user";
}
