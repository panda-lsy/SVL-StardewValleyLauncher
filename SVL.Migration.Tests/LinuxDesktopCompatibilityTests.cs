using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SVL.Migration.Tests;

[TestClass]
public sealed class LinuxDesktopCompatibilityTests
{
    [TestMethod]
    public void AvaloniaX11InputMethod_ShouldResolveItsDbusDependencies()
    {
        // Avalonia 11.2's X11 IME fails before the first window opens if its
        // D-Bus dependency no longer exposes the API used by this method.
        var assembly = Assembly.Load("Avalonia.FreeDesktop");
        var helper = assembly.GetType("Avalonia.FreeDesktop.DBusIme.X11DBusImeHelper");
        if (helper is null)
        {
            Assert.Inconclusive("此 Avalonia 版本不再使用该 X11 输入法辅助类");
            return;
        }

        var method = helper.GetMethod("DetectAndRegister", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.IsNotNull(method, "找不到 Avalonia X11 输入法初始化方法");
        RuntimeHelpers.PrepareMethod(method.MethodHandle);
    }
}
