using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SVL.Avalonia.Views;

public partial class DownloadPageView : UserControl
{
    public DownloadPageView()
    {
        InitializeComponent();
    }

    /// <summary>SteamCMD 日志实时滚动到底部，保证日志始终保持在底部显示。</summary>
    private void SteamCmdLogBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox box || string.IsNullOrEmpty(box.Text))
        {
            return;
        }

        try
        {
            // 把插入符移到末尾，TextBox 会把插入符（即末行）滚动到可视区。
            // 不能用 ScrollToLine(int.MaxValue)：会抛 ArgumentOutOfRangeException 导致 UI 崩溃。
            box.CaretIndex = box.Text.Length;
        }
        catch
        {
            // 兜底：滚动失败不影响使用
        }
    }
}
