using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using RaceLogger.App.Models;

namespace RaceLogger.App.Views
{
    public partial class TrackDetailsView : UserControl
    {
        public event EventHandler BackRequested;

        // Track the currently selected class for filtering
        private string _currentClass = "GT3";

        // Internal memory for loaded records and current track
        private string _currentTrackId = "";
        private List<LapRecord> _allRecords = new List<LapRecord>();

        public TrackDetailsView()
        {
            InitializeComponent();
        }

        public void UpdateStatus(IBrush color, string status, string driverInfo)
        {
            if (LocalStatusIcon != null) LocalStatusIcon.Fill = color;
            if (LocalStatusText != null) LocalStatusText.Text = status;
            if (LocalDriverInfoText != null) LocalDriverInfoText.Text = driverInfo;
        }

        public void LoadTrack(string trackId, List<LapRecord> records)
        {
            _currentTrackId = trackId;
            _allRecords = records ?? new List<LapRecord>();

            // Disable event temporarily to prevent firing during population
            LayoutSelector.SelectionChanged -= LayoutSelector_SelectionChanged;
            LayoutSelector.Items.Clear();

            // Fetch layouts for this specific track
            List<string> layouts = GetTrackLayouts(trackId);
            foreach (var layout in layouts)
            {
                LayoutSelector.Items.Add(layout);
            }

            // Select the first layout by default
            if (LayoutSelector.Items.Count > 0)
            {
                LayoutSelector.SelectedIndex = 0;
            }

            LayoutSelector.SelectionChanged += LayoutSelector_SelectionChanged;

            // Load data for the initially selected layout
            RefreshDataForLayout(LayoutSelector.SelectedItem as string);
        }

        // Allows MainWindow to push new data instantly if a background sync finishes while the user is actively viewing this track's details
        public void UpdateDataSilently(List<LapRecord> newRecords)
        {
            _allRecords = newRecords ?? new List<LapRecord>();

            if (LayoutSelector.SelectedItem is string selectedLayout)
            {
                RefreshDataForLayout(selectedLayout);
            }
        }

        private void LayoutSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LayoutSelector.SelectedItem is string selectedLayout)
            {
                Debug.WriteLine($"[UI] Track layout changed to: {selectedLayout}");
                RefreshDataForLayout(selectedLayout);
            }
        }

        private void FilterClass_Checked(object? sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && rb.IsChecked == true)
            {
                _currentClass = rb.Content?.ToString() ?? "GT3";
                Debug.WriteLine($"[UI] Detailed leaderboard class changed to: {_currentClass}");

                if (LayoutSelector.SelectedItem is string selectedLayout)
                {
                    RefreshDataForLayout(selectedLayout);
                }
            }
        }

        private void RefreshDataForLayout(string layoutName)
        {
            if (_allRecords == null || !_allRecords.Any())
            {
                LapTimesList.ItemsSource = new List<LapDetailItem>();
                return;
            }

            // Filter valid records using the dictionary
            var validRecords = _allRecords
                .Where(r => r.Track != null &&
                            IsLayoutMatch(layoutName, r.Track) &&
                            IsClassMatch(r.CarClass, _currentClass))
                .OrderBy(r => r.LapTime)
                .ToList();

            if (!validRecords.Any())
            {
                LapTimesList.ItemsSource = new List<LapDetailItem>();
                return;
            }

            // Find the fastest sectors across the currently filtered list
            string bestS1 = validRecords
                .Where(r => !string.IsNullOrWhiteSpace(r.Sector1) && r.Sector1 != "00:00.000")
                .Select(r => r.Sector1)
                .OrderBy(s => s)
                .FirstOrDefault();

            string bestS2 = validRecords
                .Where(r => !string.IsNullOrWhiteSpace(r.Sector2) && r.Sector2 != "00:00.000")
                .Select(r => r.Sector2)
                .OrderBy(s => s)
                .FirstOrDefault();

            string bestS3 = validRecords
                .Where(r => !string.IsNullOrWhiteSpace(r.Sector3) && r.Sector3 != "00:00.000")
                .Select(r => r.Sector3)
                .OrderBy(s => s)
                .FirstOrDefault();

            string purpleHex = "#c77dff";

            // Map to UI items and assign dynamic colors
            var filteredTimes = validRecords.Select((r, index) =>
            {
                bool isBestLap = index == 0;
                bool isBestS1 = bestS1 != null && r.Sector1 == bestS1;
                bool isBestS2 = bestS2 != null && r.Sector2 == bestS2;
                bool isBestS3 = bestS3 != null && r.Sector3 == bestS3;

                string s1Display = string.IsNullOrWhiteSpace(r.Sector1) || r.Sector1 == "00:00.000" ? "[NO DATA]" : r.Sector1;
                string s2Display = string.IsNullOrWhiteSpace(r.Sector2) || r.Sector2 == "00:00.000" ? "[NO DATA]" : r.Sector2;
                string s3Display = string.IsNullOrWhiteSpace(r.Sector3) || r.Sector3 == "00:00.000" ? "[NO DATA]" : r.Sector3;

                return new LapDetailItem
                {
                    CarClass = _currentClass,
                    Initials = GetInitials(r.Driver),

                    LapTime = string.IsNullOrWhiteSpace(r.RawLapTimeStr) ? "[NO DATA]" : r.RawLapTimeStr,
                    S1 = s1Display,
                    S2 = s2Display,
                    S3 = s3Display,

                    Car = string.IsNullOrWhiteSpace(r.CarModel) ? "[NO DATA]" : r.CarModel,
                    Tires = string.IsNullOrWhiteSpace(r.Tires) ? "[NO DATA]" : r.Tires,
                    Conditions = string.IsNullOrWhiteSpace(r.Conditions) ? "[NO DATA]" : r.Conditions,
                    Temps = string.IsNullOrWhiteSpace(r.Temps) ? "[NO DATA]" : r.Temps,
                    Date = string.IsNullOrWhiteSpace(r.Date) ? "[NO DATA]" : r.Date,

                    LapTimeColor = isBestLap ? purpleHex : "#e4e4e4",
                    S1Color = isBestS1 ? purpleHex : "#ccc",
                    S2Color = isBestS2 ? purpleHex : "#ccc",
                    S3Color = isBestS3 ? purpleHex : "#ccc"
                };
            }).ToList();

            LapTimesList.ItemsSource = filteredTimes;
        }

        // ==========================================
        // TRACK DICTIONARY
        // ==========================================
        private bool IsLayoutMatch(string layoutName, string rawTrackName)
        {
            if (string.IsNullOrEmpty(rawTrackName)) return false;

            // Left = UI layout name, Right = expected raw track name from the data source
            string expectedRawName = layoutName switch
            {
                // SPA
                "Spa-Francorchamps (Endurance)" => "Circuit de Spa-Francorchamps Endurance",
                "Spa-Francorchamps" => "Circuit de Spa-Francorchamps", 

                // BAHRAIN
                "Bahrain Endurance Circuit" => "Bahrain Endurance Circuit", 
                "Bahrain Outer Circuit" => "Bahrain Outer Circuit", 
                "Bahrain Paddock Circuit" => "Bahrain Paddock Circuit", 
                "Bahrain Sakhir Circuit" => "Bahrain Sakhir Circuit", 

                // SARTHE
                "Circuit De La Sarthe Mulsanne" => "Circuit de la Sarthe Mulsanne", 
                "Circuit De La Sarthe" => "Circuit de la Sarthe", 

                // MONZA
                "Autodromo Nazionale Monza Curva Grande" => "Monza Curva Grande Circuit", 
                "Autodromo Nazionale Monza" => "Autodromo Nazionale Monza", 

                // COTA
                "COTA National Circuit" => "Circuit of the Americas National Circuit", 
                "Circuit Of The Americas" => "Circuit of the Americas", 

                // FUJI
                "Fuji Speedway Classic" => "Fuji Speedway Classic", 
                "Fuji Speedway" => "Fuji Speedway", 

                // LUSAIL
                "Lusail Short Circuit" => "Lusail Short Circuit", 
                "Lusail International Circuit" => "Lusail International Circuit", 

                // PAUL RICARD
                "Circuit Paul Ricard - 1A-V2 Short" => "Paul Ricard 1A-V2 Short", 
                "Circuit Paul Ricard - 1A-V2" => "Paul Ricard 1A-V2", 
                "Circuit Paul Ricard - 1A" => "Paul Ricard 1A", 
                "Circuit Paul Ricard - 3A" => "Paul Ricard 3A", 
                "Circuit Paul Ricard" => "Paul Ricard", 

                // SEBRING
                "Sebring School Circuit" => "Sebring School Circuit", 
                "Sebring" => "Sebring", 

                // SILVERSTONE
                "Silverstone International Circuit" => "Silverstone International Circuit", 
                "Silverstone National Circuit" => "Silverstone National Circuit", 
                "Silverstone" => "Silverstone", 

                // Default fallback
                _ => layoutName
            };

            // Return true if the raw track name exactly matches the expected name
            return rawTrackName.Equals(expectedRawName, StringComparison.OrdinalIgnoreCase);
        }

        private bool IsClassMatch(string carClass, string selectedFilter)
        {
            if (string.IsNullOrEmpty(carClass)) return false;
            return carClass.Contains(selectedFilter, StringComparison.OrdinalIgnoreCase);
        }

        // Track dictionary for connecting default tracks with all their available layouts
        private List<string> GetTrackLayouts(string trackId)
        {
            string baseName = GetFullTrackName(trackId);

            return trackId switch
            {
                "Spa" => new List<string> { baseName, "Spa-Francorchamps (Endurance)" },
                "Bahrain" => new List<string>
                {
                    baseName,
                    "Bahrain Endurance Circuit",
                    "Bahrain Outer Circuit",
                    "Bahrain Paddock Circuit"
                },
                "DeLaSarthe" => new List<string> { baseName, "Circuit De La Sarthe Mulsanne" },
                "Monza" => new List<string> { baseName, "Autodromo Nazionale Monza Curva Grande" },
                "COTA" => new List<string> { baseName, "COTA National Circuit" },
                "Fuji" => new List<string> { baseName, "Fuji Speedway Classic" },
                "Lusail" => new List<string> { baseName, "Lusail Short Circuit" },
                "PaulRicard" => new List<string>
                {
                    baseName,
                    "Circuit Paul Ricard - 1A",
                    "Circuit Paul Ricard - 1A-V2",
                    "Circuit Paul Ricard - 1A-V2 Short",
                    "Circuit Paul Ricard - 3A"
                },
                "Sebring" => new List<string> { baseName, "Sebring School Circuit" },
                "Silverstone" => new List<string>
                {
                    baseName,
                    "Silverstone International Circuit",
                    "Silverstone National Circuit"
                },
                _ => new List<string> { baseName }
            };
        }

        // Converts a track ID to its full name for display purposes
        private string GetFullTrackName(string trackId)
        {
            return trackId switch
            {
                "Spa" => "Spa-Francorchamps",
                "Monza" => "Autodromo Nazionale Monza",
                "DeLaSarthe" => "Circuit De La Sarthe",
                "Fuji" => "Fuji Speedway",
                "Bahrain" => "Bahrain Sakhir Circuit",
                "Interlagos" => "Autódromo José Carlos Pace",
                "Portimao" => "Algarve International Circuit",
                "COTA" => "Circuit of the Americas",
                "Imola" => "Autodromo Enzo e Dino Ferrari",
                "PaulRicard" => "Circuit Paul Ricard",
                "Daytona" => "Daytona International Speedway Road Course",
                "Lusail" => "Lusail International Circuit",
                "Silverstone" => "Silverstone",
                "LagunaSeca" => "WeatherTech Raceway Laguna Seca",
                "Sebring" => "Sebring International Raceway",
                _ => trackId
            };
        }

        private string GetInitials(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName)) return "??";
            var parts = fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1) return parts[0].Substring(0, Math.Min(2, parts[0].Length)).ToUpper();
            return $"{parts[0][0]}{parts[parts.Length - 1][0]}".ToUpper();
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            BackRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    public class LapDetailItem
    {
        public string CarClass { get; set; }
        public string Initials { get; set; }
        public string LapTime { get; set; }
        public string S1 { get; set; }
        public string S2 { get; set; }
        public string S3 { get; set; }
        public string Car { get; set; }
        public string Tires { get; set; }
        public string Conditions { get; set; }
        public string Temps { get; set; }
        public string Date { get; set; }

        public string LapTimeColor { get; set; }
        public string S1Color { get; set; }
        public string S2Color { get; set; }
        public string S3Color { get; set; }
    }
}