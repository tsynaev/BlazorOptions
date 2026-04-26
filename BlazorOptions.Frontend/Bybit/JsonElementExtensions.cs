using System.Globalization;
using System.Text.Json;

namespace BlazorOptions.Services;

public static class JsonElementExtensions
{
    public static bool TryReadString(this JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind == JsonValueKind.Null
            || property.ValueKind == JsonValueKind.Undefined)
        {
            return false;
        }

        value = property.ValueKind == JsonValueKind.String
            ? property.GetString()?.Trim() ?? string.Empty
            : property.GetRawText().Trim().Trim('"');

        return !string.IsNullOrWhiteSpace(value);
    }

    public static string ReadString(this JsonElement element, string propertyName)
    {
        return element.TryReadString(propertyName, out var value) ? value : string.Empty;
    }

    public static string ReadString(this JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (element.TryReadString(propertyName, out var value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    public static decimal ReadDecimal(this JsonElement element, string propertyName, params string[] fallbackPropertyNames)
    {
        if (element.TryReadDecimal(propertyName, out var value))
        {
            return value;
        }

        foreach (var fallback in fallbackPropertyNames)
        {
            if (element.TryReadDecimal(fallback, out value))
            {
                return value;
            }
        }

        return 0m;
    }

    public static decimal? ReadNullableDecimal(this JsonElement element, string propertyName)
    {
        return element.TryReadDecimal(propertyName, out var value) ? value : null;
    }

    public static decimal? ReadNullableDecimal(this JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (element.TryReadDecimal(propertyName, out var value))
            {
                return value;
            }
        }

        return null;
    }

    public static bool TryReadDecimal(this JsonElement element, string propertyName, out decimal value)
    {
        value = 0m;
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind == JsonValueKind.Null
            || property.ValueKind == JsonValueKind.Undefined)
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDecimal(out value))
        {
            return true;
        }

        var raw = property.ValueKind == JsonValueKind.String ? property.GetString() : property.GetRawText();
        return !string.IsNullOrWhiteSpace(raw)
               && decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
    }

    public static bool TryReadInt(this JsonElement element, out int value)
    {
        value = 0;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value))
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        return int.TryParse(element.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out value);
    }

    public static bool TryReadInt(this JsonElement element, string propertyName, out int value)
    {
        value = 0;
        return element.TryGetProperty(propertyName, out var property) && property.TryReadInt(out value);
    }

    public static bool TryReadLong(this JsonElement element, string propertyName, out long value)
    {
        value = 0;
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out value))
        {
            return true;
        }

        return property.ValueKind == JsonValueKind.String
               && long.TryParse(property.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out value);
    }
}
