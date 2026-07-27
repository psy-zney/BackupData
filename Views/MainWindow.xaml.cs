using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using BackupUtility.Models;
using BackupUtility.Services;
using Microsoft.Win32;

namespace BackupUtility.Views
{
    public partial class MainWindow : Window
    {
        public ObservableCollection<AppItemModel> BackupApps { get; set; } = new();
        public ObservableCollection<DataFolderItemModel> BackupDataFolders { get; set; } = new();

        public ObservableCollection<AppItemModel> RestoreApps { get; set; } = new();
        public ObservableCollection<DataFolderItemModel> RestoreDataFolders { get; set; } = new();

        private string _loadedBackupFilePath = string.Empty;
        private CancellationTokenSource? _scanCancellation;
        private bool _isBackupRunning;
        private bool _isRestoreRunning;

        public MainWindow()
        {
            InitializeComponent();
            LvBackupApps.ItemsSource = BackupApps;
            LvBackupData.ItemsSource = BackupDataFolders;
            LvRestoreApps.ItemsSource = RestoreApps;
            LvRestoreData.ItemsSource = RestoreDataFolders;
            Closed += (_, _) => _scanCancellation?.Cancel();

            Log(T("InitialPrompt"));
        }

        private static string T(string key, params object[] arguments)
        {
            var template = Application.Current.TryFindResource(key)?.ToString() ?? key;
            return arguments.Length == 0
                ? template
                : string.Format(CultureInfo.CurrentCulture, template, arguments);
        }

        private void Log(string message)
        {
            if (!Dispatcher.CheckAccess())
            {
                _ = Dispatcher.BeginInvoke(new Action(() => Log(message)));
                return;
            }

            TxtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
            TxtLog.ScrollToEnd();
        }

        private void LanguageSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LanguageSelector.SelectedItem is ComboBoxItem { Tag: string languageCode })
            {
                BackupUtility.App.SetLanguage(languageCode);
            }
        }

        private void SetScanningState(bool isScanning, string status = "")
        {
            BtnScanItems.IsEnabled = !isScanning;
            BtnChooseExport.IsEnabled = !isScanning;
            BtnCreateBackup.IsEnabled = !isScanning && !_isBackupRunning;
            ScanProgressPanel.Visibility = isScanning ? Visibility.Visible : Visibility.Collapsed;
            if (isScanning)
            {
                ScanProgressBar.Value = 0;
                TxtScanStatus.Text = status;
            }
        }

        private async Task ScanExportItemsAsync()
        {
            if (_scanCancellation is not null) return;

            using var cancellation = new CancellationTokenSource();
            _scanCancellation = cancellation;
            SetScanningState(true, "Reading Desktop and Start Menu shortcuts...");

            var progress = new Progress<ScanProgress>(update =>
            {
                ScanProgressBar.Value = update.Percent;
                TxtScanStatus.Text = $"{update.Message} ({update.Percent}%)";
                Log(update.Message);
            });

            try
            {
                Log(T("ScanStarting"));
                var shortcutApps = await AppScannerService.ScanShortcutAppsAsync(cancellation.Token);

                BackupApps.Clear();
                foreach (var app in shortcutApps)
                {
                    BackupApps.Add(app);
                }
                Log(T("ShortcutScanReady", shortcutApps.Count));

                var registryAppsTask = AppScannerService.ScanRegistryInstalledAppsAsync(cancellation.Token);
                var dataFoldersTask = AppScannerService.GetSuggestedDataFoldersAsync(progress, cancellation.Token);
                var wingetAppsTask = AppScannerService.ScanWingetPackagesAsync(progress, cancellation.Token);
                await Task.WhenAll(registryAppsTask, dataFoldersTask);

                var scannedApps = AppScannerService.PrepareApps(shortcutApps.Concat(await registryAppsTask));
                BackupApps.Clear();
                foreach (var app in scannedApps)
                {
                    BackupApps.Add(app);
                }

                BackupDataFolders.Clear();
                foreach (var folder in await dataFoldersTask)
                {
                    BackupDataFolders.Add(folder);
                }
                Log(T("BaseScanReady", scannedApps.Count, BackupDataFolders.Count));

                var wingetApps = await wingetAppsTask;
                if (wingetApps.Count > 0)
                {
                    scannedApps = AppScannerService.PrepareApps(scannedApps.Concat(wingetApps));
                    BackupApps.Clear();
                    foreach (var app in scannedApps)
                    {
                        BackupApps.Add(app);
                    }
                }

                ScanProgressBar.Value = 100;
                TxtScanStatus.Text = T("ScanCompleteStatus");
                Log(T("ScanFound", scannedApps.Count, BackupDataFolders.Count));
            }
            catch (OperationCanceledException)
            {
                Log(T("ScanCancelled"));
            }
            catch (Exception ex)
            {
                Log(T("ScanFailed", ex.Message));
                MessageBox.Show(T("ScanWarningMessage"), T("ScanWarningTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                _scanCancellation = null;
                SetScanningState(false);
            }
        }

        private async void BtnChooseExport_Click(object sender, RoutedEventArgs e)
        {
            ModeSelector.Visibility = Visibility.Collapsed;
            MainTabs.Visibility = Visibility.Visible;
            MainTabs.SelectedIndex = 0;
            await ScanExportItemsAsync();
        }

        private void BtnChooseImport_Click(object sender, RoutedEventArgs e)
        {
            ModeSelector.Visibility = Visibility.Collapsed;
            MainTabs.Visibility = Visibility.Visible;
            MainTabs.SelectedIndex = 1;
            Log(T("SelectImportPrompt"));
        }

        private async void BtnScanApps_Click(object sender, RoutedEventArgs e)
        {
            await ScanExportItemsAsync();
        }

        private async void BtnCreateBackup_Click(object sender, RoutedEventArgs e)
        {
            if (_isBackupRunning) return;
            var selectedApps = BackupApps.Where(a => a.IsSelected).ToList();
            var selectedFolders = BackupDataFolders.Where(d => d.IsSelected).ToList();

            if (selectedApps.Count == 0 && selectedFolders.Count == 0)
            {
                MessageBox.Show(T("SelectBackupItemsWarning"), T("NoticeTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Filter = "Zney Backup (*.zney)|*.zney",
                DefaultExt = ".zney",
                AddExtension = true,
                InitialDirectory = GetBackupOutputDirectory(),
                FileName = $"Zney_Backup_{DateTime.Now:yyyyMMdd_HHmmss}.zney"
            };

            if (dialog.ShowDialog() == true)
            {
                _isBackupRunning = true;
                BtnCreateBackup.IsEnabled = false;
                MainTabs.IsEnabled = false;
                try
                {
                    Log(T("BackupStarting", dialog.FileName));
                    bool success = await BackupRestoreService.CreateBackupPackageAsync(
                        dialog.FileName,
                        selectedApps,
                        selectedFolders,
                        Log);

                    if (success)
                    {
                        MessageBox.Show(T("BackupSuccessMessage"), T("SuccessTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        MessageBox.Show(T("BackupFailureMessage"), T("BackupErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                finally
                {
                    _isBackupRunning = false;
                    BtnCreateBackup.IsEnabled = true;
                    MainTabs.IsEnabled = true;
                }
            }
        }

        private static string GetBackupOutputDirectory()
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Zney Backups");
            try
            {
                Directory.CreateDirectory(directory);
                return directory;
            }
            catch (Exception)
            {
                return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
        }

        private async void BtnOpenBackupFile_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Zney Backup (*.zney)|*.zney",
                DefaultExt = ".zney"
            };

            if (dialog.ShowDialog() == true)
            {
                var selectedBackupPath = dialog.FileName;
                Log(T("ReadingBackup", selectedBackupPath));

                var manifest = await BackupRestoreService.ReadManifestFromPackageAsync(selectedBackupPath);
                if (manifest == null)
                {
                    _loadedBackupFilePath = string.Empty;
                    RestoreApps.Clear();
                    RestoreDataFolders.Clear();
                    TxtSelectedBackupFile.SetResourceReference(TextBlock.TextProperty, "NoBackupSelected");
                    MessageBox.Show(T("InvalidBackupMessage"), T("ErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                _loadedBackupFilePath = selectedBackupPath;
                TxtSelectedBackupFile.Text = Path.GetFileName(_loadedBackupFilePath);
                RestoreApps.Clear();
                foreach (var app in manifest.Apps)
                {
                    app.IsSelected = false;
                    RestoreApps.Add(app);
                }

                RestoreDataFolders.Clear();
                foreach (var folder in manifest.DataFolders)
                {
                    folder.IsSelected = false;
                    RestoreDataFolders.Add(folder);
                }

                Log(T("BackupReadSuccess", manifest.Apps.Count, manifest.DataFolders.Count));
            }
        }

        private async void BtnStartRestore_Click(object sender, RoutedEventArgs e)
        {
            if (_isRestoreRunning) return;
            if (string.IsNullOrEmpty(_loadedBackupFilePath) || !File.Exists(_loadedBackupFilePath))
            {
                MessageBox.Show(T("SelectValidBackupWarning"), T("NoticeTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var appsToRestore = RestoreApps.Where(a => a.IsSelected).ToList();
            var dataToRestore = RestoreDataFolders.Where(d => d.IsSelected).ToList();

            if (appsToRestore.Count == 0 && dataToRestore.Count == 0)
            {
                MessageBox.Show(T("SelectRestoreItemsWarning"), T("NoticeTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Log(T("RestoreStarted"));
            var confirmation = MessageBox.Show(
                T("RestoreConfirmMessage"),
                T("RestoreConfirmTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirmation != MessageBoxResult.Yes) return;

            _isRestoreRunning = true;
            BtnRestoreSelected.IsEnabled = false;
            MainTabs.IsEnabled = false;
            try
            {
                var restoreErrors = new List<string>();
                var restoredFiles = 0;

            // 1. Run only the supported workflow for each selected application.
            if (appsToRestore.Count > 0)
            {
                Log(T("ProcessingApps", appsToRestore.Count));
                foreach (var app in appsToRestore)
                {
                    var workflowResult = await AppRestoreWorkflowService.RestoreAsync(app, Log);
                    Log(workflowResult.Message);
                    if (!workflowResult.Succeeded && !workflowResult.Skipped)
                    {
                        restoreErrors.Add(workflowResult.Message);
                    }
                }
            }

            // 2. Phục hồi Local Data
            if (dataToRestore.Count > 0)
            {
                Log(T("RestoringData", dataToRestore.Count));
                var restoreResult = await BackupRestoreService.RestoreSelectedDataAsync(_loadedBackupFilePath, dataToRestore, Log);
                restoredFiles = restoreResult.RestoredFiles;
                restoreErrors.AddRange(restoreResult.Errors);
            }

            foreach (var error in restoreErrors)
            {
                Log(T("WarningPrefix", error));
            }

            if (restoreErrors.Count > 0)
            {
                Log(T("RestorePartialLog", restoredFiles));
                MessageBox.Show(T("RestorePartialMessage"), T("RestorePartialTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Log(T("RestoreDoneLog"));
            MessageBox.Show(T("RestoreDoneMessage"), T("SuccessTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Log(T("RestoreFailedLog", ex.Message));
                MessageBox.Show(T("RestoreFailedMessage"), T("RestoreWarningTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                _isRestoreRunning = false;
                BtnRestoreSelected.IsEnabled = true;
                MainTabs.IsEnabled = true;
            }
        }
    }
}
