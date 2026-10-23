using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using RaceLogger.App.Models;

namespace RaceLogger.App.Services
{
    public class LeaderboardService
    {
        private static readonly HttpClient _httpClient = new HttpClient();

        public async Task<List<LapRecord>> FetchRawDataAsync(string webhookUrl, Action<string> logMessage)
        {
            var records = new List<LapRecord>();
            if (string.IsNullOrWhiteSpace(webhookUrl)) return records;

            try
            {
                logMessage("[API] Waiting for response from Google Sheets...");
                string jsonData = await _httpClient.GetStringAsync(webhookUrl);

                // If Google returns an HTML page instead of JSON
                if (jsonData.TrimStart().StartsWith("<"))
                {
                    logMessage("[API ERROR] Received HTML page instead of data! Make sure you deployed the script as 'New version' and access is set to 'Anyone'.");
                    return records;
                }

                // If Google returns custom JSON error
                if (jsonData.TrimStart().StartsWith("{") && jsonData.Contains("error"))
                {
                    logMessage($"[API ERROR] Script returned an error: {jsonData}");
                    return records;
                }

                var rows = JsonSerializer.Deserialize<JsonElement[][]>(jsonData);

                if (rows == null || rows.Length == 0)
                {
                    logMessage("[API] The table in Google Sheets is empty.");
                    return records;
                }

                // Skip the first row (i = 1) because it contains headers (Track, Driver)
                for (int i = 1; i < rows.Length; i++)
                {
                    var cols = rows[i];

                    // Ensure the row has enough columns to prevent index out of bounds
                    if (cols.Length < 10) continue;

                    var record = new LapRecord
                    {
                        Track = cols[0].ToString().Trim(),
                        Driver = cols[1].ToString().Trim(),
                        CarClass = cols[2].ToString().Trim(),
                        CarModel = cols[3].ToString().Trim(),
                        RawLapTimeStr = cols[4].ToString().Trim(),
                        Sector1 = cols[5].ToString().Trim(),
                        Sector2 = cols[6].ToString().Trim(),
                        Sector3 = cols[7].ToString().Trim(),
                        Session = cols[8].ToString().Trim(),
                        Game = cols[9].ToString().Trim()
                    };

                    if (string.IsNullOrWhiteSpace(record.RawLapTimeStr)) continue;

                    record.LapTime = LapRecord.ParseLapTime(record.RawLapTimeStr);
                    records.Add(record);
                }
            }
            catch (JsonException jEx)
            {
                logMessage($"[API ERROR] Data conversion error: {jEx.Message}");
            }
            catch (Exception ex)
            {
                logMessage($"[API ERROR] Connection failed: {ex.Message}");
            }

            return records;
        }

        public List<TrackLeaderboardEntry> GetTopClassesForTrack(List<LapRecord> allRecords, string fullTrackName)
        {
            return allRecords
                .Where(r => r.Track == fullTrackName && r.LapTime != TimeSpan.MaxValue)
                .GroupBy(r => r.CarClass)
                .Select(group =>
                {
                    var bestLap = group.OrderBy(r => r.LapTime).First();
                    return new TrackLeaderboardEntry
                    {
                        CarClass = bestLap.CarClass,
                        DriverInitials = GetInitials(bestLap.Driver),
                        LapTimeStr = bestLap.RawLapTimeStr
                    };
                })
                .OrderBy(entry => entry.LapTimeStr)
                .Take(3)
                .ToList();
        }

        private string GetInitials(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName)) return "??";
            var parts = fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1) return parts[0].Substring(0, Math.Min(2, parts[0].Length)).ToUpper();
            return $"{parts[0][0]}{parts[parts.Length - 1][0]}".ToUpper();
        }
    }
}