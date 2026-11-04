using System.IO.Compression;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SVL.Core.Platform.IO;

namespace SVL.Migration.Tests;

[TestClass]
public sealed class GameArchiveImporterTests
{
    private string _root;

    [TestInitialize]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "svl-game-import-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public async Task ImportsNestedGameFromNonSeekableStreamIntoSeparateInstances()
    {
        var data = Zip(("./Download/Game/Stardew Valley.dll", "game"),
            ("./Download/Game/StardewValley.GameData.dll", "data"),
            ("./Download/Game/Content/Data/Objects.xnb", "content"),
            ("Download/readme.txt", "outside"));

        await using var firstSource = new NonSeekableStream(data);
        var first = await GameArchiveImporter.ImportZipAsync(firstSource, _root);
        await using var secondSource = new NonSeekableStream(data);
        var second = await GameArchiveImporter.ImportZipAsync(secondSource, _root);

        Assert.AreNotEqual(first, second);
        Assert.AreEqual("game", await File.ReadAllTextAsync(Path.Combine(first, "Stardew Valley.dll")));
        Assert.AreEqual("content", await File.ReadAllTextAsync(Path.Combine(first, "Content", "Data", "Objects.xnb")));
        Assert.IsFalse(File.Exists(Path.Combine(first, "readme.txt")));
        Assert.AreEqual(2, Directory.GetDirectories(_root, "game-*").Length);
        Assert.AreEqual(0, Directory.GetDirectories(_root, ".import-*").Length);
    }

    [TestMethod]
    public async Task RejectsTraversalWithoutTouchingExistingInstance()
    {
        var existing = Path.Combine(_root, "game-existing");
        Directory.CreateDirectory(existing);
        var guard = Path.Combine(existing, "keep.txt");
        await File.WriteAllTextAsync(guard, "keep");
        var escapeName = Path.GetFileName(_root) + "-escape.txt";
        var data = Zip(("Stardew Valley.dll", "game"), ("StardewValley.GameData.dll", "data"),
            ("Content/Data/Objects.xnb", "content"), ("../" + escapeName, "bad"));

        await using var source = new MemoryStream(data);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => GameArchiveImporter.ImportZipAsync(source, _root));

        Assert.AreEqual("keep", await File.ReadAllTextAsync(guard));
        Assert.IsFalse(File.Exists(Path.Combine(Directory.GetParent(_root)!.FullName, escapeName)));
        Assert.AreEqual(0, Directory.GetDirectories(_root, ".import-*").Length);
        Assert.AreEqual(1, Directory.GetDirectories(_root, "game-*").Length);
    }

    [TestMethod]
    public async Task RejectsIncompleteAndAmbiguousGames()
    {
        var incomplete = Zip(("Stardew Valley.dll", "game"), ("StardewValley.GameData.dll", "data"));
        await using (var source = new MemoryStream(incomplete))
            await Assert.ThrowsExceptionAsync<InvalidDataException>(() => GameArchiveImporter.ImportZipAsync(source, _root));

        var ambiguous = Zip(("one/Stardew Valley.dll", "game"), ("one/StardewValley.GameData.dll", "data"),
            ("one/Content/a.xnb", "asset"), ("two/Stardew Valley.dll", "game"),
            ("two/StardewValley.GameData.dll", "data"), ("two/Content/a.xnb", "asset"));
        await using (var source = new MemoryStream(ambiguous))
            await Assert.ThrowsExceptionAsync<InvalidDataException>(() => GameArchiveImporter.ImportZipAsync(source, _root));

        Assert.AreEqual(0, Directory.GetDirectories(_root).Length);
    }

    [TestMethod]
    public async Task RejectsCaseInsensitiveDuplicatePathsAndCleansStaging()
    {
        var data = Zip(("Stardew Valley.dll", "game"), ("StardewValley.GameData.dll", "data"),
            ("Content/a.xnb", "one"), ("content/A.xnb", "two"));
        await using var source = new MemoryStream(data);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => GameArchiveImporter.ImportZipAsync(source, _root));
        Assert.AreEqual(0, Directory.GetDirectories(_root).Length);
    }

    [TestMethod]
    public async Task RejectsSymbolicLinksAndCancelledImports()
    {
        using var bytes = new MemoryStream();
        using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.CreateEntry("link").ExternalAttributes = unchecked((int)(0xA000u << 16));
        }
        bytes.Position = 0;
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => GameArchiveImporter.ImportZipAsync(bytes, _root));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await using var source = new MemoryStream(Zip(("Stardew Valley.dll", "game")));
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => GameArchiveImporter.ImportZipAsync(source, _root, cancellationToken: cancelled.Token));
        Assert.AreEqual(0, Directory.GetDirectories(_root).Length);
    }

    private static byte[] Zip(params (string Path, string Text)[] entries)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.CreateEntry("./");
            foreach (var (path, text) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(path).Open(), Encoding.UTF8);
                writer.Write(text);
            }
        }
        return output.ToArray();
    }

    private sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
        public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();
    }
}
