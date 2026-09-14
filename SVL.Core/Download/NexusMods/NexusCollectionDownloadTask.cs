using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
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
                // 新版 GraphQL 在非 Premium 情况下通常不返回 downloadLink，
                // 但旧 REST Revision 接口仍可能返回可安装的 Mod 列表。先尝试
                // 将该列表转换为标准清单；确实拿不到列表时才进入浏览器/Premium 回退。
                var directMods = await GetCollectionModsAsync(accessToken, revisionDetail.RevisionNumber);
                var directModFiles = directMods
                    .Where(mod => mod.Mod?.Id > 0 &&
                                  (mod.File?.FileId > 0 || mod.File?.Id > 0))
                    .Select(mod => new NexusCollectionModFile
                    {
                        ModId = mod.Mod.Id,
                        FileId = mod.File.FileId > 0 ? mod.File.FileId : mod.File.Id,
                        Name = string.IsNullOrWhiteSpace(mod.File.Name)
                            ? mod.Mod.Name ?? $"Mod_{mod.Mod.Id}"
                            : mod.File.Name,
                        Version = mod.File.Version ?? string.Empty,
                        Optional = mod.Optional
                    })
                    .ToList();

                if (!await TryCompleteFromModFilesAsync(revisionDetail, directModFiles))
                {
                    throw new NexusPremiumRequiredException(_gameId, 0, 0, "Collection 下载需要 Premium 权限或有效的 Mod 清单");
                }

                return;
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
                // 非 Premium/旧版接口可能返回可解析的 Mod 列表，而不是
                // download_links 压缩包。把它转换为标准 collection.json ZIP，
                // 这样旧 WPF 下载任务和后续安装任务仍能使用同一条安装链路。
                var modFiles = ParseCollectionJson(collectionJson);
                if (modFiles.Count == 0)
                {
                    Log.Warn("[CollectionDownload] Collection 响应既没有可用下载链接，也没有有效 Mod 列表");
                    throw new Exception("Collection 响应不包含可安装的 Mod 清单，请确认 Nexus 账号权限或重新打开下载页面");
                }

                StatusMessage = $"正在整理 Collection 清单（{modFiles.Count} 个 Mod）...";
                Progress = 20;
                if (!await TryCompleteFromModFilesAsync(revisionDetail, modFiles))
                {
                    throw new Exception("Collection 响应不包含可安装的 Mod 清单，请确认 Nexus 账号权限或重新打开下载页面");
                }
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
    private async Task<List<NexusCollectionMod>> GetCollectionModsAsync(
        string accessToken,
        int? revisionOverride = null)
    {
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");
            client.DefaultRequestHeaders.Add("User-Agent", "SVL-StardewLauncher/1.0");
            client.Timeout = TimeSpan.FromSeconds(30);

            // 获取集合的链接
            var revisionNumber = revisionOverride.GetValueOrDefault();
            if (revisionNumber <= 0)
            {
                revisionNumber = _revisionNumber;
            }
            var revision = revisionNumber > 0 ? revisionNumber.ToString() : "latest";
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
            if (TryGetPropertyIgnoreCase(doc.RootElement, "errors", out var errorsElement) &&
                errorsElement.ValueKind == JsonValueKind.Array &&
                errorsElement.GetArrayLength() > 0)
            {
                Log.Warn($"[CollectionDownload] Collection JSON 包含错误: {errorsElement.ToString()}");
                return new List<NexusCollectionModFile>();
            }

            // 尝试多种可能的路径
            JsonElement modsElement = default;
            bool found = false;

            // 路径 1: collection.mods（标准 Collection JSON 格式）
            if (!found && TryGetPropertyIgnoreCase(doc.RootElement, "collection", out var collectionElement))
            {
                if (TryGetPropertyIgnoreCase(collectionElement, "mods", out modsElement))
                {
                    found = true;
                    Log.Info("[CollectionDownload] 找到 mods 路径: collection.mods");
                }
            }

            // 路径 2: data.collectionRevision.mods（GraphQL 格式）
            if (!found && TryGetPropertyIgnoreCase(doc.RootElement, "data", out var dataElement))
            {
                if (TryGetPropertyIgnoreCase(dataElement, "collectionRevision", out var collectionRevisionElement))
                {
                    if (TryGetPropertyIgnoreCase(collectionRevisionElement, "mods", out modsElement))
                    {
                        found = true;
                        Log.Info("[CollectionDownload] 找到 mods 路径: data.collectionRevision.mods");
                    }
                }
            }

            // 路径 3: data.collection.mods
            if (!found && TryGetPropertyIgnoreCase(doc.RootElement, "data", out dataElement))
            {
                if (TryGetPropertyIgnoreCase(dataElement, "collection", out collectionElement))
                {
                    if (TryGetPropertyIgnoreCase(collectionElement, "mods", out modsElement))
                    {
                        found = true;
                        Log.Info("[CollectionDownload] 找到 mods 路径: data.collection.mods");
                    }
                }
            }

            // 路径 4: data.mods
            if (!found && TryGetPropertyIgnoreCase(doc.RootElement, "data", out dataElement))
            {
                if (TryGetPropertyIgnoreCase(dataElement, "mods", out modsElement))
                {
                    found = true;
                    Log.Info("[CollectionDownload] 找到 mods 路径: data.mods");
                }
            }

            // 路径 5: 直接的 mods 数组（根级别）
            if (!found && TryGetPropertyIgnoreCase(doc.RootElement, "mods", out modsElement))
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
                if (TryGetPropertyIgnoreCase(modElement, "mod", out var modObj))
                {
                    modId = ReadLongProperty(modObj, "id", "modId");
                    name = ReadStringProperty(modObj, "name", "modName");
                }

                if (TryGetPropertyIgnoreCase(modElement, "file", out var fileObj))
                {
                    fileId = ReadLongProperty(fileObj, "id", "fileId");
                    name = ReadStringProperty(fileObj, "name", "fileName") ?? name;
                    version = ReadStringProperty(fileObj, "version", "modVersion") ?? version;
                }

                // 如果还没有获取到 modId 和 fileId，尝试直接从根获取
                if (modId == 0) modId = ReadLongProperty(modElement, "modId", "projectId");
                if (fileId == 0) fileId = ReadLongProperty(modElement, "fileId", "fileID");
                name ??= ReadStringProperty(modElement, "name", "modName", "fileName");
                version ??= ReadStringProperty(modElement, "version", "modVersion");

                // 检查 optional 标志
                if (TryGetPropertyIgnoreCase(modElement, "optional", out var optElement) &&
                    optElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
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

    private static long ReadLongProperty(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!TryGetPropertyIgnoreCase(element, propertyName, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String &&
                long.TryParse(value.GetString(), out var textNumber))
            {
                return textNumber;
            }
        }

        return 0;
    }

    private static string? ReadStringProperty(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!TryGetPropertyIgnoreCase(element, propertyName, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }

            if (value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            {
                return value.ToString();
            }
        }

        return null;
    }

    /// <summary>
    /// 检查 JSON 是否为 download_links 格式
    /// </summary>
    private bool IsDownloadLinksFormat(string json)
    {
        return TryGetFirstDownloadLink(json, out _, out _);
    }

    private static bool TryGetFirstDownloadLink(
        string json,
        out string downloadUrl,
        out string linkName)
    {
        downloadUrl = string.Empty;
        linkName = "Nexus CDN";

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!TryGetPropertyIgnoreCase(doc.RootElement, "download_links", out var links))
            {
                return false;
            }

            IEnumerable<JsonElement> candidates = links.ValueKind switch
            {
                JsonValueKind.Array => links.EnumerateArray(),
                JsonValueKind.Object => links.EnumerateObject().Select(property => property.Value),
                _ => Array.Empty<JsonElement>()
            };

            foreach (var candidate in candidates)
            {
                var url = candidate.ValueKind == JsonValueKind.String
                    ? candidate.GetString()
                    : GetStringPropertyIgnoreCase(candidate, "URI", "url", "downloadUrl", "download_url");
                if (string.IsNullOrWhiteSpace(url) ||
                    !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    continue;
                }

                downloadUrl = url.Trim();
                if (candidate.ValueKind == JsonValueKind.Object)
                {
                    linkName = GetStringPropertyIgnoreCase(
                                   candidate,
                                   "short_name",
                                   "shortName",
                                   "name")
                               ?? linkName;
                }

                return true;
            }
        }
        catch
        {
            // 交给上层按“没有有效下载链接/清单”处理，避免错误响应中断任务泵。
        }

        return false;
    }

    private static bool TryGetPropertyIgnoreCase(
        JsonElement element,
        string propertyName,
        out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string? GetStringPropertyIgnoreCase(
        JsonElement element,
        params string[] propertyNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (!propertyNames.Any(name => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            return property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString()
                : property.Value.ToString();
        }

        return null;
    }

    private async Task<string> CreateManifestCollectionArchiveAsync(
        NexusCollectionRevisionDetail revisionDetail,
        IReadOnlyCollection<NexusCollectionModFile> modFiles)
    {
        Directory.CreateDirectory(_downloadDirectory);

        var revision = revisionDetail.RevisionNumber > 0
            ? revisionDetail.RevisionNumber
            : _revisionNumber;
        var safeSlug = new string(_collectionSlug
            .Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)
            .ToArray());
        if (string.IsNullOrWhiteSpace(safeSlug))
        {
            safeSlug = "collection";
        }

        var archivePath = Path.Combine(_downloadDirectory, $"collection_{safeSlug}_r{revision}.zip");
        var tempPath = archivePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var manifest = new NexusCollectionJson
        {
            Info = new NexusCollectionJsonInfo
            {
                Name = string.IsNullOrWhiteSpace(revisionDetail.CollectionName)
                    ? _collectionSlug
                    : revisionDetail.CollectionName,
                Author = revisionDetail.Author,
                DomainName = _gameId,
                GameVersions = Array.Empty<string>()
            },
            Mods = modFiles
                .Where(mod => mod.ModId > 0 && mod.FileId > 0)
                .Select(mod => new NexusCollectionJsonMod
                {
                    Name = mod.Name,
                    Version = mod.Version,
                    Optional = mod.Optional,
                    DomainName = _gameId,
                    Source = new NexusCollectionJsonModSource
                    {
                        Type = "nexus",
                        ModId = mod.ModId,
                        FileId = mod.FileId
                    }
                })
                .ToArray()
        };

        try
        {
            using (var fileStream = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.ReadWrite,
                       FileShare.None))
            using (var archive = new ZipArchive(fileStream, ZipArchiveMode.Create, leaveOpen: false))
            using (var writer = new StreamWriter(
                       archive.CreateEntry("collection.json", CompressionLevel.Fastest).Open(),
                       new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });
                await writer.WriteAsync(json);
            }

            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }

            File.Move(tempPath, archivePath);
            return archivePath;
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }

    private async Task<bool> TryCompleteFromModFilesAsync(
        NexusCollectionRevisionDetail revisionDetail,
        IReadOnlyCollection<NexusCollectionModFile> modFiles)
    {
        var validModFiles = modFiles
            .Where(mod => mod.ModId > 0 && mod.FileId > 0)
            .ToList();
        if (validModFiles.Count == 0)
        {
            return false;
        }

        var archivePath = await CreateManifestCollectionArchiveAsync(revisionDetail, validModFiles);
        DownloadedArchivePath = archivePath;

        if (!string.IsNullOrEmpty(_gameBasePath) &&
            !string.IsNullOrEmpty(_instanceName) &&
            !string.IsNullOrEmpty(_targetModsPath))
        {
            CanUsePremiumInstall = true;
        }

        Progress = 100;
        Status = DownloadTaskStatus.Completed;
        CompletedTime = DateTime.Now;
        StatusMessage = $"✓ Collection 清单已整理: {Path.GetFileName(archivePath)}";
        Log.Info($"[CollectionDownload] 已将 Mod 列表转换为兼容 Collection: {archivePath}");
        return true;
    }

    /// <summary>
    /// 下载 Collection 压缩包（7z 格）并保存
    /// </summary>
    private async Task<string?> DownloadCollectionArchiveAsync(string downloadLinksJson, string accessToken)
    {
        string? tempPath = null;
        try
        {
            if (!TryGetFirstDownloadLink(downloadLinksJson, out var downloadUrl, out var linkName))
            {
                Log.Warn("[CollectionDownload] download_links 中没有有效的 URI");
                return null;
            }

            Log.Info($"[CollectionDownload] 使用 {linkName} 下载 Collection 压缩包: {downloadUrl}");

            // 从 URL 中提取文件名
            var uri = new Uri(downloadUrl);
            var fileName = Path.GetFileName(uri.LocalPath);
            if (string.IsNullOrEmpty(fileName) ||
                (!fileName.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) &&
                 !fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
            {
                fileName = $"collection_{_collectionSlug}_r{_revisionNumber}.7z";
            }

            var savePath = Path.Combine(_downloadDirectory, fileName);
            tempPath = savePath + "." + Guid.NewGuid().ToString("N") + ".part";

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
                using var response = await client.GetAsync(
                    downloadUrl,
                    HttpCompletionOption.ResponseHeadersRead,
                    _cts.Token);
                if (!response.IsSuccessStatusCode)
                {
                    Log.Warn($"[CollectionDownload] 下载 Collection 压缩包失败: {response.StatusCode}");
                    return null;
                }

                var totalBytes = response.Content.Headers.ContentLength ?? 0;
                Log.Info($"[CollectionDownload] 开始下载 Collection 压缩包，大小: {totalBytes} 字节");

                using var contentStream = await response.Content.ReadAsStreamAsync();
                using var outputStream = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 128 * 1024,
                    useAsync: true);
                var buffer = new byte[128 * 1024];
                long bytesReceived = 0;
                int read;
                while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length, _cts.Token)) > 0)
                {
                    await outputStream.WriteAsync(buffer, 0, read, _cts.Token);
                    bytesReceived += read;
                    Progress = totalBytes > 0
                        ? 10 + Math.Min(80, Math.Max(0,
                            (int)Math.Floor(bytesReceived * 80.0 / totalBytes)))
                        : 10;
                }

                await outputStream.FlushAsync(_cts.Token);

                if (File.Exists(savePath))
                {
                    File.Delete(savePath);
                }

                File.Move(tempPath, savePath);

                Log.Info($"[CollectionDownload] Collection 压缩包下载成功: {savePath}, 大小: {bytesReceived} 字节");
            }

            return savePath;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[CollectionDownload] 下载 Collection 压缩包异常");
            return null;
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
                // best-effort cleanup
            }
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
