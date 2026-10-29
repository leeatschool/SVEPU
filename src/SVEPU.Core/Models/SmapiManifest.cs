using System.Text.Json;
using System.Text.Json.Serialization;

namespace SVEPU.Core.Models;

public sealed class SmapiManifest
{
    public string Name { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;

    [JsonConverter(typeof(SmapiVersionJsonConverter))]
    public string Version { get; set; } = "1.0.0";

    public string Description { get; set; } = string.Empty;
    public string UniqueID { get; set; } = string.Empty;
    public string? EntryDll { get; set; }
    public SmapiContentPackFor? ContentPackFor { get; set; }
    public List<SmapiDependency>? Dependencies { get; set; }
}

public sealed class SmapiContentPackFor
{
    public string UniqueID { get; set; } = string.Empty;

    [JsonConverter(typeof(SmapiVersionJsonConverter))]
    public string? MinimumVersion { get; set; }
}

public sealed class SmapiDependency
{
    public string UniqueID { get; set; } = string.Empty;

    [JsonConverter(typeof(SmapiVersionJsonConverter))]
    public string? MinimumVersion { get; set; }

    public bool IsRequired { get; set; } = true;
}

public sealed class SmapiVersionJsonConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return reader.GetString() ?? "1.0.0";
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            int major = TryGetInt(root, "MajorVersion");
            int minor = TryGetInt(root, "MinorVersion");
            int patch = TryGetInt(root, "PatchVersion");
            string? build = TryGetString(root, "Build");

            return string.IsNullOrWhiteSpace(build)
                ? $"{major}.{minor}.{patch}"
                : $"{major}.{minor}.{patch}-{build}";
        }

        if (reader.TokenType == JsonTokenType.Number)
        {
            return reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        reader.Skip();
        return "1.0.0";
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value);
    }

    private static int TryGetInt(JsonElement element, string propName)
    {
        foreach (var prop in element.EnumerateObject())
        {
            if (string.Equals(prop.Name, propName, StringComparison.OrdinalIgnoreCase) &&
                prop.Value.ValueKind == JsonValueKind.Number &&
                prop.Value.TryGetInt32(out int val))
            {
                return val;
            }
        }
        return 0;
    }

    private static string? TryGetString(JsonElement element, string propName)
    {
        foreach (var prop in element.EnumerateObject())
        {
            if (string.Equals(prop.Name, propName, StringComparison.OrdinalIgnoreCase) &&
                prop.Value.ValueKind == JsonValueKind.String)
            {
                return prop.Value.GetString();
            }
        }
        return null;
    }
}
