using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Animation;
using Avalonia.Media;
using Avalonia.VisualTree;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SVL.Avalonia;
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

                ThemeService.SetDarkMode(true);
                var darkAccent = ((SolidColorBrush)resources["AccentBrush"]!).Color;
                Assert.IsTrue(darkAccent.R > 0x12 || darkAccent.G > 0x34 || darkAccent.B > 0x56,
                    "深色模式应提高自定义强调色的可读性");

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
}
