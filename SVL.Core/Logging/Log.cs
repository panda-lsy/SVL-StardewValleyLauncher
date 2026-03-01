using System;

namespace SVL.Core.Logging;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
    Critical
}

public static class Log
{
    public static void Debug(string message, Exception? exception = null) => WriteLog(LogLevel.Debug, message, exception);

    public static void Info(string message, Exception? exception = null) => WriteLog(LogLevel.Info, message, exception);

    public static void Warn(string message, Exception? exception = null) => WriteLog(LogLevel.Warning, message, exception);

    public static void Error(Exception exception, string message) => WriteLog(LogLevel.Error, message, exception);

    public static void Error(string message, Exception? exception = null) => WriteLog(LogLevel.Error, message, exception);

    public static void Critical(string message, Exception? exception = null) => WriteLog(LogLevel.Critical, message, exception);

    private static void WriteLog(LogLevel level, string message, Exception? exception)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var exInfo = exception != null ? $" [{exception.GetType().Name}: {exception.Message}]" : string.Empty;
        var logMessage = $"[{timestamp}] [{level}] {message}{exInfo}";

        Console.WriteLine(logMessage);

        if (level >= LogLevel.Warning && exception != null)
        {
            Console.WriteLine(exception.StackTrace);
        }
    }
}
