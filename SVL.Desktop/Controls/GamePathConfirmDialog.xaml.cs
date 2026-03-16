using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using SVL.Core.Config;
using SVL.Core.Stardew.Instance;

namespace SVL.Desktop.Controls;

/// <summary>
/// GamePathConfirmDialog.xaml 的交互逻辑
/// </summary>
public partial class GamePathConfirmDialog : Window
{
    private readonly List<BasePathOption> _availablePaths = new();

    public string? GamePath { get; private set; }

    /// <summary>
    /// 用户是否选择了新路径
    /// </summary>
    public bool UserSelectedNewPath { get; private set; }

    public GamePathConfirmDialog()
    {
        InitializeComponent();
        UserSelectedNewPath = false;
        LoadConfiguredPaths();
    }

    public void SetGamePath(string path)
    {
        AddPathOption(path, true);
        UpdateSelectedPath(path);
    }

    /// <summary>
    /// 获取选择的游戏路径
    /// </summary>
    public string? GetSelectedPath()
    {
        return GamePath;
    }

    private void LoadConfiguredPaths()
    {
        var knownPaths = new List<string>();
        var savedPath = GamePathConfig.GetGamePath();
        if (!string.IsNullOrWhiteSpace(savedPath))
        {
            knownPaths.Add(savedPath);
        }

        knownPaths.AddRange(SettingsService.LoadInstances()
            .Select(instance => instance.GamePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>());

        foreach (var path in knownPaths)
        {
            AddPathOption(path, false);
        }

        UpdateSelectorVisibility();
        if (string.IsNullOrWhiteSpace(GamePath) && _availablePaths.Count > 0)
        {
            UpdateSelectedPath(_availablePaths[0].Path);
        }
    }

    private void AddPathOption(string? path, bool prioritize)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        path = path.Trim();
        var existing = _availablePaths.FirstOrDefault(option => string.Equals(option.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            if (prioritize)
            {
                BasePathComboBox.SelectedItem = existing;
            }

            return;
        }

        var displayText = path;

        var option = new BasePathOption(path, displayText);
        if (prioritize)
        {
            _availablePaths.Insert(0, option);
        }
        else
        {
            _availablePaths.Add(option);
        }

        BasePathComboBox.ItemsSource = null;
        BasePathComboBox.ItemsSource = _availablePaths;
        UpdateSelectorVisibility();
    }

    private void UpdateSelectorVisibility()
    {
        BasePathSelectorPanel.Visibility = Visibility.Visible;
    }

    private void UpdateSelectedPath(string? path)
    {
        GamePath = string.IsNullOrWhiteSpace(path) ? null : path;
        BasePathComboBox.Text = GamePath ?? string.Empty;

        var selected = _availablePaths.FirstOrDefault(option => string.Equals(option.Path, GamePath, StringComparison.OrdinalIgnoreCase));
        if (selected != null && !Equals(BasePathComboBox.SelectedItem, selected))
        {
            BasePathComboBox.SelectedItem = selected;
        }

        ValidateSelectedPath();
    }

    private bool ValidateSelectedPath()
    {
        if (string.IsNullOrWhiteSpace(GamePath))
        {
            ValidationTextBlock.Text = "请先选择有效的游戏目录。";
            ValidationTextBlock.Visibility = Visibility.Visible;
            ConfirmButton.IsEnabled = false;
            return false;
        }

        if (!GamePathService.IsValidGamePath(GamePath))
        {
            ValidationTextBlock.Text = "当前 BASE 不包含可识别的游戏核心文件（如 Stardew Valley.dll），请重新选择。";
            ValidationTextBlock.Visibility = Visibility.Visible;
            ConfirmButton.IsEnabled = false;
            return false;
        }

        ValidationTextBlock.Visibility = Visibility.Collapsed;
        ConfirmButton.IsEnabled = true;
        return true;
    }

    private void BasePathComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BasePathComboBox.SelectedItem is BasePathOption option)
        {
            UpdateSelectedPath(option.Path);
        }
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (ValidateSelectedPath())
        {
            DialogResult = true;
            Close();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        // 用户取消安装
        DialogResult = false;
        Close();
    }

    private void SelectNew_Click(object sender, RoutedEventArgs e)
    {
        // 显示路径选择对话框
        var pathDialog = new GamePathSelectionDialog();
        pathDialog.Owner = this.Owner;
        var pathResult = pathDialog.ShowDialog();

        if (pathResult == true && !string.IsNullOrEmpty(pathDialog.SelectedPath))
        {
            AddPathOption(pathDialog.SelectedPath, true);
            UpdateSelectedPath(pathDialog.SelectedPath);
            UserSelectedNewPath = true;

            // 保存新路径配置
            SVL.Core.Config.GamePathConfig.SaveGamePath(pathDialog.SelectedPath);
        }
        // else: 用户取消了选择，对话框保持打开状态
    }

    private sealed class BasePathOption
    {
        public BasePathOption(string path, string displayText)
        {
            Path = path;
            DisplayText = displayText;
        }

        public string Path { get; }

        public string DisplayText { get; }

        public override string ToString()
        {
            return DisplayText;
        }
    }
}
