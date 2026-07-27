using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BackupUtility.Models;
using BackupUtility.Services;
using Xunit;

namespace BackupUtility.Tests;

public sealed class BackupRestoreServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BackupUtility-Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CreatesPackageAndRestoresFileOnlyAfterHashVerification()
    {
        var item = await CreateDataItemAsync("profile", "settings.json", "{\"theme\":\"dark\"}");
        var package = Path.Combine(_root, "backup.zney");

        Assert.True(await BackupRestoreService.CreateBackupPackageAsync(package, [], [item]));
        var manifest = await BackupRestoreService.ReadManifestFromPackageAsync(package);
        Assert.NotNull(manifest);
        Assert.Single(manifest!.DataFolders);
        Assert.NotEmpty(manifest.DataFolders[0].FileHashes);

        var filePath = Path.Combine(_root, "profile", "settings.json");
        File.Delete(filePath);
        var result = await BackupRestoreService.RestoreSelectedDataAsync(package, manifest.DataFolders);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal(1, result.RestoredFiles);
        Assert.Equal("{\"theme\":\"dark\"}", await File.ReadAllTextAsync(filePath));
    }

    [Fact]
    public async Task RejectsTamperedArchiveEntryBeforeItOverwritesData()
    {
        var item = await CreateDataItemAsync("profile", "settings.json", "trusted");
        var package = Path.Combine(_root, "tampered.zney");
        Assert.True(await BackupRestoreService.CreateBackupPackageAsync(package, [], [item]));
        var manifest = (await BackupRestoreService.ReadManifestFromPackageAsync(package))!;

        using (var archive = ZipFile.Open(package, ZipArchiveMode.Update))
        {
            var nestedEntry = archive.GetEntry("archives/profile.zip")!;
            using var innerBytes = new MemoryStream();
            await using (var source = nestedEntry.Open())
                await source.CopyToAsync(innerBytes);
            nestedEntry.Delete();
            var replacement = archive.CreateEntry("archives/profile.zip", CompressionLevel.NoCompression);
            await using var replacementStream = replacement.Open();
            innerBytes.Position = 0;
            using var nestedArchive = new ZipArchive(innerBytes, ZipArchiveMode.Update, leaveOpen: true);
            nestedArchive.GetEntry("settings.json")!.Delete();
            var replacementFile = nestedArchive.CreateEntry("settings.json");
            await using (var fileStream = replacementFile.Open())
                await fileStream.WriteAsync(Encoding.UTF8.GetBytes("untrusted"));
            nestedArchive.Dispose();
            innerBytes.Position = 0;
            await innerBytes.CopyToAsync(replacementStream);
        }

        var filePath = Path.Combine(_root, "profile", "settings.json");
        File.Delete(filePath);
        var result = await BackupRestoreService.RestoreSelectedDataAsync(package, manifest.DataFolders);

        Assert.False(result.Succeeded);
        Assert.DoesNotContain(result.Errors, error => !error.Contains("SHA256", StringComparison.OrdinalIgnoreCase));
        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public async Task RejectsZipSlipPaths()
    {
        Directory.CreateDirectory(_root);
        var package = Path.Combine(_root, "zip-slip.zney");
        var target = $"%LOCALAPPDATA%\\BackupUtility-Tests\\{Path.GetFileName(_root)}\\profile";
        var unsafeRelativePath = "../escape.txt";
        var payload = Encoding.UTF8.GetBytes("evil");
        var manifest = new BackupManifest
        {
            DataFolders =
            [
                new DataFolderItemModel
                {
                    Name = "profile",
                    Category = "ApplicationSettings",
                    RelativeArchivePath = "profile",
                    ArchiveEntryName = "archives/profile.zip",
                    TargetPath = target,
                    SizeBytes = payload.Length,
                    FileHashes = new Dictionary<string, string>
                    {
                        [unsafeRelativePath] = Convert.ToHexString(SHA256.HashData(payload))
                    }
                }
            ]
        };
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            var manifestEntry = archive.CreateEntry("manifest.json");
            await using (var manifestStream = manifestEntry.Open())
                await JsonSerializer.SerializeAsync(manifestStream, manifest);
            var nestedEntry = archive.CreateEntry("archives/profile.zip", CompressionLevel.NoCompression);
            await using var nestedStream = nestedEntry.Open();
            using var nestedArchive = new ZipArchive(nestedStream, ZipArchiveMode.Create, leaveOpen: false);
            var entry = nestedArchive.CreateEntry("../escape.txt");
            await using var stream = entry.Open();
            await stream.WriteAsync(payload);
        }

        var result = await BackupRestoreService.RestoreSelectedDataAsync(package, manifest.DataFolders);

        Assert.False(result.Succeeded);
        Assert.False(File.Exists(Path.Combine(_root, "escape.txt")));
    }

    private async Task<DataFolderItemModel> CreateDataItemAsync(string archivePath, string fileName, string contents)
    {
        var directory = Path.Combine(_root, archivePath);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, fileName), contents);
        return new DataFolderItemModel
        {
            Name = archivePath,
            RelativeArchivePath = archivePath,
            TargetPath = $"%LOCALAPPDATA%\\BackupUtility-Tests\\{Path.GetFileName(_root)}\\{archivePath}"
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
