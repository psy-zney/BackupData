using System;
using System.Collections.Generic;

namespace BackupUtility.Models
{
    public class AppItemModel
    {
        public string Name { get; set; } = string.Empty;
        public string PackageId { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string Publisher { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty; // e.g. winget, registry
        // Winget is unattended. Steam and Manual require an explicit user step.
        public string RestoreWorkflow { get; set; } = "Manual";
        public bool RequiresInteractiveLogin { get; set; }
        public string RestoreInstructions { get; set; } = string.Empty;
        public bool IsSelected { get; set; } = true;
    }

    public class DataFolderItemModel
    {
        public string Name { get; set; } = string.Empty;
        public string Category { get; set; } = "ApplicationSettings";
        // May differ from TargetPath for known folders redirected to OneDrive.
        public string SourcePath { get; set; } = string.Empty;
        public string RelativeArchivePath { get; set; } = string.Empty;
        public string ArchiveEntryName { get; set; } = string.Empty;
        public string SettingsEntryName { get; set; } = string.Empty;
        public string TargetPath { get; set; } = string.Empty; // e.g., %APPDATA%\AppName
        public string FileHash { get; set; } = string.Empty; // SHA256 checksum
        // Keys are paths relative to this data item, using forward slashes.
        // Keeping the hash for every file lets restore verify content before it
        // overwrites anything on the current machine.
        public Dictionary<string, string> FileHashes { get; set; } = new();
        public long SizeBytes { get; set; }
        public bool IsSelected { get; set; } = true;
    }

    public class BackupManifest
    {
        public int FormatVersion { get; set; } = 3;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public string MachineName { get; set; } = Environment.MachineName;
        public string OSVersion { get; set; } = Environment.OSVersion.ToString();
        public List<AppItemModel> Apps { get; set; } = new();
        public List<DataFolderItemModel> DataFolders { get; set; } = new();
    }
}
