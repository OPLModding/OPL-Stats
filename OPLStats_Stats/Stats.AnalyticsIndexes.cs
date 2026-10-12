using System;
using System.Collections.Generic;

namespace OPLStats_Stats;

public partial class Stats
{
    private sealed class CachedShotXg
    {
        internal double Probability;
        internal double NetZ;
        internal string ModelSignature;
    }
    private static readonly Dictionary<PlayByPlayEvent, CachedShotXg> _shotXgCache = new Dictionary<PlayByPlayEvent, CachedShotXg>();
    private static long _analyticsRevision;
    private static long _eventStructureRevision;
    private static float _analyticsDirtyFromTime = float.NegativeInfinity;
    private static readonly List<PlayByPlayEvent> _orderedAnalyticsEvents = new List<PlayByPlayEvent>();
    private static readonly List<PlayByPlayEvent> _attemptShots = new List<PlayByPlayEvent>();
    private static readonly List<PlayByPlayEvent> _onNetShots = new List<PlayByPlayEvent>();
    private static readonly List<PlayByPlayEvent> _shotResultEvents = new List<PlayByPlayEvent>();
    private static readonly List<PlayByPlayEvent> _shotResolutionScratch = new List<PlayByPlayEvent>();
    private static readonly PlayerTeam[] TrackedTeams = { PlayerTeam.Blue, PlayerTeam.Red };
    private static readonly PlayerTeam[] CheckTeams = { PlayerTeam.Red, PlayerTeam.Blue };

    private static void MarkAnalyticsChanged(float fromTime)
    {
        _analyticsRevision++;
        _analyticsDirtyFromTime = Math.Min(_analyticsDirtyFromTime, fromTime);
        _cachedLiveBoxscoreSnapshot = null;
        ClearFrozenGameExportSnapshot();
    }

    private static int CompareAnalyticsEvents(PlayByPlayEvent left, PlayByPlayEvent right)
    {
        int time = left.GameTime.CompareTo(right.GameTime);
        return time != 0 ? time : left.EventId.CompareTo(right.EventId);
    }

    private static void InsertOrderedEvent(List<PlayByPlayEvent> events, PlayByPlayEvent item)
    {
        int lo = 0, hi = events.Count;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            int comparison = CompareAnalyticsEvents(events[mid], item);
            if (comparison < 0 || (comparison == 0 && _playByPlayEvents.IndexOf(events[mid]) <= _playByPlayEvents.IndexOf(item))) lo = mid + 1;
            else hi = mid;
        }
        events.Insert(lo, item);
    }

    private static void EventCollectionChanged(PlayByPlayEvent item, bool added)
    {
        if (item == null) return;
        item.IsTracked = added;
        if (added) InsertOrderedEvent(_orderedAnalyticsEvents, item);
        else { _orderedAnalyticsEvents.Remove(item); _shotXgCache.Remove(item); }
        _eventStructureRevision++;
        UpdateShotIndexes(item);
        MarkAnalyticsChanged(item.GameTime);
    }

    private static void EventDataChanged(PlayByPlayEvent item, float previousTime, bool orderingChanged)
    {
        if (orderingChanged)
        {
            _orderedAnalyticsEvents.Remove(item);
            InsertOrderedEvent(_orderedAnalyticsEvents, item);
        }
        UpdateShotIndexes(item);
        MarkAnalyticsChanged(Math.Min(previousTime, item.GameTime));
    }

    private static void SetShotMembership(List<PlayByPlayEvent> index, PlayByPlayEvent item, bool included)
    {
        int existing = index.IndexOf(item);
        if (!included)
        {
            if (existing >= 0) index.RemoveAt(existing);
            return;
        }
        if (existing >= 0) return;
        if (_playByPlayEvents.Count > 0 && ReferenceEquals(_playByPlayEvents[_playByPlayEvents.Count - 1], item))
        {
            index.Add(item);
            return;
        }
        // Preserve the source list's order, including backdated insertions.
        int sourceIndex = _playByPlayEvents.IndexOf(item);
        int insertion = index.Count;
        for (int i = 0; i < index.Count; i++)
        {
            if (_playByPlayEvents.IndexOf(index[i]) > sourceIndex) { insertion = i; break; }
        }
        index.Insert(insertion, item);
    }

    private static void UpdateShotIndexes(PlayByPlayEvent item)
    {
        bool shot = item.IsTracked && item.EventType == PlayByPlayEventType.Shot;
        SetShotMembership(_attemptShots, item, shot && item.Outcome == "attempt");
        SetShotMembership(_onNetShots, item, shot && item.Outcome == "on net");
        _shotResultEvents.Remove(item);
        if (item.IsTracked && (item.EventType == PlayByPlayEventType.Save || item.EventType == PlayByPlayEventType.Goal || item.EventType == PlayByPlayEventType.Block))
            InsertOrderedEvent(_shotResultEvents, item);
    }

    private static List<PlayByPlayEvent> GetShotResolutionCandidates(PlayerTeam team, bool attempts, float earliest = float.NegativeInfinity)
    {
        _shotResolutionScratch.Clear();
        List<PlayByPlayEvent> source = attempts ? _attemptShots : _onNetShots;
        for (int i = 0; i < source.Count; i++)
        {
            PlayByPlayEvent item = source[i];
            if (item.PlayerTeam == (int)team && item.GameTime >= earliest) _shotResolutionScratch.Add(item);
        }
        return _shotResolutionScratch;
    }

    private static bool HasShotResultBetween(float start, float end)
    {
        int lo = 0, hi = _shotResultEvents.Count;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (_shotResultEvents[mid].GameTime < start) lo = mid + 1;
            else hi = mid;
        }
        return lo < _shotResultEvents.Count && _shotResultEvents[lo].GameTime <= end;
    }

    private static void GoalCollectionChanged(GoalInfo item, bool added)
    {
        if (item != null) item.IsTracked = added;
        MarkAnalyticsChanged(float.NegativeInfinity);
    }
}
