using SVL.Core.Platform.Abstractions;

namespace SVL.Avalonia.Services;

/// <summary>
/// SMAPI 安装服务（转发壳）。实际逻辑已下沉到 SVL.Core.Platform.Services.SmapiInstallService，
/// 此类保留以维持现有调用方（VersionSettingsPageViewModel/DownloadPageViewModel）的依赖注入不变。
/// 用内部代理类避免与本类同名冲突（直接引用 Core.Platform.Services.SmapiInstallService 会触发 CS0104）。
/// 转发时注入 SharpCompress 解压器，支持 Deflate64 等非标准压缩方法。
/// </summary>
public sealed class SmapiInstallService : ISmapiInstallService
{
    private readonly ISmapiInstallService _inner = new CoreSmapiInstallServiceImpl();

    public Task<SmapiInstallResult> InstallFromZipAsync(
        string zipFilePath,
        string gameBasePath,
        string instanceName,
        CancellationToken cancellationToken = default,
        Action<string>? logger = null,
        Func<string, string, CancellationToken, Task>? zipExtractor = null,
        bool updateExisting = false)
    {
        // 默认注入 SharpCompress 解压器，支持 Deflate64 压缩
        var effectiveExtractor = zipExtractor ?? ZipExtractor.ExtractToDirectoryAsync;
        return _inner.InstallFromZipAsync(
            zipFilePath,
            gameBasePath,
            instanceName,
            cancellationToken,
            logger,
            effectiveExtractor,
            updateExisting);
    }
}

internal sealed class CoreSmapiInstallServiceImpl : ISmapiInstallService
{
    private readonly ISmapiInstallService _inner = new global::SVL.Core.Platform.Services.SmapiInstallService();

    public Task<SmapiInstallResult> InstallFromZipAsync(
        string zipFilePath,
        string gameBasePath,
        string instanceName,
        CancellationToken cancellationToken = default,
        Action<string>? logger = null,
        Func<string, string, CancellationToken, Task>? zipExtractor = null,
        bool updateExisting = false)
    {
        return _inner.InstallFromZipAsync(
            zipFilePath,
            gameBasePath,
            instanceName,
            cancellationToken,
            logger,
            zipExtractor,
            updateExisting);
    }
}
