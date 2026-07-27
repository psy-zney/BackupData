using System;
using System.Threading.Tasks;
using BackupUtility.Models;

namespace BackupUtility.Services
{
    public sealed class AppRestoreWorkflowResult
    {
        public bool Succeeded { get; init; }
        public bool Skipped { get; init; }
        public string Message { get; init; } = string.Empty;
    }

    public static class AppRestoreWorkflowService
    {
        public static async Task<AppRestoreWorkflowResult> RestoreAsync(AppItemModel app, Action<string>? onLog = null)
        {
            if (app.RestoreWorkflow.Equals("Steam", StringComparison.OrdinalIgnoreCase))
            {
                var installed = await WingetService.InstallWingetPackageAsync("Valve.Steam", onLog);
                return installed
                    ? new AppRestoreWorkflowResult
                    {
                        Succeeded = true,
                        Message = "Steam was installed. Open Steam, sign in, and choose games from your library. Zney does not access credentials or download games."
                    }
                    : new AppRestoreWorkflowResult { Message = "Steam could not be installed through winget." };
            }

            if (app.RestoreWorkflow.Equals("Winget", StringComparison.OrdinalIgnoreCase))
            {
                var installed = await WingetService.InstallWingetPackageAsync(app.PackageId, onLog);
                return installed
                    ? new AppRestoreWorkflowResult { Succeeded = true, Message = $"Installed {app.Name}." }
                    : new AppRestoreWorkflowResult { Message = $"{app.Name} could not be installed through winget." };
            }

            return new AppRestoreWorkflowResult
            {
                Skipped = true,
                Message = $"Skipped {app.Name}: {app.RestoreInstructions}"
            };
        }
    }
}
