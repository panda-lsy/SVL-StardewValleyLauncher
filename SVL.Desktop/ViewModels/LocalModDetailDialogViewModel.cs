using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SVL.Core.Logging;
using SVL.Core.Stardew.Mod;
using SVL.Desktop.Controls;

namespace SVL.Desktop.ViewModels;

public partial class LocalModDetailDialogViewModel : ObservableObject
{
    private readonly SdVMod _mod;
    private readonly Func<ModDependencyLink, Task>? _navigateDependencyAsync;

    [ObservableProperty]
    private string _modName = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private string _version = string.Empty;

    [ObservableProperty]
    private string _author = string.Empty;

    [ObservableProperty]
    private string _uniqueId = string.Empty;

    [ObservableProperty]
    private string _sourceFileName = string.Empty;

    [ObservableProperty]
    private string _modPath = string.Empty;

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private string _isEnabledBackground = "#D2A679";

    [ObservableProperty]
    private string _isEnabledText = "已启用";

    [ObservableProperty]
    private bool _hasUpdate;

    [ObservableProperty]
    private bool _canOpenFolder = true;

    [ObservableProperty]
    private bool _hasDependencies;

    public ObservableCollection<ModDependencyLink> Dependencies { get; } = new();

    public event EventHandler? RequestClose;

    public LocalModDetailDialogViewModel(SdVMod mod, Func<ModDependencyLink, Task>? navigateDependencyAsync = null)
    {
        _mod = mod ?? throw new ArgumentNullException(nameof(mod));
        _navigateDependencyAsync = navigateDependencyAsync;

        // 加载MOD信息
        LoadModInfo();
    }

    private void LoadModInfo()
    {
        ModName = _mod.Name ?? "未知MOD";
        Description = _mod.Description ?? "无描述";
        Version = _mod.Version ?? "未知版本";
        Author = _mod.Author ?? "未知作者";
        UniqueId = _mod.UniqueId ?? "无";
        SourceFileName = !string.IsNullOrEmpty(_mod.SourceFileName) ? _mod.SourceFileName : "无";
        ModPath = _mod.ModPath ?? "无";
        IsEnabled = _mod.IsEnabled;
        HasUpdate = _mod.HasUpdate;

        // 更新启用状态显示
        UpdateEnabledStatus();

        // 加载依赖项
        if (_mod.DisplayDependencies != null && _mod.DisplayDependencies.Count > 0)
        {
            HasDependencies = true;
            foreach (var dep in _mod.DisplayDependencies)
            {
                Dependencies.Add(dep);
            }
        }
        else if (_mod.DependencyDetails != null && _mod.DependencyDetails.Count > 0)
        {
            HasDependencies = true;
            foreach (var dep in _mod.DependencyDetails)
            {
                Dependencies.Add(new ModDependencyLink
                {
                    UniqueId = dep.UniqueId ?? string.Empty,
                    DisplayName = dep.UniqueId ?? string.Empty,
                    MinimumVersion = dep.MinimumVersion ?? string.Empty,
                    IsRequired = dep.IsRequired
                });
            }
        }
        else
        {
            HasDependencies = false;
        }

        // 检查是否可以打开文件夹
        CanOpenFolder = !string.IsNullOrEmpty(_mod.ModPath) && System.IO.Directory.Exists(_mod.ModPath);
    }

    private void UpdateEnabledStatus()
    {
        if (IsEnabled)
        {
            IsEnabledBackground = "#D2A679";
            IsEnabledText = "已启用";
        }
        else
        {
            IsEnabledBackground = "#9E9E9E";
            IsEnabledText = "已禁用";
        }
    }

    [RelayCommand]
    private void Close()
    {
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (!string.IsNullOrEmpty(_mod.ModPath) && System.IO.Directory.Exists(_mod.ModPath))
        {
            try
            {
                Process.Start("explorer.exe", _mod.ModPath);
            }
            catch (Exception ex)
            {
                SvlMessageBox.Error($"无法打开文件夹：{ex.Message}");
            }
        }
    }

    [RelayCommand]
    private void OpenDependency(ModDependencyLink dependency)
    {
        if (dependency == null || _navigateDependencyAsync == null)
        {
            return;
        }

        RequestClose?.Invoke(this, EventArgs.Empty);

        Application.Current?.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                await _navigateDependencyAsync(dependency);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[LocalModDetailDialog] 跳转前置 Mod 失败");
                SvlMessageBox.Error($"无法打开前置 Mod：{ex.Message}");
            }
        }));
    }
}
