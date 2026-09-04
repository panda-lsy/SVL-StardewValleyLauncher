using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SVL.Avalonia.Services;
using System.Collections.ObjectModel;
using System.Text;

namespace SVL.Avalonia.ViewModels;

public partial class DebugConsoleViewModel : ObservableObject
{
    private readonly DebugConsoleService _consoleService;

    /// <summary>全部日志条目（不过滤）。</summary>
    public ObservableCollection<DebugLogEntry> AllLogEntries { get; } = [];

    /// <summary>过滤后显示的日志条目（受级别按钮控制）。</summary>
    public ObservableCollection<DebugLogEntry> FilteredLogEntries { get; } = [];

    [ObservableProperty]
    private string _logsText = string.Empty;

    [ObservableProperty]
    private string _statusMessage = "点击级别按钮切换显示，选中态显示该级别，未选中则过滤。";

    /// <summary>DEBUG 级别是否显示。</summary>
    [ObservableProperty]
    private bool _showDebug = true;

    /// <summary>INFO 级别是否显示。</summary>
    [ObservableProperty]
    private bool _showInfo = true;

    /// <summary>WARN 级别是否显示。</summary>
    [ObservableProperty]
    private bool _showWarn = true;

    /// <summary>ERROR 级别是否显示。</summary>
    [ObservableProperty]
    private bool _showError = true;

    public DebugConsoleViewModel(DebugConsoleService consoleService)
    {
        _consoleService = consoleService;
        _consoleService.LineAdded += HandleLineAdded;
        _consoleService.Cleared += HandleCleared;

        foreach (var entry in _consoleService.Snapshot())
        {
            AllLogEntries.Add(entry);
        }

        RefreshFiltered();
    }

    partial void OnShowDebugChanged(bool value) => RefreshFiltered();
    partial void OnShowInfoChanged(bool value) => RefreshFiltered();
    partial void OnShowWarnChanged(bool value) => RefreshFiltered();
    partial void OnShowErrorChanged(bool value) => RefreshFiltered();

    /// <summary>切换 DEBUG 级别显示状态。</summary>
    [RelayCommand]
    private void ToggleDebug() => ShowDebug = !ShowDebug;

    /// <summary>切换 INFO 级别显示状态。</summary>
    [RelayCommand]
    private void ToggleInfo() => ShowInfo = !ShowInfo;

    /// <summary>切换 WARN 级别显示状态。</summary>
    [RelayCommand]
    private void ToggleWarn() => ShowWarn = !ShowWarn;

    /// <summary>切换 ERROR 级别显示状态。</summary>
    [RelayCommand]
    private void ToggleError() => ShowError = !ShowError;

    [RelayCommand]
    private void Clear()
    {
        _consoleService.Clear();
        _consoleService.Append("Logs cleared.");
        StatusMessage = "日志已清空。";
    }

    [RelayCommand]
    private void Export()
    {
        if (FilteredLogEntries.Count == 0)
        {
            StatusMessage = "当前没有可导出的日志。";
            return;
        }

        try
        {
            var exportDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SVL",
                "Avalonia",
                "Logs");
            Directory.CreateDirectory(exportDir);

            var filePath = Path.Combine(exportDir, $"debug-console-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(filePath, LogsText, Encoding.UTF8);
            StatusMessage = $"已导出日志: {filePath}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"导出失败: {ex.Message}";
        }
    }

    private void HandleLineAdded(DebugLogEntry entry)
    {
        AllLogEntries.Add(entry);
        while (AllLogEntries.Count > 800)
        {
            AllLogEntries.RemoveAt(0);
        }

        if (IsLevelVisible(entry.Level))
        {
            FilteredLogEntries.Add(entry);
            while (FilteredLogEntries.Count > 800)
            {
                FilteredLogEntries.RemoveAt(0);
            }
        }

        RebuildLogsText();
    }

    private void HandleCleared()
    {
        AllLogEntries.Clear();
        FilteredLogEntries.Clear();
        RebuildLogsText();
    }

    /// <summary>根据级别按钮状态重新过滤全部日志。</summary>
    private void RefreshFiltered()
    {
        FilteredLogEntries.Clear();
        foreach (var entry in AllLogEntries)
        {
            if (IsLevelVisible(entry.Level))
            {
                FilteredLogEntries.Add(entry);
            }
        }

        RebuildLogsText();
    }

    private bool IsLevelVisible(string level)
    {
        return level.ToUpperInvariant() switch
        {
            "DEBUG" => ShowDebug,
            "INFO" => ShowInfo,
            "WARN" => ShowWarn,
            "ERROR" => ShowError,
            _ => true
        };
    }

    private void RebuildLogsText()
    {
        LogsText = string.Join(Environment.NewLine, FilteredLogEntries.Select(e => e.Text));
    }
}
