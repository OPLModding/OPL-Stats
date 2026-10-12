using System;
using System.Collections.Generic;
using System.Linq;
using Codebase;

namespace OPLStats_Stats;

public partial class Stats
{
	private sealed class CachedPlayByPlayAnalytics
	{
		public string GameReferenceId { get; set; } = "";

		public string ModelSignature { get; set; } = "";

		public long Fingerprint { get; set; }

		public List<PlayByPlayEvent> OrderedEvents { get; set; } = new List<PlayByPlayEvent>();

		public ExpectedGoalsExportData ExpectedGoals { get; set; } = new ExpectedGoalsExportData();

		public ResolvedPlayByPlaySummary ResolvedSummary { get; set; } = new ResolvedPlayByPlaySummary();
	}

	private sealed class CachedLiveBoxscoreSnapshot
	{
		public string GameReferenceId { get; set; } = "";

		public string ModelSignature { get; set; } = "";

		public long AnalyticsFingerprint { get; set; }

		public long LiveStatsRevision { get; set; }

		public long TimeOnIceRevision { get; set; }

		public long RosterFingerprint { get; set; }

		public LiveBoxscoreData Data { get; set; } = new LiveBoxscoreData();
	}

	private static long _previewLiveStatsRevision = 0L;

	private static long _previewTimeOnIceRevision = 0L;

	private static CachedPlayByPlayAnalytics _cachedPlayByPlayAnalytics = null;

	private static CachedLiveBoxscoreSnapshot _cachedLiveBoxscoreSnapshot = null;

	private static void MarkPreviewLiveStatsChanged()
	{
		_previewLiveStatsRevision++;
		_cachedLiveBoxscoreSnapshot = null;
		ClearFrozenGameExportSnapshot();
	}

	private static void MarkPreviewTimeOnIceChanged()
	{
		_previewTimeOnIceRevision++;
		_cachedLiveBoxscoreSnapshot = null;
		ClearFrozenGameExportSnapshot();
	}

	private static void ResetPreviewDerivedDataCaches()
	{
		_previewLiveStatsRevision = 0L;
		_previewTimeOnIceRevision = 0L;
		_cachedPlayByPlayAnalytics = null;
		_analyticsDirtyFromTime = float.NegativeInfinity;
		_shotXgCache.Clear();
		_cachedLiveBoxscoreSnapshot = null;
		ClearFrozenGameExportSnapshot();
	}

	private static CachedPlayByPlayAnalytics GetOrBuildCachedPlayByPlayAnalytics()
	{
		string currentGameReferenceIdOrCreate = GetCurrentGameReferenceIdOrCreate();
		string expectedGoalsModelCacheSignature = GetExpectedGoalsModelCacheSignature();
		long num = _analyticsRevision;
		CachedPlayByPlayAnalytics cachedPlayByPlayAnalytics = _cachedPlayByPlayAnalytics;
		if (cachedPlayByPlayAnalytics != null && cachedPlayByPlayAnalytics.Fingerprint == num && string.Equals(cachedPlayByPlayAnalytics.GameReferenceId, currentGameReferenceIdOrCreate, StringComparison.Ordinal) && string.Equals(cachedPlayByPlayAnalytics.ModelSignature, expectedGoalsModelCacheSignature, StringComparison.Ordinal))
		{
			return cachedPlayByPlayAnalytics;
		}
		ExpectedGoalsExportData expectedGoalsExportData = new ExpectedGoalsExportData
		{
			Enabled = _expectedGoalsModel != null && _expectedGoalsModel.Enabled,
			ModelVersion = _expectedGoalsModel?.ModelVersion ?? "",
			TrainedThrough = _expectedGoalsModel?.TrainedThrough ?? "",
			ModelType = _expectedGoalsModel?.ModelType ?? ""
		};
		List<PlayByPlayEvent> list = _orderedAnalyticsEvents;
		ResetPlayByPlayModelAnnotations(list);
		ResolvedPlayByPlaySummary resolvedPlayByPlaySummary = new ResolvedPlayByPlaySummary();
		if (list.Count > 0)
		{
			resolvedPlayByPlaySummary = BuildResolvedPlayByPlaySummary(list, applyResolvedShotOutcomes: true);
			AnnotateExpectedGoals(list, expectedGoalsExportData);
		}
		cachedPlayByPlayAnalytics = new CachedPlayByPlayAnalytics
		{
			GameReferenceId = currentGameReferenceIdOrCreate,
			ModelSignature = expectedGoalsModelCacheSignature,
			Fingerprint = _analyticsRevision,
			OrderedEvents = list,
			ExpectedGoals = expectedGoalsExportData,
			ResolvedSummary = resolvedPlayByPlaySummary
		};
		_cachedPlayByPlayAnalytics = cachedPlayByPlayAnalytics;
		_analyticsDirtyFromTime = float.PositiveInfinity;
		return cachedPlayByPlayAnalytics;
	}

	private static ResolvedPlayByPlaySummary GetResolvedPlayByPlaySummaryForCurrentAnalytics(List<PlayByPlayEvent> orderedEvents)
	{
		CachedPlayByPlayAnalytics cachedPlayByPlayAnalytics = _cachedPlayByPlayAnalytics;
		if (cachedPlayByPlayAnalytics != null && cachedPlayByPlayAnalytics.Fingerprint == _analyticsRevision && ReferenceEquals(cachedPlayByPlayAnalytics.OrderedEvents, orderedEvents))
		{
			return cachedPlayByPlayAnalytics.ResolvedSummary;
		}
		return BuildResolvedPlayByPlaySummary(orderedEvents);
	}

	private static bool TryGetCachedLiveBoxscoreData(out LiveBoxscoreData liveBoxscoreData)
	{
		liveBoxscoreData = null;
		CachedLiveBoxscoreSnapshot cachedLiveBoxscoreSnapshot = _cachedLiveBoxscoreSnapshot;
		if (cachedLiveBoxscoreSnapshot == null)
		{
			return false;
		}
		string currentGameReferenceIdOrCreate = GetCurrentGameReferenceIdOrCreate();
		if (!string.Equals(cachedLiveBoxscoreSnapshot.GameReferenceId, currentGameReferenceIdOrCreate, StringComparison.Ordinal))
		{
			return false;
		}
		CachedPlayByPlayAnalytics orBuildCachedPlayByPlayAnalytics = GetOrBuildCachedPlayByPlayAnalytics();
		string expectedGoalsModelCacheSignature = GetExpectedGoalsModelCacheSignature();
		long liveBoxscoreRosterFingerprint = ComputeLiveBoxscoreRosterFingerprint();
		if (cachedLiveBoxscoreSnapshot.AnalyticsFingerprint != orBuildCachedPlayByPlayAnalytics.Fingerprint || cachedLiveBoxscoreSnapshot.LiveStatsRevision != _previewLiveStatsRevision || cachedLiveBoxscoreSnapshot.TimeOnIceRevision != _previewTimeOnIceRevision || cachedLiveBoxscoreSnapshot.RosterFingerprint != liveBoxscoreRosterFingerprint || !string.Equals(cachedLiveBoxscoreSnapshot.ModelSignature, expectedGoalsModelCacheSignature, StringComparison.Ordinal))
		{
			return false;
		}
		liveBoxscoreData = CloneLiveBoxscoreData(cachedLiveBoxscoreSnapshot.Data);
		return liveBoxscoreData != null;
	}

	private static void StoreCachedLiveBoxscoreData(LiveBoxscoreData liveBoxscoreData)
	{
		if (liveBoxscoreData == null)
		{
			_cachedLiveBoxscoreSnapshot = null;
			return;
		}
		CachedPlayByPlayAnalytics orBuildCachedPlayByPlayAnalytics = GetOrBuildCachedPlayByPlayAnalytics();
		_cachedLiveBoxscoreSnapshot = new CachedLiveBoxscoreSnapshot
		{
			GameReferenceId = GetCurrentGameReferenceIdOrCreate(),
			ModelSignature = GetExpectedGoalsModelCacheSignature(),
			AnalyticsFingerprint = orBuildCachedPlayByPlayAnalytics.Fingerprint,
			LiveStatsRevision = _previewLiveStatsRevision,
			TimeOnIceRevision = _previewTimeOnIceRevision,
			RosterFingerprint = ComputeLiveBoxscoreRosterFingerprint(),
			Data = CloneLiveBoxscoreData(liveBoxscoreData)
		};
	}

	private static string BuildExpectedGoalsModelCacheSignature()
	{
		if (_expectedGoalsModel == null)
		{
			return "none";
		}
		string text = string.Join(",", (_expectedGoalsModel.IncludedShotOutcomes ?? new List<string>()).OrderBy((string outcome) => outcome, StringComparer.OrdinalIgnoreCase));
		return string.Join("|", new string[9]
		{
			_expectedGoalsModel.Enabled ? "1" : "0",
			_expectedGoalsModel.ModelVersion ?? "",
			_expectedGoalsModel.TrainedThrough ?? "",
			_expectedGoalsModel.ModelType ?? "",
			text,
			_expectedGoalsModel.Intercept.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
			_expectedGoalsModel.MinimumProbability.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
			_expectedGoalsModel.MaximumProbability.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
			string.Join(";", (_expectedGoalsModel.Weights ?? new Dictionary<string, double>()).Select(w => w.Key + "=" + w.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)))
		});
	}

	private static long ComputePlayByPlayAnalyticsFingerprint(string modelSignature)
	{
		long num = -3750763034362895579L;
		MixHash(ref num, GetCurrentGameReferenceIdOrCreate());
		MixHash(ref num, modelSignature);
		MixHash(ref num, _playByPlayEvents.Count);
		foreach (PlayByPlayEvent playByPlayEvent in _playByPlayEvents)
		{
			if (playByPlayEvent == null)
			{
				MixHash(ref num, -1);
				continue;
			}
			MixHash(ref num, playByPlayEvent.EventId);
			MixHash(ref num, (int)playByPlayEvent.EventType);
			MixHash(ref num, playByPlayEvent.GameTime);
			MixHash(ref num, playByPlayEvent.Period);
			MixHash(ref num, playByPlayEvent.PlayerSteamId);
			MixHash(ref num, playByPlayEvent.PlayerTeam);
			MixHash(ref num, playByPlayEvent.PlayerPosition);
			MixHash(ref num, playByPlayEvent.PlayerJersey);
			MixHash(ref num, playByPlayEvent.PlayerSpeed);
			MixHash(ref num, (int)playByPlayEvent.Zone);
			MixHash(ref num, playByPlayEvent.Position.x);
			MixHash(ref num, playByPlayEvent.Position.y);
			MixHash(ref num, playByPlayEvent.Position.z);
			MixHash(ref num, playByPlayEvent.Velocity.x);
			MixHash(ref num, playByPlayEvent.Velocity.y);
			MixHash(ref num, playByPlayEvent.Velocity.z);
			MixHash(ref num, playByPlayEvent.ForceMagnitude);
			MixHash(ref num, playByPlayEvent.Outcome);
			MixHash(ref num, playByPlayEvent.Flags);
			MixHash(ref num, playByPlayEvent.Team);
			MixHash(ref num, playByPlayEvent.TeamInPossession);
			MixHash(ref num, playByPlayEvent.CurrentPlayInPossession);
			MixHash(ref num, playByPlayEvent.ScoreState);
			MixHash(ref num, playByPlayEvent.TeamForwardsSteamID);
			MixHash(ref num, playByPlayEvent.TeamDefencemenSteamID);
			MixHash(ref num, playByPlayEvent.TeamGoalieSteamID);
			MixHash(ref num, playByPlayEvent.OpposingTeamForwardsSteamID);
			MixHash(ref num, playByPlayEvent.OpposingTeamDefencemenSteamID);
			MixHash(ref num, playByPlayEvent.OpposingTeamGoalieSteamID);
		}
		MixHash(ref num, _goals.Count);
		foreach (GoalInfo item in _goals.OrderBy((GoalInfo goal) => goal.Period).ThenBy((GoalInfo goal) => goal.GameTime))
		{
			MixHash(ref num, item.GameTime);
			MixHash(ref num, item.Period);
			MixHash(ref num, item.Team);
			MixHash(ref num, item.Scorer);
			MixHash(ref num, item.PrimaryAssist);
			MixHash(ref num, item.SecondaryAssist);
			MixHash(ref num, item.GWG ? 1 : 0);
		}
		return num;
	}

	private static long ComputeLiveBoxscoreRosterFingerprint()
	{
		long num = -3750763034362895579L;
		List<Player> players = MonoBehaviourSingleton<PlayerManager>.Instance?.GetPlayers() ?? new List<Player>();
		MixHash(ref num, players.Count);
		long num2 = 0L;
		long num3 = 0L;
		foreach (Player item in players)
		{
			if (item == null || !(bool)item)
			{
				continue;
			}
			long num4 = -3750763034362895579L;
			MixHash(ref num4, item.SteamId.Value.ToString());
			MixHash(ref num4, item.Username.Value.ToString());
			MixHash(ref num4, (int)item.Team);
			MixHash(ref num4, GetPlayerPosition(item));
			MixHash(ref num4, item.Number.Value);
			MixHash(ref num4, PlayerFunc.IsPlayerPlaying(item) ? 1 : 0);
			num2 ^= num4;
			num3 += num4;
		}
		MixHash(ref num, num2);
		MixHash(ref num, num3);
		return num;
	}

	private static void MixHash(ref long hash, int value)
	{
		hash = unchecked(hash * 1099511628211L ^ value);
	}

	private static void MixHash(ref long hash, long value)
	{
		hash = unchecked(hash * 1099511628211L ^ value);
	}

	private static void MixHash(ref long hash, float value)
	{
		MixHash(ref hash, value.GetHashCode());
	}

	private static void MixHash(ref long hash, string value)
	{
		MixHash(ref hash, StringComparer.Ordinal.GetHashCode(value ?? ""));
	}
}
