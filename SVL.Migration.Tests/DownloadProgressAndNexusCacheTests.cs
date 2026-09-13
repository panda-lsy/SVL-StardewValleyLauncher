using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using SharpCompress.Writers;
using SharpCompress.Writers.SevenZip;
using SVL.Avalonia.Models;
using SVL.Avalonia.Services;
using SVL.Avalonia.ViewModels;
using SVL.Core.Platform.Modpack;

namespace SVL.Migration.Tests;

[TestClass]
public sealed class DownloadProgressAndNexusCacheTests
{
    [TestMethod]
    public void TaskStatus_ShouldExposeFullTargetAndInstalledPaths()
    {
        var targetPath = Path.Combine("D:\\Applications", "Stardew Valley", "versions", "Target Base");
        var installedPath = Path.Combine(targetPath, "versions", "Imported Pack");
        var task = new DownloadTaskItem
        {
            Name = "Imported Pack",
            TargetGamePath = targetPath,
            InstalledPath = installedPath
        };

        var page = new TaskStatusPageViewModel();
        page.SetCurrentTask(task);

        Assert.AreEqual(targetPath, page.SelectedTaskTargetGamePath);
        Assert.IsTrue(page.SelectedTaskHasTargetGamePath);
        Assert.AreEqual(installedPath, page.SelectedTaskInstalledPath);
        Assert.IsTrue(page.SelectedTaskHasInstalledPath);
    }

    [TestMethod]
    public void DownloadCacheRetention_ShouldPersistSeparatelyFromSearchCacheRetention()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "svl-download-cache-retention-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AppUserSettingsStore(root);
            store.Save(new AppUserSettings
            {
                CacheRetentionMinutes = 5,
                DownloadCacheRetentionMinutes = 10080
            });

            var loaded = store.Load();
            Assert.AreEqual(5, loaded.CacheRetentionMinutes);
            Assert.AreEqual(10080, loaded.DownloadCacheRetentionMinutes);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void DisplayProgress_ShouldNotReach100BeforeAllBytesAreDownloaded()
    {
        Assert.AreEqual(99, DownloadProgressCalculator.ToDisplayPercent(99.6, 996, 1000));
        Assert.AreEqual(99, DownloadProgressCalculator.ToDisplayPercent(100, 999, 1000));
        Assert.AreEqual(100, DownloadProgressCalculator.ToDisplayPercent(100, 1000, 1000));
    }

    [TestMethod]
    public async Task DownloadCache_ShouldEvictInvalidArtifactWhenValidatorRejectsIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-download-cache-validation-test-" + Guid.NewGuid().ToString("N"));
        var targetPath = Path.Combine(root, "download.zip");
        using var server = new LocalArchiveServer(CreateZipBytes(
            ("manifest.json", "{\"Name\":\"Cache Test\"}")));
        var cachePath = DownloadFileCache.GetCachePath(server.Url.ToString());

        try
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            File.WriteAllText(cachePath, "<html>stale error page</html>");

            var serverTask = server.ServeOnceAsync(
                new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);
            var service = new HttpDownloadService(
                new AppUserSettingsStore(Path.Combine(root, "settings")));

            await service.DownloadAsync(
                server.Url.ToString(),
                targetPath,
                threadCount: 4,
                onProgress: null,
                cacheValidator: IsZipArchive);
            await serverTask;

            Assert.IsTrue(IsZipArchive(targetPath));
            Assert.IsTrue(IsZipArchive(cachePath), "坏的通用 URL 缓存应被清理并替换为有效归档");
        }
        finally
        {
            try
            {
                if (File.Exists(cachePath))
                {
                    File.Delete(cachePath);
                }
            }
            catch { }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public async Task DownloadCache_ShouldHonorCancellationBeforeCopyingHit()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-download-cache-cancel-test-" + Guid.NewGuid().ToString("N"));
        var url = "https://example.invalid/cached-cancel-test.zip";
        var targetPath = Path.Combine(root, "download.zip");
        var cachePath = DownloadFileCache.GetCachePath(url);

        try
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            File.WriteAllBytes(cachePath, CreateZipBytes(
                ("manifest.json", "{\"Name\":\"Cancellation Test\"}")));

            var settings = new AppUserSettingsStore(Path.Combine(root, "settings"));
            settings.Save(new AppUserSettings { EnableDownloadCache = true });
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            var service = new HttpDownloadService(settings);
            await Assert.ThrowsExceptionAsync<OperationCanceledException>(() =>
                service.DownloadAsync(
                    url,
                    targetPath,
                    onProgress: null,
                    cancellationToken: cancellation.Token,
                    cacheValidator: IsZipArchive));

            Assert.IsFalse(File.Exists(targetPath), "取消命中缓存时不应复制目标文件");
        }
        finally
        {
            try
            {
                if (File.Exists(cachePath))
                {
                    File.Delete(cachePath);
                }
            }
            catch { }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public async Task HttpDownloadService_ShouldRetryTruncatedResponse()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "svl-http-truncated-response-test-" + Guid.NewGuid().ToString("N"));
        var targetPath = Path.Combine(root, "download.zip");
        var payload = CreateZipBytes(("manifest.json", "{\"Name\":\"Transient Test\"}"));
        using var server = new TruncatedResponseServer(payload);

        try
        {
            var settings = new AppUserSettingsStore(Path.Combine(root, "settings"));
            settings.Save(new AppUserSettings
            {
                EnableDownloadCache = false,
                DownloadSegmentThreads = 4
            });

            var logs = new List<string>();
            var serverTask = server.ServeAsync(
                new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);
            var service = new HttpDownloadService(settings);

            await service.DownloadAsync(
                server.Url.ToString(),
                targetPath,
                threadCount: 1,
                onProgress: null,
                log: logs.Add,
                cacheValidator: IsZipArchive);
            await serverTask;

            CollectionAssert.AreEqual(payload, File.ReadAllBytes(targetPath));
            StringAssert.Contains(
                string.Join("\n", logs),
                "自动重试",
                "响应提前结束时应在 HTTP 层自动重试，而不是直接把半包交给上层");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void ProgressSnapshot_ShouldWaitForDownloadCallCompletionBeforeShowing100()
    {
        var lastChunkObserved = new DownloadProgressSnapshot(
            100,
            1000,
            1000,
            0,
            SegmentPercents: [100, 100],
            IsComplete: false);
        var completed = lastChunkObserved with { IsComplete = true };

        Assert.AreEqual(99, DownloadProgressCalculator.ToDisplayPercent(lastChunkObserved));
        Assert.AreEqual(100, DownloadProgressCalculator.ToDisplayPercent(completed));
    }

    [TestMethod]
    public void MultipartProgressSnapshot_ShouldOnlyFillSegmentsAfterDownloadCompletes()
    {
        var createSnapshot = typeof(HttpDownloadService).GetMethod(
            "CreateSnapshot",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(createSnapshot);

        var ranges = new (long Start, long End)[]
        {
            (0, 499),
            (500, 999)
        };
        var downloaded = new long[] { 500, 500 };

        var observed = (DownloadProgressSnapshot)createSnapshot!.Invoke(
            null,
            new object[] { 1000L, 1000L, 1d, 0L, downloaded, ranges, false })!;
        Assert.AreEqual(99, DownloadProgressCalculator.ToDisplayPercent(observed));
        Assert.IsNotNull(observed.SegmentPercents);
        Assert.IsTrue(observed.SegmentPercents!.All(percent => percent <= 99));

        var partiallyObserved = (DownloadProgressSnapshot)createSnapshot.Invoke(
            null,
            new object[] { 999L, 1000L, 1d, 0L, new long[] { 499, 500 }, ranges, false })!;
        CollectionAssert.AreEqual(new[] { 99d, 99d }, partiallyObserved.SegmentPercents);

        var completed = (DownloadProgressSnapshot)createSnapshot.Invoke(
            null,
            new object[] { 1000L, 1000L, 1d, 0L, downloaded, ranges, true })!;
        Assert.IsTrue(completed.IsComplete);
        CollectionAssert.AreEqual(new[] { 100d, 100d }, completed.SegmentPercents);
    }

    [TestMethod]
    public void DownloadTaskArtifactReuse_ShouldOnlyAcceptCompleteArchiveForMatchingAction()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-download-artifact-reuse-test-" + Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(root, "mod.zip");
        try
        {
            Directory.CreateDirectory(root);
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                WriteArchiveText(archive, "manifest.json", "{\"Name\":\"Test Mod\",\"UniqueID\":\"Test.Mod\",\"Version\":\"1.0.0\"}");
            }

            var resolver = typeof(DownloadPageViewModel).GetMethod(
                "TryReuseExistingDownloadedArtifact",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(resolver);

            var installTask = new DownloadTaskItem
            {
                TaskAction = DownloadTaskAction.InstallMod,
                OutputFilePath = archivePath
            };
            Assert.IsTrue((bool)resolver!.Invoke(null, [installTask])!);

            var smapiTask = new DownloadTaskItem
            {
                TaskAction = DownloadTaskAction.InstallSmapi,
                OutputFilePath = archivePath
            };
            Assert.IsFalse((bool)resolver.Invoke(null, [smapiTask])!);

            File.WriteAllText(archivePath, "<html>download error</html>");
            Assert.IsFalse((bool)resolver.Invoke(null, [installTask])!);

            File.Delete(archivePath);
            using (var nonModArchive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                WriteArchiveText(nonModArchive, "error.html", "not a mod");
            }

            Assert.IsFalse((bool)resolver.Invoke(null, [installTask])!,
                "没有 manifest.json 的合法压缩包不能作为 Mod 缓存复用");

            var savedFilePath = Path.Combine(root, "saved-resource.bin");
            File.WriteAllBytes(savedFilePath, [1, 2, 3]);
            var saveOnlyTask = new DownloadTaskItem
            {
                TaskAction = DownloadTaskAction.SaveOnly,
                OutputFilePath = savedFilePath
            };
            Assert.IsTrue((bool)resolver.Invoke(null, [saveOnlyTask])!,
                "另存为任务不应强制要求输出文件包含 Mod manifest");

            File.WriteAllBytes(savedFilePath + ".part", [4]);
            Assert.IsFalse((bool)resolver.Invoke(null, [saveOnlyTask])!,
                "仍有断点文件时不能把另存为输出误判为完整文件");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void SegmentProgress_ShouldStayWithinDisplayRange()
    {
        var task = new DownloadTaskItem();

        task.SyncSegmentProgress([150, -1, double.NaN]);

        Assert.AreEqual(100, task.SegmentItems[0].Percent);
        Assert.AreEqual(0, task.SegmentItems[1].Percent);
        Assert.AreEqual(0, task.SegmentItems[2].Percent);
    }

    [TestMethod]
    public void ModpackRetry_ShouldRecognizePreviouslyInstalledRuntimeAfterDownloadStage()
    {
        var resolver = typeof(DownloadPageViewModel).GetMethod(
            "HasPreviouslyInstalledModpackRuntime",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(resolver);

        var task = new DownloadTaskItem
        {
            TaskAction = DownloadTaskAction.InstallModpack,
            TaskState = DownloadTaskState.Downloading,
            InstalledPath = "D:/games/versions/RetryPack"
        };

        // 真实下载整合包时状态已从 Pending 变为 Downloading，判断不能依赖状态文本，
        // 只能依据任务中持久化的已安装运行目录。
        Assert.IsTrue((bool)resolver!.Invoke(null, [task])!);

        task.InstalledPath = string.Empty;
        task.InstalledDirectory = string.Empty;
        Assert.IsFalse((bool)resolver.Invoke(null, [task])!);
    }

    [TestMethod]
    public void RecoveredSmapiTask_ShouldUseUpdateModeWhenInstanceDirectoryExists()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-smapi-retry-runtime-test-" + Guid.NewGuid().ToString("N"));
        var basePath = Path.Combine(root, "game");
        var instanceName = "SMAPI Retry";
        var instancePath = Path.Combine(basePath, "versions", instanceName);
        try
        {
            Directory.CreateDirectory(instancePath);

            var resolver = typeof(DownloadPageViewModel).GetMethod(
                "HasPreviouslyInstalledSmapiRuntime",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(resolver);

            var task = new DownloadTaskItem
            {
                TaskAction = DownloadTaskAction.InstallSmapi,
                TargetGamePath = basePath,
                TargetInstanceName = instanceName
            };

            Assert.IsTrue((bool)resolver!.Invoke(null, [task])!);
            task.TargetGamePath = instancePath;
            Assert.IsTrue((bool)resolver.Invoke(null, [task])!);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void PackageRetry_ShouldReuseLocalArchiveWhenInstallDirectoryWasCreatedBeforeStateSave()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-package-retry-archive-test-" + Guid.NewGuid().ToString("N"));
        var basePath = Path.Combine(root, "game");
        var instanceName = "Retry Pack";
        var archivePath = Path.Combine(root, "retry-pack.zip");
        try
        {
            Directory.CreateDirectory(Path.Combine(basePath, "versions", instanceName));
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                WriteArchiveText(
                    archive,
                    "modpack.json",
                    "{\"name\":\"Retry Pack\",\"version\":\"1.0.0\",\"mods\":[]}");
            }

            var resolver = typeof(DownloadPageViewModel).GetMethod(
                "ShouldReuseLocalPackageArchive",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(resolver);

            var task = new DownloadTaskItem
            {
                TaskKind = DownloadTaskKind.SvlModpack,
                TaskAction = DownloadTaskAction.InstallModpack,
                TargetGamePath = basePath,
                TargetInstanceName = instanceName,
                OutputFilePath = archivePath
            };

            Assert.IsTrue((bool)resolver!.Invoke(null, [task])!);
            File.Delete(archivePath);
            Assert.IsFalse((bool)resolver.Invoke(null, [task])!);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void PackageRetry_ShouldNotReuseCorruptedArchive()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-package-corrupted-archive-test-" + Guid.NewGuid().ToString("N"));
        var basePath = Path.Combine(root, "game");
        var archivePath = Path.Combine(root, "retry-pack.zip");
        try
        {
            Directory.CreateDirectory(Path.Combine(basePath, "versions", "Retry Pack"));
            File.WriteAllText(archivePath, "<html>download error</html>");

            var resolver = typeof(DownloadPageViewModel).GetMethod(
                "ShouldReuseLocalPackageArchive",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(resolver);

            var task = new DownloadTaskItem
            {
                TaskKind = DownloadTaskKind.SvlModpack,
                TaskAction = DownloadTaskAction.InstallModpack,
                TargetGamePath = basePath,
                TargetInstanceName = "Retry Pack",
                OutputFilePath = archivePath
            };

            Assert.IsFalse((bool)resolver!.Invoke(null, [task])!);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void CurseforgePackageCacheSource_ShouldRemainExecutableAndRecoverable()
    {
        var buildSource = typeof(DownloadPageViewModel).GetMethod(
            "BuildCurseforgeCacheSourceUrl",
            BindingFlags.Static | BindingFlags.NonPublic);
        var hasSource = typeof(DownloadPageViewModel).GetMethod(
            "HasRealDownloadSource",
            BindingFlags.Static | BindingFlags.NonPublic);
        var isCacheSource = typeof(DownloadPageViewModel).GetMethod(
            "IsCurseforgeCacheSource",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.IsNotNull(buildSource);
        Assert.IsNotNull(hasSource);
        Assert.IsNotNull(isCacheSource);

        var source = (string)buildSource!.Invoke(null, [1012214L, 5312529L])!;
        Assert.AreEqual("svl-curseforge-cache://1012214/5312529", source);
        Assert.IsTrue((bool)isCacheSource!.Invoke(null, [source])!);

        var task = new DownloadTaskItem
        {
            TaskKind = DownloadTaskKind.CurseforgeModpack,
            TaskAction = DownloadTaskAction.InstallModpack,
            SourceUrl = source,
            OutputFilePath = Path.Combine(Path.GetTempPath(), "cf-pack.zip"),
            SourcePlatform = "Curseforge",
            SourceModId = 1012214,
            SourceFileId = 5312529
        };

        Assert.IsTrue((bool)hasSource!.Invoke(null, [task])!,
            "稳定缓存来源必须进入真实下载执行分支，缓存失效后才能按 ID 刷新 CDN");
    }

    [TestMethod]
    public void PendingPackageTask_ShouldReuseValidArchiveBeforeRuntimeExists()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-package-pending-archive-test-" + Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(root, "pending-pack.zip");
        try
        {
            Directory.CreateDirectory(root);
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                WriteArchiveText(
                    archive,
                    "modpack.json",
                    "{\"name\":\"Pending Pack\",\"version\":\"1.0.0\",\"mods\":[]}");
            }

            var resolver = typeof(DownloadPageViewModel).GetMethod(
                "ShouldReuseLocalPackageArchive",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(resolver);

            var task = new DownloadTaskItem
            {
                TaskKind = DownloadTaskKind.SvlModpack,
                TaskAction = DownloadTaskAction.InstallModpack,
                TargetGamePath = Path.Combine(root, "game"),
                TargetInstanceName = "Pending Pack",
                OutputFilePath = archivePath
            };

            Assert.IsTrue((bool)resolver!.Invoke(null, [task])!);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void RecoveredGenericPackageTask_ShouldRecoverPackageKindFromLocalArchive()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-recovered-generic-package-test-" + Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(root, "downloaded-package.zip");
        try
        {
            Directory.CreateDirectory(root);
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                WriteArchiveText(
                    archive,
                    "modpack.json",
                    "{\"name\":\"Recovered Package\",\"version\":\"1.0.0\",\"mods\":[]}");
            }

            var resolver = typeof(DownloadPageViewModel).GetMethod(
                "TryResolveLocalPackageTaskKind",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(resolver);

            var task = new DownloadTaskItem
            {
                TaskKind = DownloadTaskKind.Generic,
                TaskAction = DownloadTaskAction.InstallModpack,
                OutputFilePath = archivePath
            };

            Assert.IsTrue((bool)resolver!.Invoke(null, [task])!);
            Assert.AreEqual(DownloadTaskKind.SvlModpack, task.TaskKind);
            Assert.AreEqual(DownloadTaskAction.InstallModpack, task.TaskAction);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void ExistingCollectionArchive_ShouldBeRecognizedForDownloadReuse()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-existing-collection-archive-test-" + Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(root, "collection.zip");
        try
        {
            Directory.CreateDirectory(root);
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                WriteArchiveText(
                    archive,
                    "Export/collection.json",
                    "{\"info\":{\"name\":\"Cached Collection\"},\"mods\":[]}");
            }

            var resolver = typeof(DownloadPageViewModel).GetMethod(
                "IsValidCollectionArchive",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(resolver);
            Assert.IsTrue((bool)resolver!.Invoke(null, [archivePath])!);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public async Task CancelledCollectionUpdate_ShouldPreserveExistingInstanceDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-collection-cancel-update-test-" + Guid.NewGuid().ToString("N"));
        var gamePath = Path.Combine(root, "Game");
        var archivePath = Path.Combine(root, "collection.zip");
        var instanceName = "Existing Collection";
        var instancePath = Path.Combine(gamePath, "versions", instanceName);
        var settingsStore = new AppUserSettingsStore(Path.Combine(root, "settings"));

        try
        {
            Directory.CreateDirectory(instancePath);
            var markerPath = Path.Combine(instancePath, "keep-me.txt");
            File.WriteAllText(markerPath, "existing");

            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                WriteArchiveText(
                    archive,
                    "collection.json",
                    "{\"info\":{\"name\":\"Cancelled Update\"},\"mods\":[]}");
            }

            var nxmParser = new SVL.Core.Platform.Abstractions.NxmLinkParser();
            var service = new CollectionInstallService(
                new FixedGameInstallPathLocator(gamePath),
                new TestSmapiInstallService(),
                new HttpDownloadService(settingsStore),
                new RemoteCatalogService(settingsStore),
                settingsStore,
                new NexusModDownloadResolverService(),
                nxmParser,
                new BrowserDownloadFallbackService(
                    nxmParser,
                    new SVL.Core.Platform.Services.ExternalProcessService()),
                new ModpackInstallService(
                    new FixedGameInstallPathLocator(gamePath),
                    new TestSmapiInstallService(),
                    new HttpDownloadService(settingsStore),
                    new RemoteCatalogService(settingsStore),
                    settingsStore,
                    new NexusModDownloadResolverService(),
                    nxmParser));

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var result = await service.InstallCollectionFromArchiveAsync(
                archivePath,
                instanceName,
                onProgress: null,
                cancellationToken: cancellation.Token,
                gameBasePath: gamePath,
                updateExisting: true);

            Assert.IsTrue(result.IsCancelled, result.Message);
            Assert.IsTrue(File.Exists(markerPath), "取消更新不应删除已有实例目录");
            Assert.AreEqual("existing", File.ReadAllText(markerPath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void CollectionRetry_ShouldRecognizeNxmCollectionRuntime()
    {
        var resolver = typeof(DownloadPageViewModel).GetMethod(
            "HasPreviouslyInstalledPackageRuntime",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(resolver);

        var task = new DownloadTaskItem
        {
            TaskKind = DownloadTaskKind.NxmCollection,
            TaskAction = DownloadTaskAction.InstallCollection,
            InstalledPath = "D:/games/versions/Collection"
        };

        Assert.IsTrue((bool)resolver!.Invoke(null, [task])!);
    }

    [TestMethod]
    public void CollectionRetry_ShouldUseFailedUrlsForBothCollectionTaskKinds()
    {
        var resolver = typeof(DownloadPageViewModel).GetMethod(
            "HasFailedCollectionDownloads",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(resolver);

        foreach (var kind in new[] { DownloadTaskKind.NxmCollection, DownloadTaskKind.NexusCollection })
        {
            var task = new DownloadTaskItem
            {
                TaskKind = kind,
                FailedDownloadUrls = ["https://example.invalid/collection.7z"]
            };

            Assert.IsTrue((bool)resolver!.Invoke(null, [task])!, $"任务类型 {kind} 应进入失败下载重试分支");
        }

        var normalModTask = new DownloadTaskItem
        {
            TaskKind = DownloadTaskKind.Generic,
            FailedDownloadUrls = ["https://example.invalid/mod.zip"]
        };
        Assert.IsFalse((bool)resolver.Invoke(null, [normalModTask])!);
    }

    [TestMethod]
    public void NexusDownloadResolver_ShouldParseCollectionLinkResponseVariants()
    {
        var parser = typeof(NexusModDownloadResolverService).GetMethod(
            "ParseDownloadLinks",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(parser);

        var samples = new[]
        {
            "[{\"URI\":\"https://cdn.example.test/one.zip\",\"name\":\"CDN\"}]",
            "{\"download_links\":{\"Nexus CDN\":\"https://cdn.example.test/two.7z\"}}",
            "{\"DOWNLOAD_LINKS\":[{\"url\":\"https://cdn.example.test/three.zip\"}]}",
            "{\"data\":{\"links\":[\"https://cdn.example.test/four.zip\"]}}"
        };

        foreach (var sample in samples)
        {
            using var document = JsonDocument.Parse(sample);
            var result = parser!.Invoke(null, [document.RootElement]);
            Assert.IsNotNull(result);

            var items = ((System.Collections.IEnumerable)result!).Cast<object>().ToList();
            Assert.AreEqual(1, items.Count, sample);
            var uri = items[0].GetType().GetProperty("Uri")?.GetValue(items[0]) as string;
            Assert.IsTrue(uri!.StartsWith("https://cdn.example.test/", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public void RecoveredPackageTask_ShouldRestoreSpecificActionForLegacyState()
    {
        var normalizer = typeof(DownloadPageViewModel).GetMethod(
            "NormalizeRecoveredTaskState",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(normalizer);

        var collectionTask = new DownloadTaskItem
        {
            TaskKind = DownloadTaskKind.NxmCollection,
            TaskAction = DownloadTaskAction.InstallMod,
            TaskState = DownloadTaskState.Pending
        };
        normalizer!.Invoke(null, [collectionTask]);
        Assert.AreEqual(DownloadTaskAction.InstallCollection, collectionTask.TaskAction);

        var modpackTask = new DownloadTaskItem
        {
            TaskKind = DownloadTaskKind.SvlModpack,
            TaskAction = DownloadTaskAction.InstallMod,
            TaskState = DownloadTaskState.Pending
        };
        normalizer.Invoke(null, [modpackTask]);
        Assert.AreEqual(DownloadTaskAction.InstallModpack, modpackTask.TaskAction);
    }

    [TestMethod]
    public void RecoveredSmapiTask_ShouldRecoverInstanceNameFromLegacyNameAndPath()
    {
        var resolver = typeof(DownloadPageViewModel).GetMethod(
            "ResolveExistingSmapiInstanceName",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(resolver);

        var byTaskName = new DownloadTaskItem
        {
            Name = "SMAPI 4.5.2 - Blissful Valley",
            TargetGamePath = Path.Combine(Path.GetTempPath(), "svl-smapi-base")
        };
        Assert.AreEqual("Blissful Valley", resolver!.Invoke(null, [byTaskName]));

        var byLegacyPath = new DownloadTaskItem
        {
            Name = "SMAPI 4.5.2",
            TargetGamePath = Path.Combine(
                Path.GetTempPath(),
                "svl-smapi-base",
                "versions",
                "Legacy SMAPI",
                "game")
        };
        Assert.AreEqual("Legacy SMAPI", resolver.Invoke(null, [byLegacyPath]));
    }

    [TestMethod]
    public void DownloadTaskStateStore_ShouldKeepTasksWhoseNamesContainSmoke()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-task-state-smoke-name-test-" + Guid.NewGuid().ToString("N"));
        var statePath = Path.Combine(root, "tasks.json.gz");
        try
        {
            var task = new DownloadTaskItem
            {
                Name = "Smoke Test UI Mod",
                SourceUrl = "https://example.invalid/smoke-test-ui-mod.zip",
                OutputFilePath = Path.Combine(root, "smoke-test-ui-mod.zip"),
                TaskState = DownloadTaskState.Pending,
                Status = "等待下载"
            };

            var store = new DownloadTaskStateStore();
            store.Save(statePath, [task]);
            var records = store.Load(statePath, out _);

            Assert.AreEqual(1, records.Count);
            Assert.AreEqual(task.Name, records[0].Name);
            Assert.AreEqual(task.SourceUrl, records[0].SourceUrl);
            Assert.AreEqual(task.OutputFilePath, records[0].OutputFilePath);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void DownloadTaskStateStore_ShouldReadLegacyStringEnums()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-task-state-string-enum-test-" + Guid.NewGuid().ToString("N"));
        var statePath = Path.Combine(root, "tasks.json");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(
                statePath,
                "{\"version\":3,\"tasks\":[{" +
                "\"name\":\"Legacy pending pack\",\"status\":\"等待下载\",\"progress\":0," +
                "\"taskState\":\"Pending\",\"taskKind\":\"SvlModpack\",\"taskAction\":\"InstallModpack\"," +
                "\"sourceUrl\":\"C:/packs/legacy.zip\"}]}");

            var records = new DownloadTaskStateStore().Load(statePath, out var brokenPath);

            Assert.AreEqual(1, records.Count);
            Assert.IsTrue(string.IsNullOrWhiteSpace(brokenPath));
            Assert.AreEqual(DownloadTaskState.Pending, records[0].TaskState);
            Assert.AreEqual(DownloadTaskKind.SvlModpack, records[0].TaskKind);
            Assert.AreEqual(DownloadTaskAction.InstallModpack, records[0].TaskAction);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void NexusFileIdParser_ShouldReadFileIdFromQueryParameter()
    {
        var parser = typeof(DownloadPageViewModel).GetMethod(
            "TryExtractFileIdFromOption",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(parser);

        var arguments = new object[]
        {
            "https://www.nexusmods.com/stardewvalley/mods/29868?tab=files&file_id=7448774&nmm=1",
            0L
        };

        var parsed = (bool)parser!.Invoke(null, arguments)!;

        Assert.IsTrue(parsed);
        Assert.AreEqual(7448774L, arguments[1]);
    }

    [TestMethod]
    public void DownloadOptionIdentityParser_ShouldReadLegacyFileAndCurseforgeTokens()
    {
        var cases = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["File 7448774_ Content Patcher 2.9.0 2.9.0.zip"] = 7448774,
            ["File%207448774_%20Content%20Patcher.zip"] = 7448774,
            ["https://www.nexusmods.com/stardewvalley/mods/29868?tab=files&file-id=7448774"] = 7448774,
            ["https://example.invalid/cf-1012214-5312529.zip"] = 5312529,
            ["https://edge.forgecdn.net/files/7942/677/MarketTown.zip"] = 7942677,
            ["https://edge.forgecdn.net/files/5357/471/Cape%20Stardew%206.1.7.zip"] = 5357471,
            ["https://api.nexusmods.com/v1/games/stardewvalley/mods/29868/files/7448774.zip"] = 7448774
        };

        foreach (var item in cases)
        {
            Assert.IsTrue(
                DownloadOptionIdentityParser.TryExtractFileId(item.Key, out var fileId),
                item.Key);
            Assert.AreEqual(item.Value, fileId, item.Key);
        }
    }

    [TestMethod]
    public void ModDetailsDownloadOption_ShouldStripGeneratedFilePrefix()
    {
        var parser = typeof(ModDetailsPageViewModel).GetMethod(
            "ParseDownloadOptionItem",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(parser);

        var item = parser!.Invoke(null, [
            "File 7448774_ Content Patcher 2.9.0 2.9.0.zip",
            "",
            false
        ]);
        Assert.IsNotNull(item);
        Assert.AreEqual(
            "Content Patcher 2.9.0 2.9.0.zip",
            item!.GetType().GetProperty("Title")?.GetValue(item));
    }

    [TestMethod]
    public void VersionSettings_ShouldReadLegacyWpfSourceAliases()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-legacy-source-alias-test-" + Guid.NewGuid().ToString("N"));
        var modPath = Path.Combine(root, "ContentPatcher");
        try
        {
            Directory.CreateDirectory(modPath);
            File.WriteAllText(
                Path.Combine(modPath, "svl-source.json"),
                "{\"source\":\"NexusMods\",\"mod_id\":29868,\"file_id\":7448774,\"download_url\":\"nxm://stardewvalley/mods/29868/files/7448774\",\"file_name\":\"Content Patcher.zip\"}");

            var reader = typeof(VersionSettingsPageViewModel).GetMethod(
                "TryReadSourceCredential",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(reader);

            var credential = reader!.Invoke(null, [modPath]);
            Assert.IsNotNull(credential);
            Assert.AreEqual("NexusMods", credential!.GetType().GetProperty("Platform")?.GetValue(credential));
            Assert.AreEqual("29868", credential.GetType().GetProperty("ProjectId")?.GetValue(credential));
            Assert.AreEqual("7448774", credential.GetType().GetProperty("FileId")?.GetValue(credential));
            Assert.AreEqual("Content Patcher.zip", credential.GetType().GetProperty("FileName")?.GetValue(credential));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void TaskStatus_ShouldGiveSourceSpecificAdviceForMissingSourceFailure()
    {
        var task = new DownloadTaskItem
        {
            Name = "Exported Modpack",
            TaskState = DownloadTaskState.Failed,
            Status = "部分完成",
            FailedDetails = "Content Patcher: 缺少可用下载来源（请补充平台、项目 ID/FileID 或直链）",
            CanRetry = true
        };
        var viewModel = new TaskStatusPageViewModel();

        viewModel.SyncTasks([task]);
        viewModel.SetCurrentTask(task);

        Assert.AreEqual("需要补充 Mod 来源", viewModel.AdviceTitle);
        Assert.IsTrue(viewModel.SuggestedActions.Any(action => action.Contains("补充来源", StringComparison.Ordinal)));
        Assert.IsFalse(viewModel.SuggestedActions.Any(action => action.StartsWith("若仍失败", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void TaskStatus_ShouldNotSuggestAnotherTasksRetryReport()
    {
        var taskWithReport = new DownloadTaskItem
        {
            Name = "Task with report",
            TaskState = DownloadTaskState.Failed,
            Status = "下载失败",
            FailedDetails = "网络错误",
            RetryReportPath = "C:\\reports\\task-with-report.json"
        };
        var taskWithoutReport = new DownloadTaskItem
        {
            Name = "Task without report",
            TaskState = DownloadTaskState.Failed,
            Status = "下载失败",
            FailedDetails = "网络错误"
        };
        var viewModel = new TaskStatusPageViewModel();

        viewModel.AddRetryReport(taskWithReport.RetryReportPath);
        viewModel.SyncTasks([taskWithReport, taskWithoutReport]);
        viewModel.SetCurrentTask(taskWithoutReport);

        Assert.IsFalse(viewModel.SuggestedActions.Any(action => action.Contains("重试报告", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void TaskStatus_ShouldRefreshSelectedTaskStateAndCapFailedProgress()
    {
        var task = new DownloadTaskItem
        {
            Name = "State transition",
            TaskState = DownloadTaskState.Downloading,
            Status = "下载中",
            Progress = 42
        };
        var viewModel = new TaskStatusPageViewModel();

        viewModel.SyncTasks([task]);
        viewModel.SetCurrentTask(task);
        Assert.IsFalse(viewModel.IsFailedState);
        Assert.AreEqual(42, viewModel.ProgressPercent);

        task.Progress = 100;
        Assert.AreEqual(100, viewModel.ProgressPercent);

        task.FailedDetails = "Content Patcher: 缺少可用下载来源";
        task.SetState(DownloadTaskState.Failed, "部分完成");

        Assert.IsTrue(viewModel.IsFailedState);
        Assert.IsFalse(viewModel.IsCompletedState);
        Assert.AreEqual(99, viewModel.ProgressPercent);
        Assert.AreEqual("需要补充 Mod 来源", viewModel.AdviceTitle);

        task.SetState(DownloadTaskState.Completed, "已完成");
        Assert.IsFalse(viewModel.IsFailedState);
        Assert.IsTrue(viewModel.IsCompletedState);
        Assert.AreEqual(100, viewModel.ProgressPercent);

        task.InstalledDirectory = "C:\\Game\\versions\\State transition";
        task.ReportPath = "C:\\Game\\reports\\install.json";
        task.RetryReportPath = "C:\\Game\\reports\\retry.json";
        Assert.IsTrue(viewModel.SelectedTaskHasInstalledDirectory);
        Assert.IsTrue(viewModel.SelectedTaskHasReportPath);
        Assert.IsTrue(viewModel.SelectedTaskHasRetryReportPath);
    }

    [TestMethod]
    public void ModManageItem_GroupSelectionShouldCascadeToChildren()
    {
        var parent = new ModManageItem
        {
            DisplayName = "Composite Mod",
            IsCompositeParent = true
        };
        var child = new ModManageItem
        {
            DisplayName = "Content Pack",
            IsChildMod = true
        };

        parent.ChildMods.Add(child);
        parent.IsSelected = true;

        Assert.IsTrue(parent.HasChildren);
        Assert.IsTrue(child.IsSelected);
        Assert.AreEqual("展开", parent.GroupToggleText);

        parent.IsGroupExpanded = true;
        Assert.AreEqual("收起", parent.GroupToggleText);
    }

    [TestMethod]
    public void ModpackSourceParser_ShouldAcceptLegacyAndTopLevelCredentials()
    {
        var parser = typeof(ModpackInstallService).GetMethod(
            "HasActionableDownloadSource",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(parser);

        var samples = new[]
        {
            "{\"name\":\"Content Patcher\",\"source\":{\"source\":\"Nexus\",\"collection\":\"123\",\"file\":\"456\"}}",
            "{\"name\":\"Generic Mod Config Menu\",\"source\":\"CurseForge\",\"projectId\":\"1012214\",\"fileId\":\"5312529\"}",
            "{\"name\":\"Direct Mod\",\"platform\":\"CurseForge\",\"projectId\":1012214,\"fileId\":5312529}"
        };

        foreach (var json in samples)
        {
            using var document = JsonDocument.Parse(json);
            var result = (bool)parser.Invoke(null, [document.RootElement]);
            Assert.IsTrue(result, json);
        }
    }

    [TestMethod]
    public void ModpackSourceParser_ShouldRejectIncompleteOrUnknownIdOnlySource()
    {
        var parser = typeof(ModpackInstallService).GetMethod(
            "HasActionableDownloadSource",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(parser);

        using var nexusFileOnly = JsonDocument.Parse(
            "{\"name\":\"Content Patcher\",\"platform\":\"NexusMods\",\"fileId\":7448774}");
        using var unknownIds = JsonDocument.Parse(
            "{\"name\":\"Content Patcher\",\"platform\":\"Unknown\",\"projectId\":29868,\"fileId\":7448774}");
        using var zeroStandardIds = JsonDocument.Parse(
            "{\"name\":\"Generic Mod Config Menu\",\"platform\":\"Curseforge\",\"projectId\":0,\"project\":1012214,\"fileId\":\"0\",\"file\":5312529}");

        Assert.IsFalse((bool)parser!.Invoke(null, [nexusFileOnly.RootElement])!);
        Assert.IsFalse((bool)parser.Invoke(null, [unknownIds.RootElement])!);
        Assert.IsTrue((bool)parser.Invoke(null, [zeroStandardIds.RootElement])!);
    }

    [TestMethod]
    public void ModpackSourceParser_ShouldRejectCurseforgePageWithoutProjectId()
    {
        var parser = typeof(ModpackInstallService).GetMethod(
            "HasActionableDownloadSource",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(parser);

        using var pageOnly = JsonDocument.Parse(
            "{\"name\":\"Content Patcher\",\"platform\":\"Curseforge\",\"downloadUrl\":\"https://www.curseforge.com/stardew-valley/mc-mods/content-patcher/files/5312529\"}");
        using var pageWithProject = JsonDocument.Parse(
            "{\"name\":\"Content Patcher\",\"platform\":\"Curseforge\",\"projectId\":1012214,\"downloadUrl\":\"https://www.curseforge.com/stardew-valley/mc-mods/content-patcher/files/5312529\"}");

        Assert.IsFalse((bool)parser!.Invoke(null, [pageOnly.RootElement])!);
        Assert.IsTrue((bool)parser.Invoke(null, [pageWithProject.RootElement])!);
    }

    [TestMethod]
    public void SourcePlatformInference_ShouldRecognizeCurseforgeWebAndCdnUrls()
    {
        var modpackParser = typeof(ModpackInstallService).GetMethod(
            "IsLikelyCurseforgeUrl",
            BindingFlags.Static | BindingFlags.NonPublic);
        var collectionParser = typeof(CollectionInstallService).GetMethod(
            "IsLikelyCurseforgeUrl",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(modpackParser);
        Assert.IsNotNull(collectionParser);

        var urls = new[]
        {
            "https://www.curseforge.com/stardewvalley/mods/content-patcher",
            "https://edge.forgecdn.net/files/5312/529/ContentPatcher.zip",
            "https://api.curse.tools/v1/cf/mods/1012214/files/5312529"
        };

        foreach (var url in urls)
        {
            Assert.IsTrue((bool)modpackParser.Invoke(null, [url])!, url);
            Assert.IsTrue((bool)collectionParser.Invoke(null, [url])!, url);
        }

        Assert.IsFalse((bool)modpackParser.Invoke(null, ["https://example.com/mod.zip"])!);
        Assert.IsFalse((bool)collectionParser.Invoke(null, ["https://example.com/mod.zip"])!);
    }

    [TestMethod]
    public void CurseforgeUrlParser_ShouldRecoverProjectAndFileIdsFromLegacyUrls()
    {
        var parser = typeof(ModpackInstallService).GetMethod(
            "TryParseCurseforgeIdsFromUrl",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(parser);

        var apiArguments = new object[]
        {
            "https://api.curse.tools/v1/cf/mods/1012214/files/5312529",
            0L,
            0L
        };
        Assert.IsTrue((bool)parser!.Invoke(null, apiArguments)!);
        Assert.AreEqual(1012214L, apiArguments[1]);
        Assert.AreEqual(5312529L, apiArguments[2]);

        var cdnArguments = new object[]
        {
            "https://edge.forgecdn.net/files/5312/529/ContentPatcher.zip",
            0L,
            0L
        };
        Assert.IsTrue((bool)parser.Invoke(null, cdnArguments)!);
        Assert.AreEqual(0L, cdnArguments[1]);
        Assert.AreEqual(5312529L, cdnArguments[2]);

        var queryArguments = new object[]
        {
            "https://api.curse.tools/v1/file?projectId=1012214&fileId=5312529",
            0L,
            0L
        };
        Assert.IsTrue((bool)parser.Invoke(null, queryArguments)!);
        Assert.AreEqual(1012214L, queryArguments[1]);
        Assert.AreEqual(5312529L, queryArguments[2]);

        var identityParser = typeof(VersionSettingsPageViewModel).GetMethod(
            "InferExportSourceIdentity",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(identityParser);
        var identityArguments = new object[]
        {
            "",
            "",
            "",
            new[] { "https://api.curse.tools/v1/cf/mods/1012214/files/5312529" }
        };
        identityParser!.Invoke(null, identityArguments);
        Assert.AreEqual("Curseforge", identityArguments[0]);
        Assert.AreEqual("1012214", identityArguments[1]);
        Assert.AreEqual("5312529", identityArguments[2]);

        var actionableParser = typeof(ModpackInstallService).GetMethod(
            "HasActionableDownloadSource",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(actionableParser);
        using var apiSource = JsonDocument.Parse(
            "{\"name\":\"Content Patcher\",\"source\":{\"url\":\"https://api.curse.tools/v1/cf/mods/1012214/files/5312529\"}}");
        Assert.IsTrue((bool)actionableParser!.Invoke(null, [apiSource.RootElement])!);
    }

    [TestMethod]
    public void CurseforgeSourceParser_ShouldNotTreatProjectPageAsArchiveUrl()
    {
        var parser = typeof(ModpackInstallService).GetMethod(
            "IsLikelyDirectDownloadUrl",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(parser);

        Assert.IsFalse((bool)parser.Invoke(null, [
            "https://www.curseforge.com/stardewvalley/mods/content-patcher"
        ])!);
        Assert.IsFalse((bool)parser.Invoke(null, [
            "https://www.curseforge.com/stardewvalley/mods/content-patcher/files/5312529/download"
        ])!);
        Assert.IsTrue((bool)parser.Invoke(null, [
            "https://edge.forgecdn.net/files/5312/529/ContentPatcher.zip"
        ])!);
    }

    [TestMethod]
    public void BatchUpdateSourceParser_ShouldResolveCurseforgePagesInsteadOfDownloadingHtml()
    {
        var parser = typeof(DownloadPageViewModel).GetMethod(
            "IsLikelyCurseforgeDirectDownloadUrl",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(parser);

        Assert.IsFalse((bool)parser.Invoke(null, [
            "https://www.curseforge.com/stardewvalley/mods/content-patcher/files/5312529/download"
        ])!);
        Assert.IsFalse((bool)parser.Invoke(null, [
            "https://api.curse.tools/v1/mods/1012214/files/5312529"
        ])!);
        Assert.IsFalse((bool)parser.Invoke(null, [
            "https://api.curse.tools/v1/mods/1012214/files/5312529.zip"
        ])!, "API 路径即使带 .zip 也仍然是 JSON 接口，不应作为归档直链");
        Assert.IsFalse((bool)parser.Invoke(null, [
            "https://www.curseforge.com/stardewvalley/mods/content-patcher/download.zip"
        ])!, "CurseForge 页面即使带归档后缀也不应绕过页面过滤");
        Assert.IsTrue((bool)parser.Invoke(null, [
            "https://edge.forgecdn.net/files/5312/529/ContentPatcher.zip"
        ])!);
        Assert.IsTrue((bool)parser.Invoke(null, [
            "https://downloads.example.com/mods/ContentPatcher.7z"
        ])!);
    }

    [TestMethod]
    public void NexusSourceParser_ShouldAllowCdnAndRejectNexusPagesAsDirectUrls()
    {
        var parser = typeof(DownloadPageViewModel).GetMethod(
            "IsLikelyNexusDirectDownloadUrl",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(parser);

        Assert.IsTrue((bool)parser.Invoke(null, [
            new Uri("https://file-metadata.nexusmods.com/123/456?fid=7448774&source=download")
        ])!);
        Assert.IsTrue((bool)parser.Invoke(null, [
            new Uri("https://downloads.example.com/mods/ContentPatcher.7z")
        ])!);
        Assert.IsFalse((bool)parser.Invoke(null, [
            new Uri("https://www.nexusmods.com/stardewvalley/mods/29868?tab=files&file_id=7448774&nmm=1")
        ])!);
        Assert.IsFalse((bool)parser.Invoke(null, [
            new Uri("https://api.nexusmods.com/v1/games/stardewvalley/mods/29868/files/7448774.zip")
        ])!);
    }

    [TestMethod]
    public void CurseforgeRemoteResolver_ShouldRejectPageAndApiUrls()
    {
        var parser = typeof(RemoteCatalogService).GetMethod(
            "IsLikelyCurseforgeDirectDownloadUrl",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(parser);

        Assert.IsFalse((bool)parser.Invoke(null, [
            "https://www.curseforge.com/stardewvalley/mods/content-patcher/files/5312529/download"
        ])!);
        Assert.IsFalse((bool)parser.Invoke(null, [
            "https://api.curse.tools/v1/mods/1012214/files/5312529"
        ])!);
        Assert.IsTrue((bool)parser.Invoke(null, [
            "https://edge.forgecdn.net/files/5312/529/ContentPatcher.zip"
        ])!);
    }

    [TestMethod]
    public async Task CurseforgeRemoteResolver_ShouldPropagateCancellation()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "svl-curseforge-cancellation-test-" + Guid.NewGuid().ToString("N"));

        try
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            var service = new RemoteCatalogService(
                new AppUserSettingsStore(Path.Combine(root, "settings")));

            await Assert.ThrowsExceptionAsync<TaskCanceledException>(() =>
                service.ResolveCurseforgeFileDownloadUrlAsync(
                    1012214,
                    5312529,
                    cancellationToken: cancellation.Token));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void CurseforgeRemoteResolver_ShouldReadNestedScalarAndCaseInsensitiveDownloadUrl()
    {
        var parser = typeof(RemoteCatalogService).GetMethod(
            "FindDownloadUrlInElement",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(parser);

        using var scalarDocument = JsonDocument.Parse(
            "{\"data\":\"https://edge.forgecdn.net/files/5312/529/ContentPatcher.zip\"}");
        var scalarResult = parser!.Invoke(null, [scalarDocument.RootElement, 5312529L, true]) as string;
        Assert.AreEqual(
            "https://edge.forgecdn.net/files/5312/529/ContentPatcher.zip",
            scalarResult);

        using var objectDocument = JsonDocument.Parse(
            "{\"DATA\":{\"DOWNLOAD_URL\":\"https://edge.forgecdn.net/files/5312/529/ContentPatcher.zip\"}}");
        var objectResult = parser.Invoke(null, [objectDocument.RootElement, 5312529L, true]) as string;
        Assert.AreEqual(
            "https://edge.forgecdn.net/files/5312/529/ContentPatcher.zip",
            objectResult);
    }

    [TestMethod]
    public void CurseforgeRemotePayload_ShouldAcceptNestedWrappersAndStringIds()
    {
        var arrayParser = typeof(RemoteCatalogService).GetMethod(
            "TryGetArrayPayload",
            BindingFlags.Static | BindingFlags.NonPublic);
        var objectParser = typeof(RemoteCatalogService).GetMethod(
            "TryGetObjectPayload",
            BindingFlags.Static | BindingFlags.NonPublic);
        var longParser = typeof(RemoteCatalogService).GetMethod(
            "TryGetLong",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(arrayParser);
        Assert.IsNotNull(objectParser);
        Assert.IsNotNull(longParser);

        using var arrayDocument = JsonDocument.Parse(
            "{\"RESULT\":{\"FILES\":[{\"id\":\"5312529\"}]}}");
        var arrayArguments = new object[]
        {
            arrayDocument.RootElement,
            null,
            new[] { "data", "files", "results", "items" }
        };
        Assert.IsTrue((bool)arrayParser!.Invoke(null, arrayArguments)!);
        var payload = (JsonElement)arrayArguments[1]!;
        Assert.AreEqual(JsonValueKind.Array, payload.ValueKind);

        var longArguments = new object[] { payload[0], "id" };
        Assert.AreEqual(5312529L, (long)longParser!.Invoke(null, longArguments)!);

        using var objectDocument = JsonDocument.Parse(
            "{\"result\":{\"name\":\"Content Patcher\"}}");
        var objectArguments = new object[] { objectDocument.RootElement, null };
        Assert.IsTrue((bool)objectParser!.Invoke(null, objectArguments)!);
        var objectPayload = (JsonElement)objectArguments[1]!;
        Assert.AreEqual("Content Patcher", objectPayload.GetProperty("name").GetString());
    }

    [TestMethod]
    public void ExportSourceFileId_ShouldRecoverFromLegacyLocalNames()
    {
        var parser = typeof(VersionSettingsPageViewModel).GetMethod(
            "TryExtractSourceFileIdFromLocalMetadata",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(parser);

        var nexusFileId = parser!.Invoke(null, [
            "NexusMods",
            "29868",
            new string[]
            {
                "File 7448774_ Content Patcher 2.9.0 2.9.0.zip",
                null,
                "https://www.nexusmods.com/stardewvalley/mods/29868?tab=files&nmm=1",
                "Content Patcher"
            }
        ]) as string;
        Assert.AreEqual("7448774", nexusFileId);

        var curseforgeFileId = parser.Invoke(null, [
            "Curseforge",
            "1012214",
            new string[]
            {
                "cf-1012214-5312529.zip",
                "Content Patcher",
                null,
                null
            }
        ]) as string;
        Assert.AreEqual("5312529", curseforgeFileId);
    }

    [TestMethod]
    public void CurseforgeSourceParser_ShouldResolveIdsBeforeDirectDownload()
    {
        var parser = typeof(ModpackInstallService).GetMethod(
            "CanUseDirectDownloadUrl",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(parser);

        Assert.IsFalse((bool)parser.Invoke(null, [
            "Curseforge",
            "https://www.curseforge.com/stardewvalley/mods/content-patcher/files/5312529/download"
        ])!);
        Assert.IsTrue((bool)parser.Invoke(null, [
            "Curseforge",
            "https://edge.forgecdn.net/files/5312/529/ContentPatcher.zip"
        ])!);
        Assert.IsTrue((bool)parser.Invoke(null, [
            "NexusMods",
            "https://downloads.example.com/mods/content-patcher/download"
        ])!);
    }

    [TestMethod]
    public void CollectionSourceParser_ShouldNotTreatCurseforgePageAsArchiveUrl()
    {
        var parser = typeof(CollectionInstallService).GetMethod(
            "IsLikelyDirectDownloadUrl",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(parser);

        Assert.IsFalse((bool)parser.Invoke(null, [
            "https://www.curseforge.com/stardewvalley/mods/content-patcher/files/5312529/download"
        ])!);
        Assert.IsTrue((bool)parser.Invoke(null, [
            "https://edge.forgecdn.net/files/5312/529/ContentPatcher.zip"
        ])!);
        Assert.IsTrue((bool)parser.Invoke(null, [
            "https://downloads.example.com/mods/content-patcher/download"
        ])!);
    }

    [TestMethod]
    public void CollectionSourceParser_ShouldExtractNexusPageFileIds()
    {
        var parser = typeof(CollectionInstallService).GetMethod(
            "TryParseNexusPageIds",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(parser);

        var arguments = new object[]
        {
            "https://www.nexusmods.com/stardewvalley/mods/29868?tab=files&file_id=7448774",
            0L,
            0L
        };

        var parsed = (bool)parser.Invoke(null, arguments);
        Assert.IsTrue(parsed);
        Assert.AreEqual(29868L, arguments[1]);
        Assert.AreEqual(7448774L, arguments[2]);
    }

    [TestMethod]
    public void NexusSourceParser_ShouldRejectLookalikeHost()
    {
        var parser = typeof(ModpackInstallService).GetMethod(
            "TryParseNexusPageIds",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(parser);

        var arguments = new object[]
        {
            "https://notnexusmods.com/stardewvalley/mods/29868?file_id=7448774",
            0L,
            0L
        };

        var parsed = (bool)parser.Invoke(null, arguments);
        Assert.IsFalse(parsed);
    }

    [TestMethod]
    public void NexusSourceParser_ShouldReadHyphenatedAndEncodedQueryKeys()
    {
        var parser = typeof(ModpackInstallService).GetMethod(
            "TryParseNexusPageIds",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(parser);

        var arguments = new object[]
        {
            "https://www.nexusmods.com/stardewvalley/mods/29868?mod%2Did=29868&file%2Did=7448774",
            0L,
            0L
        };

        var parsed = (bool)parser!.Invoke(null, arguments)!;
        Assert.IsTrue(parsed);
        Assert.AreEqual(29868L, arguments[1]);
        Assert.AreEqual(7448774L, arguments[2]);
    }

    [TestMethod]
    public void NexusSourceParser_ShouldExtractModIdWithoutFileId()
    {
        var parserType = typeof(ModpackInstallService).Assembly.GetType(
            "SVL.Avalonia.Services.NexusSourceParser");
        var parser = parserType?.GetMethod(
            "TryParseModId",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.IsNotNull(parser);

        var arguments = new object[]
        {
            "https://www.nexusmods.com/stardewvalley/mods/29868?tab=files&nmm=1",
            0L
        };

        var parsed = (bool)parser.Invoke(null, arguments);
        Assert.IsTrue(parsed);
        Assert.AreEqual(29868L, arguments[1]);
    }

    [TestMethod]
    public void NexusCollectionRevisionMetadata_ShouldBePreservedInDownloadOption()
    {
        var builder = typeof(RemoteCatalogService).GetMethod(
            "BuildNexusCollectionOptionMetadata",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(builder);

        var metadata = (string)builder.Invoke(null, [
            "最新",
            "12 MB",
            "1,024",
            "2026-09-06",
            18L,
            7L
        ]);

        StringAssert.Contains(metadata, "revision=7");
        StringAssert.Contains(metadata, "channel=最新");
        StringAssert.Contains(metadata, "mods=18");
    }

    [TestMethod]
    public void ModpackCachedInstall_ShouldWriteSourceCredentialForExport()
    {
        var modsPath = Path.Combine(Path.GetTempPath(), "svl-source-write-test-" + Guid.NewGuid().ToString("N"));
        var modPath = Path.Combine(modsPath, "Content Patcher");
        var writer = typeof(ModpackInstallService).GetMethod(
            "WriteModpackSourceCredentials",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.IsNotNull(writer);

        try
        {
            Directory.CreateDirectory(modPath);
            writer.Invoke(null, [
                modsPath,
                new List<string> { "Content Patcher" },
                "NexusMods",
                123L,
                456L,
                null,
                null
            ]);

            var credentialPath = Path.Combine(modPath, "svl-source.json");
            Assert.IsTrue(File.Exists(credentialPath));
            using var document = JsonDocument.Parse(File.ReadAllText(credentialPath));
            Assert.AreEqual("NexusMods", document.RootElement.GetProperty("platform").GetString());
            Assert.AreEqual("123", document.RootElement.GetProperty("projectId").GetString());
            Assert.AreEqual("456", document.RootElement.GetProperty("fileId").GetString());
        }
        finally
        {
            if (Directory.Exists(modsPath))
            {
                Directory.Delete(modsPath, true);
            }
        }
    }

    [TestMethod]
    public void ModpackSourceCredentialWrite_ShouldClearNestedModUpdateSnapshots()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "svl-source-update-snapshot-test-" + Guid.NewGuid().ToString("N"));
        var parentPath = Path.Combine(root, "MarketTown");
        var childPath = Path.Combine(parentPath, "CloneNPC_RSV");
        var writer = typeof(ModpackInstallService).GetMethod(
            "WriteModpackSourceCredentials",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.IsNotNull(writer);

        try
        {
            Directory.CreateDirectory(childPath);
            File.WriteAllText(
                Path.Combine(parentPath, "svl-source.json"),
                "{\"platform\":\"Curseforge\",\"projectId\":\"1012214\",\"hasUpdate\":true,\"latestVersion\":\"6.7.1\",\"updateStatus\":\"可更新 -> 6.7.1\",\"updateFileId\":\"5312529\"}");
            File.WriteAllText(
                Path.Combine(childPath, "svl-source.json"),
                "{\"platform\":\"Curseforge\",\"projectId\":\"1012214\",\"hasUpdate\":true,\"latestVersion\":\"6.7.1\",\"updateStatus\":\"可更新 -> 6.7.1\",\"updateFileId\":\"5312529\"}");

            writer!.Invoke(null, [
                root,
                new List<string> { "MarketTown" },
                "Curseforge",
                1012214L,
                5312529L,
                null,
                null
            ]);

            foreach (var path in new[] {
                         Path.Combine(parentPath, "svl-source.json"),
                         Path.Combine(childPath, "svl-source.json")
                     })
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                Assert.IsFalse(document.RootElement.GetProperty("hasUpdate").GetBoolean());
                Assert.AreEqual(string.Empty, document.RootElement.GetProperty("latestVersion").GetString());
                Assert.AreEqual("未检查", document.RootElement.GetProperty("updateStatus").GetString());
                Assert.AreEqual(string.Empty, document.RootElement.GetProperty("updateFileId").GetString());
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void NexusDownloadCache_ShouldPersistAndReadByModAndFileId()
    {
        var modId = 900_000_000L + Random.Shared.Next(1_000_000);
        var fileId = 900_000_000L + Random.Shared.Next(1_000_000);
        var sourceRoot = Path.Combine(Path.GetTempPath(), "svl-nexus-cache-test-" + Guid.NewGuid().ToString("N"));
        var sourcePath = Path.Combine(sourceRoot, "source.zip");
        var cachePath = NexusDownloadCache.GetCachePath(modId, fileId);

        try
        {
            Directory.CreateDirectory(sourceRoot);
            using (var archive = ZipFile.Open(sourcePath, ZipArchiveMode.Create))
            {
                using var writer = new StreamWriter(archive.CreateEntry("content.txt").Open());
                writer.Write("valid nexus cache");
            }

            NexusDownloadCache.Save(modId, fileId, sourcePath);

            Assert.IsTrue(NexusDownloadCache.TryGet(modId, fileId, out var resolvedPath));
            Assert.AreEqual(cachePath, resolvedPath);
            Assert.AreEqual(new FileInfo(sourcePath).Length, new FileInfo(resolvedPath).Length);
        }
        finally
        {
            if (File.Exists(cachePath))
            {
                File.Delete(cachePath);
            }

            if (Directory.Exists(sourceRoot))
            {
                Directory.Delete(sourceRoot, true);
            }
        }
    }

    [TestMethod]
    public void NexusCollectionDownloadCache_ShouldPersistBySlugAndRevision()
    {
        var slug = "cache-test-" + Guid.NewGuid().ToString("N");
        const int revision = 7;
        var sourceRoot = Path.Combine(
            Path.GetTempPath(),
            "svl-nexus-collection-cache-test-" + Guid.NewGuid().ToString("N"));
        var sourcePath = Path.Combine(sourceRoot, "collection.7z");
        var cachePath = NexusCollectionDownloadCache.GetCachePath(
            "stardewvalley", slug, revision);

        try
        {
            Directory.CreateDirectory(sourceRoot);
            File.WriteAllBytes(sourcePath, CreateZipBytes(
                ("collection.json", "{\"info\":{\"name\":\"Cache Collection\"},\"mods\":[]}")));

            NexusCollectionDownloadCache.Save(
                "stardewvalley",
                slug,
                revision,
                sourcePath,
                IsZipArchive);

            Assert.IsTrue(NexusCollectionDownloadCache.TryGet(
                "STARDEWVALLEY",
                slug.ToUpperInvariant(),
                revision,
                out var resolvedPath,
                IsZipArchive));
            Assert.AreEqual(cachePath, resolvedPath);
            Assert.AreEqual(new FileInfo(sourcePath).Length, new FileInfo(resolvedPath).Length);
            Assert.AreNotEqual(
                cachePath,
                NexusCollectionDownloadCache.GetCachePath("stardewvalley", slug, revision + 1));
        }
        finally
        {
            if (File.Exists(cachePath))
            {
                File.Delete(cachePath);
            }

            if (Directory.Exists(sourceRoot))
            {
                Directory.Delete(sourceRoot, true);
            }
        }
    }

    [TestMethod]
    public void ArchiveDetector_ShouldPreferSignatureOverSevenZipExtension()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "svl-archive-signature-test-" + Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(root, "collection.7z");

        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllBytes(archivePath, CreateZipBytes(
                ("collection.json", "{\"info\":{\"name\":\"Zip Named SevenZip\"},\"mods\":[]}")));

            Assert.IsFalse(SVL.Core.Platform.IO.ArchiveExtractor.IsSevenZip(archivePath));
            Assert.IsTrue(SVL.Core.Platform.IO.ArchiveExtractor.IsZip(archivePath));

            var detection = ModpackTypeDetector.Detect(archivePath);
            try
            {
                Assert.AreEqual(ModpackType.NexusCollection, detection.Type);
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(detection.TempExtractPath))
                {
                    ModpackTypeDetector.CleanupTempDirectory(detection.TempExtractPath);
                }
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public async Task CollectionInstall_ShouldReuseStableCacheWithoutNexusCredentials()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "svl-collection-stable-cache-e2e-" + Guid.NewGuid().ToString("N"));
        var gamePath = Path.Combine(root, "Game");
        var settingsPath = Path.Combine(root, "settings");
        var instanceName = "Cached Collection " + Guid.NewGuid().ToString("N")[..8];
        var slug = "stable-cache-" + Guid.NewGuid().ToString("N");
        const int revision = 19;
        var cachePath = NexusCollectionDownloadCache.GetCachePath(
            "stardewvalley", slug, revision);
        var registry = new InstanceRegistryStore();

        try
        {
            // 给安装器一个可离线复用的 SMAPI 实例；测试重点是 Collection
            // 本体命中稳定缓存后，不应再读取 Nexus 凭据或请求解析地址。
            Directory.CreateDirectory(Path.Combine(
                gamePath, "versions", "Existing SMAPI 4.5.2"));
            File.WriteAllText(
                Path.Combine(gamePath, "versions", "Existing SMAPI 4.5.2", "StardewModdingAPI.dll"),
                "existing-smapi");

            var sourceRoot = Path.Combine(root, "source");
            Directory.CreateDirectory(sourceRoot);
            var sourceArchive = Path.Combine(sourceRoot, "collection.7z");
            File.WriteAllBytes(sourceArchive, CreateZipBytes(
                ("Export/collection.json",
                    "{\"info\":{\"name\":\"Stable Cached Collection\"},\"mods\":[]}")));

            NexusCollectionDownloadCache.Save(
                "stardewvalley",
                slug,
                revision,
                sourceArchive,
                IsCollectionArchive);

            var settingsStore = new AppUserSettingsStore(settingsPath);
            // 故意不写入 NexusApiKey/OAuthAccessToken。
            var nxmParser = new SVL.Core.Platform.Abstractions.NxmLinkParser();
            var service = new CollectionInstallService(
                new FixedGameInstallPathLocator(gamePath),
                new TestSmapiInstallService(),
                new HttpDownloadService(settingsStore),
                new RemoteCatalogService(settingsStore),
                settingsStore,
                new NexusModDownloadResolverService(),
                nxmParser,
                new BrowserDownloadFallbackService(
                    nxmParser,
                    new SVL.Core.Platform.Services.ExternalProcessService()),
                new ModpackInstallService(
                    new FixedGameInstallPathLocator(gamePath),
                    new TestSmapiInstallService(),
                    new HttpDownloadService(settingsStore),
                    new RemoteCatalogService(settingsStore),
                    settingsStore,
                    new NexusModDownloadResolverService(),
                    nxmParser));

            var result = await service.InstallCollectionAsync(
                slug,
                revision,
                instanceName,
                onProgress: null,
                gameBasePath: gamePath);

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual(0, result.FailedMods.Count);
            Assert.IsTrue(File.Exists(Path.Combine(
                gamePath, "versions", instanceName, "StardewModdingAPI.dll")));
        }
        finally
        {
            try
            {
                if (File.Exists(cachePath))
                {
                    File.Delete(cachePath);
                }
            }
            catch { }

            var records = registry.LoadManualInstances();
            records.RemoveAll(record => string.Equals(
                record.Name, instanceName, StringComparison.OrdinalIgnoreCase));
            registry.SaveManualInstances(records);

            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public async Task CollectionInstall_ShouldPreferExactSmapiFileCache()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "svl-collection-smapi-file-cache-e2e-" + Guid.NewGuid().ToString("N"));
        var gamePath = Path.Combine(root, "Game");
        var archivePath = Path.Combine(root, "collection.zip");
        var smapiSourcePath = Path.Combine(root, "smapi-source.zip");
        var settingsPath = Path.Combine(root, "settings");
        var instanceName = "Exact SMAPI Collection " + Guid.NewGuid().ToString("N")[..8];
        var fileId = 920_000_000L + Random.Shared.Next(1_000_000);
        var cachePath = NexusDownloadCache.GetCachePath(SmapiDownloadService.SmapiModId, fileId);
        var previousCache = File.Exists(cachePath) ? File.ReadAllBytes(cachePath) : null;
        var registry = new InstanceRegistryStore();

        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllBytes(
                smapiSourcePath,
                CreateZipBytes((
                    "SMAPI 4.5.2 installer/internal/windows/install.dat",
                    "smapi")));
            NexusDownloadCache.Save(
                SmapiDownloadService.SmapiModId,
                fileId,
                smapiSourcePath,
                IsSmapiArchivePackage);

            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                WriteArchiveText(
                    archive,
                    "collection.json",
                    $"{{\"info\":{{\"name\":\"Exact SMAPI Collection\"}},\"mods\":[{{" +
                    $"\"name\":\"SMAPI 4.5.2\",\"version\":\"4.5.2\",\"source\":{{\"type\":\"nexus\",\"modId\":2400,\"fileId\":{fileId}}}}}]}}");
            }

            var settingsStore = new AppUserSettingsStore(settingsPath);
            var nxmParser = new SVL.Core.Platform.Abstractions.NxmLinkParser();
            var service = new CollectionInstallService(
                new FixedGameInstallPathLocator(gamePath),
                new TestSmapiInstallService(),
                new HttpDownloadService(settingsStore),
                new RemoteCatalogService(settingsStore),
                settingsStore,
                new NexusModDownloadResolverService(),
                nxmParser,
                new BrowserDownloadFallbackService(
                    nxmParser,
                    new SVL.Core.Platform.Services.ExternalProcessService()),
                new ModpackInstallService(
                    new FixedGameInstallPathLocator(gamePath),
                    new TestSmapiInstallService(),
                    new HttpDownloadService(settingsStore),
                    new RemoteCatalogService(settingsStore),
                    settingsStore,
                    new NexusModDownloadResolverService(),
                    nxmParser));

            var progress = new List<string>();
            var result = await service.InstallCollectionFromArchiveAsync(
                archivePath,
                instanceName,
                onProgress: item => progress.Add(item.StepText),
                gameBasePath: gamePath);

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual(0, result.FailedMods.Count);
            Assert.IsTrue(File.Exists(Path.Combine(
                gamePath, "versions", instanceName, "StardewModdingAPI.dll")));
            CollectionAssert.Contains(
                progress,
                $"复用 Collection 清单指定的 Nexus SMAPI 缓存（FileID: {fileId}）");
        }
        finally
        {
            try
            {
                if (File.Exists(cachePath))
                {
                    File.Delete(cachePath);
                }

                if (previousCache != null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                    File.WriteAllBytes(cachePath, previousCache);
                }
            }
            catch { }

            var records = registry.LoadManualInstances();
            records.RemoveAll(record => string.Equals(
                record.Name, instanceName, StringComparison.OrdinalIgnoreCase));
            registry.SaveManualInstances(records);

            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void NexusDownloadCache_ShouldPromoteLegacyWpfCacheOnHit()
    {
        var modId = 910_000_000L + Random.Shared.Next(1_000_000);
        var fileId = 910_000_000L + Random.Shared.Next(1_000_000);
        var sourceRoot = Path.Combine(Path.GetTempPath(), "svl-legacy-nexus-cache-test-" + Guid.NewGuid().ToString("N"));
        var sourcePath = Path.Combine(sourceRoot, "source.zip");
        var cachePath = NexusDownloadCache.GetCachePath(modId, fileId);
        var legacyCachePath = NexusDownloadCache.GetLegacyCachePath(modId, fileId);

        try
        {
            Directory.CreateDirectory(sourceRoot);
            using (var archive = ZipFile.Open(sourcePath, ZipArchiveMode.Create))
            {
                using var writer = new StreamWriter(archive.CreateEntry("content.txt").Open());
                writer.Write("legacy nexus cache");
            }

            Directory.CreateDirectory(NexusDownloadCache.LegacyRoot);
            File.Copy(sourcePath, legacyCachePath, overwrite: true);

            var found = NexusDownloadCache.TryGet(
                modId,
                fileId,
                out var resolvedPath,
                IsZipArchive);

            Assert.IsTrue(found);
            Assert.AreEqual(cachePath, resolvedPath);
            Assert.IsTrue(File.Exists(cachePath));
            Assert.AreEqual(new FileInfo(sourcePath).Length, new FileInfo(cachePath).Length);
        }
        finally
        {
            if (File.Exists(cachePath))
            {
                File.Delete(cachePath);
            }

            if (File.Exists(legacyCachePath))
            {
                File.Delete(legacyCachePath);
            }

            if (Directory.Exists(sourceRoot))
            {
                Directory.Delete(sourceRoot, true);
            }
        }
    }

    [TestMethod]
    public void CollectionCacheSource_ShouldReadLegacyWpfFileId()
    {
        var parser = typeof(CollectionInstallService).GetMethod(
            "TryGetNexusFileIdFromCachePath",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(parser);

        var arguments = new object[]
        {
            NexusDownloadCache.GetLegacyCachePath(29868, 7448774),
            29868L,
            0L
        };

        Assert.IsTrue((bool)parser!.Invoke(null, arguments)!);
        Assert.AreEqual(7448774L, arguments[2]);
    }

    [TestMethod]
    public void NexusDownloadCache_ShouldTreatInvalidArchiveAsMissWhenValidated()
    {
        var modId = 900_000_000L + Random.Shared.Next(1_000_000);
        var fileId = 900_000_000L + Random.Shared.Next(1_000_000);
        var cachePath = NexusDownloadCache.GetCachePath(modId, fileId);

        try
        {
            Directory.CreateDirectory(NexusDownloadCache.Root);
            // 非空但不完整的浏览器/网络半成品不能阻断后续重新下载。
            File.WriteAllBytes(cachePath, [1, 2, 3, 4]);

            var found = NexusDownloadCache.TryGet(
                modId,
                fileId,
                out _,
                path =>
                {
                    using var stream = File.OpenRead(path);
                    using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
                    return archive.Entries.Count > 0;
                });

            Assert.IsFalse(found);
        }
        finally
        {
            if (File.Exists(cachePath))
            {
                File.Delete(cachePath);
            }
        }
    }

    [TestMethod]
    public void NexusDownloadCache_ShouldNotPersistFileRejectedByValidator()
    {
        var modId = 900_000_000L + Random.Shared.Next(1_000_000);
        var fileId = 900_000_000L + Random.Shared.Next(1_000_000);
        var sourceRoot = Path.Combine(Path.GetTempPath(), "svl-nexus-cache-validator-" + Guid.NewGuid().ToString("N"));
        var sourcePath = Path.Combine(sourceRoot, "response.zip");
        var cachePath = NexusDownloadCache.GetCachePath(modId, fileId);

        try
        {
            Directory.CreateDirectory(sourceRoot);
            // 模拟 HTTP 200 但返回 HTML/登录页的错误响应：文件非空，
            // 但归档校验器必须拒绝，不能让下一次下载误以为缓存有效。
            File.WriteAllText(sourcePath, "<html>login required</html>");
            NexusDownloadCache.Save(modId, fileId, sourcePath, _ => false);

            Assert.IsFalse(File.Exists(cachePath));
            Assert.IsFalse(NexusDownloadCache.TryGet(modId, fileId, out _));
        }
        finally
        {
            if (File.Exists(cachePath))
            {
                File.Delete(cachePath);
            }

            if (Directory.Exists(sourceRoot))
            {
                Directory.Delete(sourceRoot, true);
            }
        }
    }

    [TestMethod]
    public void InstanceIcons_ShouldPreferSmapiAndCustomIconsOverVanilla()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-instance-icon-test-" + Guid.NewGuid().ToString("N"));
        var vanillaIcon = Path.Combine(root, ".svl-instance-icon.png");
        var smapiIcon = Path.Combine(root, ".svl-instance-icon-smapi.png");

        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(vanillaIcon, "vanilla icon");

            Assert.AreEqual(vanillaIcon, InstanceIconResolver.ResolveIconPath(root, isSmapiInstance: false));
            // Base 路径上的通用文件代表用户在版本设置中选择的自定义图标，
            // SMAPI 变体也必须继承它；只有内置 Vanilla.png 占位才会被过滤。
            Assert.AreEqual(vanillaIcon, InstanceIconResolver.ResolveIconPath(root, isSmapiInstance: true));

            File.WriteAllText(smapiIcon, "smapi icon");
            Assert.AreEqual(smapiIcon, InstanceIconResolver.ResolveIconPath(root, isSmapiInstance: true));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void InstanceIcons_ShouldPreferCustomIconOverGeneratedSmapiPreset()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-instance-icon-priority-test-" + Guid.NewGuid().ToString("N"));
        var customIcon = Path.Combine(root, ".svl-instance-icon.png");
        var generatedSmapiIcon = Path.Combine(root, ".svl-instance-icon-smapi.png");
        var generatedMarker = Path.Combine(root, ".svl-instance-icon-smapi.generated");

        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(customIcon, "player custom icon");
            File.WriteAllText(generatedSmapiIcon, "generated smapi preset");
            File.WriteAllText(generatedMarker, "generated-by-svl\n");

            // 安装流程生成的 SMAPI 预设不能遮住版本设置中选择的通用自定义图标。
            Assert.AreEqual(customIcon, InstanceIconResolver.ResolveIconPath(root, isSmapiInstance: true));

            // 一旦用户主动选择 SMAPI 图标，标记被移除，专属图标恢复最高优先级。
            File.Delete(generatedMarker);
            Assert.AreEqual(generatedSmapiIcon, InstanceIconResolver.ResolveIconPath(root, isSmapiInstance: true));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void InstanceIcons_ShouldMaterializeDefaultSmapiIconInVersionRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-default-smapi-icon-test-" + Guid.NewGuid().ToString("N"));
        var runtimePath = Path.Combine(root, "versions", "SmapiPack");
        var expectedPath = Path.Combine(runtimePath, ".svl-instance-icon-smapi.png");

        try
        {
            Directory.CreateDirectory(runtimePath);

            Assert.IsTrue(InstanceIconResolver.TryWriteDefaultSmapiIcon(runtimePath));
            Assert.IsTrue(File.Exists(expectedPath));
            Assert.IsTrue(File.ReadAllBytes(expectedPath).Length > 0);
            Assert.AreEqual(expectedPath, InstanceIconResolver.ResolveIconPath(runtimePath, isSmapiInstance: true));

            // 第二次调用只能复用已生成文件，不应改写或被 Vanilla 变体遮挡。
            var firstWriteTime = File.GetLastWriteTimeUtc(expectedPath);
            Assert.IsTrue(InstanceIconResolver.TryWriteDefaultSmapiIcon(runtimePath));
            Assert.AreEqual(firstWriteTime, File.GetLastWriteTimeUtc(expectedPath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void IsSmapiRuntime_ShouldRecognizeNewAndLegacyLayouts()
    {
        var newLayout = Path.Combine(Path.GetTempPath(), "svl-smapi-new-layout-" + Guid.NewGuid().ToString("N"));
        var legacyLayout = Path.Combine(Path.GetTempPath(), "svl-smapi-legacy-layout-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(newLayout);
            File.WriteAllText(Path.Combine(newLayout, "StardewModdingAPI.dll"), string.Empty);
            Assert.IsTrue(InstanceIconResolver.IsSmapiRuntime(newLayout));

            Directory.CreateDirectory(Path.Combine(legacyLayout, "game"));
            File.WriteAllText(Path.Combine(legacyLayout, "game", "StardewModdingAPI.exe"), string.Empty);
            Assert.IsTrue(InstanceIconResolver.IsSmapiRuntime(legacyLayout));
        }
        finally
        {
            if (Directory.Exists(newLayout))
            {
                Directory.Delete(newLayout, true);
            }

            if (Directory.Exists(legacyLayout))
            {
                Directory.Delete(legacyLayout, true);
            }
        }
    }

    [TestMethod]
    public void IsVersionIsolatedInstance_ShouldRecognizeVersionRuntimePath()
    {
        var basePath = Path.Combine(Path.GetTempPath(), "svl-isolated-icon-test-" + Guid.NewGuid().ToString("N"));
        var runtimePath = Path.Combine(basePath, "versions", "MyPack", "game");

        try
        {
            Directory.CreateDirectory(runtimePath);

            Assert.IsFalse(InstanceIconResolver.IsVersionIsolatedInstance(basePath));
            Assert.IsTrue(InstanceIconResolver.IsVersionIsolatedInstance(runtimePath));
        }
        finally
        {
            if (Directory.Exists(basePath))
            {
                Directory.Delete(basePath, true);
            }
        }
    }

    [TestMethod]
    public void IsolatedSmapiIcon_ShouldFallbackToLegacyCustomIcon()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-isolated-icon-compat-test-" + Guid.NewGuid().ToString("N"));
        var runtimePath = Path.Combine(root, "versions", "MyPack", "game");
        var legacyIcon = Path.Combine(root, "versions", "MyPack", ".svl-instance-icon.png");

        try
        {
            Directory.CreateDirectory(runtimePath);
            File.WriteAllText(Path.Combine(runtimePath, "StardewModdingAPI.dll"), string.Empty);
            File.WriteAllText(legacyIcon, "legacy custom icon");

            Assert.AreEqual(legacyIcon, InstanceIconResolver.ResolveIconPath(runtimePath, isSmapiInstance: true));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void DownloadTaskStateStore_ShouldPersistCustomIconPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-task-state-icon-test-" + Guid.NewGuid().ToString("N"));
        var statePath = Path.Combine(root, "tasks.json.gz");

        try
        {
            var task = new DownloadTaskItem
            {
                Name = "Local Modpack",
                CustomIconPath = Path.Combine(root, "icon.png")
            };

            var store = new DownloadTaskStateStore();
            store.Save(statePath, [task]);
            var records = store.Load(statePath, out _);

            Assert.AreEqual(1, records.Count);
            Assert.AreEqual(task.CustomIconPath, records[0].CustomIconPath);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public async Task SmapiDownload_ShouldReuseNexusCacheBeforeBrowserFallback()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-smapi-nexus-cache-test-" + Guid.NewGuid().ToString("N"));
        var sourcePath = Path.Combine(root, "smapi-source.zip");
        var settingsPath = Path.Combine(root, "settings");
        var version = "cache-test-" + Guid.NewGuid().ToString("N");
        var fileId = 800_000_000L + Random.Shared.Next(1_000_000);
        var cachePath = NexusDownloadCache.GetCachePath(SmapiDownloadService.SmapiModId, fileId);
        var versionCachePath = Path.Combine(Path.GetTempPath(), "SVL", "smapi", $"SMAPI-{version}.zip");
        byte[] existingCache = null;

        try
        {
            Directory.CreateDirectory(root);
            using (var archive = ZipFile.Open(sourcePath, ZipArchiveMode.Create))
            {
                var payload = new byte[4096];
                Random.Shared.NextBytes(payload);
                using var output = archive.CreateEntry("SMAPI 4.5.2 installer/internal/windows/install.dat").Open();
                output.Write(payload);
            }

            if (File.Exists(cachePath))
            {
                existingCache = File.ReadAllBytes(cachePath);
            }
            NexusDownloadCache.Save(SmapiDownloadService.SmapiModId, fileId, sourcePath);

            var service = new SmapiDownloadService(
                new HttpDownloadService(new AppUserSettingsStore(settingsPath)),
                new SVL.Core.Platform.Abstractions.NxmLinkParser());
            var progress = string.Empty;
            var result = await service.DownloadZipAsync(
                new SmapiVersionEntry
                {
                    Version = version,
                    Source = "NexusMods",
                    FileId = fileId,
                    DownloadUrl = "https://invalid.example/smapi.zip"
                },
                progressText: text => progress = text);

            Assert.IsFalse(string.IsNullOrWhiteSpace(result));
            Assert.IsTrue(File.Exists(result));
            StringAssert.Contains(progress, "Nexus 缓存");
            Assert.AreEqual(new FileInfo(sourcePath).Length, new FileInfo(result).Length);
        }
        finally
        {
            if (existingCache == null)
            {
                if (File.Exists(cachePath))
                {
                    File.Delete(cachePath);
                }
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                File.WriteAllBytes(cachePath, existingCache);
            }

            if (File.Exists(versionCachePath))
            {
                File.Delete(versionCachePath);
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public async Task SmapiDownload_ShouldReuseCurseforgeCacheBeforeHttpFallback()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-smapi-curseforge-cache-test-" + Guid.NewGuid().ToString("N"));
        var sourcePath = Path.Combine(root, "smapi-source.zip");
        var settingsPath = Path.Combine(root, "settings");
        var version = "cf-cache-test-" + Guid.NewGuid().ToString("N");
        var fileId = 810_000_000L + Random.Shared.Next(1_000_000);
        var cachePath = CurseforgeDownloadCache.GetCachePath(898372, fileId);
        var versionCachePath = Path.Combine(Path.GetTempPath(), "SVL", "smapi", $"SMAPI-{version}.zip");
        byte[] existingCache = null;

        try
        {
            Directory.CreateDirectory(root);
            using (var archive = ZipFile.Open(sourcePath, ZipArchiveMode.Create))
            {
                var payload = new byte[4096];
                Random.Shared.NextBytes(payload);
                using var output = archive.CreateEntry("SMAPI 4.5.2 installer/internal/windows/install.dat").Open();
                output.Write(payload);
            }

            if (File.Exists(cachePath))
            {
                existingCache = File.ReadAllBytes(cachePath);
            }

            CurseforgeDownloadCache.Save(898372, fileId, sourcePath);

            var service = new SmapiDownloadService(
                new HttpDownloadService(new AppUserSettingsStore(settingsPath)),
                new SVL.Core.Platform.Abstractions.NxmLinkParser());
            var progress = string.Empty;
            var result = await service.DownloadZipAsync(
                new SmapiVersionEntry
                {
                    Version = version,
                    Source = "CurseForge",
                    FileId = fileId,
                    DownloadUrl = "https://invalid.example/smapi.zip"
                },
                progressText: text => progress = text);

            Assert.IsFalse(string.IsNullOrWhiteSpace(result));
            Assert.IsTrue(File.Exists(result));
            StringAssert.Contains(progress, "CurseForge 缓存");
            Assert.AreEqual(new FileInfo(sourcePath).Length, new FileInfo(result).Length);
        }
        finally
        {
            if (existingCache == null)
            {
                if (File.Exists(cachePath))
                {
                    File.Delete(cachePath);
                }
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                File.WriteAllBytes(cachePath, existingCache);
            }

            if (File.Exists(versionCachePath))
            {
                File.Delete(versionCachePath);
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SmapiDownload_CancelBrowserWaitShouldPropagateCancellation(bool cancelAllWaiters)
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-smapi-cancel-" + Guid.NewGuid().ToString("N"));
        using var cancellation = new CancellationTokenSource();
        try
        {
            var service = new SmapiDownloadService(
                new HttpDownloadService(new AppUserSettingsStore(root)),
                new SVL.Core.Platform.Abstractions.NxmLinkParser());
            var waiting = false;
            var operation = service.DownloadZipAsync(new SmapiVersionEntry
            {
                Version = "cancel-test-" + Guid.NewGuid().ToString("N"),
                Source = "NexusMods",
                FileId = Random.Shared.NextInt64(2000000000, 3000000000),
                DownloadUrl = string.Empty
            }, progressText: _ =>
            {
                waiting = true;
                if (cancelAllWaiters) service.CancelNxmWait();
                else cancellation.Cancel();
            }, cancellationToken: cancellation.Token);

            await Assert.ThrowsExceptionAsync<OperationCanceledException>(async () =>
                await operation.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.IsTrue(waiting, "应实际进入浏览器等待再取消");
            var waiters = typeof(SmapiDownloadService).GetField("_nxmWaiters",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(service)!;
            Assert.AreEqual(0, waiters.GetType().GetProperty("Count")!.GetValue(waiters));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void NexusDownloadCache_ShouldFindAnyValidFileForMod()
    {
        var modId = 900_000_000L + Random.Shared.Next(1_000_000);
        var firstFileId = 111L;
        var secondFileId = 222L;
        var first = NexusDownloadCache.GetCachePath(modId, firstFileId);
        var second = NexusDownloadCache.GetCachePath(modId, secondFileId);
        Directory.CreateDirectory(NexusDownloadCache.Root);
        try
        {
            File.WriteAllBytes(first, [1, 2, 3]);
            File.WriteAllBytes(second, [4, 5, 6]);
            File.SetLastWriteTimeUtc(first, DateTime.UtcNow.AddMinutes(-1));

            var found = NexusDownloadCache.TryGetAnyForMod(
                modId,
                path => File.ReadAllBytes(path).SequenceEqual(new byte[] { 4, 5, 6 }),
                out var selected);

            Assert.IsTrue(found);
            Assert.AreEqual(second, selected);
        }
        finally
        {
            if (File.Exists(first)) File.Delete(first);
            if (File.Exists(second)) File.Delete(second);
        }
    }

    [TestMethod]
    public void ExportCompatibleSvlPackage_ShouldBeRecognizedByModpackDetector()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-export-shape-test-" + Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(root, "export.zip");
        var tempExtractPath = string.Empty;

        try
        {
            Directory.CreateDirectory(root);
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                WriteArchiveText(archive, "modpack.json", "{\"name\":\"Exported Pack\",\"version\":\"1.0.0\",\"smapi_version\":\"4.5.2\",\"mods\":[{}]}");
                WriteArchiveText(archive, "sources.json", "[{\"name\":\"Example\",\"directoryName\":\"ExampleFolder\",\"bundled\":false,\"source\":{\"platform\":\"NexusMods\",\"projectId\":\"1\",\"fileId\":\"2\"}}]");
                WriteArchiveText(archive, "settings/Mods/ExampleFolder/config.json", "{}");
            }

            var detection = ModpackTypeDetector.Detect(archivePath);
            tempExtractPath = detection.TempExtractPath;

            Assert.AreEqual(ModpackType.SVL, detection.Type);
            Assert.AreEqual(1, detection.ModCount);
        }
        finally
        {
            if (Directory.Exists(tempExtractPath))
            {
                Directory.Delete(tempExtractPath, true);
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void ModpackTypeDetector_ShouldReadUtf16Metadata()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-utf16-modpack-test-" + Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(root, "utf16.zip");
        var tempExtractPath = string.Empty;

        try
        {
            Directory.CreateDirectory(root);
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                WriteArchiveTextWithEncoding(
                    archive,
                    "modpack.json",
                    "{\"name\":\"UTF16 Pack\",\"version\":\"1.0.0\",\"mods\":[{}]}",
                    Encoding.Unicode);
            }

            var detection = ModpackTypeDetector.Detect(archivePath);
            tempExtractPath = detection.TempExtractPath;

            Assert.AreEqual(ModpackType.SVL, detection.Type);
            Assert.AreEqual("UTF16 Pack", detection.ModpackName);
            Assert.AreEqual(1, detection.ModCount);
        }
        finally
        {
            if (Directory.Exists(tempExtractPath))
            {
                Directory.Delete(tempExtractPath, true);
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void ModpackTypeDetector_ShouldFindCaseInsensitiveMetadataAndIcon()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-case-insensitive-detection-test-" + Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(root, "case-pack.zip");
        try
        {
            Directory.CreateDirectory(root);
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                WriteArchiveText(
                    archive,
                    "Release/MANIFEST.JSON",
                    "{\"name\":\"大小写包\",\"version\":\"1.0.0\",\"manifestVersion\":1,\"files\":[]}");
                var icon = archive.CreateEntry("Release/PACK-ICON.PNG");
                using var stream = icon.Open();
                stream.Write([1, 2, 3, 4]);
            }

            var detection = ModpackTypeDetector.Detect(archivePath);
            try
            {
                Assert.AreEqual(ModpackType.Curseforge, detection.Type);
                Assert.IsNotNull(detection.ModpackIconPath);
                Assert.IsTrue(File.Exists(detection.ModpackIconPath));
                CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(detection.ModpackIconPath!));
            }
            finally
            {
                ModpackTypeDetector.CleanupTempDirectory(detection.TempExtractPath);
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void ModpackTypeDetector_ShouldRejectZipTraversalEntry()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-zip-traversal-test-" + Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(root, "malicious.zip");

        try
        {
            Directory.CreateDirectory(root);
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                WriteArchiveText(archive, "../../outside-marker.txt", "should not be extracted");
                WriteArchiveText(archive, "modpack.json", "{\"name\":\"Unexpected Pack\",\"mods\":[]}");
            }

            var detection = ModpackTypeDetector.Detect(archivePath);

            Assert.AreEqual(ModpackType.Unknown, detection.Type);
            StringAssert.Contains(detection.ErrorMessage ?? string.Empty, "解压");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public async Task SvlModpackInstall_ShouldFlattenModAndPersistDirectSourceCredential()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-svl-install-e2e-" + Guid.NewGuid().ToString("N"));
        var gamePath = Path.Combine(root, "Game");
        var packagePath = Path.Combine(root, "exported-pack.zip");
        var instanceName = "E2E-" + Guid.NewGuid().ToString("N")[..8];
        var settingsStore = new AppUserSettingsStore(Path.Combine(root, "settings"));
        var registry = new InstanceRegistryStore();
        LocalArchiveServer server = null;

        try
        {
            Directory.CreateDirectory(gamePath);
            var existingSmapiRuntime = Path.Combine(gamePath, "versions", "Existing SMAPI 4.5.2");
            Directory.CreateDirectory(existingSmapiRuntime);
            File.WriteAllText(Path.Combine(existingSmapiRuntime, "StardewModdingAPI.dll"), "smapi");

            var modArchive = CreateZipBytes(
                ("Release/ActualMod/manifest.json",
                    "{\"Name\":\"Actual Mod\",\"UniqueID\":\"Example.Actual\",\"Version\":\"1.2.3\"}"),
                ("Release/ActualMod/content.json", "{}"));
            server = new LocalArchiveServer(modArchive);
            using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var serverTask = server.ServeOnceAsync(testTimeout.Token);

            using (var package = ZipFile.Open(packagePath, ZipArchiveMode.Create))
            {
                WriteArchiveText(
                    package,
                    "Exported Pack/modpack.json",
                    "{\"name\":\"Exported Pack\",\"version\":\"1.0.0\",\"smapi_version\":\"4.5.2\",\"mods\":[{\"name\":\"Actual Mod\"}]}" );
                WriteArchiveText(
                    package,
                    "Exported Pack/sources.json",
                    $"[{{\"directoryName\":\"Actual Mod\",\"bundled\":false,\"source\":{{\"platform\":\"Direct\",\"downloadUrl\":\"{server.Url}\"}}}}]");
                WriteArchiveText(package, "Exported Pack/settings/Mods/Actual Mod/config.json", "{\"enabled\":true}");
                using var icon = package.CreateEntry("Exported Pack/icon.png").Open();
                icon.Write([1, 2, 3, 4]);
            }

            var service = new ModpackInstallService(
                new FixedGameInstallPathLocator(gamePath),
                new TestSmapiInstallService(),
                new HttpDownloadService(settingsStore),
                new RemoteCatalogService(settingsStore),
                settingsStore,
                new NexusModDownloadResolverService(),
                new SVL.Core.Platform.Abstractions.NxmLinkParser());

            var result = await service.InstallSvlModpackAsync(
                packagePath,
                instanceName,
                gamePath,
                onProgress: progress => Console.WriteLine(
                    $"[SvlModpackE2E] {progress.StepText} {progress.SubProgressText}"),
                cancellationToken: testTimeout.Token);

            try
            {
                await serverTask;
            }
            catch (OperationCanceledException)
            {
                // 失败断言会包含任务结果；取消本地服务避免测试自身无限等待。
            }

            var installedPath = Path.Combine(gamePath, "versions", instanceName, "Mods", "ActualMod");
            Assert.IsTrue(result.IsSuccess, $"{result.Message}; failed={string.Join(" | ", result.FailedMods)}");
            Assert.AreEqual(1, result.InstalledMods.Count);
            Assert.AreEqual(0, result.FailedMods.Count);
            Assert.IsTrue(File.Exists(Path.Combine(installedPath, "manifest.json")));
            Assert.IsTrue(File.Exists(Path.Combine(installedPath, "content.json")));
            Assert.IsTrue(File.Exists(Path.Combine(installedPath, "config.json")));
            Assert.IsFalse(Directory.Exists(Path.Combine(installedPath, "Release")));
            Assert.IsFalse(Directory.Exists(Path.Combine(gamePath, "versions", instanceName, "Mods", "Actual Mod")));
            Assert.IsTrue(File.Exists(Path.Combine(gamePath, "versions", instanceName, ".svl-instance-icon-smapi.png")));

            var sourceCredentialPath = Path.Combine(installedPath, "svl-source.json");
            Assert.IsTrue(
                File.Exists(sourceCredentialPath),
                $"未写入来源凭证，Mods 目录内容: {string.Join(", ", Directory.GetDirectories(Path.Combine(gamePath, "versions", instanceName, "Mods")))}");
            using var credential = JsonDocument.Parse(
                File.ReadAllText(sourceCredentialPath));
            Assert.AreEqual("Direct", credential.RootElement.GetProperty("platform").GetString());
            Assert.AreEqual(server.Url.ToString(), credential.RootElement.GetProperty("downloadUrl").GetString());
        }
        finally
        {
            server?.Dispose();
            var records = registry.LoadManualInstances();
            records.RemoveAll(record => string.Equals(record.Name, instanceName, StringComparison.OrdinalIgnoreCase));
            registry.SaveManualInstances(records);

            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public async Task SvlModpackInstall_ShouldKeepInferredNexusIdentityForDirectCdnSource()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-inferred-nexus-direct-e2e-" + Guid.NewGuid().ToString("N"));
        var gamePath = Path.Combine(root, "Game");
        var packagePath = Path.Combine(root, "inferred-nexus-pack.zip");
        var instanceName = "NexusDirect-" + Guid.NewGuid().ToString("N")[..8];
        var settingsStore = new AppUserSettingsStore(Path.Combine(root, "settings"));
        var registry = new InstanceRegistryStore();
        LocalArchiveServer server = null!;
        var modId = Math.Abs(DateTime.UtcNow.Ticks % 900_000_000L) + 100_000_000L;
        var fileId = modId + 1;
        var cachePath = NexusDownloadCache.GetCachePath(modId, fileId);

        try
        {
            Directory.CreateDirectory(gamePath);
            var existingSmapiRuntime = Path.Combine(gamePath, "versions", "Existing SMAPI 4.5.2");
            Directory.CreateDirectory(existingSmapiRuntime);
            File.WriteAllText(Path.Combine(existingSmapiRuntime, "StardewModdingAPI.dll"), "smapi");

            server = new LocalArchiveServer(CreateZipBytes(
                ("Release/ActualMod/manifest.json",
                    "{\"Name\":\"Actual Mod\",\"UniqueID\":\"Example.Actual\",\"Version\":\"1.2.3\"}"),
                ("Release/ActualMod/content.json", "{}")));
            using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var serverTask = server.ServeOnceAsync(testTimeout.Token);

            using (var package = ZipFile.Open(packagePath, ZipArchiveMode.Create))
            {
                WriteArchiveText(
                    package,
                    "Inferred Nexus Pack/modpack.json",
                    "{\"name\":\"Inferred Nexus Pack\",\"version\":\"1.0.0\",\"smapi_version\":\"4.5.2\",\"mods\":[{\"name\":\"Actual Mod\"}]}" );
                // 旧导出可能只有两个数字 ID 与真实 CDN 直链，没有 platform。
                // 安装器已在解析阶段推断为 Nexus；这里验证该身份不会在直链分支丢失。
                WriteArchiveText(
                    package,
                    "Inferred Nexus Pack/sources.json",
                    $"[{{\"name\":\"Actual Mod\",\"directoryName\":\"Actual Mod\",\"bundled\":false,\"projectId\":\"{modId}\",\"fileId\":\"{fileId}\",\"downloadUrl\":\"{server.Url}\"}}]");
            }

            var service = new ModpackInstallService(
                new FixedGameInstallPathLocator(gamePath),
                new TestSmapiInstallService(),
                new HttpDownloadService(settingsStore),
                new RemoteCatalogService(settingsStore),
                settingsStore,
                new NexusModDownloadResolverService(),
                new SVL.Core.Platform.Abstractions.NxmLinkParser());

            var result = await service.InstallSvlModpackAsync(
                packagePath,
                instanceName,
                gamePath,
                onProgress: null,
                cancellationToken: testTimeout.Token);

            try
            {
                await serverTask;
            }
            catch (OperationCanceledException)
            {
                // 失败断言会包含任务结果；取消本地服务避免测试自身无限等待。
            }

            var installedPath = Path.Combine(gamePath, "versions", instanceName, "Mods", "ActualMod");
            var sourcePath = Path.Combine(installedPath, "svl-source.json");
            Assert.IsTrue(result.IsSuccess, $"{result.Message}; failed={string.Join(" | ", result.FailedMods)}");
            Assert.AreEqual(1, result.InstalledMods.Count);
            Assert.AreEqual(0, result.FailedMods.Count);
            Assert.IsTrue(File.Exists(Path.Combine(installedPath, "manifest.json")));
            Assert.IsTrue(File.Exists(sourcePath));

            using var source = JsonDocument.Parse(File.ReadAllText(sourcePath));
            Assert.AreEqual("NexusMods", source.RootElement.GetProperty("platform").GetString());
            Assert.AreEqual(modId.ToString(), source.RootElement.GetProperty("projectId").GetString());
            Assert.AreEqual(fileId.ToString(), source.RootElement.GetProperty("fileId").GetString());
        }
        finally
        {
            server?.Dispose();
            try
            {
                if (File.Exists(cachePath))
                {
                    File.Delete(cachePath);
                }
            }
            catch
            {
                // 测试清理不应掩盖主体断言。
            }

            var records = registry.LoadManualInstances();
            records.RemoveAll(record => string.Equals(record.Name, instanceName, StringComparison.OrdinalIgnoreCase));
            registry.SaveManualInstances(records);

            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public async Task SvlModpackInstall_ShouldReuseNexusCacheFromBrowserFilesPageSource()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-nexus-page-cache-e2e-" + Guid.NewGuid().ToString("N"));
        var gamePath = Path.Combine(root, "Game");
        var packagePath = Path.Combine(root, "nexus-page-pack.zip");
        var instanceName = "Nexus-" + Guid.NewGuid().ToString("N")[..8];
        var settingsStore = new AppUserSettingsStore(Path.Combine(root, "settings"));
        var registry = new InstanceRegistryStore();
        const long modId = 29868;
        const long fileId = 7448774;
        var cachePath = NexusDownloadCache.GetCachePath(modId, fileId);
        byte[] existingCache = null;

        try
        {
            if (File.Exists(cachePath))
            {
                existingCache = File.ReadAllBytes(cachePath);
            }

            Directory.CreateDirectory(Path.Combine(gamePath, "versions", "Existing SMAPI 4.5.2"));
            File.WriteAllText(
                Path.Combine(gamePath, "versions", "Existing SMAPI 4.5.2", "StardewModdingAPI.dll"),
                "existing-smapi");

            var modArchive = Path.Combine(root, "File 7448774_ Content Patcher 2.9.0 2.9.0.zip");
            File.WriteAllBytes(
                modArchive,
                CreateZipBytes(
                    ("Release/Content Patcher/manifest.json",
                        "{\"Name\":\"Content Patcher\",\"UniqueID\":\"Pathoschild.ContentPatcher\",\"Version\":\"2.9.0\"}"),
                    ("Release/Content Patcher/content.json", "{}")));
            NexusDownloadCache.Save(modId, fileId, modArchive, IsZipArchive);

            using (var package = ZipFile.Open(packagePath, ZipArchiveMode.Create))
            {
                WriteArchiveText(
                    package,
                    "Nexus Page Pack/modpack.json",
                    "{\"name\":\"Nexus Page Pack\",\"version\":\"1.0.0\",\"smapi_version\":\"4.5.2\",\"mods\":[{\"name\":\"Content Patcher\"}]}");
                WriteArchiveText(
                    package,
                    "Nexus Page Pack/sources.json",
                    "[{\"name\":\"Content Patcher\",\"directoryName\":\"File 7448774_ Content Patcher 2.9.0 2.9.0\",\"bundled\":false," +
                    "\"source\":{\"platform\":\"NexusMods\",\"projectId\":\"\",\"fileId\":\"\"," +
                    "\"downloadUrl\":\"https://www.nexusmods.com/stardewvalley/mods/29868?tab=files&nmm=1\"," +
                    "\"fileName\":\"File 7448774_ Content Patcher 2.9.0 2.9.0.zip\"}}]");
            }

            // 不注入浏览器回退服务：如果 FileID 没有从页面地址/文件名恢复，
            // 测试会尝试联网或等待回调，从而把“缓存命中”误判成偶然成功。
            var service = new ModpackInstallService(
                new FixedGameInstallPathLocator(gamePath),
                new TestSmapiInstallService(),
                new HttpDownloadService(settingsStore),
                new RemoteCatalogService(settingsStore),
                settingsStore,
                new NexusModDownloadResolverService(),
                new SVL.Core.Platform.Abstractions.NxmLinkParser());

            var result = await service.InstallSvlModpackAsync(
                packagePath,
                instanceName,
                gamePath,
                onProgress: null);

            var installedPath = Path.Combine(
                gamePath,
                "versions",
                instanceName,
                "Mods",
                "Content Patcher");
            var credentialPath = Path.Combine(installedPath, "svl-source.json");

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual(1, result.InstalledMods.Count);
            Assert.AreEqual(0, result.FailedMods.Count);
            Assert.IsTrue(File.Exists(Path.Combine(installedPath, "manifest.json")));
            Assert.IsTrue(File.Exists(Path.Combine(installedPath, "content.json")));
            Assert.IsTrue(File.Exists(credentialPath));

            using var credential = JsonDocument.Parse(File.ReadAllText(credentialPath));
            Assert.AreEqual("NexusMods", credential.RootElement.GetProperty("platform").GetString());
            Assert.AreEqual(modId.ToString(), credential.RootElement.GetProperty("projectId").GetString());
            Assert.AreEqual(fileId.ToString(), credential.RootElement.GetProperty("fileId").GetString());
        }
        finally
        {
            if (existingCache == null)
            {
                try
                {
                    if (File.Exists(cachePath))
                    {
                        File.Delete(cachePath);
                    }
                }
                catch
                {
                    // 测试清理不应掩盖主体断言。
                }
            }
            else
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                    File.WriteAllBytes(cachePath, existingCache);
                }
                catch
                {
                    // 测试清理不应掩盖主体断言。
                }
            }

            var records = registry.LoadManualInstances();
            records.RemoveAll(record =>
                string.Equals(record.Name, instanceName, StringComparison.OrdinalIgnoreCase) &&
                record.Path.StartsWith(gamePath, StringComparison.OrdinalIgnoreCase));
            registry.SaveManualInstances(records);

            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public async Task SvlModpackInstall_ShouldReuseNexusCacheWhenSourceHasExpiredCdnUrl()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-nexus-cdn-cache-e2e-" + Guid.NewGuid().ToString("N"));
        var gamePath = Path.Combine(root, "Game");
        var packagePath = Path.Combine(root, "nexus-cdn-pack.zip");
        var instanceName = "NexusCdn-" + Guid.NewGuid().ToString("N")[..8];
        var settingsStore = new AppUserSettingsStore(Path.Combine(root, "settings"));
        var registry = new InstanceRegistryStore();
        const long modId = 29868;
        const long fileId = 7448774;
        var cachePath = NexusDownloadCache.GetCachePath(modId, fileId);
        byte[] existingCache = null;

        try
        {
            if (File.Exists(cachePath))
            {
                existingCache = File.ReadAllBytes(cachePath);
            }

            Directory.CreateDirectory(Path.Combine(gamePath, "versions", "Existing SMAPI 4.5.2"));
            File.WriteAllText(
                Path.Combine(gamePath, "versions", "Existing SMAPI 4.5.2", "StardewModdingAPI.dll"),
                "existing-smapi");

            var modArchive = Path.Combine(root, "Content Patcher.zip");
            File.WriteAllBytes(
                modArchive,
                CreateZipBytes(
                    ("Content Patcher/manifest.json",
                        "{\"Name\":\"Content Patcher\",\"UniqueID\":\"Pathoschild.ContentPatcher\",\"Version\":\"2.9.0\"}"),
                    ("Content Patcher/content.json", "{}")));
            NexusDownloadCache.Save(modId, fileId, modArchive, IsZipArchive);

            using (var package = ZipFile.Open(packagePath, ZipArchiveMode.Create))
            {
                WriteArchiveText(
                    package,
                    "Nexus CDN Pack/modpack.json",
                    "{\"name\":\"Nexus CDN Pack\",\"version\":\"1.0.0\",\"smapi_version\":\"4.5.2\",\"mods\":[{\"name\":\"Content Patcher\"}]}");
                WriteArchiveText(
                    package,
                    "Nexus CDN Pack/sources.json",
                    "[{\"name\":\"Content Patcher\",\"directoryName\":\"Content Patcher\",\"bundled\":false," +
                    "\"source\":{\"platform\":\"NexusMods\",\"projectId\":\"29868\",\"fileId\":\"7448774\"," +
                    "\"downloadUrl\":\"https://file-metadata.nexusmods.com/expired/7448774/ContentPatcher.zip?fid=7448774\"," +
                    "\"fileName\":\"Content Patcher.zip\"}}]");
            }

            // 不注入浏览器回退服务；过期 CDN 地址不可联网，只有稳定缓存命中才能完成安装。
            var service = new ModpackInstallService(
                new FixedGameInstallPathLocator(gamePath),
                new TestSmapiInstallService(),
                new HttpDownloadService(settingsStore),
                new RemoteCatalogService(settingsStore),
                settingsStore,
                new NexusModDownloadResolverService(),
                new SVL.Core.Platform.Abstractions.NxmLinkParser());

            var result = await service.InstallSvlModpackAsync(
                packagePath,
                instanceName,
                gamePath,
                onProgress: null);

            var installedPath = Path.Combine(
                gamePath,
                "versions",
                instanceName,
                "Mods",
                "Content Patcher");
            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual(1, result.InstalledMods.Count);
            Assert.AreEqual(0, result.FailedMods.Count);
            Assert.IsTrue(File.Exists(Path.Combine(installedPath, "manifest.json")));
            Assert.IsTrue(File.Exists(Path.Combine(installedPath, "svl-source.json")));
        }
        finally
        {
            if (existingCache == null)
            {
                try
                {
                    if (File.Exists(cachePath)) File.Delete(cachePath);
                }
                catch { }
            }
            else
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                    File.WriteAllBytes(cachePath, existingCache);
                }
                catch { }
            }

            var records = registry.LoadManualInstances();
            records.RemoveAll(record =>
                string.Equals(record.Name, instanceName, StringComparison.OrdinalIgnoreCase) &&
                record.Path.StartsWith(gamePath, StringComparison.OrdinalIgnoreCase));
            registry.SaveManualInstances(records);

            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("https://expired.example.invalid/mod.zip")]
    public async Task SvlSource_ShouldInstallCurseforgeCacheBeforeResolvingUrl(string downloadUrl)
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-cf-source-cache-" + Guid.NewGuid().ToString("N"));
        var projectId = Random.Shared.NextInt64(1000000000, 2000000000);
        var fileId = Random.Shared.NextInt64(1000000000, 2000000000);
        var cachePath = CurseforgeDownloadCache.GetCachePath(projectId, fileId);
        try
        {
            Directory.CreateDirectory(root);
            var archive = Path.Combine(root, "mod.zip");
            File.WriteAllBytes(archive, CreateZipBytes(
                ("Release/CachedMod/manifest.json", "{\"Name\":\"CachedMod\",\"UniqueID\":\"Test.CachedMod\",\"Version\":\"1.2.3\"}"),
                ("Release/CachedMod/content.json", "{}")));
            CurseforgeDownloadCache.Save(projectId, fileId, archive, IsZipArchive);
            var settings = new AppUserSettingsStore(Path.Combine(root, "settings"));
            var service = new ModpackInstallService(
                new FixedGameInstallPathLocator(root), new TestSmapiInstallService(),
                new HttpDownloadService(settings), new RemoteCatalogService(settings), settings,
                new NexusModDownloadResolverService(), new SVL.Core.Platform.Abstractions.NxmLinkParser());
            using var source = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                name = "CachedMod",
                source = new { platform = "Curseforge", projectId, fileId, downloadUrl }
            }));
            var method = typeof(ModpackInstallService).GetMethod("DownloadModFromSourceAsync",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var mods = Path.Combine(root, "Mods");
            var task = (Task)method.Invoke(service, [source.RootElement, mods, CancellationToken.None, null])!;
            await task.WaitAsync(TimeSpan.FromSeconds(5));
            var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
            Assert.AreEqual(true, result.GetType().GetProperty("IsSuccess")!.GetValue(result));
            Assert.IsTrue(File.Exists(Path.Combine(mods, "CachedMod", "content.json")));
            using var credential = JsonDocument.Parse(File.ReadAllText(Path.Combine(mods, "CachedMod", "svl-source.json")));
            Assert.AreEqual(fileId.ToString(), credential.RootElement.GetProperty("fileId").GetString());
            Assert.AreEqual("Curseforge", credential.RootElement.GetProperty("platform").GetString());
        }
        finally
        {
            if (File.Exists(cachePath)) File.Delete(cachePath);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task SvlSource_ShouldInferNexusFileIdFromLogicalFilenameAndReuseCache()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "svl-nexus-logical-filename-e2e-" + Guid.NewGuid().ToString("N"));
        var modId = Random.Shared.NextInt64(1_000_000_000, 2_000_000_000);
        var fileId = Random.Shared.NextInt64(1_000_000_000, 2_000_000_000);
        var cachePath = NexusDownloadCache.GetCachePath(modId, fileId);

        try
        {
            Directory.CreateDirectory(root);
            var archive = Path.Combine(root, "cached-content-patcher.zip");
            File.WriteAllBytes(archive, CreateZipBytes(
                ("Release/Content Patcher/manifest.json",
                    "{\"Name\":\"Content Patcher\",\"UniqueID\":\"Test.ContentPatcher\",\"Version\":\"2.9.0\"}"),
                ("Release/Content Patcher/content.json", "{}")));
            NexusDownloadCache.Save(modId, fileId, archive, IsZipArchive);

            var settings = new AppUserSettingsStore(Path.Combine(root, "settings"));
            var service = new ModpackInstallService(
                new FixedGameInstallPathLocator(root),
                new TestSmapiInstallService(),
                new HttpDownloadService(settings),
                new RemoteCatalogService(settings),
                settings,
                new NexusModDownloadResolverService(),
                new SVL.Core.Platform.Abstractions.NxmLinkParser());

            // 模拟 Collection/第三方导出的布局：页面 URL 只有 ModID，
            // FileID 仅保存在 logicalFilename 中，URL 本身不可直接下载。
            using var source = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                name = "Content Patcher",
                source = new
                {
                    platform = "NexusMods",
                    projectId = modId,
                    url = $"https://www.nexusmods.com/stardewvalley/mods/{modId}?tab=files&nmm=1",
                    logicalFilename = $"File {fileId}_ Content Patcher 2.9.0.zip"
                }
            }));

            var method = typeof(ModpackInstallService).GetMethod(
                "DownloadModFromSourceAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(method);

            var mods = Path.Combine(root, "Mods");
            var task = (Task)method!.Invoke(
                service,
                [source.RootElement, mods, CancellationToken.None, null])!;
            await task.WaitAsync(TimeSpan.FromSeconds(5));
            var result = task.GetType().GetProperty("Result")!.GetValue(task)!;

            Assert.AreEqual(true, result.GetType().GetProperty("IsSuccess")!.GetValue(result));
            var installedPath = Path.Combine(mods, "Content Patcher");
            Assert.IsTrue(File.Exists(Path.Combine(installedPath, "manifest.json")));
            using var credential = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(installedPath, "svl-source.json")));
            Assert.AreEqual("NexusMods", credential.RootElement.GetProperty("platform").GetString());
            Assert.AreEqual(modId.ToString(), credential.RootElement.GetProperty("projectId").GetString());
            Assert.AreEqual(fileId.ToString(), credential.RootElement.GetProperty("fileId").GetString());
        }
        finally
        {
            if (File.Exists(cachePath))
            {
                File.Delete(cachePath);
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public async Task SvlModpackInstall_RetryShouldUseUpdateModeForExistingInstance()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-svl-retry-mode-test-" + Guid.NewGuid().ToString("N"));
        var gamePath = Path.Combine(root, "Game");
        var packagePath = Path.Combine(root, "retry-pack.zip");
        var instanceName = "Retry-" + Guid.NewGuid().ToString("N")[..8];
        var settingsStore = new AppUserSettingsStore(Path.Combine(root, "settings"));
        var registry = new InstanceRegistryStore();

        try
        {
            Directory.CreateDirectory(gamePath);
            var existingSmapiRuntime = Path.Combine(gamePath, "versions", "Existing SMAPI 4.5.2");
            Directory.CreateDirectory(existingSmapiRuntime);
            File.WriteAllText(Path.Combine(existingSmapiRuntime, "StardewModdingAPI.dll"), "smapi");

            using (var package = ZipFile.Open(packagePath, ZipArchiveMode.Create))
            {
                WriteArchiveText(
                    package,
                    "modpack.json",
                    "{\"name\":\"Retry Pack\",\"version\":\"1.0.0\",\"smapi_version\":\"4.5.2\",\"mods\":[]}");
                WriteArchiveText(package, "sources.json", "[]");
            }

            var smapi = new TestSmapiInstallService();
            var service = new ModpackInstallService(
                new FixedGameInstallPathLocator(gamePath),
                smapi,
                new HttpDownloadService(settingsStore),
                new RemoteCatalogService(settingsStore),
                settingsStore,
                new NexusModDownloadResolverService(),
                new SVL.Core.Platform.Abstractions.NxmLinkParser());

            var first = await service.InstallSvlModpackAsync(
                packagePath,
                instanceName,
                gamePath,
                onProgress: null,
                updateExisting: false);
            Assert.IsTrue(first.IsSuccess, first.Message);

            // 更新重试时目标实例可能是 Base 下唯一的 SMAPI；旧的辅助实例被移除后，
            // 仍应允许从当前 Retry 实例生成临时安装包。
            Directory.Delete(existingSmapiRuntime, recursive: true);

            var second = await service.InstallSvlModpackAsync(
                packagePath,
                instanceName,
                gamePath,
                onProgress: null,
                updateExisting: true);
            Assert.IsTrue(second.IsSuccess, second.Message);
            CollectionAssert.AreEqual(new[] { false, true }, smapi.UpdateExistingCalls);
            Assert.IsTrue(Directory.Exists(Path.Combine(gamePath, "versions", instanceName)));
        }
        finally
        {
            var records = registry.LoadManualInstances();
            records.RemoveAll(record => string.Equals(record.Name, instanceName, StringComparison.OrdinalIgnoreCase));
            registry.SaveManualInstances(records);

            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public async Task CurseforgeModpackInstall_ShouldReuseCachedArchiveAndKeepCurseforgeCredential()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-cf-cache-e2e-" + Guid.NewGuid().ToString("N"));
        var gamePath = Path.Combine(root, "Game");
        var packagePath = Path.Combine(root, "curseforge-pack.zip");
        var instanceName = "CurseForge-" + Guid.NewGuid().ToString("N")[..8];
        var settingsStore = new AppUserSettingsStore(Path.Combine(root, "settings"));
        var registry = new InstanceRegistryStore();
        const long projectId = 1012214;
        const long fileId = 5312529;
        var globalCachePath = CurseforgeDownloadCache.GetCachePath(projectId, fileId);
        byte[] existingGlobalCache = null;

        try
        {
            Directory.CreateDirectory(Path.Combine(gamePath, "versions", "Existing SMAPI 4.5.2"));
            File.WriteAllText(
                Path.Combine(gamePath, "versions", "Existing SMAPI 4.5.2", "StardewModdingAPI.dll"),
                "existing-smapi");

            using (var package = ZipFile.Open(packagePath, ZipArchiveMode.Create))
            {
                WriteArchiveText(
                    package,
                    "CurseForge Pack/manifest.json",
                    "{\"name\":\"CurseForge Cache Pack\",\"version\":\"1.0.0\",\"manifestVersion\":1," +
                    "\"files\":[{\"projectID\":1012214,\"fileID\":5312529," +
                    "\"required\":true},{\"projectID\":898372,\"fileID\":4500000," +
                    "\"required\":true},{\"projectID\":1234567,\"fileID\":7654321," +
                    "\"required\":false}],\"overrides\":\"overrides\"}");
                WriteArchiveText(package, "CurseForge Pack/overrides/config.ini", "enabled=true");
            }

            var targetModsPath = Path.Combine(gamePath, "versions", instanceName, "Mods");
            var cachePath = Path.Combine(
                targetModsPath,
                "_downloads",
                $"cf-{projectId}-{fileId}.zip");
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            File.WriteAllBytes(
                cachePath,
                CreateZipBytes(
                    ("Release/Content Patcher/manifest.json",
                        "{\"Name\":\"Content Patcher\",\"UniqueID\":\"Pathoschild.ContentPatcher\",\"Version\":\"2.9.0\"}"),
                    ("Release/Content Patcher/content.json", "{}")));

            // 模拟下载页/另一个 Base 已经写入的全局缓存；本次实例目录不保留
            // _downloads 文件，确保安装流程确实走 ProjectID/FileID 稳定缓存。
            if (File.Exists(globalCachePath))
            {
                existingGlobalCache = File.ReadAllBytes(globalCachePath);
            }

            CurseforgeDownloadCache.Save(
                projectId,
                fileId,
                cachePath,
                IsArchiveWithManifest);
            File.Delete(cachePath);

            var nxmParser = new SVL.Core.Platform.Abstractions.NxmLinkParser();
            var service = new ModpackInstallService(
                new FixedGameInstallPathLocator(gamePath),
                new TestSmapiInstallService(),
                new HttpDownloadService(settingsStore),
                new RemoteCatalogService(settingsStore),
                settingsStore,
                new NexusModDownloadResolverService(),
                nxmParser);

            var result = await service.InstallCurseforgeModpackAsync(
                packagePath,
                instanceName,
                gamePath,
                onProgress: null);

            var installedPath = Path.Combine(
                targetModsPath,
                "Content Patcher");
            var credentialPath = Path.Combine(installedPath, "svl-source.json");
            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual(1, result.InstalledMods.Count);
            Assert.AreEqual(0, result.FailedMods.Count);
            Assert.IsTrue(File.Exists(Path.Combine(installedPath, "manifest.json")));
            Assert.IsTrue(File.Exists(Path.Combine(installedPath, "content.json")));
            Assert.IsTrue(File.Exists(Path.Combine(
                gamePath,
                "versions",
                instanceName,
                "config.ini")));
            Assert.IsTrue(File.Exists(globalCachePath), "有效的 CurseForge 全局缓存不应在复用后被删除");

            using var credential = JsonDocument.Parse(File.ReadAllText(credentialPath));
            Assert.AreEqual("Curseforge", credential.RootElement.GetProperty("platform").GetString());
            Assert.AreEqual(projectId.ToString(), credential.RootElement.GetProperty("projectId").GetString());
            Assert.AreEqual(fileId.ToString(), credential.RootElement.GetProperty("fileId").GetString());

        }
        finally
        {
            if (existingGlobalCache == null)
            {
                try
                {
                    if (File.Exists(globalCachePath))
                    {
                        File.Delete(globalCachePath);
                    }
                }
                catch { }
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(globalCachePath)!);
                File.WriteAllBytes(globalCachePath, existingGlobalCache);
            }

            var records = registry.LoadManualInstances();
            records.RemoveAll(record => string.Equals(record.Name, instanceName, StringComparison.OrdinalIgnoreCase));
            registry.SaveManualInstances(records);

            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public async Task CurseforgeModpackInstall_ShouldPreferManifestSmapiFileWhenCatalogIsUnavailable()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-cf-manifest-smapi-" + Guid.NewGuid().ToString("N"));
        var gamePath = Path.Combine(root, "Game");
        var packagePath = Path.Combine(root, "curseforge-pack.zip");
        var instanceName = "CurseForge-SMAPI-" + Guid.NewGuid().ToString("N")[..8];
        var settingsStore = new AppUserSettingsStore(Path.Combine(root, "settings"));
        var smapiFileId = 900_000_000L + Random.Shared.Next(1_000_000);
        var smapiCachePath = CurseforgeDownloadCache.GetCachePath(898372, smapiFileId);
        byte[] previousSmapiCache = null;

        static bool IsSmapiArchive(string path)
        {
            try
            {
                using var archive = ZipFile.OpenRead(path);
                return archive.Entries.Any(entry =>
                    entry.FullName.EndsWith("install.dat", StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(packagePath)!);
            using (var package = ZipFile.Open(packagePath, ZipArchiveMode.Create))
            {
                WriteArchiveText(
                    package,
                    "manifest.json",
                    $"{{\"name\":\"Manifest SMAPI Pack\",\"version\":\"1.0.0\",\"manifestVersion\":1," +
                    $"\"files\":[{{\"projectID\":898372,\"fileID\":{smapiFileId},\"required\":true}}," +
                    "{\"projectID\":1012214,\"fileID\":5312529,\"required\":true}]," +
                    "\"overrides\":\"overrides\"}");
                WriteArchiveText(package, "overrides/config.ini", "enabled=true");
            }

            var smapiSourcePath = Path.Combine(root, "smapi.zip");
            File.WriteAllBytes(
                smapiSourcePath,
                CreateZipBytes(("SMAPI 4.5.2 installer/internal/windows/install.dat", "smapi")));
            if (File.Exists(smapiCachePath))
            {
                previousSmapiCache = File.ReadAllBytes(smapiCachePath);
            }

            // 只预置清单指定的 SMAPI 全局缓存和单个 Mod 的实例临时缓存，
            // 不提供任何已有 SMAPI 实例；如果代码仍先请求版本目录，测试就会
            // 在无网络环境下失败。
            CurseforgeDownloadCache.Save(898372, smapiFileId, smapiSourcePath, IsSmapiArchive);

            var localModArchive = Path.Combine(
                gamePath,
                "versions",
                instanceName,
                "Mods",
                "_downloads",
                "cf-1012214-5312529.zip");
            Directory.CreateDirectory(Path.GetDirectoryName(localModArchive)!);
            File.WriteAllBytes(
                localModArchive,
                CreateZipBytes(
                    ("Release/Content Patcher/manifest.json",
                        "{\"Name\":\"Content Patcher\",\"UniqueID\":\"Pathoschild.ContentPatcher\",\"Version\":\"2.9.0\"}"),
                    ("Release/Content Patcher/content.json", "{}")));

            var service = new ModpackInstallService(
                new FixedGameInstallPathLocator(gamePath),
                new TestSmapiInstallService(),
                new HttpDownloadService(settingsStore),
                new RemoteCatalogService(settingsStore),
                settingsStore,
                new NexusModDownloadResolverService(),
                new SVL.Core.Platform.Abstractions.NxmLinkParser());

            var result = await service.InstallCurseforgeModpackAsync(
                packagePath,
                instanceName,
                gamePath,
                onProgress: null);

            var installedPath = Path.Combine(
                gamePath,
                "versions",
                instanceName,
                "Mods",
                "Content Patcher");
            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual(1, result.InstalledMods.Count);
            Assert.AreEqual(0, result.FailedMods.Count);
            Assert.IsTrue(File.Exists(Path.Combine(installedPath, "manifest.json")));
            Assert.IsTrue(File.Exists(Path.Combine(
                gamePath,
                "versions",
                instanceName,
                "StardewModdingAPI.dll")));
        }
        finally
        {
            try
            {
                if (File.Exists(smapiCachePath))
                {
                    File.Delete(smapiCachePath);
                }

                if (previousSmapiCache != null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(smapiCachePath)!);
                    File.WriteAllBytes(smapiCachePath, previousSmapiCache);
                }
            }
            catch { }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public async Task CollectionInstall_ShouldReuseCachedCurseforgeArchive()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-collection-cf-cache-e2e-" + Guid.NewGuid().ToString("N"));
        var gamePath = Path.Combine(root, "Game");
        var archivePath = Path.Combine(root, "collection.zip");
        var instanceName = "CollectionCF-" + Guid.NewGuid().ToString("N")[..8];
        var settingsStore = new AppUserSettingsStore(Path.Combine(root, "settings"));
        var registry = new InstanceRegistryStore();
        const long projectId = 1012214;
        const long fileId = 5312529;

        try
        {
            Directory.CreateDirectory(Path.Combine(gamePath, "versions", "Existing SMAPI 4.5.2"));
            File.WriteAllText(
                Path.Combine(gamePath, "versions", "Existing SMAPI 4.5.2", "StardewModdingAPI.dll"),
                "existing-smapi");

            using (var package = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                WriteArchiveText(
                    package,
                    "Collection/collection.json",
                    "{\"info\":{\"name\":\"CurseForge Cache Collection\"},\"mods\":[" +
                    "{\"name\":\"Content Patcher\",\"version\":\"2.9.0\",\"source\":{" +
                    "\"type\":\"Curseforge\",\"modId\":1012214,\"fileId\":5312529," +
                    "\"url\":\"https://edge.forgecdn.net/files/5312/529/content-patcher.zip\"}}]}");
            }

            var targetModsPath = Path.Combine(gamePath, "versions", instanceName, "Mods");
            var cachePath = Path.Combine(
                targetModsPath,
                "_downloads",
                $"cf-{projectId}-{fileId}.zip");
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            File.WriteAllBytes(
                cachePath,
                CreateZipBytes(
                    ("Release/Content Patcher/manifest.json",
                        "{\"Name\":\"Content Patcher\",\"UniqueID\":\"Pathoschild.ContentPatcher\",\"Version\":\"2.9.0\"}"),
                    ("Release/Content Patcher/content.json", "{}")));

            var nxmParser = new SVL.Core.Platform.Abstractions.NxmLinkParser();
            var service = new CollectionInstallService(
                new FixedGameInstallPathLocator(gamePath),
                new TestSmapiInstallService(),
                new HttpDownloadService(settingsStore),
                new RemoteCatalogService(settingsStore),
                settingsStore,
                new NexusModDownloadResolverService(),
                nxmParser,
                new BrowserDownloadFallbackService(
                    nxmParser,
                    new SVL.Core.Platform.Services.ExternalProcessService()),
                new ModpackInstallService(
                    new FixedGameInstallPathLocator(gamePath),
                    new TestSmapiInstallService(),
                    new HttpDownloadService(settingsStore),
                    new RemoteCatalogService(settingsStore),
                    settingsStore,
                    new NexusModDownloadResolverService(),
                    nxmParser));

            var result = await service.InstallCollectionFromArchiveAsync(
                archivePath,
                instanceName,
                onProgress: null,
                gameBasePath: gamePath);

            var installedPath = Path.Combine(targetModsPath, "Content Patcher");
            var credentialPath = Path.Combine(installedPath, "svl-source.json");
            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual(1, result.InstalledMods.Count);
            Assert.AreEqual(0, result.FailedMods.Count);
            Assert.IsTrue(File.Exists(Path.Combine(installedPath, "manifest.json")));
            Assert.IsTrue(File.Exists(Path.Combine(installedPath, "content.json")));
            Assert.IsTrue(File.Exists(cachePath), "有效的 CurseForge 缓存不应在 Collection 安装后被删除");

            using var credential = JsonDocument.Parse(File.ReadAllText(credentialPath));
            Assert.AreEqual("Curseforge", credential.RootElement.GetProperty("platform").GetString());
            Assert.AreEqual(projectId.ToString(), credential.RootElement.GetProperty("projectId").GetString());
            Assert.AreEqual(fileId.ToString(), credential.RootElement.GetProperty("fileId").GetString());

            // 模拟整合包只有后续条目失败后的显式重试：已安装 Mod 中的用户修改
            // 不应因重试而被缓存归档再次覆盖。服务必须先校验来源凭证/manifest，
            // 再把该条目标记为“复用”，而不是仅相信任务状态。
            var installedContentPath = Path.Combine(installedPath, "content.json");
            File.WriteAllText(installedContentPath, "user-edit");
            var resumedProgress = new List<CollectionInstallProgress>();
            var resumedResult = await service.InstallCollectionFromArchiveAsync(
                archivePath,
                instanceName,
                resumedProgress.Add,
                gameBasePath: gamePath,
                updateExisting: true,
                resumeInstalledModNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "Content Patcher"
                });

            Assert.IsTrue(resumedResult.IsSuccess, resumedResult.Message);
            Assert.AreEqual("user-edit", File.ReadAllText(installedContentPath));
            Assert.IsTrue(
                resumedProgress.Any(progress =>
                    string.Equals(progress.ModName, "Content Patcher", StringComparison.OrdinalIgnoreCase) &&
                    progress.ModState == CollectionModTaskState.Installed &&
                    progress.ModMessage.Contains("重试时跳过", StringComparison.Ordinal)),
                "已安装 Collection Mod 重试时应复用现有目录并留下可观察的跳过状态");
        }
        finally
        {
            var records = registry.LoadManualInstances();
            records.RemoveAll(record => string.Equals(record.Name, instanceName, StringComparison.OrdinalIgnoreCase));
            registry.SaveManualInstances(records);

            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public async Task CollectionInstall_ShouldInferNexusIdsFromPageAndLogicalFilenameAndReuseCache()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "svl-collection-nexus-page-cache-e2e-" + Guid.NewGuid().ToString("N"));
        var gamePath = Path.Combine(root, "Game");
        var archivePath = Path.Combine(root, "collection.zip");
        var instanceName = "CollectionNexus-" + Guid.NewGuid().ToString("N")[..8];
        var settingsStore = new AppUserSettingsStore(Path.Combine(root, "settings"));
        var registry = new InstanceRegistryStore();
        var modId = 920_000_000L + Random.Shared.Next(1_000_000);
        var fileId = 920_000_000L + Random.Shared.Next(1_000_000);
        var cachePath = NexusDownloadCache.GetCachePath(modId, fileId);
        byte[] previousCache = null;

        try
        {
            if (File.Exists(cachePath))
            {
                previousCache = File.ReadAllBytes(cachePath);
            }

            Directory.CreateDirectory(Path.Combine(
                gamePath,
                "versions",
                "Existing SMAPI 4.5.2"));
            File.WriteAllText(
                Path.Combine(
                    gamePath,
                    "versions",
                    "Existing SMAPI 4.5.2",
                    "StardewModdingAPI.dll"),
                "existing-smapi");

            var logicalFilename = $"File {fileId}_ Content Patcher 2.9.0 2.9.0.zip";
            var modArchive = Path.Combine(root, logicalFilename);
            File.WriteAllBytes(
                modArchive,
                CreateZipBytes(
                    ("Release/Content Patcher/manifest.json",
                        "{\"Name\":\"Content Patcher\",\"UniqueID\":\"Pathoschild.ContentPatcher\",\"Version\":\"2.9.0\"}"),
                    ("Release/Content Patcher/content.json", "{}")));
            NexusDownloadCache.Save(modId, fileId, modArchive, IsZipArchive);

            using (var package = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                WriteArchiveText(
                    package,
                    "Collection Export/collection.json",
                    "{\"info\":{\"name\":\"Nexus Page Collection\"},\"mods\":[" +
                    $"{{\"name\":\"Content Patcher\",\"version\":\"2.9.0\",\"source\":{{" +
                    $"\"type\":\"NexusMods\",\"url\":\"https://www.nexusmods.com/stardewvalley/mods/{modId}?tab=files&nmm=1\"," +
                    $"\"logicalFilename\":\"{logicalFilename}\"}}}}]}}");
            }

            // 不提供 Nexus 凭据或浏览器回退；如果页面 URL 和 logicalFilename
            // 没有在真实 Collection 安装链路中恢复 ModID/FileID，就会错误地
            // 进入联网分支，测试也就无法通过。
            var nxmParser = new SVL.Core.Platform.Abstractions.NxmLinkParser();
            var service = new CollectionInstallService(
                new FixedGameInstallPathLocator(gamePath),
                new TestSmapiInstallService(),
                new HttpDownloadService(settingsStore),
                new RemoteCatalogService(settingsStore),
                settingsStore,
                new NexusModDownloadResolverService(),
                nxmParser,
                new BrowserDownloadFallbackService(
                    nxmParser,
                    new SVL.Core.Platform.Services.ExternalProcessService()),
                new ModpackInstallService(
                    new FixedGameInstallPathLocator(gamePath),
                    new TestSmapiInstallService(),
                    new HttpDownloadService(settingsStore),
                    new RemoteCatalogService(settingsStore),
                    settingsStore,
                    new NexusModDownloadResolverService(),
                    nxmParser));

            var result = await service.InstallCollectionFromArchiveAsync(
                archivePath,
                instanceName,
                onProgress: null,
                gameBasePath: gamePath);

            var installedPath = Path.Combine(
                gamePath,
                "versions",
                instanceName,
                "Mods",
                "Content Patcher");
            var credentialPath = Path.Combine(installedPath, "svl-source.json");
            Assert.IsTrue(result.IsSuccess, $"{result.Message}; failed={string.Join(" | ", result.FailedMods)}");
            Assert.AreEqual(1, result.InstalledMods.Count);
            Assert.AreEqual(0, result.FailedMods.Count);
            Assert.IsTrue(File.Exists(Path.Combine(installedPath, "manifest.json")));
            Assert.IsTrue(File.Exists(credentialPath));

            using var credential = JsonDocument.Parse(File.ReadAllText(credentialPath));
            Assert.AreEqual("NexusMods", credential.RootElement.GetProperty("platform").GetString());
            Assert.AreEqual(modId.ToString(), credential.RootElement.GetProperty("projectId").GetString());
            Assert.AreEqual(fileId.ToString(), credential.RootElement.GetProperty("fileId").GetString());
        }
        finally
        {
            try
            {
                if (previousCache == null)
                {
                    if (File.Exists(cachePath))
                    {
                        File.Delete(cachePath);
                    }
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                    File.WriteAllBytes(cachePath, previousCache);
                }
            }
            catch
            {
                // 测试清理不应掩盖主体断言。
            }

            var records = registry.LoadManualInstances();
            records.RemoveAll(record => string.Equals(
                record.Name,
                instanceName,
                StringComparison.OrdinalIgnoreCase));
            registry.SaveManualInstances(records);

            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public async Task CollectionInstall_ShouldParseArchiveAndFlattenBundledMod()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-collection-archive-e2e-" + Guid.NewGuid().ToString("N"));
        var gamePath = Path.Combine(root, "Game");
        var archivePath = Path.Combine(root, "collection.zip");
        var instanceName = "Collection-" + Guid.NewGuid().ToString("N")[..8];
        var settingsStore = new AppUserSettingsStore(Path.Combine(root, "settings"));
        var registry = new InstanceRegistryStore();

        try
        {
            Directory.CreateDirectory(Path.Combine(gamePath, "versions", "Existing SMAPI 4.5.2"));
            File.WriteAllText(
                Path.Combine(gamePath, "versions", "Existing SMAPI 4.5.2", "StardewModdingAPI.dll"),
                "existing-smapi");

            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                WriteArchiveText(
                    archive,
                    "Collection Export/collection.json",
                    "{\"info\":{\"name\":\"Collection Export\"},\"mods\":[]}");
                WriteArchiveText(
                    archive,
                    "Collection Export/bundled/Release/ActualMod/manifest.json",
                    "{\"Name\":\"Actual Mod\",\"UniqueID\":\"Example.Actual\",\"Version\":\"1.0.0\"}");
                WriteArchiveText(
                    archive,
                    "Collection Export/bundled/Release/ActualMod/content.json",
                    "{}");
            }

            var nxmParser = new SVL.Core.Platform.Abstractions.NxmLinkParser();
            var service = new CollectionInstallService(
                new FixedGameInstallPathLocator(gamePath),
                new TestSmapiInstallService(),
                new HttpDownloadService(settingsStore),
                new RemoteCatalogService(settingsStore),
                settingsStore,
                new NexusModDownloadResolverService(),
                nxmParser,
                new BrowserDownloadFallbackService(
                    nxmParser,
                    new SVL.Core.Platform.Services.ExternalProcessService()),
                new ModpackInstallService(
                    new FixedGameInstallPathLocator(gamePath),
                    new TestSmapiInstallService(),
                    new HttpDownloadService(settingsStore),
                    new RemoteCatalogService(settingsStore),
                    settingsStore,
                    new NexusModDownloadResolverService(),
                    nxmParser));

            var result = await service.InstallCollectionFromArchiveAsync(
                archivePath,
                instanceName,
                onProgress: null,
                gameBasePath: gamePath);

            var installedPath = Path.Combine(gamePath, "versions", instanceName, "Mods", "ActualMod");
            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual(1, result.InstalledMods.Count);
            Assert.AreEqual(0, result.FailedMods.Count);
            Assert.IsTrue(File.Exists(Path.Combine(installedPath, "manifest.json")));
            Assert.IsTrue(File.Exists(Path.Combine(installedPath, "content.json")));
            Assert.IsFalse(Directory.Exists(Path.Combine(installedPath, "Release")));
            Assert.IsFalse(Directory.Exists(Path.Combine(gamePath, "versions", instanceName, "Mods", "Actual Mod")));
        }
        finally
        {
            var records = registry.LoadManualInstances();
            records.RemoveAll(record => string.Equals(record.Name, instanceName, StringComparison.OrdinalIgnoreCase));
            registry.SaveManualInstances(records);

            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public async Task CollectionInstall_ShouldParseSevenZipArchiveAndFlattenBundledMod()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-collection-7z-archive-e2e-" + Guid.NewGuid().ToString("N"));
        var gamePath = Path.Combine(root, "Game");
        var archivePath = Path.Combine(root, "collection.7z");
        var instanceName = "Collection7z-" + Guid.NewGuid().ToString("N")[..8];
        var settingsStore = new AppUserSettingsStore(Path.Combine(root, "settings"));
        var registry = new InstanceRegistryStore();

        try
        {
            // 让 SMAPI 解析器完全离线命中已有实例，测试只验证 Collection 7z
            // 解压、清单定位和 bundled Mod 安装，不依赖真实网络目录。
            var existingSmapiPath = Path.Combine(gamePath, "versions", "Existing SMAPI 4.5.2");
            Directory.CreateDirectory(existingSmapiPath);
            File.WriteAllText(Path.Combine(existingSmapiPath, "StardewModdingAPI.dll"), "existing-smapi");

            using (var writer = SevenZipWriter.OpenWriter(
                       archivePath,
                       new SevenZipWriterOptions(SharpCompress.Common.CompressionType.LZMA2)))
            {
                WriteSevenZipText(
                    writer,
                    "Collection Export/collection.json",
                    "{\"info\":{\"name\":\"Collection 7z Export\"},\"mods\":[]}");
                WriteSevenZipText(
                    writer,
                    "Collection Export/bundled/Release/ActualMod/manifest.json",
                    "{\"Name\":\"Actual Mod\",\"UniqueID\":\"Example.Actual\",\"Version\":\"1.0.0\"}");
                WriteSevenZipText(writer, "Collection Export/bundled/Release/ActualMod/content.json", "{}");
            }

            var nxmParser = new SVL.Core.Platform.Abstractions.NxmLinkParser();
            var service = new CollectionInstallService(
                new FixedGameInstallPathLocator(gamePath),
                new TestSmapiInstallService(),
                new HttpDownloadService(settingsStore),
                new RemoteCatalogService(settingsStore),
                settingsStore,
                new NexusModDownloadResolverService(),
                nxmParser,
                new BrowserDownloadFallbackService(
                    nxmParser,
                    new SVL.Core.Platform.Services.ExternalProcessService()),
                new ModpackInstallService(
                    new FixedGameInstallPathLocator(gamePath),
                    new TestSmapiInstallService(),
                    new HttpDownloadService(settingsStore),
                    new RemoteCatalogService(settingsStore),
                    settingsStore,
                    new NexusModDownloadResolverService(),
                    nxmParser));

            var result = await service.InstallCollectionFromArchiveAsync(
                archivePath,
                instanceName,
                onProgress: null,
                gameBasePath: gamePath);

            var installedPath = Path.Combine(gamePath, "versions", instanceName, "Mods", "ActualMod");
            Assert.IsTrue(result.IsSuccess, $"{result.Message}; failed={string.Join(" | ", result.FailedMods)}");
            Assert.AreEqual(1, result.InstalledMods.Count);
            Assert.AreEqual(0, result.FailedMods.Count);
            Assert.IsTrue(File.Exists(Path.Combine(installedPath, "manifest.json")));
            Assert.IsTrue(File.Exists(Path.Combine(installedPath, "content.json")));
            Assert.IsFalse(Directory.Exists(Path.Combine(installedPath, "Release")));
            Assert.IsFalse(Directory.Exists(Path.Combine(
                gamePath,
                "versions",
                instanceName,
                "Mods",
                "Actual Mod")));
        }
        finally
        {
            var records = registry.LoadManualInstances();
            records.RemoveAll(record => string.Equals(record.Name, instanceName, StringComparison.OrdinalIgnoreCase));
            registry.SaveManualInstances(records);

            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public async Task CollectionInstall_ShouldReportBundledFailureAlongsideSuccessfulMod()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-collection-bundled-failure-e2e-" + Guid.NewGuid().ToString("N"));
        var gamePath = Path.Combine(root, "Game");
        var archivePath = Path.Combine(root, "collection.zip");
        var instanceName = "CollectionBundledFailure-" + Guid.NewGuid().ToString("N")[..8];
        var settingsStore = new AppUserSettingsStore(Path.Combine(root, "settings"));
        var registry = new InstanceRegistryStore();

        try
        {
            Directory.CreateDirectory(Path.Combine(gamePath, "versions", "Existing SMAPI 4.5.2"));
            File.WriteAllText(
                Path.Combine(gamePath, "versions", "Existing SMAPI 4.5.2", "StardewModdingAPI.dll"),
                "existing-smapi");

            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                WriteArchiveText(
                    archive,
                    "Collection Export/collection.json",
                    "{\"info\":{\"name\":\"Collection Export\"},\"mods\":[]}");
                WriteArchiveText(
                    archive,
                    "Collection Export/bundled/Valid Release/ActualMod/manifest.json",
                    "{\"Name\":\"Actual Mod\",\"UniqueID\":\"Example.Actual\",\"Version\":\"1.0.0\"}");
                WriteArchiveText(
                    archive,
                    "Collection Export/bundled/Valid Release/ActualMod/content.json",
                    "{}");
                WriteArchiveText(
                    archive,
                    "Collection Export/bundled/Broken Release/readme.txt",
                    "missing manifest");
            }

            var nxmParser = new SVL.Core.Platform.Abstractions.NxmLinkParser();
            var service = new CollectionInstallService(
                new FixedGameInstallPathLocator(gamePath),
                new TestSmapiInstallService(),
                new HttpDownloadService(settingsStore),
                new RemoteCatalogService(settingsStore),
                settingsStore,
                new NexusModDownloadResolverService(),
                nxmParser,
                new BrowserDownloadFallbackService(
                    nxmParser,
                    new SVL.Core.Platform.Services.ExternalProcessService()),
                new ModpackInstallService(
                    new FixedGameInstallPathLocator(gamePath),
                    new TestSmapiInstallService(),
                    new HttpDownloadService(settingsStore),
                    new RemoteCatalogService(settingsStore),
                    settingsStore,
                    new NexusModDownloadResolverService(),
                    nxmParser));

            var result = await service.InstallCollectionFromArchiveAsync(
                archivePath,
                instanceName,
                onProgress: null,
                gameBasePath: gamePath);

            var installedPath = Path.Combine(gamePath, "versions", instanceName, "Mods", "ActualMod");
            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual(1, result.InstalledMods.Count);
            Assert.AreEqual(1, result.FailedMods.Count);
            StringAssert.Contains(result.FailedMods[0], "Broken Release");
            StringAssert.Contains(result.FailedMods[0], "manifest.json");
            Assert.IsTrue(File.Exists(Path.Combine(installedPath, "manifest.json")));
        }
        finally
        {
            var records = registry.LoadManualInstances();
            records.RemoveAll(record => string.Equals(record.Name, instanceName, StringComparison.OrdinalIgnoreCase));
            registry.SaveManualInstances(records);

            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public async Task CollectionInstall_ShouldReportMissingSourceReasonForRetry()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-collection-missing-source-e2e-" + Guid.NewGuid().ToString("N"));
        var gamePath = Path.Combine(root, "Game");
        var archivePath = Path.Combine(root, "collection.zip");
        var instanceName = "CollectionMissing-" + Guid.NewGuid().ToString("N")[..8];
        var settingsStore = new AppUserSettingsStore(Path.Combine(root, "settings"));
        var registry = new InstanceRegistryStore();

        try
        {
            Directory.CreateDirectory(Path.Combine(gamePath, "versions", "Existing SMAPI 4.5.2"));
            File.WriteAllText(
                Path.Combine(gamePath, "versions", "Existing SMAPI 4.5.2", "StardewModdingAPI.dll"),
                "existing-smapi");

            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                WriteArchiveText(
                    archive,
                    "collection.json",
                    "{\"info\":{\"name\":\"Missing Source Collection\"},\"mods\":[" +
                    "{\"name\":\"Missing Source Mod\"}]}" );
            }

            var nxmParser = new SVL.Core.Platform.Abstractions.NxmLinkParser();
            var service = new CollectionInstallService(
                new FixedGameInstallPathLocator(gamePath),
                new TestSmapiInstallService(),
                new HttpDownloadService(settingsStore),
                new RemoteCatalogService(settingsStore),
                settingsStore,
                new NexusModDownloadResolverService(),
                nxmParser,
                new BrowserDownloadFallbackService(
                    nxmParser,
                    new SVL.Core.Platform.Services.ExternalProcessService()),
                new ModpackInstallService(
                    new FixedGameInstallPathLocator(gamePath),
                    new TestSmapiInstallService(),
                    new HttpDownloadService(settingsStore),
                    new RemoteCatalogService(settingsStore),
                    settingsStore,
                    new NexusModDownloadResolverService(),
                    nxmParser));

            var result = await service.InstallCollectionFromArchiveAsync(
                archivePath,
                instanceName,
                onProgress: null,
                gameBasePath: gamePath);

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual(0, result.InstalledMods.Count);
            Assert.AreEqual(1, result.FailedMods.Count);
            StringAssert.Contains(result.FailedMods[0], "Missing Source Mod");
            StringAssert.Contains(result.FailedMods[0], "缺少来源信息");
        }
        finally
        {
            var records = registry.LoadManualInstances();
            records.RemoveAll(record => string.Equals(record.Name, instanceName, StringComparison.OrdinalIgnoreCase));
            registry.SaveManualInstances(records);

            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public async Task CollectionInstall_ShouldReportManualSourceAndSkipUnsafeUrl()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "svl-collection-manual-source-e2e-" + Guid.NewGuid().ToString("N"));
        var gamePath = Path.Combine(root, "Game");
        var archivePath = Path.Combine(root, "collection.zip");
        var instanceName = "CollectionManual-" + Guid.NewGuid().ToString("N")[..8];
        var settingsStore = new AppUserSettingsStore(Path.Combine(root, "settings"));
        var registry = new InstanceRegistryStore();
        var sourceUrl = "https://www.nexusmods.com/stardewvalley/mods/12345";
        var progress = new List<CollectionInstallProgress>();

        try
        {
            Directory.CreateDirectory(Path.Combine(gamePath, "versions", "Existing SMAPI 4.5.2"));
            File.WriteAllText(
                Path.Combine(gamePath, "versions", "Existing SMAPI 4.5.2", "StardewModdingAPI.dll"),
                "existing-smapi");

            using (var package = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                WriteArchiveText(
                    package,
                    "collection.json",
                    "{\"info\":{\"name\":\"Manual Source Collection\"},\"mods\":[" +
                    "{\"name\":\"Manual External Mod\",\"version\":\"1.0.0\",\"source\":{" +
                    $"\"type\":\"manual\",\"modId\":12345,\"fileId\":67890,\"url\":\"{sourceUrl}\"}}}}]}}");
            }

            var nxmParser = new SVL.Core.Platform.Abstractions.NxmLinkParser();
            var service = new CollectionInstallService(
                new FixedGameInstallPathLocator(gamePath),
                new TestSmapiInstallService(),
                new HttpDownloadService(settingsStore),
                new RemoteCatalogService(settingsStore),
                settingsStore,
                new NexusModDownloadResolverService(),
                nxmParser,
                new BrowserDownloadFallbackService(
                    nxmParser,
                    new SVL.Core.Platform.Services.ExternalProcessService()),
                new ModpackInstallService(
                    new FixedGameInstallPathLocator(gamePath),
                    new TestSmapiInstallService(),
                    new HttpDownloadService(settingsStore),
                    new RemoteCatalogService(settingsStore),
                    settingsStore,
                    new NexusModDownloadResolverService(),
                    nxmParser));

            var result = await service.InstallCollectionFromArchiveAsync(
                archivePath,
                instanceName,
                progress.Add,
                gameBasePath: gamePath);

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.AreEqual(0, result.InstalledMods.Count);
            Assert.AreEqual(1, result.FailedMods.Count);
            StringAssert.Contains(result.FailedMods[0], "需要手动下载");
            StringAssert.Contains(result.FailedMods[0], sourceUrl);

            var failedProgress = progress.Last(item =>
                item.ModState == CollectionModTaskState.Failed);
            Assert.IsTrue(failedProgress.ModRequiresManualAction);
            Assert.AreEqual(sourceUrl, failedProgress.ModSourceUrl);
        }
        finally
        {
            var records = registry.LoadManualInstances();
            records.RemoveAll(record => string.Equals(
                record.Name,
                instanceName,
                StringComparison.OrdinalIgnoreCase));
            registry.SaveManualInstances(records);

            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public async Task DownloadInstall_ShouldFlattenOuterModDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-mod-install-shape-test-" + Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(root, "release.zip");
        var modsPath = Path.Combine(root, "Mods");

        try
        {
            Directory.CreateDirectory(root);
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                WriteArchiveText(archive, "Release-1.0/ActualMod/manifest.json", "{\"Name\":\"Example.Mod\"}");
                WriteArchiveText(archive, "Release-1.0/ActualMod/content.json", "{}");
            }

            var service = new DownloadInstallService(new TestGameInstallPathLocator())
            {
                CurrentModsPathResolver = () => modsPath
            };

            var result = await service.InstallAsync(
                archivePath,
                "Example Mod",
                sourcePlatform: "Curseforge",
                sourceProjectId: 12345,
                sourceFileId: 67890);
            var installedPath = Path.Combine(modsPath, "ActualMod");

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.IsTrue(File.Exists(Path.Combine(installedPath, "manifest.json")));
            Assert.IsTrue(File.Exists(Path.Combine(installedPath, "content.json")));
            Assert.IsFalse(Directory.Exists(Path.Combine(installedPath, "Release-1.0")));

            var sourceJson = File.ReadAllText(Path.Combine(installedPath, "svl-source.json"));
            StringAssert.Contains(sourceJson, "\"platform\": \"Curseforge\"");
            StringAssert.Contains(sourceJson, "\"projectId\": \"12345\"");
            StringAssert.Contains(sourceJson, "\"fileId\": \"67890\"");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void DownloadInstall_BackupShouldUseModManagementBackupDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-mod-backup-root-test-" + Guid.NewGuid().ToString("N"));
        var modsPath = Path.Combine(root, "Mods");
        var existingModPath = Path.Combine(modsPath, "Content Patcher");
        try
        {
            Directory.CreateDirectory(existingModPath);
            File.WriteAllText(Path.Combine(existingModPath, "manifest.json"), "{\"Name\":\"Content Patcher\"}");

            var service = new DownloadInstallService(new TestGameInstallPathLocator());
            var success = service.TryBackupExistingModDirectory(
                modsPath,
                existingModPath,
                out var backupPath);

            Assert.IsTrue(success);
            Assert.IsTrue(backupPath.StartsWith(
                Path.Combine(root, "ModsBackup") + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(File.Exists(Path.Combine(backupPath, "manifest.json")));
            Assert.IsTrue(File.Exists(Path.Combine(backupPath, ".svl-backup.json")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void DownloadInstall_UpdateBackupShouldWriteChainAndMoveOriginalDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-mod-update-chain-test-" + Guid.NewGuid().ToString("N"));
        var modsPath = Path.Combine(root, "Mods");
        var existingModPath = Path.Combine(modsPath, "Old Mod Folder");
        try
        {
            Directory.CreateDirectory(existingModPath);
            File.WriteAllText(
                Path.Combine(existingModPath, "manifest.json"),
                "{\"Name\":\"New Mod Name\",\"UniqueID\":\"test.new-mod\",\"Version\":\"1.0.0\"}");

            var service = new DownloadInstallService(new TestGameInstallPathLocator());
            var success = service.TryBackupExistingModDirectory(
                modsPath,
                existingModPath,
                ["New Mod Folder"],
                "New Mod Folder.zip",
                out var backupPath);

            Assert.IsTrue(success);
            Assert.IsFalse(Directory.Exists(existingModPath), "更新备份成功后旧目录应已移入 ModsBackup");
            Assert.IsTrue(File.Exists(Path.Combine(backupPath, ".svl-update-chain.json")));

            using var chain = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(backupPath, ".svl-update-chain.json")));
            Assert.AreEqual("Old Mod Folder", chain.RootElement.GetProperty("OriginalFolderName").GetString());
            CollectionAssert.Contains(
                chain.RootElement.GetProperty("ReplacementFolderNames")
                    .EnumerateArray()
                    .Select(item => item.GetString())
                    .ToList(),
                "New Mod Folder");

            using var metadata = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(backupPath, ".svl-backup.json")));
            Assert.IsTrue(metadata.RootElement.TryGetProperty("UpdateChain", out _));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public async Task LegacyCollectionInstall_ShouldSkipOuterNameOnlyManifest()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-collection-legacy-shape-test-" + Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(root, "collection-part.zip");
        var modsPath = Path.Combine(root, "Mods");

        try
        {
            Directory.CreateDirectory(root);
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                // 外层是发布包元数据，不应成为 Mods/Release；内层才是实际 Mod。
                WriteArchiveText(
                    archive,
                    "Release/manifest.json",
                    "{\"Name\":\"Release Package\",\"Version\":\"1.0.0\"}");
                WriteArchiveText(
                    archive,
                    "Release/cf-1012214-5312529/manifest.json",
                    "{\"Name\":\"Actual Mod\",\"UniqueID\":\"Example.Actual\",\"Version\":\"1.0.0\"}");
                WriteArchiveText(archive, "Release/cf-1012214-5312529/content.json", "{}");
            }

            var service = new DownloadInstallService(new TestGameInstallPathLocator())
            {
                CurrentModsPathResolver = () => modsPath
            };

            var result = await service.InstallCollectionAsync([archivePath], "Legacy Collection");

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.IsTrue(File.Exists(Path.Combine(modsPath, "Actual Mod", "manifest.json")));
            Assert.IsFalse(Directory.Exists(Path.Combine(modsPath, "Release")));
            Assert.IsFalse(Directory.Exists(Path.Combine(modsPath, "Actual Mod", "Release")));
            Assert.IsFalse(Directory.Exists(Path.Combine(modsPath, "cf-1012214-5312529")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public async Task DownloadInstall_ShouldPreserveExistingModWhenArchiveIsInvalid()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-mod-install-preserve-test-" + Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(root, "ActualMod.zip");
        var modsPath = Path.Combine(root, "Mods");
        var existingModPath = Path.Combine(modsPath, "ActualMod");
        var existingManifestPath = Path.Combine(existingModPath, "manifest.json");

        try
        {
            Directory.CreateDirectory(existingModPath);
            File.WriteAllText(
                existingManifestPath,
                "{\"Name\":\"Actual Mod\",\"UniqueID\":\"Example.ActualMod\",\"Version\":\"1.0.0\"}");
            File.WriteAllText(Path.Combine(existingModPath, "old-content.json"), "keep");

            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                WriteArchiveText(archive, "error.html", "This is not a valid Mod package");
            }

            var service = new DownloadInstallService(new TestGameInstallPathLocator())
            {
                CurrentModsPathResolver = () => modsPath
            };

            var result = await service.InstallAsync(archivePath, "ActualMod");

            Assert.IsFalse(result.IsSuccess);
            Assert.IsTrue(File.Exists(existingManifestPath));
            Assert.AreEqual("keep", File.ReadAllText(Path.Combine(existingModPath, "old-content.json")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public async Task DownloadInstall_ShouldUseManifestNameWhenManifestIsAtArchiveRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-mod-install-name-test-" + Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(root, "File 7448774_ Content Patcher 2.9.0 2.9.0.zip");
        var modsPath = Path.Combine(root, "Mods");

        try
        {
            Directory.CreateDirectory(root);
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                WriteArchiveText(archive, "manifest.json", "{\"Name\":\"Content Patcher\",\"UniqueID\":\"Pathoschild.ContentPatcher\"}");
                WriteArchiveText(archive, "content.json", "{}");
            }

            var service = new DownloadInstallService(new TestGameInstallPathLocator())
            {
                CurrentModsPathResolver = () => modsPath
            };

            var sourceUrl = "https://downloads.example.test/content-patcher.zip";
            var result = await service.InstallAsync(
                archivePath,
                Path.GetFileName(archivePath),
                sourceDownloadUrl: sourceUrl,
                sourceFileName: Path.GetFileName(archivePath));
            var installedPath = Path.Combine(modsPath, "Content Patcher");

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.IsTrue(File.Exists(Path.Combine(installedPath, "manifest.json")));
            Assert.IsTrue(File.Exists(Path.Combine(installedPath, "content.json")));
            Assert.IsFalse(Directory.Exists(Path.Combine(modsPath, Path.GetFileNameWithoutExtension(archivePath))));
            using var source = JsonDocument.Parse(File.ReadAllText(Path.Combine(installedPath, "svl-source.json")));
            Assert.AreEqual(sourceUrl, source.RootElement.GetProperty("downloadUrl").GetString());
            Assert.AreEqual(Path.GetFileName(archivePath), source.RootElement.GetProperty("fileName").GetString());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public async Task DownloadInstall_ShouldReadUtf16ManifestAndFlattenPackage()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-mod-install-utf16-test-" + Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(root, "release.zip");
        var modsPath = Path.Combine(root, "Mods");

        try
        {
            Directory.CreateDirectory(root);
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                using (var manifest = new StreamWriter(
                           archive.CreateEntry("Release/ActualMod/manifest.json").Open(),
                           new UnicodeEncoding(bigEndian: false, byteOrderMark: true)))
                {
                    manifest.Write("{\"Name\":\"UTF16 Mod\",\"UniqueID\":\"Example.UTF16\",\"Version\":\"1.0.0\"}");
                }

                WriteArchiveText(archive, "Release/ActualMod/content.json", "{}");
            }

            var service = new DownloadInstallService(new TestGameInstallPathLocator())
            {
                CurrentModsPathResolver = () => modsPath
            };

            var result = await service.InstallAsync(archivePath, "File 7448774_ UTF16 Mod 1.0.0.zip");
            var installedPath = Path.Combine(modsPath, "ActualMod");

            Assert.IsTrue(result.IsSuccess, result.Message);
            Assert.IsTrue(File.Exists(Path.Combine(installedPath, "manifest.json")));
            Assert.IsTrue(File.Exists(Path.Combine(installedPath, "content.json")));
            Assert.IsFalse(Directory.Exists(Path.Combine(installedPath, "Release")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void DownloadInstallValidation_ShouldAcceptCaseInsensitiveAndNumericManifestFields()
    {
        var root = Path.Combine(Path.GetTempPath(), "svl-mod-validation-shape-test-" + Guid.NewGuid().ToString("N"));
        var modDirectory = Path.Combine(root, "ExampleMod");
        try
        {
            Directory.CreateDirectory(modDirectory);
            File.WriteAllText(
                Path.Combine(modDirectory, "Manifest.JSON"),
                "{\"uniqueid\":\"Example.CaseInsensitive\",\"version\":2.9}");

            var validator = typeof(DownloadInstallService).GetMethod(
                "ValidateInstalledMods",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(validator);

            var result = validator!.Invoke(null, [new[] { modDirectory }]);
            Assert.IsNotNull(result);
            Assert.IsTrue((bool)result!.GetType().GetProperty("IsValid")!.GetValue(result)!);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private sealed class TestGameInstallPathLocator : SVL.Core.Platform.Abstractions.IGameInstallPathLocator
    {
        public string TryLocateSteamStardewPath() => null;

        public string TryLocateGogStardewPath() => null;
    }

    private sealed class FixedGameInstallPathLocator(string gamePath)
        : SVL.Core.Platform.Abstractions.IGameInstallPathLocator
    {
        public string TryLocateSteamStardewPath() => gamePath;

        public string TryLocateGogStardewPath() => null;
    }

    private sealed class TestSmapiInstallService : SVL.Core.Platform.Abstractions.ISmapiInstallService
    {
        public List<bool> UpdateExistingCalls { get; } = [];

        public Task<SVL.Core.Platform.Abstractions.SmapiInstallResult> InstallFromZipAsync(
            string zipFilePath,
            string gameBasePath,
            string instanceName,
            CancellationToken cancellationToken = default,
            Action<string> logger = null,
            Func<string, string, CancellationToken, Task> zipExtractor = null,
            bool updateExisting = false)
        {
            UpdateExistingCalls.Add(updateExisting);
            var versionRoot = Path.Combine(gameBasePath, "versions", instanceName);
            Directory.CreateDirectory(versionRoot);
            File.WriteAllText(Path.Combine(versionRoot, "StardewModdingAPI.dll"), "smapi");
            return Task.FromResult(
                SVL.Core.Platform.Abstractions.SmapiInstallResult.Success(versionRoot, versionRoot));
        }
    }

    private sealed class TruncatedResponseServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly byte[] _payload;

        public TruncatedResponseServer(byte[] payload)
        {
            _payload = payload;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Url = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/transient.zip");
        }

        public Uri Url { get; }

        public async Task ServeAsync(CancellationToken cancellationToken)
        {
            // 初次下载和底层重试各包含一次 Range 探测与一次实际下载。
            for (var requestIndex = 0; requestIndex < 4; requestIndex++)
            {
                using var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                await using var stream = client.GetStream();
                var requestBuffer = new byte[4096];
                var requestLength = 0;
                while (requestLength < requestBuffer.Length)
                {
                    var read = await stream.ReadAsync(
                        requestBuffer.AsMemory(requestLength),
                        cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    requestLength += read;
                    if (requestLength >= 4 &&
                        Encoding.ASCII.GetString(requestBuffer, 0, requestLength)
                            .Contains("\r\n\r\n", StringComparison.Ordinal))
                    {
                        break;
                    }
                }

                var header = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\n" +
                    $"Content-Type: application/zip\r\n" +
                    $"Content-Length: {_payload.Length}\r\n" +
                    "Connection: close\r\n\r\n");
                await stream.WriteAsync(header, cancellationToken);

                if (requestIndex == 1)
                {
                    // 声明完整长度但只写入半包，模拟 CDN 的 ResponseEnded/EOF。
                    await stream.WriteAsync(
                        _payload.AsMemory(0, Math.Max(1, _payload.Length / 2)),
                        cancellationToken);
                }
                else
                {
                    await stream.WriteAsync(_payload, cancellationToken);
                }

                await stream.FlushAsync(cancellationToken);
            }
        }

        public void Dispose()
        {
            _listener.Stop();
        }
    }

    private sealed class LocalArchiveServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly byte[] _payload;

        public LocalArchiveServer(byte[] payload)
        {
            _payload = payload;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Url = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/actual-mod.zip");
        }

        public Uri Url { get; }

        public async Task ServeOnceAsync(CancellationToken cancellationToken)
        {
            // HttpDownloadService 先发送一个 Range=0-0 探测请求，再按默认 4
            // 个分片发送实际请求；测试服务需要完整响应这一组请求。
            for (var requestIndex = 0; requestIndex < 5; requestIndex++)
            {
                using var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                await using var stream = client.GetStream();
                var requestBuffer = new byte[4096];
                var requestLength = 0;
                while (requestLength < requestBuffer.Length)
                {
                    var read = await stream.ReadAsync(
                        requestBuffer.AsMemory(requestLength),
                        cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    requestLength += read;
                    if (requestLength >= 4 &&
                        Encoding.ASCII.GetString(requestBuffer, 0, requestLength).Contains("\r\n\r\n", StringComparison.Ordinal))
                    {
                        break;
                    }
                }

                var request = Encoding.ASCII.GetString(requestBuffer, 0, requestLength);
                var rangeStart = 0L;
                var rangeEnd = _payload.Length - 1L;
                var rangeHeaderIndex = request.IndexOf("Range: bytes=", StringComparison.OrdinalIgnoreCase);
                if (rangeHeaderIndex >= 0)
                {
                    var rangeStartIndex = rangeHeaderIndex + "Range: bytes=".Length;
                    var rangeTextEnd = request.IndexOf('\r', rangeStartIndex);
                    var rangeText = request[rangeStartIndex..(
                        rangeTextEnd > rangeStartIndex ? rangeTextEnd : request.Length)];
                    var dashIndex = rangeText.IndexOf('-');
                    if (dashIndex > 0)
                    {
                        _ = long.TryParse(rangeText[..dashIndex], out rangeStart);
                        _ = long.TryParse(rangeText[(dashIndex + 1)..], out rangeEnd);
                    }
                }

                rangeStart = Math.Clamp(rangeStart, 0, _payload.Length - 1L);
                rangeEnd = Math.Clamp(rangeEnd, rangeStart, _payload.Length - 1L);
                var length = checked((int)(rangeEnd - rangeStart + 1));
                var responseBody = new ReadOnlyMemory<byte>(
                    _payload,
                    checked((int)rangeStart),
                    length);
                var isPartial = rangeHeaderIndex >= 0;
                var header = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {(isPartial ? "206 Partial Content" : "200 OK")}\r\n" +
                    $"Content-Type: application/zip\r\nContent-Length: {length}\r\n" +
                    (isPartial ? $"Content-Range: bytes {rangeStart}-{rangeEnd}/{_payload.Length}\r\n" : string.Empty) +
                    "Accept-Ranges: bytes\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header, cancellationToken);
                await stream.WriteAsync(responseBody, cancellationToken);
            }
        }

        public void Dispose()
        {
            _listener.Stop();
        }
    }

    private static byte[] CreateZipBytes(params (string Path, string Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                WriteArchiveText(archive, path, content);
            }
        }

        return stream.ToArray();
    }

    private static bool IsSmapiArchivePackage(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            return archive.Entries.Any(entry =>
                entry.Name.Equals("install.dat", StringComparison.OrdinalIgnoreCase) &&
                entry.FullName.Contains("internal", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static bool IsZipArchive(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            return archive.Entries.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsCollectionArchive(string path)
    {
        var detection = ModpackTypeDetector.Detect(path);
        if (!string.IsNullOrWhiteSpace(detection.TempExtractPath))
        {
            ModpackTypeDetector.CleanupTempDirectory(detection.TempExtractPath);
        }

        return detection.Type == ModpackType.NexusCollection;
    }

    private static bool IsArchiveWithManifest(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            return archive.Entries.Any(entry =>
                entry.FullName.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static void WriteArchiveText(ZipArchive archive, string path, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(path).Open(), Encoding.UTF8);
        writer.Write(content);
    }

    private static void WriteSevenZipText(IWriter writer, string entryName, string content)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        writer.Write(entryName, stream, null);
    }

    private static void WriteArchiveTextWithEncoding(
        ZipArchive archive,
        string path,
        string content,
        Encoding encoding)
    {
        using var writer = new StreamWriter(archive.CreateEntry(path).Open(), encoding);
        writer.Write(content);
    }
}
