using System.Text.Json;

namespace SprocketTools;

/// Export preferences only: never writes to the vehicle or its component blueprints.
/// VUIDs are local to a design, so include the design name rather than sharing IDs globally.
public sealed class GunAnnotationPreferences
{
    readonly HashSet<string> hidden;

    public GunAnnotationPreferences(string? json)
    {
        try { hidden = new(JsonSerializer.Deserialize<string[]>(json ?? "[]") ?? Array.Empty<string>(), StringComparer.Ordinal); }
        catch (JsonException) { hidden = new(StringComparer.Ordinal); }
    }

    public static string Key(string? designName, int gunId) =>
        string.IsNullOrWhiteSpace(designName) || gunId < 0 ? "" :
        JsonSerializer.Serialize(new[] { designName, gunId.ToString(System.Globalization.CultureInfo.InvariantCulture) });

    public bool Shows(string key) => key.Length == 0 || !hidden.Contains(key);

    public void Set(string key, bool show)
    {
        if (key.Length == 0) return;
        if (show) hidden.Remove(key); else hidden.Add(key);
    }

    public string Serialize() => JsonSerializer.Serialize(hidden.OrderBy(k => k, StringComparer.Ordinal).ToArray());
}
