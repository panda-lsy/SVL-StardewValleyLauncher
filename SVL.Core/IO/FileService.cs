using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SVL.Core.IO;

public interface IFileTask
{
    string Name { get; }
    Task ExecuteAsync();
}

public static class FileService
{
    private static readonly ConcurrentQueue<IFileTask> _pendingTasks = new();
    private static readonly AutoResetEvent _continueEvent = new(false);
    private static Thread? _fileThread;

    public static void Initialize()
    {
        if (_fileThread == null || !_fileThread.IsAlive)
        {
            _fileThread = new Thread(ProcessTasks)
            {
                Name = "FileServiceThread",
                IsBackground = true
            };
            _fileThread.Start();
        }
    }

    public static void QueueTask(params IFileTask[] tasks)
    {
        foreach (var task in tasks)
        {
            _pendingTasks.Enqueue(task);
        }
        _continueEvent.Set();
    }

    public static async Task CopyAsync(string source, string destination, bool overwrite = false)
    {
        await Task.Run(() =>
        {
            if (File.Exists(destination))
            {
                if (!overwrite) return;
                File.Delete(destination);
            }

            var directory = Path.GetDirectoryName(destination);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.Copy(source, destination, true);
        });
    }

    public static async Task DeleteAsync(string path)
    {
        await Task.Run(() =>
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            else if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        });
    }

    public static async Task EnsureDirectoryAsync(string path)
    {
        await Task.Run(() =>
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
        });
    }

    private static void ProcessTasks()
    {
        while (true)
        {
            _continueEvent.WaitOne();

            while (_pendingTasks.TryDequeue(out var task))
            {
                try
                {
                    task.ExecuteAsync().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    Logging.Log.Error(ex, $"File task failed: {task.Name}");
                }
            }
        }
    }

    public static void Shutdown()
    {
        _fileThread?.Interrupt();
        _continueEvent.Dispose();
    }
}
