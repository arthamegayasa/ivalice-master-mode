using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Ivalice.Core;

/// <summary>One user-editable setting of a mod, as described by the catalog.</summary>
public sealed record SettingSpec(
    string Key,
    string Type,
    string Label,
    string Help,
    JsonNode? Default,
    double? Min = null,
    double? Max = null,
    double? Step = null,
    string? Unit = null,
    bool Advanced = false);

/// <summary>Curated description of a mod: what it does, what it needs, what can be tuned.</summary>
public sealed record CatalogMod(
    string Id,
    string Name,
    string Author,
    string Role,
    string Category,
    string Summary,
    string Details,
    string SourceUrl,
    string License,
    IReadOnlyList<string> Requires,
    IReadOnlyList<SettingSpec> Settings,
    IReadOnlyList<string>? SharesHookWith = null);

public sealed record CatalogApp(string Id, string Name, int SteamAppId);

public sealed record Catalog(int Schema, CatalogApp App, IReadOnlyList<CatalogMod> Mods)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    public static Catalog Parse(string json)
    {
        var catalog = JsonSerializer.Deserialize<Catalog>(json, Options);
        if (catalog?.App is null || catalog.Mods is null) throw new InvalidDataException("Catalog has no app or mods.");
        // Entries may leave these lists out; callers can then rely on them never being null.
        return catalog with
        {
            Mods = catalog.Mods.Select(m => m with { Requires = m.Requires ?? [], Settings = m.Settings ?? [] }).ToList(),
        };
    }

    public static Catalog Load(string path) => Parse(File.ReadAllText(path));

    public CatalogMod? Find(string id) =>
        Mods.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
}
