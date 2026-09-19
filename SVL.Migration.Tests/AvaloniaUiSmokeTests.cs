using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Animation;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
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

                var iconCanvases = buttons
                    .SelectMany(button => button.GetVisualDescendants().OfType<Grid>())
                    .Where(grid => Math.Abs(grid.Bounds.Width - 24) < 0.01 &&
                                   Math.Abs(grid.Bounds.Height - 24) < 0.01)
                    .ToList();
                Assert.AreEqual(3, iconCanvases.Count, "三个按钮必须各自使用统一的 24×24 图标画布");

                var minimizeButton = buttons.Single(button => button.Classes.Contains("min"));
                var minimizeGlyph = minimizeButton.GetVisualDescendants().OfType<Rectangle>().Single();
                var minimizeCanvas = minimizeGlyph.GetVisualAncestors().OfType<Grid>()
                    .Single(grid => Math.Abs(grid.Bounds.Width - 24) < 0.01 &&
                                    Math.Abs(grid.Bounds.Height - 24) < 0.01);
                var minimizeGlyphCenter = minimizeGlyph.TranslatePoint(
                    new Point(minimizeGlyph.Bounds.Width / 2, minimizeGlyph.Bounds.Height / 2),
                    minimizeCanvas);
                Assert.IsNotNull(minimizeGlyphCenter);
                Assert.AreEqual(12d, minimizeGlyphCenter!.Value.Y, 0.01,
                    "最小化横线的图形中心必须与 24×24 图标画布中心重合");

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
                    var minimizeGlyph = minimizeButton.GetVisualDescendants().OfType<Rectangle>().Single();
                    var minimizeCanvas = minimizeGlyph.GetVisualAncestors().OfType<Grid>()
                        .Single(grid => Math.Abs(grid.Bounds.Width - 24) < 0.01 &&
                                        Math.Abs(grid.Bounds.Height - 24) < 0.01);
                    var glyphCenter = minimizeGlyph.TranslatePoint(
                        new Point(minimizeGlyph.Bounds.Width / 2, minimizeGlyph.Bounds.Height / 2),
                        minimizeCanvas);
                    Assert.IsNotNull(glyphCenter);
                    Assert.IsTrue(Math.Abs(glyphCenter!.Value.Y - minimizeCanvas.Bounds.Height / 2) <= 0.5,
                        $"{renderScale:P0} 下最小化图形中心偏差不能超过半个 DIP");

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
            var previousTransparency = ThemeService.TransparencyEnabled;
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

                ThemeService.SetTransparencyEnabled(false);
                applyTransparency!.Invoke(window, [false]);
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
                        "[WindowTransparency] 设置已关闭",
                        StringComparison.Ordinal));
                Assert.IsNotNull(disabledDiagnostic, "关闭透明效果时应记录平台实际透明级别");
                StringAssert.Contains(disabledDiagnostic!.Text, "requested=[None]");
                StringAssert.Contains(disabledDiagnostic.Text, $"actual={window.ActualTransparencyLevel}");

                ThemeService.SetTransparencyEnabled(true);
                applyTransparency.Invoke(window, [true]);
                Assert.AreEqual((byte)0x80,
                    ((SolidColorBrush)windowResources["WindowBackgroundBrush"]!).Color.A,
                    "主窗口底色必须足够透明，让 Acrylic 材质可见");
                Assert.AreEqual((byte)0x80,
                    ((ISolidColorBrush)windowBackdrop.Background!).Color.A,
                    "窗口铺层应实际解析到主窗口作用域的半透明画刷");
                Assert.AreEqual((byte)0xB3,
                    ((SolidColorBrush)windowResources["HeaderBackgroundBrush"]!).Color.A,
                    "主窗口标题栏应透出 Acrylic 材质");
                Assert.AreEqual((byte)0x66,
                    ((SolidColorBrush)windowResources["PanelBackgroundBrush"]!).Color.A,
                    "主页面面板应使用独立半透明画刷");
                Assert.AreEqual((byte)0xBF,
                    ((SolidColorBrush)windowResources["CardBrush"]!).Color.A,
                    "主页面卡片也必须半透明，避免不透明卡片遮住整个窗口的 Acrylic 材质");
                Assert.AreEqual((byte)0xCC,
                    ((SolidColorBrush)windowResources["SurfaceBrush"]!).Color.A,
                    "主页面输入框与表面容器应使用半透明画刷");

                // 某些窗口后端在 Opened/透明级别变更时会短暂回报 None；
                // 这不应把已开启的主页面半透明画刷错误切回不透明。
                var openedField = typeof(MainWindow).GetField(
                    "_hasBeenOpened",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Assert.IsNotNull(openedField);
                openedField!.SetValue(window, true);
                applyTransparency.Invoke(window, [true]);
                Assert.AreEqual((byte)0x80,
                    ((SolidColorBrush)windowResources["WindowBackgroundBrush"]!).Color.A,
                    "已打开窗口即使暂时报告透明级别 None，也必须保留主页面半透明画刷");

                Assert.AreEqual((byte)0xFF,
                    ((SolidColorBrush)resources["CardBrush"]!).Color.A,
                    "主窗口透明效果不得让 Debug/确认弹窗等其它窗口的卡片意外透明");

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
                Assert.AreEqual((byte)0xBF,
                    ((ISolidColorBrush)sampleCard.Background!).Color.A,
                    "实际主页面卡片必须解析到局部半透明画刷");
                CollectionAssert.AreEqual(
                    new[] { WindowTransparencyLevel.Transparent, WindowTransparencyLevel.AcrylicBlur },
                    window.TransparencyLevelHint.ToArray());
                var enabledDiagnostic = DebugConsoleService.Instance.Snapshot()
                    .LastOrDefault(entry => entry.Text.Contains(
                        "[WindowTransparency] 设置已开启",
                        StringComparison.Ordinal));
                Assert.IsNotNull(enabledDiagnostic, "启用透明效果时应记录平台实际透明级别");
                StringAssert.Contains(enabledDiagnostic!.Text, "requested=[Transparent, AcrylicBlur]");
                StringAssert.Contains(enabledDiagnostic.Text, $"actual={window.ActualTransparencyLevel}");

                Assert.IsTrue(ThemeService.TrySetCustomPrimaryColor("#123456", out var themeError), themeError);
                var themedHeader = (SolidColorBrush)resources["HeaderBackgroundBrush"]!;
                var windowHeader = (SolidColorBrush)windowResources["HeaderBackgroundBrush"]!;
                Assert.AreEqual(themedHeader.Color.R, windowHeader.Color.R,
                    "主题强调色变化后，主窗口局部标题栏画刷应同步主题颜色");
                Assert.AreEqual(themedHeader.Color.G, windowHeader.Color.G);
                Assert.AreEqual(themedHeader.Color.B, windowHeader.Color.B);
                Assert.AreEqual((byte)0xB3, windowHeader.Color.A,
                    "主题变更同步颜色时仍须保留主窗口标题栏透明度");
            }
            finally
            {
                ThemeService.SetTransparencyEnabled(previousTransparency);
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
