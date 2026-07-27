using System;
using System.Collections.Generic;
using BackupUtility.Models;
using Microsoft.Win32;

namespace BackupUtility.Services
{
    public sealed class WindowsSettingsSnapshot
    {
        public Dictionary<string, int> ExplorerAdvanced { get; set; } = new();
        public Dictionary<string, int> Personalization { get; set; } = new();
    }

    public static class WindowsSettingsService
    {
        private static readonly string[] ExplorerValueNames = ["HideFileExt", "Hidden", "ShowSuperHidden", "TaskbarAl"];
        private static readonly string[] PersonalizationValueNames = ["AppsUseLightTheme", "SystemUsesLightTheme", "EnableTransparency"];

        public static WindowsSettingsSnapshot Capture()
        {
            return new WindowsSettingsSnapshot
            {
                ExplorerAdvanced = ReadValues(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", ExplorerValueNames),
                Personalization = ReadValues(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", PersonalizationValueNames)
            };
        }

        public static void Apply(WindowsSettingsSnapshot snapshot, Action<string>? onProgress = null)
        {
            WriteAllowedValues(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", ExplorerValueNames, snapshot.ExplorerAdvanced, onProgress);
            WriteAllowedValues(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", PersonalizationValueNames, snapshot.Personalization, onProgress);
            onProgress?.Invoke("Selected Windows settings were applied. Some changes may require signing out and back in.");
        }

        private static Dictionary<string, int> ReadValues(string keyPath, IEnumerable<string> names)
        {
            var values = new Dictionary<string, int>();
            using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
            if (key is null) return values;
            foreach (var name in names)
            {
                if (key.GetValue(name) is int value) values[name] = value;
            }
            return values;
        }

        private static void WriteAllowedValues(string keyPath, IEnumerable<string> allowedNames, IReadOnlyDictionary<string, int> values, Action<string>? onProgress)
        {
            using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
            if (key is null) return;
            foreach (var name in allowedNames)
            {
                if (values.TryGetValue(name, out var value))
                {
                    key.SetValue(name, value, RegistryValueKind.DWord);
                    onProgress?.Invoke($"Applied Windows setting: {name}.");
                }
            }
        }
    }
}
