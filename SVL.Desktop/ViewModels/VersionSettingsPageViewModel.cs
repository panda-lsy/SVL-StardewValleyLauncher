using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SVL.Core.Stardew.Instance;

namespace SVL.Desktop.ViewModels;

/// <summary>
/// 版本设置页面的左侧导航项
/// </summary>
public enum VersionSettingsPageType
{
    Overview,     // 概览
    AutoInstall,  // 自动安装
    Settings,     // 设置
    ModManage,    // Mod管理
    Export        // 导出
}

/// <summary>
/// 导航项模型
/// </summary>
public class NavigationItem
{
    public string Name { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
    public VersionSettingsPageType PageType { get; set; }
}

/// <summary>
/// 版本设置页面的 ViewModel（管理整个页面）
/// </summary>
public partial class VersionSettingsViewModel : ObservableObject
{
    private MainWindowViewModel _mainViewModel;
    private VersionSettingsRightViewModel? _rightContentStatusSource;

    /// <summary>
    /// 当前正在配置的实例
    /// </summary>
    [ObservableProperty]
    private GamePathInfo? _currentInstance;

    /// <summary>
    /// 当前选中的左侧导航项
    /// </summary>
    [ObservableProperty]
    private VersionSettingsPageType _selectedPage = VersionSettingsPageType.Overview;

    /// <summary>
    /// 当前选中的导航项对象（用于 ListBox 绑定）
    /// </summary>
    [ObservableProperty]
    private NavigationItem? _selectedNavigationItem;

    /// <summary>
    /// 左侧导航项列表
    /// </summary>
    public ObservableCollection<NavigationItem> NavigationItems { get; } = new();

    /// <summary>
    /// 右侧内容区域的 ViewModel
    /// </summary>
    [ObservableProperty]
    private ObservableObject? _rightContent;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    partial void OnSelectedPageChanged(VersionSettingsPageType value)
    {
        UpdateNavigationItems();
        UpdateRightContent();
    }

    partial void OnSelectedNavigationItemChanged(NavigationItem? value)
    {
        if (value != null)
        {
            SelectedPage = value.PageType;
        }
    }

    partial void OnCurrentInstanceChanged(GamePathInfo? value)
    {
        UpdateRightContent();
    }

    private void UpdateNavigationItems()
    {
        System.Diagnostics.Debug.WriteLine($"[VersionSettings] UpdateNavigationItems called, SelectedPage={SelectedPage}");

        // 同步 SelectedNavigationItem
        SelectedNavigationItem = NavigationItems.FirstOrDefault(i => i.PageType == SelectedPage);

        if (SelectedNavigationItem != null)
        {
            System.Diagnostics.Debug.WriteLine($"[VersionSettings] Set SelectedNavigationItem={SelectedNavigationItem.Name}");
        }
    }

    private void UpdateRightContent()
    {
        if (CurrentInstance == null)
        {
            DetachRightContentStatusSource();
            StatusMessage = string.Empty;
            RightContent = null;
            return;
        }

        // 根据左侧选择的页面创建对应的 ViewModel
        switch (SelectedPage)
        {
            case VersionSettingsPageType.Overview:
                RightContent = new VersionSettingsRightViewModel(_mainViewModel, CurrentInstance);
                break;
            case VersionSettingsPageType.AutoInstall:
                // 创建自动安装页面的 ViewModel
                var autoInstallVm = new VersionSettingsRightViewModel(_mainViewModel, CurrentInstance);
                autoInstallVm.CurrentPage = VersionSettingsPage.AutoInstall;
                RightContent = autoInstallVm;
                break;
            case VersionSettingsPageType.Settings:
                RightContent = new InstanceSettingsViewModel(_mainViewModel, CurrentInstance);
                break;
            case VersionSettingsPageType.ModManage:
                // 创建 Mod管理页面的 ViewModel
                var viewModel = new VersionSettingsRightViewModel(_mainViewModel, CurrentInstance);
                viewModel.CurrentPage = VersionSettingsPage.ModManagement;
                RightContent = viewModel;
                break;
            case VersionSettingsPageType.Export:
                RightContent = new ExportViewModel(_mainViewModel, CurrentInstance);
                break;
        }

        AttachRightContentStatusSource();
    }

    private void AttachRightContentStatusSource()
    {
        DetachRightContentStatusSource();

        _rightContentStatusSource = RightContent as VersionSettingsRightViewModel;
        if (_rightContentStatusSource != null)
        {
            StatusMessage = _rightContentStatusSource.StatusMessage;
            _rightContentStatusSource.PropertyChanged += OnRightContentPropertyChanged;
        }
        else
        {
            StatusMessage = string.Empty;
        }
    }

    private void DetachRightContentStatusSource()
    {
        if (_rightContentStatusSource != null)
        {
            _rightContentStatusSource.PropertyChanged -= OnRightContentPropertyChanged;
            _rightContentStatusSource = null;
        }
    }

    private void OnRightContentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VersionSettingsRightViewModel.StatusMessage) && sender is VersionSettingsRightViewModel vm)
        {
            StatusMessage = vm.StatusMessage;
        }
    }

    public VersionSettingsViewModel(MainWindowViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;

        // 初始化导航项
        NavigationItems.Add(new NavigationItem { Name = "概览", Icon = "📊", PageType = VersionSettingsPageType.Overview });
        NavigationItems.Add(new NavigationItem { Name = "自动安装", Icon = "⬇️", PageType = VersionSettingsPageType.AutoInstall });
        NavigationItems.Add(new NavigationItem { Name = "设置", Icon = "⚙️", PageType = VersionSettingsPageType.Settings });
        NavigationItems.Add(new NavigationItem { Name = "Mod管理", Icon = "📦", PageType = VersionSettingsPageType.ModManage });
        NavigationItems.Add(new NavigationItem { Name = "导出", Icon = "📤", PageType = VersionSettingsPageType.Export });

        // 从 MainWindowViewModel 获取当前选中的实例
        var launchLeftVm = mainViewModel.LeftPanelContent as LaunchLeftViewModel;
        if (launchLeftVm != null && launchLeftVm.SelectedGamePath != null)
        {
            CurrentInstance = launchLeftVm.SelectedGamePath;
        }

        // 如果没有找到，尝试使用 SelectedVersionSettingsInstance
        if (CurrentInstance == null && mainViewModel.SelectedVersionSettingsInstance != null)
        {
            CurrentInstance = mainViewModel.SelectedVersionSettingsInstance;
        }

        // 初始化选中项和右侧内容
        UpdateNavigationItems();
        UpdateRightContent();
    }

    /// <summary>
    /// 导航到指定页面
    /// </summary>
    [RelayCommand]
    private void NavigateToPage(NavigationItem item)
    {
        SelectedPage = item.PageType;
    }

    /// <summary>
    /// 返回启动页面
    /// </summary>
    [RelayCommand]
    private void NavigateBack()
    {
        _mainViewModel.NavigateToLaunchCommand.Execute(null);
    }
}
