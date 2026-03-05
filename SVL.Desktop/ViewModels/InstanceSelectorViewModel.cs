using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SVL.Core.Stardew.Instance;
using SVL.Core.Logging;
using WinForms = System.Windows.Forms;
using System.IO;
using SVL.Desktop.Controls;

namespace SVL.Desktop.ViewModels;

public partial class InstanceSelectorViewModel : ObservableObject
{
    private readonly MainWindowViewModel _mainViewModel;

    [ObservableProperty]
    private ObservableCollection<GamePathEntry> _pathEntries = new();

    [ObservableProperty]
    private GamePathEntry? _selectedPathEntry;

    [ObservableProperty]
    private bool _hasPathEntries;

    /// <summary>
    /// 当前选中路径下的实例列表（用于右侧显示）
    /// </summary>
    public ObservableCollection<GamePathInfo> SelectedPathInstances { get; } = new();

    partial void OnPathEntriesChanged(ObservableCollection<GamePathEntry> value)
    {
        HasPathEntries = value.Any();
    }

    partial void OnSelectedPathEntryChanged(GamePathEntry? value)
    {
        System.Diagnostics.Debug.WriteLine($"[InstanceSelector] OnSelectedPathEntryChanged called, value = {value?.DisplayName}");

        // 更新右侧实例列表
        SelectedPathInstances.Clear();
        if (value != null)
        {
            System.Diagnostics.Debug.WriteLine($"[InstanceSelector] Path has {value.Instances.Count} instances");
            foreach (var instance in value.Instances)
            {
                System.Diagnostics.Debug.WriteLine($"[InstanceSelector] Adding instance: {instance.Name} (SMAPI: {instance.IsSMAPIInstance})");
                SelectedPathInstances.Add(instance);
            }
        }

        // 手动触发属性通知，确保 UI 更新
        OnPropertyChanged(nameof(SelectedPathInstances));
        System.Diagnostics.Debug.WriteLine($"[InstanceSelector] SelectedPathInstances count = {SelectedPathInstances.Count}");
    }

    public InstanceSelectorViewModel(MainWindowViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;

        // 订阅全局实例更改事件
        GlobalEvents.InstanceChanged += OnInstanceChanged;

        // 延迟加载，避免阻塞 UI 线程
        System.Windows.Application.Current.Dispatcher.BeginInvoke(
            new System.Action(() => LoadPathEntries()),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 当实例配置更改时刷新显示
    /// </summary>
    private void OnInstanceChanged(object? sender, InstanceChangedEventArgs e)
    {
        // 刷新实例列表以显示最新的图标等信息
        System.Windows.Application.Current.Dispatcher.BeginInvoke(new System.Action(() =>
        {
            // 如果实例 ID 为空，执行完整刷新（包括扫描版本目录，但不显示消息框）
            if (string.IsNullOrWhiteSpace(e.InstanceId))
            {
                RefreshWithoutMessage();
            }
            else
            {
                LoadPathEntries();
            }
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>
    /// 加载游戏路径列表
    /// </summary>
    private void LoadPathEntries()
    {
        try
        {
            // 保存当前选中的路径信息
            var currentPath = SelectedPathEntry?.GamePath;
            System.Diagnostics.Debug.WriteLine($"[InstanceSelector] Loading entries, current path: {currentPath}");

            PathEntries.Clear();

            // 从配置文件加载已保存的实例
            var savedInstances = SettingsService.LoadInstances();
            System.Diagnostics.Debug.WriteLine($"[InstanceSelector] Loaded {savedInstances.Count} instances from config");

            // 按路径分组
            var pathEntries = GamePathService.GroupInstancesByPath(savedInstances);
            System.Diagnostics.Debug.WriteLine($"[InstanceSelector] Grouped into {pathEntries.Count} path entries");

            foreach (var entry in pathEntries)
            {
                System.Diagnostics.Debug.WriteLine($"[InstanceSelector] Path entry: {entry.DisplayName}, {entry.Instances.Count} instances");
                PathEntries.Add(entry);
            }

            // 如果没有已保存的实例，尝试自动检测游戏路径
            if (!PathEntries.Any())
            {
                Log.Info("[InstanceSelector] 没有已保存的实例，开始自动检测游戏路径...");
                var detectedPaths = GamePathService.AutoDetectGamePaths();
                Log.Info($"[InstanceSelector] 自动检测到 {detectedPaths.Length} 个游戏路径");

                foreach (var path in detectedPaths)
                {
                    try
                    {
                        Log.Info($"[InstanceSelector] 正在添加检测到的路径: {path}");
                        var pathInfos = GamePathService.CreateGamePathInfos(path);
                        var newEntry = new GamePathEntry
                        {
                            DisplayName = Path.GetFileName(path),
                            GamePath = path,
                            Version = pathInfos.First().Version,
                            Instances = pathInfos
                        };

                        PathEntries.Add(newEntry);
                        Log.Info($"[InstanceSelector] 成功添加路径: {newEntry.DisplayName} (版本: {newEntry.Version})");
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"[InstanceSelector] 添加路径失败 {path}: {ex.Message}");
                    }
                }

                // 如果检测到了路径，自动保存
                if (PathEntries.Any())
                {
                    SaveAllInstances();
                    Log.Info($"[InstanceSelector] 自动保存了 {PathEntries.Count} 个检测到的实例");
                }
                else
                {
                    Log.Info("[InstanceSelector] 未检测到任何游戏路径");
                }
            }

            // 尝试恢复之前选中的路径
            if (!string.IsNullOrEmpty(currentPath))
            {
                var previousEntry = PathEntries.FirstOrDefault(e => e.GamePath == currentPath);
                if (previousEntry != null)
                {
                    SelectedPathEntry = previousEntry;
                    System.Diagnostics.Debug.WriteLine($"[InstanceSelector] Restored previous path: {previousEntry.DisplayName}");
                    return;
                }
            }

            // 如果没有之前选中的路径，默认选中第一个
            if (PathEntries.Any())
            {
                SelectedPathEntry = PathEntries.First();
                System.Diagnostics.Debug.WriteLine($"[InstanceSelector] Auto-selected first path: {SelectedPathEntry.DisplayName}");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"加载路径列表失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 添加游戏实例
    /// </summary>
    [RelayCommand]
    private void AddInstance()
    {
        using (var dialog = new Utilities.SimpleFolderDialog())
        {
            dialog.Title = "选择 Stardew Valley 游戏安装目录";

            if (dialog.ShowDialog())
            {
                var selectedPath = dialog.SelectedPath;

                // 验证游戏路径
                if (!GamePathService.IsValidGamePath(selectedPath))
                {
                    SvlMessageBox.Warning(
                        $"所选路径不是有效的游戏目录：\n{selectedPath}\n\n" +
                        "请确保目录包含 'Stardew Valley.exe' 文件",
                        "无效路径");
                    return;
                }

                // 检查路径是否已存在
                var existingEntry = PathEntries.FirstOrDefault(e =>
                    e.GamePath.Equals(selectedPath, StringComparison.OrdinalIgnoreCase));

                if (existingEntry != null)
                {
                    SvlMessageBox.Info("该路径已存在", "重复添加");
                    // 选中已存在的路径
                    SelectedPathEntry = existingEntry;
                    return;
                }

                // 创建新路径条目
                var pathInfos = GamePathService.CreateGamePathInfos(selectedPath);
                var newEntry = new GamePathEntry
                {
                    DisplayName = Path.GetFileName(selectedPath),
                    GamePath = selectedPath,
                    Version = pathInfos.First().Version,
                    Instances = pathInfos
                };

                PathEntries.Add(newEntry);
                SelectedPathEntry = newEntry;

                // 保存到配置
                SaveAllInstances();
            }
        }
    }

    /// <summary>
    /// 刷新实例列表（自动检测游戏 + 扫描所有路径的版本隔离实例 + 检测已删除的版本）
    /// </summary>
    [RelayCommand]
    private void Refresh()
    {
        try
        {
            // 先重新加载已保存的实例
            LoadPathEntries();

            var totalIsolatedCount = 0;
            var pathRefreshCount = 0;
            var removedInstances = new List<string>();

            // 对每个已存在的路径，扫描版本隔离实例
            foreach (var entry in PathEntries.ToList())
            {
                try
                {
                    // *** 检测已删除的版本 ***
                    var existingInstances = entry.Instances.ToList();
                    var instancesToRemove = new List<GamePathInfo>();

                    foreach (var instance in existingInstances)
                    {
                        if (instance.EnableIsolation)
                        {
                            // 检查版本隔离目录是否存在
                            var versionPath = InstanceIsolationService.GetVersionPath(entry.GamePath, instance.Name);
                            if (!Directory.Exists(versionPath))
                            {
                                instancesToRemove.Add(instance);
                                removedInstances.Add($"{entry.DisplayName}/{instance.Name}");
                            }
                        }
                        else
                        {
                            // 检查游戏本体路径是否存在
                            if (!Directory.Exists(entry.GamePath))
                            {
                                instancesToRemove.Add(instance);
                                removedInstances.Add($"{entry.DisplayName}/{instance.Name}");
                            }
                        }
                    }

                    // 移除已删除的实例
                    foreach (var instance in instancesToRemove)
                    {
                        entry.Instances.Remove(instance);
                    }

                    // 扫描versions文件夹中的版本隔离实例
                    var isolatedInstances = GamePathService.ScanVersionIsolatedInstances(entry.GamePath, existingInstances);

                    if (isolatedInstances.Count > 0)
                    {
                        // 合并实例列表
                        var allInstances = entry.Instances.Concat(isolatedInstances).ToList();

                        // 更新实例列表
                        entry.Instances.Clear();
                        foreach (var info in allInstances)
                        {
                            entry.Instances.Add(info);
                        }

                        totalIsolatedCount += isolatedInstances.Count;
                        pathRefreshCount++;
                    }
                }
                catch
                {
                    // 忽略单个路径的错误，继续处理其他路径
                }
            }

            // 保存所有实例
            SaveAllInstances();

            // 刷新当前选中路径的显示
            if (SelectedPathEntry != null)
            {
                OnSelectedPathEntryChanged(SelectedPathEntry);
            }

            // 然后执行自动检测新路径
            var detectedPaths = GamePathService.AutoDetectGamePaths();
            var newPathCount = 0;

            foreach (var path in detectedPaths)
            {
                try
                {
                    // 检查是否已存在
                    if (!PathEntries.Any(e => e.GamePath.Equals(path, StringComparison.OrdinalIgnoreCase)))
                    {
                        var pathInfos = GamePathService.CreateGamePathInfos(path);
                        var newEntry = new GamePathEntry
                        {
                            DisplayName = Path.GetFileName(path),
                            GamePath = path,
                            Version = pathInfos.First().Version,
                            Instances = pathInfos
                        };

                        PathEntries.Add(newEntry);
                        newPathCount++;
                    }
                }
                catch
                {
                    // 忽略单个路径的错误，继续处理其他路径
                }
            }

            // 显示汇总信息
            var message = "刷新完成";
            var messageParts = new List<string>();

            if (removedInstances.Count > 0)
            {
                messageParts.Add($"• 移除了 {removedInstances.Count} 个已删除的版本");
            }

            if (pathRefreshCount > 0)
            {
                messageParts.Add($"• 更新了 {pathRefreshCount} 个路径的版本隔离实例");
            }

            if (totalIsolatedCount > 0)
            {
                messageParts.Add($"• 发现 {totalIsolatedCount} 个新版本隔离实例");
            }

            if (newPathCount > 0)
            {
                messageParts.Add($"• 检测到 {newPathCount} 个新游戏路径");
            }

            if (messageParts.Count > 0)
            {
                message += "\n" + string.Join("\n", messageParts);
                SvlMessageBox.Success(message, "刷新完成");
            }
        }
        catch (Exception ex)
        {
            SvlMessageBox.Error($"刷新失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 刷新实例列表（不带消息框，用于自动刷新）
    /// </summary>
    private void RefreshWithoutMessage()
    {
        try
        {
            // 先重新加载已保存的实例
            LoadPathEntries();

            // 对每个已存在的路径，扫描版本隔离实例
            foreach (var entry in PathEntries.ToList())
            {
                try
                {
                    // *** 检测已删除的版本 ***
                    var existingInstances = entry.Instances.ToList();
                    var instancesToRemove = new System.Collections.Generic.List<GamePathInfo>();

                    foreach (var instance in existingInstances)
                    {
                        if (instance.EnableIsolation)
                        {
                            // 检查版本隔离目录是否存在
                            var versionPath = InstanceIsolationService.GetVersionPath(entry.GamePath, instance.Name);
                            if (!Directory.Exists(versionPath))
                            {
                                instancesToRemove.Add(instance);
                            }
                        }
                        else
                        {
                            // 检查游戏本体路径是否存在
                            if (!Directory.Exists(entry.GamePath))
                            {
                                instancesToRemove.Add(instance);
                            }
                        }
                    }

                    // 移除已删除的实例
                    foreach (var instance in instancesToRemove)
                    {
                        entry.Instances.Remove(instance);
                    }

                    // 扫描versions文件夹中的版本隔离实例
                    var isolatedInstances = GamePathService.ScanVersionIsolatedInstances(entry.GamePath, existingInstances);

                    if (isolatedInstances.Count > 0)
                    {
                        // 合并实例列表
                        var allInstances = entry.Instances.Concat(isolatedInstances).ToList();

                        // 更新实例列表
                        entry.Instances.Clear();
                        foreach (var info in allInstances)
                        {
                            entry.Instances.Add(info);
                        }
                    }
                }
                catch
                {
                    // 忽略单个路径的错误，继续处理其他路径
                }
            }

            // 保存所有实例
            SaveAllInstances();

            // 刷新当前选中路径的显示
            if (SelectedPathEntry != null)
            {
                OnSelectedPathEntryChanged(SelectedPathEntry);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"自动刷新实例列表失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 导入整合包
    /// </summary>
    [RelayCommand]
    private async System.Threading.Tasks.Task ImportModpack()
    {
        try
        {
            var mainWindow = System.Windows.Application.Current.MainWindow;
            if (mainWindow == null)
                return;

            // 显示文件选择对话框
            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择整合包文件",
                Filter = "整合包文件 (*.zip;*.7z;*.cfmodpack)|*.zip;*.7z;*.cfmodpack|ZIP 文件 (*.zip)|*.zip|7z 文件 (*.7z)|*.7z|Curseforge 整合包 (*.cfmodpack)|*.cfmodpack",
                FilterIndex = 1,
                RestoreDirectory = true
            };

            var dialogResult = openFileDialog.ShowDialog();
            if (dialogResult != true || string.IsNullOrEmpty(openFileDialog.FileName))
                return;

            var filePath = openFileDialog.FileName;

            // 使用 MainWindow 的模糊效果
            if (mainWindow is MainWindow mw)
            {
                mw.ApplyBlurEffect();
            }

            // 显示新的导入对话框
            var result = await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                return SVL.Desktop.Controls.ModpackDropDialog.Show(mainWindow, filePath);
            });

            if (mainWindow is MainWindow mw2)
            {
                mw2.RemoveBlurEffect();
            }

            if (result != null && result.IsValid && result.DetectionResult != null)
            {
                // 调用 MainWindowViewModel 的方法启动安装
                _mainViewModel.HandleModpackDialogResult(result);
            }
        }
        catch (Exception ex)
        {
            SvlMessageBox.Error($"导入整合包失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 点击实例：选中并返回主页
    /// </summary>
    [RelayCommand]
    private void SelectInstance(GamePathInfo instance)
    {
        if (instance == null)
            return;

        // 保存为默认实例
        SettingsService.SaveDefaultInstance(instance.Id);

        // 保存所有实例（更新默认标记）
        SaveAllInstances();

        // 返回主页
        _mainViewModel.NavigateToLaunchCommand.Execute(null);
    }

    /// <summary>
    /// 右键菜单：打开版本设置
    /// </summary>
    [RelayCommand]
    private void OpenVersionSettings(GamePathInfo instance)
    {
        if (instance == null)
            return;

        // 设置当前选中的实例
        _mainViewModel.SelectedVersionSettingsInstance = instance;

        // 导航到版本设置页面
        _mainViewModel.CurrentPage = PageType.VersionSettings;
    }

    /// <summary>
    /// 保存所有实例到配置
    /// </summary>
    private void SaveAllInstances()
    {
        var allInstances = PathEntries
            .SelectMany(e => e.Instances)
            .ToList();

        SettingsService.SaveInstances(allInstances);
    }

    /// <summary>
    /// 重命名路径
    /// </summary>
    [RelayCommand]
    private void RenamePath(GamePathEntry entry)
    {
        if (entry == null)
            return;

        // 获取主窗口
        var mainWindow = System.Windows.Application.Current.MainWindow;
        if (mainWindow == null)
            return;

        // 使用输入对话框获取新名称
        var newName = SVL.Desktop.Controls.InputDialog.Show(
            mainWindow,
            "请输入新的路径名称：",
            entry.DisplayName);

        if (string.IsNullOrWhiteSpace(newName))
            return;

        if (newName == entry.DisplayName)
            return;

        entry.DisplayName = newName;
        SaveAllInstances();
    }

    /// <summary>
    /// 打开路径文件夹
    /// </summary>
    [RelayCommand]
    private void OpenFolder(GamePathEntry entry)
    {
        if (entry == null)
            return;

        try
        {
            System.Diagnostics.Process.Start("explorer.exe", entry.GamePath);
        }
        catch (Exception ex)
        {
            SvlMessageBox.Error($"打开文件夹失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 删除路径
    /// </summary>
    [RelayCommand]
    private void DeletePath(GamePathEntry entry)
    {
        if (entry == null)
            return;

        if (SvlMessageBox.Confirm(
            $"确定要从列表中移除路径 '{entry.DisplayName}' 吗？\n\n注意：此操作不会删除游戏文件，只会从列表中移除。",
            "确认删除"))
        {
            PathEntries.Remove(entry);
            SaveAllInstances();

            // 如果删除的是当前选中的路径，清空选择
            if (SelectedPathEntry == entry)
            {
                SelectedPathEntry = PathEntries.FirstOrDefault();
            }
        }
    }

    /// <summary>
    /// 单个路径刷新（重新读取版本信息）
    /// </summary>
    [RelayCommand]
    private void RefreshPath(GamePathEntry entry)
    {
        if (entry == null)
            return;

        try
        {
            // 重新检测基础实例（原版/SMAPI）
            var detectedBaseInfos = GamePathService.CreateGamePathInfos(entry.GamePath);

            // 从配置中读取该路径已有实例，保留用户元数据（图标/描述/收藏等）
            var savedInstances = SettingsService.LoadInstances()
                .Where(i => i.GamePath.Equals(entry.GamePath, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var savedByName = savedInstances.ToDictionary(i => i.Name, StringComparer.OrdinalIgnoreCase);

            // 合并基础实例：优先保留已有实例对象，更新检测字段
            var mergedBaseInfos = new List<GamePathInfo>();
            foreach (var detected in detectedBaseInfos)
            {
                if (savedByName.TryGetValue(detected.Name, out var saved))
                {
                    saved.Version = detected.Version;
                    saved.SMAPIVersion = detected.SMAPIVersion;
                    saved.HasSMAPIInstalled = detected.HasSMAPIInstalled;
                    saved.IsSMAPIInstance = detected.IsSMAPIInstance;
                    saved.EnableIsolation = detected.EnableIsolation;
                    mergedBaseInfos.Add(saved);
                    savedByName.Remove(detected.Name);
                }
                else
                {
                    mergedBaseInfos.Add(detected);
                }
            }

            // 扫描 versions 文件夹，传入已合并实例以便复用元数据
            var isolatedInstances = GamePathService.ScanVersionIsolatedInstances(entry.GamePath, mergedBaseInfos);

            // 合并实例列表
            var allInstances = mergedBaseInfos.Concat(isolatedInstances).ToList();

            // 更新实例列表
            entry.Instances.Clear();
            foreach (var info in allInstances)
            {
                entry.Instances.Add(info);
            }

            // 更新版本信息
            entry.Version = mergedBaseInfos.First().Version;

            SaveAllInstances();

            // 如果刷新的是当前选中的路径，刷新右侧显示
            if (SelectedPathEntry == entry)
            {
                OnSelectedPathEntryChanged(entry);
            }

            var message = $"路径 '{entry.DisplayName}' 信息已刷新";
            if (isolatedInstances.Count > 0)
            {
                message += $"\n发现 {isolatedInstances.Count} 个版本隔离实例";
            }
            SvlMessageBox.Success(message);
        }
        catch (Exception ex)
        {
            SvlMessageBox.Error($"刷新路径失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 切换实例收藏状态
    /// </summary>
    [RelayCommand]
    private void ToggleFavorite(GamePathInfo instance)
    {
        if (instance == null)
            return;

        // 切换收藏状态
        instance.IsFavorite = !instance.IsFavorite;

        // 保存到配置
        SaveAllInstances();

        // 触发全局事件，通知其他页面实例配置已更改
        GlobalEvents.OnInstanceChanged(instance.Id);
    }

    /// <summary>
    /// 刷新所有实例列表（从配置文件重新加载）
    /// </summary>
    public void RefreshInstances()
    {
        try
        {
            LoadPathEntries();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"刷新实例列表失败: {ex.Message}");
        }
    }
}
