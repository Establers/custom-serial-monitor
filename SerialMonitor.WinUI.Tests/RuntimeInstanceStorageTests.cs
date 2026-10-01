using SerialMonitor.WinUI.Infrastructure;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace SerialMonitor.WinUI.Tests;

public sealed class RuntimeInstanceStorageTests
{
    [Fact]
    public void LaunchesHaveDistinctBrowserAndDiagnosticPathsEvenInOneProcess()
    {
        var root = Path.Combine(Path.GetTempPath(), "SerialInstancePaths");
        var first = new RuntimeInstanceStorage(root);
        var second = new RuntimeInstanceStorage(root);
        Assert.NotEqual(first.InstanceId, second.InstanceId);
        Assert.NotEqual(first.WebViewUserDataFolder, second.WebViewUserDataFolder);
        Assert.NotEqual(first.DiagnosticsDirectory, second.DiagnosticsDirectory);
        Assert.Equal(RuntimeInstanceStorage.Current.DiagnosticsDirectory, RuntimeDiagnostics.DirectoryPath);
        Assert.Equal(RuntimeDiagnostics.DirectoryPath, Path.GetDirectoryName(RuntimeDiagnostics.LastErrorPath));
    }

    [Fact]
    public async Task StartupAndShutdownNeverDeleteAnotherActiveInstancesCache()
    {
        var root = Path.Combine(Path.GetTempPath(), $"SerialInstance_{Guid.NewGuid():N}");
        var first = new RuntimeInstanceStorage(root);
        var second = new RuntimeInstanceStorage(root);
        try
        {
            await first.PrepareWebViewDirectoryAsync(default);
            var firstCache = Path.Combine(first.WebViewUserDataFolder, "cache.txt");
            await File.WriteAllTextAsync(firstCache, "first instance");
            await second.PrepareWebViewDirectoryAsync(default);
            Assert.Equal("first instance", await File.ReadAllTextAsync(firstCache));
            await second.ReleaseWebViewDirectoryAsync(browserExited: true);
            Assert.True(Directory.Exists(first.WebViewUserDataFolder));
            Assert.False(Directory.Exists(second.WebViewUserDataFolder));
        }
        finally
        {
            await first.ReleaseWebViewDirectoryAsync(true);
            await second.ReleaseWebViewDirectoryAsync(true);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task AbandonedCacheIsCleanedWithoutTouchingSettingsProfilesOrDiagnostics()
    {
        var root = Path.Combine(Path.GetTempPath(), $"SerialInstance_{Guid.NewGuid():N}");
        var first = new RuntimeInstanceStorage(root);
        var second = new RuntimeInstanceStorage(root);
        try
        {
            await first.PrepareWebViewDirectoryAsync(default);
            await first.ReleaseWebViewDirectoryAsync(browserExited: false);
            Directory.CreateDirectory(Path.Combine(root, "profiles"));
            Directory.CreateDirectory(first.DiagnosticsDirectory);
            var profile = Path.Combine(root, "profiles", "default.json");
            var diagnostic = Path.Combine(first.DiagnosticsDirectory, "last_runtime_error.txt");
            await File.WriteAllTextAsync(profile, "profile");
            await File.WriteAllTextAsync(diagnostic, "diagnostic");
            await second.PrepareWebViewDirectoryAsync(default);
            Assert.False(Directory.Exists(first.WebViewUserDataFolder));
            Assert.Equal("profile", await File.ReadAllTextAsync(profile));
            Assert.Equal("diagnostic", await File.ReadAllTextAsync(diagnostic));
        }
        finally
        {
            await first.ReleaseWebViewDirectoryAsync(true);
            await second.ReleaseWebViewDirectoryAsync(true);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LockedCachePreservesOwnerMarkerAndIsRemovedAfterTheLockIsReleased(bool deleteDuringShutdown)
    {
        var root = Path.Combine(Path.GetTempPath(), $"SerialInstance_{Guid.NewGuid():N}");
        var first = new RuntimeInstanceStorage(root);
        var second = new RuntimeInstanceStorage(root);
        FileStream? lockedCache = null;
        try
        {
            await first.PrepareWebViewDirectoryAsync(default);
            var cacheDirectory = Path.Combine(first.WebViewUserDataFolder, "cache");
            Directory.CreateDirectory(cacheDirectory);
            var cachePath = Path.Combine(cacheDirectory, "browser-cache.bin");
            lockedCache = new FileStream(cachePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            lockedCache.WriteByte(42);
            var marker = Path.Combine(first.WebViewUserDataFolder, ".instance.lock");
            await first.ReleaseWebViewDirectoryAsync(browserExited: deleteDuringShutdown);
            await second.PrepareWebViewDirectoryAsync(default);

            Assert.True(Directory.Exists(first.WebViewUserDataFolder));
            Assert.True(File.Exists(marker), "A failed cleanup must keep the owner marker so future startups retry.");
            Assert.True(File.Exists(cachePath));

            lockedCache.Dispose();
            lockedCache = null;
            second.CleanupAbandonedWebViewDirectories(default);
            Assert.False(Directory.Exists(first.WebViewUserDataFolder));
            Assert.True(Directory.Exists(second.WebViewUserDataFolder));
        }
        finally
        {
            lockedCache?.Dispose();
            await first.ReleaseWebViewDirectoryAsync(true);
            await second.ReleaseWebViewDirectoryAsync(true);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task LockedCacheDirectoryRestoresTheMarkerWhenFinalRemovalFails()
    {
        var root = Path.Combine(Path.GetTempPath(), $"SerialInstance_{Guid.NewGuid():N}");
        var first = new RuntimeInstanceStorage(root);
        var second = new RuntimeInstanceStorage(root);
        SafeFileHandle? directoryLock = null;
        try
        {
            await first.PrepareWebViewDirectoryAsync(default);
            var cachePath = Path.Combine(first.WebViewUserDataFolder, "cache.bin");
            await File.WriteAllTextAsync(cachePath, "cache");
            await first.ReleaseWebViewDirectoryAsync(browserExited: false);
            // Deny deleting the directory itself while allowing its files to be
            // opened and removed, exercising the final non-recursive removal.
            directoryLock = CreateFileW(first.WebViewUserDataFolder, 1 /* FILE_LIST_DIRECTORY */, FileShare.ReadWrite,
                nint.Zero, FileMode.Open, 0x02000000 /* FILE_FLAG_BACKUP_SEMANTICS */, nint.Zero);
            Assert.False(directoryLock.IsInvalid, $"Directory lock failed: {Marshal.GetLastWin32Error()}");
            await second.PrepareWebViewDirectoryAsync(default);
            Assert.True(Directory.Exists(first.WebViewUserDataFolder));
            Assert.True(File.Exists(Path.Combine(first.WebViewUserDataFolder, ".instance.lock")));
            Assert.False(File.Exists(cachePath));

            directoryLock.Dispose();
            directoryLock = null;
            second.CleanupAbandonedWebViewDirectories(default);
            Assert.False(Directory.Exists(first.WebViewUserDataFolder));
            Assert.True(Directory.Exists(second.WebViewUserDataFolder));
        }
        finally
        {
            directoryLock?.Dispose();
            await first.ReleaseWebViewDirectoryAsync(true);
            await second.ReleaseWebViewDirectoryAsync(true);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, FileShare shareMode,
        nint securityAttributes, FileMode creationDisposition, uint flagsAndAttributes, nint templateFile);

    [Fact]
    public async Task CancellationDoesNotCreateAnUnownedCache()
    {
        var root = Path.Combine(Path.GetTempPath(), $"SerialInstance_{Guid.NewGuid():N}");
        var storage = new RuntimeInstanceStorage(root);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => storage.PrepareWebViewDirectoryAsync(cancellation.Token));
        Assert.False(Directory.Exists(root));
    }
}
