namespace SVL.Avalonia.Services;

/// <summary>
/// Avalonia 的兼容门面；底层回收站行为由共享平台服务实现，供 SMAPI 安装等
/// Core.Platform 流程与界面删除操作使用同一套可恢复语义。
/// </summary>
public static class RecycleBinService
{
    public static bool TryMoveToRecycleBin(string path, out string message)
    {
        return SVL.Core.Platform.Services.RecycleBinService.TryMoveToRecycleBin(path, out message);
    }
}
