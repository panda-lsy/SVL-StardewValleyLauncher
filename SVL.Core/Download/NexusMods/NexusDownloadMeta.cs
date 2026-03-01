using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using SVL.Core.Logging;

namespace SVL.Core.Download.NexusMods;

/// <summary>
/// NexusMods 下载元数据（.meta 文件）
/// 参考 Mod Organizer 实现，存储完整下载信息以支持：
/// - 断点续传
/// - 应用重启后恢复下载
/// - 多 CDN URL 备用
/// </summary>
public class NexusDownloadMeta
{
    /// <summary>
    /// 元数据版本
    /// </summary>
    public const int MetaVersion = 1;

    /// <summary>
    /// 游戏域名（如 stardewvalley）
    /// </summary>
    public string GameId { get; set; } = string.Empty;

    /// <summary>
    /// Mod ID
    /// </summary>
    public long ModId { get; set; }

    /// <summary>
    /// 文件 ID
    /// </summary>
    public long FileId { get; set; }

    /// <summary>
    /// Mod 名称
    /// </summary>
    public string ModName { get; set; } = string.Empty;

    /// <summary>
    /// 文件名称
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// 文件大小（字节）
    /// </summary>
    public long FileSize { get; set; }

    /// <summary>
    /// 所有 CDN 下载 URL
    /// </summary>
    public List<string> DownloadUrls { get; set; } = new();

    /// <summary>
    /// NXM key（如果有）
    /// </summary>
    public string? NxmKey { get; set; }

    /// <summary>
    /// NXM key 过期时间（Unix 时间戳）
    /// </summary>
    public long? NxmKeyExpires { get; set; }

    /// <summary>
    /// 用户 ID
    /// </summary>
    public long? UserId { get; set; }

    /// <summary>
    /// 已下载的字节数
    /// </summary>
    public long DownloadedBytes { get; set; }

    /// <summary>
    /// 最后更新时间
    /// </summary>
    public DateTime LastUpdateTime { get; set; } = DateTime.Now;

    /// <summary>
    /// 元数据版本
    /// </summary>
    public int Version { get; set; } = MetaVersion;

    /// <summary>
    /// 检查 NXM key 是否已过期
    /// </summary>
    public bool IsNxMKeyExpired
    {
        get
        {
            if (!NxmKeyExpires.HasValue)
                return false;

            var expiryDate = DateTimeOffset.FromUnixTimeSeconds(NxmKeyExpires.Value);
            return DateTimeOffset.UtcNow > expiryDate;
        }
    }

    /// <summary>
    /// 检查下载是否完成
    /// </summary>
    public bool IsComplete => DownloadedBytes >= FileSize && FileSize > 0;

    /// <summary>
    /// 获取文件对应的 .meta 文件路径
    /// </summary>
    public static string GetMetaFilePath(string downloadedFilePath)
    {
        return $"{downloadedFilePath}.meta";
    }
}

/// <summary>
/// NexusMods 下载元数据管理器
/// </summary>
public static class NexusDownloadMetaManager
{
    /// <summary>
    /// 保存元数据到 .meta 文件（JSON 格式）
    /// </summary>
    public static void SaveMeta(string downloadedFilePath, NexusDownloadMeta meta)
    {
        try
        {
            var metaPath = NexusDownloadMeta.GetMetaFilePath(downloadedFilePath);
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };
            var json = JsonSerializer.Serialize(meta, options);
            File.WriteAllText(metaPath, json, Encoding.UTF8);
            Log.Debug($"[Meta] 已保存元数据: {metaPath}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Meta] 保存元数据失败");
        }
    }

    /// <summary>
    /// 从 .meta 文件加载元数据
    /// </summary>
    public static NexusDownloadMeta? LoadMeta(string downloadedFilePath)
    {
        try
        {
            var metaPath = NexusDownloadMeta.GetMetaFilePath(downloadedFilePath);
            if (!File.Exists(metaPath))
                return null;

            var json = File.ReadAllText(metaPath, Encoding.UTF8);
            var meta = JsonSerializer.Deserialize<NexusDownloadMeta>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (meta != null)
            {
                // 验证版本兼容性
                if (meta.Version > NexusDownloadMeta.MetaVersion)
                {
                    Log.Warn($"[Meta] 元数据版本 {meta.Version} 高于当前版本 {NexusDownloadMeta.MetaVersion}");
                    return null;
                }

                // 验证文件是否仍然存在
                if (!File.Exists(downloadedFilePath))
                {
                    Log.Warn($"[Meta] 文件不存在，删除元数据: {downloadedFilePath}");
                    DeleteMeta(downloadedFilePath);
                    return null;
                }

                // 验证已下载字节数与实际文件大小
                var fileInfo = new FileInfo(downloadedFilePath);
                if (fileInfo.Length != meta.DownloadedBytes)
                {
                    Log.Warn($"[Meta] 文件大小不匹配: 预期 {meta.DownloadedBytes}, 实际 {fileInfo.Length}");
                    meta.DownloadedBytes = fileInfo.Length; // 更新为实际大小
                    SaveMeta(downloadedFilePath, meta);
                }

                Log.Info($"[Meta] 已加载元数据: {Path.GetFileName(downloadedFilePath)}, 进度: {meta.DownloadedBytes}/{meta.FileSize}");
            }

            return meta;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Meta] 加载元数据失败");
            return null;
        }
    }

    /// <summary>
    /// 删除 .meta 文件
    /// </summary>
    public static void DeleteMeta(string downloadedFilePath)
    {
        try
        {
            var metaPath = NexusDownloadMeta.GetMetaFilePath(downloadedFilePath);
            if (File.Exists(metaPath))
            {
                File.Delete(metaPath);
                Log.Debug($"[Meta] 已删除元数据: {metaPath}");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Meta] 删除元数据失败");
        }
    }

    /// <summary>
    /// 更新已下载字节数
    /// </summary>
    public static void UpdateProgress(string downloadedFilePath, long downloadedBytes)
    {
        try
        {
            var meta = LoadMeta(downloadedFilePath);
            if (meta != null)
            {
                meta.DownloadedBytes = downloadedBytes;
                meta.LastUpdateTime = DateTime.Now;
                SaveMeta(downloadedFilePath, meta);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Meta] 更新进度失败");
        }
    }

    /// <summary>
    /// 创建新的元数据
    /// </summary>
    public static NexusDownloadMeta CreateMeta(
        string gameId,
        long modId,
        long fileId,
        string modName,
        string fileName,
        long fileSize,
        List<string> downloadUrls,
        string? nxmKey = null,
        long? nxmKeyExpires = null,
        long? userId = null)
    {
        return new NexusDownloadMeta
        {
            GameId = gameId,
            ModId = modId,
            FileId = fileId,
            ModName = modName,
            FileName = fileName,
            FileSize = fileSize,
            DownloadUrls = downloadUrls,
            NxmKey = nxmKey,
            NxmKeyExpires = nxmKeyExpires,
            UserId = userId,
            DownloadedBytes = 0,
            LastUpdateTime = DateTime.Now,
            Version = NexusDownloadMeta.MetaVersion
        };
    }
}
