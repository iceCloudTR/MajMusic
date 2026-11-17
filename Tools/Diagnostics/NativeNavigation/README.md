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

Add `--rapid` for the interrupted home-mode scenario or `--rapid-mobile` for the same scenario in a compact layout 420 logical pixels wide:

```powershell
& $dotnet artifacts/native-navigation-build-UNIQUE/bin/NativeNavigation/release/NativeNavigation.dll --rapid
& $dotnet artifacts/native-navigation-build-UNIQUE/bin/NativeNavigation/release/NativeNavigation.dll --rapid-mobile
```

The run delivers 12 pointer clicks to the diagnostic's own Win32 window in three groups, one from each settled home mode. It moves the pointer over the starting card's quick-play glyph just before the first click and requests successive clicks 42 ms after the previous selection. The feed serves 150 generated recommendations in-process, while Library and Playlists use distinct entries from the generated local database. A silent backend keeps the transport in its playing state without opening or playing audio. No HTTP or credential requests are allowed. `rapid-report.json` records the time from pointer dispatch to the mode notification, whether that switch interrupted motion, visible panel positions, native frame gaps, the first 25 card and decoded-cover references, and the mounted virtualized prefix. It samples six stationary desktop icon paths (four in compact mode) in window coordinates and one quick-play glyph per page in both card and window coordinates. The quick-play glyph may move horizontally with its page; its card-local pose and vertical position should remain unchanged, and its artwork source should stay bound. The original single-list version can produce a baseline report; the resident version requires every prepared card and mounted prefix to retain its identity across the burst. The compact run exercises the shared mobile layout on Windows; it is not a device measurement.
