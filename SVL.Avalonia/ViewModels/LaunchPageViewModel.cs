using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SVL.Avalonia.Services;
using SVL.Core.Platform.Abstractions;
using System;
using System.Collections.Generic;
using System.IO;

namespace SVL.Avalonia.ViewModels;

public partial class LaunchPageViewModel : ObservableObject
{
    private readonly IGameInstallPathLocator _gameInstallPathLocator;
    private readonly IExternalProcessService _externalProcessService;
    private readonly AppUserSettingsStore _settingsStore;
    private readonly LocalizationService _localizationService;
    private readonly ImageResourceService _imageResourceService;
    private string _currentGamePath = string.Empty;
    private string _preferredLaunchModeToken = "auto";

    public event Action? NavigateToInstancesRequested;
    public event Action? NavigateToVersionSettingsRequested;
    public event Action? NavigateToModManageRequested;

    [ObservableProperty]
    private string _instanceName = string.Empty;

    [ObservableProperty]
    private string _gameVersion = string.Empty;

    [ObservableProperty]
    private string _versionStatus = string.Empty;

    [ObservableProperty]
    private bool _isLaunching;

    [ObservableProperty]
    private string _launchButtonText = string.Empty;

    [ObservableProperty]
    private bool _showModManageButton;

    [ObservableProperty]
    private bool _hasInstances;

    [ObservableProperty]
    private string _actionStatus = string.Empty;

    [ObservableProperty]
    private string _preferredLaunchMode = string.Empty;

    [ObservableProperty]
    private bool _enableSafeLaunch;

    [ObservableProperty]
    private string _safeLaunchState = string.Empty;

    [ObservableProperty]
    private string _instanceInfoLabel = string.Empty;

    [ObservableProperty]
    private string _launchModeLabel = string.Empty;

    [ObservableProperty]
    private string _safeLaunchLabel = string.Empty;

    [ObservableProperty]
    private string _statusLabel = string.Empty;

    [ObservableProperty]
    private string _refreshButtonText = string.Empty;

    [ObservableProperty]
    private string _versionSelectButtonText = string.Empty;

    [ObservableProperty]
    private string _modManageButtonText = string.Empty;

    [ObservableProperty]
    private string _versionSettingsButtonText = string.Empty;

    [ObservableProperty]
    private string _welcomeTitle = string.Empty;

    [ObservableProperty]
    private string _getStartedTitle = string.Empty;

    [ObservableProperty]
    private string _statusHeadline = string.Empty;

    [ObservableProperty]
    private string _statusSubline = string.Empty;

    [ObservableProperty]
    private string _brandText = string.Empty;

    [ObservableProperty]
    private string _guideNoInstanceLead = string.Empty;

    [ObservableProperty]
    private string _guideStep1 = string.Empty;

    [ObservableProperty]
    private string _guideStep2 = string.Empty;

    [ObservableProperty]
    private string _guideStep3 = string.Empty;

    [ObservableProperty]
    private string _guideStep4 = string.Empty;

    [ObservableProperty]
    private string _guideUsageTitle = string.Empty;

    [ObservableProperty]
    private string _guideUsageLine1 = string.Empty;

    [ObservableProperty]
    private string _guideUsageLine2 = string.Empty;

    [ObservableProperty]
    private string _guideUsageModManageLine = string.Empty;

    [ObservableProperty]
    private string _instanceIconSource = "avares://SVL.Avalonia/Assets/Icons/Junimo.png";

    public bool ShowOnboardingCard => !HasInstances;

    public bool ShowNoInstanceIcon => !HasInstances;

    public bool ShowModdedInstanceIcon => HasInstances && ShowModManageButton;

    public bool ShowVanillaInstanceIcon => HasInstances && !ShowModManageButton;

    public bool CanOpenVersionSettings => HasInstances;

    partial void OnActionStatusChanged(string value)
    {
        RefreshStatusBanner();
    }

    partial void OnIsLaunchingChanged(bool value)
    {
        RefreshStatusBanner();
    }

    partial void OnEnableSafeLaunchChanged(bool value)
    {
        SafeLaunchState = GetSafeLaunchStateText(value);
    }

    partial void OnShowModManageButtonChanged(bool value)
    {
        NotifyInstanceFlavorIconVisibilityChanged();
        RefreshStatusBanner();
    }

    partial void OnHasInstancesChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowOnboardingCard));
        OnPropertyChanged(nameof(CanOpenVersionSettings));
        NotifyInstanceFlavorIconVisibilityChanged();
        RefreshStatusBanner();
    }

    public LaunchPageViewModel(
        IGameInstallPathLocator gameInstallPathLocator,
        IExternalProcessService externalProcessService,
        AppUserSettingsStore settingsStore,
        LocalizationService localizationService,
        ImageResourceService imageResourceService)
    {
        _gameInstallPathLocator = gameInstallPathLocator;
        _externalProcessService = externalProcessService;
        _settingsStore = settingsStore;
        _localizationService = localizationService;
        _imageResourceService = imageResourceService;
        _localizationService.LanguageChanged += ApplyLocalizedTexts;
        _imageResourceService.ResourcesChanged += RefreshInstanceFromLocalEnvironment;
        ApplyLocalizedTexts();
        RefreshLaunchPreferencesFromSettings();
        RefreshInstanceFromLocalEnvironment();
        NotifyInstanceFlavorIconVisibilityChanged();
        RefreshStatusBanner();
    }

    private void NotifyInstanceFlavorIconVisibilityChanged()
    {
        OnPropertyChanged(nameof(ShowNoInstanceIcon));
        OnPropertyChanged(nameof(ShowModdedInstanceIcon));
        OnPropertyChanged(nameof(ShowVanillaInstanceIcon));
    }

    private void RefreshStatusBanner()
    {
        if (IsLaunching)
        {
            StatusHeadline = _localizationService.Get("Launch.Status.StartingTitle");
            StatusSubline = _localizationService.Get("Launch.Status.StartingSubtitle");
            return;
        }

        if (!HasInstances)
        {
            StatusHeadline = _localizationService.Get("Launch.Status.NoInstanceTitle");
            StatusSubline = _localizationService.Get("Launch.Status.NoInstanceSubtitle");
            return;
        }

        StatusHeadline = ShowModManageButton
            ? _localizationService.Get("Launch.Status.ReadySmapiTitle")
            : _localizationService.Get("Launch.Status.ReadyVanillaTitle");
        StatusSubline = string.IsNullOrWhiteSpace(ActionStatus)
            ? _localizationService.Get("Launch.Status.ReadySubtitle")
            : ActionStatus;
    }

    private void ApplyLocalizedTexts()
    {
        InstanceInfoLabel = Text("Launch.InstanceInfo");
        LaunchModeLabel = Text("Launch.Mode");
        SafeLaunchLabel = Text("Launch.SafeLaunch");
        StatusLabel = Text("Launch.Status");
        RefreshButtonText = Text("Launch.Refresh");
        VersionSelectButtonText = Text("Launch.VersionSelect");
        ModManageButtonText = Text("Launch.ModManage");
        VersionSettingsButtonText = Text("Launch.VersionSettings");
        WelcomeTitle = Text("Launch.Welcome");
        GetStartedTitle = Text("Launch.GetStarted");
        BrandText = Text("Launch.Brand");
        GuideNoInstanceLead = Text("Launch.Guide.NoInstanceLead");
        GuideStep1 = Text("Launch.Guide.Step1");
        GuideStep2 = Text("Launch.Guide.Step2");
        GuideStep3 = Text("Launch.Guide.Step3");
        GuideStep4 = Text("Launch.Guide.Step4");
        GuideUsageTitle = Text("Launch.Guide.UsageTitle");
        GuideUsageLine1 = Text("Launch.Guide.UsageLine1");
        GuideUsageLine2 = Text("Launch.Guide.UsageLine2");
        GuideUsageModManageLine = Text("Launch.Guide.UsageModManage");
        LaunchButtonText = IsLaunching ? Text("Launch.Button.Launching") : Text("Launch.Button.Launch");
        PreferredLaunchMode = GetLaunchModeDisplayText(_preferredLaunchModeToken);
        SafeLaunchState = GetSafeLaunchStateText(EnableSafeLaunch);
        RefreshStatusBanner();
    }

    [RelayCommand]
    private void RefreshLocalGamePath()
    {
        RefreshFromSettingsAndEnvironment(true);
    }

    public void RefreshFromSettingsAndEnvironment(bool updateStatus = false)
    {
        RefreshLaunchPreferencesFromSettings();
        RefreshInstanceFromLocalEnvironment();
        if (updateStatus)
        {
            ActionStatus = Format("Launch.Action.Refreshed", DateTime.Now.ToString("HH:mm:ss"));
        }
    }

    public void RefreshLaunchPreferencesFromSettings()
    {
        var settings = _settingsStore.Load();
        _preferredLaunchModeToken = NormalizeLaunchModeToken(settings.PreferredLaunchMode);
        PreferredLaunchMode = GetLaunchModeDisplayText(_preferredLaunchModeToken);
        EnableSafeLaunch = settings.EnableSafeLaunch;
        SafeLaunchState = GetSafeLaunchStateText(EnableSafeLaunch);
    }

    private void RefreshInstanceFromLocalEnvironment()
    {
        var settings = _settingsStore.Load();
        var preferredPath = settings.PreferredInstancePath;
        if (!string.IsNullOrWhiteSpace(preferredPath) && Directory.Exists(preferredPath))
        {
            HasInstances = true;
            _currentGamePath = preferredPath;
            InstanceName = string.IsNullOrWhiteSpace(settings.InstanceName)
                ? Path.GetFileName(preferredPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                : settings.InstanceName;
            GameVersion = Text("Launch.Instance.SelectedLoaded");

            var preferredSmapiWin = Path.Combine(preferredPath, "StardewModdingAPI.exe");
            var preferredSmapiLinux = Path.Combine(preferredPath, "StardewModdingAPI");
            var preferredHasSmapi = File.Exists(preferredSmapiWin) || File.Exists(preferredSmapiLinux);
            var selectedModeToken = NormalizeLaunchModeToken(settings.PreferredLaunchMode);
            var selectedIsSmapi = string.Equals(selectedModeToken, "smapi", StringComparison.OrdinalIgnoreCase);
            var gameVersion = DetectGameVersion(preferredPath);
            var smapiVersion = preferredHasSmapi ? DetectSmapiVersion(preferredPath) : "未安装";

            ShowModManageButton = preferredHasSmapi;
            VersionStatus = BuildVersionStatusText(selectedIsSmapi, gameVersion, smapiVersion);
            SetInstanceIconSource(ResolveInstanceIconSource(preferredPath, selectedIsSmapi));
            ActionStatus = Format("Launch.Action.LoadedInstance", InstanceName);
            return;
        }

        var steamPath = _gameInstallPathLocator.TryLocateSteamStardewPath();
        var gogPath = _gameInstallPathLocator.TryLocateGogStardewPath();
        var gamePath = steamPath ?? gogPath;

        if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
        {
            HasInstances = false;
            _currentGamePath = string.Empty;
            InstanceName = Text("Launch.Instance.NoneName");
            GameVersion = Text("Launch.Instance.NoneVersion");
            VersionStatus = Text("Launch.Instance.NoneStatus");
            ShowModManageButton = false;
            SetInstanceIconSource(ResolveNoneInstanceIcon());
            ActionStatus = Text("Launch.Action.NoPathDetected");
            return;
        }

        HasInstances = true;
        _currentGamePath = gamePath;
        InstanceName = Path.GetFileName(gamePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        GameVersion = Text("Launch.Instance.DetectedLocal");

        var smapiWin = Path.Combine(gamePath, "StardewModdingAPI.exe");
        var smapiLinux = Path.Combine(gamePath, "StardewModdingAPI");
        var hasSmapi = File.Exists(smapiWin) || File.Exists(smapiLinux);
        var detectedGameVersion = DetectGameVersion(gamePath);
        var detectedSmapiVersion = hasSmapi ? DetectSmapiVersion(gamePath) : "未安装";

        ShowModManageButton = hasSmapi;
        VersionStatus = BuildVersionStatusText(hasSmapi, detectedGameVersion, detectedSmapiVersion);
        SetInstanceIconSource(ResolveInstanceIconSource(gamePath, hasSmapi));
        ActionStatus = Format("Launch.Action.DetectedPath", gamePath);
    }

    private void SetInstanceIconSource(string source)
    {
        var normalized = NormalizeImageSource(source);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        if (File.Exists(normalized))
        {
            InstanceIconSource = $"{normalized}?v={DateTime.UtcNow.Ticks}";
            return;
        }

        if (string.Equals(InstanceIconSource, normalized, StringComparison.OrdinalIgnoreCase))
        {
            InstanceIconSource = string.Empty;
        }

        InstanceIconSource = normalized;
    }

    private static string NormalizeImageSource(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return string.Empty;
        }

        var index = source.IndexOfAny(['?', '#']);
        return index < 0 ? source : source[..index];
    }

    private static string BuildVersionStatusText(bool isSmapiMode, string gameVersion, string smapiVersion)
    {
        if (isSmapiMode)
        {
            return string.IsNullOrWhiteSpace(smapiVersion)
                ? "SMAPI 未安装"
                : $"SMAPI {smapiVersion}";
        }

        var resolvedGameVersion = string.IsNullOrWhiteSpace(gameVersion) ? "未知版本" : gameVersion;
        return $"原版 {resolvedGameVersion}";
    }

    private static string DetectGameVersion(string? gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
        {
            return "未知版本";
        }

        var depsPath = Path.Combine(gamePath, "Stardew Valley.deps.json");
        if (File.Exists(depsPath))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(depsPath));
                if (doc.RootElement.TryGetProperty("targets", out var targetsElement))
                {
                    foreach (var target in targetsElement.EnumerateObject())
                    {
                        foreach (var package in target.Value.EnumerateObject())
                        {
                            if (!package.Name.StartsWith("Stardew Valley/", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            var parts = package.Name.Split('/');
                            if (parts.Length == 2)
                            {
                                return parts[1];
                            }
                        }
                    }
                }
            }
            catch
            {
                // Fallback to file metadata below.
            }
        }

        var dllPath = Path.Combine(gamePath, "Stardew Valley.dll");
        if (!File.Exists(dllPath))
        {
            return "未知版本";
        }

        try
        {
            var fileVersion = System.Diagnostics.FileVersionInfo.GetVersionInfo(dllPath).FileVersion;
            return string.IsNullOrWhiteSpace(fileVersion) ? "未知版本" : fileVersion;
        }
        catch
        {
            return "未知版本";
        }
    }

    private static string DetectSmapiVersion(string? gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
        {
            return "未安装";
        }

        var markers = new[]
        {
            Path.Combine(gamePath, "StardewModdingAPI.exe"),
            Path.Combine(gamePath, "StardewModdingAPI"),
            Path.Combine(gamePath, "StardewModdingAPI.dll")
        };

        var markerPath = markers.FirstOrDefault(File.Exists);
        if (string.IsNullOrWhiteSpace(markerPath))
        {
            return "未安装";
        }

        try
        {
            var version = System.Diagnostics.FileVersionInfo.GetVersionInfo(markerPath).FileVersion;
            return string.IsNullOrWhiteSpace(version) ? "Unknown" : version;
        }
        catch
        {
            return "Unknown";
        }
    }

    private string ResolveInstanceIconSource(string instancePath, bool isSmapiInstance)
    {
        // 自定义图标优先级高于系统预设图标
        var customIcon = InstanceIconResolver.ResolveIconPath(instancePath, isSmapiInstance);
        if (!string.IsNullOrWhiteSpace(customIcon))
        {
            return customIcon;
        }

        // 异常检测：路径无效或游戏文件缺失时使用异常图标
        var isAnomaly = IsInstanceAnomaly(instancePath);
        return ResolveDefaultInstanceIcon(isSmapiInstance, isAnomaly);
    }

    /// <summary>检测实例是否处于异常状态（路径无效或关键游戏文件缺失）。</summary>
    private static bool IsInstanceAnomaly(string? instancePath)
    {
        if (string.IsNullOrWhiteSpace(instancePath) || !Directory.Exists(instancePath))
        {
            return true;
        }

        // 关键游戏文件均不存在则视为异常
        return !File.Exists(Path.Combine(instancePath, "Stardew Valley.dll")) &&
               !File.Exists(Path.Combine(instancePath, "Stardew Valley.exe")) &&
               !File.Exists(Path.Combine(instancePath, "Stardew Valley.deps.json"));
    }

    private string ResolveNoneInstanceIcon()
    {
        var resolved = _imageResourceService.Get("launch.instance.none");
        return string.IsNullOrWhiteSpace(resolved)
            ? "avares://SVL.Avalonia/Assets/Icons/Junimo.png"
            : resolved;
    }

    /// <summary>
    /// 解析默认预设图标。【临时占位】后续有新的预设条件可随时更换。
    /// 优先级：自定义图标 > 系统预设图标（SMAPI=Modded.png / 原版=Vanilla.png / 异常=Junimo2.png）
    /// </summary>
    private string ResolveDefaultInstanceIcon(bool isSmapiInstance, bool isAnomaly)
    {
        // 异常状态直接使用异常图标（不经过 imageResourceService 覆盖，确保异常可见性）
        if (isAnomaly)
        {
            var anomalyResolved = _imageResourceService.Get("launch.instance.anomaly");
            return string.IsNullOrWhiteSpace(anomalyResolved)
                ? InstanceIconResolver.ResolveDefaultPresetIcon(isSmapiInstance, isAnomaly)
                : anomalyResolved;
        }

        var key = isSmapiInstance ? "launch.instance.modded" : "launch.instance.vanilla";
        var fallback = InstanceIconResolver.ResolveDefaultPresetIcon(isSmapiInstance, isAnomaly);
        var resolved = _imageResourceService.Get(key);
        return string.IsNullOrWhiteSpace(resolved) ? fallback : resolved;
    }

    [RelayCommand]
    private void LaunchGame()
    {
        RefreshLaunchPreferencesFromSettings();
        var settings = _settingsStore.Load();

        if (!HasInstances)
        {
            ActionStatus = Text("Launch.Action.RequireInstance");
            return;
        }

        if (string.IsNullOrWhiteSpace(_currentGamePath) || !Directory.Exists(_currentGamePath))
        {
            ActionStatus = Text("Launch.Action.InvalidInstancePath");
            return;
        }

        var launchTarget = ResolveLaunchTarget(_currentGamePath, _preferredLaunchModeToken);
        if (string.IsNullOrWhiteSpace(launchTarget) || (!File.Exists(launchTarget) && !Directory.Exists(launchTarget)))
        {
            ActionStatus = Format("Launch.Action.TargetNotFound", PreferredLaunchMode);
            return;
        }

        IsLaunching = true;
        LaunchButtonText = Text("Launch.Button.Launching");

        var launchArguments = BuildLaunchArguments(settings);
        var hasArguments = !string.IsNullOrWhiteSpace(launchArguments);

        var launched = hasArguments && !Directory.Exists(launchTarget)
            ? _externalProcessService.TryLaunchProcess(
                launchTarget,
                launchArguments,
                Path.GetDirectoryName(launchTarget))
            : _externalProcessService.TryOpenPath(launchTarget);

        if (launched)
        {
            var safeText = EnableSafeLaunch ? Text("Launch.Action.SafeTag") : string.Empty;
            var argsText = hasArguments ? Format("Launch.Action.ArgsTag", launchArguments) : string.Empty;
            ActionStatus = Format(
                "Launch.Action.Started",
                DateTime.Now.ToString("HH:mm:ss"),
                Path.GetFileName(launchTarget),
                PreferredLaunchMode,
                safeText,
                argsText);
        }
        else
        {
            ActionStatus = Text("Launch.Action.LaunchFailed");
        }

        IsLaunching = false;
        LaunchButtonText = Text("Launch.Button.Launch");
    }

    [RelayCommand]
    private void NavigateToVersionSelect()
    {
        ActionStatus = Text("Launch.Action.NavigateVersionSelect");
        NavigateToInstancesRequested?.Invoke();
    }

    [RelayCommand]
    private void OpenModManage()
    {
        if (!HasInstances || string.IsNullOrWhiteSpace(_currentGamePath))
        {
            ActionStatus = Text("Launch.Action.RequireValidInstance");
            return;
        }

        ActionStatus = Text("Launch.Action.OpenModManage");
        NavigateToModManageRequested?.Invoke();
    }

    [RelayCommand]
    private void OpenVersionSettings()
    {
        if (!HasInstances)
        {
            ActionStatus = Text("Launch.Action.RequireInstance");
            return;
        }

        ActionStatus = Text("Launch.Action.OpenVersionSettings");
        NavigateToVersionSettingsRequested?.Invoke();
    }

    private static string ResolveLaunchTarget(string gamePath, string launchModeToken)
    {
        var smapiCandidates = new[]
        {
            Path.Combine(gamePath, "StardewModdingAPI.exe"),
            Path.Combine(gamePath, "StardewModdingAPI")
        };

        var gameCandidates = new[]
        {
            Path.Combine(gamePath, "Stardew Valley.exe"),
            Path.Combine(gamePath, "StardewValley.exe"),
            Path.Combine(gamePath, "StardewValley"),
            Path.Combine(gamePath, "Stardew Valley.app")
        };

        if (string.Equals(launchModeToken, "smapi", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var candidate in smapiCandidates)
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return string.Empty;
        }

        if (string.Equals(launchModeToken, "vanilla", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var candidate in gameCandidates)
            {
                if (File.Exists(candidate) || Directory.Exists(candidate))
                {
                    return candidate;
                }
            }

            return string.Empty;
        }

        foreach (var candidate in smapiCandidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        foreach (var candidate in gameCandidates)
        {
            if (File.Exists(candidate) || Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    private string Text(string key) => _localizationService.Get(key);

    private string Format(string key, params object[] args) => string.Format(Text(key), args);

    private string GetSafeLaunchStateText(bool enabled) => enabled
        ? Text("Launch.SafeLaunch.Enabled")
        : Text("Launch.SafeLaunch.Disabled");

    private string GetLaunchModeDisplayText(string modeToken)
    {
        return modeToken switch
        {
            "smapi" => Text("Launch.ModeValue.Smapi"),
            "vanilla" => Text("Launch.ModeValue.Vanilla"),
            _ => Text("Launch.ModeValue.Auto")
        };
    }

    private static string NormalizeLaunchModeToken(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return "auto";
        }

        var normalized = mode.Trim();

        if (string.Equals(normalized, "SMAPI", StringComparison.OrdinalIgnoreCase))
        {
            return "smapi";
        }

        if (string.Equals(normalized, "原版", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "vanilla", StringComparison.OrdinalIgnoreCase))
        {
            return "vanilla";
        }

        if (string.Equals(normalized, "自动", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return "auto";
        }

        return "auto";
    }

    private string BuildLaunchArguments(Models.AppUserSettings settings)
    {
        var args = new List<string>();

        if (EnableSafeLaunch)
        {
            args.Add("--safe");
        }

        if (!string.IsNullOrWhiteSpace(settings.InstanceCustomLaunchArguments))
        {
            args.Add(settings.InstanceCustomLaunchArguments.Trim());
        }

        return string.Join(" ", args);
    }
}
