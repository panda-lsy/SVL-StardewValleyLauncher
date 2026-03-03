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
        // 当自定义图标改变时，更新图标显示
        if (e.PropertyName == nameof(GamePathInfo.CustomIcon))
        {
            UpdateIconSource();
        }
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

            if (SelectedGamePath.EnableIsolation)
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

            if (SelectedGamePath.EnableIsolation && instanceFolderName != null)
            {
                // 使用隔离路径启动
                launchPath = InstanceIsolationService.GetLaunchPath(
                    SelectedGamePath.GamePath,
                    instanceFolderName,
                    SelectedGamePath.IsSMAPIInstance);

                workingDirectory = InstanceIsolationService.GetWorkingDirectory(
                    SelectedGamePath.GamePath,
                    instanceFolderName,
                    SelectedGamePath.IsSMAPIInstance);

                System.Diagnostics.Debug.WriteLine($"[Launch] Isolated launch path: {launchPath}");
                System.Diagnostics.Debug.WriteLine($"[Launch] Isolated working directory: {workingDirectory}");
            }
            else if (SelectedGamePath.IsSMAPIInstance)
            {
                // 非隔离 SMAPI 模式
                launchPath = Path.Combine(SelectedGamePath.GamePath, "StardewModdingAPI.exe");
                workingDirectory = SelectedGamePath.GamePath;
            }
            else
            {
                // 原版游戏
                launchPath = Path.Combine(SelectedGamePath.GamePath, "Stardew Valley.exe");
                workingDirectory = SelectedGamePath.GamePath;
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

    [RelayCommand]
    private void NavigateToVersionSelect()
    {
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
