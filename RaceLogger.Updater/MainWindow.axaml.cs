using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RaceLogger.Updater
{
    public partial class MainWindow : Window
    {
        private string[] _args;
        private bool _isDetailsOpen = false;

        public MainWindow()
        {
            InitializeComponent();
        }

        public MainWindow(string[] args)
        {
            InitializeComponent();
            _args = args;

            // Start the update process on a background thread so the UI does not freeze
            Task.Run(() => PerformUpdateAsync());
        }

        private void DetailsToggle_PointerPressed(object sender, PointerPressedEventArgs e)
        {
            _isDetailsOpen = !_isDetailsOpen;
            DetailsPanel.IsVisible = _isDetailsOpen;
            DetailsArrowText.Text = _isDetailsOpen ? "^" : "v";
        }

        private void LogMessage(string message)
        {
            Dispatcher.UIThread.Post(() =>
            {
                LogText.Text += message + "\n";
                LogScrollViewer.ScrollToEnd(); // Auto-scroll to the newest log entry
            });
        }

        private void UpdateProgress(int percentage)
        {
            Dispatcher.UIThread.Post(() =>
            {
                UpdateProgressBar.Value = percentage;
                PercentageText.Text = $"{percentage}%";
            });
        }

        private async Task PerformUpdateAsync()
        {
            // Expected arguments: 
            // [0] = PID (Process ID of main app)
            // [1] = Source Folder (Extracted temp files)
            // [2] = Destination Folder (Main app directory)
            // [3] = Executable Name (e.g., RaceLogger.App.exe)
            if (_args == null || _args.Length < 4)
            {
                LogMessage("[ERROR] Missing arguments. Updater must be launched by the main application.");
                await Task.Delay(3000);
                Environment.Exit(1);
                return;
            }

            if (!int.TryParse(_args[0], out int targetPid)) return;
            string updateTempFolder = _args[1];
            string destinationFolder = _args[2];
            string executableName = _args[3];

            try
            {
                LogMessage($"[SYSTEM] Waiting for main application (PID: {targetPid}) to exit...");

                // Wait for the main application to close and release file locks (10 seconds timeout)
                try
                {
                    var raceLoggerProcess = Process.GetProcessById(targetPid);
                    raceLoggerProcess.WaitForExit(10000);
                }
                catch (ArgumentException) { /* Process already closed, safe to proceed */ }

                await Task.Delay(500); // Extra buffer to ensure Windows OS drops the file locks
                LogMessage("[SYSTEM] Main application closed. Initializing update sequence...");

                // Process file copying with UI progress
                if (Directory.Exists(updateTempFolder))
                {
                    var allFiles = Directory.GetFiles(updateTempFolder, "*.*", SearchOption.AllDirectories);
                    int totalFiles = allFiles.Length;
                    int copiedFiles = 0;

                    LogMessage($"[SYSTEM] Found {totalFiles} files to apply.");

                    foreach (string sourceFile in allFiles)
                    {
                        string relativePath = sourceFile.Substring(updateTempFolder.Length + 1);
                        string destinationFile = Path.Combine(destinationFolder, relativePath);

                        // Recreate folder structure if necessary
                        string targetDir = Path.GetDirectoryName(destinationFile);
                        if (targetDir != null && !Directory.Exists(targetDir))
                        {
                            Directory.CreateDirectory(targetDir);
                        }

                        // Copy and overwrite the file
                        File.Copy(sourceFile, destinationFile, true);
                        copiedFiles++;

                        LogMessage($"Copying: {relativePath}");

                        // Update the progress bar
                        int progress = (int)((copiedFiles / (double)totalFiles) * 100);
                        UpdateProgress(progress);

                        // Tiny delay to ensure smooth UI rendering for small and quick file copies
                        await Task.Delay(10);
                    }

                    LogMessage("[SYSTEM] All files updated successfully.");
                    LogMessage("[SYSTEM] Cleaning up temporary update cache...");
                    Directory.Delete(updateTempFolder, true);
                }
                else
                {
                    LogMessage("[ERROR] Update source folder does not exist! Aborting.");
                    await Task.Delay(3000);
                    Environment.Exit(1);
                    return;
                }

                LogMessage("[SYSTEM] Update complete! Relaunching application...");
                await Task.Delay(1000); // Give the user a second to see 100% completion

                // Run the newly updated main executable
                string newExePath = Path.Combine(destinationFolder, executableName);
                if (File.Exists(newExePath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = newExePath,
                        UseShellExecute = true,
                        WorkingDirectory = destinationFolder
                    });
                }
                else
                {
                    LogMessage($"[ERROR] Could not find executable: {newExePath}");
                    await Task.Delay(3000);
                }

                // Safely kill the updater process on the UI thread
                Dispatcher.UIThread.Post(() =>
                {
                    Environment.Exit(0);
                });
            }
            catch (Exception ex)
            {
                LogMessage($"[FATAL ERROR] {ex.Message}");
            }
        }
    }
}