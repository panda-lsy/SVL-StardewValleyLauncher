using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SVL.Core.Stardew.Mod;

public class SdVModManifest
{
    [JsonPropertyName("Name")]
    public string Name { get; set; }

    [JsonPropertyName("Author")]
    public string Author { get; set; }

    [JsonPropertyName("Version")]
    public string Version { get; set; }

    [JsonPropertyName("Description")]
    public string Description { get; set; }

    [JsonPropertyName("UniqueID")]
    public string UniqueId { get; set; }

    [JsonPropertyName("EntryDll")]
    public string EntryDll { get; set; }

    [JsonPropertyName("MinimumApiVersion")]
    public string MinimumApiVersion { get; set; }

    [JsonPropertyName("MinimumGameVersion")]
    public string MinimumGameVersion { get; set; }

    [JsonPropertyName("UpdateKeys")]
    public List<string> UpdateKeys { get; set; } = [];

    [JsonPropertyName("ContentPackFor")]
    public ContentPackFor ContentPackFor { get; set; }

    [JsonPropertyName("Dependencies")]
    public List<ModDependency> Dependencies { get; set; } = [];
}

public class ContentPackFor
{
    [JsonPropertyName("UniqueID")]
    public string UniqueId { get; set; }

    [JsonPropertyName("MinimumVersion")]
    public string MinimumVersion { get; set; }
}

public class ModDependency
{
    [JsonPropertyName("UniqueID")]
    public string UniqueId { get; set; }

    [JsonPropertyName("IsRequired")]
    public bool IsRequired { get; set; } = true;

    [JsonPropertyName("MinimumVersion")]
    public string MinimumVersion { get; set; }
}
