using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace RaceLogger.App.Services
{
    public class UpdateService
    {
        // ===============
        // GITHUB CONFIG
        // ===============
        private const string GithubOwner = "Snefee";
        private const string GithubRepo = "RaceLogger";

        public string CurrentVersion { get; private set; }

        private static readonly HttpClient _client = new HttpClient();

        private readonly string _updateCacheFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UpdateCache");
        private readonly string _updaterExeName = "RaceLogger.Updater.exe";

        public UpdateService()
        {
            // User-Agent for Github API
            if (!_client.DefaultRequestHeaders.Contains("User-Agent"))
            {
                _client.DefaultRequestHeaders.Add("User-Agent", "RaceLogger-OTA-Updater");
            }

            var version = Assembly.GetExecutingAssembly().GetName().Version;

            CurrentVersion = $"v{version?.Major ?? 1}.{version?.Minor ?? 0}.{version?.Build ?? 0}";
        }

        // Check if there is new version available on GitHub
        public async Task<(bool isUpdateAvailable, string latestVersion, string zipUrl, string hashUrl)> CheckForUpdatesAsync()
        {
            try
            {
                string apiUrl = $"https://api.github.com/repos/{GithubOwner}/{GithubRepo}/releases/latest";
                var response = await _client.GetStringAsync(apiUrl);

                using (JsonDocument doc = JsonDocument.Parse(response))
                {
                    var root = doc.RootElement;
                    string latestVersion = root.GetProperty("tag_name").GetString();

                    if (latestVersion != CurrentVersion)
                    {
                        var assets = root.GetProperty("assets").EnumerateArray().ToList();

                        // Find the .zip and .sha256 assets
                        string zipUrl = assets.FirstOrDefault(a => a.GetProperty("name").GetString().EndsWith(".zip")).GetProperty("browser_download_url").GetString();
                        string hashUrl = assets.FirstOrDefault(a => a.GetProperty("name").GetString().EndsWith(".sha256")).GetProperty("browser_download_url").GetString();

                        return (true, latestVersion, zipUrl, hashUrl);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OTA ERROR] Failed to check for updates: {ex.Message}");
            }

            return (false, CurrentVersion, null, null);
        }

        // Downloading, SHA256 verification, and extraction
        public async Task<bool> DownloadAndPrepareUpdateAsync(string zipUrl, string hashUrl)
        {
            try
            {
                if (Directory.Exists(_updateCacheFolder))
                {
                    Directory.Delete(_updateCacheFolder, true);
                }
                Directory.CreateDirectory(_updateCacheFolder);

                string zipPath = Path.Combine(_updateCacheFolder, "update.zip");
                string hashPath = Path.Combine(_updateCacheFolder, "checksum.sha256");

                // Downloading files
                Debug.WriteLine("[OTA] Downloading files...");
                var zipBytes = await _client.GetByteArrayAsync(zipUrl);
                await File.WriteAllBytesAsync(zipPath, zipBytes);

                var expectedHash = (await _client.GetStringAsync(hashUrl)).Trim().Split(' ')[0];

                // Verification of SHA256
                Debug.WriteLine("[OTA] Verifying SHA256 Hash...");
                using (var sha256 = SHA256.Create())
                using (var stream = File.OpenRead(zipPath))
                {
                    byte[] hash = sha256.ComputeHash(stream);
                    string actualHash = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();

                    if (actualHash != expectedHash.ToLowerInvariant())
                    {
                        Debug.WriteLine("[OTA FATAL] Hash mismatch! Update file is corrupted. Aborting.");
                        return false;
                    }
                }

                // Extraction to UpdateCache folder
                Debug.WriteLine("[OTA] Hash OK. Extracting update...");
                ZipFile.ExtractToDirectory(zipPath, _updateCacheFolder, true);

                // Delete the .zip file to avoid copying it in a loop
                File.Delete(zipPath);

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OTA ERROR] Download/Extraction failed: {ex.Message}");
                return false;
            }
        }

        // Exit the app and launch the separate updater
        public void LaunchUpdaterAndExit()
        {
            string updaterPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _updaterExeName);

            if (!File.Exists(updaterPath))
            {
                Debug.WriteLine($"[OTA FATAL] Updater executable not found at: {updaterPath}");
                return;
            }

            int currentPid = Environment.ProcessId;
            string mainAppFolder = AppDomain.CurrentDomain.BaseDirectory;
            string mainExeName = AppDomain.CurrentDomain.FriendlyName + ".exe"; // ex. RaceLogger.App.exe

            // Constructing arguments for the updater
            // args[0] = PID
            // args[1] = UpdateCache folder
            // args[2] = Main app folder
            // args[3] = Main exe name
            string arguments = $"\"{currentPid}\" \"{_updateCacheFolder}\" \"{mainAppFolder}\" \"{mainExeName}\"";

            Debug.WriteLine($"[OTA] Launching Updater: {updaterPath} {arguments}");

            Process.Start(new ProcessStartInfo
            {
                FileName = updaterPath,
                Arguments = arguments,
                UseShellExecute = true
            });

            // Exit main application to allow the updater to replace files
            Environment.Exit(0);
        }
    }
}