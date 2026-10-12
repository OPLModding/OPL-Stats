using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;

namespace OPLStats_Stats;

public partial class Stats
{
    private sealed class FrozenEventIndex
    {
        internal readonly Dictionary<string, List<PlayByPlayEvent>> Players = new Dictionary<string, List<PlayByPlayEvent>>(StringComparer.Ordinal);
        internal readonly List<PlayByPlayEvent> NullPlayerEvents = new List<PlayByPlayEvent>();
        internal FrozenEventIndex(FrozenGameExportSnapshot snapshot)
        {
            foreach (var item in snapshot.PlayByPlayEvents ?? new List<PlayByPlayEvent>())
            {
                if (item == null) continue;
                if (item.PlayerSteamId == null) { NullPlayerEvents.Add(item); continue; }
                if (!Players.TryGetValue(item.PlayerSteamId, out var events)) Players[item.PlayerSteamId] = events = new List<PlayByPlayEvent>();
                events.Add(item);
            }
        }
    }
    private static readonly ConditionalWeakTable<FrozenGameExportSnapshot, FrozenEventIndex> _frozenEventIndexes = new ConditionalWeakTable<FrozenGameExportSnapshot, FrozenEventIndex>();
    private static IEnumerable<PlayByPlayEvent> GetFrozenPlayerEvents(FrozenGameExportSnapshot snapshot, string steamId)
    {
        if (snapshot == null) return Enumerable.Empty<PlayByPlayEvent>();
        var index = _frozenEventIndexes.GetValue(snapshot, key => new FrozenEventIndex(key));
        if (steamId == null) return index.NullPlayerEvents;
        return index.Players.TryGetValue(steamId, out var events) ? events : Enumerable.Empty<PlayByPlayEvent>();
    }

    private sealed class SerializedPreviousSnapshot { internal string Json; }
    private static readonly ConditionalWeakTable<object, SerializedPreviousSnapshot> _previousSnapshotJson = new ConditionalWeakTable<object, SerializedPreviousSnapshot>();
    private static string SerializePreviousSnapshot(object snapshot) => snapshot == null ? "null" : _previousSnapshotJson.GetValue(snapshot, key => new SerializedPreviousSnapshot { Json = JsonConvert.SerializeObject(key) }).Json;
}
