# Native navigation diagnostics

This standalone project opens its own normal Windows/Skia window and closes it after a finite run. It does not launch the production executable, read player settings, access stored accounts, play audio or contact a source. Its real `MainView` uses generated covers, 600 songs and 20 private playlists in a new artifact directory.

Build into a new isolated output tree, then run its own DLL with the same .NET 10 host. This also works when the machine-wide `dotnet` is an older version:

```powershell
$dotnet = Join-Path $env:USERPROFILE '.dotnet/dotnet.exe'
& $dotnet build Tools/Diagnostics/NativeNavigation/NativeNavigation.csproj -c Release --artifacts-path artifacts/native-navigation-build-UNIQUE
& $dotnet artifacts/native-navigation-build-UNIQUE/bin/NativeNavigation/release/NativeNavigation.dll
```

Use `dotnet` from PATH instead if it already resolves to .NET 10. The optional first DLL argument selects a new artifact destination. The default is `artifacts/native-navigation-<timestamp>-<id>` under the current working directory. Do not point it at an existing player data directory.

Outputs:

- `report.json`: first-use and warmed Home/Settings/Directories navigation with Full motion, warmed navigation with motion Off, Library/Playlists mode commands, constructor/explicit-layout costs, actual snapshot dimensions and zero-network/credential counters. `CaptureDuringAction` and `CaptureDuringPhase` read the production surface's actual counters and final capture duration; a duration is not a sum when a host's count exceeds one.
- `IdlePageWarmupBeforeNavigation` records completed detached warmups, elapsed work, failures and page-root `Loaded` counts before any navigation; inactive roots should remain unloaded.
- The original two `home-mode:` phases remain unchanged for comparison. A subsequent `home-mode-warm:` round waits for `BrowsePreloadUpdate` before each target, and records actual placeholder reuse counters separately.
- Real `SongCard` references are observed before an action, after its explicitly timed first layout, and after the phase. `NewCardInstances` and `ReusedCardInstances` use reference equality against the initial attached tree; snapshots also retain generated track IDs and clipped viewport visibility. Returning from a page with no attached cards can count returning controls as new references, so compare reuse between populated home modes. No visual-tree scan is performed every frame.
- `RowsDuringAction`, `RowsAfterLayout` and `RowsDuringPhase` count the production `BrowseRows` collection's Reset/Add/Remove/Replace/Move notifications, distinguishing stable row rebinding from native container resets.
- `summary.csv`: synchronous invocation, explicit layout, first selected-state feedback, scene recording, UI frame callback and maximum scheduling gaps per phase.
- `timeline.csv`: monotonic frame and 8ms dispatcher-heartbeat observations.
- `failure.txt`: an observed diagnostic failure, when present.

The window uses the normal platform frame driver. Frame callbacks and scene recording are not GPU-present or display-scan-out timestamps. First layout is forced and timed separately. The production private `CaptureSurface` method is invoked through reflection outside navigation measurements to isolate bitmap allocation plus synchronous rasterization; reflection overhead is included. Full-versus-Off comparisons also include animation setup. No screenshots or image encoders run inside navigation timing phases.
