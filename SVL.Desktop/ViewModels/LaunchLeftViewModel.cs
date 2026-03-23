using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SVL.Core.Config;
using SVL.Core.Stardew.Instance;
using SVL.Desktop.Controls;

namespace SVL.Desktop.ViewModels;

public partial class LaunchLeftViewModel : ObservableObject
{
    private MainWindowViewModel _mainViewModel;

    public LaunchLeftViewModel(MainWindowViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;
        GlobalEvents.InstanceChanged += OnGlobalInstanceChanged;
        LoadSelectedInstance();
    }

    [ObservableProperty]
    private GamePathInfo? _selectedGamePath;

    partial void OnSelectedGamePathChanged(GamePathInfo? value)
    {
        // 取消之前的订阅
        if (_selectedGamePath != null)
        {
            _selectedGamePath.PropertyChanged -= OnSelectedGamePathPropertyChanged;
        }

        if (value != null)
        {
            // 订阅属性变化事件，以便在自定义图标改变时更新显示
            value.PropertyChanged += OnSelectedGamePathPropertyChanged;
            InstanceName = value.Name;
            GameVersion = string.IsNullOrEmpty(value.Version) ? "未知版本" : value.Version;
            HasSMAPI = value.IsSMAPIInstance;
            SMAPIVersion = value.SMAPIVersion;
            IsVersionDetected = !string.IsNullOrEmpty(value.Version);

            // 更新图标路径
            UpdateIconSource();
            OnPropertyChanged(nameof(ShowModManageButton));
        }
        else
        {
            InstanceName = "暂无实例";
            GameVersion = "请先添加游戏实例";
            HasSMAPI = false;
            SMAPIVersion = null;
            IsVersionDetected = false;
            IconSource = "/Images/Junimo2.png";
            VersionStatus = "未找到版本";
            OnPropertyChanged(nameof(ShowModManageButton));
        }
    }

    public void LoadSelectedInstance()
    {
        // 从配置加载已保存的实例
        var savedInstances = SettingsService.LoadInstances();
        var defaultInstanceId = SettingsService.LoadDefaultInstanceId();

        if (savedInstances.Count > 0)
        {
            // 优先使用默认实例，如果没有则使用第一个
            var defaultInstance = savedInstances.FirstOrDefault(i => i.Id == defaultInstanceId);
            SelectedGamePath = defaultInstance ?? savedInstances[0];
        }
        else
        {
            SelectedGamePath = null;
        }
    }

    private void UpdateIconSource()
    {
        if (!IsVersionDetected)
        {
            IconSource = "/Images/Junimo2.png";
            VersionStatus = "未找到版本";
        }
        else if (SelectedGamePath != null && !string.IsNullOrEmpty(SelectedGamePath.CustomIcon))
        {
            // 优先使用自定义图标
            IconSource = SelectedGamePath.CustomIcon;
            VersionStatus = HasSMAPI ? $"模组版 {SMAPIVersion}" : "原版";
        }
        else if (HasSMAPI)
        {
            IconSource = "/Images/Modded.png";
            VersionStatus = $"模组版 {SMAPIVersion}";
        }
        else
        {
            IconSource = "/Images/Vanilla.png";
            VersionStatus = "原版";
        }
    }

    /// <summary>
    /// 监听选中的游戏实例的属性变化
    /// </summary>
    private void OnSelectedGamePathPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // 当核心显示属性改变时，刷新头部信息和图标
        if (e.PropertyName == nameof(GamePathInfo.CustomIcon) ||
            e.PropertyName == nameof(GamePathInfo.Name) ||
            e.PropertyName == nameof(GamePathInfo.Version) ||
            e.PropertyName == nameof(GamePathInfo.IsSMAPIInstance) ||
            e.PropertyName == nameof(GamePathInfo.SMAPIVersion))
        {
            if (SelectedGamePath != null)
            {
                InstanceName = SelectedGamePath.Name;
                GameVersion = string.IsNullOrEmpty(SelectedGamePath.Version) ? "未知版本" : SelectedGamePath.Version;
                HasSMAPI = SelectedGamePath.IsSMAPIInstance;
                SMAPIVersion = SelectedGamePath.SMAPIVersion;
                IsVersionDetected = !string.IsNullOrEmpty(SelectedGamePath.Version);
            }
            UpdateIconSource();
            OnPropertyChanged(nameof(ShowModManageButton));
        }
    }

    private void OnGlobalInstanceChanged(object? sender, InstanceChangedEventArgs e)
    {
        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            var savedInstances = SettingsService.LoadInstances();
            if (savedInstances.Count == 0)
            {
                SelectedGamePath = null;
                return;
            }

            GamePathInfo? target = null;

            if (!string.IsNullOrWhiteSpace(e.InstanceId))
            {
                target = savedInstances.FirstOrDefault(i => i.Id == e.InstanceId);
            }

            if (target == null && SelectedGamePath != null)
            {
                target = savedInstances.FirstOrDefault(i => i.Id == SelectedGamePath.Id);
            }

            if (target == null)
            {
                var defaultId = SettingsService.LoadDefaultInstanceId();
                target = savedInstances.FirstOrDefault(i => i.Id == defaultId) ?? savedInstances[0];
            }

            SelectedGamePath = target;
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    [ObservableProperty]
    private string _instanceName = "未找到可用的游戏";

    [ObservableProperty]
    private string _gameVersion = "-";

    [ObservableProperty]
    private string _iconSource = "/Images/Junimo2.png";

    [ObservableProperty]
    private bool _hasSMAPI;

    [ObservableProperty]
    private string? _sMAPIVersion;

    [ObservableProperty]
    private bool _isVersionDetected;

    [ObservableProperty]
    private string _versionStatus = "未知";

    [ObservableProperty]
    private bool _isLaunching;

    [ObservableProperty]
    private string _launchButtonText = "启动游戏";

    public bool ShowModManageButton => SelectedGamePath?.IsSMAPIInstance == true;

    [RelayCommand]
    private async void LaunchGame()
    {
        if (SelectedGamePath == null)
        {
            SvlMessageBox.Info("请先选择一个游戏实例", "提示");
            return;
        }

        try
        {
            // 设置启动状态
            IsLaunching = true;
            LaunchButtonText = "启动中...";

            // 获取主窗口引用
            var mainWindow = Application.Current.MainWindow;

            System.Diagnostics.Debug.WriteLine($"[Launch] Starting game: {SelectedGamePath.Name}");
            System.Diagnostics.Debug.WriteLine($"[Launch] IsSMAPI: {SelectedGamePath.IsSMAPIInstance}");
            System.Diagnostics.Debug.WriteLine($"[Launch] EnableIsolation: {SelectedGamePath.EnableIsolation}");
            System.Diagnostics.Debug.WriteLine($"[Launch] CustomArguments: {SelectedGamePath.CustomArguments}");

            // 版本隔离处理
            string? instanceFolderName = null;
            string launchPath;
            string workingDirectory;

            if (SelectedGamePath.EnableIsolation && SelectedGamePath.IsSMAPIInstance)
            {
                instanceFolderName = InstanceIsolationService.GenerateVersionFolderName(
                    SelectedGamePath.Name,
                    SelectedGamePath.IsSMAPIInstance);

                System.Diagnostics.Debug.WriteLine($"[Launch] Setting up isolation for instance: {instanceFolderName}");

                // 初始化隔离目录
                if (!InstanceIsolationService.InitializeIsolationDirectories(
                    SelectedGamePath.GamePath,
                    instanceFolderName,
                    SelectedGamePath.IsSMAPIInstance))
                {
                    SvlMessageBox.Warning("设置版本隔离环境失败，游戏将以非隔离模式启动");
                    // 失败时回退到非隔离模式
                    instanceFolderName = null;
                }
            }

            Process? gameProcess = null;

            if (SelectedGamePath.EnableIsolation && SelectedGamePath.IsSMAPIInstance && instanceFolderName != null)
            {
                workingDirectory = InstanceIsolationService.GetWorkingDirectory(
                    SelectedGamePath.GamePath,
                    instanceFolderName,
                    SelectedGamePath.IsSMAPIInstance);

                // 使用智能路径解析：兼容原版/SMAPI 启动文件被改名或缺失的情况
                launchPath = ResolveLaunchPath(
                    workingDirectory,
                    SelectedGamePath.IsSMAPIInstance,
                    out var launchHint);
                if (!string.IsNullOrWhiteSpace(launchHint))
                {
                    SvlMessageBox.Info(launchHint, "启动兼容提示");
                }

                System.Diagnostics.Debug.WriteLine($"[Launch] Isolated launch path: {launchPath}");
                System.Diagnostics.Debug.WriteLine($"[Launch] Isolated working directory: {workingDirectory}");
            }
            else
            {
                // 非隔离模式：同样使用智能路径解析
                workingDirectory = SelectedGamePath.GamePath;
                launchPath = ResolveLaunchPath(
                    SelectedGamePath.GamePath,
                    SelectedGamePath.IsSMAPIInstance,
                    out var launchHint);
                if (!string.IsNullOrWhiteSpace(launchHint))
                {
                    SvlMessageBox.Info(launchHint, "启动兼容提示");
                }
            }

            // 检查启动文件是否存在
            if (!File.Exists(launchPath))
            {
                SvlMessageBox.Error($"找不到启动文件：{launchPath}");
                IsLaunching = false;
                LaunchButtonText = "启动游戏";
                return;
            }

            // 构建启动参数
            var arguments = new System.Text.StringBuilder();
            if (!string.IsNullOrEmpty(SelectedGamePath.CustomArguments))
            {
                arguments.Append($"{SelectedGamePath.CustomArguments} ");
            }

            // 如果是 SMAPI 隔离模式，添加 --game-path 参数
            // 指向游戏根目录，SMAPI 会从那里读取游戏文件
            if (SelectedGamePath.EnableIsolation && instanceFolderName != null && SelectedGamePath.IsSMAPIInstance)
            {
                arguments.Append($"--game-path \"{SelectedGamePath.GamePath}\" ");
                System.Diagnostics.Debug.WriteLine($"[Launch] SMAPI isolation mode, game-path: {SelectedGamePath.GamePath}");
            }

            // 启动游戏进程
            var processInfo = new ProcessStartInfo
            {
                FileName = launchPath,
                WorkingDirectory = workingDirectory,
                Arguments = arguments.ToString().Trim(),
                UseShellExecute = true
            };

            gameProcess = Process.Start(processInfo);

            if (gameProcess != null)
            {
                // 确定游戏窗口标题（优先级：版本设置 > 全局设置 > 默认值）
                string finalTitle = GetGameWindowTitle();

                // 如果设置了自定义窗口标题，等待窗口出现后再隐藏启动器
                if (!string.IsNullOrEmpty(finalTitle) && finalTitle != "<default>")
                {
                    System.Diagnostics.Debug.WriteLine($"[Launch] Starting window title setter task...");
                    System.Diagnostics.Debug.WriteLine($"[Launch] Title template: {finalTitle}");
                    System.Diagnostics.Debug.WriteLine($"[Launch] Game path: {SelectedGamePath.GamePath}");

                    // 等待窗口出现并设置标题（此方法会等待窗口出现）
                    await SVL.Core.Stardew.Launch.WindowTitleSetter.SetWindowTitleAsync(
                        gameProcess,
                        finalTitle,
                        SelectedGamePath.Name,
                        SelectedGamePath.IsSMAPIInstance,
                        SelectedGamePath.GamePath);
                }
                else
                {
                    // 没有设置标题，等待游戏窗口出现
                    await SVL.Core.Stardew.Launch.WindowTitleSetter.WaitForGameWindowAsync(
                        gameProcess,
                        SelectedGamePath.GamePath);
                }

                // 游戏窗口已出现，现在隐藏启动器
                if (mainWindow != null)
                {
                    mainWindow.Hide();
                }

                // 等待游戏进程退出
                await Task.Run(() => gameProcess.WaitForExit());
            }

            // 游戏结束后显示主窗口
            if (mainWindow != null)
            {
                mainWindow.Show();
                mainWindow.Activate(); // 激活窗口到前台
            }
        }
        catch (Exception ex)
        {
            // 如果出错，确保主窗口被显示
            var mainWindow = Application.Current.MainWindow;
            if (mainWindow != null && !mainWindow.IsVisible)
            {
                mainWindow.Show();
            }

            SvlMessageBox.Error($"启动游戏失败：{ex.Message}");
        }
        finally
        {
            // 恢复按钮状态
            IsLaunching = false;
            LaunchButtonText = "启动游戏";
        }
    }

    private static string ResolveLaunchPath(string launchDirectory, bool preferSmapi, out string? hint)
    {
        hint = null;

        var vanillaExe = Path.Combine(launchDirectory, "Stardew Valley.exe");
        var smapiExe = Path.Combine(launchDirectory, "StardewModdingAPI.exe");
        var hasVanillaExe = File.Exists(vanillaExe);
        var hasSmapiExe = File.Exists(smapiExe);
        var hasSmapiInstalled = GamePathService.CheckSMAPI(launchDirectory, out _);

        if (preferSmapi)
        {
            if (hasSmapiExe)
            {
                return smapiExe;
            }

            // 兼容：用户把 StardewModdingAPI.exe 改名成 Stardew Valley.exe
            if (hasVanillaExe && hasSmapiInstalled)
            {
                hint = "未找到 StardewModdingAPI.exe，已使用兼容方式启动（检测到该目录已安装 SMAPI）。";
                return vanillaExe;
            }

            return smapiExe;
        }

        if (hasVanillaExe)
        {
            if (!hasSmapiExe && hasSmapiInstalled)
            {
                hint = "未检测到原版独立启动程序，当前“原版启动”可能实际运行 SMAPI。";
            }

            return vanillaExe;
        }

        // 原版启动兜底：若原版 EXE 缺失但 SMAPI 启动程序存在，则自动回退可执行文件。
        if (hasSmapiExe && hasSmapiInstalled)
        {
            hint = "未找到 Stardew Valley.exe，已自动切换为 SMAPI 启动。";
            return smapiExe;
        }

        return vanillaExe;
    }

    [RelayCommand]
    private void NavigateToVersionSelect()
    {
        // 进入“版本选择”前，传递当前选中的实例用于默认路径定位。
        if (SelectedGamePath != null)
        {
            _mainViewModel.SelectedVersionSettingsInstance = SelectedGamePath;
        }

        _mainViewModel.NavigateToInstancesCommand.Execute(null);
    }

    [RelayCommand]
    private void OpenVersionSettings()
    {
        if (SelectedGamePath == null)
        {
            SvlMessageBox.Info("请先选择一个游戏实例", "提示");
            return;
        }

        // 将当前选中的实例传递给 MainWindowViewModel
        _mainViewModel.SelectedVersionSettingsInstance = SelectedGamePath;

        // 导航到版本设置页面
        _mainViewModel.NavigateToVersionSettingsCommand.Execute(null);
    }

    [RelayCommand]
    private void OpenModManage()
    {
        if (SelectedGamePath == null)
        {
            SvlMessageBox.Info("请先选择一个游戏实例", "提示");
            return;
        }

        if (!SelectedGamePath.IsSMAPIInstance)
        {
            return;
        }

        _mainViewModel.SelectedVersionSettingsInstance = SelectedGamePath;
        _mainViewModel.OpenVersionSettingsAtModManage = true;
        _mainViewModel.NavigateToVersionSettingsCommand.Execute(null);
    }

    // 当从版本选择页面返回时重新加载
    public void ReloadInstance()
    {
        LoadSelectedInstance();
    }

    /// <summary>
    /// 获取游戏窗口标题（优先级：版本设置 > 全局设置 > 默认值）
    /// </summary>
    private string GetGameWindowTitle()
    {
        // 优先级 1: 版本设置（如果设置了且不等于 "<default>"）
        if (!string.IsNullOrEmpty(SelectedGamePath?.WindowTitle) &&
            SelectedGamePath.WindowTitle != "<default>")
        {
            return SelectedGamePath.WindowTitle;
        }

        // 优先级 2: 全局设置（如果设置了且不等于 "<default>"）
        var globalSettings = AppConfig.GetSettings();
        if (!string.IsNullOrEmpty(globalSettings.GameWindowTitle) &&
            globalSettings.GameWindowTitle != "<default>")
        {
            return globalSettings.GameWindowTitle;
        }

        // 优先级 3: 默认值
        return "Stardew Valley";
    }
}
