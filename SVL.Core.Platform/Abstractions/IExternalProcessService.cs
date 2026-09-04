namespace SVL.Core.Platform.Abstractions;

public interface IExternalProcessService
{
    bool TryOpenUrl(string url);

    bool TryOpenPath(string path);

    bool TryLaunchProcess(string fileName, string arguments, string? workingDirectory = null);

    int RunCommand(string fileName, string arguments, string? workingDirectory = null);
}
