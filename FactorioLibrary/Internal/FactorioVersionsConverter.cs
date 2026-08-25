using System.Text.Json;
using System.Text.Json.Serialization;
using FactorioLibrary.Objects;

namespace FactorioLibrary.Internal;

internal class FactorioVersionsConverter : JsonConverter<FactorioVersions>
{
    public override FactorioVersions Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        Dictionary<string, List<FactorioVersion>> platforms = JsonSerializer.Deserialize<Dictionary<string, List<FactorioVersion>>>(ref reader, options) ?? [];

        return new() { Platforms = platforms };
    }

    public override void Write(Utf8JsonWriter writer, FactorioVersions value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value.Platforms, options);
    }
}
