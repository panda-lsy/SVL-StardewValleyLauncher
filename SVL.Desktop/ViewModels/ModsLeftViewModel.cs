using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SVL.Core.Stardew.Mod;
using SVL.Core.Stardew.Mod.SMAPI;
using SVL.Core.Stardew.Instance;
using SVL.Core.Logging;
using SVL.Desktop.Controls;

namespace SVL.Desktop.ViewModels;

public partial class ModsLeftViewModel : ObservableObject
{
    private MainWindowViewModel _mainViewModel;

    public ModsLeftViewModel(MainWindowViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;
    }

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private List<SdVMod> _filteredMods = [];

    [ObservableProperty]
    private SdVMod? _selectedMod;

    partial void OnSearchTextChanged(string value)
    {
        FilterMods();
    }

    private void FilterMods()
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            FilteredMods = new List<SdVMod>(_mainViewModel.Mods);
        }
        else
        {
            FilteredMods = _mainViewModel.Mods
                .Where(m => m.Name.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) >= 0 ||
                             (m.Author != null && m.Author.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) >= 0))
                .ToList();
        }
    }

    [RelayCommand]
    private void SelectMod(SdVMod mod)
    {
        SelectedMod = mod;
    }

    [RelayCommand]
    private void InstallMod()
    {
    }

    [ObservableProperty]
    private bool _isInstallingSmapi;

    [ObservableProperty]
    private string _smapiInstallStatus = "";

    [RelayCommand]
    private async Task InstallSMAPIAsync()
    {
        Log.Info("[ModsLeftViewModel] InstallSMAPIAsync 开始执行");
        
        // 获取当前选中的实例
        var currentInstance = _mainViewModel.SelectedInstance;
        if (currentInstance == null)
        {
            Log.Warn("[ModsLeftViewModel] 未选择实例");
            SvlMessageBox.Info("请先选择一个游戏实例", "提示");
            return;
        }

        Log.Info($"[ModsLeftViewModel] 当前实例路径：{currentInstance.Path}");
        Log.Info("[ModsLeftViewModel] 准备显示游戏路径确认对话框");

        // 显示游戏路径确认对话框
        var dialog = new GamePathConfirmDialog
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        dialog.SetGamePath(currentInstance.Path);
        
        Log.Info("[ModsLeftViewModel] 对话框已显示，等待用户操作...");
        
        if (dialog.ShowDialog() != true)
        {
            Log.Info("[ModsLeftViewModel] 用户取消了 SMAPI 安装");
            return;
        }

        var gamePath = dialog.GetSelectedPath();
        if (string.IsNullOrEmpty(gamePath))
        {
            Log.Warn("[ModsLeftViewModel] 用户未选择有效的游戏路径");
            return;
        }

        Log.Info($"[ModsLeftViewModel] 用户选择的游戏路径：{gamePath}");

        // 检查是否已安装 SMAPI
        var alreadyInstalled = await SmapApiService.CheckInstalledVersionAsync(gamePath);
        if (alreadyInstalled)
        {
            if (!SvlMessageBox.Confirm(
                "SMAPI 已经安装，是否要重新安装？\n\n这会覆盖现有的 SMAPI 文件。",
                "确认"))
            {
                return;
            }
        }

        // 显示安装状态
        IsInstallingSmapi = true;
        SmapiInstallStatus = "正在获取最新版本信息...";

        // 异步安装
        var success = await Task.Run(async () =>
        {
            try
            {
                // 获取最新版本
                var latestVersion = await SmapApiService.GetLatestVersionAsync();
                if (string.IsNullOrEmpty(latestVersion))
                {
                    SmapiInstallStatus = "获取版本信息失败";
                    return false;
                }

                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    SmapiInstallStatus = $"正在下载 SMAPI {latestVersion}...";
                });

                // 安装 SMAPI
                var installed = await SmapApiService.InstallAsync(gamePath, "latest");

                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    SmapiInstallStatus = installed ? "安装完成！" : "安装失败";
                });

                return installed;
            }
            catch (Exception ex)
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    SmapiInstallStatus = $"安装失败: {ex.Message}";
                });
                return false;
            }
        });

        IsInstallingSmapi = false;

        // 显示结果
        if (success)
        {
            SvlMessageBox.Success("SMAPI 安装成功！\n\n现在可以使用 SMAPI 启动游戏了。");
        }
        else
        {
            SvlMessageBox.Error("SMAPI 安装失败，请查看日志了解详情。");
        }
    }

    /// <summary>
    /// 搜索 Nexus MOD（打开 MOD 搜索页面）
    /// </summary>
    [RelayCommand]
    private void SearchNexus()
    {
        // 导航到下载页面的 Mods 子页面
        if (System.Windows.Application.Current.MainWindow is MainWindow mainWindow &&
            mainWindow.DataContext is MainWindowViewModel mainViewModel)
        {
            mainViewModel.CurrentPage = PageType.Download;
            mainViewModel.CurrentDownloadSubPage = DownloadSubPageType.Mods;

            // 更新右侧面板
            mainViewModel.UpdateDownloadRightPanel();
        }
    }
}
