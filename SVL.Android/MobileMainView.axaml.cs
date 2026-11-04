using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using SVL.Core.Platform.IO;
using Button = Avalonia.Controls.Button;

namespace SVL.Android;

public sealed partial class MobileMainView : UserControl
{
    private TextBlock LibraryStatusControl => this.FindControl<TextBlock>("LibraryStatus")!;
    private TextBlock ActionStatusControl => this.FindControl<TextBlock>("ActionStatus")!;
    private Button ImportButtonControl => this.FindControl<Button>("ImportButton")!;

    private static string InstancesRoot => Path.Combine(
        global::Android.App.Application.Context.FilesDir?.AbsolutePath
            ?? throw new IOException("无法访问应用存储空间。"), "games");

    public MobileMainView()
    {
        AvaloniaXamlLoader.Load(this);
        ImportButtonControl.Click += ImportGame;
        RefreshLibrary();
    }

    private void RefreshLibrary()
    {
        try
        {
            var root = InstancesRoot;
            var count = Directory.Exists(root)
                ? Directory.EnumerateDirectories(root, "game-*").Count(path => File.Exists(Path.Combine(path, "Stardew Valley.dll")))
                : 0;
            LibraryStatusControl.Text = $"已导入 {count} 个游戏文件实例。";
        }
        catch (IOException)
        {
            LibraryStatusControl.Text = "无法读取游戏文件实例。";
        }
    }

    private async void ImportGame(object? sender, RoutedEventArgs args)
    {
        ImportButtonControl.IsEnabled = false;
        ActionStatusControl.Text = string.Empty;
        try
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage is null || !storage.CanOpen)
                throw new IOException("当前设备无法打开文件选择器。");

            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择游戏 ZIP",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("ZIP 压缩包")
                {
                    Patterns = ["*.zip"],
                    MimeTypes = ["application/zip", "application/x-zip-compressed", "application/octet-stream"]
                }]
            });
            if (files.Count == 0) return;

            using var file = files[0];
            await using var source = await file.OpenReadAsync();
            var progress = new Progress<string>(message => ActionStatusControl.Text = message);
            var root = InstancesRoot;
            await Task.Run(() => GameArchiveImporter.ImportZipAsync(source, root, progress));
            RefreshLibrary();
            ActionStatusControl.Text = "游戏文件导入完成；独立运行环境仍在开发中。";
        }
        catch (Exception ex)
        {
            ActionStatusControl.Text = "导入失败：" + ex.Message;
        }
        finally
        {
            ImportButtonControl.IsEnabled = true;
        }
    }
}
