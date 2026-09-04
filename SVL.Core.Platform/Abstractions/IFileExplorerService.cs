namespace SVL.Core.Platform.Abstractions;

public interface IFileExplorerService
{
    bool TryOpenFolder(string folderPath);

    bool TryRevealFile(string filePath);
}
