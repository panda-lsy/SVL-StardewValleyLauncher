using Microsoft.VisualStudio.TestTools.UnitTesting;
using SVL.Core.Platform.Services;

namespace SVL.Migration.Tests;

[TestClass]
public class NxmProtocolRegistrationServiceTests
{
    [TestMethod]
    public void GetStatus_ShouldReportSupported_OnLinuxAndMacOS()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            Assert.Inconclusive("该用例仅验证 Linux/macOS 平台行为。");
            return;
        }

        var service = new NxmProtocolRegistrationService();
        var status = service.GetStatus();

        Assert.IsTrue(status.IsSuccess);
        Assert.IsTrue(status.IsSupported);
    }

    [TestMethod]
    public void TryRegister_ShouldBeSupportedButRejectMissingExecutable_OnLinuxAndMacOS()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            Assert.Inconclusive("该用例仅验证 Linux/macOS 平台行为。");
            return;
        }

        var service = new NxmProtocolRegistrationService();
        var result = service.TryRegister(Path.Combine(Path.GetTempPath(), "svl-missing-" + Guid.NewGuid().ToString("N")));

        Assert.IsFalse(result.IsSuccess);
        Assert.IsTrue(result.IsSupported);
        Assert.IsFalse(result.IsRegistered);
    }

    [TestMethod]
    [DoNotParallelize]
    public void LinuxService_ShouldRegisterIntoConfiguredXdgHomes()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("该用例仅验证 Linux NXM 服务入口。");
            return;
        }

        var originalDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var originalConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var originalCurrentDesktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP");
        var root = Path.Combine(Path.GetTempPath(), "svl-nxm-linux-service-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var dataHome = Path.Combine(root, "data");
            var configHome = Path.Combine(root, "config");
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", dataHome);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);
            Environment.SetEnvironmentVariable("XDG_CURRENT_DESKTOP", "GNOME:Unity");

            var executable = Environment.ProcessPath;
            Assert.IsFalse(string.IsNullOrWhiteSpace(executable));
            var service = new NxmProtocolRegistrationService();
            var result = service.TryRegister(executable!);

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.IsTrue(result.IsRegistered);
            Assert.IsTrue(File.Exists(Path.Combine(dataHome, "applications", LinuxNxmProtocolRegistration.DesktopFileId)));
            Assert.IsTrue(File.Exists(Path.Combine(configHome, "mimeapps.list")));
            Assert.IsTrue(File.Exists(Path.Combine(configHome, "gnome-mimeapps.list")));
            Assert.IsTrue(File.Exists(Path.Combine(configHome, "unity-mimeapps.list")));
            Assert.IsTrue(service.GetStatus().IsRegistered);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", originalDataHome);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", originalConfigHome);
            Environment.SetEnvironmentVariable("XDG_CURRENT_DESKTOP", originalCurrentDesktop);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void LinuxTryRegister_ShouldCreateUserDesktopAndDefaultHandler_AndPreserveOtherEntries()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-nxm-linux-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var executable = Path.Combine(root, "launcher 100% $name.AppImage");
            Directory.CreateDirectory(root);
            File.WriteAllText(executable, "test launcher");
            var dataHome = Path.Combine(root, "data");
            var configHome = Path.Combine(root, "config");
            Directory.CreateDirectory(configHome);
            File.WriteAllText(Path.Combine(configHome, "mimeapps.list"),
                "[Added Associations]\ntext/plain=editor.desktop;\n\n[Default Applications]\ntext/plain=editor.desktop;\n");

            var result = LinuxNxmProtocolRegistration.TryRegister(executable, dataHome, configHome, desktopEnvironmentName: null);

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.IsTrue(result.IsSupported);
            Assert.IsTrue(result.IsRegistered);

            var desktopPath = Path.Combine(dataHome, "applications", LinuxNxmProtocolRegistration.DesktopFileId);
            var desktop = File.ReadAllText(desktopPath);
            StringAssert.Contains(desktop, "MimeType=x-scheme-handler/nxm;");
            var escapedExecutable = executable.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal)
                .Replace("$", "\\$", StringComparison.Ordinal)
                .Replace("`", "\\`", StringComparison.Ordinal)
                .Replace("%", "%%", StringComparison.Ordinal);
            StringAssert.Contains(desktop, $"Exec=\"{escapedExecutable}\" %u");

            var mimeApps = File.ReadAllText(Path.Combine(configHome, "mimeapps.list"));
            StringAssert.Contains(mimeApps, "text/plain=editor.desktop;");
            Assert.AreEqual(1, mimeApps.Split("x-scheme-handler/nxm=", StringSplitOptions.None).Length - 1);
            Assert.IsTrue(LinuxNxmProtocolRegistration.GetStatus(dataHome, configHome, desktopEnvironmentName: null).IsRegistered);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void LinuxTryRegister_ShouldBeIdempotent_WhenRepeated()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-nxm-linux-idempotent-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var executable = Path.Combine(root, "svl");
            File.WriteAllText(executable, "test launcher");
            var dataHome = Path.Combine(root, "data");
            var configHome = Path.Combine(root, "config");

            Assert.IsTrue(LinuxNxmProtocolRegistration.TryRegister(executable, dataHome, configHome, desktopEnvironmentName: null).IsRegistered);
            Assert.IsTrue(LinuxNxmProtocolRegistration.TryRegister(executable, dataHome, configHome, desktopEnvironmentName: null).IsRegistered);

            var mimeApps = File.ReadAllText(Path.Combine(configHome, "mimeapps.list"));
            Assert.AreEqual(1, mimeApps.Split("[Default Applications]", StringSplitOptions.None).Length - 1);
            Assert.AreEqual(1, mimeApps.Split("x-scheme-handler/nxm=", StringSplitOptions.None).Length - 1);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void LinuxMimeApps_ShouldAddDefaultSectionWhenItIsTheLastLine()
    {
        var result = LinuxNxmProtocolRegistration.UpsertDefaultHandler("[Added Associations]\ntext/plain=editor.desktop;\n[Default Applications]");

        StringAssert.Contains(result, "[Default Applications]\nx-scheme-handler/nxm=svl-avalonia-nxm.desktop;");
        StringAssert.Contains(result, "text/plain=editor.desktop;");
    }

    [TestMethod]
    public void LinuxTryRegister_ShouldUpdateCurrentDesktopSpecificDefault()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-nxm-linux-desktop-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var executable = Path.Combine(root, "svl");
            File.WriteAllText(executable, "test launcher");
            var dataHome = Path.Combine(root, "data");
            var configHome = Path.Combine(root, "config");
            Assert.IsTrue(LinuxNxmProtocolRegistration.TryRegister(
                executable, dataHome, configHome, desktopEnvironmentName: null).IsRegistered);

            var desktopMimeAppsPath = Path.Combine(configHome, "gnome-mimeapps.list");
            Directory.CreateDirectory(configHome);
            File.WriteAllText(Path.Combine(dataHome, "applications", "other-handler.desktop"),
                "[Desktop Entry]\nType=Application\nName=Other Handler\n" +
                "Exec=/usr/bin/true %u\nMimeType=x-scheme-handler/nxm;\n");
            File.WriteAllText(desktopMimeAppsPath,
                "[Default Applications]\nx-scheme-handler/nxm=other-handler.desktop;\n");
            Assert.IsFalse(LinuxNxmProtocolRegistration.GetStatus(dataHome, configHome, "gnome").IsRegistered);

            var result = LinuxNxmProtocolRegistration.TryRegister(executable, dataHome, configHome, "gnome");

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.IsTrue(result.IsRegistered);
            StringAssert.Contains(File.ReadAllText(desktopMimeAppsPath),
                "x-scheme-handler/nxm=svl-avalonia-nxm.desktop;");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void LinuxDesktopSpecificDefaults_ShouldFollowOrderedCurrentDesktopList()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-nxm-linux-desktops-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var executable = Path.Combine(root, "svl");
            File.WriteAllText(executable, "test launcher");
            var dataHome = Path.Combine(root, "data");
            var configHome = Path.Combine(root, "config");
            var desktops = new[] { "gnome", "unity" };

            var result = LinuxNxmProtocolRegistration.TryRegisterForDesktops(
                executable, dataHome, configHome, desktops);

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.IsTrue(result.IsRegistered);
            var gnomeMimeAppsPath = Path.Combine(configHome, "gnome-mimeapps.list");
            var unityMimeAppsPath = Path.Combine(configHome, "unity-mimeapps.list");
            Assert.IsTrue(File.Exists(gnomeMimeAppsPath));
            Assert.IsTrue(File.Exists(unityMimeAppsPath));
            var applicationsPath = Path.Combine(dataHome, "applications");
            File.WriteAllText(Path.Combine(applicationsPath, "generic-handler.desktop"),
                "[Desktop Entry]\nType=Application\nName=Generic Handler\n" +
                "Exec=/usr/bin/true %u\nMimeType=x-scheme-handler/nxm;\n");
            File.WriteAllText(Path.Combine(configHome, "mimeapps.list"),
                "[Default Applications]\nx-scheme-handler/nxm=generic-handler.desktop;\n");

            File.WriteAllText(gnomeMimeAppsPath,
                "[Default Applications]\ntext/plain=editor.desktop;\n");
            Assert.IsTrue(LinuxNxmProtocolRegistration.GetStatusForDesktops(
                dataHome, configHome, desktops).IsRegistered,
                "查找顺序应在首个桌面文件未设置 nxm 时继续检查下一个桌面文件。");

            File.WriteAllText(gnomeMimeAppsPath,
                "[Default Applications]\nx-scheme-handler/nxm=missing-handler.desktop;\n");
            Assert.IsTrue(LinuxNxmProtocolRegistration.GetStatusForDesktops(
                dataHome, configHome, desktops).IsRegistered,
                "不存在或未关联 nxm 的桌面项应跳过，继续查找后续桌面配置。");

            File.WriteAllText(Path.Combine(applicationsPath, "other-handler.desktop"),
                "[Desktop Entry]\nType=Application\nName=Other Handler\nExec=/usr/bin/true %u\n" +
                "[Desktop Action Open]\nName=Open\nMimeType=x-scheme-handler/nxm;\n");
            File.WriteAllText(gnomeMimeAppsPath,
                "[Default Applications]\nx-scheme-handler/nxm=other-handler.desktop;\n");
            Assert.IsTrue(LinuxNxmProtocolRegistration.GetStatusForDesktops(
                dataHome, configHome, desktops).IsRegistered,
                "Desktop Action 分组中的 MIME 字段不能代替主 Desktop Entry 关联。");

            File.WriteAllText(Path.Combine(applicationsPath, "other-handler.desktop"),
                "[Desktop Entry]\nType=Application\nName=Other Handler\n" +
                "Exec=/usr/bin/true %u\nMimeType=x-scheme-handler/nxm;\n");
            Assert.IsFalse(LinuxNxmProtocolRegistration.GetStatusForDesktops(
                dataHome, configHome, desktops).IsRegistered,
                "首个桌面特定配置中的有效处理器应优先于后续桌面和通用配置。");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void LinuxDesktopName_ShouldNormalizeSafeDesktopAndRejectPathSegments()
    {
        Assert.AreEqual("gnome", LinuxNxmProtocolRegistration.NormalizeDesktopEnvironmentName("GNOME:Unity"));
        Assert.AreEqual("xfce", LinuxNxmProtocolRegistration.NormalizeDesktopEnvironmentName("  XFCE  "));
        Assert.IsNull(LinuxNxmProtocolRegistration.NormalizeDesktopEnvironmentName("../../outside"));
        CollectionAssert.AreEqual(
            new[] { "gnome", "unity", "xfce" },
            LinuxNxmProtocolRegistration.NormalizeDesktopEnvironmentNames(
                "GNOME:Unity:XFCE:gnome:../../outside").ToArray());
    }

    [TestMethod]
    public void LinuxDesktopEntry_ShouldRejectNewlineInjection()
    {
        Assert.ThrowsException<ArgumentException>(() =>
            LinuxNxmProtocolRegistration.BuildDesktopEntry("/opt/svl\nExec=malicious"));

        var desktop = LinuxNxmProtocolRegistration.BuildDesktopEntry("/opt/SVL 100% $name \"quoted\"");
        StringAssert.Contains(desktop, "Exec=\"/opt/SVL 100%% \\$name \\\"quoted\\\"\" %u");
    }

    [TestMethod]
    public void MacOSBundleParser_ShouldRequireNxmUrlType_AndReadBundleId()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-nxm-macos-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var appPath = Path.Combine(root, "SVL.app");
            var contentsPath = Path.Combine(appPath, "Contents");
            var executable = Path.Combine(contentsPath, "MacOS", "SVL");
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            File.WriteAllText(executable, "test launcher");
            var infoPlistPath = Path.Combine(contentsPath, "Info.plist");
            File.WriteAllText(infoPlistPath,
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
                "<plist version=\"1.0\"><dict>" +
                "<key>CFBundleIdentifier</key><string>io.svl.launcher.test</string>" +
                "<key>CFBundleURLTypes</key><array><dict>" +
                "<key>CFBundleURLSchemes</key><array><string>nxm</string></array>" +
                "</dict></array></dict></plist>");

            var result = MacOSNxmProtocolRegistration.TryGetBundleInfo(executable, out var bundleId, out var resolvedAppPath, out var error);

            Assert.IsTrue(result, error);
            Assert.AreEqual("io.svl.launcher.test", bundleId);
            Assert.AreEqual(appPath, resolvedAppPath);

            File.WriteAllText(infoPlistPath,
                "<plist version=\"1.0\"><dict><key>CFBundleIdentifier</key>" +
                "<string>io.svl.launcher.test</string></dict></plist>");
            Assert.IsFalse(MacOSNxmProtocolRegistration.TryGetBundleInfo(executable, out _, out _, out error));
            StringAssert.Contains(error, "尚未在 Info.plist 声明 nxm");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
