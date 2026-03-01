using System;

namespace SVL.Core.Stardew.Instance;

public class StardewInstanceInfo
{
    /// <summary>
    /// 游戏本体路径（所有实例共享同一个游戏本体）
    /// </summary>
    public string GameBasePath { get; set; } = string.Empty;

    public string GameVersion { get; set; }
    public string SmapVersion { get; set; }
    public string Platform { get; set; }
    public int ModCount { get; set; }
    public DateTime LastPlayed { get; set; }
}
