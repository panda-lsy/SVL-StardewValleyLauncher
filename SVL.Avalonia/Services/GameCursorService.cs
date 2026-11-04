using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;

namespace SVL.Avalonia.Services;

/// <summary>使用游戏素材作为指针，保留文本输入和窗口边缘等系统专用指针。</summary>
internal static class GameCursorService
{
    internal const string NormalCursorUri = "avares://SVL.Avalonia/Assets/Icons/Stardew/Cursor/Cursor.png";
    internal const string HandCursorResource = "Svl.Cursor.Hand";
    internal const string TextCursorResource = "Svl.Cursor.Text";
    internal const string SearchCursorResource = "Svl.Cursor.Search";
    internal const string AddCursorResource = "Svl.Cursor.Add";
    internal const string FavoriteCursorResource = "Svl.Cursor.Favorite";
    internal const int CursorScale = 2;
    internal static readonly PixelPoint NormalCursorHotspot = new(0, 0);

    // Native cursors can retain bitmap resources; keep both alive for the application lifetime.
    private static readonly List<(Bitmap Bitmap, Cursor Cursor)> Resources = [];
    private static readonly List<Cursor> NativeResources = [];
    private static readonly HashSet<Application> InstalledApplications = [];

    internal static bool TryInstall(Application application)
    {
        if (InstalledApplications.Contains(application)) return true;

        var prepared = new List<(Bitmap Bitmap, Cursor Cursor)>();
        Cursor? textCursor = null;
        try
        {
            var normal = CreateCursor(NormalCursorUri, NormalCursorHotspot, prepared);
            var hand = CreateCursor("avares://SVL.Avalonia/Assets/Icons/Stardew/Cursor/Cursor_Hand.png", new PixelPoint(1, 0), prepared);
            var search = CreateCursor("avares://SVL.Avalonia/Assets/Icons/Stardew/Cursor/Cursor_Info.png", new PixelPoint(4, 4), prepared);
            var add = CreateCursor("avares://SVL.Avalonia/Assets/Icons/Stardew/Cursor/Cursor_Add.png", new PixelPoint(0, 0), prepared);
            var favorite = CreateCursor("avares://SVL.Avalonia/Assets/Icons/Stardew/Cursor/Cursor_Heart.png", new PixelPoint(0, 0), prepared);
            textCursor = new Cursor(StandardCursorType.Ibeam);

            application.Resources[HandCursorResource] = hand;
            application.Resources[SearchCursorResource] = search;
            application.Resources[AddCursorResource] = add;
            application.Resources[FavoriteCursorResource] = favorite;
            application.Resources[TextCursorResource] = textCursor;

            var style = new Style(selector => selector.OfType<Window>());
            style.Setters.Add(new Setter(InputElement.CursorProperty, normal));
            application.Styles.Add(style);
            var popupStyle = new Style(selector => selector.OfType<PopupRoot>());
            popupStyle.Setters.Add(new Setter(InputElement.CursorProperty, normal));
            application.Styles.Add(popupStyle);
            AddCursorStyle<Button>(application, hand);
            AddCursorStyle<ToggleButton>(application, hand);
            AddCursorStyle<CheckBox>(application, hand);
            AddCursorStyle<RadioButton>(application, hand);
            AddCursorStyle<ComboBox>(application, hand);
            AddCursorStyle<ListBoxItem>(application, hand);
            AddCursorStyle<MenuItem>(application, hand);
            AddCursorStyle<TextBox>(application, textCursor);
            Resources.AddRange(prepared);
            NativeResources.Add(textCursor);
            InstalledApplications.Add(application);
            return true;
        }
        catch (Exception exception)
        {
            textCursor?.Dispose();
            foreach (var (bitmap, cursor) in prepared)
            {
                cursor.Dispose();
                bitmap.Dispose();
            }
            // 显式引用的动态资源仍应退回系统手形，避免素材加载失败时失去点击提示。
            try
            {
                var fallback = new Cursor(StandardCursorType.Hand);
                application.Resources[HandCursorResource] = fallback;
                application.Resources[SearchCursorResource] = fallback;
                application.Resources[AddCursorResource] = fallback;
                application.Resources[FavoriteCursorResource] = fallback;
            }
            catch { /* 平台无法创建任何指针时保留系统默认值。 */ }
            global::SVL.Core.Platform.Diagnostics.RuntimeDebugLog.WriteLine($"Unable to load the game cursor; using the system cursor: {exception.Message}");
            return false;
        }
    }

    internal static Bitmap LoadScaledBitmap(string uri)
    {
        using var stream = AssetLoader.Open(new Uri(uri));
        using var original = new Bitmap(stream);
        return original.CreateScaledBitmap(
            new PixelSize(original.PixelSize.Width * CursorScale, original.PixelSize.Height * CursorScale),
            BitmapInterpolationMode.None);
    }

    private static Cursor CreateCursor(string uri, PixelPoint hotspot, List<(Bitmap Bitmap, Cursor Cursor)> resources)
    {
        var bitmap = LoadScaledBitmap(uri);
        try
        {
            var cursor = new Cursor(bitmap, new PixelPoint(hotspot.X * CursorScale, hotspot.Y * CursorScale));
            resources.Add((bitmap, cursor));
            return cursor;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static void AddCursorStyle<T>(Application application, Cursor cursor) where T : InputElement
    {
        var style = new Style(selector => selector.OfType<T>());
        style.Setters.Add(new Setter(InputElement.CursorProperty, cursor));
        application.Styles.Add(style);
    }
}
