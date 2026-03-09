using System;
using System.IO;
using System.Threading.Tasks;
using ICSharpCode.SharpZipLib.Zip;
using SVL.Core.Logging;

namespace SVL.Core.IO;

public static class FileServiceExtensions
{
    public static async Task ExtractModAsync(string zipPath, string destinationPath)
    {
        await Task.Run(() =>
        {
            try
            {
                if (File.Exists(zipPath))
                {
                    using (var zipFile = new ZipFile(zipPath))
                    {
                        foreach (ZipEntry entry in zipFile)
                        {
                            var normalizedEntryName = entry.Name.Replace('/', Path.DirectorySeparatorChar);
                            var entryPath = Path.Combine(destinationPath, normalizedEntryName);

                            if (entry.IsDirectory)
                            {
                                Directory.CreateDirectory(entryPath);
                            }
                            else if (!File.Exists(entryPath))
                            {
                                var entryDirectory = Path.GetDirectoryName(entryPath);
                                if (!string.IsNullOrEmpty(entryDirectory) && !Directory.Exists(entryDirectory))
                                {
                                    Directory.CreateDirectory(entryDirectory);
                                }

                                using (var stream = zipFile.GetInputStream(entry))
                                using (var fileStream = File.Create(entryPath))
                                {
                                    stream.CopyTo(fileStream);
                                }
                            }
                        }
                    }
                }

                Log.Info($"Extracted mod to: {destinationPath}");
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Failed to extract mod: {zipPath}");
            }
        });
    }

    public static async Task CopyDirectoryAsync(string sourceDir, string destinationDir, bool overwrite = true)
    {
        await Task.Run(async () =>
        {
            try
            {
                if (!Directory.Exists(sourceDir))
                {
                    Log.Warn($"Source directory does not exist: {sourceDir}");
                    return;
                }

                if (!Directory.Exists(destinationDir))
                {
                    Directory.CreateDirectory(destinationDir);
                }

                var files = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    var relativePath = file.Substring(sourceDir.Length);
                    var destFile = Path.Combine(destinationDir, relativePath.TrimStart('\\'));

                    var destDirPath = Path.GetDirectoryName(destFile);
                    if (!string.IsNullOrEmpty(destDirPath) && !Directory.Exists(destDirPath))
                    {
                        Directory.CreateDirectory(destDirPath);
                    }

                    if (File.Exists(destFile))
                    {
                        if (!overwrite) continue;
                        File.Delete(destFile);
                    }
                    File.Copy(file, destFile, true);
                }

                Log.Info($"Copied directory: {sourceDir} -> {destinationDir}");
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Failed to copy directory: {sourceDir}");
            }
        });
    }

    public static async Task MoveAsync(string source, string destination)
    {
        await Task.Run(() =>
        {
            try
            {
                var destDir = Path.GetDirectoryName(destination);
                if (!Directory.Exists(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }

                if (File.Exists(destination))
                {
                    File.Delete(destination);
                }

                Directory.Move(source, destination);
                Log.Info($"Moved: {source} -> {destination}");
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Failed to move: {source}");
            }
        });
    }
}
