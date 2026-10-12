using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

namespace OPLStats_Stats;

public partial class Stats
{
    private static PlayByPlayEvent BenchmarkEvent(int i) => new PlayByPlayEvent {
        EventId = i + 1, GameTime = i * 0.6f, Period = 1 + i / 1000,
        EventType = i % 7 == 0 ? PlayByPlayEventType.Shot : PlayByPlayEventType.Touch,
        PlayerSteamId = "p" + i % 12, PlayerTeam = i % 2 == 0 ? (int)PlayerTeam.Red : (int)PlayerTeam.Blue,
        PlayerPosition = "C", Position = new Vector3(i % 10 - 5, .1f, i % 2 == 0 ? 32 : -32),
        Outcome = "on net", Zone = EventZone.Offensive, OpposingTeamGoalieSteamID = "=\"g1\"",
        TeamForwardsSteamID = "=\"p1\";=\"p2\";=\"p3\"", TeamDefencemenSteamID = "=\"p4\";=\"p5\"",
        OpposingTeamForwardsSteamID = "=\"p6\";=\"p7\";=\"p8\"", OpposingTeamDefencemenSteamID = "=\"p9\";=\"p10\""
    };

    private static int RunBenchmark(string output)
    {
        for (int i = 0; i < 100; i++) { _playByPlayEvents.Add(BenchmarkEvent(i)); GetOrBuildCachedPlayByPlayAnalytics(); }
        _playByPlayEvents.Clear(); _goals.Clear(); ResetPreviewDerivedDataCaches();
        var results = new Dictionary<string, object>();
        void Measure(string name, Action action)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew(); action(); watch.Stop();
            results[name] = new { milliseconds = watch.Elapsed.TotalMilliseconds, allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated };
        }
        Measure("append_2000_events_with_analytics", () => {
            for (int i = 0; i < 2000; i++) { _playByPlayEvents.Add(BenchmarkEvent(i)); GetOrBuildCachedPlayByPlayAnalytics(); }
        });
        Measure("read_cached_analytics_2000_times", () => {
            for (int i = 0; i < 2000; i++) GetOrBuildCachedPlayByPlayAnalytics();
        });
        int checksum = 0;
        Measure("query_unresolved_shots_20000_times", () => {
            for (int i = 0; i < 20000; i++)
            {
#if PERFORMANCE_IMPLEMENTATION
                checksum += GetShotResolutionCandidates(PlayerTeam.Red, true).Count;
#else
                checksum += _playByPlayEvents.Where(e => e.EventType == PlayByPlayEventType.Shot && e.Outcome == "attempt" && e.PlayerTeam == (int)PlayerTeam.Red).ToList().Count;
#endif
            }
        });
        results["checksum"] = checksum;
        File.WriteAllText(output, JsonConvert.SerializeObject(results, Formatting.Indented));
        Console.WriteLine("PASS: standalone analytics benchmark saved to " + output);
        return 0;
    }
}
