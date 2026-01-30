using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlazorOptions.ViewModels;

public sealed class SafeDecimalConverter : JsonConverter<decimal>
{
    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            if (reader.TryGetDecimal(out var value))
            {
                return value;
            }

            if (reader.TryGetDouble(out var doubleValue))
            {
                if (doubleValue > (double)decimal.MaxValue || doubleValue < (double)decimal.MinValue)
                {
                    return 0m;
                }

                return (decimal)doubleValue;
            }
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            var raw = reader.GetString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return 0m;
            }

            if (decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }

            if (double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var doubleParsed))
            {
                if (doubleParsed > (double)decimal.MaxValue || doubleParsed < (double)decimal.MinValue)
                {
                    return 0m;
                }

                return (decimal)doubleParsed;
            }
        }

        return 0m;
    }

    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value);
    }
}
