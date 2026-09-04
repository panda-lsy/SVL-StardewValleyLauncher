using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SVL.Avalonia.Models;
using SVL.Avalonia.Services;

namespace SVL.Avalonia.ViewModels;

public sealed partial class SmapiVersionPickerViewModel : ObservableObject
{
    private const int PageSize = 5;

    private readonly string _targetPath;
    private readonly string _gameBasePath;
    private readonly RemoteCatalogService _catalogService;

    [ObservableProperty]
    private string _currentVersionText = "检测中...";

    [ObservableProperty]
    private string _loadingText = "正在加载...";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasSelectedVersion;

    [ObservableProperty]
    private bool _hasNextPage;

    [ObservableProperty]
    private bool _hasPreviousPage;

    [ObservableProperty]
    private string _pageInfoText = "第 1 页";

    [ObservableProperty]
    private SmapiVersionEntry? _selectedVersion;

    [ObservableProperty]
    private string _selectedSource = "GitHub";

    [ObservableProperty]
    private string _selectedPath = string.Empty;

    public ObservableCollection<SmapiVersionEntry> Versions { get; } = new();

    public List<string> Sources { get; } = ["GitHub", "CurseForge"];

    /// <summary>可选安装路径列表（来自版本选择页面的 Base 路径列表）。</summary>
    public List<string> AvailablePaths { get; }

    /// <summary>
    /// 由 DialogService 注入，Confirm/Cancel 时调用来关闭窗口
    /// </summary>
    public Action<SmapiVersionEntry?>? RequestClose { get; set; }

    private int _currentPage = 1;
    private bool _isLoadingInternal;

    public SmapiVersionPickerViewModel(
        string targetPath,
        string gameBasePath,
        RemoteCatalogService catalogService,
        IReadOnlyList<string>? availablePaths = null)
    {
        _targetPath = targetPath;
        _gameBasePath = gameBasePath;
        _catalogService = catalogService;

        // 初始化可选路径列表（来自版本选择页面的 Base 路径列表）
        AvailablePaths = availablePaths != null && availablePaths.Count > 0
            ? availablePaths.ToList()
            : new List<string> { targetPath };

        // 默认选择当前选中版本所在的 Base 路径：
        // targetPath 是版本路径（如 .../Versions/A World Of Dew），
        // 需要找到它的父目录（Base 路径，如 .../Versions）在 AvailablePaths 中的匹配项
        _selectedPath = ResolveDefaultBasePath(targetPath, AvailablePaths);
    }

    /// <summary>
    /// 根据版本路径推断对应的 Base 路径。
    /// 匹配优先级：父目录匹配 > 精确匹配 > 祖先目录匹配 > 第一个可用路径。
    /// 优先父目录是因为 AvailablePaths 可能同时包含 Base 路径和版本路径，
    /// 用户期望 SMAPI 安装到版本路径的父目录（Base 路径）。
    /// </summary>
    private static string ResolveDefaultBasePath(string targetPath, IReadOnlyList<string> availablePaths)
    {
        if (availablePaths.Count == 0)
        {
            return targetPath;
        }

        // 规范化路径比较：去除尾部目录分隔符
        var normalizedTarget = NormalizePath(targetPath);

        // 1. 优先匹配 targetPath 的父目录（版本路径 → Base 路径）
        try
        {
            var parent = System.IO.Path.GetDirectoryName(normalizedTarget);
            while (!string.IsNullOrWhiteSpace(parent))
            {
                var parentMatch = availablePaths.FirstOrDefault(p =>
                    string.Equals(NormalizePath(p), parent, StringComparison.OrdinalIgnoreCase));
                if (parentMatch != null)
                {
                    return parentMatch;
                }
                parent = System.IO.Path.GetDirectoryName(parent);
            }
        }
        catch
        {
            // 路径解析异常，忽略
        }

        // 2. 精确匹配（targetPath 本身在列表中，说明它就是一个 Base 路径）
        var exactMatch = availablePaths.FirstOrDefault(p =>
            string.Equals(NormalizePath(p), normalizedTarget, StringComparison.OrdinalIgnoreCase));
        if (exactMatch != null)
        {
            return exactMatch;
        }

        // 3. targetPath 是某个 AvailablePath 的子目录
        var ancestorMatch = availablePaths.FirstOrDefault(p =>
        {
            var np = NormalizePath(p);
            return normalizedTarget.StartsWith(np, StringComparison.OrdinalIgnoreCase) &&
                   normalizedTarget.Length > np.Length;
        });
        if (ancestorMatch != null)
        {
            return ancestorMatch;
        }

        return availablePaths[0];
    }

    /// <summary>去除尾部目录分隔符的规范化路径。</summary>
    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        return path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
    }

    public async Task InitializeAsync()
    {
        CurrentVersionText = DetectCurrentSmapiVersion(_targetPath);
        await LoadCurrentPageAsync();
    }

    partial void OnSelectedVersionChanged(SmapiVersionEntry? value)
    {
        HasSelectedVersion = value != null;
    }

    partial void OnSelectedSourceChanged(string value)
    {
        _currentPage = 1;
        _ = LoadWithSourceFallbackAsync(value);
    }

    private async Task LoadWithSourceFallbackAsync(string requestedSource)
    {
        var success = await LoadCurrentPageAsync();
        if (!success && string.Equals(requestedSource, "CurseForge", StringComparison.OrdinalIgnoreCase))
        {
            // CurseForge 失败，回退到 GitHub
            LoadingText = "CurseForge 暂不可用，已切换回 GitHub";
            SelectedSource = "GitHub";
            _currentPage = 1;
            await LoadCurrentPageAsync();
        }
    }

    [RelayCommand]
    private async Task PreviousPageAsync()
    {
        if (_currentPage > 1 && !_isLoadingInternal)
        {
            _currentPage--;
            await LoadCurrentPageAsync();
        }
    }

    [RelayCommand]
    private async Task NextPageAsync()
    {
        if (HasNextPage && !_isLoadingInternal)
        {
            _currentPage++;
            await LoadCurrentPageAsync();
        }
    }

    [RelayCommand]
    private void Confirm()
    {
        if (SelectedVersion != null)
        {
            SelectedVersion.TargetPath = SelectedPath;
            Debug.WriteLine($"[SmapiVersionPicker] 用户选择安装 SMAPI {SelectedVersion.Version} ({SelectedVersion.Source}) 到 {SelectedPath}");
            RequestClose?.Invoke(SelectedVersion);
        }
    }

    private async Task<bool> LoadCurrentPageAsync()
    {
        if (_isLoadingInternal)
        {
            return true;
        }

        _isLoadingInternal = true;
        IsLoading = true;
        SelectedVersion = null;
        HasSelectedVersion = false;
        var success = false;

        try
        {
            Versions.Clear();

            if (string.Equals(SelectedSource, "CurseForge", StringComparison.OrdinalIgnoreCase))
            {
                success = await LoadCurseForgePageAsync();
            }
            else
            {
                await LoadGitHubPageAsync();
                success = true;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SmapiVersionPicker] 加载 SMAPI 版本失败: {ex.Message}");
            LoadingText = $"加载失败: {ex.Message}";
            success = false;
        }
        finally
        {
            _isLoadingInternal = false;
            IsLoading = false;
        }

        return success;
    }

    private async Task LoadGitHubPageAsync()
    {
        LoadingText = $"正在从 GitHub 获取版本列表（第 {_currentPage} 页）...";

        var entries = await _catalogService.GetSmapiVersionEntriesAsync(_currentPage, PageSize);

        if (entries.Count > 0)
        {
            foreach (var entry in entries)
            {
                Versions.Add(entry);
            }

            HasNextPage = entries.Count == PageSize;
            HasPreviousPage = _currentPage > 1;
            PageInfoText = $"第 {_currentPage} 页";
            LoadingText = $"已加载 {entries.Count} 个版本";
        }
        else
        {
            HasNextPage = false;
            HasPreviousPage = _currentPage > 1;
            LoadingText = _currentPage == 1 ? "无法获取版本列表，请检查网络" : "没有更多版本";
        }
    }

    private async Task<bool> LoadCurseForgePageAsync()
    {
        LoadingText = "正在从 CurseForge 获取版本列表...";

        try
        {
            var entries = await _catalogService.GetSmapiVersionEntriesFromCurseForgeAsync(_currentPage, PageSize);

            if (entries.Count > 0)
            {
                foreach (var entry in entries)
                {
                    Versions.Add(entry);
                }

                HasNextPage = entries.Count == PageSize;
                HasPreviousPage = _currentPage > 1;
                PageInfoText = $"第 {_currentPage} 页";
                LoadingText = $"已加载 {entries.Count} 个版本";
                return true;
            }

            HasNextPage = false;
            LoadingText = "CurseForge 暂时不可用，请切换到 GitHub 源";
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SmapiVersionPicker] CurseForge 加载失败: {ex.Message}");
            HasNextPage = false;
            LoadingText = $"CurseForge 加载失败，请切换到 GitHub 源";
            return false;
        }
    }

    private static string DetectCurrentSmapiVersion(string targetPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(targetPath) || !System.IO.Directory.Exists(targetPath))
            {
                return "未安装";
            }

            var exePath = System.IO.Path.Combine(targetPath, "StardewModdingAPI.exe");
            if (!System.IO.File.Exists(exePath))
            {
                exePath = System.IO.Path.Combine(targetPath, "StardewModdingAPI");
            }

            if (System.IO.File.Exists(exePath))
            {
                var version = System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath).FileVersion;
                return string.IsNullOrWhiteSpace(version) ? "未知版本" : version;
            }

            return "未安装";
        }
        catch
        {
            return "未安装";
        }
    }
}
