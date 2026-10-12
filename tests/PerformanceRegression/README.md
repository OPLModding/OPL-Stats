Run these commands from the repository root in PowerShell:

```powershell
./tests/PerformanceRegression/Run.ps1 -Output analysis_outputs/performance-current.json
./tests/PerformanceRegression/Run.ps1 -SourceRoot ../../analysis_outputs/performance_baseline_20260918 -Output analysis_outputs/performance-baseline.json
python -c "import json; assert json.load(open('analysis_outputs/performance-current.json')) == json.load(open('analysis_outputs/performance-baseline.json')); print('PASS: exact baseline comparison')"
```

The saved baseline contains the source before this performance work. Never deploy its project as part of the tests. The harness compiles either source tree into a separate .NET 9 executable against the installed Puck assemblies. It substitutes `null` for the Harmony constructor in a generated harness-only copy of Stats.cs, because Unity's bundled Harmony detour runtime cannot initialize on the standalone CLR. Production code retains the original Harmony initialization and targets .NET Framework 4.8 for Puck.

The checks exercise analytics, history corrections, event indexes, frozen exports, actual CSV bytes, the batch codec, numeric formatting, model invalidation, ordered worker error recovery and atomic file replacement. They do not simulate Unity gameplay, UI events or actual network connections.

For a standalone microbenchmark:

```powershell
./tests/PerformanceRegression/Run.ps1 -Benchmark -Output analysis_outputs/benchmark-current.json
./tests/PerformanceRegression/Run.ps1 -SourceRoot ../../analysis_outputs/performance_baseline_20260918 -Benchmark -Output analysis_outputs/benchmark-baseline.json
```

Run the two builds sequentially: they share the harness output directory. These measurements describe synthetic analytics workloads on this machine, not in-game frame rates. GeneratedStats.cs, bin and obj are disposable harness outputs.

Build the mod without automatic deployment:

```powershell
dotnet build DomaStatsBranch.csproj -c Release -p:DeployOnBuild=false
```
