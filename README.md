# OPL Stats

Stats and analytics mod for Puck, maintained by OPL Modding.

The mod provides live player and team statistics, play-by-play exports, expected-goals scoring, boxscore UI support, and client-side network performance reporting from the game’s F5 advanced debug diagnostics.

## Repository layout

- `OPLStats_Stats/` — core stats, tracking, networking, and export implementation.
- `OPLStats_Stats.Configs/` — client, server, and expected-goals configuration.
- `Codebase/` and `Codebase.Configs/` — shared game integration helpers.
- `config/doma_stats/xg_model.json` — bundled expected-goals model.
- `tests/PerformanceRegression/` — performance regression harness.
- `tools/` — offline model and release tooling.

Generated builds, local game assemblies, decompilation output, and machine-specific analysis are intentionally excluded from version control.

## Build prerequisites

1. Install the .NET SDK with .NET Framework 4.8 targeting support.
2. Install Puck and ensure its managed assemblies are available under the standard Steam installation path, or override `PuckGameDir`/`PuckManagedDir` when building.
3. Keep `config/doma_stats/xg_model.json` present. The model is required by the build and is bundled with deployment.

Build without deployment:

```powershell
dotnet build DomaStatsBranch.csproj -c Release -p:DeployOnBuild=false
```

Deploy to the local Puck plugins folder:

```powershell
dotnet build DomaStatsBranch.csproj -c Release -p:DeployOnBuild=true
```

The deployment target copies the plugin DLL and `xg_model.json` to the configured Puck plugins directory. The game should be closed before replacing a loaded plugin.

## Exports

Tracked games use the existing dated game reference naming convention. Network diagnostics are written separately as:

```text
{server}_{game-reference}_network_performance.json
```

The export contains per-player reports for each period, including application frame timing, network traffic/ping/stalls, and object synchronization health. Client reports are aggregated locally and sent at period boundaries; the server rewrites the same game JSON as reports arrive.
