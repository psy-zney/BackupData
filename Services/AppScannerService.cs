using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BackupUtility.Models;
using Microsoft.Win32;

namespace BackupUtility.Services;

public sealed record ScanProgress(int Percent, string Message);

public static class AppScannerService
{
    public static async Task<List<AppItemModel>> ScanInstalledAppsAsync(
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var registryApps = await ScanRegistryInstalledAppsAsync(cancellationToken);
        progress?.Report(new ScanProgress(20, $"Found {registryApps.Count} applications in Windows Registry."));

        var wingetApps = await ScanWingetPackagesAsync(progress, cancellationToken);
        return PrepareApps(registryApps.Concat(wingetApps));
    }

    public static Task<List<AppItemModel>> ScanRegistryInstalledAppsAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() => PrepareApps(GetRegistryInstalledApps(cancellationToken)), cancellationToken);
    }

    public static async Task<List<AppItemModel>> ScanWingetPackagesAsync(
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new ScanProgress(25, "Checking optional winget integration..."));
        if (!await WingetService.IsWingetAvailableAsync(TimeSpan.FromSeconds(5), cancellationToken))
        {
            progress?.Report(new ScanProgress(90, "winget is unavailable; registry results are ready."));
            return [];
        }

        var tempJson = Path.Combine(Path.GetTempPath(), $"zney-winget-{Guid.NewGuid():N}.json");
        try
        {
            progress?.Report(new ScanProgress(35, "Reading optional winget package list (maximum 35 seconds)..."));
            var apps = await WingetService.ExportWingetPackagesAsync(
                tempJson,
                TimeSpan.FromSeconds(35),
                cancellationToken);
            progress?.Report(new ScanProgress(90, $"winget scan finished: {apps.Count} packages found."));
            return apps;
        }
        catch (TimeoutException)
        {
            progress?.Report(new ScanProgress(90, "winget timed out after 35 seconds; continuing with registry results."));
            return [];
        }
        finally
        {
            try
            {
                if (File.Exists(tempJson)) File.Delete(tempJson);
            }
            catch (IOException)
            {
                // The temporary export is non-essential and may still be held by winget.
            }
        }
    }

    public static List<AppItemModel> PrepareApps(IEnumerable<AppItemModel> apps)
    {
        var list = apps.ToList();
        ApplyRestoreWorkflows(list);

        return list
            .GroupBy(GetAppIdentity, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(app => app.Source.Equals("winget", StringComparison.OrdinalIgnoreCase))
                .First())
            .OrderBy(app => app.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static async Task<List<DataFolderItemModel>> GetSuggestedDataFoldersAsync(
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var candidates = new (string Name, string SourcePath, string TargetEnv, string Category, bool IsSelected)[]
        {
            ("VS Code settings", Path.Combine(appData, "Code", "User"), "%APPDATA%\\Code\\User", "ApplicationSettings", true),
            ("Chrome profile settings", Path.Combine(localAppData, "Google", "Chrome", "User Data", "Default"), "%LOCALAPPDATA%\\Google\\Chrome\\User Data\\Default", "ApplicationSettings", true),
            ("Edge profile settings", Path.Combine(localAppData, "Microsoft", "Edge", "User Data", "Default"), "%LOCALAPPDATA%\\Microsoft\\Edge\\User Data\\Default", "ApplicationSettings", true),
            ("Git configuration", Path.Combine(userProfile, ".gitconfig"), "%USERPROFILE%\\.gitconfig", "ApplicationSettings", true),
            ("Windows Terminal settings", Path.Combine(localAppData, "Packages", "Microsoft.WindowsTerminal_8wekyb3d8bbwe", "LocalState"), "%LOCALAPPDATA%\\Packages\\Microsoft.WindowsTerminal_8wekyb3d8bbwe\\LocalState", "ApplicationSettings", true),
            ("Personal photos", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "%USERPROFILE%\\Pictures", "Photos", false),
            ("Personal documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "%USERPROFILE%\\Documents", "Documents", false),
            ("Personal videos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "%USERPROFILE%\\Videos", "Videos", false)
        };

        var folders = candidates
            .Where(item => Directory.Exists(item.SourcePath) || File.Exists(item.SourcePath))
            .Select(item => new DataFolderItemModel
            {
                Name = item.Name,
                Category = item.Category,
                SourcePath = item.SourcePath,
                TargetPath = item.TargetEnv,
                RelativeArchivePath = item.Name.Replace(" ", "_", StringComparison.Ordinal),
                IsSelected = item.IsSelected
            })
            .ToList();

        var measuredFolders = folders.Where(item => Directory.Exists(item.SourcePath) || File.Exists(item.SourcePath)).ToList();
        var completed = 0;
        progress?.Report(new ScanProgress(20, $"Calculating sizes for {measuredFolders.Count} data locations..."));

        await Parallel.ForEachAsync(
            measuredFolders,
            new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = 2 },
            (item, token) =>
            {
                item.SizeBytes = Directory.Exists(item.SourcePath)
                    ? GetDirectorySize(item.SourcePath, token)
                    : GetFileSize(item.SourcePath);

                var done = Interlocked.Increment(ref completed);
                var percent = 20 + (int)Math.Round(done * 50.0 / Math.Max(1, measuredFolders.Count));
                progress?.Report(new ScanProgress(percent, $"Scanned {done}/{measuredFolders.Count}: {item.Name}"));
                return ValueTask.CompletedTask;
            });

        folders.Add(new DataFolderItemModel
        {
            Name = "Windows personalization and Explorer settings",
            Category = "WindowsSettings",
            TargetPath = "%LOCALAPPDATA%\\ZneyBackup",
            RelativeArchivePath = "Windows_Settings",
            IsSelected = true
        });

        return folders;
    }

    private static List<AppItemModel> GetRegistryInstalledApps(CancellationToken cancellationToken)
    {
        var apps = new List<AppItemModel>();
        var locations = new[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.CurrentUser, RegistryView.Default)
        };

        foreach (var (hive, view) in locations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstallKey = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstallKey is null) continue;

                foreach (var subkeyName in uninstallKey.GetSubKeyNames())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var subkey = uninstallKey.OpenSubKey(subkeyName);
                    if (subkey is null) continue;

                    var displayName = subkey.GetValue("DisplayName")?.ToString() ?? string.Empty;
                    var systemComponent = subkey.GetValue("SystemComponent")?.ToString() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(displayName) || systemComponent == "1") continue;

                    apps.Add(new AppItemModel
                    {
                        Name = displayName,
                        PackageId = subkeyName,
                        Version = subkey.GetValue("DisplayVersion")?.ToString() ?? string.Empty,
                        Publisher = subkey.GetValue("Publisher")?.ToString() ?? string.Empty,
                        Source = "Registry",
                        IsSelected = true
                    });
                }
            }
            catch (UnauthorizedAccessException)
            {
                // A protected registry key is skipped; administrator rights are not required.
            }
            catch (IOException)
            {
                // Registry view unavailable on this Windows installation.
            }
        }

        return apps;
    }

    private static string GetAppIdentity(AppItemModel app)
    {
        if (app.RestoreWorkflow.Equals("Steam", StringComparison.OrdinalIgnoreCase)) return "steam";
        if (app.Source.Equals("winget", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(app.PackageId))
            return $"winget:{app.PackageId}";
        return $"name:{app.Name}";
    }

    private static void ApplyRestoreWorkflows(IEnumerable<AppItemModel> apps)
    {
        foreach (var app in apps)
        {
            if (app.PackageId.Equals("Valve.Steam", StringComparison.OrdinalIgnoreCase) ||
                app.Name.Contains("Steam", StringComparison.OrdinalIgnoreCase))
            {
                app.Name = "Steam";
                app.PackageId = "Valve.Steam";
                app.Source = "Steam";
                app.RestoreWorkflow = "Steam";
                app.RequiresInteractiveLogin = true;
                app.RestoreInstructions = "Install Steam, open it, and sign in. Game downloads and library management stay inside Steam.";
            }
            else if (app.Source.Equals("winget", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(app.PackageId))
            {
                app.RestoreWorkflow = "Winget";
                app.RestoreInstructions = "Install automatically with winget.";
            }
            else
            {
                app.RestoreWorkflow = "Manual";
                app.IsSelected = false;
                app.RestoreInstructions = "No trusted installer ID is available; this entry is informational only.";
            }
        }
    }

    private static long GetFileSize(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    private static long GetDirectorySize(string rootPath, CancellationToken cancellationToken)
    {
        long total = 0;
        var pending = new Stack<string>();
        pending.Push(rootPath);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) continue;

                foreach (var file in Directory.EnumerateFiles(current))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    total += GetFileSize(file);
                }

                foreach (var directory in Directory.EnumerateDirectories(current))
                {
                    pending.Push(directory);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return total;
    }
}
