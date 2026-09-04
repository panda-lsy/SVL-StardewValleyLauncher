namespace SVL.Core.Platform.Abstractions;

/// <summary>
/// SMAPI 安装服务抽象。负责从 zip 包安装 SMAPI 到隔离实例目录。
/// 实现下沉到 SVL.Core.Platform 以便新旧架构共享同一份安装逻辑。
/// </summary>
public interface ISmapiInstallService
{
    /// <summary>
    /// 从 zip 压缩包安装 SMAPI 到隔离实例目录。
    /// 流程：复制基础游戏文件 → 解压 SMAPI → 定位并复制 payload → 校验 SMAPI 标记。
    /// </summary>
    /// <param name="zipFilePath">SMAPI zip 包路径</param>
    /// <param name="gameBasePath">游戏基础路径（含 Stardew Valley.exe）</param>
    /// <param name="instanceName">实例名称（用于生成 versions/&lt;name&gt;；更新旧实例时兼容其 game 子目录）</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <param name="logger">日志回调（可选），用于向 UI 输出安装过程日志</param>
    /// <param name="zipExtractor">自定义 zip 解压器（可选）。传入 null 时使用默认 ZipFile.ExtractToDirectory。
    /// 用于支持 Deflate64 等非标准压缩方法（如 SharpCompress）。</param>
    /// <param name="updateExisting">更新模式：目标实例已存在时清空旧运行时文件但保留用户 Mods 与启动器元数据，
    /// 再重新安装（参考旧架构 SmapiDownloadTask.IsUpdateMode + CleanupVersionDirectoryForUpdate）。
    /// 为 false 时实例已存在直接返回失败。</param>
    Task<SmapiInstallResult> InstallFromZipAsync(
        string zipFilePath,
        string gameBasePath,
        string instanceName,
        CancellationToken cancellationToken = default,
        Action<string>? logger = null,
        Func<string, string, CancellationToken, Task>? zipExtractor = null,
        bool updateExisting = false);
}

/// <summary>SMAPI 安装结果。独立于具体安装实现，跨架构共享。</summary>
public sealed class SmapiInstallResult
{
    public bool IsSuccess { get; init; }

    public bool IsCancelled { get; init; }

    public string Message { get; init; } = string.Empty;

    public string RuntimePath { get; init; } = string.Empty;

    public string VersionRootPath { get; init; } = string.Empty;

    public static SmapiInstallResult Success(string runtimePath, string versionRootPath)
    {
        return new SmapiInstallResult
        {
            IsSuccess = true,
            Message = "SMAPI 安装成功",
            RuntimePath = runtimePath,
            VersionRootPath = versionRootPath
        };
    }

    public static SmapiInstallResult Failed(string message)
    {
        return new SmapiInstallResult
        {
            IsSuccess = false,
            IsCancelled = false,
            Message = message
        };
    }

    public static SmapiInstallResult Cancelled(string message)
    {
        return new SmapiInstallResult
        {
            IsSuccess = false,
            IsCancelled = true,
            Message = message
        };
    }
}
