using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
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

        public MainWindow()
        {
            InitializeComponent();
            LvBackupApps.ItemsSource = BackupApps;
            LvBackupData.ItemsSource = BackupDataFolders;
            LvRestoreApps.ItemsSource = RestoreApps;
            LvRestoreData.ItemsSource = RestoreDataFolders;

            Log("Ứng dụng sao lưu & phục hồi sẵn sàng. Hãy bấm 'Quét Phần Mềm Đã Cài' để bắt đầu.");
            LoadDefaultDataFolders();
        }

        private void Log(string message)
        {
            Dispatcher.Invoke(() =>
            {
                TxtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
                TxtLog.ScrollToEnd();
            });
        }

        private void LoadDefaultDataFolders()
        {
            var suggested = AppScannerService.GetSuggestedDataFolders();
            BackupDataFolders.Clear();
            foreach (var item in suggested)
            {
                BackupDataFolders.Add(item);
            }
        }

        private async void BtnScanApps_Click(object sender, RoutedEventArgs e)
        {
            Log("Đang quét phần mềm trên hệ thống...");
            BackupApps.Clear();
            var scanned = await AppScannerService.ScanInstalledAppsAsync();
            foreach (var app in scanned)
            {
                BackupApps.Add(app);
            }
            Log($"Tìm thấy {scanned.Count} ứng dụng đã cài.");
        }

        private async void BtnCreateBackup_Click(object sender, RoutedEventArgs e)
        {
            var selectedApps = BackupApps.Where(a => a.IsSelected).ToList();
            var selectedFolders = BackupDataFolders.Where(d => d.IsSelected).ToList();

            if (selectedApps.Count == 0 && selectedFolders.Count == 0)
            {
                MessageBox.Show("Vui lòng chọn ít nhất 1 ứng dụng hoặc 1 thư mục data để sao lưu!", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Filter = "Zney Backup (*.zney)|*.zney",
                DefaultExt = ".zney",
                AddExtension = true,
                FileName = $"Zney_Backup_{DateTime.Now:yyyyMMdd_HHmmss}.zney"
            };

            if (dialog.ShowDialog() == true)
            {
                Log($"Bắt đầu xuất backup ra file: {dialog.FileName}");
                bool success = await BackupRestoreService.CreateBackupPackageAsync(
                    dialog.FileName,
                    selectedApps,
                    selectedFolders,
                    Log);

                if (success)
                {
                    MessageBox.Show("Sao lưu hoàn tất thành công!", "Thành công", MessageBoxButton.OK, MessageBoxImage.Information);
                }
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
                _loadedBackupFilePath = dialog.FileName;
                TxtSelectedBackupFile.Text = Path.GetFileName(_loadedBackupFilePath);
                Log($"Đang đọc file backup: {_loadedBackupFilePath}...");

                var manifest = await BackupRestoreService.ReadManifestFromPackageAsync(_loadedBackupFilePath);
                if (manifest == null)
                {
                    MessageBox.Show("File backup không hợp lệ hoặc bị lỗi!", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                RestoreApps.Clear();
                foreach (var app in manifest.Apps)
                {
                    RestoreApps.Add(app);
                }

                RestoreDataFolders.Clear();
                foreach (var folder in manifest.DataFolders)
                {
                    RestoreDataFolders.Add(folder);
                }

                Log($"Đọc file thành công. Tìm thấy {manifest.Apps.Count} ứng dụng và {manifest.DataFolders.Count} gói dữ liệu.");
            }
        }

        private async void BtnStartRestore_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_loadedBackupFilePath) || !File.Exists(_loadedBackupFilePath))
            {
                MessageBox.Show("Vui lòng chọn file backup hợp lệ trước!", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var appsToRestore = RestoreApps.Where(a => a.IsSelected).ToList();
            var dataToRestore = RestoreDataFolders.Where(d => d.IsSelected).ToList();

            if (appsToRestore.Count == 0 && dataToRestore.Count == 0)
            {
                MessageBox.Show("Vui lòng tích chọn ít nhất 1 app hoặc 1 thư mục data muốn phục hồi!", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Log("=== BẮT ĐẦU TIẾN TRÌNH PHỤC HỒI ===");
            var restoreErrors = new List<string>();
            var restoredFiles = 0;

            // 1. Run only the supported workflow for each selected application.
            if (appsToRestore.Count > 0)
            {
                Log($"Bắt đầu xử lý {appsToRestore.Count} ứng dụng theo luồng đã lưu...");
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
                Log($"Bắt đầu giải nén khôi phục {dataToRestore.Count} thư mục data...");
                var restoreResult = await BackupRestoreService.RestoreSelectedDataAsync(_loadedBackupFilePath, dataToRestore, Log);
                restoredFiles = restoreResult.RestoredFiles;
                restoreErrors.AddRange(restoreResult.Errors);
            }

            foreach (var error in restoreErrors)
            {
                Log($"CẢNH BÁO: {error}");
            }

            if (restoreErrors.Count > 0)
            {
                Log($"Đã khôi phục {restoredFiles} tệp; một số mục bị bỏ qua hoặc thất bại.");
                MessageBox.Show("Khôi phục hoàn tất nhưng có mục không an toàn hoặc không hợp lệ đã bị bỏ qua. Xem nhật ký để biết chi tiết.", "Hoàn tất có cảnh báo", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Log("=== HOÀN TẤT PHỤC HỒI ===");
            MessageBox.Show("Tiến trình phục hồi hoàn tất!", "Thành công", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
