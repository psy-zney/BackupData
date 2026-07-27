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
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        public static async Task<bool> CreateBackupPackageAsync(string outputZneyPath, List<AppItemModel> selectedApps, List<DataFolderItemModel> selectedFolders, Action<string>? onProgress = null)
        {
            var temporaryPath = outputZneyPath + ".partial-" + Guid.NewGuid().ToString("N");
            try
            {
                if (string.IsNullOrWhiteSpace(outputZneyPath) || !Path.GetExtension(outputZneyPath).Equals(".zney", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Chỉ có thể tạo file backup .zney.");

                var manifest = new BackupManifest { Apps = selectedApps.Select(CloneApp).ToList() };
                await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var package = new ZipArchive(output, ZipArchiveMode.Create))
                {
                    foreach (var selected in selectedFolders)
                    {
                        var item = CloneDataFolder(selected);
                        if (!IsSafeArchiveSegment(item.RelativeArchivePath))
                            throw new InvalidDataException($"Tên nhóm dữ liệu không hợp lệ: {item.RelativeArchivePath}");

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
                        if (!TryGetSafeTargetPath(item.TargetPath, out _) || (!Directory.Exists(sourcePath) && !File.Exists(sourcePath)))
                        {
                            onProgress?.Invoke($"Bỏ qua {item.Name}: đường dẫn không an toàn hoặc không còn tồn tại.");
                            continue;
                        }

                        item.ArchiveEntryName = $"archives/{item.RelativeArchivePath}.zip";
                        onProgress?.Invoke($"Đang nén nhóm {item.Category}: {item.Name}...");
                        var outerEntry = package.CreateEntry(item.ArchiveEntryName, CompressionLevel.NoCompression);
                        await using (var outerStream = outerEntry.Open())
                        using (var dataArchive = new ZipArchive(outerStream, ZipArchiveMode.Create, leaveOpen: false))
                        {
                            if (Directory.Exists(sourcePath))
                            {
                                var enumerationOptions = new EnumerationOptions
                                {
                                    RecurseSubdirectories = true,
                                    IgnoreInaccessible = true,
                                    AttributesToSkip = FileAttributes.ReparsePoint
                                };
                                foreach (var filePath in Directory.EnumerateFiles(sourcePath, "*", enumerationOptions))
                                {
                                    var relativePath = NormalizeRelativePath(Path.GetRelativePath(sourcePath, filePath));
                                    dataArchive.CreateEntryFromFile(filePath, relativePath, CompressionLevel.Optimal);
                                    item.FileHashes[relativePath] = ComputeFileSha256(filePath);
                                    item.SizeBytes += new FileInfo(filePath).Length;
                                }
                            }
                            else
                            {
                                var relativePath = Path.GetFileName(sourcePath);
                                dataArchive.CreateEntryFromFile(sourcePath, relativePath, CompressionLevel.Optimal);
                                item.FileHashes[relativePath] = ComputeFileSha256(sourcePath);
                                item.SizeBytes = new FileInfo(sourcePath).Length;
                            }
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
                onProgress?.Invoke("Tạo file .zney thành công.");
                return true;
            }
            catch (Exception ex)
            {
                onProgress?.Invoke($"Lỗi tạo backup: {ex.Message}");
                return false;
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }

        public static async Task<BackupManifest?> ReadManifestFromPackageAsync(string zneyPath)
        {
            try
            {
                if (!Path.GetExtension(zneyPath).Equals(".zney", StringComparison.OrdinalIgnoreCase)) return null;
                using var package = ZipFile.OpenRead(zneyPath);
                var entry = package.GetEntry("manifest.json");
                if (entry is null || entry.Length > 10 * 1024 * 1024) return null;
                await using var stream = entry.Open();
                var manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(stream);
                return manifest is not null && IsValidManifest(manifest) ? manifest : null;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
            {
                return null;
            }
        }

        public static async Task<RestoreResult> RestoreSelectedDataAsync(string zneyPath, List<DataFolderItemModel> dataFoldersToRestore, Action<string>? onProgress = null)
        {
            var result = new RestoreResult();
            if (!Path.GetExtension(zneyPath).Equals(".zney", StringComparison.OrdinalIgnoreCase))
            {
                result.Errors.Add("Chỉ hỗ trợ khôi phục từ file .zney.");
                return result;
            }

            try
            {
                using var package = ZipFile.OpenRead(zneyPath);
                long totalBytes = 0;
                foreach (var item in dataFoldersToRestore)
                {
                    if (item.Category.Equals("WindowsSettings", StringComparison.OrdinalIgnoreCase))
                    {
                        await RestoreWindowsSettingsAsync(package, item, result, onProgress);
                        continue;
                    }
                    if (!TryGetSafeTargetPath(item.TargetPath, out var targetRoot) || !IsSafeArchiveEntryName(item.ArchiveEntryName))
                    {
                        result.Errors.Add($"Bỏ qua mục có đường dẫn không an toàn: {item.Name}");
                        continue;
                    }
                    if (item.FileHashes.Count == 0 && item.SizeBytes > 0)
                    {
                        result.Errors.Add($"Bỏ qua {item.Name}: manifest thiếu SHA256 từng tệp.");
                        continue;
                    }

                    var archiveEntry = package.GetEntry(item.ArchiveEntryName);
                    if (archiveEntry is null)
                    {
                        result.Errors.Add($"Thiếu archive cho nhóm {item.Name}.");
                        continue;
                    }
                    onProgress?.Invoke($"Đang khôi phục nhóm {item.Category}: {item.Name}...");
                    await using var outerStream = archiveEntry.Open();
                    using var dataArchive = new ZipArchive(outerStream, ZipArchiveMode.Read);
                    foreach (var entry in dataArchive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)))
                    {
                        var relativePath = NormalizeRelativePath(entry.FullName);
                        if (!IsSafeRelativePath(relativePath) || !item.FileHashes.TryGetValue(relativePath, out var expectedHash))
                        {
                            result.Errors.Add($"Bỏ qua tệp không hợp lệ trong {item.Name}: {entry.FullName}");
                            continue;
                        }
                        totalBytes += entry.Length;
                        if (totalBytes > MaxRestoreBytes)
                        {
                            result.Errors.Add("Dừng khôi phục: dữ liệu giải nén vượt giới hạn an toàn 25 GB.");
                            return result;
                        }

                        var destinationPath = Path.GetFullPath(Path.Combine(targetRoot, relativePath));
                        if (!IsPathInside(destinationPath, targetRoot))
                        {
                            result.Errors.Add($"Bỏ qua đường dẫn thoát thư mục đích: {entry.FullName}");
                            continue;
                        }
                        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                        var temporaryPath = destinationPath + ".restore-" + Guid.NewGuid().ToString("N");
                        try
                        {
                            await CopyAndVerifyAsync(entry, temporaryPath, expectedHash);
                            File.Move(temporaryPath, destinationPath, overwrite: true);
                            result.RestoredFiles++;
                        }
                        catch (Exception ex) when (ex is IOException or InvalidDataException or CryptographicException)
                        {
                            result.Errors.Add($"Không thể khôi phục {entry.FullName}: {ex.Message}");
                            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                        }
                    }
                }
                onProgress?.Invoke(result.Succeeded ? "Khôi phục dữ liệu thành công." : "Khôi phục hoàn tất nhưng có mục bị bỏ qua; xem nhật ký.");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                result.Errors.Add($"Không thể đọc gói .zney: {ex.Message}");
            }
            return result;
        }

        public static string ResolveEnvironmentPath(string pathWithEnv) => Environment.ExpandEnvironmentVariables(pathWithEnv);

        private static async Task RestoreWindowsSettingsAsync(ZipArchive package, DataFolderItemModel item, RestoreResult result, Action<string>? onProgress)
        {
            if (!item.SettingsEntryName.Equals("settings/windows-settings.json", StringComparison.OrdinalIgnoreCase) || !item.FileHashes.TryGetValue("windows-settings.json", out var expectedHash))
            {
                result.Errors.Add("Bỏ qua cài đặt Windows: manifest không hợp lệ.");
                return;
            }
            var entry = package.GetEntry(item.SettingsEntryName);
            if (entry is null)
            {
                result.Errors.Add("Thiếu file cài đặt Windows trong backup.");
                return;
            }
            await using var stream = entry.Open();
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            var bytes = memory.ToArray();
            if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                result.Errors.Add("SHA256 cài đặt Windows không khớp.");
                return;
            }
            var snapshot = JsonSerializer.Deserialize<WindowsSettingsSnapshot>(bytes);
            if (snapshot is null)
            {
                result.Errors.Add("Không đọc được cài đặt Windows.");
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
                throw new CryptographicException("SHA256 không khớp với manifest.");
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
            manifest.FormatVersion == 3 && manifest.Apps is not null && manifest.DataFolders is not null && manifest.DataFolders.All(IsValidDataItem);

        private static bool IsValidDataItem(DataFolderItemModel item) =>
            !string.IsNullOrWhiteSpace(item.Name) && IsSafeArchiveSegment(item.RelativeArchivePath) &&
            (item.Category.Equals("WindowsSettings", StringComparison.OrdinalIgnoreCase)
                ? item.SettingsEntryName.Equals("settings/windows-settings.json", StringComparison.OrdinalIgnoreCase)
                : TryGetSafeTargetPath(item.TargetPath, out _) && IsSafeArchiveEntryName(item.ArchiveEntryName));

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
        private static bool IsSafeRelativePath(string value) => !string.IsNullOrWhiteSpace(value) && !Path.IsPathRooted(value) && !value.Split('/', '\\').Any(part => part is "." or "..");
        private static string NormalizeRelativePath(string path) => path.Replace('\\', '/');
        private static string ComputeFileSha256(string filePath)
        {
            using var stream = File.OpenRead(filePath);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        private static string ComputeAggregateHash(IReadOnlyDictionary<string, string> hashes) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", hashes.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}:{pair.Value}")))));
        private static AppItemModel CloneApp(AppItemModel app) => new() { Name = app.Name, PackageId = app.PackageId, Version = app.Version, Publisher = app.Publisher, Source = app.Source, RestoreWorkflow = app.RestoreWorkflow, RequiresInteractiveLogin = app.RequiresInteractiveLogin, RestoreInstructions = app.RestoreInstructions, IsSelected = app.IsSelected };
        private static DataFolderItemModel CloneDataFolder(DataFolderItemModel item) => new() { Name = item.Name, Category = item.Category, SourcePath = item.SourcePath, RelativeArchivePath = item.RelativeArchivePath, ArchiveEntryName = item.ArchiveEntryName, SettingsEntryName = item.SettingsEntryName, TargetPath = item.TargetPath, FileHash = item.FileHash, FileHashes = new Dictionary<string, string>(), SizeBytes = item.SizeBytes, IsSelected = item.IsSelected };
    }
}
