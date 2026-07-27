using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using BackupUtility.Models;

namespace BackupUtility.Services
{
    public sealed class RestoreResult
    {
        public int RestoredFiles { get; internal set; }
        public List<string> Errors { get; } = new();
        public bool Succeeded => Errors.Count == 0;
    }

    public static class BackupRestoreService
    {
        private const long MaxRestoreBytes = 25L * 1024 * 1024 * 1024;
        private const long MaxNestedArchiveBytes = MaxRestoreBytes + 256L * 1024 * 1024;
        private const int MaxManifestApps = 10_000;
        private const int MaxManifestDataFolders = 100;
        private const int MaxFilesPerDataFolder = 500_000;
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        public static async Task<bool> CreateBackupPackageAsync(string outputZneyPath, List<AppItemModel> selectedApps, List<DataFolderItemModel> selectedFolders, Action<string>? onProgress = null)
        {
            var temporaryPath = outputZneyPath + ".partial-" + Guid.NewGuid().ToString("N");
            try
            {
                if (string.IsNullOrWhiteSpace(outputZneyPath) || !Path.GetExtension(outputZneyPath).Equals(".zney", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Only .zney backup files can be created.");

                var manifest = new BackupManifest { Apps = selectedApps.Select(CloneApp).ToList() };
                long totalBackupBytes = 0;
                await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var package = new ZipArchive(output, ZipArchiveMode.Create))
                {
                    foreach (var selected in selectedFolders)
                    {
                        var item = CloneDataFolder(selected);
                        if (!IsSafeArchiveSegment(item.RelativeArchivePath))
                            throw new InvalidDataException($"Invalid data-group name: {item.RelativeArchivePath}");

                        if (item.Category.Equals("WindowsSettings", StringComparison.OrdinalIgnoreCase))
                        {
                            item.SettingsEntryName = "settings/windows-settings.json";
                            var settings = WindowsSettingsService.Capture();
                            var json = JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions);
                            await WriteBytesEntryAsync(package, item.SettingsEntryName, json);
                            item.FileHashes["windows-settings.json"] = Convert.ToHexString(SHA256.HashData(json));
                            item.FileHash = ComputeAggregateHash(item.FileHashes);
                            manifest.DataFolders.Add(item);
                            continue;
                        }

                        var sourcePath = string.IsNullOrWhiteSpace(item.SourcePath) ? ResolveEnvironmentPath(item.TargetPath) : item.SourcePath;
                        var sourceIsDirectory = Directory.Exists(sourcePath);
                        if (!TryGetSafeTargetPath(item.TargetPath, out _) || (!sourceIsDirectory && !File.Exists(sourcePath)))
                        {
                            onProgress?.Invoke($"Skipped {item.Name}: the path is unsafe or no longer exists.");
                            continue;
                        }

                        item.ArchiveEntryName = $"archives/{item.RelativeArchivePath}.zip";
                        onProgress?.Invoke($"Compressing {item.Category}: {item.Name}...");
                        var outerEntry = package.CreateEntry(item.ArchiveEntryName, CompressionLevel.NoCompression);
                        await using (var outerStream = outerEntry.Open())
                        using (var dataArchive = new ZipArchive(outerStream, ZipArchiveMode.Create, leaveOpen: false))
                        {
                            if (sourceIsDirectory)
                            {
                                var enumerationOptions = new EnumerationOptions
                                {
                                    RecurseSubdirectories = true,
                                    IgnoreInaccessible = true,
                                    AttributesToSkip = FileAttributes.ReparsePoint
                                };
                                try
                                {
                                    foreach (var filePath in Directory.EnumerateFiles(sourcePath, "*", enumerationOptions))
                                    {
                                        try
                                        {
                                            var relativePath = NormalizeRelativePath(Path.GetRelativePath(sourcePath, filePath));
                                            var writeResult = await WriteFileEntryAndHashAsync(dataArchive, filePath, relativePath);
                                            if (writeResult.SizeBytes > MaxRestoreBytes - totalBackupBytes)
                                                throw new InvalidDataException("Selected data exceeds the 25 GB package safety limit.");
                                            item.FileHashes[relativePath] = writeResult.Hash;
                                            item.SizeBytes += writeResult.SizeBytes;
                                            totalBackupBytes += writeResult.SizeBytes;
                                        }
                                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                                        {
                                            onProgress?.Invoke($"Skipped {filePath}: {ex.Message}");
                                        }
                                    }
                                }
                                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                                {
                                    onProgress?.Invoke($"Could not finish enumerating {item.Name}; files already read were retained: {ex.Message}");
                                }
                            }
                            else
                            {
                                try
                                {
                                    var relativePath = Path.GetFileName(sourcePath);
                                    var writeResult = await WriteFileEntryAndHashAsync(dataArchive, sourcePath, relativePath);
                                    if (writeResult.SizeBytes > MaxRestoreBytes - totalBackupBytes)
                                        throw new InvalidDataException("Selected data exceeds the 25 GB package safety limit.");
                                    item.FileHashes[relativePath] = writeResult.Hash;
                                    item.SizeBytes = writeResult.SizeBytes;
                                    totalBackupBytes += writeResult.SizeBytes;
                                }
                                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                                {
                                    onProgress?.Invoke($"Skipped {item.Name}: {ex.Message}");
                                }
                            }
                        }
                        if (!sourceIsDirectory && item.FileHashes.Count == 0)
                        {
                            outerEntry.Delete();
                            continue;
                        }
                        item.FileHash = ComputeAggregateHash(item.FileHashes);
                        manifest.DataFolders.Add(item);
                    }

                    await WriteJsonEntryAsync(package, "metadata/apps.json", manifest.Apps);
                    await WriteJsonEntryAsync(package, "metadata/data-groups.json", manifest.DataFolders);
                    await WriteJsonEntryAsync(package, "settings/application-settings.json", manifest.DataFolders.Where(item => item.Category.Equals("ApplicationSettings", StringComparison.OrdinalIgnoreCase)).ToList());
                    await WriteJsonEntryAsync(package, "manifest.json", manifest);
                }

                File.Move(temporaryPath, outputZneyPath, overwrite: true);
                onProgress?.Invoke("The .zney file was created successfully.");
                return true;
            }
            catch (Exception ex)
            {
                onProgress?.Invoke($"Backup creation failed: {ex.Message}");
                return false;
            }
            finally
            {
                TryDeleteFile(temporaryPath);
            }
        }

        public static async Task<BackupManifest?> ReadManifestFromPackageAsync(string zneyPath)
        {
            try
            {
                if (!Path.GetExtension(zneyPath).Equals(".zney", StringComparison.OrdinalIgnoreCase)) return null;
                using var package = ZipFile.OpenRead(zneyPath);
                var manifestEntries = package.Entries
                    .Where(candidate => candidate.FullName.Equals("manifest.json", StringComparison.Ordinal))
                    .ToList();
                if (manifestEntries.Count != 1) return null;
                var entry = manifestEntries[0];
                if (entry is null || entry.Length > 10 * 1024 * 1024) return null;
                await using var stream = entry.Open();
                var manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(stream);
                return manifest is not null && IsValidManifest(manifest) ? manifest : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            {
                return null;
            }
        }

        public static async Task<RestoreResult> RestoreSelectedDataAsync(string zneyPath, List<DataFolderItemModel> dataFoldersToRestore, Action<string>? onProgress = null)
        {
            var result = new RestoreResult();
            if (!Path.GetExtension(zneyPath).Equals(".zney", StringComparison.OrdinalIgnoreCase))
            {
                result.Errors.Add("Only .zney files can be restored.");
                return result;
            }

            try
            {
                using var package = ZipFile.OpenRead(zneyPath);
                long totalBytes = 0;
                foreach (var item in dataFoldersToRestore)
                {
                    if (!IsValidDataItem(item))
                    {
                        result.Errors.Add("Skipped an item with an invalid manifest.");
                        continue;
                    }
                    if (item.Category.Equals("WindowsSettings", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            await RestoreWindowsSettingsAsync(package, item, result, onProgress);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or CryptographicException or JsonException)
                        {
                            result.Errors.Add($"Could not restore Windows settings: {ex.Message}");
                        }
                        continue;
                    }
                    if (!TryGetSafeTargetPath(item.TargetPath, out var targetRoot) || !IsSafeArchiveEntryName(item.ArchiveEntryName))
                    {
                        result.Errors.Add($"Skipped an item with an unsafe target path: {item.Name}");
                        continue;
                    }
                    if (item.FileHashes.Count == 0 && item.SizeBytes > 0)
                    {
                        result.Errors.Add($"Skipped {item.Name}: per-file SHA-256 values are missing.");
                        continue;
                    }

                    var archiveEntries = package.Entries
                        .Where(entry => entry.FullName.Equals(item.ArchiveEntryName, StringComparison.Ordinal))
                        .ToList();
                    if (archiveEntries.Count != 1)
                    {
                        result.Errors.Add($"The archive for {item.Name} is missing or duplicated.");
                        continue;
                    }
                    var archiveEntry = archiveEntries[0];
                    onProgress?.Invoke($"Restoring {item.Category}: {item.Name}...");
                    using var temporaryArchive = await OpenNestedArchiveFromTemporaryFileAsync(archiveEntry);
                    var dataArchive = temporaryArchive.Archive;
                    var fileEntries = dataArchive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)).ToList();
                    var groupErrorCount = result.Errors.Count;
                    if (fileEntries.Count > MaxFilesPerDataFolder)
                    {
                        result.Errors.Add($"Skipped {item.Name}: the file-count safety limit was exceeded.");
                        continue;
                    }

                    var archivePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    long groupBytes = 0;
                    foreach (var entry in fileEntries)
                    {
                        var relativePath = NormalizeRelativePath(entry.FullName);
                        if (!IsSafeRelativePath(relativePath) ||
                            !item.FileHashes.TryGetValue(relativePath, out var expectedHash) ||
                            !IsSha256(expectedHash))
                        {
                            result.Errors.Add($"Skipped an invalid file in {item.Name}: {entry.FullName}");
                            continue;
                        }
                        if (!archivePaths.Add(relativePath))
                        {
                            result.Errors.Add($"Skipped duplicate file in {item.Name}: {entry.FullName}");
                            continue;
                        }
                        if (entry.Length > MaxRestoreBytes - totalBytes - groupBytes)
                        {
                            result.Errors.Add("Restore stopped: extracted data exceeded the 25 GB safety limit.");
                            return result;
                        }
                        groupBytes += entry.Length;

                        var destinationPath = Path.GetFullPath(Path.Combine(targetRoot, relativePath));
                        if (!IsPathInside(destinationPath, targetRoot))
                        {
                            result.Errors.Add($"Skipped a path outside the target directory: {entry.FullName}");
                        }
                    }

                    foreach (var missingPath in item.FileHashes.Keys.Where(path => !archivePaths.Contains(path)))
                    {
                        result.Errors.Add($"Missing declared file in {item.Name}: {missingPath}");
                    }
                    if (result.Errors.Count != groupErrorCount) continue;
                    totalBytes += groupBytes;

                    var stagingRoot = Path.Combine(targetRoot, $".zney-restore-{Guid.NewGuid():N}");
                    var stagedFiles = new List<(string StagedPath, string DestinationPath, string EntryName)>();
                    try
                    {
                        foreach (var entry in fileEntries)
                        {
                            try
                            {
                                var relativePath = NormalizeRelativePath(entry.FullName);
                                var destinationPath = Path.GetFullPath(Path.Combine(targetRoot, relativePath));
                                var stagedPath = Path.GetFullPath(Path.Combine(stagingRoot, relativePath));
                                if (!IsPathInside(stagedPath, stagingRoot))
                                    throw new InvalidDataException($"Unsafe staging path: {entry.FullName}");

                                Directory.CreateDirectory(Path.GetDirectoryName(stagedPath)!);
                                await CopyAndVerifyAsync(entry, stagedPath, item.FileHashes[relativePath]);
                                stagedFiles.Add((stagedPath, destinationPath, entry.FullName));
                            }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or CryptographicException)
                            {
                                result.Errors.Add($"Could not verify {entry.FullName}; the group was not written: {ex.Message}");
                                break;
                            }
                        }

                        if (stagedFiles.Count != fileEntries.Count) continue;

                        foreach (var staged in stagedFiles)
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(staged.DestinationPath)!);
                            File.Move(staged.StagedPath, staged.DestinationPath, overwrite: true);
                            result.RestoredFiles++;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        result.Errors.Add($"Could not write the complete {item.Name} group: {ex.Message}");
                    }
                    finally
                    {
                        TryDeleteDirectory(stagingRoot);
                    }
                }
                onProgress?.Invoke(result.Succeeded ? "Data restore completed successfully." : "Restore completed with skipped items; review the log.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                result.Errors.Add($"Could not read the .zney package: {ex.Message}");
            }
            return result;
        }

        public static string ResolveEnvironmentPath(string pathWithEnv) => Environment.ExpandEnvironmentVariables(pathWithEnv);

        private static async Task RestoreWindowsSettingsAsync(ZipArchive package, DataFolderItemModel item, RestoreResult result, Action<string>? onProgress)
        {
            if (!item.SettingsEntryName.Equals("settings/windows-settings.json", StringComparison.OrdinalIgnoreCase) || !item.FileHashes.TryGetValue("windows-settings.json", out var expectedHash))
            {
                result.Errors.Add("Skipped Windows settings: the manifest is invalid.");
                return;
            }
            var entries = package.Entries
                .Where(entry => entry.FullName.Equals(item.SettingsEntryName, StringComparison.Ordinal))
                .ToList();
            if (entries.Count != 1 || entries[0].Length > 1024 * 1024)
            {
                result.Errors.Add("The Windows settings entry is missing or too large.");
                return;
            }
            var entry = entries[0];
            await using var stream = entry.Open();
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            var bytes = memory.ToArray();
            if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                result.Errors.Add("The Windows settings SHA-256 does not match.");
                return;
            }
            var snapshot = JsonSerializer.Deserialize<WindowsSettingsSnapshot>(bytes);
            if (snapshot is null)
            {
                result.Errors.Add("The Windows settings snapshot could not be read.");
                return;
            }
            WindowsSettingsService.Apply(snapshot, onProgress);
        }

        private static async Task CopyAndVerifyAsync(ZipArchiveEntry entry, string temporaryPath, string expectedHash)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var source = entry.Open();
            await using var destination = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read));
            }
            if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException("SHA-256 does not match the manifest.");
        }

        private static async Task<TemporaryZipArchive> OpenNestedArchiveFromTemporaryFileAsync(ZipArchiveEntry entry)
        {
            if (entry.Length > MaxNestedArchiveBytes)
                throw new InvalidDataException("Nested archive exceeds the safe size limit.");

            var temporaryPath = Path.Combine(Path.GetTempPath(), $"zney-archive-{Guid.NewGuid():N}.zip");
            try
            {
                await using (var source = entry.Open())
                await using (var destination = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 81920,
                    options: FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await source.CopyToAsync(destination);
                }

                return new TemporaryZipArchive(temporaryPath, ZipFile.OpenRead(temporaryPath));
            }
            catch
            {
                TryDeleteFile(temporaryPath);
                throw;
            }
        }

        private static async Task<(string Hash, long SizeBytes)> WriteFileEntryAndHashAsync(ZipArchive archive, string sourcePath, string entryPath)
        {
            await using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 81920,
                useAsync: true);

            var entry = archive.CreateEntry(entryPath, CompressionLevel.Optimal);
            try
            {
                await using var destination = entry.Open();
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                long sizeBytes = 0;
                int read;
                while ((read = await source.ReadAsync(buffer)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await destination.WriteAsync(buffer.AsMemory(0, read));
                    sizeBytes += read;
                }

                return (Convert.ToHexString(hash.GetHashAndReset()), sizeBytes);
            }
            catch
            {
                try { entry.Delete(); }
                catch (InvalidOperationException) { }
                throw;
            }
        }

        private static async Task WriteJsonEntryAsync<T>(ZipArchive package, string name, T value)
        {
            var entry = package.CreateEntry(name, CompressionLevel.Optimal);
            await using var stream = entry.Open();
            await JsonSerializer.SerializeAsync(stream, value, JsonOptions);
        }

        private static async Task WriteBytesEntryAsync(ZipArchive package, string name, byte[] bytes)
        {
            var entry = package.CreateEntry(name, CompressionLevel.Optimal);
            await using var stream = entry.Open();
            await stream.WriteAsync(bytes);
        }

        private static bool IsValidManifest(BackupManifest manifest) =>
            manifest.FormatVersion == 3 &&
            manifest.Apps is not null &&
            manifest.DataFolders is not null &&
            manifest.Apps.Count <= MaxManifestApps &&
            manifest.DataFolders.Count <= MaxManifestDataFolders &&
            manifest.Apps.All(IsValidAppItem) &&
            manifest.DataFolders.All(IsValidDataItem);

        private static bool IsValidAppItem(AppItemModel? item) =>
            item is not null &&
            item.Name is not null &&
            item.PackageId is not null &&
            item.Version is not null &&
            item.Publisher is not null &&
            item.Source is not null &&
            item.RestoreWorkflow is not null &&
            item.RestoreInstructions is not null;

        private static bool IsValidDataItem(DataFolderItemModel? item)
        {
            if (item is null ||
                string.IsNullOrWhiteSpace(item.Name) ||
                item.Category is null ||
                item.RelativeArchivePath is null ||
                item.ArchiveEntryName is null ||
                item.SettingsEntryName is null ||
                item.TargetPath is null ||
                item.FileHash is null ||
                item.FileHashes is null ||
                item.SizeBytes < 0 ||
                item.FileHashes.Count > MaxFilesPerDataFolder ||
                item.FileHashes.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != item.FileHashes.Count ||
                !IsSafeArchiveSegment(item.RelativeArchivePath) ||
                !item.FileHashes.All(pair => IsSafeRelativePath(pair.Key) && IsSha256(pair.Value)) ||
                !item.FileHash.Equals(ComputeAggregateHash(item.FileHashes), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return item.Category.Equals("WindowsSettings", StringComparison.OrdinalIgnoreCase)
                ? item.SettingsEntryName.Equals("settings/windows-settings.json", StringComparison.OrdinalIgnoreCase)
                : TryGetSafeTargetPath(item.TargetPath, out _) && IsSafeArchiveEntryName(item.ArchiveEntryName);
        }

        private static bool TryGetSafeTargetPath(string configuredPath, out string resolvedPath)
        {
            resolvedPath = string.Empty;
            if (string.IsNullOrWhiteSpace(configuredPath)) return false;
            var roots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) }.Where(root => !string.IsNullOrWhiteSpace(root));
            try
            {
                var expanded = Path.GetFullPath(ResolveEnvironmentPath(configuredPath));
                if (!roots.Select(Path.GetFullPath).Any(root => IsPathInside(expanded, root))) return false;
                resolvedPath = expanded;
                return true;
            }
            catch (Exception) { return false; }
        }

        private static bool IsPathInside(string childPath, string rootPath)
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath)) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(childPath).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSafeArchiveSegment(string value) => !string.IsNullOrWhiteSpace(value) && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !value.Contains("..", StringComparison.Ordinal) && !value.Contains('/') && !value.Contains('\\');
        private static bool IsSafeArchiveEntryName(string value) => value.StartsWith("archives/", StringComparison.OrdinalIgnoreCase) && value.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && IsSafeArchiveSegment(Path.GetFileNameWithoutExtension(value));
        private static bool IsSafeRelativePath(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value)) return false;
            var parts = value.Split('/', '\\');
            return parts.All(part =>
                !string.IsNullOrWhiteSpace(part) &&
                part is not "." and not ".." &&
                part.IndexOfAny(Path.GetInvalidFileNameChars()) < 0);
        }
        private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
        private static string NormalizeRelativePath(string path) => path.Replace('\\', '/');
        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception)
            {
                // A leftover temporary file is harmless; never turn cleanup into an app failure.
            }
        }
        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
            catch (Exception)
            {
                // Staging cleanup must not hide the restore result.
            }
        }

        private sealed class TemporaryZipArchive : IDisposable
        {
            private readonly string _path;

            public TemporaryZipArchive(string path, ZipArchive archive)
            {
                _path = path;
                Archive = archive;
            }

            public ZipArchive Archive { get; }

            public void Dispose()
            {
                try
                {
                    Archive.Dispose();
                }
                finally
                {
                    TryDeleteFile(_path);
                }
            }
        }
        private static string ComputeAggregateHash(IReadOnlyDictionary<string, string> hashes) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", hashes.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}:{pair.Value}")))));
        private static AppItemModel CloneApp(AppItemModel app) => new() { Name = app.Name, PackageId = app.PackageId, Version = app.Version, Publisher = app.Publisher, Source = app.Source, RestoreWorkflow = app.RestoreWorkflow, RequiresInteractiveLogin = app.RequiresInteractiveLogin, RestoreInstructions = app.RestoreInstructions, IsSelected = app.IsSelected };
        private static DataFolderItemModel CloneDataFolder(DataFolderItemModel item) => new() { Name = item.Name, Category = item.Category, SourcePath = item.SourcePath, RelativeArchivePath = item.RelativeArchivePath, ArchiveEntryName = item.ArchiveEntryName, SettingsEntryName = item.SettingsEntryName, TargetPath = item.TargetPath, FileHash = string.Empty, FileHashes = new Dictionary<string, string>(), SizeBytes = 0, IsSelected = item.IsSelected };
    }
}
