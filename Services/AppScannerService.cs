using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
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
        progress?.Report(new ScanProgress(5, "Reading Desktop and Start Menu shortcuts..."));
        var shortcutApps = await ScanShortcutAppsAsync(cancellationToken);
        progress?.Report(new ScanProgress(15, $"Found {shortcutApps.Count} application shortcuts."));

        var registryApps = await ScanRegistryInstalledAppsAsync(cancellationToken);
        progress?.Report(new ScanProgress(25, $"Found {registryApps.Count} applications in Windows Registry."));

        var wingetApps = await ScanWingetPackagesAsync(progress, cancellationToken);
        return PrepareApps(shortcutApps.Concat(registryApps).Concat(wingetApps));
    }

    public static Task<List<AppItemModel>> ScanShortcutAppsAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() => PrepareApps(GetShortcutApps(cancellationToken)), cancellationToken);
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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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
                .OrderByDescending(app => GetSourcePriority(app.Source))
                .First())
            .OrderBy(app => app.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static Task<List<DataFolderItemModel>> GetSuggestedDataFoldersAsync(
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

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new ScanProgress(60, $"Found {folders.Count} data locations. File sizes are calculated only during backup."));

        folders.Add(new DataFolderItemModel
        {
            Name = "Windows personalization and Explorer settings",
            Category = "WindowsSettings",
            TargetPath = "%LOCALAPPDATA%\\ZneyBackup",
            RelativeArchivePath = "Windows_Settings",
            IsSelected = true
        });

        return Task.FromResult(folders);
    }

    private static List<AppItemModel> GetRegistryInstalledApps(CancellationToken cancellationToken)
    {
        var apps = new List<AppItemModel>();
        var locations = new[]
        {
            (RegistryHive.CurrentUser, RegistryView.Registry32),
            (RegistryHive.CurrentUser, RegistryView.Registry64),
            // x86 applications are common on 64-bit Windows, so read this view before x64.
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.LocalMachine, RegistryView.Registry64),
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
        return $"name:{NormalizeAppName(app.Name)}";
    }

    private static string NormalizeAppName(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static int GetSourcePriority(string source)
    {
        if (source.Equals("winget", StringComparison.OrdinalIgnoreCase)) return 3;
        if (source.Equals("Registry", StringComparison.OrdinalIgnoreCase)) return 2;
        if (source.Equals("Shortcut", StringComparison.OrdinalIgnoreCase)) return 1;
        return 0;
    }

    private static List<AppItemModel> GetShortcutApps(CancellationToken cancellationToken)
    {
        var apps = new List<AppItemModel>();
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
        }
        .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
        .Distinct(StringComparer.OrdinalIgnoreCase);

        const int maximumShortcuts = 1500;
        var examined = 0;
        var shell = CreateShortcutShell();
        if (shell is null) return apps;
        try
        {
            foreach (var root in roots)
            {
                foreach (var shortcutPath in EnumerateShortcutFiles(root, maximumShortcuts - examined, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (examined++ >= maximumShortcuts) return apps;

                    var targetPath = TryResolveShortcutTarget(shell, shortcutPath);
                    if (!targetPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(targetPath)) continue;

                    apps.Add(new AppItemModel
                    {
                        Name = Path.GetFileNameWithoutExtension(shortcutPath),
                        PackageId = targetPath,
                        Publisher = Path.GetDirectoryName(targetPath) ?? string.Empty,
                        Source = "Shortcut",
                        IsSelected = false,
                        RestoreWorkflow = "Manual",
                        RestoreInstructions = "Detected from a Windows shortcut; no unattended installer ID is assumed."
                    });
                }
            }
        }
        finally
        {
            ReleaseComObject(shell);
        }

        return apps;
    }

    private static IReadOnlyList<string> EnumerateShortcutFiles(string root, int maximumFiles, CancellationToken cancellationToken)
    {
        var shortcuts = new List<string>();
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            foreach (var shortcut in Directory.EnumerateFiles(root, "*.lnk", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (shortcuts.Count >= maximumFiles) break;
                shortcuts.Add(shortcut);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return shortcuts;
    }

    private static object? CreateShortcutShell()
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            return shellType is null ? null : Activator.CreateInstance(shellType);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string TryResolveShortcutTarget(object shell, string shortcutPath)
    {
        object? shortcut = null;
        try
        {
            shortcut = shell.GetType().InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                binder: null,
                target: shell,
                args: [shortcutPath]);
            return shortcut?.GetType().InvokeMember(
                "TargetPath",
                BindingFlags.GetProperty,
                binder: null,
                target: shortcut,
                args: null)?.ToString() ?? string.Empty;
        }
        catch (Exception) { return string.Empty; }
        finally
        {
            ReleaseComObject(shortcut);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }

    private static void ApplyRestoreWorkflows(IEnumerable<AppItemModel> apps)
    {
        foreach (var app in apps)
        {
            if (app.PackageId.Equals("Valve.Steam", StringComparison.OrdinalIgnoreCase) ||
                app.Name.Equals("Steam", StringComparison.OrdinalIgnoreCase) ||
                app.Name.Equals("Steam Client", StringComparison.OrdinalIgnoreCase))
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

}
