using Microsoft.Win32;
using SVL.Core.Platform.Abstractions;

namespace SVL.Core.Platform.Services;

public sealed class NxmProtocolRegistrationService : INxmProtocolRegistrationService
{
    private const string ProtocolKeyPath = "Software\\Classes\\nxm";
    private const string OpenCommandKeyPath = "Software\\Classes\\nxm\\shell\\open\\command";

    public NxmProtocolRegistrationResult GetStatus()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new NxmProtocolRegistrationResult
            {
                IsSuccess = true,
                IsSupported = false,
                IsRegistered = false,
                Message = "当前平台不支持自动注册 NXM 协议"
            };
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(OpenCommandKeyPath, writable: false);
            var value = key?.GetValue(string.Empty) as string;
            var isRegistered = !string.IsNullOrWhiteSpace(value);

            return new NxmProtocolRegistrationResult
            {
                IsSuccess = true,
                IsSupported = true,
                IsRegistered = isRegistered,
                Message = isRegistered ? "NXM 协议已注册" : "NXM 协议未注册"
            };
        }
        catch (Exception ex)
        {
            return new NxmProtocolRegistrationResult
            {
                IsSuccess = false,
                IsSupported = true,
                IsRegistered = false,
                Message = $"读取 NXM 协议状态失败: {ex.Message}"
            };
        }
    }

    public NxmProtocolRegistrationResult TryRegister(string launcherExecutablePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new NxmProtocolRegistrationResult
            {
                IsSuccess = true,
                IsSupported = false,
                IsRegistered = false,
                Message = "当前平台不支持自动注册 NXM 协议"
            };
        }

        if (string.IsNullOrWhiteSpace(launcherExecutablePath) || !File.Exists(launcherExecutablePath))
        {
            return new NxmProtocolRegistrationResult
            {
                IsSuccess = false,
                IsSupported = true,
                IsRegistered = false,
                Message = "未找到启动器可执行文件，无法注册 NXM 协议"
            };
        }

        try
        {
            using (var protocolKey = Registry.CurrentUser.CreateSubKey(ProtocolKeyPath))
            {
                protocolKey?.SetValue(string.Empty, "URL:nxm Protocol");
                protocolKey?.SetValue("URL Protocol", string.Empty);
            }

            using (var iconKey = Registry.CurrentUser.CreateSubKey($"{ProtocolKeyPath}\\DefaultIcon"))
            {
                iconKey?.SetValue(string.Empty, launcherExecutablePath);
            }

            using (var commandKey = Registry.CurrentUser.CreateSubKey(OpenCommandKeyPath))
            {
                commandKey?.SetValue(string.Empty, $"\"{launcherExecutablePath}\" \"%1\"");
            }

            return new NxmProtocolRegistrationResult
            {
                IsSuccess = true,
                IsSupported = true,
                IsRegistered = true,
                Message = "NXM 协议注册成功"
            };
        }
        catch (Exception ex)
        {
            return new NxmProtocolRegistrationResult
            {
                IsSuccess = false,
                IsSupported = true,
                IsRegistered = false,
                Message = $"NXM 协议注册失败: {ex.Message}"
            };
        }
    }
}