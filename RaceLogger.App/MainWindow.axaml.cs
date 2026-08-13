using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Win32;
using RaceLogger.App.Models;
using RaceLogger.App.Services;
using RaceLogger.App.Views;
using rF2SMMonitor;
using rF2SMMonitor.rFactor2Data;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RaceLogger.App
{
    public partial class MainWindow : Window
    {
        private bool _isLogging = false;
        private static readonly HttpClient client = new HttpClient();

        private readonly LeaderboardService _leaderboardService = new LeaderboardService();

        // CACHE - Store lap times here to avoid redownloading on every filter click
        private List<LapRecord> _cachedLeaderboard = new List<LapRecord>();
        private readonly string _cacheFilePath = "records_cache.json"; // Local database file

        // Application State & Settings Variables
        private string _settingsPath = "settings.json";
        private string _loadedWebhookUrl = "";
        private bool _loadedDevMode = false;
        private int _loadedStartupMode = 0;
        private int _loadedExitAction = 0;

        private bool _hasUnsavedChanges = false;
        private bool _isLoadingSettings = false;
        private bool _forceExit = false;

        private bool _isChangingSelectionProgrammatically = false;
        private Control _pendingView = null;
        private ListBox _pendingNavList = null;
        private int _pendingNavIndex = -1;
        private Dictionary<string, string> _carDatabase = new Dictionary<string, string>();

        private readonly UpdateService _updateService = new UpdateService(); // OTA Update Service

        private string _pendingZipUrl = null;
        private string _pendingHashUrl = null;

        public MainWindow()
        {
            InitializeComponent();
            LoadSettings();
            LoadCarDatabase();

            if (CurrentVersionText != null)
            {
                CurrentVersionText.Text = $"Current Version: {_updateService.CurrentVersion}";
            }

            TrackDetailsControl.BackRequested += (s, e) =>
            {
                NavigateTo(LeaderboardView, MainNavList, 2);
                RefreshLeaderboardAsync(forceDownload: false);
            };

            _isLogging = true;
            Task.Run(() => TelemetryLoop());
        }

        private void LogMessage(string message)
        {
            Debug.WriteLine(message);
            string timestamped = $"[{DateTime.Now:HH:mm:ss}] {message}\n";

            Dispatcher.UIThread.Post(() =>
            {
                if (ConsoleOutputTextBox != null)
                {
                    ConsoleOutputTextBox.Text = (ConsoleOutputTextBox.Text ?? "") + timestamped;
                    ConsoleOutputTextBox.CaretIndex = ConsoleOutputTextBox.Text.Length;
                }
            });
        }

        private void LoadSettings()
        {
            _isLoadingSettings = true;
            try
            {
                if (File.Exists(_settingsPath))
                {
                    string json = File.ReadAllText(_settingsPath);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json);
                    if (settings != null)
                    {
                        _loadedWebhookUrl = settings.WebhookUrl;
                        _loadedDevMode = settings.DeveloperMode;
                        _loadedStartupMode = settings.StartupMode;
                        _loadedExitAction = settings.ExitAction;

                        SheetsLinkInput.Text = _loadedWebhookUrl;
                        DevModeToggle.IsChecked = _loadedDevMode;
                        StartupComboBox.SelectedIndex = _loadedStartupMode;
                        ExitActionComboBox.SelectedIndex = _loadedExitAction;

                        if (DevConsoleMenuItem != null)
                        {
                            DevConsoleMenuItem.IsVisible = _loadedDevMode;
                        }
                    }
                }
                else
                {
                    StartupComboBox.SelectedIndex = 0;
                    ExitActionComboBox.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                LogMessage($"[SYSTEM] Error loading settings: {ex.Message}");
            }

            _isLoadingSettings = false;
            _hasUnsavedChanges = false;
        }

        private async void SaveSettings()
        {
            try
            {
                int newStartupMode = StartupComboBox.SelectedIndex;

                var settings = new AppSettings
                {
                    WebhookUrl = SheetsLinkInput.Text ?? "",
                    DeveloperMode = DevModeToggle.IsChecked ?? false,
                    StartupMode = newStartupMode,
                    ExitAction = ExitActionComboBox.SelectedIndex
                };

                string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_settingsPath, json);

                if (_loadedStartupMode != newStartupMode)
                {
                    ApplyStartupRegistry(newStartupMode);
                }

                _loadedWebhookUrl = settings.WebhookUrl;
                _loadedDevMode = settings.DeveloperMode;
                _loadedStartupMode = settings.StartupMode;
                _loadedExitAction = settings.ExitAction;
                _hasUnsavedChanges = false;

                if (DevConsoleMenuItem != null)
                {
                    DevConsoleMenuItem.IsVisible = _loadedDevMode;
                }

                if (!_loadedDevMode && DevConsoleView != null && DevConsoleView.IsVisible)
                {
                    NavigateTo(DashboardView, MainNavList, 0);
                }

                if (SavedNotification != null)
                {
                    SavedNotification.IsVisible = true;
                    await Task.Delay(3000);
                    SavedNotification.IsVisible = false;
                }
            }
            catch (Exception ex)
            {
                LogMessage($"[SYSTEM] Error saving settings: {ex.Message}");
            }
        }

        private void LoadCarDatabase()
        {
            try
            {
                string path = "cars.json";
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);

                    var options = new JsonSerializerOptions
                    {
                        ReadCommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true
                    };

                    var db = JsonSerializer.Deserialize<Dictionary<string, string>>(json, options);

                    _carDatabase = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (db != null)
                    {
                        foreach (var kvp in db)
                        {
                            _carDatabase[kvp.Key] = kvp.Value;
                        }
                    }
                    LogMessage($"[SYSTEM] Loaded {_carDatabase.Count} exact car matches from database.");
                }
                else
                {
                    LogMessage("[SYSTEM] cars.json not found. Creating empty database.");
                    File.WriteAllText(path, "{\n  \n}");
                }
            }
            catch (Exception ex)
            {
                LogMessage($"[SYSTEM] Error loading cars.json: {ex.Message}");
            }
        }

        private void ApplyStartupRegistry(int startupMode)
        {
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
                    {
                        if (key != null)
                        {
                            if (startupMode == 0) // Disabled
                            {
                                key.DeleteValue("RaceLogger", false);
                                LogMessage("[SYSTEM] Removed app from Windows Startup.");
                            }
                            else // Enabled (1 = Visible, 2 = Hidden)
                            {
                                string appPath = Environment.ProcessPath;
                                if (startupMode == 2)
                                {
                                    appPath += " --hidden";
                                }
                                key.SetValue("RaceLogger", appPath);
                                LogMessage($"[SYSTEM] Added app to Windows Startup (Path: {appPath}).");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogMessage($"[SYSTEM] Failed to update startup registry: {ex.Message}");
                }
            }
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            if (!_forceExit && _loadedExitAction == 1) // 1 = Hide to tray
            {
                e.Cancel = true;
                this.Hide();
                LogMessage("[SYSTEM] Window hidden. Process continues in background.");
            }
            else
            {
                _isLogging = false;
                base.OnClosing(e);
            }
        }

        // -------------------------------------------------------------------------
        // LOGIC: Background Sync & Cache Management
        // -------------------------------------------------------------------------
        private void RefreshLeaderboardAsync(string forcedClass = null, bool forceDownload = false)
        {
            if (string.IsNullOrWhiteSpace(_loadedWebhookUrl))
            {
                LogMessage("[SYSTEM] Cannot fetch leaderboard: Webhook URL is empty.");
                return;
            }

            // Load from local disk (Only when memory is empty)
            if (_cachedLeaderboard.Count == 0 && File.Exists(_cacheFilePath))
            {
                try
                {
                    string json = File.ReadAllText(_cacheFilePath);
                    var loaded = JsonSerializer.Deserialize<List<LapRecord>>(json);
                    if (loaded != null)
                    {
                        _cachedLeaderboard = loaded;
                        LogMessage($"[SYSTEM] Read {_cachedLeaderboard.Count} records from local cache.");
                    }
                }
                catch (Exception ex)
                {
                    LogMessage($"[SYSTEM] Local cache read error: {ex.Message}");
                }
            }

            // Instant UI render (Using whatever is in memory right now)
            UpdateLeaderboardUI(forcedClass);

            // Silent background sync
            if (forceDownload)
            {
                LogMessage("[SYSTEM] Background sync initiated...");

                _ = Task.Run(async () =>
                {
                    try
                    {
                        // Fetch from Google API silently
                        var newRecords = await _leaderboardService.FetchRawDataAsync(_loadedWebhookUrl, msg => Debug.WriteLine(msg));

                        if (newRecords != null && newRecords.Count > 0)
                        {
                            // Safely switch back to the main UI thread to apply the new data
                            Dispatcher.UIThread.Post(() =>
                            {
                                // Check if there are new laps
                                if (newRecords.Count != _cachedLeaderboard.Count)
                                {
                                    _cachedLeaderboard = newRecords;

                                    // Save the fresh database to the local file
                                    File.WriteAllText(_cacheFilePath, JsonSerializer.Serialize(_cachedLeaderboard));

                                    LogMessage($"[SYSTEM] Sync complete! Local database updated to {newRecords.Count} records.");

                                    // Refresh the active views seamlessly
                                    if (LeaderboardView.IsVisible) UpdateLeaderboardUI(forcedClass);
                                    if (TrackDetailsControl.IsVisible) TrackDetailsControl.UpdateDataSilently(_cachedLeaderboard);
                                }
                                else
                                {
                                    LogMessage("[SYSTEM] Sync complete. Local database is already up to date.");
                                }
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.UIThread.Post(() => LogMessage($"[SYSTEM] Background sync failed: {ex.Message}"));
                    }
                });
            }
        }

        // -------------------------------------------------------------------------
        // UI: Visual Rendering of the Main Leaderboard Cards
        // -------------------------------------------------------------------------
        private async void UpdateLeaderboardUI(string forcedClass)
        {
            // Small delay to ensure the UI has fully rendered before manipulating it
            await Task.Delay(50);

            string selectedClass = forcedClass;
            if (string.IsNullOrEmpty(selectedClass))
            {
                if (FilterGt3?.IsChecked == true) selectedClass = "GT3";
                else if (FilterGte?.IsChecked == true) selectedClass = "GTE";
                else if (FilterLmp3?.IsChecked == true) selectedClass = "LMP3";
                else if (FilterLmp2?.IsChecked == true) selectedClass = "LMP2";
                else if (FilterHyp?.IsChecked == true) selectedClass = "HYP";
                else selectedClass = "GT3"; // Default fallback
            }

            LogMessage($"[UI] Drawing leaderboard for class: {selectedClass}");

            var buttons = LeaderboardView.GetVisualDescendants()
                                         .OfType<Button>()
                                         .Where(b => b.Classes.Contains("trackCard"))
                                         .ToList();

            foreach (var btn in buttons)
            {
                string tag = btn.Tag as string;
                if (string.IsNullOrEmpty(tag)) continue;

                // Extract all runs on this track and in the SELECTED class from the cache
                var trackRecords = _cachedLeaderboard.Where(r => IsTrackMatch(tag, r.Track) && IsClassMatch(r.CarClass, selectedClass)).ToList();

                // Group by DRIVER to find the Top 3 best players in the selected class
                var topEntries = trackRecords
                    .Where(r => r.LapTime != TimeSpan.MaxValue)
                    .GroupBy(r => r.Driver)
                    .Select(group =>
                    {
                        var bestLap = group.OrderBy(r => r.LapTime).First();
                        return new
                        {
                            DriverInitials = GetInitials(bestLap.Driver),
                            LapTimeStr = bestLap.RawLapTimeStr
                        };
                    })
                    .OrderBy(entry => entry.LapTimeStr)
                    .Take(3)
                    .ToList();

                var stackPanel = btn.GetVisualDescendants().OfType<StackPanel>().FirstOrDefault();
                if (stackPanel == null) continue;

                var textBlocks = stackPanel.Children.OfType<TextBlock>().ToList();

                // Reset TextBlocks in the card
                foreach (var tb in textBlocks)
                {
                    tb.Text = "";
                    tb.Foreground = SolidColorBrush.Parse("#a0a0a0");
                    tb.FontWeight = Avalonia.Media.FontWeight.Normal;
                }

                if (topEntries.Count == 0)
                {
                    if (textBlocks.Count > 0) textBlocks[0].Text = "No times yet";
                    continue;
                }

                // Fill in the places in the card
                for (int i = 0; i < topEntries.Count && i < textBlocks.Count; i++)
                {
                    var entry = topEntries[i];
                    textBlocks[i].Text = $"{entry.DriverInitials} - {entry.LapTimeStr}";

                    if (i == 0) // Highlight leader in bright color
                    {
                        textBlocks[i].Foreground = SolidColorBrush.Parse("#e4e4e4");
                        textBlocks[i].FontWeight = Avalonia.Media.FontWeight.SemiBold;
                    }
                }
            }
        }

        private bool IsTrackMatch(string tag, string rawTrackName)
        {
            if (string.IsNullOrEmpty(rawTrackName)) return false;
            var t = rawTrackName.ToLower();

            return tag switch
            {
                "Spa" => t.Contains("spa"),
                "Monza" => t.Contains("monza"),
                "DeLaSarthe" => t.Contains("sarthe") || t.Contains("le mans"),
                "Fuji" => t.Contains("fuji"),
                "Bahrain" => t.Contains("bahrain") || t.Contains("sakhir"),
                "Interlagos" => t.Contains("pace") || t.Contains("interlagos"),
                "Portimao" => t.Contains("algarve") || t.Contains("portim"),
                "COTA" => t.Contains("americas") || t.Contains("cota"),
                "Imola" => t.Contains("imola") || t.Contains("enzo"),
                "PaulRicard" => t.Contains("ricard"),
                "Daytona" => t.Contains("daytona"),
                "Lusail" => t.Contains("lusail") || t.Contains("losail"),
                "Silverstone" => t.Contains("silverstone"),
                "LagunaSeca" => t.Contains("laguna"),
                "Sebring" => t.Contains("sebring"),
                _ => false
            };
        }

        private bool IsClassMatch(string carClass, string selectedFilter)
        {
            if (string.IsNullOrEmpty(carClass)) return false;
            var c = carClass.ToLower();

            return selectedFilter switch
            {
                "HYP" => c.Contains("hyp") || c.Contains("hyper"),
                "LMP2" => c.Contains("lmp2"),
                "LMP3" => c.Contains("lmp3"),
                "GTE" => c == "gte" || c.Contains("gte") || c.Contains("lm gte"),
                "GT3" => c == "gt3" || c.Contains("gt3") || c.Contains("lmgt3"),
                _ => true
            };
        }

        private string GetInitials(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName)) return "??";
            var parts = fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1) return parts[0].Substring(0, Math.Min(2, parts[0].Length)).ToUpper();
            return $"{parts[0][0]}{parts[parts.Length - 1][0]}".ToUpper();
        }

        // --- UI Event Handlers ---
        private void FilterClass_Checked(object? sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && rb.IsChecked == true)
            {
                if (LeaderboardView != null && LeaderboardView.IsVisible)
                {
                    string clickedClass = rb.Content?.ToString() ?? "GT3";
                    RefreshLeaderboardAsync(forcedClass: clickedClass, forceDownload: false);
                }
            }
        }

        private void TrackCard_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string trackName)
            {
                LogMessage($"[UI] Opening detailed leaderboard for: {trackName}");

                // Passing cached list to the detailed view
                TrackDetailsControl.LoadTrack(trackName, _cachedLeaderboard);

                NavigateTo(TrackDetailsControl, MainNavList, 2);
            }
        }

        private void SaveSettingsBtn_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings();
        }

        private void Settings_Changed(object sender, RoutedEventArgs e)
        {
            if (_isLoadingSettings) return;

            bool urlChanged = (SheetsLinkInput.Text != _loadedWebhookUrl);
            bool devModeChanged = (DevModeToggle.IsChecked != _loadedDevMode);
            bool startupChanged = (StartupComboBox.SelectedIndex != _loadedStartupMode);
            bool exitChanged = (ExitActionComboBox.SelectedIndex != _loadedExitAction);

            _hasUnsavedChanges = urlChanged || devModeChanged || startupChanged || exitChanged;

            if (DevConsoleMenuItem != null)
            {
                DevConsoleMenuItem.IsVisible = DevModeToggle.IsChecked == true;
            }
        }

        private void HamburgerBtn_Click(object sender, RoutedEventArgs e)
        {
            if (MainSplitView != null)
            {
                MainSplitView.IsPaneOpen = !MainSplitView.IsPaneOpen;
            }
        }

        private void MainNavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isChangingSelectionProgrammatically) return;
            if (MainNavList == null || MainNavList.SelectedItem == null) return;

            if (DashboardView == null || LeaderboardView == null || SettingsView == null || DevConsoleView == null) return;

            Control targetView = DashboardView;

            if (MainNavList.SelectedIndex == 0)
            {
                targetView = DashboardView;
            }
            else if (MainNavList.SelectedIndex == 2)
            {
                if (string.IsNullOrWhiteSpace(_loadedWebhookUrl))
                {
                    int revertIndex = DashboardView.IsVisible ? 0 : -1;

                    _isChangingSelectionProgrammatically = true;
                    MainNavList.SelectedIndex = revertIndex;
                    _isChangingSelectionProgrammatically = false;

                    if (MissingWebhookModal != null) MissingWebhookModal.IsVisible = true;
                    return;
                }

                targetView = LeaderboardView;
            }

            if (_hasUnsavedChanges)
            {
                _pendingView = targetView;
                _pendingNavList = MainNavList;
                _pendingNavIndex = MainNavList.SelectedIndex;
                if (UnsavedChangesModal != null) UnsavedChangesModal.IsVisible = true;
                return;
            }

            NavigateTo(targetView, MainNavList, MainNavList.SelectedIndex);

            if (targetView == LeaderboardView)
            {
                RefreshLeaderboardAsync(forceDownload: true);
            }
        }

        private void SettingsNavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isChangingSelectionProgrammatically) return;
            if (SettingsNavList == null || SettingsNavList.SelectedItem == null) return;

            if (DashboardView == null || LeaderboardView == null || SettingsView == null || DevConsoleView == null) return;

            Control targetView = SettingsView;
            if (SettingsNavList.SelectedItem == DevConsoleMenuItem) targetView = DevConsoleView;
            if (SettingsNavList.SelectedItem == SettingsMenuItem) targetView = SettingsView;

            if (_hasUnsavedChanges)
            {
                _pendingView = targetView;
                _pendingNavList = SettingsNavList;
                _pendingNavIndex = SettingsNavList.SelectedIndex;
                if (UnsavedChangesModal != null) UnsavedChangesModal.IsVisible = true;
                return;
            }

            NavigateTo(targetView, SettingsNavList, SettingsNavList.SelectedIndex);
        }

        private void NavigateTo(Control viewToShow, ListBox listToHighlight, int highlightIndex)
        {
            if (DashboardView != null) DashboardView.IsVisible = false;
            if (LeaderboardView != null) LeaderboardView.IsVisible = false;
            if (SettingsView != null) SettingsView.IsVisible = false;
            if (DevConsoleView != null) DevConsoleView.IsVisible = false;
            if (TrackDetailsControl != null) TrackDetailsControl.IsVisible = false;

            if (GlobalTopBar != null) GlobalTopBar.IsVisible = (viewToShow != TrackDetailsControl);

            if (viewToShow != null) viewToShow.IsVisible = true;

            _isChangingSelectionProgrammatically = true;
            if (listToHighlight == MainNavList)
            {
                if (SettingsNavList != null) SettingsNavList.SelectedItem = null;
                if (MainNavList != null) MainNavList.SelectedIndex = highlightIndex;
            }
            else if (listToHighlight == SettingsNavList)
            {
                if (MainNavList != null) MainNavList.SelectedItem = null;
                if (SettingsNavList != null) SettingsNavList.SelectedIndex = highlightIndex;
            }
            _isChangingSelectionProgrammatically = false;
        }

        private void ExitAppBtn_Click(object sender, RoutedEventArgs e)
        {
            if (ExitConfirmationModal != null) ExitConfirmationModal.IsVisible = true;
        }

        private void ExitModalCancel_Click(object sender, RoutedEventArgs e)
        {
            if (ExitConfirmationModal != null) ExitConfirmationModal.IsVisible = false;
        }

        private void ExitModalConfirm_Click(object sender, RoutedEventArgs e)
        {
            _forceExit = true;
            this.Close();
        }

        private void MissingWebhookModalCancel_Click(object sender, RoutedEventArgs e)
        {
            if (MissingWebhookModal != null) MissingWebhookModal.IsVisible = false;
            if (QuickWebhookInput != null) QuickWebhookInput.Text = "";
        }

        private void MissingWebhookModalSave_Click(object sender, RoutedEventArgs e)
        {
            if (QuickWebhookInput != null && !string.IsNullOrWhiteSpace(QuickWebhookInput.Text))
            {
                if (SheetsLinkInput != null) SheetsLinkInput.Text = QuickWebhookInput.Text;
                SaveSettings();

                if (MissingWebhookModal != null) MissingWebhookModal.IsVisible = false;
                QuickWebhookInput.Text = "";

                NavigateTo(LeaderboardView, MainNavList, 2);
                RefreshLeaderboardAsync(forceDownload: true);
            }
        }

        private void ModalCancel_Click(object sender, RoutedEventArgs e)
        {
            if (UnsavedChangesModal != null) UnsavedChangesModal.IsVisible = false;

            _isChangingSelectionProgrammatically = true;
            if (DashboardView != null && DashboardView.IsVisible)
            {
                if (SettingsNavList != null) SettingsNavList.SelectedItem = null;
                if (MainNavList != null) MainNavList.SelectedIndex = 0;
            }
            else if (LeaderboardView != null && LeaderboardView.IsVisible)
            {
                if (SettingsNavList != null) SettingsNavList.SelectedItem = null;
                if (MainNavList != null) MainNavList.SelectedIndex = 2;
            }
            else if (SettingsView != null && SettingsView.IsVisible)
            {
                if (MainNavList != null) MainNavList.SelectedItem = null;
                if (SettingsNavList != null) SettingsNavList.SelectedItem = SettingsMenuItem;
            }
            else if (DevConsoleView != null && DevConsoleView.IsVisible)
            {
                if (MainNavList != null) MainNavList.SelectedItem = null;
                if (SettingsNavList != null) SettingsNavList.SelectedItem = DevConsoleMenuItem;
            }
            _isChangingSelectionProgrammatically = false;
        }

        private void ModalDiscard_Click(object sender, RoutedEventArgs e)
        {
            _isLoadingSettings = true;
            if (SheetsLinkInput != null) SheetsLinkInput.Text = _loadedWebhookUrl;
            if (DevModeToggle != null) DevModeToggle.IsChecked = _loadedDevMode;
            if (StartupComboBox != null) StartupComboBox.SelectedIndex = _loadedStartupMode;
            if (ExitActionComboBox != null) ExitActionComboBox.SelectedIndex = _loadedExitAction;

            if (DevConsoleMenuItem != null)
            {
                DevConsoleMenuItem.IsVisible = _loadedDevMode;
            }

            _isLoadingSettings = false;
            _hasUnsavedChanges = false;
            if (UnsavedChangesModal != null) UnsavedChangesModal.IsVisible = false;

            NavigateTo(_pendingView, _pendingNavList, _pendingNavIndex);
        }

        private void ModalSave_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings();
            if (UnsavedChangesModal != null) UnsavedChangesModal.IsVisible = false;

            NavigateTo(_pendingView, _pendingNavList, _pendingNavIndex);
        }

        // --- Telemetry Methods ---
        private void UpdateConnectionUI(IBrush color, string status, string driverInfo)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (StatusIcon != null) StatusIcon.Fill = color;
                if (StatusText != null) StatusText.Text = status;
                if (DriverInfoText != null) DriverInfoText.Text = driverInfo;

                if (TrackDetailsControl != null)
                {
                    TrackDetailsControl.UpdateStatus(color, status, driverInfo);
                }
            });
        }

        private void TelemetryLoop()
        {
            LogMessage("========================================");
            LogMessage("               RaceLogger               ");
            LogMessage("========================================");
            LogMessage("[SYSTEM] Logger initialized. Auto-connecting...");

            int lastLapCount = -1;
            bool currentLapValid = true;

            while (_isLogging)
            {
                try
                {
                    using (var mappedFile = MemoryMappedFile.OpenExisting(rFactor2Constants.MM_SCORING_FILE_NAME))
                    using (var accessor = mappedFile.CreateViewAccessor())
                    {
                        rF2Scoring scoring = ReadSharedMemory<rF2Scoring>(accessor);

                        for (int i = 0; i < scoring.mScoringInfo.mNumVehicles; i++)
                        {
                            var vehicle = scoring.mVehicles[i];

                            if (vehicle.mIsPlayer == 1)
                            {
                                if (lastLapCount == -1)
                                {
                                    lastLapCount = vehicle.mTotalLaps;
                                    currentLapValid = (vehicle.mCountLapFlag != 0);
                                    string driverName = ParseByteArray(vehicle.mDriverName);

                                    UpdateConnectionUI(Brushes.LimeGreen, "Connected", $"LMU - {driverName}");
                                    LogMessage($"[SYSTEM] Connected! Driver: {driverName}.");
                                }
                                else if (vehicle.mTotalLaps < lastLapCount)
                                {
                                    LogMessage("[SYSTEM] Session reset or track change detected. Resetting telemetry...");
                                    lastLapCount = vehicle.mTotalLaps;
                                    currentLapValid = (vehicle.mCountLapFlag != 0);
                                }
                                else if (vehicle.mTotalLaps > lastLapCount)
                                {
                                    bool wasLapValid = currentLapValid && vehicle.mLastLapTime > 0;
                                    lastLapCount = vehicle.mTotalLaps;
                                    currentLapValid = (vehicle.mCountLapFlag != 0);

                                    string track = ParseByteArray(scoring.mScoringInfo.mTrackName);
                                    string lapTimeStr = FormatTime(vehicle.mLastLapTime);

                                    if (wasLapValid)
                                    {
                                        string driver = ParseByteArray(vehicle.mDriverName);
                                        string carClass = ParseByteArray(vehicle.mVehicleClass);
                                        string rawLiveryName = ParseByteArray(vehicle.mVehicleName);
                                        string carModel = GetBaseCarModel(rawLiveryName, carClass);
                                        string session = GetSessionType(scoring.mScoringInfo.mSession);

                                        double rawS1 = vehicle.mLastSector1;
                                        double rawS2 = vehicle.mLastSector2;
                                        double rawLap = vehicle.mLastLapTime;

                                        double actualS1 = rawS1 > 0 ? rawS1 : 0;
                                        double actualS2 = (rawS2 > 0 && rawS1 > 0) ? (rawS2 - rawS1) : 0;
                                        double actualS3 = (rawLap > 0 && rawS2 > 0) ? (rawLap - rawS2) : 0;

                                        string s1Str = FormatTime(actualS1);
                                        string s2Str = FormatTime(actualS2);
                                        string s3Str = FormatTime(actualS3);

                                        LogMessage($"[LAP RECORDED] {lapTimeStr} on {track} (Valid)");

                                        _ = SendLapDataAsync(track, driver, carClass, carModel, lapTimeStr, s1Str, s2Str, s3Str, session, "LMU");
                                    }
                                    else
                                    {
                                        LogMessage($"[LAP RECORDED] {lapTimeStr} on {track} -> Invalid lap or out-lap. Ignored.");
                                    }
                                }
                                else
                                {
                                    if (vehicle.mCountLapFlag == 0 && currentLapValid)
                                    {
                                        currentLapValid = false;
                                        LogMessage("[WARNING] Track limits exceeded! Current lap invalidated.");
                                    }
                                }

                                break;
                            }
                        }
                    }
                }
                catch (FileNotFoundException)
                {
                    if (lastLapCount != -1)
                    {
                        UpdateConnectionUI(Brushes.Red, "Disconnected", "Waiting for game...");
                        lastLapCount = -1;
                    }
                }
                catch (Exception)
                {
                }

                Thread.Sleep(200);
            }
        }

        static T ReadSharedMemory<T>(MemoryMappedViewAccessor accessor)
        {
            int size = Marshal.SizeOf(typeof(T));
            byte[] bytes = new byte[size];
            accessor.ReadArray(0, bytes, 0, size);

            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.Copy(bytes, 0, ptr, size);
                return (T)Marshal.PtrToStructure(ptr, typeof(T));
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        static string ParseByteArray(byte[] bytes)
        {
            return Encoding.Default.GetString(bytes).Split('\0')[0];
        }

        static string FormatTime(double timeSeconds)
        {
            if (timeSeconds <= 0) return "00:00.000";
            TimeSpan t = TimeSpan.FromSeconds(timeSeconds);
            return string.Format("{0:D2}:{1:D2}.{2:D3}", t.Minutes, t.Seconds, t.Milliseconds);
        }

        static string GetSessionType(int sessionIndex)
        {
            if (sessionIndex == 0 || (sessionIndex >= 1 && sessionIndex <= 4)) return "Practice";
            if (sessionIndex >= 5 && sessionIndex <= 8) return "Qualifying";
            if (sessionIndex == 9) return "Warmup";
            if (sessionIndex >= 10 && sessionIndex <= 13) return "Race";
            return "Unknown";
        }

        private string GetBaseCarModel(string liveryName, string carClass)
        {
            string upperName = liveryName.ToUpper().Trim();
            string upperClass = carClass.ToUpper().Trim();

            if (_carDatabase.TryGetValue(upperName, out string exactCarModel))
            {
                return exactCarModel;
            }


            // Fallback keyword matching for unknown liveries
            LogMessage($"[CAR UNKNOWN] Exact match not found for: \"{liveryName}\". Using fallback keywords.");

            if (upperClass.Contains("HYPERCAR") || upperClass.Contains("LMH") || upperClass.Contains("LMDH"))
            {
                if (upperName.Contains("TOYOTA") || upperName.Contains("TR010")) return "Toyota TR010";
                if (upperName.Contains("FERRARI") || upperName.Contains("499P") || upperName.Contains("AF CORSE")) return "Ferrari 499P";
                if (upperName.Contains("PORSCHE") || upperName.Contains("963") || upperName.Contains("JOTA") || upperName.Contains("PROTON")) return "Porsche 963";
                if (upperName.Contains("CADILLAC") || upperName.Contains("V-SERIES.R") || upperName.Contains("HERTZ TEAM")) return "Cadillac V-Series.R";
                if (upperName.Contains("PEUGEOT") || upperName.Contains("9X8")) return "Peugeot 9X8";
                if (upperName.Contains("ALPINE") || upperName.Contains("A424")) return "Alpine A424";
                if (upperName.Contains("BMW") || upperName.Contains("HYBRID V8") || upperName.Contains("WRT")) return "BMW M Hybrid V8";
                if (upperName.Contains("ISOTTA")) return "Isotta Fraschini Tipo 6";
                if (upperName.Contains("LAMBORGHINI") || upperName.Contains("SC63") || upperName.Contains("IRON LYNX")) return "Lamborghini SC63";
                if (upperName.Contains("ASTON MARTIN") || upperName.Contains("THOR")) return "Aston Martin Valkyrie LMH";
                if (upperName.Contains("GENESIS") || upperName.Contains("MAGMA")) return "Genesis GMR001";
            }

            if (upperClass.Contains("GTE"))
            {
                if (upperName.Contains("PORSCHE") || upperName.Contains("911") || upperName.Contains("RSR")) return "Porsche 911 RSR GTE";
                if (upperName.Contains("FERRARI") || upperName.Contains("488")) return "Ferrari 488 GTE Evo";
                if (upperName.Contains("ASTON MARTIN") || upperName.Contains("VANTAGE")) return "Aston Martin Vantage AMR GTE";
                if (upperName.Contains("CORVETTE") || upperName.Contains("C8.R")) return "Corvette C8.R GTE";
                return "GTE Class Car";
            }

            if (upperClass.Contains("GT3") || upperClass.Contains("LMGT3"))
            {
                if (upperName.Contains("WRT") || upperName.Contains("ROWE") || upperName.Contains("BMW M4")) return "BMW M4 GT3";
                if (upperName.Contains("AF CORSE") || upperName.Contains("FERRARI") || upperName.Contains("296") || upperName.Contains("VISTA") || upperName.Contains("AF CORSA") || upperName.Contains("SPIRIT OF RACE") || upperName.Contains("KESSEL") || upperName.Contains("JMW") || upperName.Contains("GR RACING")) return "Ferrari 296 GT3";
                if (upperName.Contains("MANTHEY") || upperName.Contains("PORSCHE") || upperName.Contains("911") || upperName.Contains("PROTON COMPETITION 2025") || upperName.Contains("IRON DAMES 2025")) return "Porsche 911 GT3 R";
                if (upperName.Contains("IRON LYNX 2024") || upperName.Contains("IRON DAMES 2024") || upperName.Contains("LAMBORGHINI") || upperName.Contains("HURACAN")) return "Lamborghini Huracan GT3 EVO2";
                if (upperName.Contains("D'STATION") || upperName.Contains("HEART OF RACING") || upperName.Contains("RACING SPIRIT") || upperName.Contains("ASTON MARTIN")) return "Aston Martin Vantage GT3";
                if (upperName.Contains("TF SPORT") || upperName.Contains("CORVETTE") || upperName.Contains("RACING TEAM TURKEY")) return "Corvette Z06 GT3.R";
                if (upperName.Contains("UNITED AUTOSPORTS") || upperName.Contains("MCLAREN") || upperName.Contains("GARAGE 59")) return "McLaren 720S GT3 Evo";
                if (upperName.Contains("PROTON COMPETITION 2026") || upperName.Contains("MUSTANG")) return "Ford Mustang GT3";
                if (upperName.Contains("AKKODIS") || upperName.Contains("LEXUS")) return "Lexus RC F GT3";
                if (upperName.Contains("IRON LYNX 2026") || upperName.Contains("MERCEDES") || upperName.Contains("AMG")) return "Mercedes-AMG GT3";
                if (upperName.Contains("Logitech G Challenge")) return "McLaren 720S GT3 Evo (Logitech)";
            }

            if (upperClass.Contains("LMP2")) return "Oreca 07 Gibson";

            if (upperClass.Contains("LMP3"))
            {
                if (upperName.Contains("LIGIER") || upperName.Contains("VIRAGE") || upperName.Contains("EUROINTERNATIONAL") || upperName.Contains("RLR") || upperName.Contains("CLX") || upperName.Contains("SPIRIT") || upperName.Contains("ULTIMATE") || upperName.Contains("M RACING") || upperName.Contains("INTER EUROPOL")) return "Ligier JS P320";
                if (upperName.Contains("DUQUEINE") || upperName.Contains("WTM")) return "Duqueine D08";
                if (upperName.Contains("ADESS")) return "Adess AD25";
                if (upperName.Contains("GINETTA") || upperName.Contains("DKR")) return "Ginetta G61-LT-P325 Evo";
            }

            return liveryName;
        }

        private async Task SendLapDataAsync(string track, string driver, string carClass, string carModel, string lapTime, string s1, string s2, string s3, string session, string game)
        {
            if (string.IsNullOrWhiteSpace(_loadedWebhookUrl))
            {
                LogMessage("[API ERROR] Webhook URL is empty. Please set it in Settings.");
                return;
            }

            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            var lapData = new
            {
                track = track,
                driver = driver,
                carClass = carClass,
                carModel = carModel,
                lapTime = lapTime,
                sector1 = s1,
                sector2 = s2,
                sector3 = s3,
                session = session,
                game = game,
                date = timestamp
            };

            string jsonString = JsonSerializer.Serialize(lapData);
            var content = new StringContent(jsonString, Encoding.UTF8, "application/json");

            try
            {
                HttpResponseMessage response = await client.PostAsync(_loadedWebhookUrl, content);
                if (!response.IsSuccessStatusCode)
                {
                    LogMessage($"[API ERROR] Failed to send to Sheets. Code: {response.StatusCode}");
                }
                else
                {
                    LogMessage($"[SUCCESS] Lap data uploaded to Google Sheets.");
                }
            }
            catch (Exception ex)
            {
                LogMessage($"[NETWORK ERROR] Could not connect to Google API: {ex.Message}");
            }
        }


        // OTA UPDATER UI LOGIC
        private async void CheckUpdatesBtn_Click(object sender, RoutedEventArgs e)
        {
            if (CheckUpdatesBtn != null) CheckUpdatesBtn.IsEnabled = false;

            UpdateModalTitle.Text = "Checking for Updates";
            UpdateModalMessage.Text = "Please wait while we contact the server...";
            UpdateModalStatus.Text = "";
            UpdateModalDownloadBtn.IsVisible = false;
            UpdateModalCancelBtn.IsVisible = false;
            UpdateModalOkBtn.IsVisible = false;
            UpdateModal.IsVisible = true;

            var (isUpdateAvailable, latestVersion, zipUrl, hashUrl) = await _updateService.CheckForUpdatesAsync();

            if (isUpdateAvailable)
            {
                _pendingZipUrl = zipUrl;
                _pendingHashUrl = hashUrl;

                UpdateModalTitle.Text = "Update Found!";
                UpdateModalMessage.Text = $"Version {_updateService.CurrentVersion} -> {latestVersion}";

                UpdateModalDownloadBtn.IsVisible = true;
                UpdateModalCancelBtn.IsVisible = true;
            }
            else
            {
                UpdateModalTitle.Text = "Up to Date";
                UpdateModalMessage.Text = "No updates found. You are on the latest version.";

                UpdateModalOkBtn.IsVisible = true;
            }
        }

        private async void UpdateModalDownloadBtn_Click(object sender, RoutedEventArgs e)
        {
            UpdateModalDownloadBtn.IsVisible = false;
            UpdateModalCancelBtn.IsVisible = false;
            UpdateModalStatus.Text = "Downloading update... Please wait.";

            bool success = await _updateService.DownloadAndPrepareUpdateAsync(_pendingZipUrl, _pendingHashUrl);

            if (success)
            {
                UpdateModalStatus.Text = "Restarting to apply update...";
                await Task.Delay(1500);
                _updateService.LaunchUpdaterAndExit();
            }
            else
            {
                UpdateModalTitle.Text = "Error";
                UpdateModalMessage.Text = "Failed to download update. Try again later.";
                UpdateModalStatus.Text = "";
                UpdateModalOkBtn.IsVisible = true;
            }
        }

        private void UpdateModalClose_Click(object sender, RoutedEventArgs e)
        {
            UpdateModal.IsVisible = false;
            if (CheckUpdatesBtn != null) CheckUpdatesBtn.IsEnabled = true;
        }

    } // <-- MainWindow Class End

    public class AppSettings
    {
        public string WebhookUrl { get; set; } = "";
        public bool DeveloperMode { get; set; } = false;
        public int StartupMode { get; set; } = 0;
        public int ExitAction { get; set; } = 0;
    }
}
