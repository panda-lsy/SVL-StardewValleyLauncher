using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SVL.Avalonia.Services;

namespace SVL.Avalonia.Services;

/// <summary>
/// 匿名用户统计服务。
/// 仅上报匿名实例标识（SHA-256 哈希）与基础运行信息，不采集用户名、路径等隐私数据。
/// 参考旧架构 SVL.Desktop.Services.AnonymousUsageTelemetryService 实现。
/// </summary>
public static class AnonymousUsageTelemetryService
{
    private const string TelemetryEndpoint = "https://api.svl.qzz.io/api/stats/usage/report";

    private static readonly string TelemetryStatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SVL",
        "telemetry",
        "usage-state.json");

    /// <summary>
    /// 每日上报一次匿名活跃信息（失败静默）。
    /// </summary>
    public static async Task ReportDailyActiveAsync()
    {
        try
        {
            // 支持环境变量一键关闭统计
            var disableFlag = Environment.GetEnvironmentVariable("SVL_DISABLE_ANON_TELEMETRY");
            if (string.Equals(disableFlag, "1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(disableFlag, "true", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var state = LoadState();
            var todayUtc = DateTime.UtcNow.Date;
            if (state.LastReportedDateUtc.HasValue && state.LastReportedDateUtc.Value >= todayUtc)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(state.InstallationId))
            {
                state.InstallationId = Guid.NewGuid().ToString("N");
            }

            var payload = new UsagePayload
            {
                AnonymousId = ComputeSha256(state.InstallationId),
                Version = (System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString()) ?? "1.2.0.0",
                Runtime = "net10.0",
                OsVersion = Environment.OSVersion.VersionString,
                TimestampUtc = DateTime.UtcNow
            };

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            var jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };
            using var content = new StringContent(
                JsonSerializer.Serialize(payload, jsonOptions),
                Encoding.UTF8,
                "application/json");

            var response = await client.PostAsync(TelemetryEndpoint, content);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            state.LastReportedDateUtc = todayUtc;
            SaveState(state);
        }
        catch
        {
            // 失败静默，不影响启动器正常使用
        }
    }

    private static TelemetryState LoadState()
    {
        try
        {
            if (!File.Exists(TelemetryStatePath))
            {
                return new TelemetryState();
            }

            var json = File.ReadAllText(TelemetryStatePath);
            return JsonSerializer.Deserialize<TelemetryState>(json) ?? new TelemetryState();
        }
        catch
        {
            return new TelemetryState();
        }
    }

    private static void SaveState(TelemetryState state)
    {
        try
        {
            var dir = Path.GetDirectoryName(TelemetryStatePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(TelemetryStatePath, json);
        }
        catch
        {
            // best-effort
        }
    }

    private static string ComputeSha256(string input)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
        {
            sb.Append(b.ToString("x2"));
        }

        return sb.ToString();
    }

    private sealed class TelemetryState
    {
        [JsonPropertyName("installationId")]
        public string InstallationId { get; set; } = string.Empty;

        [JsonPropertyName("lastReportedDateUtc")]
        public DateTime? LastReportedDateUtc { get; set; }
    }

    private sealed class UsagePayload
    {
        public string AnonymousId { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string Runtime { get; set; } = string.Empty;
        public string OsVersion { get; set; } = string.Empty;
        public DateTime TimestampUtc { get; set; }
    }
}
