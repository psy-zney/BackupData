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
                        Message = "Steam đã được cài. Hãy mở Steam, đăng nhập và tự chọn game trong thư viện. Ứng dụng không truy cập phiên đăng nhập hoặc tự tải game."
                    }
                    : new AppRestoreWorkflowResult { Message = "Không thể cài Steam qua winget." };
            }

            if (app.RestoreWorkflow.Equals("Winget", StringComparison.OrdinalIgnoreCase))
            {
                var installed = await WingetService.InstallWingetPackageAsync(app.PackageId, onLog);
                return installed
                    ? new AppRestoreWorkflowResult { Succeeded = true, Message = $"Đã cài {app.Name}." }
                    : new AppRestoreWorkflowResult { Message = $"Không thể cài {app.Name} qua winget." };
            }

            return new AppRestoreWorkflowResult
            {
                Skipped = true,
                Message = $"Bỏ qua {app.Name}: {app.RestoreInstructions}"
            };
        }
    }
}
