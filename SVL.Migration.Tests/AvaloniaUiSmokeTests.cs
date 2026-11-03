using System.Buffers.Binary;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Animation;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using AvaloniaPath = Avalonia.Controls.Shapes.Path;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SVL.Avalonia;
using SVL.Avalonia.Controls;
using SVL.Avalonia.Models;
using SVL.Avalonia.Services;

[assembly: AvaloniaTestApplication(typeof(App))]

namespace SVL.Migration.Tests;

/// <summary>
/// 通过 Avalonia Headless 验证真实主窗口的结构约束。
/// 这类测试不替代 Windows 截图验收，但可以在 CI 中尽早发现 XAML 加载和布局回归。
/// </summary>
[TestClass]
public sealed class AvaloniaUiSmokeTests
{
    [TestMethod]
    public void GameCursor_ShouldApplyToWindowsAndPreserveExplicitControlCursors()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(AvaloniaUiSmokeTests).Assembly);
        session.Dispatch(() =>
        {
            var application = Application.Current!;
            Assert.IsTrue(GameCursorService.TryInstall(application), "自定义指针应能由当前平台加载");
            Assert.AreEqual(new PixelPoint(0, 0), GameCursorService.NormalCursorHotspot);
            using var originalCursorStream = AssetLoader.Open(new Uri(GameCursorService.NormalCursorUri));
            using var originalCursor = new Bitmap(originalCursorStream);
            using var scaledCursor = GameCursorService.LoadScaledBitmap(GameCursorService.NormalCursorUri);
            Assert.AreEqual(new PixelSize(originalCursor.PixelSize.Width * 2, originalCursor.PixelSize.Height * 2),
                scaledCursor.PixelSize, "指针素材应按最近邻放大，避免显示过小");

            var explicitCursor = new Cursor(StandardCursorType.Hand);
            var link = new Button { Cursor = explicitCursor, Content = "链接" };
            var defaultButton = new Button { Content = "普通按钮" };
            var textCursor = new Cursor(StandardCursorType.Ibeam);
            var input = new TextBox { Cursor = textCursor };
            var defaultInput = new TextBox();
            var popupContent = new Border { Width = 50, Height = 30 };
            var popup = new global::Avalonia.Controls.Primitives.Popup { PlacementTarget = link, Child = popupContent };
            var panel = new Grid();
            panel.Children.Add(link);
            panel.Children.Add(defaultButton);
            panel.Children.Add(input);
            panel.Children.Add(defaultInput);
            panel.Children.Add(popup);
            var window = new Window { Content = panel, Width = 180, Height = 100 };
            try
            {
                window.Show();
                window.UpdateLayout();
                Assert.IsNotNull(window.Cursor, "窗口应使用游戏默认指针");
                Assert.AreSame(explicitCursor, link.Cursor, "已有特殊指针不能被默认指针覆盖");
                Assert.AreSame(application.Resources[GameCursorService.HandCursorResource], defaultButton.Cursor, "按钮应使用游戏手形指针");
                Assert.AreSame(textCursor, input.Cursor, "文本输入指针不能被手形覆盖");
                Assert.AreSame(application.Resources[GameCursorService.TextCursorResource], defaultInput.Cursor, "文本输入应保留系统 I 形指针");
                popup.IsOpen = true;
                window.UpdateLayout();
                var popupRoot = TopLevel.GetTopLevel(popupContent);
                Assert.IsNotNull(popupRoot, "弹出内容应有独立的窗口根");
                Assert.IsNotNull(popupRoot.Cursor, "弹出菜单也应使用游戏默认指针");
            }
            finally
            {
                window.Close();
                explicitCursor.Dispose();
                textCursor.Dispose();
            }
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void GameIcons_ShouldLoadWithoutBundlingArchivedGeneratedAssets()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(AvaloniaUiSmokeTests).Assembly);
        session.Dispatch(() =>
        {
            foreach (var iconName in new[] { "retro-pc", "pencil-ruler", "archive", "cog", "trash", "arrow-left", "arrow-right" })
            {
                var icon = new ThemeIcon { IconName = iconName };
                Assert.IsNotNull(icon.Source, $"游戏图标 {iconName} 应能从应用资源加载");
                Assert.AreEqual(BitmapInterpolationMode.None, RenderOptions.GetBitmapInterpolationMode(icon));
            }

            using var sprite = AssetLoader.Open(new Uri("avares://SVL.Avalonia/Assets/Icons/Stardew/Book.png"));
            var header = new byte[24];
            sprite.ReadExactly(header);
            Assert.AreEqual(16, BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16, 4)));
            Assert.AreEqual(16, BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20, 4)));
            Assert.IsFalse(AssetLoader.Exists(new Uri("avares://SVL.Avalonia/Assets/Icons/Generated/UI/Info.png")));
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void NotificationService_ShouldHonorTheDisplayPreferenceWithoutLosingCallbacks()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(AvaloniaUiSmokeTests).Assembly);
        session.Dispatch(() =>
        {
            var service = new NotificationService { IsEnabled = false };
            var callbackCalled = false;
            ((INotificationService)service).Show("提示", "内容", 0, () => callbackCalled = true, NotificationType.Info);
            Assert.AreEqual(0, service.ActiveCount);
            Assert.IsTrue(callbackCalled);

            service.IsEnabled = true;
            ((INotificationService)service).Show("提示", "内容", 0, null, NotificationType.Info);
            Assert.AreEqual(1, service.ActiveCount);
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void MainWindow_ShouldLoadAndKeepWindowButtonsOnSharedGrid()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(AvaloniaUiSmokeTests).Assembly);

        session.Dispatch(() =>
        {
            var window = new MainWindow();
            try
            {
                window.Show();
                window.UpdateLayout();

                var mainPagePanel = window.GetVisualDescendants()
                    .OfType<Border>()
                    .Single(border => border.Classes.Contains("panel"));
                var pagePresenter = mainPagePanel.GetVisualDescendants()
                    .OfType<ContentControl>()
                    .Single();
                var pageRoot = new UserControl();
                var themeTestIcon = new ThemeIcon
                {
                    IconName = "retro-pc",
                    Width = 18,
                    Height = 18
                };
                pageRoot.Content = themeTestIcon;
                pagePresenter.Content = pageRoot;
                window.UpdateLayout();
                Assert.IsTrue(pageRoot.Background is ISolidColorBrush pageBrush && pageBrush.Color.A == 0x00,
                    "主窗口页面根 UserControl 必须透明，避免额外叠加主题底色");

                var buttons = window
                    .GetVisualDescendants()
                    .OfType<Button>()
                    .Where(button => button.Classes.Contains("winCtrl"))
                    .ToList();

                Assert.AreEqual(3, buttons.Count, "主窗口必须包含最小化、最大化、关闭三个控制按钮");
                Assert.IsTrue(buttons.All(button => Math.Abs(button.Bounds.Width - 40) < 0.01));
                Assert.IsTrue(buttons.All(button => Math.Abs(button.Bounds.Height - 48) < 0.01));

                var centers = buttons
                    .Select(button => Math.Round(button.Bounds.Center.Y, 2))
                    .Distinct()
                    .ToList();
                Assert.AreEqual(1, centers.Count, "三个窗口按钮必须处于同一水平中心线");

                var maximizeButton = buttons.Single(button => button.Classes.Contains("max"));
                var maximizeIcon = maximizeButton.GetVisualDescendants()
                    .OfType<AvaloniaPath>()
                    .Single(icon => icon.Name == "MaximizeButtonIcon");
                var restoreIcon = maximizeButton.GetVisualDescendants()
                    .OfType<AvaloniaPath>()
                    .Single(icon => icon.Name == "RestoreButtonIcon");
                Assert.IsTrue(maximizeIcon.IsVisible);
                Assert.IsFalse(restoreIcon.IsVisible);

                var previousDarkMode = ThemeService.IsDarkMode;
                window.UpdateLayout();
                var originalThemedSource = themeTestIcon.Source;
                Assert.IsNotNull(originalThemedSource, "页面 PNG 图标必须能从应用资源加载");
                Assert.IsFalse(buttons.SelectMany(button => button.GetVisualDescendants().OfType<ThemeIcon>()).Any(),
                    "标题栏窗口控制图形保持专门绘制，不使用通用 PNG 图标");
                foreach (var iconName in new[]
                         {
                             "retro-pc", "pencil-ruler", "notebook", "archive", "download", "download-alt",
                             "file-import", "analytics", "cog", "paint-brush", "info-circle", "question-circle",
                             "exclamation-triangle", "lightbulb", "plus", "refresh", "globe", "link",
                             "search", "ellipses-horizontal", "bolt", "arrow-left", "arrow-right", "star", "clock",
                             "check-circle", "times-circle", "times", "octagon-times",
                             "lock-open", "lock", "logout"
                         })
                {
                    Assert.IsTrue(ThemeIcon.IsIconMapped(iconName), $"语义图标“{iconName}”必须登记到游戏图标映射表");
                    themeTestIcon.IconName = iconName;
                    window.UpdateLayout();
                    Assert.IsNotNull(themeTestIcon.Source, $"语义图标“{iconName}”必须映射到游戏图像");
                }

                foreach (var (iconName, fileName) in new[] { ("notebook", "Book.png"), ("archive", "Chest.png") })
                {
                    themeTestIcon.IconName = iconName;
                    Assert.IsNotNull(themeTestIcon.Source);
                    // Headless 后端将解码位图替换为 1x1 占位图，须读取原始 PNG 的 IHDR 检查尺寸。
                    using var stream = AssetLoader.Open(new Uri($"avares://SVL.Avalonia/Assets/Icons/Stardew/{fileName}"));
                    var header = new byte[24];
                    stream.ReadExactly(header);
                    Assert.AreEqual(16, BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16, 4)), $"{iconName} 应使用游戏原始像素素材");
                    Assert.AreEqual(16, BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20, 4)), $"{iconName} 应使用游戏原始像素素材");
                }
                Assert.AreEqual(BitmapInterpolationMode.None, RenderOptions.GetBitmapInterpolationMode(themeTestIcon),
                    "像素图标应关闭插值，避免缩小时变糊");

                themeTestIcon.IconName = "retro-pc";
                window.UpdateLayout();
                originalThemedSource = themeTestIcon.Source;
                try
                {
                    ThemeService.SetDarkMode(!previousDarkMode);
                    Assert.IsNotNull(themeTestIcon.Source, "主题切换后图标资源仍须加载");
                    Assert.IsTrue(ReferenceEquals(originalThemedSource, themeTestIcon.Source),
                        "游戏图标在浅色和深色主题中应复用同一图像资源");
                    Assert.IsTrue(maximizeIcon.Stroke is ISolidColorBrush chromeStroke && chromeStroke.Color == Colors.White,
                        "专门绘制的标题栏图形应保持白色描边");
                }
                finally
                {
                    ThemeService.SetDarkMode(previousDarkMode);
                }

                window.WindowState = WindowState.Maximized;
                window.UpdateLayout();
                Assert.IsFalse(maximizeIcon.IsVisible, "最大化后应显示还原图标");
                Assert.IsTrue(restoreIcon.IsVisible, "最大化后应显示还原图标");
                window.WindowState = WindowState.Normal;
                window.UpdateLayout();
                Assert.IsTrue(maximizeIcon.IsVisible, "还原窗口后应恢复最大化图标");
                Assert.IsFalse(restoreIcon.IsVisible);

                var iconCanvases = buttons
                    .SelectMany(button => button.GetVisualDescendants().OfType<Grid>())
                    .Where(grid => Math.Abs(grid.Bounds.Width - 24) < 0.01 &&
                                   Math.Abs(grid.Bounds.Height - 24) < 0.01)
                    .ToList();
                Assert.AreEqual(3, iconCanvases.Count, "三个按钮必须各自使用统一的 24×24 图标画布");

                var minimizeButton = buttons.Single(button => button.Classes.Contains("min"));
                var minimizeIcon = minimizeButton.GetVisualDescendants()
                    .OfType<Rectangle>()
                    .Single();
                Assert.AreEqual(12d, minimizeIcon.Bounds.Width, 0.01);
                Assert.AreEqual(2d, minimizeIcon.Bounds.Height, 0.01);
                var minimizeCanvas = minimizeIcon.GetVisualAncestors().OfType<Grid>()
                    .Single(grid => Math.Abs(grid.Bounds.Width - 24) < 0.01 &&
                                    Math.Abs(grid.Bounds.Height - 24) < 0.01);
                var minimizeIconCenter = minimizeIcon.TranslatePoint(
                    new Point(minimizeIcon.Bounds.Width / 2, minimizeIcon.Bounds.Height / 2),
                    minimizeCanvas);
                Assert.IsNotNull(minimizeIconCenter);
                Assert.AreEqual(12d, minimizeIconCenter!.Value.Y, 0.01,
                    "最小化图标中心必须与 24×24 图标画布中心重合");

                var resources = Application.Current!.Resources;
                var previousAnimations = ThemeService.AnimationsEnabled;
                try
                {
                    ThemeService.SetAnimationsEnabled(false);

                    Assert.IsFalse(ThemeService.AnimationsEnabled);
                    Assert.AreEqual(
                        0,
                        ((Transitions)resources["NavButtonTransitions"]!).Count,
                        "关闭动画后导航按钮不应继续保留过渡动画");
                    Assert.AreEqual(
                        0,
                        ((Transitions)resources["ModRowTransitions"]!).Count,
                        "关闭动画后 Mod 行不应继续保留过渡动画");

                    ThemeService.SetAnimationsEnabled(true);

                    Assert.IsTrue(ThemeService.AnimationsEnabled);
                    Assert.IsTrue(
                        ((Transitions)resources["NavButtonTransitions"]!).Count > 0,
                        "重新开启动画后应恢复导航按钮过渡动画");
                    Assert.AreEqual(
                        2,
                        ((Transitions)resources["ModRowTransitions"]!).Count,
                        "重新开启动画后应恢复 Mod 行的背景和边框过渡");
                }
                finally
                {
                    ThemeService.SetAnimationsEnabled(previousAnimations);
                }
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void MainWindow_ShouldKeepCaptionButtonsAlignedAndHittableAtFractionalRenderScales()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(AvaloniaUiSmokeTests).Assembly);

        session.Dispatch(() =>
        {
            var window = new MainWindow();
            try
            {
                window.Show();

                foreach (var renderScale in new[] { 1.25, 1.5 })
                {
                    SetHeadlessRenderScale(window, renderScale);
                    window.UpdateLayout();

                    Assert.AreEqual(renderScale, window.RenderScaling, 0.001,
                        $"Headless 窗口应已应用 {renderScale:P0} 渲染缩放");

                    var buttons = window
                        .GetVisualDescendants()
                        .OfType<Button>()
                        .Where(button => button.Classes.Contains("winCtrl"))
                        .ToList();
                    Assert.AreEqual(3, buttons.Count);
                    Assert.IsTrue(buttons.All(button =>
                            Math.Abs(button.Bounds.Width - 40) < 0.01 &&
                            Math.Abs(button.Bounds.Height - 48) < 0.01),
                        $"{renderScale:P0} 下三个按钮应保留相同的可点击单元格尺寸");

                    var canvases = buttons
                        .SelectMany(button => button.GetVisualDescendants().OfType<Grid>())
                        .Where(grid => Math.Abs(grid.Bounds.Width - 24) < 0.01 &&
                                       Math.Abs(grid.Bounds.Height - 24) < 0.01)
                        .ToList();
                    Assert.AreEqual(3, canvases.Count,
                        $"{renderScale:P0} 下三个图形均应保留 24×24 画布");
                    var minimizeButton = buttons.Single(button => button.Classes.Contains("min"));
                    var minimizeIcon = minimizeButton.GetVisualDescendants()
                        .OfType<Rectangle>()
                        .Single();
                    Assert.AreEqual(12d, minimizeIcon.Bounds.Width, 0.01);
                    Assert.IsTrue(minimizeIcon.Bounds.Height >= 1.5 && minimizeIcon.Bounds.Height <= 2.5,
                        $"{renderScale:P0} 下最小化短横线应保持可见厚度，不得退化为点");
                    var minimizeCanvas = minimizeIcon.GetVisualAncestors().OfType<Grid>()
                        .Single(grid => Math.Abs(grid.Bounds.Width - 24) < 0.01 &&
                                        Math.Abs(grid.Bounds.Height - 24) < 0.01);
                    var iconCenter = minimizeIcon.TranslatePoint(
                        new Point(minimizeIcon.Bounds.Width / 2, minimizeIcon.Bounds.Height / 2),
                        minimizeCanvas);
                    Assert.IsNotNull(iconCenter);
                    Assert.IsTrue(Math.Abs(iconCenter!.Value.Y - minimizeCanvas.Bounds.Height / 2) <= 0.5,
                        $"{renderScale:P0} 下最小化图标中心偏差不能超过半个 DIP");

                    var centers = buttons
                        .Select(button => button.TranslatePoint(
                            new Point(button.Bounds.Width / 2, button.Bounds.Height / 2),
                            window))
                        .ToList();
                    Assert.IsTrue(centers.All(center => center.HasValue));
                    Assert.AreEqual(1, centers
                        .Select(center => Math.Round(center!.Value.Y, 2))
                        .Distinct()
                        .Count(),
                        $"{renderScale:P0} 下三个标题栏按钮应仍在同一水平中心线上");
                    Assert.AreEqual(3, centers
                        .Select(center => Math.Round(center!.Value.X, 2))
                        .Distinct()
                        .Count(),
                        "三个按钮必须保持独立的命中区域");

                    for (var i = 0; i < buttons.Count; i++)
                    {
                        var button = buttons[i];
                        Assert.IsTrue(button.IsHitTestVisible && button.IsVisible && button.IsEnabled,
                            $"{renderScale:P0} 下窗口按钮必须保持可见、启用且参与命中测试");
                        Assert.IsTrue(new Rect(0, 0, button.Bounds.Width, button.Bounds.Height)
                                .Contains(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2)),
                            $"{renderScale:P0} 下按钮中心必须落在自身命中区域内");
                    }
                }
            }
            finally
            {
                SetHeadlessRenderScale(window, 1d);
                window.Close();
            }
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void MainWindow_ShouldHideToTrayOnUserCloseButAllowExplicitExit()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(AvaloniaUiSmokeTests).Assembly);

        session.Dispatch(() =>
        {
            var window = new MainWindow
            {
                ShouldMinimizeToTrayOnClose = () => true
            };

            window.Show();
            window.Close();

            Assert.IsFalse(window.IsVisible,
                "启用最小化到托盘时，用户关闭窗口应隐藏窗口而不是销毁窗口");

            window.Show();
            window.CloseForExit();
            Assert.IsFalse(window.IsVisible,
                "托盘退出/程序主动退出必须绕过最小化到托盘拦截");
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static void SetHeadlessRenderScale(Window window, double scale)
    {
        // Avalonia.Headless 11.2.8 未暴露 DPI 参数；在测试窗口的 headless 实现上
        // 改变平台缩放并发送标准 ScalingChanged 回调，覆盖 125%/150% 布局舍入路径。
        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic;
        var platformImplField = typeof(TopLevel).GetField("<PlatformImpl>k__BackingField", flags);
        var platformImpl = platformImplField?.GetValue(window);
        Assert.IsNotNull(platformImpl, "Headless 窗口必须已创建平台实现");

        var implementationType = platformImpl!.GetType();
        var scalingField = implementationType.GetField("<RenderScaling>k__BackingField", flags);
        Assert.IsNotNull(scalingField, "Headless 平台应公开渲染缩放的内部存储");
        scalingField!.SetValue(platformImpl, scale);

        var scalingChangedProperty = implementationType.GetProperty(
            "ScalingChanged",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
        var scalingChanged = scalingChangedProperty?.GetValue(platformImpl) as Action<double>;
        Assert.IsNotNull(scalingChanged, "TopLevel 应订阅 headless 平台的缩放变化");
        scalingChanged!(scale);
    }

    [TestMethod]
    public void TransparencyPreference_ShouldDriveThemeAlphaAndWindowLevelHints()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(AvaloniaUiSmokeTests).Assembly);

        session.Dispatch(() =>
        {
            var previousTransparency = ThemeService.BackgroundTransparencyPercent;
            var previousPrimaryColor = ThemeService.CustomPrimaryColorHex;
            var resources = Application.Current!.Resources;
            var window = new MainWindow();
            var windowResources = window.Resources;
            var rootGrid = (Grid)window.Content!;
            var windowBackdrop = rootGrid.Children.OfType<Border>()
                .Single(border => border.Name == "WindowBackdrop");
            var applyTransparency = typeof(MainWindow).GetMethod(
                "ApplyWindowTransparency",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(applyTransparency);

            try
            {
                Assert.AreEqual("WindowBackdrop", windowBackdrop.Name,
                    "主窗口应使用独立主题铺层，而不是将着色画刷直接设为 Window.Background");
                Assert.IsTrue(window.Background is ISolidColorBrush rootBackground && rootBackground.Color.A == 0x00,
                    "主窗口根背景必须透明，避免覆盖平台透明合成材质");
                Assert.IsFalse(windowBackdrop.IsHitTestVisible,
                    "主题背景铺层不应挡住主窗口内容的鼠标命中");

                ThemeService.SetBackgroundTransparencyPercent(0);
                applyTransparency!.Invoke(window, [0]);
                Assert.AreEqual((byte)0xFF,
                    ((SolidColorBrush)windowResources["WindowBackgroundBrush"]!).Color.A,
                    "关闭透明效果时主窗口背景应回到不透明");
                Assert.AreEqual((byte)0xFF,
                    ((ISolidColorBrush)windowBackdrop.Background!).Color.A,
                    "窗口铺层必须实际使用主窗口作用域的不透明画刷");
                Assert.AreEqual((byte)0xFF,
                    ((SolidColorBrush)windowResources["PanelBackgroundBrush"]!).Color.A,
                    "关闭透明效果时主页面面板应回到不透明，保持原有可读性");
                Assert.AreEqual((byte)0xFF,
                    ((SolidColorBrush)windowResources["HeaderBackgroundBrush"]!).Color.A,
                    "关闭透明效果时主窗口标题栏应回到不透明");
                Assert.AreEqual((byte)0xFF,
                    ((SolidColorBrush)windowResources["WindowTransparencyFallbackBrush"]!).Color.A,
                    "不支持透明的后端应使用不透明主题色回退");
                CollectionAssert.AreEqual(
                    new[] { WindowTransparencyLevel.None },
                    window.TransparencyLevelHint.ToArray());
                var disabledDiagnostic = DebugConsoleService.Instance.Snapshot()
                    .LastOrDefault(entry => entry.Text.Contains(
                        "[WindowTransparency] 背景透明度 0%",
                        StringComparison.Ordinal));
                Assert.IsNotNull(disabledDiagnostic, "关闭透明效果时应记录平台实际透明级别");
                StringAssert.Contains(disabledDiagnostic!.Text, "requested=[None]");
                StringAssert.Contains(disabledDiagnostic.Text, $"actual={window.ActualTransparencyLevel}");

                ThemeService.SetBackgroundTransparencyPercent(100);
                applyTransparency.Invoke(window, [100]);
                Assert.AreEqual((byte)0x66,
                    ((SolidColorBrush)windowResources["WindowBackgroundBrush"]!).Color.A,
                    "主窗口底色必须足够透明，让 Acrylic 材质可见");
                Assert.AreEqual((byte)0x66,
                    ((ISolidColorBrush)windowBackdrop.Background!).Color.A,
                    "窗口铺层应实际解析到主窗口作用域的半透明画刷");
                Assert.AreEqual((byte)0x99,
                    ((SolidColorBrush)windowResources["HeaderBackgroundBrush"]!).Color.A,
                    "主窗口标题栏应透出 Acrylic 材质");
                Assert.AreEqual((byte)0x33,
                    ((SolidColorBrush)windowResources["PanelBackgroundBrush"]!).Color.A,
                    "主页面面板应使用独立半透明画刷");
                Assert.AreEqual((byte)0xA6,
                    ((SolidColorBrush)windowResources["CardBrush"]!).Color.A,
                    "主页面卡片也必须半透明，避免不透明卡片遮住整个窗口的 Acrylic 材质");
                Assert.AreEqual((byte)0xA6,
                    ((SolidColorBrush)windowResources["SurfaceBrush"]!).Color.A,
                    "主页面输入框与表面容器应使用半透明画刷");

                ThemeService.SetBackgroundTransparencyPercent(50);
                applyTransparency.Invoke(window, [50]);
                Assert.AreEqual((byte)0xB3,
                    ((SolidColorBrush)windowResources["WindowBackgroundBrush"]!).Color.A,
                    "50% 背景透明度应处于完全不透明和最大透明效果之间");
                ThemeService.SetBackgroundTransparencyPercent(100);
                applyTransparency.Invoke(window, [100]);

                // 某些窗口后端在 Opened/透明级别变更时会短暂回报 None；
                // 这不应把已开启的主页面半透明画刷错误切回不透明。
                var openedField = typeof(MainWindow).GetField(
                    "_hasBeenOpened",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Assert.IsNotNull(openedField);
                openedField!.SetValue(window, true);
                applyTransparency.Invoke(window, [100]);
                Assert.AreEqual((byte)0x66,
                    ((SolidColorBrush)windowResources["WindowBackgroundBrush"]!).Color.A,
                    "已打开窗口即使暂时报告透明级别 None，也必须保留主页面半透明画刷");

                Assert.AreEqual((byte)0xFF,
                    ((SolidColorBrush)resources["CardBrush"]!).Color.A,
                    "主窗口透明效果不得让 Debug/确认弹窗等其它窗口的卡片意外透明");
                Assert.AreEqual((byte)0xFF,
                    ((SolidColorBrush)resources["PanelBackgroundBrush"]!).Color.A,
                    "主窗口透明效果不得改变其它窗口使用的全局面板画刷");

                var shellGrid = (Grid)window.Content!;
                var pageHost = shellGrid.Children.OfType<Grid>()
                    .Single(grid => Grid.GetRow(grid) == 1);
                var contentPanel = pageHost.Children.OfType<Border>()
                    .Single(border => border.Classes.Contains("panel"));
                var pagePresenter = (ContentControl)contentPanel.Child!;
                var sampleCard = new Border();
                sampleCard.Classes.Add("hintCard");
                var samplePage = new UserControl { Content = sampleCard };
                pagePresenter.Content = samplePage;
                window.UpdateLayout();
                Assert.IsTrue(samplePage.Background is ISolidColorBrush pageBackground && pageBackground.Color.A == 0,
                    "实际嵌入的主页面根控件应透明");
                Assert.AreEqual((byte)0xA6,
                    ((ISolidColorBrush)sampleCard.Background!).Color.A,
                    "实际主页面卡片必须解析到局部半透明画刷");
                CollectionAssert.AreEqual(
                    new[] { WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Transparent },
                    window.TransparencyLevelHint.ToArray());
                var enabledDiagnostic = DebugConsoleService.Instance.Snapshot()
                    .LastOrDefault(entry => entry.Text.Contains(
                        "[WindowTransparency] 背景透明度 100%",
                        StringComparison.Ordinal));
                Assert.IsNotNull(enabledDiagnostic, "启用透明效果时应记录平台实际透明级别");
                StringAssert.Contains(enabledDiagnostic!.Text, "requested=[AcrylicBlur, Transparent]");
                StringAssert.Contains(enabledDiagnostic.Text, $"actual={window.ActualTransparencyLevel}");

                Assert.IsTrue(ThemeService.TrySetCustomPrimaryColor("#123456", out var themeError), themeError);
                var themedHeader = (SolidColorBrush)resources["HeaderBackgroundBrush"]!;
                var windowHeader = (SolidColorBrush)windowResources["HeaderBackgroundBrush"]!;
                Assert.AreEqual(themedHeader.Color.R, windowHeader.Color.R,
                    "主题强调色变化后，主窗口局部标题栏画刷应同步主题颜色");
                Assert.AreEqual(themedHeader.Color.G, windowHeader.Color.G);
                Assert.AreEqual(themedHeader.Color.B, windowHeader.Color.B);
                Assert.AreEqual((byte)0x99, windowHeader.Color.A,
                    "主题变更同步颜色时仍须保留主窗口标题栏透明度");
            }
            finally
            {
                ThemeService.SetBackgroundTransparencyPercent(previousTransparency);
                ThemeService.TrySetCustomPrimaryColor(previousPrimaryColor, out _);
                window.Close();
            }
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void ThemeService_ShouldApplyAndClearCustomPrimaryColor()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(AvaloniaUiSmokeTests).Assembly);

        session.Dispatch(() =>
        {
            var previousDarkMode = ThemeService.IsDarkMode;
            var previousFollowSystem = ThemeService.FollowSystemTheme;
            var previousPrimaryColor = ThemeService.CustomPrimaryColorHex;

            try
            {
                ThemeService.SetThemeMode(false, false);
                Assert.IsTrue(
                    ThemeService.TrySetCustomPrimaryColor("#123456", out var error),
                    error);
                Assert.AreEqual("#123456", ThemeService.CustomPrimaryColorHex);

                var resources = Application.Current!.Resources;
                var lightAccent = ((SolidColorBrush)resources["AccentBrush"]!).Color;
                Assert.AreEqual((byte)0x12, lightAccent.R);
                Assert.AreEqual((byte)0x34, lightAccent.G);
                Assert.AreEqual((byte)0x56, lightAccent.B);

                var lightNotificationError = ((SolidColorBrush)resources["NotificationErrorBackground"]!).Color;
                Assert.AreEqual((byte)0xC6, lightNotificationError.R,
                    "浅色模式通知错误色必须使用主题资源，而不是固定 XAML 颜色");
                Assert.AreEqual((byte)0x28, lightNotificationError.G);

                var lightModStatus = ((SolidColorBrush)resources["ModStatusEnabledBackground"]!).Color;
                Assert.AreEqual(Color.Parse("#D2A679"), lightModStatus,
                    "浅色模式的本地 Mod 状态标签应使用主题资源");

                ThemeService.SetDarkMode(true);
                var darkAccent = ((SolidColorBrush)resources["AccentBrush"]!).Color;
                Assert.IsTrue(darkAccent.R > 0x12 || darkAccent.G > 0x34 || darkAccent.B > 0x56,
                    "深色模式应提高自定义强调色的可读性");

                var darkNotificationError = ((SolidColorBrush)resources["NotificationErrorBackground"]!).Color;
                Assert.AreEqual((byte)0xB7, darkNotificationError.R,
                    "深色模式通知错误色必须切换为深色可读配色");
                Assert.AreEqual((byte)0x1C, darkNotificationError.G);

                var darkModStatus = ((SolidColorBrush)resources["ModStatusEnabledBackground"]!).Color;
                Assert.AreEqual(Color.Parse("#8A5A36"), darkModStatus,
                    "深色模式的本地 Mod 状态标签应切换为深色主题资源");

                Assert.IsFalse(ThemeService.TrySetCustomPrimaryColor("not-a-color", out _));
                Assert.AreEqual("#123456", ThemeService.CustomPrimaryColorHex,
                    "无效颜色不能覆盖已应用的颜色");

                Assert.IsTrue(ThemeService.TrySetCustomPrimaryColor(string.Empty, out var clearError), clearError);
                Assert.AreEqual(string.Empty, ThemeService.CustomPrimaryColorHex);
            }
            finally
            {
                ThemeService.TrySetCustomPrimaryColor(previousPrimaryColor, out _);
                ThemeService.SetThemeMode(previousDarkMode, previousFollowSystem);
            }
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void FloatingNotification_ShouldRecolorWhenThemeChanges()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(AvaloniaUiSmokeTests).Assembly);

        session.Dispatch(() =>
        {
            var previousDarkMode = ThemeService.IsDarkMode;
            var previousFollowSystem = ThemeService.FollowSystemTheme;
            var previousPrimaryColor = ThemeService.CustomPrimaryColorHex;
            var notification = new FloatingNotificationControl
            {
                DataContext = new NotificationItem
                {
                    Type = NotificationType.Error,
                    Title = "错误",
                    Message = "测试通知"
                }
            };
            var host = new Window { Content = notification };

            try
            {
                host.Show();
                host.UpdateLayout();

                ThemeService.SetThemeMode(false, false);
                var card = notification.GetVisualDescendants()
                    .OfType<Border>()
                    .Single(border => border.Name == "Card");
                var lightColor = ((SolidColorBrush)card.Background!).Color;
                Assert.AreEqual((byte)0xC6, lightColor.R);
                Assert.AreEqual((byte)0x28, lightColor.G);

                ThemeService.SetDarkMode(true);
                var darkColor = ((SolidColorBrush)card.Background!).Color;
                Assert.AreEqual((byte)0xB7, darkColor.R,
                    "已显示的通知也必须响应主题切换，不能保留旧的固定背景色");
                Assert.AreEqual((byte)0x1C, darkColor.G);
            }
            finally
            {
                ThemeService.TrySetCustomPrimaryColor(previousPrimaryColor, out _);
                ThemeService.SetThemeMode(previousDarkMode, previousFollowSystem);
                host.Close();
            }
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void RestoreTransaction_ShouldRecreateOriginalFromSnapshotWhenTargetWasRemoved()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"SVL_RestoreTransaction_{Guid.NewGuid():N}");
        var snapshot = System.IO.Path.Combine(root, "ModsBackup", "old-mod");
        var target = System.IO.Path.Combine(root, "Mods", "old-mod");
        Directory.CreateDirectory(snapshot);
        File.WriteAllText(System.IO.Path.Combine(snapshot, "manifest.json"), "{\"Name\":\"Old Mod\"}");
        File.WriteAllText(System.IO.Path.Combine(snapshot, "content.txt"), "original");

        try
        {
            var method = typeof(SVL.Avalonia.ViewModels.VersionSettingsPageViewModel)
                .GetMethod(
                    "TryRollbackRestoreTransaction",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(method, "恢复事务回滚方法必须存在");

            var arguments = new object[] { target, snapshot, null! };
            var result = (bool)method!.Invoke(null, arguments)!;

            Assert.IsTrue(result, arguments[2]?.ToString());
            Assert.IsTrue(File.Exists(System.IO.Path.Combine(target, "manifest.json")));
            Assert.AreEqual(
                "original",
                File.ReadAllText(System.IO.Path.Combine(target, "content.txt")),
                "原有 Mod 快照应在目标目录被移走后恢复");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void ConflictResolutionDialog_ShouldLoadWithComparisonAndSafeReplaceActions()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(AvaloniaUiSmokeTests).Assembly);

        session.Dispatch(() =>
        {
            var dialog = new ConflictResolutionDialog
            {
                DataContext = new ConflictResolutionDialogModel
                {
                    Title = "恢复备份时发现冲突",
                    Message = "目标 Mod 已存在，请先查看比对结果。",
                    ComparisonSummary = "相同 3 个，内容不同 1 个",
                    IncomingPathText = "备份来源：D:/ModsBackup/old-mod",
                    ExistingPathText = "当前目录：D:/Mods/old-mod",
                    BackupNotice = "替换前会先备份原有 Mod。"
                }
            };

            try
            {
                dialog.Show();
                dialog.UpdateLayout();

                Assert.IsTrue(dialog.Classes.Contains("conflictDialog"));

                var textBlocks = dialog
                    .GetVisualDescendants()
                    .OfType<TextBlock>()
                    .Select(textBlock => textBlock.Text ?? string.Empty)
                    .ToList();
                StringAssert.Contains(string.Join("\n", textBlocks), "相同 3 个，内容不同 1 个");
                StringAssert.Contains(string.Join("\n", textBlocks), "替换前会先备份原有 Mod");

                var buttons = dialog.GetVisualDescendants().OfType<Button>().ToList();
                Assert.AreEqual(4, buttons.Count, "冲突弹窗必须提供两个打开目录、取消和替换四个操作");
                CollectionAssert.Contains(buttons.Select(button => button.Content?.ToString()).ToList(), "取消");
                CollectionAssert.Contains(buttons.Select(button => button.Content?.ToString()).ToList(), "替换（先备份）");
            }
            finally
            {
                dialog.Close();
            }
        }, CancellationToken.None).GetAwaiter().GetResult();
    }
}
