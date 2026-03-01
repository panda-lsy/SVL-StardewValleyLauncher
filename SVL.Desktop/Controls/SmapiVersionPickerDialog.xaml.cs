using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SVL.Core.Logging;
using SVL.Core.Stardew.Mod.SMAPI;
using SVL.Core.Stardew.ResourceProject.NexusMods;
using SVL.Core.Download;

namespace SVL.Desktop.Controls;

/// <summary>
/// SMAPI 版本选择对话框
/// </summary>
public partial class SmapiVersionPickerDialog : Window
{
    /// <summary>
    /// 用户选择的 SMAPI 版本
    /// </summary>
    public SmapiVersionInfo? SelectedVersion { get; private set; }

    private readonly string _targetPath;
    private readonly string _gameBasePath;
    private readonly ObservableCollection<SmapiVersionInfo> _versions = new();
    private string _currentSource = "GitHub";
    private bool _isLoading = false;

    // 分页相关
    private const int PageSize = 5;
    private int _currentPage = 1;
    private int _totalCount = 0;
    private bool _hasNextPage = false;

    // 本地缓存（用于 NexusMods/CurseForge 本地分页）
    private List<SmapiVersionInfo> _cachedVersions = new();
    private List<NexusModFile>? _nexusModFilesCache;

    public SmapiVersionPickerDialog(string targetPath, string gameBasePath)
    {
        InitializeComponent();
        _targetPath = targetPath;
        _gameBasePath = gameBasePath;

        VersionsListView.ItemsSource = _versions;
        VersionsListView.SelectionChanged += VersionsListView_SelectionChanged;

        Loaded += SmapiVersionPickerDialog_Loaded;
    }

    private async void SmapiVersionPickerDialog_Loaded(object sender, RoutedEventArgs e)
    {
        // 显示当前版本
        var currentVersion = SmapApiService.GetInstalledSmapiVersion(_targetPath);
        CurrentVersionRun.Text = currentVersion ?? "未安装";

        // 加载可用版本
        await LoadCurrentPageAsync();
    }

    private async void SourceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SourceComboBox == null || _isLoading)
            return;

        if (SourceComboBox.SelectedItem is ComboBoxItem selectedItem)
        {
            var tag = selectedItem.Tag?.ToString();
            var newSource = tag ?? "GitHub";

            if (newSource != _currentSource)
            {
                _currentSource = newSource;
                _currentPage = 1;
                _cachedVersions.Clear();
                _nexusModFilesCache = null;
                _hasNextPage = false;
                _totalCount = 0;
                await LoadCurrentPageAsync();
            }
        }
    }

    private async void PrevPage_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPage > 1 && !_isLoading)
        {
            _currentPage--;
            await LoadCurrentPageAsync();
        }
    }

    private async void NextPage_Click(object sender, RoutedEventArgs e)
    {
        if (_hasNextPage && !_isLoading)
        {
            _currentPage++;
            await LoadCurrentPageAsync();
        }
    }

    private async Task LoadCurrentPageAsync()
    {
        if (_isLoading)
            return;

        _isLoading = true;

        try
        {
            await Dispatcher.InvokeAsync(() =>
            {
                _versions.Clear();
                LoadingPanel.Visibility = Visibility.Visible;
                VersionsListView.Visibility = Visibility.Collapsed;
                InstallButton.IsEnabled = false;
                SelectedVersion = null;
                UpdatePaginationState();
            });

            if (_currentSource == "GitHub")
            {
                await LoadGitHubPageAsync();
            }
            else if (_currentSource == "NexusMods")
            {
                await LoadNexusModsPageAsync();
            }
            else if (_currentSource == "CurseForge")
            {
                await LoadCurseForgePageAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[SmapiVersionPicker] 加载 SMAPI 版本失败");
            await Dispatcher.InvokeAsync(() =>
            {
                LoadingText.Text = $"加载失败: {ex.Message}";
                LoadingPanel.Visibility = Visibility.Visible;
                VersionsListView.Visibility = Visibility.Collapsed;
            });
        }
        finally
        {
            _isLoading = false;
        }
    }

    /// <summary>
    /// 加载 GitHub 页面（服务器端分页）
    /// </summary>
    private async Task LoadGitHubPageAsync()
    {
        LoadingText.Text = $"正在从 GitHub 获取版本列表（第 {_currentPage} 页）...";

        try
        {
            // GitHub 使用服务器端分页，每次只请求当前页
            var githubVersions = await SmapApiService.GetAllVersionsAsync(page: _currentPage, perPage: PageSize);

            if (githubVersions != null && githubVersions.Count > 0)
            {
                foreach (var version in githubVersions)
                {
                    version.Source = "GitHub";
                }

                // 如果返回数量等于 PageSize，说明可能还有下一页
                _hasNextPage = githubVersions.Count == PageSize;

                // 对于 GitHub，无法知道总数，显示当前加载的数量
                _totalCount = (_currentPage - 1) * PageSize + githubVersions.Count;

                await DisplayVersionsAsync(githubVersions, hasMorePages: _hasNextPage);

                Log.Info($"[SmapiVersionPicker] 从 GitHub 加载了 {githubVersions.Count} 个 SMAPI 版本（第 {_currentPage} 页）");
            }
            else
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    LoadingText.Text = _currentPage == 1 ? "无法从 GitHub 获取版本列表" : "没有更多版本";
                    if (_currentPage == 1)
                    {
                        LoadingPanel.Visibility = Visibility.Visible;
                        VersionsListView.Visibility = Visibility.Collapsed;
                    }
                    _hasNextPage = false;
                    UpdatePaginationState();
                });
            }
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                LoadingText.Text = $"加载失败: {ex.Message}";
            });
            Log.Error(ex, "[SmapiVersionPicker] 从 GitHub 加载失败");
        }
    }

    /// <summary>
    /// 加载 NexusMods 页面（本地分页）
    /// </summary>
    private async Task LoadNexusModsPageAsync()
    {
        LoadingText.Text = "正在从 NexusMods 获取版本列表...";

        try
        {
            // 如果没有缓存，则获取所有文件
            if (_nexusModFilesCache == null)
            {
                // SMAPI 在 NexusMods 的 Mod ID 是 2400
                var modFiles = await NexusModsService.GetModFilesAsync(2400);

                if (modFiles == null || modFiles.Count == 0)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        LoadingText.Text = "无法从 NexusMods 获取版本列表";
                    });
                    return;
                }

                _nexusModFilesCache = modFiles;

                // 构建缓存列表
                _cachedVersions = _nexusModFilesCache
                    .OrderByDescending(f => f.UploadedTime)
                    .Select(file => new SmapiVersionInfo
                    {
                        Version = !string.IsNullOrEmpty(file.Version) ? file.Version : ExtractVersionFromName(file.Name ?? "Unknown"),
                        Description = file.Name ?? "",
                        Source = "NexusMods",
                        FileId = file.GetFileIdLong(),
                        DownloadUrl = ""
                    })
                    .ToList();

                _totalCount = _cachedVersions.Count;
                Log.Info($"[SmapiVersionPicker] 从 NexusMods 获取了 {_totalCount} 个文件");
            }

            // 本地分页
            var skip = (_currentPage - 1) * PageSize;
            var pageVersions = _cachedVersions.Skip(skip).Take(PageSize).ToList();
            _hasNextPage = skip + PageSize < _totalCount;

            await DisplayVersionsAsync(pageVersions, hasMorePages: _hasNextPage, total: _totalCount);
        }
        catch (NexusModsTokenExpiredException)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                LoadingText.Text = "NexusMods 登录已过期，请重新登录";
                Log.Warn("[SmapiVersionPicker] NexusMods Token 已过期");
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                LoadingText.Text = $"加载失败: {ex.Message}";
            });
            Log.Error(ex, "[SmapiVersionPicker] 从 NexusMods 加载失败");
        }
    }

    /// <summary>
    /// 加载 CurseForge 页面（本地分页）
    /// </summary>
    private async Task LoadCurseForgePageAsync()
    {
        LoadingText.Text = "正在从 CurseForge 获取版本列表...";

        try
        {
            // 检查 CurseForge API Key 是否配置
            if (!CurseforgeApiService.HasApiKey)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    LoadingText.Text = "CurseForge API 未配置";
                });
                return;
            }

            // 如果没有缓存，则获取所有文件
            if (_cachedVersions.Count == 0)
            {
                var curseforgeVersions = await SmapApiService.GetAllVersionsFromCurseforgeAsync(index: 0, pageSize: 50);

                if (curseforgeVersions != null && curseforgeVersions.Count > 0)
                {
                    _cachedVersions = curseforgeVersions.ToList();
                    _totalCount = _cachedVersions.Count;
                    Log.Info($"[SmapiVersionPicker] 从 CurseForge 获取了 {_totalCount} 个文件");
                }
                else
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        LoadingText.Text = "无法从 CurseForge 获取版本列表";
                    });
                    return;
                }
            }

            // 本地分页
            var skip = (_currentPage - 1) * PageSize;
            var pageVersions = _cachedVersions.Skip(skip).Take(PageSize).ToList();
            _hasNextPage = skip + PageSize < _totalCount;

            await DisplayVersionsAsync(pageVersions, hasMorePages: _hasNextPage, total: _totalCount);
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                LoadingText.Text = $"加载失败: {ex.Message}";
            });
            Log.Error(ex, "[SmapiVersionPicker] 从 CurseForge 加载失败");
        }
    }

    /// <summary>
    /// 显示版本列表
    /// </summary>
    private async Task DisplayVersionsAsync(List<SmapiVersionInfo> versions, bool hasMorePages, int? total = null)
    {
        await Dispatcher.InvokeAsync(() =>
        {
            _versions.Clear();
            foreach (var version in versions)
            {
                _versions.Add(version);
            }

            LoadingPanel.Visibility = Visibility.Collapsed;
            VersionsListView.Visibility = Visibility.Visible;

            _hasNextPage = hasMorePages;

            if (total.HasValue)
            {
                var start = (_currentPage - 1) * PageSize + 1;
                var end = Math.Min(start + versions.Count - 1, total.Value);
                LoadingText.Text = $"共 {total.Value} 个版本，显示 {start}-{end}";
            }
            else
            {
                // GitHub 无法知道总数
                LoadingText.Text = $"已加载 {versions.Count} 个版本";
            }

            PageInfoText.Text = $"第 {_currentPage} 页";
            UpdatePaginationState();
        });
    }

    /// <summary>
    /// 更新分页按钮状态
    /// </summary>
    private void UpdatePaginationState()
    {
        PrevPageButton.IsEnabled = _currentPage > 1;
        NextPageButton.IsEnabled = _hasNextPage;
    }

    private string ExtractVersionFromName(string name)
    {
        // 尝试从文件名提取版本号
        var match = System.Text.RegularExpressions.Regex.Match(name, @"(\d+\.\d+\.?\d*)");
        return match.Success ? match.Value : name;
    }

    private void VersionsListView_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (VersionsListView.SelectedItem is SmapiVersionInfo version)
        {
            SelectedVersion = version;
            InstallButton.IsEnabled = true;
        }
        else
        {
            SelectedVersion = null;
            InstallButton.IsEnabled = false;
        }
    }

    private void Install_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedVersion != null)
        {
            Log.Info($"[SmapiVersionPicker] 用户选择安装 SMAPI {SelectedVersion.Version} ({SelectedVersion.Source})");
            DialogResult = true;
            Close();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    /// <summary>
    /// 显示对话框并返回选择的版本
    /// </summary>
    public static SmapiVersionInfo? Show(Window owner, string targetPath, string gameBasePath)
    {
        var dialog = new SmapiVersionPickerDialog(targetPath, gameBasePath)
        {
            Owner = owner
        };

        // 应用模糊效果
        if (dialog.Owner is MainWindow mainWindow)
        {
            mainWindow.ApplyBlurEffect();
        }

        var result = dialog.ShowDialog();

        // 移除模糊效果
        if (dialog.Owner is MainWindow main)
        {
            main.RemoveBlurEffect();
        }

        if (result == true)
        {
            return dialog.SelectedVersion;
        }

        return null;
    }
}
