using rF2SMMonitor;
using rF2SMMonitor.rFactor2Data;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RaceLogger.App.Services
{
    public class LapCompletedArgs : EventArgs
    {
        public string Track { get; set; } = "";
        public string Driver { get; set; } = "";
        public string CarClass { get; set; } = "";
        public string RawLiveryName { get; set; } = "";
        public int SessionIndex { get; set; }
        public double LapTime { get; set; }
        public double Sector1 { get; set; }
        public double Sector2 { get; set; }
        public bool IsValid { get; set; }
    }

    public class LiveTelemetryArgs : EventArgs
    {
        public int Gear { get; set; }
        public double RPM { get; set; }
        public double SpeedKmh { get; set; }
        public double ThrottlePct { get; set; }
        public double BrakePct { get; set; }
        public double SteeringDegrees { get; set; }
        public double WaterTemp { get; set; }
        public double OilTemp { get; set; }
        public double FuelLiters { get; set; }
        public double FuelCapacity { get; set; }

        // Array [FL, FR, RL, RR]
        public double[] TireTemps { get; set; } = new double[4];
        public double[] TirePressures { get; set; } = new double[4];
        public double[] TireLife { get; set; } = new double[4];
        public double[] BrakeTemps { get; set; } = new double[4];

        public double PosX { get; set; }
        public double PosZ { get; set; }
    }

    public class TelemetryService
    {
        private bool _isLogging = false;
        private int _currentPlayerID = -1;
        private int _telemetryTickCounter = 0;

        // MainWindow connection events
        public event EventHandler<string>? OnLogMessage;
        public event EventHandler<(bool isConnected, string status, string driverInfo)>? OnConnectionStatusChanged;
        public event EventHandler<LapCompletedArgs>? OnLapCompleted;
        public event EventHandler<LiveTelemetryArgs>? OnLiveTelemetryUpdated;

        public void Start()
        {
            if (_isLogging) return;
            _isLogging = true;

            Task.Run(() => ScoringLoop());               // 5Hz - Laptime & Session Info
            Task.Run(() => HighFrequencyTelemetryLoop()); // 50Hz - Raw Telemetry Data
        }

        public void Stop()
        {
            _isLogging = false;
        }

        private void ScoringLoop()
        {
            OnLogMessage?.Invoke(this, "========================================");
            OnLogMessage?.Invoke(this, "               RaceLogger               ");
            OnLogMessage?.Invoke(this, "========================================");
            OnLogMessage?.Invoke(this, "[SYSTEM] Logger initialized. Auto-connecting...");

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
                                _currentPlayerID = vehicle.mID;

                                if (lastLapCount == -1)
                                {
                                    lastLapCount = vehicle.mTotalLaps;
                                    currentLapValid = (vehicle.mCountLapFlag != 0);
                                    string driverName = ParseByteArray(vehicle.mDriverName);

                                    OnConnectionStatusChanged?.Invoke(this, (true, "Connected", $"LMU - {driverName}"));
                                    OnLogMessage?.Invoke(this, $"[SYSTEM] Connected! Driver: {driverName}.");
                                }
                                else if (vehicle.mTotalLaps < lastLapCount)
                                {
                                    OnLogMessage?.Invoke(this, "[SYSTEM] Session reset or track change detected. Resetting telemetry...");
                                    lastLapCount = vehicle.mTotalLaps;
                                    currentLapValid = (vehicle.mCountLapFlag != 0);
                                }
                                else if (vehicle.mTotalLaps > lastLapCount)
                                {
                                    bool wasLapValid = currentLapValid && vehicle.mLastLapTime > 0;
                                    lastLapCount = vehicle.mTotalLaps;
                                    currentLapValid = (vehicle.mCountLapFlag != 0);

                                    if (wasLapValid)
                                    {
                                        var args = new LapCompletedArgs
                                        {
                                            Track = ParseByteArray(scoring.mScoringInfo.mTrackName),
                                            Driver = ParseByteArray(vehicle.mDriverName),
                                            CarClass = ParseByteArray(vehicle.mVehicleClass),
                                            RawLiveryName = ParseByteArray(vehicle.mVehicleName),
                                            SessionIndex = scoring.mScoringInfo.mSession,
                                            LapTime = vehicle.mLastLapTime,
                                            Sector1 = vehicle.mLastSector1,
                                            Sector2 = vehicle.mLastSector2,
                                            IsValid = true
                                        };
                                        OnLapCompleted?.Invoke(this, args);
                                    }
                                    else
                                    {
                                        OnLogMessage?.Invoke(this, $"[LAP RECORDED] Invalid lap or out-lap. Ignored.");
                                    }
                                }
                                else
                                {
                                    if (vehicle.mCountLapFlag == 0 && currentLapValid)
                                    {
                                        currentLapValid = false;
                                        OnLogMessage?.Invoke(this, "[WARNING] Track limits exceeded! Current lap invalidated.");
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
                        OnConnectionStatusChanged?.Invoke(this, (false, "Disconnected", "Waiting for game..."));
                        lastLapCount = -1;
                        _currentPlayerID = -1;
                    }
                }
                catch (Exception) { }

                Thread.Sleep(200); // 5Hz
            }
        }

        private void HighFrequencyTelemetryLoop()
        {
            OnLogMessage?.Invoke(this, "[SYSTEM] High-Frequency Telemetry Loop (50Hz) started.");

            while (_isLogging)
            {
                if (_currentPlayerID != -1)
                {
                    try
                    {
                        using (var mappedFile = MemoryMappedFile.OpenExisting(rFactor2Constants.MM_TELEMETRY_FILE_NAME))
                        using (var accessor = mappedFile.CreateViewAccessor())
                        {
                            rF2Telemetry telemetry = ReadSharedMemory<rF2Telemetry>(accessor);

                            // Anti-Tearing Check
                            if (telemetry.mVersionUpdateBegin == telemetry.mVersionUpdateEnd)
                            {
                                for (int i = 0; i < telemetry.mNumVehicles; i++)
                                {
                                    var vehicle = telemetry.mVehicles[i];

                                    if (vehicle.mID == _currentPlayerID)
                                    {
                                        // Refresh interface every second frame [25hz]
                                        if (_telemetryTickCounter % 2 == 0)
                                        {
                                            double speedMs = Math.Sqrt(Math.Pow(vehicle.mLocalVel.x, 2) + Math.Pow(vehicle.mLocalVel.y, 2) + Math.Pow(vehicle.mLocalVel.z, 2));

                                            var liveData = new LiveTelemetryArgs
                                            {
                                                Gear = vehicle.mGear,
                                                RPM = vehicle.mEngineRPM,
                                                SpeedKmh = speedMs * 3.6,
                                                ThrottlePct = vehicle.mUnfilteredThrottle * 100,
                                                BrakePct = vehicle.mUnfilteredBrake * 100,
                                                SteeringDegrees = vehicle.mUnfilteredSteering * (vehicle.mPhysicalSteeringWheelRange / 2),
                                                WaterTemp = vehicle.mEngineWaterTemp,
                                                OilTemp = vehicle.mEngineOilTemp,
                                                FuelLiters = vehicle.mFuel,
                                                FuelCapacity = vehicle.mFuelCapacity,
                                                PosX = vehicle.mPos.x,
                                                PosZ = vehicle.mPos.z
                                            };

                                            for (int j = 0; j < 4; j++)
                                            {
                                                liveData.TireTemps[j] = vehicle.mWheels[j].mTireCarcassTemperature - 273.15;
                                                liveData.TirePressures[j] = vehicle.mWheels[j].mPressure;
                                                liveData.TireLife[j] = 100.0 - (vehicle.mWheels[j].mWear * 100.0);
                                                liveData.BrakeTemps[j] = vehicle.mWheels[j].mBrakeTemp - 273.15;
                                            }

                                            OnLiveTelemetryUpdated?.Invoke(this, liveData);
                                        }
                                        break;
                                    }
                                }
                            }
                        }
                    }
                    catch (FileNotFoundException) { }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[TELEMETRY ERROR] {ex.Message}");
                    }
                }

                _telemetryTickCounter++;
                Thread.Sleep(20); // 50Hz
            }
        }

        private static T ReadSharedMemory<T>(MemoryMappedViewAccessor accessor)
        {
            int size = Marshal.SizeOf(typeof(T));
            byte[] bytes = new byte[size];
            accessor.ReadArray(0, bytes, 0, size);

            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.Copy(bytes, 0, ptr, size);
                return (T)Marshal.PtrToStructure(ptr, typeof(T))!;
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        private static string ParseByteArray(byte[] bytes)
        {
            return Encoding.Default.GetString(bytes).Split('\0')[0];
        }
    }
}