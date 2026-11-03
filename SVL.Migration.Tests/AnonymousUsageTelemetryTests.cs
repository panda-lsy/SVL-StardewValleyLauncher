using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SVL.Avalonia.Services;

namespace SVL.Migration.Tests;

[TestClass]
public sealed class AnonymousUsageTelemetryTests
{
    private static readonly DateTime NowUtc = new(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc);
    private const string LegacyId = "legacy-installation-for-test";

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task AlreadyReportedToday_ShouldNotReportAgain_ForOldAndNewStateFormats(bool legacy)
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "usage-state.json");
            var json = StateJson(legacy, NowUtc.Date);
            File.WriteAllText(path, json);
            var handler = new FakeTelemetryHandler();
            using var client = new HttpClient(handler);

            await AnonymousUsageTelemetryService.ReportDailyActiveAsync(client, path, NowUtc, null);

            Assert.AreEqual(0, handler.Bodies.Count, "同一 UTC 日不应重复上报");
            Assert.AreEqual(json, File.ReadAllText(path), "跳过上报时应保留原状态");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task LegacyState_NextDay_ShouldReuseIdentityAndPersistSuccessfulReport()
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "usage-state.json");
            File.WriteAllText(path, StateJson(true, NowUtc.Date.AddDays(-1)));
            var handler = new FakeTelemetryHandler();
            using var client = new HttpClient(handler);

            await AnonymousUsageTelemetryService.ReportDailyActiveAsync(client, path, NowUtc, null);
            await AnonymousUsageTelemetryService.ReportDailyActiveAsync(client, path, NowUtc, null);

            Assert.AreEqual(1, handler.Bodies.Count);
            using var payload = JsonDocument.Parse(handler.Bodies[0]);
            var expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(LegacyId))).ToLowerInvariant();
            Assert.AreEqual(expectedHash, payload.RootElement.GetProperty("anonymousId").GetString());
            Assert.AreEqual("net10.0", payload.RootElement.GetProperty("runtime").GetString());
            using var state = JsonDocument.Parse(File.ReadAllText(path));
            Assert.AreEqual(LegacyId, state.RootElement.GetProperty("installationId").GetString());
            Assert.AreEqual(NowUtc.Date, state.RootElement.GetProperty("lastReportedDateUtc").GetDateTime().Date);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task FailedReport_ShouldPreserveLegacyStateAndRetryWithSameIdentity()
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "usage-state.json");
            var original = StateJson(true, NowUtc.Date.AddDays(-1));
            File.WriteAllText(path, original);
            var handler = new FakeTelemetryHandler { Status = HttpStatusCode.ServiceUnavailable };
            using var client = new HttpClient(handler);

            await AnonymousUsageTelemetryService.ReportDailyActiveAsync(client, path, NowUtc, null);
            Assert.AreEqual(original, File.ReadAllText(path), "上报失败不能覆盖旧版标识");

            handler.Status = HttpStatusCode.OK;
            await AnonymousUsageTelemetryService.ReportDailyActiveAsync(client, path, NowUtc, null);
            Assert.AreEqual(2, handler.Bodies.Count);
            using var first = JsonDocument.Parse(handler.Bodies[0]);
            using var second = JsonDocument.Parse(handler.Bodies[1]);
            Assert.AreEqual(first.RootElement.GetProperty("anonymousId").GetString(),
                second.RootElement.GetProperty("anonymousId").GetString());
            using var state = JsonDocument.Parse(File.ReadAllText(path));
            Assert.AreEqual(LegacyId, state.RootElement.GetProperty("installationId").GetString());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task DisabledTelemetry_ShouldNotSendOrCreateState()
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "usage-state.json");
            var handler = new FakeTelemetryHandler();
            using var client = new HttpClient(handler);

            await AnonymousUsageTelemetryService.ReportDailyActiveAsync(client, path, NowUtc, "true");

            Assert.AreEqual(0, handler.Bodies.Count);
            Assert.IsFalse(File.Exists(path));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string StateJson(bool legacy, DateTime reportedDate) => legacy
        ? JsonSerializer.Serialize(new { InstallationId = LegacyId, LastReportedDateUtc = reportedDate })
        : JsonSerializer.Serialize(new { installationId = LegacyId, lastReportedDateUtc = reportedDate });

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "svl-telemetry-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakeTelemetryHandler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public List<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(Status);
        }
    }
}
