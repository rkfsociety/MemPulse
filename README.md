# MemPulse

A compact Windows memory monitor and cleaner with a per-process RAM view. Inspired by [Mem Reduct](https://github.com/henrypp/memreduct), but implemented independently in C# / WPF.

## Features

- Live physical memory usage, available RAM, system cache, committed bytes and commit limit.
- Searchable, sortable process table: PID, **working set (physical pages mapped into the process)**, and **private committed bytes**.
- One-click purge of standby pages and opt-in advanced actions for system working sets and modified pages.
- Optional trim of a **selected** process working set, with confirmation.
- Optional automatic **standby-only** purge above a RAM usage threshold with a cooldown. Off by default.
- Elevation on demand. Monitoring works without admin rights; system-wide cleaning requires admin.
- Windows notification area, minimize/close-to-tray preferences, optional Windows startup, local settings.
- Windows CI build, smoke tests, and a self-contained portable x64 build artifact.
- Per-user Windows installer and versioned GitHub Releases.
- Optional update checks against the official GitHub Releases API. The update button downloads the release installer, verifies its SHA-256 against the asset digest, and launches the installer after confirmation.

## Build

Requires Windows 10/11 and the .NET 8 SDK with WPF tooling.

```powershell
dotnet build src/MemPulse/MemPulse.csproj -c Release
dotnet run --project src/MemPulse/MemPulse.csproj -c Release
```

Portable self-contained build:

```powershell
dotnet publish src/MemPulse/MemPulse.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

Every push to `main` runs the [Windows build workflow](.github/workflows/windows.yml) and uploads a portable executable artifact after a successful build.

## Releases and updates

New stable releases are built by [Release MemPulse](.github/workflows/release.yml).
Go to **Actions → Release MemPulse → Run workflow**, choose main and enter a new tag such as `v0.1.0`.
Alternatively, push a new tag matching `vMAJOR.MINOR.PATCH` that points to a commit on main.

The workflow builds/tests the WPF project, publishes a self-contained `MemPulse.exe`,
builds a per-user Inno Setup installer (`MemPulse-Setup-x64.exe`), creates
`SHA256SUMS.txt`, and publishes all three files to a GitHub Release. Existing
release tags must not be reused. The version is stamped into the .NET assembly.

**Use the installer for updateable installations.** It installs into
`%LOCALAPPDATA%\\Programs\\MemPulse` without elevation. Once installed, the
app checks for updates at startup and every six hours (configurable on/off in
Settings). Press **Check updates** to check manually, then **Install v…** to download
the installer with SHA-256 verification and open the upgrade wizard. It closes
MemPulse to let setup replace the files. Local settings are preserved.

A standalone portable EXE can also detect new versions, but the update button
runs the installer (migrating to an installed copy), rather than overwriting
the portable file. Release executables are not currently Authenticode-signed:
Windows SmartScreen may display a warning until code signing is configured.

## Understanding the numbers

**Used physical RAM** is total physical RAM minus Windows' available memory. **Working set** is physical memory mapped into a process, including potentially shared pages. It must **not** be summed across processes to estimate total physical RAM. **Private** is process-specific committed memory, not necessarily resident in RAM. File cache is a separate approximate Windows performance counter and overlaps with other memory accounting categories.

## Safety and compatibility

Clearing caches **does not create more physical RAM** or guarantee faster performance. Windows normally reclaims cached memory automatically. Forcing cache purges can **increase disk activity, page faults and stuttering**. Memory cleanup should generally be used only when investigating a specific memory-pressure issue.

The system-wide cleanup actions call the **undocumented** `NtSetSystemInformation(SystemMemoryListInformation)` commands used on current Windows versions. They may fail, behave differently, or cease to work after Windows updates. The UI reports the actual NTSTATUS for failed calls. Run the application normally for monitoring; restart with administrator privileges only when cleanup is needed. The more disruptive working-set and modified-page actions require explicit selection and confirmation. Auto-clean never invokes them.

Protected or short-lived processes might not be readable or trimmable. Process RAM usage is an instantaneous estimate. Settings are stored at `%LOCALAPPDATA%\MemPulse\settings.json` and Windows startup uses the current user's `Run` registry key.

## License and attribution

MemPulse is an independent implementation, **not** a redistribution or fork of Mem Reduct. Mem Reduct is GPL-3.0 licensed; its source code was not copied into MemPulse. The code in this repository is offered under the [MIT License](LICENSE).
