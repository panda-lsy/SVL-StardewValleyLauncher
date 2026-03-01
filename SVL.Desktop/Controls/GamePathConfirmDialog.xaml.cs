using System.Windows;

namespace SVL.Desktop.Controls;

/// <summary>
/// GamePathConfirmDialog.xaml 的交互逻辑
/// </summary>
public partial class GamePathConfirmDialog : Window
{
    public string? GamePath { get; private set; }

    /// <summary>
    /// 用户是否选择了新路径
    /// </summary>
    public bool UserSelectedNewPath { get; private set; }

    public GamePathConfirmDialog()
    {
        InitializeComponent();
        UserSelectedNewPath = false;
    }

    public void SetGamePath(string path)
    {
        GamePath = path;
        PathTextBlock.Text = path;
    }

    /// <summary>
    /// 获取选择的游戏路径
    /// </summary>
    public string? GetSelectedPath()
    {
        return GamePath;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(GamePath))
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
            // 用户选择了新路径
            GamePath = pathDialog.SelectedPath;
            PathTextBlock.Text = pathDialog.SelectedPath;
            UserSelectedNewPath = true;

            // 保存新路径配置
            SVL.Core.Config.GamePathConfig.SaveGamePath(pathDialog.SelectedPath);
        }
        // else: 用户取消了选择，对话框保持打开状态
    }
}
