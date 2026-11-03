namespace SVL.Avalonia.Models;

/// <summary>一个游戏运行目录独有的启动偏好。</summary>
public sealed class InstanceLaunchSettings
{
    public string WindowTitle { get; set; } = "<default>";
    public string CustomArguments { get; set; } = string.Empty;
    public bool AutoConnectServer { get; set; }
    public string ServerAddress { get; set; } = string.Empty;
    public string SteamInviteCode { get; set; } = string.Empty;
    public bool OverrideSteamLaunchOptions { get; set; }
    public string SteamLaunchOptions { get; set; } = string.Empty;

    public static InstanceLaunchSettings FromGlobal(AppUserSettings settings) => new()
    {
        WindowTitle = string.IsNullOrWhiteSpace(settings.GameWindowTitle) ? "<default>" : settings.GameWindowTitle,
        CustomArguments = settings.InstanceCustomLaunchArguments ?? string.Empty,
        AutoConnectServer = settings.InstanceAutoConnectServer,
        ServerAddress = settings.InstanceServerAddress ?? string.Empty,
        SteamInviteCode = settings.InstanceSteamInviteCode ?? string.Empty,
        OverrideSteamLaunchOptions = settings.OverrideSteamLaunchOptions,
        SteamLaunchOptions = settings.SteamLaunchOptions ?? string.Empty
    };
}
