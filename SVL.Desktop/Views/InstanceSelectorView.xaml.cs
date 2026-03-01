using System.Windows.Controls;
using System.Windows;
using System.Windows.Input;
using SVL.Desktop.ViewModels;

namespace SVL.Desktop.Views;

public partial class InstanceSelectorView : UserControl
{
    public InstanceSelectorView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 收藏按钮点击事件 - 阻止事件冒泡到父按钮
    /// </summary>
    private void FavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        // 阻止事件冒泡，避免触发父按钮的 SelectInstance 命令
        e.Handled = true;
    }
}
