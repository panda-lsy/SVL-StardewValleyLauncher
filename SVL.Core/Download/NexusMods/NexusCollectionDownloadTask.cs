using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SVL.Core.Config;
using SVL.Core.Logging;
using SVL.Core.Stardew.ResourceProject.NexusMods;

namespace SVL.Core.Download.NexusMods;

/// <summary>
/// NexusMods 集合下载任务
/// </summary>
public class NexusCollectionDownloadTask : DownloadTask
{
    private readonly string _gameId;
    private readonly string _collectionSlug;
    private readonly int _revisionNumber;
    private readonly string _downloadDirectory;
    private readonly string? _oauthToken;
    private readonly CancellationTokenSource _cts = new();

    private int _totalMods = 0;

    // 安装相关参数（参考 CurseforgeModpackDownloadTask）
    private readonly string? _gameBasePath;
    private readonly string? _instanceName;
    private readonly string? _targetModsPath;

    /// <summary>
    /// 下载的 Collection 文件路径（供上层代码使用）
    /// </summary>
    public string? DownloadedArchivePath { get; private set; }

    /// <summary>
    /// 是否可以使用 Premium 安装（已提供安装参数）
    /// </summary>
    public bool CanUsePremiumInstall { get; private set; }

    /// <summary>
    /// 创建 Collection 下载任务
    /// </summary>
    public NexusCollectionDownloadTask(
        string gameId,
        string collectionSlug,
        int revisionNumber = -1,
        string downloadDirectory = null,
        string? oauthToken = null,
        string? gameBasePath = null,
        string? instanceName = null,
        string? targetModsPath = null)
    {
        _gameId = gameId;
        _collectionSlug = collectionSlug;
        _revisionNumber = revisionNumber;
        _downloadDirectory = downloadDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SVL",
            "downloads",
            "collections",
            collectionSlug
        );
        _oauthToken = oauthToken;
        _gameBasePath = gameBasePath;
        _instanceName = instanceName;
        _targetModsPath = targetModsPath;

        Type = DownloadTaskType.Modpack;
        Name = $"Collection: {_collectionSlug}";
        StatusMessage = "准备下载集合...";
    }

    public override async Task ExecuteAsync()
    {
        string? accessToken = _oauthToken;

        // 如果没有提供 OAuth Token，从配置加载
        if (string.IsNullOrEmpty(accessToken))
        {
            var settings = AppConfig.GetSettings();
            accessToken = settings.NexusModsOAuthToken;

            if (string.IsNullOrEmpty(accessToken))
            {
                throw new Exception("未找到 NexusMods OAuth Token，请先登录");
            }
        }

        Log.Info($"[CollectionDownload] 开始下载集合: {_collectionSlug} (Revision {_revisionNumber})");

        try
        {
            Status = DownloadTaskStatus.Downloading;
            StatusMessage = "正在获取集合信息...";
            Progress = 0;

            // 使用 GraphQL 获取 Collection Revision 详情
            var revisionDetail = await NexusModsClient.GetCollectionRevisionDetailAsync(
                _collectionSlug, _revisionNumber, _gameId);

            if (revisionDetail == null)
            {
                throw new Exception($"获取 Collection Revision 详情失败: {_collectionSlug} r{_revisionNumber}");
            }

            Name = $"Collection: {revisionDetail.CollectionName}";
            _totalMods = revisionDetail.ModCount;
            StatusMessage = $"集合包含 {_totalMods} 个 Mod ({revisionDetail.FileSizeFormatted})";

            Log.Info($"[CollectionDownload] 集合: {revisionDetail.CollectionName}, 作者: {revisionDetail.Author}, Mod 数量: {_totalMods}");

            // 检查是否有下载链接（Premium 用户）
            if (string.IsNullOrEmpty(revisionDetail.DownloadLink))
            {
                throw new NexusPremiumRequiredException(_gameId, 0, 0, "Collection 下载需要 Premium 权限");
            }

            Log.Info($"[CollectionDownload] 下载链接: {revisionDetail.DownloadLink}");

            // 根据 Vortex 文档，使用 download_link 下载 Collection 信息
            StatusMessage = "正在获取 Collection 信息...";
            Progress = 5;

            var collectionJson = await DownloadCollectionJsonAsync(revisionDetail.DownloadLink, accessToken);
            if (string.IsNullOrEmpty(collectionJson))
            {
                throw new Exception("获取 Collection 信息失败");
            }

            // 检查是否为 download_links 格式（需要下载 7z 文件）
            if (IsDownloadLinksFormat(collectionJson))
            {
                Log.Info("[CollectionDownload] 检测到 download_links 格式，下载 Collection 压缩包");

                StatusMessage = "正在下载 Collection 压缩包...";
                Progress = 10;

                // 下载 7z 文件并保存
                var archivePath = await DownloadCollectionArchiveAsync(collectionJson, accessToken);
                if (string.IsNullOrEmpty(archivePath))
                {
                    throw new Exception("下载 Collection 压缩包失败");
                }

                Log.Info($"[CollectionDownload] Collection 压缩包下载成功: {archivePath}");

                // 保存下载的文件路径，供上层代码使用
                DownloadedArchivePath = archivePath;

                // 不再自动启动安装任务，由上层代码决定使用何种安装方式
                // 如果提供了安装参数，设置标志表示可以使用 Premium 安装
                if (!string.IsNullOrEmpty(_gameBasePath) && !string.IsNullOrEmpty(_instanceName) && !string.IsNullOrEmpty(_targetModsPath))
                {
                    CanUsePremiumInstall = true;
                    StatusMessage = "下载完成，准备安装...";
                    Log.Info($"[CollectionDownload] Collection 下载完成，可以使用 Premium 安装");
                }
                else
                {
                    StatusMessage = "下载完成";
                }

                // 完成
                Progress = 100;
                Status = DownloadTaskStatus.Completed;
                CompletedTime = DateTime.Now;
                StatusMessage = $"✓ Collection 下载完成: {Path.GetFileName(archivePath)}";
                Log.Info($"[CollectionDownload] Collection 下载完成: {archivePath}");
            }
            else
            {
                // 如果不是 download_links 格式，说明是直接下载的 Mod 列表（暂不支持）
                Log.Warn("[CollectionDownload] Collection 格式不支持，请使用 Premium 账号");
                throw new Exception("Collection 格式不支持，请使用 Premium 账号");
            }
        }
        catch (OperationCanceledException)
        {
            Status = DownloadTaskStatus.Cancelled;
            StatusMessage = "已取消";
            Log.Info("[CollectionDownload] 下载已取消");
        }
        catch (Exception ex)
        {
            Status = DownloadTaskStatus.Failed;
            StatusMessage = $"错误: {ex.Message}";
            CompletedTime = DateTime.Now;
            Log.Error(ex, "[CollectionDownload] 下载失败");
            throw;
        }
    }

    /// <summary>
    /// 获取集合信息
    /// </summary>
    private async Task<NexusCollectionInfo?> GetCollectionInfoAsync(string accessToken)
    {
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");
            client.DefaultRequestHeaders.Add("User-Agent", "SVL-StardewLauncher/1.0");
            client.Timeout = TimeSpan.FromSeconds(30);

            // 使用 API 获取集合信息
            // 注意：这里使用的是实际 API 端点，可能需要根据实际情况调整
            var url = $"https://api.nexusmods.com/v1/games/{_gameId}/collections/{_collectionSlug}";
            var response = await client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                Log.Warn($"[CollectionDownload] 获取集合信息失败: {response.StatusCode}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<NexusCollectionInfo>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[CollectionDownload] 获取集合信息异常");
            return null;
        }
    }

    /// <summary>
    /// 获取集合中的 Mod 列表
    /// </summary>
    private async Task<List<NexusCollectionMod>> GetCollectionModsAsync(string accessToken)
    {
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");
            client.DefaultRequestHeaders.Add("User-Agent", "SVL-StardewLauncher/1.0");
            client.Timeout = TimeSpan.FromSeconds(30);

            // 获取集合的链接
            var revision = _revisionNumber > 0 ? _revisionNumber.ToString() : "latest";
            var url = $"https://api.nexusmods.com/v1/games/{_gameId}/collections/{_collectionSlug}/revisions/{revision}";

            var response = await client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                Log.Warn($"[CollectionDownload] 获取集合 Mod 列表失败: {response.StatusCode}");
                return new List<NexusCollectionMod>();
            }

            var json = await response.Content.ReadAsStringAsync();
            var collectionData = JsonSerializer.Deserialize<NexusCollectionDownloadResponse>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            return collectionData?.Mods ?? new List<NexusCollectionMod>();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[CollectionDownload] 获取集合 Mod 列表异常");
            return new List<NexusCollectionMod>();
        }
    }

    /// <summary>
    /// 下载 Collection JSON 文件
    /// </summary>
    private async Task<string?> DownloadCollectionJsonAsync(string downloadLink, string accessToken)
    {
        try
        {
            // 构造完整的下载 URL
            var fullUrl = downloadLink.StartsWith("http")
                ? downloadLink
                : $"https://api.nexusmods.com{downloadLink}";

            Log.Info($"[CollectionDownload] 下载 Collection JSON: {fullUrl}");

            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");
            client.DefaultRequestHeaders.Add("User-Agent", "SVL-StardewLauncher/1.0");
            client.Timeout = TimeSpan.FromSeconds(30);

            var response = await client.GetAsync(fullUrl);

            if (!response.IsSuccessStatusCode)
            {
                Log.Warn($"[CollectionDownload] 下载 Collection JSON 失败: {response.StatusCode}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            Log.Info($"[CollectionDownload] Collection JSON 下载成功，大小: {json.Length} 字节");
            Log.Debug($"[CollectionDownload] Collection JSON 内容: {json}");

            return json;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[CollectionDownload] 下载 Collection JSON 异常");
            return null;
        }
    }

    /// <summary>
    /// 解析 Collection JSON 文件
    /// </summary>
    private List<NexusCollectionModFile> ParseCollectionJson(string json)
    {
        try
        {
            Log.Info($"[CollectionDownload] 解析 Collection JSON...");
            Log.Debug($"[CollectionDownload] JSON 内容（前500字符）: {(json.Length > 500 ? json.Substring(0, 500) + "..." : json)}");

            using var doc = JsonDocument.Parse(json);

            // 检查是否有错误
            if (doc.RootElement.TryGetProperty("errors", out var errorsElement) && errorsElement.ValueKind == JsonValueKind.Array && errorsElement.GetArrayLength() > 0)
            {
                Log.Warn($"[CollectionDownload] Collection JSON 包含错误: {errorsElement.ToString()}");
                return new List<NexusCollectionModFile>();
            }

            // 尝试多种可能的路径
            JsonElement modsElement = default;
            bool found = false;

            // 路径 1: collection.mods（标准 Collection JSON 格式）
            if (!found && doc.RootElement.TryGetProperty("collection", out var collectionElement))
            {
                if (collectionElement.TryGetProperty("mods", out modsElement))
                {
                    found = true;
                    Log.Info("[CollectionDownload] 找到 mods 路径: collection.mods");
                }
            }

            // 路径 2: data.collectionRevision.mods（GraphQL 格式）
            if (!found && doc.RootElement.TryGetProperty("data", out var dataElement))
            {
                if (dataElement.TryGetProperty("collectionRevision", out var collectionRevisionElement))
                {
                    if (collectionRevisionElement.TryGetProperty("mods", out modsElement))
                    {
                        found = true;
                        Log.Info("[CollectionDownload] 找到 mods 路径: data.collectionRevision.mods");
                    }
                }
            }

            // 路径 3: data.collection.mods
            if (!found && doc.RootElement.TryGetProperty("data", out dataElement))
            {
                if (dataElement.TryGetProperty("collection", out collectionElement))
                {
                    if (collectionElement.TryGetProperty("mods", out modsElement))
                    {
                        found = true;
                        Log.Info("[CollectionDownload] 找到 mods 路径: data.collection.mods");
                    }
                }
            }

            // 路径 4: data.mods
            if (!found && doc.RootElement.TryGetProperty("data", out dataElement))
            {
                if (dataElement.TryGetProperty("mods", out modsElement))
                {
                    found = true;
                    Log.Info("[CollectionDownload] 找到 mods 路径: data.mods");
                }
            }

            // 路径 5: 直接的 mods 数组（根级别）
            if (!found && doc.RootElement.TryGetProperty("mods", out modsElement))
            {
                found = true;
                Log.Info("[CollectionDownload] 找到 mods 路径: mods（根级别）");
            }

            if (!found || modsElement.ValueKind != JsonValueKind.Array)
            {
                Log.Warn("[CollectionDownload] Collection JSON 缺少有效的 mods 数组");
                return new List<NexusCollectionModFile>();
            }

            var modFiles = new List<NexusCollectionModFile>();
            foreach (var modElement in modsElement.EnumerateArray())
            {
                // 解析每个 Mod
                long modId = 0;
                long fileId = 0;
                string? name = null;
                string? version = null;
                bool optional = false;

                // 尝试从不同路径获取 Mod 信息
                if (modElement.TryGetProperty("mod", out var modObj))
                {
                    if (modObj.TryGetProperty("id", out var idElement)) modId = idElement.GetInt64();
                    if (modObj.TryGetProperty("name", out var nameElement)) name = nameElement.GetString();
                }

                if (modElement.TryGetProperty("file", out var fileObj))
                {
                    if (fileObj.TryGetProperty("id", out var fidElement)) fileId = fidElement.GetInt64();
                    if (fileObj.TryGetProperty("fileId", out var fileIdElement)) fileId = fileIdElement.GetInt64();
                    if (fileObj.TryGetProperty("name", out var fNameElement)) name = fNameElement.GetString();
                    if (fileObj.TryGetProperty("version", out var versionElement)) version = versionElement.GetString();
                }

                // 如果还没有获取到 modId 和 fileId，尝试直接从根获取
                if (modId == 0 && modElement.TryGetProperty("modId", out var midElement)) modId = midElement.GetInt64();
                if (fileId == 0 && modElement.TryGetProperty("fileId", out var fElement)) fileId = fElement.GetInt64();
                if (string.IsNullOrEmpty(name) && modElement.TryGetProperty("name", out var nElement)) name = nElement.GetString();
                if (string.IsNullOrEmpty(version) && modElement.TryGetProperty("version", out var vElement)) version = vElement.GetString();

                // 检查 optional 标志
                if (modElement.TryGetProperty("optional", out var optElement) && optElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    optional = optElement.GetBoolean();
                }

                if (modId > 0 && fileId > 0)
                {
                    modFiles.Add(new NexusCollectionModFile
                    {
                        ModId = modId,
                        FileId = fileId,
                        Name = name ?? $"Mod_{modId}",
                        Version = version ?? string.Empty,
                        Optional = optional
                    });
                }
                else
                {
                    Log.Warn($"[CollectionDownload] 跳过无效的 Mod: modId={modId}, fileId={fileId}");
                }
            }

            Log.Info($"[CollectionDownload] 解析到 {modFiles.Count} 个 Mod");
            return modFiles;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[CollectionDownload] 解析 Collection JSON 异常");
            return new List<NexusCollectionModFile>();
        }
    }

    /// <summary>
    /// 检查 JSON 是否为 download_links 格式
    /// </summary>
    private bool IsDownloadLinksFormat(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("download_links", out var linksElement)
                && linksElement.ValueKind == JsonValueKind.Array
                && linksElement.GetArrayLength() > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 下载 Collection 压缩包（7z 格）并保存
    /// </summary>
    private async Task<string?> DownloadCollectionArchiveAsync(string downloadLinksJson, string accessToken)
    {
        try
        {
            using var doc = JsonDocument.Parse(downloadLinksJson);
            var downloadLinksElement = doc.RootElement.GetProperty("download_links");

            // 获取第一个下载链接（通常是 Nexus CDN）
            if (downloadLinksElement.GetArrayLength() == 0)
            {
                Log.Warn("[CollectionDownload] download_links 数组为空");
                return null;
            }

            var firstLink = downloadLinksElement[0];
            var downloadUrl = firstLink.GetProperty("URI").GetString();
            var linkName = firstLink.GetProperty("short_name").GetString() ?? "Nexus CDN";

            if (string.IsNullOrEmpty(downloadUrl))
            {
                Log.Warn("[CollectionDownload] download_links 中没有有效的 URI");
                return null;
            }

            Log.Info($"[CollectionDownload] 使用 {linkName} 下载 Collection 压缩包: {downloadUrl}");

            // 从 URL 中提取文件名
            var uri = new Uri(downloadUrl);
            var fileName = Path.GetFileName(uri.LocalPath);
            if (string.IsNullOrEmpty(fileName) || !fileName.EndsWith(".7z"))
            {
                fileName = $"collection_{_collectionSlug}_r{_revisionNumber}.7z";
            }

            var savePath = Path.Combine(_downloadDirectory, fileName);

            // 确保目录存在
            if (!Directory.Exists(_downloadDirectory))
            {
                Directory.CreateDirectory(_downloadDirectory);
            }

            // 下载 7z 文件
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");
                client.DefaultRequestHeaders.Add("User-Agent", "SVL-StardewLauncher/1.0");
                client.Timeout = TimeSpan.FromMinutes(30);

                // 创建进度报告
                var progress = new Progress<long>(bytesReceived =>
                {
                    // 更新进度（10% 到 90%）
                    Progress = 10 + (int)(Math.Min(100, bytesReceived / (1024.0 * 1024.0)) * 80);
                });

                var response = await client.GetAsync(downloadUrl);
                if (!response.IsSuccessStatusCode)
                {
                    Log.Warn($"[CollectionDownload] 下载 Collection 压缩包失败: {response.StatusCode}");
                    return null;
                }

                var totalBytes = response.Content.Headers.ContentLength ?? 0;
                Log.Info($"[CollectionDownload] 开始下载 Collection 压缩包，大小: {totalBytes} 字节");

                var zipData = await response.Content.ReadAsByteArrayAsync();
                File.WriteAllBytes(savePath, zipData);

                Log.Info($"[CollectionDownload] Collection 压缩包下载成功: {savePath}, 大小: {zipData.Length} 字节");
            }

            return savePath;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[CollectionDownload] 下载 Collection 压缩包异常");
            return null;
        }
    }

    public override void Cancel()
    {
        try
        {
            _cts.Cancel();
            Status = DownloadTaskStatus.Cancelled;
            StatusMessage = "正在取消...";
            Log.Info($"[CollectionDownload] 取消任务: {_collectionSlug}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[CollectionDownload] 取消任务失败");
        }
    }
}
