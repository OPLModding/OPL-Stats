#if PERFORMANCE_IMPLEMENTATION
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Codebase;

namespace OPLStats_Stats;

public partial class Stats
{
    private static void RunModelInvalidationChecks()
    {
        var original = _expectedGoalsModel;
        _expectedGoalsModel = Newtonsoft.Json.JsonConvert.DeserializeObject<OPLStats_Stats.Configs.ExpectedGoalsModelConfig>(Newtonsoft.Json.JsonConvert.SerializeObject(original));
        Action validate = () => {
            AssertRegression(GetExpectedGoalsModelCacheSignature() == BuildExpectedGoalsModelCacheSignature(), "Model signature cache is stale");
            RegressionAnalytics("model-invalidation");
        };
        validate();
        _expectedGoalsModel.Intercept += .5; validate();
        string firstWeight = _expectedGoalsModel.Weights.Keys.First();
        _expectedGoalsModel.Weights[firstWeight] += .125; validate();
        _expectedGoalsModel.Weights = _expectedGoalsModel.Weights.Reverse().ToDictionary(p => p.Key, p => p.Value); validate();
        _expectedGoalsModel.IncludedShotOutcomes = new List<string> { "on net" }; validate();
        _expectedGoalsModel.MinimumProbability = .25; validate();
        _expectedGoalsModel.MaximumProbability = .5; validate();
        _expectedGoalsModel.Enabled = false; validate();
        _expectedGoalsModel.Enabled = true; validate();
        _expectedGoalsModel = original; validate();
        Console.WriteLine("PASS: model edits/toggles invalidate cached probabilities without changing model version.");
    }

    private static void AssertRegression(bool valid, string message) { if (!valid) throw new Exception(message); }

    private static void AssertPerformanceIndexes()
    {
        AssertRegression(_orderedAnalyticsEvents.SequenceEqual(_playByPlayEvents.Where(e => e != null).OrderBy(e => e.GameTime).ThenBy(e => e.EventId)), "Ordered event index differs from stable sort");
        foreach (var team in TrackedTeams)
        {
            foreach (bool attempts in new[] { true, false })
            {
                string outcome = attempts ? "attempt" : "on net";
                foreach (float earliest in new[] { float.NegativeInfinity, 30f, 100f })
                {
                    var expected = _playByPlayEvents.Where(e => e.EventType == PlayByPlayEventType.Shot && e.Outcome == outcome && e.PlayerTeam == (int)team && e.GameTime >= earliest);
                    AssertRegression(expected.SequenceEqual(GetShotResolutionCandidates(team, attempts, earliest)), "Shot candidate index differs from legacy filter");
                }
            }
        }
        for (int i = 0; i < 200; i += 7)
            AssertRegression(HasShotResultBetween(i, i + 2) == _playByPlayEvents.Any(e => e.GameTime >= i && e.GameTime <= i + 2 && (e.EventType == PlayByPlayEventType.Save || e.EventType == PlayByPlayEventType.Goal || e.EventType == PlayByPlayEventType.Block)), "Shot result range differs from history scan");
    }

    private static void RunInfrastructureChecks()
    {
        var records = CombinedBatchNames.Select((name, i) => new KeyValuePair<string, string>(name, "76561198000000000;" + i)).ToList();
        AssertRegression(StatBatchCodec.TryEncode(records, out var packets), "Normal batch encoding failed");
        var decoded = new List<KeyValuePair<string, string>>();
        foreach (var packet in packets)
        {
            AssertRegression(StatBatchCodec.TryDecode(packet, CombinedBatchNames, out var entries), "Normal batch decoding failed");
            decoded.AddRange(entries);
        }
        AssertRegression(records.SequenceEqual(decoded), "Batch order or values changed");
        var random = new Random(49152);
        records.Clear();
        string[] names = CombinedBatchNames.ToArray();
        for (int i = 0; i < 600; i++) records.Add(new KeyValuePair<string, string>(names[i % names.Length], new string(i % 2 == 0 ? '界' : ';', random.Next(1, 300)) + ":😀"));
        AssertRegression(StatBatchCodec.TryEncode(records, out packets) && packets.Count > 1, "UTF8 batch chunking failed");
        decoded.Clear();
        foreach (var packet in packets)
        {
            AssertRegression(Encoding.UTF8.GetByteCount(packet) <= StatBatchCodec.MaximumPayloadBytes, "Packet exceeded byte limit");
            AssertRegression(ValidateStringForNetwork(packet, COMBINED_STAT_BATCH, out _), "Packet exceeded transport bound");
            AssertRegression(StatBatchCodec.TryDecode(packet, CombinedBatchNames, out var entries), "Chunk decode failed");
            decoded.AddRange(entries);
        }
        AssertRegression(records.SequenceEqual(decoded), "UTF8/delimiter batch round trip changed values");
        var many = Enumerable.Repeat(new KeyValuePair<string,string>(names[0], ""), 4097).ToList();
        AssertRegression(StatBatchCodec.TryEncode(many, out var manyPackets) && manyPackets.Count >= 2, "Entry-count chunking failed");
        foreach (string packet in manyPackets) AssertRegression(StatBatchCodec.TryDecode(packet, CombinedBatchNames, out _), "Entry-count chunk was not decodable");
        foreach (string invalid in new[] { "", "1", "-1:x", "9999999:x", "3:SOG99999:x", "6:_ALL_V11:1", "8:RESETALL1:1", "3:SOG2:x" })
            AssertRegression(!StatBatchCodec.TryDecode(invalid, CombinedBatchNames, out _), "Accepted malformed or nested batch: " + invalid);
        AssertRegression(!StatBatchCodec.TryEncode(new[] { new KeyValuePair<string,string>(names[0], new string('x', 48000)) }, out _), "Oversize packet must fall back to legacy delivery");

        var previousCulture = CultureInfo.CurrentCulture;
        foreach (string culture in new[] { "en-US", "de-DE", "fr-FR" })
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            foreach (double value in new[] { 0.0, 12.345, 1000000.1234 })
            {
                var pending = new PendingStatValue(value, "F2");
                string expected = value.ToString("F2");
                CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
                AssertRegression(pending.ToString() == expected, "Deferred numeric format lost enqueue culture");
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            }
        }
        CultureInfo.CurrentCulture = previousCulture;

        var queue = new OrderedWorkQueue();
        int callerThread = Thread.CurrentThread.ManagedThreadId;
        var writes = new List<int>();
        var completions = new List<int>();
        for (int i = 0; i < 200; i++)
        {
            int index = i;
            queue.Enqueue(() => { writes.Add(index); if (index == 50) throw new IOException("Expected test error"); }, error => {
                AssertRegression(Thread.CurrentThread.ManagedThreadId == callerThread, "Completion escaped calling thread");
                AssertRegression((error != null) == (index == 50), "Lost worker error");
                completions.Add(index);
            });
        }
        queue.Drain();
        AssertRegression(writes.SequenceEqual(Enumerable.Range(0,200)) && completions.SequenceEqual(writes), "Worker lost or reordered jobs after error");
        string directory = Path.Combine(Path.GetTempPath(), "DomaStatsRegression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "live.json");
        try
        {
            for (int i = 0; i < 20; i++)
            {
                string captured = i.ToString(CultureInfo.InvariantCulture);
                queue.Enqueue(() => WriteAllTextAtomically(path, captured), error => AssertRegression(error == null, "Atomic write failed"));
            }
            queue.Drain();
            AssertRegression(File.ReadAllText(path) == "19" && !File.Exists(path + ".writing"), "Final atomic write was lost or stale");
        }
        finally { File.Delete(path); File.Delete(path + ".writing"); Directory.Delete(directory); }
        Console.WriteLine("PASS: all 36 batch categories, UTF8/chunk limits, malformed batches, culture, 200 ordered jobs/error recovery, atomic writes.");
    }
}
#endif
