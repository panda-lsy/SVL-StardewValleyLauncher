using System.Runtime.InteropServices;
using System.Xml;
using System.Xml.Linq;
using SVL.Core.Platform.Abstractions;

namespace SVL.Core.Platform.Services;

/// <summary>通过应用 bundle 声明和 Launch Services 注册 macOS NXM URL handler。</summary>
internal static class MacOSNxmProtocolRegistration
{
    private const string Scheme = "nxm";
    private const uint Utf8Encoding = 0x08000100;
    private const string CoreServices = "/System/Library/Frameworks/CoreServices.framework/CoreServices";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    internal static NxmProtocolRegistrationResult GetStatus(string? launcherExecutablePath)
    {
        try
        {
            if (!TryGetBundleInfo(launcherExecutablePath, out var bundleId, out _, out var error))
            {
                return new NxmProtocolRegistrationResult
                {
                    IsSuccess = true,
                    IsSupported = true,
                    IsRegistered = false,
                    Message = error
                };
            }

            var currentHandler = GetDefaultHandler();
            var registered = string.Equals(currentHandler, bundleId, StringComparison.Ordinal);
            return new NxmProtocolRegistrationResult
            {
                IsSuccess = true,
                IsSupported = true,
                IsRegistered = registered,
                Message = registered ? "NXM 协议已注册" : "NXM 协议未注册"
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

    internal static NxmProtocolRegistrationResult TryRegister(string launcherExecutablePath)
    {
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
            if (!TryGetBundleInfo(launcherExecutablePath, out var bundleId, out _, out var error))
            {
                return new NxmProtocolRegistrationResult
                {
                    IsSuccess = false,
                    IsSupported = true,
                    IsRegistered = false,
                    Message = error
                };
            }

            var status = SetDefaultHandler(bundleId);
            if (status != 0)
            {
                return new NxmProtocolRegistrationResult
                {
                    IsSuccess = false,
                    IsSupported = true,
                    IsRegistered = false,
                    Message = $"NXM 协议注册失败（Launch Services 错误码 {status}）"
                };
            }

            return GetStatus(launcherExecutablePath);
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

    internal static bool TryGetBundleInfo(
        string? launcherExecutablePath,
        out string bundleId,
        out string bundlePath,
        out string error)
    {
        bundleId = string.Empty;
        bundlePath = string.Empty;
        error = "无法识别 macOS .app；NXM 协议未注册。请使用标准 SVL .app 包。";

        if (string.IsNullOrWhiteSpace(launcherExecutablePath))
        {
            return false;
        }

        var executableDirectory = Path.GetDirectoryName(Path.GetFullPath(launcherExecutablePath));
        if (string.IsNullOrWhiteSpace(executableDirectory))
        {
            return false;
        }

        var current = new DirectoryInfo(executableDirectory);
        while (current.Exists)
        {
            if (current.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            {
                var infoPlist = Path.Combine(current.FullName, "Contents", "Info.plist");
                if (!File.Exists(infoPlist))
                {
                    error = "SVL .app 缺少 Contents/Info.plist，无法注册 NXM 协议。";
                    return false;
                }

                using var reader = XmlReader.Create(infoPlist, new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Ignore,
                    XmlResolver = null
                });
                var document = XDocument.Load(reader);
                var dict = document.Root?.Element("dict");
                bundleId = ReadDictionaryString(dict, "CFBundleIdentifier") ?? string.Empty;
                var declaresScheme = ReadUrlSchemes(dict).Any(value => value.Equals(Scheme, StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrWhiteSpace(bundleId) || !declaresScheme)
                {
                    error = "SVL .app 尚未在 Info.plist 声明 nxm URL scheme；请重新打包应用后再注册。";
                    return false;
                }

                bundlePath = current.FullName;
                error = string.Empty;
                return true;
            }

            current = current.Parent;
            if (current is null)
            {
                break;
            }
        }

        return false;
    }

    private static string? ReadDictionaryString(XElement? dictionary, string keyName)
    {
        if (dictionary is null)
        {
            return null;
        }

        var nodes = dictionary.Elements().ToArray();
        for (var index = 0; index + 1 < nodes.Length; index++)
        {
            if (nodes[index].Name == "key" && nodes[index].Value == keyName)
            {
                return nodes[index + 1].Name == "string" ? nodes[index + 1].Value : null;
            }
        }

        return null;
    }

    private static IEnumerable<string> ReadUrlSchemes(XElement? dictionary)
    {
        if (dictionary is null)
        {
            yield break;
        }

        var nodes = dictionary.Elements().ToArray();
        for (var index = 0; index + 1 < nodes.Length; index++)
        {
            if (nodes[index].Name != "key" || nodes[index].Value != "CFBundleURLTypes" || nodes[index + 1].Name != "array")
            {
                continue;
            }

            foreach (var entry in nodes[index + 1].Elements("dict"))
            {
                var entryNodes = entry.Elements().ToArray();
                for (var entryIndex = 0; entryIndex + 1 < entryNodes.Length; entryIndex++)
                {
                    if (entryNodes[entryIndex].Name != "key" ||
                        entryNodes[entryIndex].Value != "CFBundleURLSchemes" ||
                        entryNodes[entryIndex + 1].Name != "array")
                    {
                        continue;
                    }

                    foreach (var scheme in entryNodes[entryIndex + 1].Elements("string"))
                    {
                        yield return scheme.Value;
                    }
                }
            }
        }
    }

    private static int SetDefaultHandler(string bundleId)
    {
        var schemeRef = CreateString(Scheme);
        try
        {
            var bundleRef = CreateString(bundleId);
            try
            {
                return LSSetDefaultHandlerForURLScheme(schemeRef, bundleRef);
            }
            finally
            {
                CFRelease(bundleRef);
            }
        }
        finally
        {
            CFRelease(schemeRef);
        }
    }

    private static string? GetDefaultHandler()
    {
        var schemeRef = CreateString(Scheme);
        try
        {
            var handlerRef = LSCopyDefaultHandlerForURLScheme(schemeRef);
            if (handlerRef == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var length = CFStringGetLength(handlerRef);
                var bufferSize = CFStringGetMaximumSizeForEncoding(length, Utf8Encoding) + 1;
                var buffer = new byte[checked((int)bufferSize)];
                if (!CFStringGetCString(handlerRef, buffer, bufferSize, Utf8Encoding))
                {
                    return null;
                }

                var terminator = Array.IndexOf(buffer, (byte)0);
                return System.Text.Encoding.UTF8.GetString(buffer, 0, terminator >= 0 ? terminator : buffer.Length);
            }
            finally
            {
                CFRelease(handlerRef);
            }
        }
        finally
        {
            CFRelease(schemeRef);
        }
    }

    private static IntPtr CreateString(string value)
    {
        var result = CFStringCreateWithCString(IntPtr.Zero, value, Utf8Encoding);
        return result != IntPtr.Zero
            ? result
            : throw new InvalidOperationException("无法创建 Launch Services 字符串参数。");
    }

    [DllImport(CoreServices, EntryPoint = "LSSetDefaultHandlerForURLScheme")]
    private static extern int LSSetDefaultHandlerForURLScheme(IntPtr urlScheme, IntPtr handlerBundleId);

    [DllImport(CoreServices, EntryPoint = "LSCopyDefaultHandlerForURLScheme")]
    private static extern IntPtr LSCopyDefaultHandlerForURLScheme(IntPtr urlScheme);

    [DllImport(CoreFoundation, EntryPoint = "CFStringCreateWithCString")]
    private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, uint encoding);

    [DllImport(CoreFoundation, EntryPoint = "CFStringGetLength")]
    private static extern nint CFStringGetLength(IntPtr value);

    [DllImport(CoreFoundation, EntryPoint = "CFStringGetMaximumSizeForEncoding")]
    private static extern nint CFStringGetMaximumSizeForEncoding(nint length, uint encoding);

    [DllImport(CoreFoundation, EntryPoint = "CFStringGetCString")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFStringGetCString(IntPtr value, [Out] byte[] buffer, nint bufferSize, uint encoding);

    [DllImport(CoreFoundation, EntryPoint = "CFRelease")]
    private static extern void CFRelease(IntPtr value);
}
