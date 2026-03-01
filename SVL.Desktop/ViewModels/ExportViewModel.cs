using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SVL.Core.Logging;
using SVL.Core.Stardew.Instance;
using SVL.Core.Stardew.Mod;
using SVL.Core.Stardew.ResourceProject.Modpack;

namespace SVL.Desktop.ViewModels;

/// <summary>
/// 导出列表中的 Mod 条目
/// </summary>
public partial class ExportModItem : ObservableObject
{
    /// <summary>
    /// 是否勾选导出
    /// </summary>
    [ObservableProperty]
    private bool _isSelected = true;

    /// <summary>
    /// Mod 名称
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Mod UniqueId
    /// </summary>
    public string UniqueId { get; set; } = string.Empty;

    /// <summary>
    /// Mod 版本
    /// </summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// Mod 作者
    /// </summary>
    public string Author { get; set; } = string.Empty;

    /// <summary>
    /// Mod 文件夹路径
    /// </summary>
    public string ModPath { get; set; } = string.Empty;

    /// <summary>
    /// 是否启用
    /// </summary>
    public bool IsEnabled { get; set; }

    /// <summary>
    /// 来源平台 (NexusMods / Curseforge / 未知)
    /// </summary>
    public string SourcePlatform { get; set; } = "未知";

    /// <summary>
    /// 来源项目ID
    /// </summary>
    public string SourceProjectId { get; set; } = string.Empty;

    /// <summary>
    /// 来源文件ID
    /// </summary>
    public string SourceFileId { get; set; } = string.Empty;

    /// <summary>
    /// 是否有来源凭证（有的话导入时可自动下载）
    /// </summary>
    public bool HasSourceCredential => !string.IsNullOrEmpty(SourcePlatform) && SourcePlatform != "未知";

    /// <summary>
    /// 来源描述文本
    /// </summary>
    public string SourceDescription => HasSourceCredential
        ? $"{SourcePlatform} #{SourceProjectId}"
        : "无来源信息（需打包文件）";

    /// <summary>
    /// 关联的 SdVMod 对象
    /// </summary>
    public SdVMod OriginalMod { get; set; }
}

/// <summary>
/// 导出配置（用于 保存/读取 配置文件）
/// </summary>
public class ExportConfig
{
    public string ModpackName { get; set; } = string.Empty;
    public string ModpackVersion { get; set; } = "1.0.0";
    public string ModpackAuthor { get; set; } = string.Empty;
    public bool IncludeMods { get; set; } = true;
    public bool IncludeModSettings { get; set; } = true;
    public bool IncludeSvlLauncher { get; set; }
    public bool BundleModFiles { get; set; } = true;

    /// <summary>
    /// 选中的 Mod UniqueId 列表（空 = 全选）
    /// </summary>
    public string[] SelectedModUniqueIds { get; set; } = Array.Empty<string>();
}

/// <summary>
/// 版本设置 - 导出页面的 ViewModel
/// </summary>
public partial class ExportViewModel : ObservableObject
{
    private readonly MainWindowViewModel _mainViewModel;
    private readonly GamePathInfo _instance;

    #region Card 1: 整合包基本信息

    /// <summary>
    /// 整合包名称
    /// </summary>
    [ObservableProperty]
    private string _modpackName = string.Empty;

    /// <summary>
    /// 整合包版本
    /// </summary>
    [ObservableProperty]
    private string _modpackVersion = "1.0.0";

    /// <summary>
    /// 整合包作者
    /// </summary>
    [ObservableProperty]
    private string _modpackAuthor = string.Empty;

    #endregion

    #region Card 2: 导出内容

    /// <summary>
    /// 导出 Mod 列表
    /// </summary>
    [ObservableProperty]
    private bool _includeMods = true;

    /// <summary>
    /// 导出 Mod 设置（config.json 等配置文件）
    /// </summary>
    [ObservableProperty]
    private bool _includeModSettings = true;

    /// <summary>
    /// 导出 SVL 启动器程序
    /// </summary>
    [ObservableProperty]
    private bool _includeSvlLauncher;

    #endregion

    #region Card 3: 打包选项

    /// <summary>
    /// 打包 Mod 文件（避免导入时下载）
    /// </summary>
    [ObservableProperty]
    private bool _bundleModFiles = true;

    #endregion

    #region 状态

    /// <summary>
    /// 当前状态信息
    /// </summary>
    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>
    /// 是否正在导出
    /// </summary>
    [ObservableProperty]
    private bool _isExporting;

    /// <summary>
    /// 导出进度 (0-100)
    /// </summary>
    [ObservableProperty]
    private int _exportProgress;

    /// <summary>
    /// Mod 列表
    /// </summary>
    public ObservableCollection<ExportModItem> ExportModItems { get; } = new();

    /// <summary>
    /// 已选中的 Mod 数量
    /// </summary>
    public int SelectedModCount => ExportModItems.Count(m => m.IsSelected);

    /// <summary>
    /// 总 Mod 数量
    /// </summary>
    public int TotalModCount => ExportModItems.Count;

    /// <summary>
    /// 实例名称（显示用）
    /// </summary>
    public string InstanceName => _instance?.Name ?? "未知";

    /// <summary>
    /// SMAPI 版本（显示用）
    /// </summary>
    public string SmapiVersionDisplay =>
        _instance != null && _instance.IsSMAPIInstance && !string.IsNullOrEmpty(_instance.SMAPIVersion)
            ? $"SMAPI {_instance.SMAPIVersion}"
            : "未安装 SMAPI";

    /// <summary>
    /// 游戏版本（显示用）
    /// </summary>
    public string GameVersionDisplay =>
        !string.IsNullOrEmpty(_instance?.Version) ? _instance.Version : "未知";

    #endregion

    public ExportViewModel(MainWindowViewModel mainViewModel, GamePathInfo instance)
    {
        _mainViewModel = mainViewModel;
        _instance = instance;

        // 默认使用实例名称作为整合包名称
        ModpackName = instance?.Name ?? "我的整合包";

        // 加载 Mod 列表
        _ = LoadModsAsync();
    }

    /// <summary>
    /// 加载当前实例的 Mod 列表
    /// </summary>
    private async Task LoadModsAsync()
    {
        try
        {
            StatusMessage = "正在加载 Mod 列表...";

            var modsPath = GetModsPath();
            if (string.IsNullOrEmpty(modsPath) || !Directory.Exists(modsPath))
            {
                StatusMessage = "未找到 Mods 目录";
                return;
            }

            var modManager = new ModManager();
            var mods = await modManager.LoadModsAsync(modsPath);

            // SMAPI 内置 Mod 的文件夹名/UniqueId，不纳入导出列表
            var smapiBundledIds = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "SMAPI.ConsoleCommands", "SMAPI.SaveBackup",
                "ConsoleCommands", "SaveBackup"
            };

            ExportModItems.Clear();
            foreach (var mod in mods.OrderBy(m => m.Name))
            {
                // 跳过 SMAPI 附带的内置 Mod
                if (smapiBundledIds.Contains(mod.UniqueId ?? string.Empty)
                    || smapiBundledIds.Contains(Path.GetFileName(mod.ModPath ?? string.Empty)))
                    continue;
                var item = new ExportModItem
                {
                    Name = mod.Name ?? mod.UniqueId ?? Path.GetFileName(mod.ModPath),
                    UniqueId = mod.UniqueId ?? string.Empty,
                    Version = mod.Version ?? string.Empty,
                    Author = mod.Author ?? string.Empty,
                    ModPath = mod.ModPath ?? string.Empty,
                    IsEnabled = mod.IsEnabled,
                    IsSelected = mod.IsEnabled, // 默认只勾选已启用的 Mod
                    OriginalMod = mod
                };

                // 读取 svl-source.json 来源信息
                TryReadSourceCredential(item);

                ExportModItems.Add(item);
            }

            OnPropertyChanged(nameof(SelectedModCount));
            OnPropertyChanged(nameof(TotalModCount));

            StatusMessage = $"已加载 {ExportModItems.Count} 个 Mod";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Export] 加载 Mod 列表失败");
            StatusMessage = "加载 Mod 列表失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 获取当前实例的 Mods 路径
    /// </summary>
    private string GetModsPath()
    {
        if (_instance == null) return string.Empty;

        if (_instance.EnableIsolation)
        {
            return InstanceIsolationService.GetIsolatedModsPath(_instance.GamePath, _instance.Name);
        }
        return Path.Combine(_instance.GamePath, "Mods");
    }

    /// <summary>
    /// 读取 Mod 的来源凭证（svl-source.json）
    /// </summary>
    private static void TryReadSourceCredential(ExportModItem item)
    {
        try
        {
            if (string.IsNullOrEmpty(item.ModPath) || !Directory.Exists(item.ModPath))
                return;

            var path = Path.Combine(item.ModPath, "svl-source.json");
            if (!File.Exists(path))
            {
                // 尝试读取 .source.json（NexusMods Collection 写入的格式）
                var altPath = Path.Combine(item.ModPath, ".source.json");
                if (!File.Exists(altPath))
                    return;

                var altJson = File.ReadAllText(altPath);
                using var altDoc = JsonDocument.Parse(altJson);
                var altRoot = altDoc.RootElement;

                if (altRoot.TryGetProperty("source", out var srcProp))
                {
                    item.SourcePlatform = srcProp.GetString() ?? "NexusMods";
                }
                if (altRoot.TryGetProperty("collection", out var collProp))
                {
                    item.SourceProjectId = collProp.GetString() ?? string.Empty;
                }
                return;
            }

            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("platform", out var platform))
            {
                item.SourcePlatform = platform.GetString() ?? "未知";
            }
            if (root.TryGetProperty("projectId", out var projectId))
            {
                item.SourceProjectId = projectId.GetString() ?? string.Empty;
            }
            if (root.TryGetProperty("fileId", out var fileId))
            {
                item.SourceFileId = fileId.GetString() ?? string.Empty;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[Export] 读取来源凭证失败: {item.ModPath}", ex);
        }
    }

    #region Commands

    /// <summary>
    /// 全选/取消全选 Mod
    /// </summary>
    [RelayCommand]
    private void ToggleSelectAll()
    {
        var allSelected = ExportModItems.All(m => m.IsSelected);
        foreach (var item in ExportModItems)
        {
            item.IsSelected = !allSelected;
        }
        OnPropertyChanged(nameof(SelectedModCount));
    }

    /// <summary>
    /// 保存导出配置
    /// </summary>
    [RelayCommand]
    private void SaveConfig()
    {
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "保存导出配置",
                Filter = "导出配置 (*.svlexport)|*.svlexport|JSON 文件 (*.json)|*.json",
                DefaultExt = ".svlexport",
                FileName = $"{ModpackName}_export_config"
            };

            if (dialog.ShowDialog() != true)
                return;

            var config = new ExportConfig
            {
                ModpackName = ModpackName,
                ModpackVersion = ModpackVersion,
                ModpackAuthor = ModpackAuthor,
                IncludeMods = IncludeMods,
                IncludeModSettings = IncludeModSettings,
                IncludeSvlLauncher = IncludeSvlLauncher,
                BundleModFiles = BundleModFiles,
                SelectedModUniqueIds = ExportModItems
                    .Where(m => m.IsSelected)
                    .Select(m => m.UniqueId)
                    .ToArray()
            };

            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(dialog.FileName, json);

            StatusMessage = "配置已保存";
            Log.Info($"[Export] 导出配置已保存: {dialog.FileName}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Export] 保存配置失败");
            StatusMessage = "保存配置失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 读取导出配置
    /// </summary>
    [RelayCommand]
    private void LoadConfig()
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "读取导出配置",
                Filter = "导出配置 (*.svlexport)|*.svlexport|JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*",
                DefaultExt = ".svlexport"
            };

            if (dialog.ShowDialog() != true)
                return;

            var json = File.ReadAllText(dialog.FileName);
            var config = JsonSerializer.Deserialize<ExportConfig>(json);
            if (config == null)
            {
                StatusMessage = "配置文件格式无效";
                return;
            }

            // 应用配置
            ModpackName = config.ModpackName;
            ModpackVersion = config.ModpackVersion;
            ModpackAuthor = config.ModpackAuthor;
            IncludeMods = config.IncludeMods;
            IncludeModSettings = config.IncludeModSettings;
            IncludeSvlLauncher = config.IncludeSvlLauncher;
            BundleModFiles = config.BundleModFiles;

            // 恢复 Mod 勾选状态
            if (config.SelectedModUniqueIds != null && config.SelectedModUniqueIds.Length > 0)
            {
                var selectedSet = new System.Collections.Generic.HashSet<string>(
                    config.SelectedModUniqueIds, StringComparer.OrdinalIgnoreCase);

                foreach (var item in ExportModItems)
                {
                    item.IsSelected = selectedSet.Contains(item.UniqueId);
                }
            }

            OnPropertyChanged(nameof(SelectedModCount));
            StatusMessage = "配置已加载";
            Log.Info($"[Export] 导出配置已加载: {dialog.FileName}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Export] 读取配置失败");
            StatusMessage = "读取配置失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 开始导出
    /// </summary>
    [RelayCommand]
    private async Task StartExportAsync()
    {
        if (IsExporting) return;

        if (string.IsNullOrWhiteSpace(ModpackName))
        {
            StatusMessage = "请输入整合包名称";
            return;
        }

        var selectedMods = ExportModItems.Where(m => m.IsSelected).ToList();
        if (IncludeMods && selectedMods.Count == 0)
        {
            StatusMessage = "请至少选择一个 Mod";
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "选择导出路径",
            Filter = "SVL 整合包 (*.zip)|*.zip",
            DefaultExt = ".zip",
            FileName = ModpackName
        };

        if (dialog.ShowDialog() != true)
            return;

        IsExporting = true;
        ExportProgress = 0;

        try
        {
            var outputPath = dialog.FileName;
            // 移除可能的重复扩展名
            if (outputPath.EndsWith(ModpackManager.FileExtension, StringComparison.OrdinalIgnoreCase))
            {
                outputPath = outputPath.Substring(0, outputPath.Length - ModpackManager.FileExtension.Length);
            }

            StatusMessage = "正在构建导出清单...";
            ExportProgress = 5;

            // 构建 Mod 列表
            var modsToExport = selectedMods
                .Where(m => m.OriginalMod != null)
                .Select(m => m.OriginalMod)
                .ToList();

            // 使用增强的导出方法
            var success = await ModpackManager.ExportModpackEnhancedAsync(
                mods: modsToExport,
                outputPath: outputPath,
                name: ModpackName,
                version: ModpackVersion,
                author: ModpackAuthor,
                description: $"由 SVL 导出的 {_instance?.Name ?? ""} 整合包",
                bundleModFiles: BundleModFiles,
                includeModSettings: IncludeModSettings,
                includeSvlLauncher: IncludeSvlLauncher,
                progressCallback: (progress, message) =>
                {
                    Application.Current?.Dispatcher?.Invoke(() =>
                    {
                        ExportProgress = progress;
                        StatusMessage = message;
                    });
                },
                smapiVersion: _instance?.SMAPIVersion ?? string.Empty,
                gameVersion: _instance?.Version ?? string.Empty);

            if (success)
            {
                ExportProgress = 100;
                var fileName = Path.GetFileName(outputPath + ModpackManager.FileExtension);
                StatusMessage = $"导出完成：{fileName}";
                Log.Info($"[Export] 整合包导出成功: {outputPath}{ModpackManager.FileExtension}");

                Controls.FloatingNotificationControl.Show(
                    title: "导出完成",
                    message: $"整合包 \"{ModpackName}\" 已成功导出为 {fileName}",
                    autoCloseDelay: 5000,
                    notificationType: Controls.NotificationType.Success);
            }
            else
            {
                StatusMessage = "导出失败，请查看日志";

                Controls.FloatingNotificationControl.Show(
                    title: "导出失败",
                    message: "整合包导出过程中出现错误，请查看日志了解详情。",
                    autoCloseDelay: 5000,
                    notificationType: Controls.NotificationType.Error);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Export] 导出失败");
            StatusMessage = "导出失败：" + ex.Message;
        }
        finally
        {
            IsExporting = false;
        }
    }

    #endregion
}
