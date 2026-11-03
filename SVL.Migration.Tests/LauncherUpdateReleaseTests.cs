using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SVL.Avalonia.Controls;
using SVL.Avalonia.Models;
using SVL.Avalonia.Services;

namespace SVL.Migration.Tests;

[TestClass]
public sealed class LauncherUpdateReleaseTests
{
    [TestMethod]
    public async Task UpdateTxt_ShouldBeFetchedAndShownInTheUpdateDialog()
    {
        const string updateText = "v9999 更新内容\n\n跨平台：\n  修复启动问题";
        var handler = new ReleaseHandler(updateText);
        var service = new LauncherUpdateService(new HttpClient(handler), null, LauncherRuntimeMode.FrameworkDependent);
        var result = await service.CheckForUpdateAsync(includePrerelease: false, preferGitee: false);

        Assert.IsTrue(result.Success, result.ErrorMessage);
        Assert.IsTrue(result.HasUpdate);
        Assert.AreEqual(updateText, result.ReleaseInfo.UpdateLog);
        Assert.AreEqual(1, handler.UpdateLogRequests);
        Assert.IsTrue(service.SelectPreferredAsset(result.ReleaseInfo).Name.Contains("requires_dotnet10"));

        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(AvaloniaUiSmokeTests).Assembly);
        await session.Dispatch(() =>
        {
            var dialog = new UpdateDialog(result.CurrentVersion, result.ReleaseInfo, result.Source);
            dialog.InitializeForDownload(service);
            try
            {
                dialog.Show();
                dialog.UpdateLayout();
                Assert.AreEqual(updateText, dialog.FindControl<TextBox>("ChangelogText").Text);
                StringAssert.Contains(dialog.FindControl<TextBlock>("RuntimeModeText").Text, "轻量版");
                Assert.IsTrue(dialog.FindControl<Button>("DownloadButton").IsVisible);
                Assert.IsTrue(dialog.FindControl<Button>("OpenReleaseButton").IsVisible);
            }
            finally { dialog.Close(); }
        }, CancellationToken.None);
    }

    [TestMethod]
    public async Task MissingUpdateTxt_ShouldFallBackToReleaseBodyAndKeepManualChoice()
    {
        var handler = new ReleaseHandler(updateText: null);
        var service = new LauncherUpdateService(new HttpClient(handler), null, LauncherRuntimeMode.SelfContained);
        var result = await service.CheckForUpdateAsync(includePrerelease: false, preferGitee: false);
        Assert.IsTrue(result.Success, result.ErrorMessage);
        Assert.AreEqual("备用发布说明", result.ReleaseInfo.UpdateLog);
        Assert.IsNull(service.SelectPreferredAsset(result.ReleaseInfo));

        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(AvaloniaUiSmokeTests).Assembly);
        await session.Dispatch(() =>
        {
            var dialog = new UpdateDialog(result.CurrentVersion, result.ReleaseInfo, result.Source);
            dialog.InitializeForDownload(service, autoDownload: true);
            try
            {
                dialog.Show();
                dialog.UpdateLayout();
                Assert.IsFalse(dialog.FindControl<Button>("DownloadButton").IsVisible);
                Assert.IsTrue(dialog.FindControl<TextBlock>("RuntimeWarningText").IsVisible);
                Assert.IsTrue(dialog.FindControl<Button>("OpenReleaseButton").IsVisible);
            }
            finally { dialog.Close(); }
        }, CancellationToken.None);
    }

    private sealed class ReleaseHandler(string updateText) : HttpMessageHandler
    {
        public int UpdateLogRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri.AbsolutePath == "/update.txt")
            {
                UpdateLogRequests++;
                return Task.FromResult(updateText == null
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(Encoding.UTF8.GetBytes(updateText))
                    });
            }

            var platform = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux";
            var arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
            var extension = OperatingSystem.IsMacOS() ? "dmg" : "zip";
#if DEBUG
            const string configuration = "Debug";
#else
            const string configuration = "Release";
#endif
            var releases = new[]
            {
                new
                {
                    tag_name = "v9999.0.0.0",
                    name = "测试版",
                    body = "备用发布说明",
                    html_url = "https://example.test/release",
                    prerelease = false,
                    assets = new[]
                    {
                        new { name = "update.txt", browser_download_url = "https://example.test/update.txt" },
                        new { name = $"SVL_v9999.0.0.0_{configuration}_{platform}_{arch}_requires_dotnet10.{extension}", browser_download_url = "https://example.test/asset" }
                    }
                }
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(releases), Encoding.UTF8, "application/json")
            });
        }
    }
}
