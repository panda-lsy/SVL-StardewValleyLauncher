using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;

namespace SVL.Avalonia.Services;

/// <summary>使用当前可信程序的临时副本完成 Windows 单文件更新，并保留旧程序用于回滚。</summary>
internal static class LauncherUpdateApplier
{
    internal const string ApplyArgument = "--svl-apply-update";
    internal const string FailureArgument = "--svl-update-failed";
    private const long MaxExecutableBytes = 512L * 1024 * 1024;
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SVL", "Avalonia", "logs", "launcher-update.log");

    internal static void Start(string downloadedPath, string workRoot)
    {
        if (!OperatingSystem.IsWindows())
            throw new NotSupportedException("当前平台请前往发布页面手动安装更新");
        if (!File.Exists(downloadedPath)) throw new FileNotFoundException("更新文件不存在", downloadedPath);
        var target = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位当前程序");
        if (!string.IsNullOrEmpty(Assembly.GetEntryAssembly()?.Location))
            throw new InvalidOperationException("源码运行模式无法自动替换，请使用发布版启动器");
        ValidateExecutable(target);

        var workDir = Path.Combine(workRoot, "apply-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var payload = PrepareExecutable(downloadedPath, workDir);
        ValidateExecutable(payload);
        var helper = Path.Combine(workDir, "SVL-update-helper.exe");
        File.Copy(target, helper);

        using var current = Process.GetCurrentProcess();
        var info = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workDir };
        info.ArgumentList.Add(ApplyArgument);
        info.ArgumentList.Add(current.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        info.ArgumentList.Add(current.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        info.ArgumentList.Add(target);
        info.ArgumentList.Add(payload);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动更新辅助进程");
    }

    internal static bool TryHandle(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0 || args[0] != ApplyArgument) return false;
        var parentExited = false;
        string? target = null;
        try
        {
            if (args.Length != 5 || !int.TryParse(args[1], out var pid) || !long.TryParse(args[2], out var startTicks))
                throw new ArgumentException("更新参数无效");
            target = Path.GetFullPath(args[3]);
            var payload = Path.GetFullPath(args[4]);
            try
            {
                using var parent = Process.GetProcessById(pid);
                if (parent.StartTime.ToUniversalTime().Ticks == startTicks && !parent.WaitForExit(90000))
                    throw new TimeoutException("原启动器尚未退出，已取消更新");
            }
            catch (ArgumentException) { /* 原进程已退出。 */ }
            parentExited = true;
            ValidateExecutable(target);
            ValidateExecutable(payload);

            var result = ReplaceAndLaunch(target, payload, LaunchAndConfirm);
            if (!result.Success)
            {
                Log($"更新失败: {result.Error}; 备份={result.BackupPath}");
                StartPreviousVersion(File.Exists(result.BackupPath) ? result.BackupPath : target);
                exitCode = 1;
            }
            else Log($"更新成功，旧版本备份={result.BackupPath}");
        }
        catch (Exception ex)
        {
            Log($"更新辅助进程失败: {ex}");
            // 原程序还在运行时不能再次启动；只有确认它已退出才尝试恢复。
            if (parentExited && target != null) StartPreviousVersion(target);
            exitCode = 1;
        }
        return true;
    }

    internal static UpdateApplyResult ReplaceAndLaunch(string target, string payload, Func<string, bool> launch)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var staged = target + ".svl-update-" + suffix + ".tmp";
        var backup = target + ".backup-" + suffix + ".exe";
        var failed = target + ".failed-" + suffix + ".exe";
        var replaced = false;
        try
        {
            File.Copy(payload, staged, overwrite: false);
            // 同目录原子替换，并先保存原程序；替换失败时原文件保持不变。
            File.Replace(staged, target, backup, ignoreMetadataErrors: true);
            replaced = true;
            if (!launch(target)) throw new InvalidOperationException("新版本启动后立即退出");
            return new UpdateApplyResult(true, backup, string.Empty);
        }
        catch (Exception ex)
        {
            if (replaced)
            {
                try { File.Replace(backup, target, failed, ignoreMetadataErrors: true); }
                catch (Exception rollback) { return new UpdateApplyResult(false, backup, $"{ex.Message}；恢复失败: {rollback.Message}"); }
            }
            return new UpdateApplyResult(false, backup, ex.Message);
        }
        finally
        {
            // 仅删除本次生成且尚未使用的临时文件，不删除用户原程序或失败版本。
            try { if (File.Exists(staged)) File.Delete(staged); } catch { }
        }
    }

    internal static string PrepareExecutable(string path, string workDir)
    {
        if (string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)) return path;
        if (!string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新包必须是 Windows EXE 或 ZIP");
        using var archive = ZipFile.OpenRead(path);
        var candidates = archive.Entries.Where(entry =>
            string.Equals(Path.GetFileName(entry.FullName.Replace('\\', '/')), "SVL.Avalonia.exe", StringComparison.OrdinalIgnoreCase)).ToList();
        if (candidates.Count != 1 || candidates[0].Length <= 0 || candidates[0].Length > MaxExecutableBytes)
            throw new InvalidDataException("更新 ZIP 中缺少唯一的 SVL.Avalonia.exe");
        var output = Path.Combine(workDir, "SVL-update-payload.exe");
        using var input = candidates[0].Open();
        using var destination = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(destination);
        return output;
    }

    private static void ValidateExecutable(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("更新程序不存在", path);
        var file = new FileInfo(path);
        if (file.Length <= 0 || file.Length > MaxExecutableBytes) throw new InvalidDataException("更新程序大小无效");
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var expected = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => Machine.Amd64,
            Architecture.X86 => Machine.I386,
            Architecture.Arm64 => Machine.Arm64,
            _ => throw new NotSupportedException("当前处理器架构不支持自动更新")
        };
        if (pe.PEHeaders.CoffHeader.Machine != expected ||
            !string.Equals(FileVersionInfo.GetVersionInfo(path).ProductName, "SVL.Avalonia", StringComparison.Ordinal))
            throw new InvalidDataException("更新文件不是与当前系统匹配的 SVL Avalonia 程序");
    }

    private static bool LaunchAndConfirm(string path)
    {
        using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path)! });
        return process != null && !process.WaitForExit(5000);
    }

    private static void StartPreviousVersion(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var info = new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path)! };
            info.ArgumentList.Add(FailureArgument);
            Process.Start(info)?.Dispose();
        }
        catch (Exception ex) { Log($"无法重新打开旧版本: {ex.Message}"); }
    }

    private static void Log(string text)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!); File.AppendAllText(LogPath, $"[{DateTime.Now:O}] {text}{Environment.NewLine}"); }
        catch { }
    }
}

internal sealed record UpdateApplyResult(bool Success, string BackupPath, string Error);
