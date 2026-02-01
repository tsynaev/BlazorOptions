using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlazorOptions.Services;

public sealed class SafeDecimalConverter : JsonConverter<decimal>
{
    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        try
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                var text = reader.GetString();
                if (decimal.TryParse(text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                {
                    return parsed;
                }

                return 0m;
            }

            return reader.GetDecimal();
        }
        catch
        {
            return 0m;
        }
    }

    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value);
    }
}
