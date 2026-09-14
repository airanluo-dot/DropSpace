using System.Text.Json;
using System.Text.Json.Serialization;

namespace DropSpace.Core.Updates;

public sealed class UpdateChannelJsonConverter : JsonConverter<UpdateChannel>
{
    public static bool TryParse(string? value, out UpdateChannel channel)
    {
        channel = UpdateChannel.Stable;
        if (string.Equals(value, "stable", StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.Equals(value, "beta", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(value, "preview", StringComparison.OrdinalIgnoreCase)) return false;
        channel = UpdateChannel.Beta;
        return true;
    }

    public override UpdateChannel Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String && TryParse(reader.GetString(), out var channel)) return channel;
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var number) && number is 0 or 1) return (UpdateChannel)number;
        throw new JsonException("Unsupported update channel.");
    }

    public override void Write(Utf8JsonWriter writer, UpdateChannel value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch { UpdateChannel.Stable => "Stable", UpdateChannel.Beta => "Beta", _ => throw new JsonException("Unsupported update channel.") });
}
