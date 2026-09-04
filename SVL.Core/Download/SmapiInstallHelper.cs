using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SVL.Core.Download.NexusMods;
using SVL.Core.Logging;
using SVL.Core.Stardew.Mod.SMAPI;

namespace SVL.Core.Download;

public sealed class SmapiDownloadProgress
{
    public double Percentage { get; set; }

    public long BytesReceived { get; set; }

    public long TotalBytes { get; set; }

    public string Message { get; set; } = string.Empty;
}

public sealed class SmapiDownloadResult
{
    public bool Success { get; set; }

    public string ZipPath { get; set; } = string.Empty;

    public string SuccessSource { get; set; } = string.Empty;

    public bool TokenExpired { get; set; }

    public string[] Errors { get; set; } = Array.Empty<string>();
}

public static class SmapiInstallHelper
{
    public static async Task<bool> SetupIsolatedSmapiAsync(
        string smapiZipPath,
        string gameBasePath,
        string gameFilesPath,
        string modsPath = null,
        Action<double> progressCallback = null)
    {
        try
        {
            Directory.CreateDirectory(gameFilesPath);
            progressCallback?.Invoke(0.1);

            CopyBaseGameFiles(gameBasePath, gameFilesPath);
            progressCallback?.Invoke(0.55);

            var installed = await SmapApiService.InstallFromZipAsync(
                smapiZipPath,
                gameFilesPath,
                p => progressCallback?.Invoke(0.55 + (p * 0.45)),
                enableIsolation: true);

            if (!installed)
            {
                return false;
            }

            var targetModsPath = string.IsNullOrWhiteSpace(modsPath)
                ? Path.Combine(gameFilesPath, "Mods")
                : modsPath;
            Directory.CreateDirectory(targetModsPath);
            progressCallback?.Invoke(1);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("[SmapiInstallHelper] SetupIsolatedSmapiAsync failed", ex);
            return false;
        }
    }

    public static string NormalizeSmapiVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return "latest";
        }

        return version.Trim().TrimStart('v', 'V');
    }

    public static async Task<SmapiDownloadResult> DownloadSmapiZipAsync(
        string smapiVersion,
        Action<SmapiDownloadProgress> progressCallback = null,
        Func<NexusPremiumRequiredException, Task<string>> onPremiumRequired = null,
        CancellationToken cancellationToken = default)
    {
        var errors = new System.Collections.Generic.List<string>();
        var normalizedVersion = NormalizeSmapiVersion(smapiVersion);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var zipPath = await SmapApiService.DownloadSmapiAsync(
                normalizedVersion,
                (progress, bytesRead, totalBytes) =>
                {
                    progressCallback?.Invoke(new SmapiDownloadProgress
                    {
                        Percentage = progress,
                        BytesReceived = bytesRead,
                        TotalBytes = totalBytes,
                        Message = $"正在下载 SMAPI {normalizedVersion}... {(int)(progress * 100)}%"
                    });
                });

            if (!string.IsNullOrWhiteSpace(zipPath) && File.Exists(zipPath))
            {
                return new SmapiDownloadResult
                {
                    Success = true,
                    ZipPath = zipPath,
                    SuccessSource = "GitHub",
                    TokenExpired = false,
                    Errors = Array.Empty<string>()
                };
            }

            errors.Add("GitHub: 下载结果为空");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            errors.Add($"GitHub: {ex.Message}");
            Log.Warn("[SmapiInstallHelper] DownloadSmapiZipAsync github source failed", ex);
        }

        return new SmapiDownloadResult
        {
            Success = false,
            ZipPath = string.Empty,
            SuccessSource = string.Empty,
            TokenExpired = false,
            Errors = errors.ToArray()
        };
    }

    private static void CopyBaseGameFiles(string sourcePath, string targetPath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !Directory.Exists(sourcePath))
        {
            return;
        }

        var skipDirectories = new[]
        {
            "Mods",
            "versions",
            "_disabled",
            "Backups"
        };

        foreach (var directory in Directory.GetDirectories(sourcePath, "*", SearchOption.AllDirectories))
        {
            var relative = GetRelativePathCompat(sourcePath, directory);
            if (skipDirectories.Any(skip => relative.StartsWith(skip, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            Directory.CreateDirectory(Path.Combine(targetPath, relative));
        }

        foreach (var file in Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories))
        {
            var relative = GetRelativePathCompat(sourcePath, file);
            if (skipDirectories.Any(skip => relative.StartsWith(skip + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var targetFile = Path.Combine(targetPath, relative);
            var parent = Path.GetDirectoryName(targetFile);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                Directory.CreateDirectory(parent);
            }
            File.Copy(file, targetFile, true);
        }
    }

    private static string GetRelativePathCompat(string basePath, string fullPath)
    {
        var normalizedBase = Path.GetFullPath(basePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var normalizedFull = Path.GetFullPath(fullPath);

        if (!normalizedFull.StartsWith(normalizedBase, StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFileName(fullPath);
        }

        return normalizedFull.Substring(normalizedBase.Length);
    }
}
