using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace SVL.Avalonia.Services;

/// <summary>SteamCMD 登录结果。</summary>
public enum SteamCmdLoginStatus
{
    /// <summary>登录成功（凭据已缓存，后续下载可复用）。</summary>
    Success,
    /// <summary>需要 Steam Guard / 两步验证码。</summary>
    NeedsGuardCode,
    /// <summary>账号或密码错误。</summary>
    InvalidCredentials,
    /// <summary>其他错误。</summary>
    Error
}

/// <summary>SteamCMD 登录结果详情。</summary>
public sealed record SteamCmdLoginResult(SteamCmdLoginStatus Status, string Message)
{
    public static SteamCmdLoginResult Success() => new(SteamCmdLoginStatus.Success, "登录成功");
}

/// <summary>Depot 下载结果详情。</summary>
public sealed record SteamCmdDepotResult(bool Success, string Message, string? ContentPath);

/// <summary>游戏版本选项（内置 Manifest ID 或"最新版"）。</summary>
public sealed record SteamGameVersionOption(string DisplayName, string? ManifestId)
{
    /// <summary>版本描述（来自 SteamCMD app_info 的 branch description），选中后在下方展示。</summary>
    public string Description { get; init; } = string.Empty;

    public override string ToString() => DisplayName;
}

/// <summary>
/// SteamCMD 集成服务：下载/安装 SteamCMD，登录 Steam 账号，通过 download_depot 下载
/// Stardew Valley 游戏文件（AppID 413150 / Windows DepotID 413151），支持按 Manifest ID
/// 选择特定历史版本。下载走 HttpDownloadService（享受多线程与缓存）。
/// 参考：https://developer.valvesoftware.com/wiki/SteamCMD
/// </summary>
public sealed partial class SteamCmdService
{
    public const int StardewAppId = 413150;
    public const int StardewWindowsDepotId = 413151;

    private const string SteamCmdZipUrl = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip";

    private readonly HttpDownloadService _httpDownloadService;

    public SteamCmdService(HttpDownloadService httpDownloadService)
    {
        _httpDownloadService = httpDownloadService;
    }

    public string SteamCmdDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SVL", "Avalonia", "steamcmd");

    public string SteamCmdExecutablePath => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? Path.Combine(SteamCmdDirectory, "steamcmd.exe")
        : Path.Combine(SteamCmdDirectory, "steamcmd.sh");

    public bool IsSteamCmdInstalled => File.Exists(SteamCmdExecutablePath);

    /// <summary>download_depot 的内容输出目录（steamcmd 目录下 steamapps/content）。</summary>
    public string GetDepotContentPath(int depotId = StardewWindowsDepotId)
    {
        return Path.Combine(SteamCmdDirectory, "steamapps", "content", $"app_{StardewAppId}", $"depot_{depotId}");
    }

    /// <summary>内置已知版本（Windows Depot 413151 的 Manifest ID，来源 SteamDB，作为兜底）。</summary>
    public static IReadOnlyList<SteamGameVersionOption> GetKnownVersions()
    {
        return
        [
            new SteamGameVersionOption("最新版（当前分支）", null),
            new SteamGameVersionOption("1.5.6 (64-bit)", "5609262347030774375"),
            new SteamGameVersionOption("1.5.6 (32-bit XNA)", "618057478175226131"),
            new SteamGameVersionOption("1.5.4", "7802000804251603756"),
            new SteamGameVersionOption("1.4.5", "6307986820908740561"),
            new SteamGameVersionOption("1.4.0", "2373680906867811602"),
            new SteamGameVersionOption("1.3.36", "3080804457574934302"),
            new SteamGameVersionOption("1.2.33", "5793210319202900873"),
            new SteamGameVersionOption("1.1", "7487215307508292747"),
            new SteamGameVersionOption("1.0", "3352391531516945586")
        ];
    }

    /// <summary>
    /// 通过 SteamCMD 自动获取 Windows Depot 的所有可用版本分支（运行 app_info_print 并解析）。
    /// 不用硬编码：分支与 Manifest 都取自 SteamCMD 实际输出。
    /// </summary>
    public async Task<IReadOnlyList<SteamGameVersionOption>> FetchAvailableVersionsAsync(
        string? username,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsSteamCmdInstalled)
        {
            return [];
        }

        log?.Invoke("正在通过 SteamCMD 自动获取可用版本...");
        // 复用已缓存的登录凭证（+login 用户名）而非匿名，与游戏下载一致。
        var loginArg = string.IsNullOrWhiteSpace(username)
            ? "+login anonymous"
            : $"+login {EscapeArgument(username.Trim())}";
        var args = $"{loginArg} +app_info_print {StardewAppId} +quit";
        await RunSteamCmdAsync(args, log, null, cancellationToken);

        var output = _lastOutput.ToString();
        var versions = ParseDepotVersions(output, StardewWindowsDepotId);
        if (versions.Count > 0)
        {
            log?.Invoke($"已自动获取 {versions.Count} 个可用版本");
        }
        else
        {
            log?.Invoke("未能从 SteamCMD 解析可用版本（可能需登录或网络波动）");
        }

        return versions;
    }

    /// <summary>从 SteamCMD app_info_print 输出中解析指定 Depot 各分支（public/legacy_* 等）的 Manifest ID。</summary>
    private static List<SteamGameVersionOption> ParseDepotVersions(string output, int depotId)
    {
        var result = new List<SteamGameVersionOption>();
        if (string.IsNullOrWhiteSpace(output))
        {
            return result;
        }

        var depotToken = $"\"{depotId}\"";
        var depotIdx = output.IndexOf(depotToken, StringComparison.OrdinalIgnoreCase);
        if (depotIdx < 0)
        {
            return result;
        }

        var afterDepot = output.Substring(depotIdx);

        // 只取当前 Depot 的块（到下一个 Depot 编号为止），避免混入其它平台的 depot
        var nextDepotIdx = afterDepot.IndexOf($"\"{depotId + 1}\"", StringComparison.OrdinalIgnoreCase);
        var depotBlock = nextDepotIdx > 0 ? afterDepot[..nextDepotIdx] : afterDepot;

        var manifestIdx = depotBlock.IndexOf("\"manifests\"", StringComparison.OrdinalIgnoreCase);
        if (manifestIdx < 0)
        {
            return result;
        }

        var block = depotBlock.Substring(manifestIdx);
        var matches = System.Text.RegularExpressions.Regex.Matches(
            block,
            @"""(\w+)""\s*\{\s*""gid""\s*""(\d+)""");

        // 从 "branches" 段解析各分支的官方描述（如 "The legacy 1.5.6 version of Stardew Valley."）
        var descriptions = ParseBranchDescriptions(output);

        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            var branch = match.Groups[1].Value;
            var gid = match.Groups[2].Value;
            if (string.IsNullOrWhiteSpace(gid))
            {
                continue;
            }

            var friendly = FriendlyBranchName(branch);
            descriptions.TryGetValue(branch, out var desc);
            // DisplayName 只放简短名称，描述放到 Description，选中后在下方展示。
            result.Add(new SteamGameVersionOption(friendly, gid)
            {
                Description = string.IsNullOrWhiteSpace(desc) ? string.Empty : Truncate(desc, 80)
            });
        }

        return result;
    }

    /// <summary>解析 app_info_print 输出中 "branches" 段的 branch -> description 映射。</summary>
    private static Dictionary<string, string> ParseBranchDescriptions(string output)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(output))
        {
            return result;
        }

        var branchIdx = output.IndexOf("\"branches\"", StringComparison.OrdinalIgnoreCase);
        if (branchIdx < 0)
        {
            return result;
        }

        var branchesBlock = output.Substring(branchIdx);
        var matches = System.Text.RegularExpressions.Regex.Matches(
            branchesBlock,
            @"""(\w+)""\s*\{[^}]*?""description""\s*""([^""]*)""");

        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            result[match.Groups[1].Value] = match.Groups[2].Value;
        }

        return result;
    }

    private static string FriendlyBranchName(string branch)
    {
        return branch.ToLowerInvariant() switch
        {
            "public" => "最新版（当前分支）",
            "compatibility" => "兼容版（32 位 XNA）",
            "legacy_1.5.6" => "旧版 1.5.6",
            "legacy_1.6.8" => "旧版 1.6.8",
            "previous_version" => "上一版本",
            _ => branch
        };
    }

    private static string Truncate(string text, int max)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length <= max)
        {
            return text;
        }

        return text[..max] + "…";
    }

    /// <summary>确保 SteamCMD 已下载并解压（首次调用时下载官方 zip）。</summary>
    public async Task EnsureSteamCmdAsync(Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        if (IsSteamCmdInstalled)
        {
            log?.Invoke($"SteamCMD 已就绪: {SteamCmdExecutablePath}");
            return;
        }

        Directory.CreateDirectory(SteamCmdDirectory);
        var zipPath = Path.Combine(SteamCmdDirectory, "steamcmd.zip");
        log?.Invoke("正在下载 SteamCMD 安装包...");
        await _httpDownloadService.DownloadAsync(SteamCmdZipUrl, zipPath, null, cancellationToken);

        log?.Invoke("正在解压 SteamCMD...");
        ZipFile.ExtractToDirectory(zipPath, SteamCmdDirectory, overwriteFiles: true);
        TryDeleteFile(zipPath);

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Linux/macOS 需要可执行权限
            try
            {
                var chmod = Process.Start(new ProcessStartInfo("chmod", $"+x \"{SteamCmdExecutablePath}\""));
                chmod?.WaitForExit(5000);
            }
            catch
            {
                // ignore
            }
        }

        if (!IsSteamCmdInstalled)
        {
            throw new InvalidOperationException("SteamCMD 安装失败：未找到可执行文件");
        }

        log?.Invoke("SteamCMD 安装完成");
    }

    /// <summary>登录 Steam 账号（验证凭据并缓存 sentry，供后续 depot 下载复用）。</summary>
    public Task<SteamCmdLoginResult> LoginAsync(
        string username,
        string password,
        string? guardCode,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        var args = BuildLoginArgs(username, password, guardCode);
        return RunLoginAsync(args, log, cancellationToken);
    }

    /// <summary>下载游戏 Depot 到目标目录（targetPath 须不存在或为空目录）。</summary>
    public async Task<SteamCmdDepotResult> DownloadGameDepotAsync(
        string username,
        string? manifestId,
        string targetPath,
        Action<string>? log = null,
        Action<double>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsSteamCmdInstalled)
        {
            return new SteamCmdDepotResult(false, "SteamCMD 未安装，请先点击\"下载 SteamCMD\"", null);
        }

        if (string.IsNullOrWhiteSpace(username))
        {
            return new SteamCmdDepotResult(false, "请先登录 Steam 账号（会话未缓存）", null);
        }

        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return new SteamCmdDepotResult(false, "请先选择游戏文件保存目录", null);
        }

        try
        {
            if (File.Exists(targetPath))
            {
                return new SteamCmdDepotResult(false, $"目标路径不是目录: {targetPath}", null);
            }

            if (Directory.Exists(targetPath) && Directory.EnumerateFileSystemEntries(targetPath).Any())
            {
                return new SteamCmdDepotResult(false, $"目标目录不为空: {targetPath}", null);
            }
        }
        catch (Exception ex)
        {
            return new SteamCmdDepotResult(false, $"无法检查目标目录: {ex.Message}", null);
        }

        var depotContentPath = GetDepotContentPath();
        var cacheDir = GetGameCacheDir(manifestId);

        // 命中缓存：直接从缓存复制到目标目录，跳过 SteamCMD 下载。
        if (Directory.Exists(cacheDir) && Directory.GetFileSystemEntries(cacheDir).Length > 0)
        {
            log?.Invoke("命中游戏下载缓存，直接从缓存复制...");
            try
            {
                Directory.CreateDirectory(targetPath);
                CopyDirectoryContents(cacheDir, targetPath);
                log?.Invoke($"已从缓存复制游戏文件到: {targetPath}");
                return new SteamCmdDepotResult(true, "下载完成（来自缓存）", targetPath);
            }
            catch (Exception ex)
            {
                return new SteamCmdDepotResult(false, $"从缓存复制失败: {ex.Message}", cacheDir);
            }
        }

        // 清理上次下载残留，避免旧文件混入
        TryDeleteDirectory(depotContentPath);

        var depotCommand = string.IsNullOrWhiteSpace(manifestId)
            ? $"download_depot {StardewAppId} {StardewWindowsDepotId}"
            : $"download_depot {StardewAppId} {StardewWindowsDepotId} {manifestId.Trim()}";

        var versionLabel = string.IsNullOrWhiteSpace(manifestId) ? "最新版" : $"Manifest {manifestId.Trim()}";
        log?.Invoke($"开始下载 Stardew Valley 游戏文件（{versionLabel}）...");

        // 复用已缓存的会话：登录成功后 SteamCMD 会把登录态写到 config，这里只 +login 用户名
        // 即可免密复用，避免每次下载都重新认证/触发 Steam Guard。
        var loginArg = $"+login {EscapeArgument(username.Trim())}";

        // SteamCMD 的 depot 下载 stdout 会被块缓冲，无法实时拿进度。作为兜底：
        // 从日志里 "Downloading depot ... (N files, X MB)" 解析总大小，再监测 depot 目录
        // 增量来计算进度。
        long? totalBytes = null;
        var logWrapper = new Action<string>(msg =>
        {
            log?.Invoke(msg);
            if (totalBytes == null)
            {
                var totalMatch = System.Text.RegularExpressions.Regex.Match(
                    msg, @"Downloading depot \d+ \(\d+ files, ([\d.]+) MB\)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (totalMatch.Success && double.TryParse(totalMatch.Groups[1].Value, out var mb))
                {
                    totalBytes = (long)(mb * 1024 * 1024);
                }
            }
        });

        using var progressCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var monitor = StartDepotProgressMonitor(depotContentPath, () => totalBytes, onProgress, progressCts.Token);

        try
        {
            var exitCode = await RunSteamCmdAsync(
                loginArg + $" +{depotCommand} +quit",
                logWrapper,
                onProgress,
                cancellationToken);

            progressCts.Cancel();

            if (cancellationToken.IsCancellationRequested)
            {
                return new SteamCmdDepotResult(false, "已取消", null);
            }

            if (exitCode != 0 && !Directory.Exists(depotContentPath))
            {
                return new SteamCmdDepotResult(false, $"SteamCMD 退出码异常: {exitCode}", null);
            }

            if (!Directory.Exists(depotContentPath) || Directory.GetFileSystemEntries(depotContentPath).Length == 0)
            {
                return new SteamCmdDepotResult(false, "下载未产生任何文件（请确认已登录并拥有该游戏、Manifest ID 是否有效）", null);
            }
        }
        finally
        {
            progressCts.Cancel();
            try { await monitor; } catch { }
        }

        // 写入游戏缓存（供后续复用/设置清理）
        try
        {
            Directory.CreateDirectory(cacheDir);
            CopyDirectoryContents(depotContentPath, cacheDir);
            log?.Invoke($"游戏文件已写入缓存: {cacheDir}");
        }
        catch (Exception cacheEx)
        {
            log?.Invoke($"写入游戏缓存失败（忽略）: {cacheEx.Message}");
        }

        // 移动到目标目录：不删除用户填写的保存目录，把 depot 内容合并进去（覆盖同名文件）。
        // 用"复制+删除"而非 Directory.Move：SteamCMD 目录在 C:，用户保存目录可能在其它盘符，
        // Directory.Move 跨卷会抛"跨设备"异常导致文件夹为空。
        try
        {
            Directory.CreateDirectory(targetPath);
            CopyDirectoryContents(depotContentPath, targetPath);
            TryDeleteDirectory(depotContentPath);
        }
        catch (Exception ex)
        {
            return new SteamCmdDepotResult(false, $"移动游戏文件失败: {ex.Message}", depotContentPath);
        }

        log?.Invoke($"游戏文件已保存到: {targetPath}");
        return new SteamCmdDepotResult(true, "下载完成", targetPath);
    }

    /// <summary>游戏下载缓存根目录（可在设置中清理）。</summary>
    public static string GameCacheRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SVL", "Avalonia", "cache", "game");

    private static string GetGameCacheDir(string? manifestId)
    {
        var key = string.IsNullOrWhiteSpace(manifestId) ? "latest" : manifestId;
        var safe = new string(key.Where(char.IsLetterOrDigit).ToArray());
        if (string.IsNullOrWhiteSpace(safe))
        {
            safe = "latest";
        }
        return Path.Combine(GameCacheRoot, safe);
    }

    /// <summary>清理游戏下载缓存（供设置页调用）。</summary>
    public static void ClearGameCache()
    {
        try
        {
            if (Directory.Exists(GameCacheRoot))
            {
                Directory.Delete(GameCacheRoot, recursive: true);
            }
        }
        catch
        {
            // best-effort
        }
    }

    /// <summary>把 sourceDir 的内容复制到 destDir（保留目录结构）。</summary>
    private static void CopyDirectoryContents(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            CopyDirectoryContents(subDir, Path.Combine(destDir, Path.GetFileName(subDir)));
        }

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var target = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, target, overwrite: true);
        }
    }

    /// <summary>监测 depot 下载目录的字节增量来估算下载进度（SteamCMD stdout 缓冲，实时进度不可靠）。</summary>
    private static Task StartDepotProgressMonitor(
        string depotContentPath,
        Func<long?> getTotalBytes,
        Action<double>? onProgress,
        CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            try
            {
                // 等待拿到总大小（从日志 "Downloading depot ... (N files, X MB)" 解析）
                long? total = null;
                while (!cancellationToken.IsCancellationRequested)
                {
                    total = getTotalBytes();
                    if (total is long t && t > 0)
                    {
                        break;
                    }
                    await Task.Delay(200, cancellationToken);
                }

                if (total is not long totalSize || totalSize <= 0)
                {
                    return;
                }

                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        long size = 0;
                        if (Directory.Exists(depotContentPath))
                        {
                            size = Directory.EnumerateFiles(depotContentPath, "*", SearchOption.AllDirectories)
                                .Sum(file =>
                                {
                                    try { return new FileInfo(file).Length; }
                                    catch { return 0L; }
                                });
                        }
                        onProgress?.Invoke(Math.Clamp(size * 100.0 / totalSize, 0, 99));
                    }
                    catch { }

                    await Task.Delay(500, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // 正常取消
            }
            catch
            {
                // 监测失败不影响下载
            }
        }, cancellationToken);
    }

    /// <summary>把 sourceDir 的内容合并移动到 destDir（不删除 destDir 本身，保留用户目录）。</summary>
    private static void MoveDirectoryContents(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            var target = Path.Combine(destDir, Path.GetFileName(subDir));
            if (Directory.Exists(target))
            {
                MoveDirectoryContents(subDir, target);
                TryDeleteDirectory(subDir);
            }
            else
            {
                Directory.Move(subDir, target);
            }
        }

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var target = Path.Combine(destDir, Path.GetFileName(file));
            if (File.Exists(target))
            {
                File.Delete(target);
            }
            File.Move(file, target);
        }
    }

    /// <summary>
    /// 运行用户输入的自定义 SteamCMD 指令（如 app_update、download_depot、quit 等）。
    /// 把输入包装为 "+&lt;command&gt; +quit" 运行并实时转发日志。
    /// </summary>
    public async Task RunCustomCommandAsync(
        string commandLine,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsSteamCmdInstalled)
        {
            log?.Invoke("SteamCMD 未安装，请先点击\"下载 SteamCMD\"");
            return;
        }

        var trimmed = commandLine?.Trim() ?? string.Empty;
        trimmed = trimmed.TrimStart('+');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return;
        }

        log?.Invoke($"[SteamCMD] 执行自定义指令: +{trimmed}");

        var arguments = $"+{trimmed} +quit";
        await RunSteamCmdAsync(arguments, log, null, cancellationToken);
    }

    private static string BuildLoginArgs(string username, string password, string? guardCode)
    {
        var sb = new StringBuilder($"+login {EscapeArgument(username)} {EscapeArgument(password)}");
        if (!string.IsNullOrWhiteSpace(guardCode))
        {
            sb.Append(' ').Append(EscapeArgument(guardCode.Trim()));
        }

        return sb.ToString();
    }

    private async Task<SteamCmdLoginResult> RunLoginAsync(
        string args,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        var exitCode = await RunSteamCmdAsync(args + " +quit", log, null, cancellationToken);
        var output = _lastOutput.ToString();

        // 登录成功判定：新版本 SteamCMD 不再打印 "Logged in OK"，
        // 而是到达 "Waiting for client config" / "Waiting for user info" 即表示已通过认证。
        // 必须放在 "Steam Guard" 判定之前，否则成功登录时出现的 "Steam Guard code provided"
        // 会被误判为"需要验证码"。
        if (output.Contains("Logged in OK", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Waiting for user info", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Waiting for client config", StringComparison.OrdinalIgnoreCase))
        {
            return SteamCmdLoginResult.Success();
        }

        if (output.Contains("Two-factor code", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Two-factor", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Steam Guard", StringComparison.OrdinalIgnoreCase))
        {
            return new SteamCmdLoginResult(
                SteamCmdLoginStatus.NeedsGuardCode,
                "需要 Steam Guard 验证码（邮箱验证码或手机令牌），请填写后重试");
        }

        if (output.Contains("Invalid Password", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("FAILED login", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Invalid Password", StringComparison.Ordinal))
        {
            return new SteamCmdLoginResult(SteamCmdLoginStatus.InvalidCredentials, "账号或密码错误");
        }

        if (output.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
        {
            return new SteamCmdLoginResult(SteamCmdLoginStatus.Error, "登录尝试过于频繁，请稍后再试");
        }

        return new SteamCmdLoginResult(
            SteamCmdLoginStatus.Error,
            $"登录失败（退出码 {exitCode}），请查看日志");
    }

    private readonly StringBuilder _lastOutput = new();

    /// <summary>
    /// 串行化所有 SteamCMD 进程执行。并发运行两个 steamcmd（如登录等待 Guard 时又触发
    /// app_info 自动查询）会互相干扰日志/锁文件，导致异常。用信号量保证同一时刻只有一个。
    /// </summary>
    private static readonly SemaphoreSlim SteamCmdLock = new(1, 1);

    private static readonly string TraceLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SVL", "Avalonia", "logs", "steam-trace.log");

    /// <summary>写入独立步骤追踪日志，用于定位登录闪退的崩溃点（即使进程被原生终止也会保留）。</summary>
    private static void Trace(string msg)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(TraceLogPath)!);
            File.AppendAllText(TraceLogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}");
        }
        catch
        {
            // best-effort
        }
    }

    /// <summary>掩盖命令参数中的 +login 密码/验证码，避免明文写进追踪日志。</summary>
    private static string RedactArgs(string args)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            return args;
        }

        // 只掩盖 +login 用户名后、且不是 "+" 命令的令牌（即密码/验证码）。
        return System.Text.RegularExpressions.Regex.Replace(
            args,
            @"(\+login\s+\S+\s+)(?!\+)\S+(?:\s+\S+)?",
            "$1****",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>运行 SteamCMD 命令，实时转发输出到 log，解析进度百分比。</summary>
    /// <remarks>
    /// SteamCMD 首次运行时会自更新：它会打印 "[ NN%] Downloading update" 后 relaunch 自身，
    /// 此时父进程提前退出，实际命令（登录/下载）由子进程执行而未被本方法捕获。
    /// 因此在检测到自更新标记后会自动重跑一次，确保真实命令被执行并被捕获。
    /// </remarks>
    private async Task<int> RunSteamCmdAsync(
        string arguments,
        Action<string>? log,
        Action<double>? onProgress,
        CancellationToken cancellationToken)
    {
        await SteamCmdLock.WaitAsync(cancellationToken);
        try
        {
            Trace($"RunSteamCmd 开始: {RedactArgs(arguments)}");
            for (var attempt = 0; attempt < 2; attempt++)
            {
                // 先清理残留的 steamcmd 进程：若上次自更新 relaunch 的子进程仍在运行，
                // 新实例会只打印 banner 就退出（"Already running" 类问题）。
                // 放到后台线程执行，避免占用 UI 线程导致卡顿。
                await Task.Run(KillLeftoverSteamCmdProcesses);
                Trace($"RunSteamCmd 清理完成，第 {attempt} 次执行: {RedactArgs(arguments)}");

                var (exitCode, selfUpdated) = await RunSteamCmdOnceAsync(arguments, log, onProgress, cancellationToken);
                Trace($"RunSteamCmd 完成 attempt={attempt} exitCode={exitCode} selfUpdated={selfUpdated}");

                if (!selfUpdated)
                {
                    return exitCode;
                }

                if (attempt == 0)
                {
                    log?.Invoke("[SteamCMD] 检测到自更新，重新执行命令以完成实际操作...");
                }
                else
                {
                    log?.Invoke("[SteamCMD] 自更新后仍检测到更新标记，停止重试");
                    return exitCode;
                }
            }

            return 1;
        }
        catch (OperationCanceledException)
        {
            Trace("RunSteamCmd 已取消/超时");
            throw; // 取消/超时向上传播，由调用方给出明确提示
        }
        catch (Exception ex)
        {
            // 任何其它异常都不再向上冒泡，避免导致整个应用闪退
            Trace($"RunSteamCmd 异常: {ex.GetType().Name}: {ex.Message}");
            log?.Invoke($"[SteamCMD] 命令执行异常: {ex.Message}");
            return 1;
        }
        finally
        {
            SteamCmdLock.Release();
        }
    }

    private void KillLeftoverSteamCmdProcesses()
    {
        try
        {
            var executablePath = Path.GetFullPath(SteamCmdExecutablePath);
            foreach (var process in Process.GetProcessesByName("steamcmd"))
            {
                try
                {
                    var processPath = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(processPath) ||
                        !string.Equals(Path.GetFullPath(processPath), executablePath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // ignore
                }
            }
        }
        catch
        {
            // ignore
        }
    }

    private async Task<(int ExitCode, bool SelfUpdated)> RunSteamCmdOnceAsync(
        string arguments,
        Action<string>? log,
        Action<double>? onProgress,
        CancellationToken cancellationToken)
    {
        _lastOutput.Clear();

        var startInfo = new ProcessStartInfo
        {
            FileName = SteamCmdExecutablePath,
            Arguments = arguments,
            WorkingDirectory = SteamCmdDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        Trace("  进程已启动");

        // 关闭 stdin：避免 SteamCMD 在等待验证码输入时挂起
        process.StandardInput.Close();

        var outputLines = new List<string>();
        var syncLock = new object();
        var selfUpdated = false;

        // SteamCMD 重定向 stdout 时会对输出做块缓冲，导致日志不是实时显示。
        // SteamCMD 同时会把自己写的控制台日志落盘（logs/console.log 等，实时写入）。
        // 若存在该日志文件，就改从文件实时 tail，提供接近实时的日志；找不到则回退 stdout。
        var logFile = ResolveSteamLogFile();
        var useFileLog = logFile != null;

        // 处理一行输出：解析进度/自更新标记，并（可选）转发到日志。
        void ProcessLine(string raw, bool display)
        {
            var trimmed = raw.Trim();
            if (trimmed.Length == 0)
            {
                return;
            }

            if (display)
            {
                log?.Invoke(trimmed);
            }

            var progressMatch = ProgressRegex().Match(trimmed);
            if (progressMatch.Success && onProgress != null &&
                double.TryParse(progressMatch.Groups[1].Value, out var percent))
            {
                onProgress(Math.Clamp(percent, 0, 100));
            }

            var updateProgressMatch = UpdateProgressRegex().Match(trimmed);
            if (updateProgressMatch.Success && onProgress != null &&
                double.TryParse(updateProgressMatch.Groups[1].Value, out var updatePercent))
            {
                onProgress(Math.Clamp(updatePercent, 0, 100));
            }

            if (trimmed.Contains("Downloading update", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("Update complete", StringComparison.OrdinalIgnoreCase))
            {
                selfUpdated = true;
            }
        }

        var readTask = Task.Run(async () =>
        {
            try
            {
                // SteamCMD 的 depot 下载进度用 \r 原地刷新（无换行），ReadLineAsync 抓不到。
                // 改为读原始字符流，扫描 progress 并提取完整行。
                var buffer = new char[4096];
                var pending = new StringBuilder();
                while (!cancellationToken.IsCancellationRequested)
                {
                    var read = await process.StandardOutput.ReadAsync(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        break;
                    }

                    pending.Append(buffer, 0, read);
                    var text = pending.ToString();

                    // 从原始文本扫描进度（兼容 \r 原地刷新）
                    ParseProgress(text, onProgress);

                    // 提取完整行（按 \n 切分，保留最后一段不完整行）
                    var lines = text.Split('\n');
                    pending.Clear();
                    pending.Append(lines[^1]);
                    for (var i = 0; i < lines.Length - 1; i++)
                    {
                        var line = lines[i].TrimEnd('\r');
                        if (line.Length == 0)
                        {
                            continue;
                        }

                        lock (syncLock)
                        {
                            outputLines.Add(line);
                            _lastOutput.AppendLine(line);
                        }

                        // 若已用日志文件 tail，则不重复转发 stdout（避免重复行）
                        ProcessLine(line, !useFileLog);
                    }
                }

                // 处理最后一段不完整行
                var tail = pending.ToString().TrimEnd('\r');
                if (tail.Length > 0)
                {
                    lock (syncLock)
                    {
                        outputLines.Add(tail);
                        _lastOutput.AppendLine(tail);
                    }
                    ProcessLine(tail, !useFileLog);
                }
            }
            catch (OperationCanceledException)
            {
                // 正常取消
            }
            catch
            {
                // 进程被杀/管道中断等读取异常，避免未处理异常导致闪退
            }
        }, cancellationToken);

        var errorTask = Task.Run(async () =>
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await process.StandardError.ReadLineAsync(cancellationToken);
                    if (line == null)
                    {
                        break;
                    }

                    lock (syncLock)
                    {
                        _lastOutput.AppendLine(line);
                    }

                    if (!useFileLog)
                    {
                        log?.Invoke($"[stderr] {line.Trim()}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 正常取消
            }
            catch
            {
                // 进程被杀/管道中断等读取异常，避免未处理异常导致闪退
            }
        }, cancellationToken);

        var tailTask = useFileLog
            ? Task.Run(() => TailSteamLogFile(logFile!, process, ProcessLine, syncLock, cancellationToken))
            : null;

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            Trace($"  进程退出 exit={process.ExitCode}");
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);
            throw;
        }

        try
        {
            var allTasks = new List<Task> { readTask, errorTask };
            if (tailTask != null)
            {
                allTasks.Add(tailTask);
            }
            await Task.WhenAll(allTasks);
        }
        catch (OperationCanceledException)
        {
            // 进程已退出，读取任务被取消是正常路径
        }
        catch
        {
            // 读取任务不应抛出未处理异常；此处兜底避免闪退
        }

        return (process.ExitCode, selfUpdated);
    }

    /// <summary>查找 SteamCMD 实时写入的控制台日志文件（日志目录下候选文件名），找不到返回 null。</summary>
    private string? ResolveSteamLogFile()
    {
        var candidates = new[]
        {
            Path.Combine(SteamCmdDirectory, "logs", "console.log"),
            Path.Combine(SteamCmdDirectory, "logs", "console_log.txt")
        };

        // SteamCMD 启动后很快创建日志文件；短暂轮询等待其出现。
        for (var i = 0; i < 8; i++)
        {
            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate) && new FileInfo(candidate).Length > 0)
                {
                    return candidate;
                }
            }

            Thread.Sleep(100);
        }

        return null;
    }

    /// <summary>实时 tail SteamCMD 的日志文件，把新增行转发到日志/进度/自更新解析，直到进程退出或取消。</summary>
    private async Task TailSteamLogFile(
        string filePath,
        Process process,
        Action<string, bool> processLine,
        object syncLock,
        CancellationToken cancellationToken)
    {
        try
        {
            long offset = 0;
            if (File.Exists(filePath))
            {
                offset = new FileInfo(filePath).Length;
            }

            while (!cancellationToken.IsCancellationRequested && !process.HasExited)
            {
                try
                {
                    if (!File.Exists(filePath))
                    {
                        await Task.Delay(200, cancellationToken);
                        continue;
                    }

                    using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (offset < fs.Length)
                    {
                        fs.Seek(offset, SeekOrigin.Begin);
                        using var reader = new StreamReader(fs, Encoding.UTF8);
                        var content = await reader.ReadToEndAsync(cancellationToken);
                        offset = fs.Position;

                        if (!string.IsNullOrWhiteSpace(content))
                        {
                            foreach (var raw in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                            {
                                lock (syncLock)
                                {
                                    _lastOutput.AppendLine(raw);
                                }

                                processLine(raw, true);
                            }
                        }
                    }
                    else
                    {
                        await Task.Delay(200, cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    await Task.Delay(500, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常取消
        }
        catch
        {
            // 文件被占用/删除等，忽略
        }
    }

    // SteamCMD 下载进度有两种：进度条式 "progress: 25.63"（depot 下载，无 %）
    // 与自更新 "[ 32%]"。这里不要求末尾 %，兼容 depot 下载进度。
    [GeneratedRegex(@"progress:\s*([\d.]+)")]
    private static partial Regex ProgressRegex();

    [GeneratedRegex(@"\[\s*(\d+)%\s*\]")]
    private static partial Regex UpdateProgressRegex();

    /// <summary>从原始输出文本扫描所有进度值（兼容 SteamCMD 用 \r 原地刷新、无换行的进度行）。</summary>
    private static void ParseProgress(string text, Action<double>? onProgress)
    {
        if (onProgress == null || string.IsNullOrEmpty(text))
        {
            return;
        }

        foreach (System.Text.RegularExpressions.Match match in ProgressRegex().Matches(text))
        {
            if (double.TryParse(match.Groups[1].Value, out var percent))
            {
                onProgress(Math.Clamp(percent, 0, 100));
            }
        }

        foreach (System.Text.RegularExpressions.Match match in UpdateProgressRegex().Matches(text))
        {
            if (double.TryParse(match.Groups[1].Value, out var percent))
            {
                onProgress(Math.Clamp(percent, 0, 100));
            }
        }
    }

    private static string EscapeArgument(string value)
    {
        if (value == null)
        {
            return "\"\"";
        }

        var builder = new StringBuilder(value.Length + 2);
        builder.Append('\"');
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '\"')
            {
                builder.Append('\\', backslashes * 2 + 1);
                builder.Append('\"');
                backslashes = 0;
                continue;
            }

            if (backslashes > 0)
            {
                builder.Append('\\', backslashes);
                backslashes = 0;
            }

            builder.Append(character);
        }

        builder.Append('\\', backslashes * 2);
        builder.Append('\"');
        return builder.ToString();
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // ignore
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignore
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // ignore
        }
    }
}
