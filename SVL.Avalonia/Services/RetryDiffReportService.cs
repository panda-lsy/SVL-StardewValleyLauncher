using System.Text.Json;

namespace SVL.Avalonia.Services;

public sealed class RetryDiffReportService
{
    public string Write(string downloadRootPath, string taskName, IReadOnlyList<string> failedBefore, IReadOnlyList<string> failedAfter)
    {
        if (failedBefore.Count == 0 && failedAfter.Count == 0)
        {
            return string.Empty;
        }

        try
        {
            var reportDir = Path.Combine(downloadRootPath, "retry-reports");
            Directory.CreateDirectory(reportDir);

            var recovered = failedBefore
                .Where(url => !failedAfter.Contains(url, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var stillFailed = failedAfter
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var report = new RetryDiffReport
            {
                TaskName = taskName,
                CreatedAtUtc = DateTime.UtcNow,
                FailedBefore = failedBefore.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                FailedAfter = stillFailed,
                RecoveredItems = recovered
            };

            var fileName = $"retry-diff-{DateTime.Now:yyyyMMddHHmmssfff}.json";
            var path = Path.Combine(reportDir, fileName);
            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
            return path;
        }
        catch
        {
            return string.Empty;
        }
    }
}

internal sealed class RetryDiffReport
{
    public string TaskName { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    public List<string> FailedBefore { get; set; } = [];

    public List<string> FailedAfter { get; set; } = [];

    public List<string> RecoveredItems { get; set; } = [];
}
