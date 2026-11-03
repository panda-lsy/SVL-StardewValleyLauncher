using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;

namespace SVL.Avalonia.Services;

/// <summary>使用游戏素材作为窗口默认指针，保留文本和窗口边缘等专用指针。</summary>
internal static class GameCursorService
{
    internal const string NormalCursorUri = "avares://SVL.Avalonia/Assets/Icons/Stardew/Cursor/Cursor.png";
    internal static readonly PixelPoint NormalCursorHotspot = new(0, 0);

    // Native cursors can retain bitmap resources; keep both alive for the application lifetime.
    private static readonly List<(Bitmap Bitmap, Cursor Cursor)> Resources = [];

    internal static bool TryInstall(Application application)
    {
        Bitmap? bitmap = null;
        Cursor? cursor = null;
        try
        {
            using var stream = AssetLoader.Open(new Uri(NormalCursorUri));
            bitmap = new Bitmap(stream);
            cursor = new Cursor(bitmap, NormalCursorHotspot);
            var style = new Style(selector => selector.OfType<Window>());
            style.Setters.Add(new Setter(InputElement.CursorProperty, cursor));
            application.Styles.Add(style);
            var popupStyle = new Style(selector => selector.OfType<PopupRoot>());
            popupStyle.Setters.Add(new Setter(InputElement.CursorProperty, cursor));
            application.Styles.Add(popupStyle);
            Resources.Add((bitmap, cursor));
            return true;
        }
        catch (Exception exception)
        {
            cursor?.Dispose();
            bitmap?.Dispose();
            Debug.WriteLine($"Unable to load the game cursor; using the system cursor: {exception.Message}");
            return false;
        }
    }
}
