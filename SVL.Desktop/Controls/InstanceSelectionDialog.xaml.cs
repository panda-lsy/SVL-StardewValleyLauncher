using System.Collections.Generic;
using System.Linq;
using System.Windows;
using SVL.Core.Stardew.Instance;

namespace SVL.Desktop.Controls;

public partial class InstanceSelectionDialog : Window
{
    public GamePathInfo? SelectedInstance { get; private set; }

    public InstanceSelectionDialog(IEnumerable<GamePathInfo> instances, string title, string subtitle)
    {
        InitializeComponent();

        TitleTextBlock.Text = title;
        SubtitleTextBlock.Text = subtitle;

        InstancesListBox.ItemsSource = instances?.ToList() ?? new List<GamePathInfo>();
        InstancesListBox.SelectedIndex = 0;
        Loaded += (_, _) => InstancesListBox.Focus();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (InstancesListBox.SelectedItem is not GamePathInfo instance)
        {
            SvlMessageBox.Warning("请选择一个实例。", "未选择实例");
            return;
        }

        SelectedInstance = instance;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}