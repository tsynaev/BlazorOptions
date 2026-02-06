using System;

namespace BlazorOptions.Diagnostics;

public static class TelemetryOptions
{
    public const string StorageKey = "telemetry";

    public static bool IsEnabled(string? storedValue)
    {
        if (string.IsNullOrWhiteSpace(storedValue))
        {
            return false;
        }

        return !string.Equals(storedValue.Trim(), "false", StringComparison.OrdinalIgnoreCase);
    }
}
