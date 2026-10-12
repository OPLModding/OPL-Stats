param([string]$SourceRoot = '../..', [string]$Output = 'current.json', [switch]$Benchmark)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot $SourceRoot))
$generated = Join-Path $PSScriptRoot 'GeneratedStats.cs'
# The standalone CLR cannot initialize Unity's bundled Harmony detour runtime.
# Only disable that constructor in the harness copy; production source is untouched.
$source = [IO.File]::ReadAllText((Join-Path $root 'OPLStats_Stats/Stats.cs'))
$source = $source.Replace('new Harmony("oomtm450_stats")', 'null')
[IO.File]::WriteAllText($generated, $source)
$constants = if (Test-Path (Join-Path $root 'OPLStats_Stats/Stats.AnalyticsIndexes.cs')) { 'PERFORMANCE_IMPLEMENTATION' } else { 'BASELINE' }
dotnet build (Join-Path $PSScriptRoot 'PerformanceRegression.csproj') -c Release "-p:SourceRoot=$root" "-p:DefineConstants=$constants" -p:EnableNETAnalyzers=false -v:q
if ($LASTEXITCODE -ne 0) { throw 'Regression harness build failed' }
$mode = if ($Benchmark) { 'benchmark' } else { 'regression' }
dotnet (Join-Path $PSScriptRoot 'bin/Release/net9.0/PerformanceRegression.dll') (Join-Path $root 'config/doma_stats/xg_model.json') $Output $mode
if ($LASTEXITCODE -ne 0) { throw 'Regression checks failed' }
