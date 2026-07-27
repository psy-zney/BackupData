using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BackupUtility.Models;
using Microsoft.Win32;

namespace BackupUtility.Services
{
    public class AppScannerService
    {
        public static async Task<List<AppItemModel>> ScanInstalledAppsAsync()
        {
            var appsList = new List<AppItemModel>();

            // 1. Try Winget export first
            string tempJson = Path.Combine(Path.GetTempPath(), "winget_export_temp.json");
            if (await WingetService.IsWingetAvailableAsync())
            {
                try
                {
                    var wingetApps = await WingetService.ExportWingetPackagesAsync(tempJson);
                    if (wingetApps.Count > 0)
                    {
                        appsList.AddRange(wingetApps);
                    }
                }
                finally
                {
                    if (File.Exists(tempJson)) File.Delete(tempJson);
                }
            }

            // 2. Scan Registry for additional apps
            var regApps = GetRegistryInstalledApps();
            foreach (var rApp in regApps)
            {
                if (!appsList.Any(a => a.Name.Equals(rApp.Name, StringComparison.OrdinalIgnoreCase) || 
                                       (!string.IsNullOrEmpty(a.PackageId) && a.PackageId.Equals(rApp.PackageId, StringComparison.OrdinalIgnoreCase))))
                {
                    appsList.Add(rApp);
                }
            }

            ApplyRestoreWorkflows(appsList);
            return appsList
                .GroupBy(app => app.RestoreWorkflow.Equals("Steam", StringComparison.OrdinalIgnoreCase) ? "steam" : $"{app.Source}:{app.PackageId}", StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(app => app.Name)
                .ToList();
        }

        private static List<AppItemModel> GetRegistryInstalledApps()
        {
            var list = new List<AppItemModel>();
            string[] registryKeys = new string[]
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            };

            foreach (var keyPath in registryKeys)
            {
                using var key = Registry.LocalMachine.OpenSubKey(keyPath);
                if (key == null) continue;

                foreach (var subkeyName in key.GetSubKeyNames())
                {
                    using var subkey = key.OpenSubKey(subkeyName);
                    if (subkey == null) continue;

                    string displayName = subkey.GetValue("DisplayName")?.ToString() ?? "";
                    string displayVersion = subkey.GetValue("DisplayVersion")?.ToString() ?? "";
                    string publisher = subkey.GetValue("Publisher")?.ToString() ?? "";
                    string systemComponent = subkey.GetValue("SystemComponent")?.ToString() ?? "";

                    if (!string.IsNullOrWhiteSpace(displayName) && systemComponent != "1")
                    {
                        list.Add(new AppItemModel
                        {
                            Name = displayName,
                            PackageId = subkeyName,
                            Version = displayVersion,
                            Publisher = publisher,
                            Source = "Registry",
                            IsSelected = true
                        });
                    }
                }
            }

            return list;
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
                    app.RestoreInstructions = "Cài Steam, mở Steam và đăng nhập. Danh sách/tải game chỉ thực hiện trong Steam sau khi đăng nhập; ứng dụng không tự tải game.";
                }
                else if (app.Source.Equals("winget", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(app.PackageId))
                {
                    app.RestoreWorkflow = "Winget";
                    app.RestoreInstructions = "Cài tự động bằng winget.";
                }
                else
                {
                    app.RestoreWorkflow = "Manual";
                    app.IsSelected = false;
                    app.RestoreInstructions = "Không có mã nguồn cài đặt đáng tin cậy; chỉ lưu thông tin, không tự tải/cài đặt.";
                }
            }
        }

        public static List<DataFolderItemModel> GetSuggestedDataFolders()
        {
            var list = new List<DataFolderItemModel>();
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            var checkFolders = new (string Name, string SourcePath, string TargetEnv, string Category, bool IsSelected)[]
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

            foreach (var item in checkFolders)
            {
                if (Directory.Exists(item.SourcePath) || File.Exists(item.SourcePath))
                {
                    long size = 0;
                    if (Directory.Exists(item.SourcePath))
                    {
                        try
                        {
                            size = new DirectoryInfo(item.SourcePath).EnumerateFiles("*", SearchOption.AllDirectories).Sum(fi => fi.Length);
                        }
                        catch { }
                    }
                    else if (File.Exists(item.SourcePath))
                    {
                        size = new FileInfo(item.SourcePath).Length;
                    }

                    list.Add(new DataFolderItemModel
                    {
                        Name = item.Name,
                        Category = item.Category,
                        SourcePath = item.SourcePath,
                        TargetPath = item.TargetEnv,
                        RelativeArchivePath = item.Name.Replace(" ", "_"),
                        SizeBytes = size,
                        IsSelected = item.IsSelected
                    });
                }
            }

            list.Add(new DataFolderItemModel
            {
                Name = "Windows personalization and Explorer settings",
                Category = "WindowsSettings",
                TargetPath = "%LOCALAPPDATA%\\ZneyBackup",
                RelativeArchivePath = "Windows_Settings",
                IsSelected = true
            });

            return list;
        }
    }
}
