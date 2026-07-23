using rF2SMMonitor;
using rF2SMMonitor.rFactor2Data;
using System;
using System.Drawing;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RaceLogger
{
    class Program
    {

        private static readonly HttpClient client = new HttpClient();

        [DllImport("kernel32.dll")]
        static extern bool AllocConsole();

        [DllImport("kernel32.dll")]
        static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        static extern IntPtr GetSystemMenu(IntPtr hWnd, bool bRevert);

        [DllImport("user32.dll")]
        static extern bool RemoveMenu(IntPtr hMenu, uint uPosition, uint uFlags);

        [DllImport("user32.dll")]
        static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        const int SW_HIDE = 0;
        const int SW_RESTORE = 9;
        const uint SC_CLOSE = 0xF060;
        const uint MF_BYCOMMAND = 0x00000000;

        static bool isConsoleVisible = true;
        static IntPtr consoleWindow;

        [STAThread]
        static void Main(string[] args)
        {
            AllocConsole();

            Console.Title = "LMU Excel Sync";

            StreamWriter standardOutput = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(standardOutput);

            consoleWindow = GetConsoleWindow();

            if (consoleWindow != IntPtr.Zero)
            {
                IntPtr sysMenu = GetSystemMenu(consoleWindow, false);
                if (sysMenu != IntPtr.Zero)
                {
                    RemoveMenu(sysMenu, SC_CLOSE, MF_BYCOMMAND);
                }
            }

            // --- System Tray Icon Setup ---
            NotifyIcon trayIcon = new NotifyIcon();
            trayIcon.Text = "LMU Excel Sync";
            trayIcon.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            trayIcon.Visible = true;

            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem toggleMenuItem = new ToolStripMenuItem("Hide Console");

            Action toggleConsole = () =>
            {
                if (isConsoleVisible)
                {
                    ShowWindow(consoleWindow, SW_HIDE);
                    toggleMenuItem.Text = "Show Console";
                    isConsoleVisible = false;
                }
                else
                {
                    ShowWindow(consoleWindow, SW_RESTORE);
                    SetForegroundWindow(consoleWindow);

                    toggleMenuItem.Text = "Hide Console";
                    isConsoleVisible = true;
                }
            };

            // Setup menu actions
            toggleMenuItem.Click += (s, e) => toggleConsole();
            trayIcon.DoubleClick += (s, e) => toggleConsole();

            menu.Items.Add(toggleMenuItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit Logger", null, (s, e) =>
            {
                trayIcon.Visible = false;
                Application.Exit();
                Environment.Exit(0);
            });

            trayIcon.ContextMenuStrip = menu;

            // For when launching the app using the shortcut with the "--hidden" property
            if (args.Length > 0 && args[0] == "--hidden")
            {
                ShowWindow(consoleWindow, SW_HIDE);
                toggleMenuItem.Text = "Show Console";
                isConsoleVisible = false;
            }

            // --- Startup Messages ---
            Console.ForegroundColor = ConsoleColor.Cyan;

            System.Windows.Forms.Timer minimizeTimer = new System.Windows.Forms.Timer();
            minimizeTimer.Interval = 150; // Checks each 150ms
            minimizeTimer.Tick += (s, e) =>
            {
                if (isConsoleVisible && IsIconic(consoleWindow))
                {
                    toggleConsole(); // Hide the console if it's minimized
                }
            };
            minimizeTimer.Start();

            // --- Startup Messages ---

            // Start the telemetry loop in the background
            Task.Run(() => TelemetryLoop());

            // Start the tray icon message loop
            Application.Run();
        } // <-- THIS CLOSES THE MAIN METHOD

        static void TelemetryLoop()
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("========================================");
            Console.WriteLine("    LMU Telemetry to Sheets Logger      ");
            Console.WriteLine("========================================");
            Console.WriteLine("[SYSTEM] Logger initialized.");
            Console.WriteLine("[SYSTEM] Double-click the tray icon to Hide/Show this window.");
            Console.WriteLine("[SYSTEM] Waiting for Le Mans Ultimate to start...");
            Console.ResetColor();

            int lastLapCount = -1;
            bool currentLapValid = true;

                // Main telemetry polling loop (5Hz)
                while (true)
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
                                    // Initial connection
                                    if (lastLapCount == -1)
                                    {
                                        lastLapCount = vehicle.mTotalLaps;
                                        currentLapValid = (vehicle.mCountLapFlag != 0);
                                        string driverName = ParseByteArray(vehicle.mDriverName);
                                        Console.ForegroundColor = ConsoleColor.Green;
                                        Console.WriteLine($"[SYSTEM] Connected! Driver's name detected: {driverName}.");
                                        Console.ResetColor();
                                    }
                                    // Session reset detection
                                    else if (vehicle.mTotalLaps < lastLapCount)
                                    {
                                        Console.WriteLine("[SYSTEM] Session reset or track change detected. Resetting telemetry...");
                                        lastLapCount = vehicle.mTotalLaps;
                                        currentLapValid = (vehicle.mCountLapFlag != 0);
                                    }
                                    // Lap completed
                                    else if (vehicle.mTotalLaps > lastLapCount)
                                    {
                                        // Save the validity of the lap JUST finished
                                        bool wasLapValid = currentLapValid && vehicle.mLastLapTime > 0;

                                        // Instantly update states for the newly started lap
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

                                        // mLastSector1 = Time from start to S1
                                        // mLastSector2 = Time from start to S2 (S1 + S2)
                                        // mLastLapTime = Time from start to finish (S1 + S2 + S3)
                                        double rawS1 = vehicle.mLastSector1;
                                        double rawS2 = vehicle.mLastSector2;
                                        double rawLap = vehicle.mLastLapTime;

                                        double actualS1 = rawS1 > 0 ? rawS1 : 0;
                                        double actualS2 = (rawS2 > 0 && rawS1 > 0) ? (rawS2 - rawS1) : 0;
                                        double actualS3 = (rawLap > 0 && rawS2 > 0) ? (rawLap - rawS2) : 0;

                                        string s1Str = FormatTime(actualS1);
                                        string s2Str = FormatTime(actualS2);
                                        string s3Str = FormatTime(actualS3);

                                        Console.WriteLine($"[LAP RECORDED] {lapTimeStr} on {track} (Valid)");
                                            _ = SendLapDataAsync(track, driver, carClass, carModel, lapTimeStr, s1Str, s2Str, s3Str, session, "LMU");
                                        }
                                        else
                                        {
                                            Console.WriteLine($"[LAP RECORDED] {lapTimeStr} on {track} -> Invalid lap or out-lap. Ignored.");
                                        }
                                    }
                                    // Constantly check for track limit violations during the lap
                                    else
                                    {
                                        // mCountLapFlag == 0 -> Track limits exceeded, lap invalidated
                                        if (vehicle.mCountLapFlag == 0 && currentLapValid)
                                        {
                                            currentLapValid = false;
                                            Console.ForegroundColor = ConsoleColor.Yellow;
                                            Console.WriteLine("[WARNING] Track limits exceeded! Current lap invalidated.");
                                            Console.ResetColor();
                                        }
                                    }

                                    break;
                                }
                            }
                        }
                    }
                    catch (FileNotFoundException)
                    {
                        // LMU is closed
                    }
                    catch (Exception)
                    {
                        // Suppress random memory parsing errors to prevent console spam
                    }

                    Thread.Sleep(200);
                }
            }

            // --- Helper Methods ---
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

            // Helper method to extract the base car model using both livery/team name and car class
            static string GetBaseCarModel(string liveryName, string carClass)
            {
                string upperName = liveryName.ToUpper();
                string upperClass = carClass.ToUpper();

                // Hypercar Class Check
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

                // LMGT3 / GTE Class Check
                if (upperClass.Contains("GT3") || upperClass.Contains("LMGT3") || upperClass.Contains("GTE"))
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

                // LMP2 Class Check
                if (upperClass.Contains("LMP2"))
                {
                    return "Oreca 07 Gibson";
                }

                // LMP3 Class Check
                if (upperClass.Contains("LMP3"))
                {
                    if (upperName.Contains("LIGIER") || upperName.Contains("VIRAGE") || upperName.Contains("EUROINTERNATIONAL") || upperName.Contains("RLR") || upperName.Contains("CLX") || upperName.Contains("SPIRIT") || upperName.Contains("ULTIMATE") || upperName.Contains("M RACING") || upperName.Contains("INTER EUROPOL")) return "Ligier JS P320";
                    if (upperName.Contains("DUQUEINE") || upperName.Contains("WTM")) return "Duqueine D08";
                    if (upperName.Contains("ADESS")) return "Adess AD25";
                    if (upperName.Contains("GINETTA") || upperName.Contains("DKR")) return "Ginetta G61-LT-P325 Evo";
                }

                // Fallback - return the raw livery name if no class/model matches are found
                return liveryName;
            }

        static async Task SendLapDataAsync(string track, string driver, string carClass, string carModel, string lapTime, string s1, string s2, string s3, string session, string game)
        {
            // Get the current system timestamp
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
                HttpResponseMessage response = await client.PostAsync(Secrets.WebhookUrl, content);
                if (!response.IsSuccessStatusCode)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"[API ERROR] Failed to send to Sheets. Code: {response.StatusCode}");
                        Console.ResetColor();
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"[SUCCESS] Lap data uploaded to Google Sheets.");
                        Console.ResetColor();
                    }
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[NETWORK ERROR] Could not connect to Google API: {ex.Message}");
                    Console.ResetColor();
                }
            }
        }
    }