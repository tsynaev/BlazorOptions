using System.Text.Json;

namespace BlazorOptions.ViewModels;

public static class AccountRiskSettingsStorage
{
    public const string StorageKey = "blazor-options-account-risk-settings";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static AccountRiskSettings Default => new();

    public static AccountRiskSettings Parse(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return Default;
        }

        try
        {
            return JsonSerializer.Deserialize<AccountRiskSettings>(payload, SerializerOptions) ?? Default;
        }
        catch
        {
            return Default;
        }
    }

    public static string Serialize(AccountRiskSettings settings)
    {
        return JsonSerializer.Serialize(settings, SerializerOptions);
    }
}
