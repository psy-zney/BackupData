using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BackupUtility.Models;

namespace BackupUtility.Services
{
    public class WingetService
    {
        public static async Task<bool> IsWingetAvailableAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "winget",
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                if (proc == null) return false;
                await WaitForExitAsync(proc, timeout, cancellationToken);
                return proc.ExitCode == 0;
            }
            catch (TimeoutException)
            {
                return false;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return false;
            }
        }

        public static async Task<List<AppItemModel>> ExportWingetPackagesAsync(
            string tempJsonPath,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            var apps = new List<AppItemModel>();
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "winget",
                    Arguments = $"export --output \"{tempJsonPath}\" --include-versions --accept-source-agreements --disable-interactivity",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                if (proc == null) return apps;

                await WaitForExitAsync(proc, timeout, cancellationToken);

                if (proc.ExitCode == 0 && File.Exists(tempJsonPath))
                {
                    var jsonContent = await File.ReadAllTextAsync(tempJsonPath);
                    using var doc = JsonDocument.Parse(jsonContent);
                    if (doc.RootElement.TryGetProperty("Sources", out var sources))
                    {
                        foreach (var source in sources.EnumerateArray())
                        {
                            if (source.TryGetProperty("Packages", out var packages))
                            {
                                foreach (var pkg in packages.EnumerateArray())
                                {
                                    if (!pkg.TryGetProperty("PackageIdentifier", out var identifier))
                                        continue;
                                    string pkgId = identifier.GetString() ?? "";
                                    string version = pkg.TryGetProperty("Version", out var v) ? v.GetString() ?? "" : "";
                                    apps.Add(new AppItemModel
                                    {
                                        Name = pkgId,
                                        PackageId = pkgId,
                                        Version = version,
                                        Source = "winget",
                                        IsSelected = true
                                    });
                                }
                            }
                        }
                    }
                }
            }
            catch (TimeoutException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Winget export error: {ex.Message}");
            }
            return apps;
        }

        public static async Task<bool> InstallWingetPackageAsync(string packageId, Action<string>? onLog = null)
        {
            if (string.IsNullOrWhiteSpace(packageId) || packageId.IndexOfAny(['\r', '\n', '"']) >= 0)
            {
                onLog?.Invoke("Gói winget không hợp lệ; đã bỏ qua.");
                return false;
            }
            try
            {
                onLog?.Invoke($"Đang cài đặt {packageId} qua winget...");
                var psi = new ProcessStartInfo
                {
                    FileName = "winget",
                    Arguments = $"install --id \"{packageId}\" --exact --accept-source-agreements --accept-package-agreements --silent",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                if (proc == null) return false;

                proc.OutputDataReceived += (s, e) => { if (e.Data != null) onLog?.Invoke(e.Data); };
                proc.ErrorDataReceived += (s, e) => { if (e.Data != null) onLog?.Invoke(e.Data); };
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                await WaitForExitAsync(proc, TimeSpan.FromMinutes(10), CancellationToken.None, captureOutput: false);
                return proc.ExitCode == 0;
            }
            catch (Exception ex)
            {
                onLog?.Invoke($"Lỗi khi cài {packageId}: {ex.Message}");
                return false;
            }
        }

        private static async Task WaitForExitAsync(
            Process process,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            bool captureOutput = true)
        {
            var outputTask = captureOutput ? process.StandardOutput.ReadToEndAsync() : Task.CompletedTask;
            var errorTask = captureOutput ? process.StandardError.ReadToEndAsync() : Task.CompletedTask;
            var exitTask = process.WaitForExitAsync();

            try
            {
                var completed = await Task.WhenAny(exitTask, Task.Delay(timeout, cancellationToken));
                if (completed != exitTask)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    TryKill(process);
                    await exitTask;
                    throw new TimeoutException($"winget did not finish within {timeout.TotalSeconds:0} seconds.");
                }

                await exitTask;
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }
            finally
            {
                await Task.WhenAll(outputTask, errorTask);
            }
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
    }
}
