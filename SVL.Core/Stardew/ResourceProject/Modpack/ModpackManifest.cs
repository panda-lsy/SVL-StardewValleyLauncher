using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SVL.Core.Stardew.ResourceProject.Modpack;

public class ModpackManifest
{
    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; }

    [JsonPropertyName("author")]
    public string Author { get; set; }

    [JsonPropertyName("created_at")]
    public long CreatedAt { get; set; }

    [JsonPropertyName("mods")]
    public List<ModpackMod> Mods { get; set; } = [];

    [JsonPropertyName("svl_version")]
    public string SvlVersion { get; set; } = "1.0.0";

    [JsonPropertyName("smapi_version")]
    public string SmapiVersion { get; set; } = string.Empty;

    [JsonPropertyName("game_version")]
    public string GameVersion { get; set; } = string.Empty;
}

public class ModpackMod
{
    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("unique_id")]
    public string UniqueId { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; }
}

public class ModpackFileList
{
    [JsonPropertyName("files")]
    public List<string> Files { get; set; } = [];
}
