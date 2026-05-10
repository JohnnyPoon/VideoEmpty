using System.Text.Json;
using System.Text.Json.Serialization;
using VideoEmpty.Core.Model;

namespace VideoEmpty.Core.Serialization;

public static class ProjectJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var o = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters =
            {
                new JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
                new ColorJsonConverter(),
                new ElementJsonConverter()
            }
        };
        return o;
    }

    public static string Serialize(Project project) =>
        JsonSerializer.Serialize(project, Options);

    public static Project Deserialize(string json) =>
        JsonSerializer.Deserialize<Project>(json, Options)
        ?? throw new InvalidDataException("Project JSON was null.");

    public static void Save(Project project, string path) =>
        File.WriteAllText(path, Serialize(project));

    public static Project Load(string path) => Deserialize(File.ReadAllText(path));
}

internal sealed class ColorJsonConverter : JsonConverter<Color>
{
    public override Color Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => Color.FromHex(reader.GetString() ?? "#FF000000");
    public override void Write(Utf8JsonWriter writer, Color value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToHex());
}

internal sealed class ElementJsonConverter : JsonConverter<Element>
{
    public override Element Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        if (!root.TryGetProperty("kind", out var kindProp))
            throw new JsonException("Element missing discriminator 'kind'.");
        var kind = kindProp.GetString();
        var json = root.GetRawText();
        return kind switch
        {
            "shape" => JsonSerializer.Deserialize<ShapeElement>(json, options)!,
            "text"  => JsonSerializer.Deserialize<TextElement>(json, options)!,
            _ => throw new JsonException($"Unknown element kind '{kind}'.")
        };
    }

    public override void Write(Utf8JsonWriter writer, Element value, JsonSerializerOptions options)
    {
        var (kind, node) = value switch
        {
            ShapeElement s => ("shape", JsonSerializer.SerializeToNode(s, options)!),
            TextElement t  => ("text",  JsonSerializer.SerializeToNode(t,  options)!),
            _ => throw new JsonException($"Unknown element type '{value.GetType().Name}'.")
        };
        node["kind"] = kind;
        node.WriteTo(writer, options);
    }
}
