using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using SVL.Core.Stardew.Instance;
using SVL.Core.Logging;

namespace SVL.Core.Stardew.Instance;

/// <summary>
/// 设置服务
/// </summary>
public static class SettingsService
{
    private static readonly string SettingsFolder = Path.Combine(
        GetApplicationDirectory(),
        "SVL"
    );

    /// <summary>
    /// 获取应用程序所在目录（EXE 同目录）
    /// </summary>
    private static string GetApplicationDirectory()
    {
        try
        {
            // 获取程序集所在目录
            var assemblyLocation = System.Reflection.Assembly.GetExecutingAssembly().Location;
            if (!string.IsNullOrEmpty(assemblyLocation))
            {
                return Path.GetDirectoryName(assemblyLocation) ?? Environment.CurrentDirectory;
            }
        }
        catch
        {
            // 如果获取失败，使用当前目录
        }

        // 备用方案：使用当前目录或应用域基础目录
        return AppDomain.CurrentDomain.BaseDirectory ?? Environment.CurrentDirectory;
    }

    private static readonly string InstancesFile = Path.Combine(SettingsFolder, "instances.json");
    private static readonly string DefaultInstanceFile = Path.Combine(SettingsFolder, "default_instance.json");

    /// <summary>
    /// 保存默认实例 ID
    /// </summary>
    public static void SaveDefaultInstance(string instanceId)
    {
        try
        {
            if (!Directory.Exists(SettingsFolder))
                Directory.CreateDirectory(SettingsFolder);

            File.WriteAllText(DefaultInstanceFile, instanceId ?? string.Empty);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"保存默认实例失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 加载默认实例 ID
    /// </summary>
    public static string LoadDefaultInstanceId()
    {
        try
        {
            if (File.Exists(DefaultInstanceFile))
            {
                var instanceId = File.ReadAllText(DefaultInstanceFile);
                return instanceId ?? string.Empty;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"加载默认实例失败：{ex.Message}");
        }

        return string.Empty;
    }

    /// <summary>
    /// 保存实例列表
    /// </summary>
    public static void SaveInstances(List<GamePathInfo> instances)
    {
        try
        {
            Directory.CreateDirectory(SettingsFolder);

            var options = new JsonSerializerOptions
            {
                WriteIndented = true
            };

            var json = JsonSerializer.Serialize(instances, options);
            File.WriteAllText(InstancesFile, json);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"保存实例列表失败：{ex.Message}", "错误");
        }
    }

    /// <summary>
    /// 加载实例列表
    /// </summary>
    public static List<GamePathInfo> LoadInstances()
    {
        try
        {
            if (!File.Exists(InstancesFile))
                return new List<GamePathInfo>();

            var json = File.ReadAllText(InstancesFile);
            var instances = JsonSerializer.Deserialize<List<GamePathInfo>>(json);

            return instances ?? new List<GamePathInfo>();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"加载实例列表失败：{ex.Message}", "错误");
            return new List<GamePathInfo>();
        }
    }

    /// <summary>
    /// 删除实例
    /// </summary>
    /// <param name="instanceId">要删除的实例ID</param>
    /// <returns>(是否删除成功, 错误信息)</returns>
    public static (bool success, string errorMessage) DeleteInstance(string instanceId)
    {
        try
        {
            // 加载所有实例
            var instances = LoadInstances();

            // 查找要删除的实例
            var instanceToDelete = instances.FirstOrDefault(i => i.Id == instanceId);
            if (instanceToDelete == null)
            {
                return (false, $"找不到实例: {instanceId}");
            }

            // Base 实例属于根路径基座，不允许删除。
            var isBaseInstance = !instanceToDelete.EnableIsolation
                && (instanceToDelete.Tags?.Any(t => string.Equals(t, "Base", StringComparison.OrdinalIgnoreCase)) ?? false);
            if (isBaseInstance)
            {
                return (false, "Base 版本不能删除，请使用 SMAPI 卸载或切换实例。" );
            }

            // 如果实例启用了版本隔离，删除版本目录
            if (instanceToDelete.EnableIsolation && !string.IsNullOrEmpty(instanceToDelete.GamePath))
            {
                // 使用实例名称作为版本文件夹名称（与创建时的规则一致）
                var versionFolderName = InstanceIsolationService.GenerateVersionFolderName(
                    instanceToDelete.Name,
                    instanceToDelete.IsSMAPIInstance);
                var versionsPath = Path.Combine(instanceToDelete.GamePath, "versions", versionFolderName);

                if (!Directory.Exists(versionsPath))
                {
                    return (false, $"版本目录不存在：\n{versionsPath}");
                }

                var deleteSuccess = DeleteVersionDirectory(versionsPath);
                if (!deleteSuccess)
                {
                    return (false, $"删除版本目录失败：\n{versionsPath}\n\n请检查：\n• 文件是否正在被使用（请关闭游戏后重试）\n• 是否有足够的权限");
                }
            }

            // 从列表中移除
            instances.Remove(instanceToDelete);

            // 保存更新后的列表
            SaveInstances(instances);

            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            Log.Error($"[SettingsService] 删除实例失败: {instanceId}", ex);
            return (false, $"删除失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 删除版本目录（处理 junction 和硬链接）
    /// </summary>
    private static bool DeleteVersionDirectory(string versionsPath)
    {
        try
        {
            Log.Info($"[SettingsService] 开始删除版本目录: {versionsPath}");

            // *** 新架构：先删除 Content 目录连接 ***
            var contentLinkPath = Path.Combine(versionsPath, "Content");
            if (Directory.Exists(contentLinkPath))
            {
                try
                {
                    // 使用 rmdir 删除目录连接（junction）
                    var process = new System.Diagnostics.Process
                    {
                        StartInfo = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "cmd.exe",
                            Arguments = $"/c rmdir \"{contentLinkPath}\"",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true
                        }
                    };
                    process.Start();
                    process.WaitForExit();
                    if (process.ExitCode == 0)
                    {
                        Log.Info($"[SettingsService] ✓ 已删除 Content 目录连接: {contentLinkPath}");
                    }
                    else
                    {
                        var error = process.StandardError.ReadToEnd();
                        Log.Warn($"[SettingsService] 删除 Content 连接失败 (退出码: {process.ExitCode}): {error}");
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"[SettingsService] 删除 Content 目录连接失败: {contentLinkPath}", ex);
                }
            }

            // *** 兼容旧架构：删除 game 目录连接 ***
            var gameLinkPath = Path.Combine(versionsPath, "game");
            if (Directory.Exists(gameLinkPath))
            {
                try
                {
                    var process = new System.Diagnostics.Process
                    {
                        StartInfo = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "cmd.exe",
                            Arguments = $"/c rmdir \"{gameLinkPath}\"",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true
                        }
                    };
                    process.Start();
                    process.WaitForExit();
                    Log.Info($"[SettingsService] 已删除 game 目录连接: {gameLinkPath}");
                }
                catch (Exception ex)
                {
                    Log.Warn($"[SettingsService] 删除 game 目录连接失败: {gameLinkPath}", ex);
                }
            }

            // 不再逐个物理删除版本文件。用户删除实例时应能从系统回收站恢复，
            // 因此在 junction 已被移除后，将完整版本目录一次性发送到回收站。
            if (!MovePathToRecycleBin(versionsPath))
            {
                Log.Warn($"[SettingsService] 无法将版本目录移入回收站: {versionsPath}");
                return false;
            }

            Log.Info($"[SettingsService] ✓ 已将版本目录移入回收站: {versionsPath}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"[SettingsService] 删除版本目录失败: {versionsPath}", ex);
            return false;
        }
    }

    /// <summary>
    /// 将文件或目录交给 Explorer 回收站，保留用户恢复能力。
    /// </summary>
    private static bool MovePathToRecycleBin(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var operation = new ShellFileOperation
        {
            Function = FileOperationDelete,
            From = path + "\0\0",
            To = string.Empty,
            Flags = (ushort)(AllowUndo | NoConfirmation | Silent | NoErrorUi),
            ProgressTitle = string.Empty
        };

        try
        {
            var result = SHFileOperation(ref operation);
            return result == 0 && !operation.AnyOperationAborted &&
                   !File.Exists(path) && !Directory.Exists(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"[SettingsService] 回收站操作失败: {path}", ex);
            return false;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShellFileOperation operation);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileOperation
    {
        public IntPtr WindowHandle;
        public uint Function;
        public string From;
        public string To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool AnyOperationAborted;
        public IntPtr NameMappings;
        public string ProgressTitle;
    }

    private const uint FileOperationDelete = 0x0003;
    private const ushort NoConfirmation = 0x0010;
    private const ushort Silent = 0x0004;
    private const ushort AllowUndo = 0x0040;
    private const ushort NoErrorUi = 0x0400;

    /// <summary>
    /// 移动实例到回收站（将实例标记为已删除）
    /// </summary>
    /// <param name="instanceId">要删除的实例ID</param>
    /// <returns>(是否删除成功, 错误信息)</returns>
    public static (bool success, string errorMessage) MoveInstanceToRecycleBin(string instanceId)
    {
        try
        {
            // 加载所有实例
            var instances = LoadInstances();

            // 查找要删除的实例
            var instanceToDelete = instances.FirstOrDefault(i => i.Id == instanceId);
            if (instanceToDelete == null)
            {
                return (false, $"找不到实例: {instanceId}");
            }

            // DeleteInstance 会先将版本目录移入系统回收站，再更新实例列表。
            return DeleteInstance(instanceId);
        }
        catch (Exception ex)
        {
            Log.Error($"[SettingsService] 移动实例到回收站失败: {instanceId}", ex);
            return (false, $"删除失败：{ex.Message}");
        }
    }
}
