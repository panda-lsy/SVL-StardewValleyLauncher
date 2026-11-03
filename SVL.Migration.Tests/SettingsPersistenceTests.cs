using Avalonia.Headless;
using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SVL.Avalonia.Models;
using SVL.Avalonia.Services;
using SVL.Avalonia.ViewModels;
using SVL.Core.Platform.Abstractions;

namespace SVL.Migration.Tests;

[TestClass]
public sealed class SettingsPersistenceTests
{
    [TestMethod]
    public async Task AutoDownload_ShouldWorkWithoutShowingTheUpdateDialog()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-update-silent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(AvaloniaUiSmokeTests).Assembly);
            await session.Dispatch(async () =>
            {
                var payload = Encoding.UTF8.GetBytes("update file");
                var handler = new FakeUpdateHandler(payload);
                var service = new LauncherUpdateService(new HttpClient(handler), Path.Combine(root, "downloads"), LauncherRuntimeMode.SelfContained);
                var store = new AppUserSettingsStore(Path.Combine(root, "config"));
                var vm = CreateViewModel(store, service);
                vm.ShowUpdateNotification = false;
                vm.AutoDownloadUpdate = true;
                Assert.IsTrue(vm.FlushPendingSettings());
                var release = new LauncherReleaseInfo();
                var asset = new LauncherReleaseAsset { Name = "SVL_Release.exe", BrowserDownloadUrl = "https://example.test/file.exe", Size = payload.Length };
                release.Assets.Add(asset);
                await vm.DownloadUpdateInBackgroundAsync(new LauncherUpdateCheckResult { ReleaseInfo = release });
                StringAssert.Contains(vm.UpdateStatusText, "已下载");
                await service.DownloadAssetAsync(asset, null, CancellationToken.None);
                Assert.AreEqual(1, handler.Requests, "后台下载应供后续安装复用");
                return true;
            }, CancellationToken.None);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void LauncherUpdatePreferences_ShouldInterpretPersistedDisplayLabels()
    {
        Assert.IsTrue(LauncherUpdateService.IncludesPrerelease("预览版"));
        Assert.IsTrue(LauncherUpdateService.IncludesPrerelease("preview"));
        Assert.IsFalse(LauncherUpdateService.IncludesPrerelease("稳定版"));
        Assert.IsTrue(LauncherUpdateService.PrefersGitee("Gitee (国内加速)"));
        Assert.IsFalse(LauncherUpdateService.PrefersGitee("GitHub (推荐)"));
    }

    [TestMethod]
    public async Task GlobalPreferences_ShouldEachSchedulePersistence()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-settings-coverage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(AvaloniaUiSmokeTests).Assembly);
            await session.Dispatch(() =>
            {
                var store = new AppUserSettingsStore(root);
                store.Save(new AppUserSettings());
                var vm = CreateViewModel(store);
                var cases = new (string Name, Action Change, Func<AppUserSettings, bool> Saved)[]
                {
                    ("游戏窗口标题", () => vm.GameWindowTitle = "测试标题", s => s.GameWindowTitle == "测试标题"),
                    ("启动器标题", () => vm.LauncherTitle = "测试启动器", s => s.LauncherTitle == "测试启动器"),
                    ("启动器简称", () => vm.LauncherAppName = "测试", s => s.LauncherAppName == "测试"),
                    ("游戏启动后行为", () => vm.SelectedLauncherVisibilityIndex = 4, s => s.LauncherVisibility == 4),
                    ("自动连接", () => vm.InstanceAutoConnectServer = true, s => s.InstanceAutoConnectServer),
                    ("服务器地址", () => vm.InstanceServerAddress = "example.test", s => s.InstanceServerAddress == "example.test"),
                    ("邀请码", () => vm.InstanceSteamInviteCode = "test-code", s => s.InstanceSteamInviteCode == "test-code"),
                    ("下载缓存", () => vm.EnableDownloadCache = false, s => !s.EnableDownloadCache),
                    ("下载代理", () => vm.EnableDownloadProxy = true, s => s.EnableDownloadProxy),
                    ("代理地址", () => vm.DownloadProxyUrl = "http://localhost:7890", s => s.DownloadProxyUrl == "http://localhost:7890"),
                    ("代理用户名", () => vm.DownloadProxyUserName = "user", s => s.DownloadProxyUserName == "user"),
                    ("代理密码", () => vm.DownloadProxyPassword = "password", s => s.DownloadProxyPassword == "password"),
                    ("任务浮动按钮", () => vm.EnableDownloadFloatingTaskButton = false, s => !s.EnableDownloadFloatingTaskButton),
                    ("整合包冲突策略", () => vm.SelectedCollectionConflictStrategy = "跳过", s => s.CollectionInstallConflictStrategy == "跳过"),
                    ("下载任务并发", () => vm.SelectedCollectionDownloadParallelism = 5, s => s.CollectionDownloadParallelism == 5),
                    ("下载线程数", () => vm.SelectedDownloadThreads = 8, s => s.DownloadSegmentThreads == 8),
                    ("Nexus 登录失效提示", () => vm.EnableNexusAuthNotification = false, s => s.SuppressNexusAuthNotification),
                    ("NXM 自动注册", () => vm.RegisterNxmProtocolOnStartup = false, s => !s.RegisterNxmProtocolOnStartup),
                    ("汉化源", () => vm.SelectedLocalizationSource = "GitHub", s => s.LocalizationPreferredSource == "GitHub"),
                    ("搜索缓存时长", () => vm.CacheRetentionMinutes = 15, s => s.CacheRetentionMinutes == 15),
                    ("下载缓存时长", () => vm.DownloadCacheRetentionMinutes = 1440, s => s.DownloadCacheRetentionMinutes == 1440),
                    ("更新检测并发", () => vm.SelectedModUpdateConcurrency = 6, s => s.MaxConcurrentModUpdateChecks == 6),
                    ("汉化检测并发", () => vm.SelectedModLocalizationConcurrency = 8, s => s.MaxConcurrentModLocalizationChecks == 8),
                    ("主题模式", () => vm.SelectedThemeMode = "深色", s => s.ThemeMode == "深色"),
                    ("配色方案", () => vm.SelectedThemeStyle = "森林绿", s => s.ThemeColorScheme == "Green"),
                    ("强调色", () => vm.PrimaryColor = "#123456", s => s.PrimaryColor == "#123456"),
                    ("动画", () => vm.EnableAnimations = false, s => !s.EnableAnimations),
                    ("透明度", () => vm.BackgroundTransparencyPercent = 43, s => s.BackgroundTransparencyPercent == 43),
                    ("字号", () => vm.FontSize = 18, s => s.FontSize == 18),
                    ("语言", () => vm.SelectedUiLanguage = "en-US", s => s.UiLanguage == "en-US"),
                    ("显示通知", () => vm.ShowNotifications = false, s => !s.ShowNotifications),
                    ("类型筛选提示", () => vm.ShowModTypeFilterDisabledNotice = false, s => !s.ShowModTypeFilterDisabledNotice),
                    ("调试模式", () => vm.DebugMode = true, s => s.DebugMode),
                    ("启动时最小化", () => vm.MinimizeToTrayOnStartup = true, s => s.MinimizeToTrayOnStartup),
                    ("关闭时最小化", () => vm.MinimizeToTrayOnClose = true, s => s.MinimizeToTrayOnClose),
                    ("自动检查更新", () => vm.EnableAutoUpdateCheck = false, s => !s.EnableAutoUpdateCheck),
                    ("自动下载更新", () => vm.AutoDownloadUpdate = true, s => s.AutoDownloadUpdate),
                    ("更新通知", () => vm.ShowUpdateNotification = false, s => !s.ShowUpdateNotification),
                    ("更新来源", () => vm.SelectedUpdateSource = "Gitee (国内加速)", s => s.PreferredUpdateSource == "Gitee (国内加速)"),
                    ("更新通道", () => vm.SelectedUpdateChannel = "预览版", s => s.UpdateChannel == "预览版")
                };

                foreach (var testCase in cases)
                {
                    testCase.Change();
                    Assert.IsTrue(vm.FlushPendingSettings(), $"{testCase.Name} 保存失败");
                    Assert.IsTrue(testCase.Saved(store.Load()), $"{testCase.Name} 变更未触发持久化");
                }

                Assert.AreEqual("Settings", vm.PageTitleText, "选择语言应立即更新界面");
                Assert.IsFalse(vm.CacheRetentionOptionsMinutes.Contains(43200), "搜索缓存时长不能提供会被截断的选项");
                Assert.IsFalse(vm.DownloadCacheRetentionOptionsMinutes.Any(value => value < 60), "下载缓存时长不能低于一小时");
                vm.PrimaryColor = "not-a-color";
                Assert.IsTrue(vm.FlushPendingSettings());
                Assert.AreEqual("#123456", store.Load().PrimaryColor, "无效颜色不能覆盖已保存的颜色");
                vm.SaveSettingsCommand.Execute(null);
                StringAssert.Contains(vm.StatusMessage, "强调色未保存", "手动保存时必须说明无效颜色未保存");
            }, CancellationToken.None);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Settings_ShouldSaveDebugAndInputChangesAutomaticallyAndFlushBeforeReload()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-settings-save-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(AvaloniaUiSmokeTests).Assembly);
            await session.Dispatch(async () =>
            {
                var store = new AppUserSettingsStore(root);
                store.Save(new AppUserSettings { PreferredInstancePath = "preserved-instance" });
                var viewModel = CreateViewModel(store);

                viewModel.DebugMode = true;
                StringAssert.Contains(viewModel.StatusMessage, "重启后生效");
                await Task.Delay(1400);
                Assert.IsTrue(store.Load().DebugMode, "调试开关应在防抖后保存");
                StringAssert.Contains(viewModel.StatusMessage, "已自动保存");

                viewModel.GameWindowTitle = "测试窗口";
                viewModel.EnableDownloadProxy = true;
                viewModel.DownloadProxyUrl = "http://127.0.0.1:7890";
                viewModel.InstanceServerAddress = "example.test:24642";
                await Task.Delay(1400);
                var autoSaved = store.Load();
                Assert.AreEqual("测试窗口", autoSaved.GameWindowTitle);
                Assert.IsTrue(autoSaved.EnableDownloadProxy);
                Assert.AreEqual("http://127.0.0.1:7890", autoSaved.DownloadProxyUrl);
                Assert.AreEqual("example.test:24642", autoSaved.InstanceServerAddress);
                Assert.AreEqual("preserved-instance", autoSaved.PreferredInstancePath);

                viewModel.FontSize = 18;
                viewModel.SaveSettingsCommand.Execute(null);
                Assert.AreEqual(18, store.Load().FontSize, "保存按钮应立即写入设置");
                StringAssert.Contains(viewModel.StatusMessage, "设置已保存");

                viewModel.BackgroundTransparencyPercent = 42;
                viewModel.ReloadFromSettings();
                Assert.AreEqual(42, store.Load().BackgroundTransparencyPercent,
                    "重新加载前必须先写入尚在防抖的设置");
                Assert.AreEqual(42d, viewModel.BackgroundTransparencyPercent);
                return true;
            }, CancellationToken.None);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task SaveSettings_ShouldReportWriteFailures()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-settings-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(AvaloniaUiSmokeTests).Assembly);
            await session.Dispatch(() =>
            {
                var store = new AppUserSettingsStore(root);
                Directory.CreateDirectory(store.GetSettingsPath());
                var viewModel = CreateViewModel(store);
                viewModel.DebugMode = true;

                viewModel.SaveSettingsCommand.Execute(null);

                StringAssert.Contains(viewModel.StatusMessage, "保存设置失败");
            }, CancellationToken.None);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static SettingsPageViewModel CreateViewModel(AppUserSettingsStore store, LauncherUpdateService updateService = null)
    {
        var localization = new LocalizationService(store);
        return new SettingsPageViewModel(
            store, new DialogService(), new NexusAuthService(), new NexusOAuthService(),
            updateService ?? new LauncherUpdateService(), new FakeExternalProcessService(), new FakeNxmRegistrationService(),
            localization, new ImageResourceService(localization));
    }

    private sealed class FakeUpdateHandler(byte[] payload) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        }
    }

    private sealed class FakeExternalProcessService : IExternalProcessService
    {
        public bool TryOpenUrl(string url) => true;
        public bool TryOpenPath(string path) => true;
        public bool TryLaunchProcess(string fileName, string arguments, string workingDirectory = null) => true;
        public int RunCommand(string fileName, string arguments, string workingDirectory = null) => 0;
    }

    private sealed class FakeNxmRegistrationService : INxmProtocolRegistrationService
    {
        public NxmProtocolRegistrationResult GetStatus() => new() { IsSupported = true };
        public NxmProtocolRegistrationResult TryRegister(string launcherExecutablePath) => new() { IsSuccess = true };
    }
}
