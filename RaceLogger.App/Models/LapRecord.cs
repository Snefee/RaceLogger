using System;

namespace RaceLogger.App.Models
{
    public class LapRecord
    {
        public string Track { get; set; }
        public string Driver { get; set; }
        public string CarClass { get; set; }
        public string CarModel { get; set; }
        public string RawLapTimeStr { get; set; }
        public TimeSpan LapTime { get; set; }
        public string Sector1 { get; set; }
        public string Sector2 { get; set; }
        public string Sector3 { get; set; }
        public string Session { get; set; }
        public string Tires { get; set; }
        public string Conditions { get; set; }
        public string Temps { get; set; }
        public string Date { get; set; }
        public string Game { get; set; }

        // Format time from "02:22.802" to mathematical TimeSpan
        public static TimeSpan ParseLapTime(string timeStr)
        {
            if (string.IsNullOrWhiteSpace(timeStr)) return TimeSpan.MaxValue;

            if (TimeSpan.TryParseExact(timeStr, @"mm\:ss\.fff", null, out TimeSpan result))
            {
                return result;
            }
            return TimeSpan.MaxValue;
        }
    }

    public class TrackLeaderboardEntry
    {
        public string CarClass { get; set; }
        public string DriverInitials { get; set; }
        public string LapTimeStr { get; set; }
    }
}