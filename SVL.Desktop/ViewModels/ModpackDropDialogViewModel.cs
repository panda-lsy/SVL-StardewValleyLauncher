using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SVL.Core.Config;
using SVL.Core.Logging;
using SVL.Core.Modpack;
using SVL.Core.Stardew.Instance;
using SVL.Desktop.Controls;

namespace SVL.Desktop.ViewModels;

public partial class ModpackDropDialogViewModel : ObservableObject
{
    [ObservableProperty]
    private string _modpackFilePath = string.Empty;

    [ObservableProperty]
    private string _modpackName = "检测中...";

    [ObservableProperty]
    private string _modpackVersion = "-";

    [ObservableProperty]
    private string _modpackAuthor = "-";

    [ObservableProperty]
    private string _modpackDescription = "-";

    [ObservableProperty]
    private int _modCount = 0;

    [ObservableProperty]
    private string _instanceName = string.Empty;

    [ObservableProperty]
    private ObservableCollection<GamePathInfo> _gamePaths = new();

    [ObservableProperty]
    private GamePathInfo? _selectedGamePath;

    [ObservableProperty]
    private bool _isValid = false;

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private string _loadingMessage = "正在检测整合包类型...";

    [ObservableProperty]
    private string _modpackTypeText = "-";

    private ModpackDetectionResult? _detectionResult;

    public ModpackDetectionResult? DetectionResult => _detectionResult;

    // 用于存储父窗口引用（弹出子对话框时使用）
    public Window? OwnerWindow { get; set; }

    public ModpackDropDialogViewModel()
    {
        LoadGamePaths();
    }

    /// <summary>
    /// 从拖放的文件加载整合包信息
    /// </summary>
    public async void LoadFromFileAsync(string filePath)
    {
        ModpackFilePath = filePath;
        IsLoading = true;
        LoadingMessage = "正在检测整合包类型...";

        try
        {
            await System.Threading.Tasks.Task.Run(() =>
            {
                _detectionResult = ModpackTypeDetector.Detect(filePath);
            });

            if (_detectionResult == null || _detectionResult.Type == ModpackType.Unknown)
            {
                ModpackName = "无法识别的文件";
                ModpackDescription = _detectionResult?.ErrorMessage ?? "未知的整合包格式";
                ModpackTypeText = "未知";
                IsLoading = false;
                IsValid = false;
                return;
            }

            // 根据类型更新 UI
            if (_detectionResult.Type == ModpackType.Curseforge)
            {
                ModpackTypeText = "Curseforge 整合包";
                if (_detectionResult.CurseforgeManifest != null)
                {
                    ModpackName = _detectionResult.CurseforgeManifest.Name;
                    ModpackVersion = _detectionResult.CurseforgeManifest.Version;
                    ModpackAuthor = _detectionResult.CurseforgeManifest.Author;
                    ModpackDescription = !string.IsNullOrEmpty(_detectionResult.CurseforgeManifest.Description)
                        ? _detectionResult.CurseforgeManifest.Description
                        : "无描述";
                    ModCount = _detectionResult.CurseforgeManifest.Files.Count;
                    InstanceName = GenerateDefaultInstanceName(_detectionResult.CurseforgeManifest.Name);
                }
                else
                {
                    ModpackName = Path.GetFileNameWithoutExtension(filePath);
                    InstanceName = GenerateDefaultInstanceName(ModpackName);
                }
            }
            else if (_detectionResult.Type == ModpackType.NexusCollection)
            {
                ModpackTypeText = "Nexus Collection";
                // 优先使用从 collection.json 解析出的信息
                ModpackName = !string.IsNullOrEmpty(_detectionResult.ModpackName)
                    ? _detectionResult.ModpackName
                    : Path.GetFileNameWithoutExtension(filePath);
                ModpackAuthor = !string.IsNullOrEmpty(_detectionResult.ModpackAuthor)
                    ? _detectionResult.ModpackAuthor
                    : "-";
                ModpackDescription = !string.IsNullOrEmpty(_detectionResult.ModpackDescription)
                    ? _detectionResult.ModpackDescription
                    : "Nexus Collection 整合包，安装时将从 NexusMods 下载模组文件";
                ModCount = _detectionResult.ModCount;
                if (!string.IsNullOrEmpty(_detectionResult.ModpackVersion))
                    ModpackVersion = _detectionResult.ModpackVersion;
                InstanceName = GenerateDefaultInstanceName(ModpackName);
            }
            else if (_detectionResult.Type == ModpackType.SVL)
            {
                ModpackTypeText = "SVL 整合包";
                ModpackName = !string.IsNullOrEmpty(_detectionResult.ModpackName)
                    ? _detectionResult.ModpackName
                    : Path.GetFileNameWithoutExtension(filePath);
                ModpackAuthor = !string.IsNullOrEmpty(_detectionResult.ModpackAuthor)
                    ? _detectionResult.ModpackAuthor
                    : "-";
                ModpackDescription = !string.IsNullOrEmpty(_detectionResult.ModpackDescription)
                    ? _detectionResult.ModpackDescription
                    : "SVL 整合包";
                ModCount = _detectionResult.ModCount;
                if (!string.IsNullOrEmpty(_detectionResult.ModpackVersion))
                    ModpackVersion = _detectionResult.ModpackVersion;
                InstanceName = GenerateDefaultInstanceName(ModpackName);
            }

            IsLoading = false;
            IsValid = true;

            Log.Info($"[ModpackDropDialog] 已加载整合包: {ModpackName}, 类型: {ModpackTypeText}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[ModpackDropDialog] 加载整合包失败");
            ModpackName = "加载失败";
            ModpackDescription = ex.Message;
            ModpackTypeText = "错误";
            IsLoading = false;
            IsValid = false;
        }
    }

    /// <summary>
    /// 生成默认实例名称
    /// </summary>
    private string GenerateDefaultInstanceName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return "New Instance";

        // 移除非法字符
        var invalidChars = Path.GetInvalidFileNameChars();
        var safeName = string.Join("_", name.Split(invalidChars));

        // 如果名称太长，截断
        if (safeName.Length > 30)
        {
            safeName = safeName.Substring(0, 30);
        }

        return safeName.Trim();
    }

    /// <summary>
    /// 加载游戏路径列表（只显示 Base 路径）
    /// </summary>
    private void LoadGamePaths()
    {
        var savedInstances = SettingsService.LoadInstances();
        GamePaths.Clear();

        // 只加载 Base 路径（Tags 包含 "Base" 的实例）
        foreach (var instance in savedInstances.Where(i => i.Tags != null && i.Tags.Contains("Base")))
        {
            GamePaths.Add(instance);
        }

        // 如果没有 Base 路径，加载所有实例（兼容旧数据）
        if (GamePaths.Count == 0)
        {
            foreach (var instance in savedInstances)
            {
                GamePaths.Add(instance);
            }
        }

        // 默认选择第一个路径
        if (GamePaths.Count > 0)
        {
            SelectedGamePath = GamePaths[0];
        }
    }

    /// <summary>
    /// 检查版本名称是否已存在
    /// </summary>
    private bool CheckVersionNameExists(string name)
    {
        if (SelectedGamePath == null)
            return false;

        var versionPath = InstanceIsolationService.GetVersionPath(SelectedGamePath.GamePath, name);
        return Directory.Exists(versionPath);
    }

    /// <summary>
    /// 确认导入 - 弹出版本名称输入对话框
    /// </summary>
    [RelayCommand]
    private void ConfirmImport()
    {
        if (SelectedGamePath == null)
        {
            SvlMessageBox.Info("请选择目标游戏路径", "提示");
            IsValid = false;
            return;
        }

        if (_detectionResult == null || _detectionResult.Type == ModpackType.Unknown)
        {
            SvlMessageBox.Error("无法识别的整合包类型");
            IsValid = false;
            return;
        }

        // 弹出版本名称输入对话框
        var owner = OwnerWindow ?? Application.Current.MainWindow;
        var instanceName = Controls.InstanceNameDialog.Show(
            owner,
            defaultName: InstanceName,
            checkNameExists: CheckVersionNameExists,
            autoSanitize: true);

        if (string.IsNullOrEmpty(instanceName))
        {
            // 用户取消
            IsValid = false;
            return;
        }

        // 更新实例名称
        InstanceName = instanceName;

        // 所有验证通过
        IsValid = true;

        // 通知 View 关闭对话框
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 取消导入
    /// </summary>
    [RelayCommand]
    private void Cancel()
    {
        IsValid = false;

        // 清理临时文件
        if (_detectionResult != null && !string.IsNullOrEmpty(_detectionResult.TempExtractPath))
        {
            ModpackTypeDetector.CleanupTempDirectory(_detectionResult.TempExtractPath);
        }

        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 请求关闭对话框的事件
    /// </summary>
    public event EventHandler? RequestClose;
}
