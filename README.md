# RaceLogger

A Windows desktop companion for Le Mans Ultimate: live telemetry dashboard, lap logging to Google Sheets, a leaderboard built from your own laps, and automatic updates.

> **Status:** work in progress (v0.2.4)

## Features

- [x] Live telemetry dashboard: gear, RPM, speed, throttle, brake, steering, fuel, water/oil temperature,
      tyre temperature / pressure / wear, brake temperature, and live car position displayed on the map
- [x] Lap logging: detects completed laps, sector times and session type, and skips invalid laps (track limits)
- [x] Uploads laps to a Google Sheets webhook
- [x] Leaderboard and drive history built from the sheet, with a per-track details view
- [x] Track maps for 30 tracks
- [x] Settings: autostart with Windows and hide to tray
- [x] Built-in OTA updates: checks GitHub releases and verifies the SHA-256 checksum before installing
- [ ] _Planned: full telemetry recording, so you can review every detail of your races afterwards_
- [ ] _Planned: dashboard overview with the latest leaderboard changes and upcoming races_

## Tech stack

- C# / .NET 10
- Avalonia UI 12 (Fluent theme)
- Memory-mapped files (rFactor 2 shared memory interface used by Le Mans Ultimate)
- Google Sheets webhook for storage

## Getting started

1. Download the latest release from the Releases page (or build from source below).
2. Install the shared memory plugin: download `rFactor2SharedMemoryMapPlugin64.dll` from the [plugin's repository](https://github.com/TheIronWolfModding/rF2SharedMemoryMapPlugin) and copy it into the `Plugins` folder of your Le Mans Ultimate installation (create the folder if it doesn't exist).
3. Create a Google Sheet and webhook that receives laps: [step by step tutorial link] <!-- MAKE A TUTORIAL AND ADD A LINK HERE -->.
4. Open **Settings**, paste the webhook URL and press **Save & Sync**.
5. Start Le Mans Ultimate. The dashboard connects automatically.

### Build from source

```
git clone https://github.com/Snefee/RaceLogger.git
cd RaceLogger
dotnet run --project RaceLogger.App
```

Requires the .NET 10 SDK and Windows.

## Data sent to your sheet

Track, driver, class, car, lap time, sector times, session, tyres, conditions, temperatures, date and game.
Nothing is sent anywhere else.

## Credits

`rF2Data.cs` is from [rF2SharedMemoryMapPlugin](https://github.com/TheIronWolfModding/rF2SharedMemoryMapPlugin)
by The Iron Wolf.

Unofficial hobby project, not affiliated with the developers of Le Mans Ultimate.
