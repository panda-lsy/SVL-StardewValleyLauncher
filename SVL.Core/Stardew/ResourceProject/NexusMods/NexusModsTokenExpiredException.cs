using System;

namespace SVL.Core.Stardew.ResourceProject.NexusMods;

/// <summary>
/// NexusMods Token 过期异常
/// 当遇到 401 Unauthorized 错误时抛出，表示 Access Token 已过期
/// </summary>
public class NexusModsTokenExpiredException : Exception
{
    public NexusModsTokenExpiredException()
        : base("NexusMods Access Token 已过期，请重新登录")
    {
    }

    public NexusModsTokenExpiredException(string message)
        : base(message)
    {
    }

    public NexusModsTokenExpiredException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
