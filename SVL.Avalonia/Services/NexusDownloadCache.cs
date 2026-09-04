using System.IO;

namespace SVL.Avalonia.Services;

/// <summary>
/// Nexus 资源下载缓存：按 ModID_FileID 缓存已下载的 ZIP，避免下次再弹浏览器下载指引。
/// 与通用 URL 哈希缓存不同，这里按浏览器指引 URL 所携带的 ModID/FileID 唯一键缓存。
/// </summary>
public static class NexusDownloadCache
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SVL", "Avalonia", "cache", "nexus");

    /// <summary>缓存文件路径（ModID_FileID.zip）。</summary>
    public static string GetCachePath(long modId, long fileId)
        => Path.Combine(Root, $"{modId}_{fileId}.zip");

    /// <summary>是否命中缓存。</summary>
    public static bool TryGet(long modId, long fileId, out string path)
    {
        path = GetCachePath(modId, fileId);
        return File.Exists(path) && new FileInfo(path).Length > 0;
    }

    /// <summary>保存已下载文件到缓存（best-effort）。</summary>
    public static void Save(long modId, long fileId, string sourceFile)
    {
        var tempPath = string.Empty;
        try
        {
            if (string.IsNullOrWhiteSpace(sourceFile) || !File.Exists(sourceFile))
            {
                return;
            }

            Directory.CreateDirectory(Root);
            var cachePath = GetCachePath(modId, fileId);
            tempPath = cachePath + ".tmp-" + Guid.NewGuid().ToString("N");
            File.Copy(sourceFile, tempPath, overwrite: true);
            File.Move(tempPath, cachePath, overwrite: true);
        }
        catch
        {
            // best-effort
        }
        finally
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(tempPath) && File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // best-effort
            }
        }
    }

    /// <summary>清理 Nexus 缓存。</summary>
    public static void Clear()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch
        {
            // best-effort
        }
    }
}
