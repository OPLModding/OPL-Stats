using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;
using OPLStats_Stats.Configs;

namespace OPLStats_Stats;

public partial class Stats
{
    public static int RunPerformanceRegression(string[] args)
    {
        _currentGameReferenceId = "regression";
        _expectedGoalsModel = JsonConvert.DeserializeObject<ExpectedGoalsModelConfig>(File.ReadAllText(args[0]));
        _expectedGoalsModel.Enabled = true;
        if (args.Length > 2 && args[2] == "benchmark") return RunBenchmark(args[1]);
#if PERFORMANCE_IMPLEMENTATION
        RunInfrastructureChecks();
#endif
        var output = new List<object>();
        var random = new System.Random(1947);
        for (int i = 0; i < 250; i++)
        {
            var item = new PlayByPlayEvent {
                EventId = i + 1, GameTime = i * 0.6f, Period = i / 100 + 1,
                EventType = i % 7 == 0 ? PlayByPlayEventType.Shot : i % 3 == 0 ? PlayByPlayEventType.Pass : PlayByPlayEventType.Touch,
                PlayerSteamId = "p" + i % 12, PlayerName = "Player " + i % 12,
                PlayerTeam = i % 2 == 0 ? (int)PlayerTeam.Red : (int)PlayerTeam.Blue,
                PlayerPosition = i % 5 == 0 ? "G" : "C", Zone = EventZone.Offensive,
                Position = new Vector3((float)random.NextDouble() * 20 - 10, 0.1f, (i % 2 == 0 ? 1 : -1) * (20 + (float)random.NextDouble() * 18)),
                Velocity = new Vector3(1, 0.1f, 10), ForceMagnitude = 10, PlayerSpeed = 4,
                Outcome = i % 7 == 0 ? "on net" : "successful", Flags = i % 13 == 0 ? "OZEntry" : "",
                ScoreState = "1v0", CurrentPlayInPossession = (i % 8).ToString(),
                TeamForwardsSteamID = "=\"p1\";=\"p2\"", TeamDefencemenSteamID = "=\"p3\"",
                TeamGoalieSteamID = "=\"g1\"", OpposingTeamForwardsSteamID = "=\"p4\";=\"p5\"",
                OpposingTeamDefencemenSteamID = "=\"p6\"", OpposingTeamGoalieSteamID = "=\"g2\"",
                Timestamp = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            };
            _playByPlayEvents.Add(item);
            output.Add(RegressionAnalytics("append-" + i));
        }
        PlayByPlayEvent shot = _playByPlayEvents.First(e => e.EventType == PlayByPlayEventType.Shot && e.EventId > 100);
        foreach (string outcome in new[] { "attempt", "missed", "blocked", "on net", "goal" })
        {
            shot.Outcome = outcome;
            output.Add(RegressionAnalytics("correction-" + outcome));
        }
        var goal = new GoalInfo { GameTime = shot.GameTime + 0.1f, Period = shot.Period, Scorer = shot.PlayerSteamId, Team = ((PlayerTeam)shot.PlayerTeam).ToString(), ScorerPosition = "G" };
        _goals.Add(goal);
        output.Add(RegressionAnalytics("official-goal"));
        goal.Scorer = "p3"; goal.PrimaryAssist = "p7";
        output.Add(RegressionAnalytics("corrected-scorer"));
        _goals.Remove(goal);
        output.Add(RegressionAnalytics("goal-removed"));
        var inserted = ClonePlayByPlayEvent(_playByPlayEvents[2]);
        inserted.EventId = 500; inserted.GameTime = shot.GameTime - 0.01f; inserted.Flags = "OZEntry";
        _playByPlayEvents.Insert(4, inserted);
        output.Add(RegressionAnalytics("backdated-entry"));
        inserted.GameTime = shot.GameTime + 10;
        output.Add(RegressionAnalytics("retimed-event"));
        _playByPlayEvents.Remove(inserted);
        output.Add(RegressionAnalytics("removed-event"));
        shot.OpposingTeamGoalieSteamID = "=\"replacement\"";
        output.Add(RegressionAnalytics("goalie-swap"));
        shot.Position = new Vector3(3, 0.4f, -shot.Position.z);
        output.Add(RegressionAnalytics("position-correction"));
        inserted = ClonePlayByPlayEvent(shot); inserted.EventId = 501; inserted.EventType = PlayByPlayEventType.OwnGoal; inserted.GameTime += 1;
        _playByPlayEvents.Add(inserted);
        output.Add(RegressionAnalytics("own-goal"));
        for (int i = 0; i < 300; i++)
        {
            var changed = _playByPlayEvents[random.Next(_playByPlayEvents.Count)];
            switch (i % 9)
            {
                case 0: changed.GameTime = (float)random.NextDouble() * 200; break;
                case 1: changed.EventType = i % 2 == 0 ? PlayByPlayEventType.Save : PlayByPlayEventType.Shot; break;
                case 2: changed.EventType = PlayByPlayEventType.Block; break;
                case 3: changed.Outcome = i % 2 == 0 ? "attempt" : "on net"; break;
                case 4: changed.PlayerTeam = i % 2 == 0 ? (int)PlayerTeam.Red : (int)PlayerTeam.Blue; break;
                case 5: changed.Flags = i % 2 == 0 ? "OZEntry,DZExit" : "rebound"; break;
                case 6: changed.Position = new Vector3(i % 20, 0.2f, i % 37); break;
                case 7: changed.OpposingTeamGoalieSteamID = i % 2 == 0 ? "" : "=\"g3\""; break;
                case 8: changed.PlayerPosition = i % 2 == 0 ? "G" : "LW"; break;
            }
            output.Add(RegressionAnalytics("random-correction-" + i));
        }
#if PERFORMANCE_IMPLEMENTATION
        RunModelInvalidationChecks();
#endif
        var data = GetOrBuildCachedPlayByPlayAnalytics();
        var frozen = new FrozenGameExportSnapshot { GameReferenceId = "regression", PlayByPlayEvents = data.OrderedEvents.Select(ClonePlayByPlayEvent).ToList(), ExpectedGoals = CloneExpectedGoalsExportData(data.ExpectedGoals), Goals = _goals.Select(CloneGoalInfo).ToList() };
        frozen.LiveData.Players.Add(new BoxscorePlayerSnapshot { SteamId = "p0", Name = "Player 0", Team = PlayerTeam.Red, Position = "C", Sog = 50, IndividualCorsiFor = 50, OnIceTeamShotAttempts = 100, Passes = 40, PuckTouches = 100, Xg = 10, TimeOnIceSeconds = 100 });
        output.Add(new { stage = "dual-role-export", payload = BuildGameStatsExportPayload(frozen) });
        output.Add(new { stage = "csv-export-bytes", payload = RegressionCsvExport(frozen) });
        string frozenJson = JsonConvert.SerializeObject(frozen.PlayByPlayEvents.Select(e => new { e.EventId, e.Flags, e.Outcome, e.Xg }));
        shot.Flags = "changed";
        if (frozenJson != JsonConvert.SerializeObject(frozen.PlayByPlayEvents.Select(e => new { e.EventId, e.Flags, e.Outcome, e.Xg }))) throw new Exception("Frozen events were mutated");
        _playByPlayEvents.Clear(); _goals.Clear(); ResetPreviewDerivedDataCaches();
        output.Add(RegressionAnalytics("reset"));
        File.WriteAllText(args[1], JsonConvert.SerializeObject(output, Formatting.Indented));
        Console.WriteLine("PASS: " + output.Count + " analytics/export checkpoints; frozen snapshot isolation.");
        return 0;
    }

    private static object RegressionAnalytics(string stage)
    {
#if PERFORMANCE_IMPLEMENTATION
        AssertPerformanceIndexes();
#endif
        var data = GetOrBuildCachedPlayByPlayAnalytics();
        object result = RegressionAnalyticsValue(stage, data);
#if PERFORMANCE_IMPLEMENTATION
        _cachedPlayByPlayAnalytics = null;
        _shotXgCache.Clear();
        _analyticsDirtyFromTime = float.NegativeInfinity;
        var rebuilt = GetOrBuildCachedPlayByPlayAnalytics();
        if (JsonConvert.SerializeObject(result) != JsonConvert.SerializeObject(RegressionAnalyticsValue(stage, rebuilt)))
            throw new Exception("Cached analytics differed from complete rebuild at " + stage);
        AssertPerformanceIndexes();
#endif
        return result;
    }

    private static string RegressionCsvExport(FrozenGameExportSnapshot snapshot)
    {
        string previousDirectory = Directory.GetCurrentDirectory();
        string directory = Path.Combine(Path.GetTempPath(), "DomaStatsCsvRegression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        bool previousLogInfo = ServerConfig.LogInfo;
        string previousHeader = ServerConfig.FileHeaderName;
        string path = Path.Combine(directory, "stats", "regression_regression_playbyplay.csv");
        try
        {
            Directory.SetCurrentDirectory(directory);
            ServerConfig.LogInfo = false;
            ServerConfig.FileHeaderName = "regression";
            ExportPlayByPlayCSVInternal(snapshot);
#if PERFORMANCE_IMPLEMENTATION
            DrainFileWork();
            if (CaptureExportJson(snapshot).Value != JsonConvert.SerializeObject(BuildGameStatsExportPayload(snapshot), Formatting.Indented))
                throw new Exception("Captured JSON differs from synchronous payload serialization");
#endif
            return Convert.ToBase64String(File.ReadAllBytes(path));
        }
        finally
        {
            Directory.SetCurrentDirectory(previousDirectory);
            ServerConfig.LogInfo = previousLogInfo;
            ServerConfig.FileHeaderName = previousHeader;
            File.Delete(path);
            if (Directory.Exists(Path.Combine(directory, "stats"))) Directory.Delete(Path.Combine(directory, "stats"));
            Directory.Delete(directory);
        }
    }

    private static object RegressionAnalyticsValue(string stage, CachedPlayByPlayAnalytics data)
    {
        return new {
            stage,
            events = data.OrderedEvents.Select(e => new { e.EventId, e.Outcome, e.Xg }).ToArray(),
            expected = CloneExpectedGoalsExportData(data.ExpectedGoals),
            resolved = CloneResolvedPlayByPlaySummary(data.ResolvedSummary)
        };
    }
}
