Performance implementation — 19 September 2026

The Release build contains server and client optimizations. It has been compiled against the installed Puck assemblies and tested against the saved original source. The installed Workshop DLL has not been replaced.

The changes cover:

| Area | Implementation |
| --- | --- |
| xG | Reuse unchanged shot probabilities. Recalculate affected history after corrections and invalidate probabilities when inferred net direction or model settings change. |
| Analytics caching | Track event and goal property changes directly; maintain ordered events instead of hashing and sorting the entire history on every lookup. Model-signature checks avoid repeated string allocation. |
| Shot resolution | Maintain attempt/on-net and save/goal/block indexes; query candidates and result time ranges without scanning the whole history each frame. Preserve original per-team processing order. |
| Frame allocations | Reuse team arrays and possession state, stop replacing inactive check objects, and format possession values when sending instead of every update. |
| Exports | Capture detached data on the game thread, then serialize JSON and format/write CSV through an ordered worker. Keep live-export intervals, forced export triggers, file names, formats, export limits and upload behavior. JSON writes use atomic replacement; normal quit/disable drains queued writes. |
| Rosters and reflection | Categorize each roster once per capture; cache reflection metadata while reading current object values. |
| Aggregate data | Cache faceoff/goal/assist totals independently of time-on-ice and possession updates; index frozen player histories for dual-role exports. |
| Snapshot serialization | Reuse serialized JSON for the same frozen export and for immutable previous-game responses. |
| Zone scanning | Keep the previous scan position, validate its event ID, and fall back to the original search after insertions/removals. |
| Tooltips | One repeating refresh per visible tooltip, unregister callbacks on replacement, combine incoming refresh requests, and avoid repeated hidden-tooltip cleanup. Handle UI-root replacement and goalie-layout changes. |
| Scoreboard | Use direct player-map lookup with fallback; keep one geometry callback per observed element. |
| Boxscore | Reject irrelevant tab responses before parsing; keep existing content when an identical payload is already displayed. |
| Client stat files | Capture one file set per received batch, write it off-thread in order, and skip unchanged files that still exist. |
| Networking | Negotiate combined batches with updated clients; preserve legacy packets for other clients. Preserve reliable delivery, chunk by UTF-8 byte size, restrict decoded messages to supported stat categories, and bulk-read receive buffers. |
| Logging/build | Avoid constructing disabled network log messages, dispose network writers on exceptions, remove the empty FixedUpdate patch, and produce an optimized Release DLL. |

The implementation retains full goal reconciliation, xG total accumulation, roster-sensitive plus/minus calculations, and existing zone attribution rules. These are still needed for corrections, own goals and role changes. Changed boxscore payloads still rebuild their content; unchanged payloads reuse it. A persistent collider-to-player cache was not introduced because object ownership can change. No gameplay timing, statistical definition, model coefficients, configuration defaults or export threshold was intentionally changed.

Validation completed:

- Release build: zero errors, ten existing warnings (obsolete Rigidbody.velocity calls and unused fields/variables).
- 567 baseline checkpoints match exactly, including actual CSV bytes, xG, goalie attribution, official goal corrections, backdated/retimed events, random historical edits, dual-role exports and resets.
- Cached analytics agree with complete recalculation at the checkpoints. Shot indexes agree with the original history filters and range scans.
- Model weights, intercept, outcome inclusion, probability bounds and enabled state invalidate cached values without requiring a version change.
- All 36 batch categories round-trip; UTF-8, delimiters, chunk limits, oversized fallback, malformed/nested messages and entry-count boundaries are checked.
- Deferred values retain numeric formatting culture. Two hundred queued jobs preserve work/completion order through an injected error; atomic replacement leaves the latest result. Frozen events remain isolated from live edits.

One warmed standalone microbenchmark on this machine produced these results:

| Workload | Original | Changed |
| --- | ---: | ---: |
| Append 2,000 events with analytics | 1,811 ms; 2,115 MB allocated | 567 ms; 224 MB allocated |
| Read cached analytics 2,000 times | 356 ms; 2.4 MB allocated | 1.8 ms; effectively no allocation |
| Query attempts 20,000 times over a 2,000-event history with no pending attempts | 166 ms; 2.1 MB allocated | 1.1 ms; effectively no allocation |

These are synthetic .NET measurements, not measured Puck frame-rate gains. The harness disables Harmony initialization only in its generated test copy; it does not simulate Unity gameplay or UI. Instructions are in tests/PerformanceRegression/README.md; raw results and a source diff are in analysis_outputs.

In-game validation remains necessary for the scoreboard/tooltips, gameplay hooks, actual old/new client connections, and quit/reset event ordering. Exercise a full game with saves, blocks, own goals, role/team swaps, disconnect/rejoin, overtime/shootout, previous-game tabs, live/final exports, and normal mod disable. Test both legacy and updated clients against the updated server.

The package is artifacts/DomaStatsBranch-performance-release.zip. Its manifest records checksums, source files and test results. For installation, close Puck/the server and replace the DLL in the existing mod installation. The currently detected Workshop installation is C:\Program Files (x86)\Steam\steamapps\workshop\content\2994020\3571959574. Keep a single active copy of this mod. The package includes the unchanged bundled xG model and the original DLL under rollback; preserve existing configuration files. Restoring rollback/DomaStatsBranch.dll restores the original installed binary.

To build without deployment, run `dotnet build DomaStatsBranch.csproj -c Release -p:DeployOnBuild=false`. The normal project's existing automatic-deployment behavior remains available when that property is omitted.
