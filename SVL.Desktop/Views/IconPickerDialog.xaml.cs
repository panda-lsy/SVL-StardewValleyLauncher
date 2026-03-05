using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SVL.Desktop.Views;

/// <summary>
/// 图标选项模型
/// </summary>
public class IconOption
{
    public string Name { get; set; } = string.Empty;
    public string IconPath { get; set; } = string.Empty;
    public bool IsCustom { get; set; }
}

/// <summary>
/// 图标选择对话框
/// </summary>
public partial class IconPickerDialog : Window
{
    public ObservableCollection<IconOption> AvailableIcons { get; } = new();
    public string? SelectedIcon { get; private set; }

    public IconPickerDialog()
    {
        InitializeComponent();
        DataContext = this;
        InitializeIcons();
    }

    private void InitializeIcons()
    {
        AvailableIcons.Add(new IconOption { Name = "祝尼魔", IconPath = "/Images/Junimo.png", IsCustom = false });
        AvailableIcons.Add(new IconOption { Name = "绿色祝尼魔", IconPath = "/Images/Junimo2.png", IsCustom = false });
        AvailableIcons.Add(new IconOption { Name = "河豚小鸡", IconPath = "/Images/Modded.png", IsCustom = false });
        AvailableIcons.Add(new IconOption { Name = "经典小鸡", IconPath = "/Images/Vanilla.png", IsCustom = false });
        AvailableIcons.Add(new IconOption { Name = "+", IconPath = "/Images/Junimo.png", IsCustom = true });
    }

    [RelayCommand]
    private void SelectIcon(IconOption option)
    {
        if (option.IsCustom)
        {
            // 打开文件选择对话框
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择图标文件",
                Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.ico;*.gif|所有文件|*.*",
                CheckFileExists = true,
                CheckPathExists = true
            };

            if (dialog.ShowDialog() == true)
            {
                SelectedIcon = dialog.FileName;
                DialogResult = true;
                Close();
            }
        }
        else
        {
            SelectedIcon = option.IconPath;
            DialogResult = true;
            Close();
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        SelectedIcon = null;
        DialogResult = false;
        Close();
    }
}
