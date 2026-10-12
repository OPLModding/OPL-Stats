using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Codebase;

namespace OPLStats_Stats;

public partial class Stats
{
	private static bool HasCurrentFrozenGameExportSnapshot()
	{
		return IsFrozenGameExportSnapshotCurrentGame(_frozenGameExportSnapshot);
	}

	private static FrozenGameExportSnapshot GetCurrentFrozenGameExportSnapshot()
	{
		return HasCurrentFrozenGameExportSnapshot() ? _frozenGameExportSnapshot : null;
	}

	private static FrozenGameExportSnapshot GetOrCreateFrozenGameExportSnapshot()
	{
		if (HasCurrentFrozenGameExportSnapshot())
		{
			return _frozenGameExportSnapshot;
		}
		_frozenGameExportSnapshot = BuildFrozenGameExportSnapshot();
		return _frozenGameExportSnapshot;
	}

	private static void ClearFrozenGameExportSnapshot()
	{
		_frozenGameExportSnapshot = null;
	}

	private static bool IsFrozenGameExportSnapshotCurrentGame(FrozenGameExportSnapshot snapshot)
	{
		if (snapshot == null)
		{
			return false;
		}
		if (!string.Equals(snapshot.GameReferenceId, GetCurrentGameReferenceIdOrCreate(), StringComparison.Ordinal))
		{
			return false;
		}
		return snapshot.SourceEventCount == _playByPlayEvents.Count && snapshot.SourceGoalCount == _goals.Count && snapshot.SourceShootoutAttemptCount == GetCurrentShootoutAttemptCount() && snapshot.SourceShootoutCompleted == _shootoutCompletedThisGame;
	}

	private static FrozenGameExportSnapshot BuildFrozenGameExportSnapshot()
	{
		AuditGoalTrackingAgainstOfficialStats(applyCorrections: false);
		CachedPlayByPlayAnalytics orBuildCachedPlayByPlayAnalytics = GetOrBuildCachedPlayByPlayAnalytics();
		List<PlayByPlayEvent> list = orBuildCachedPlayByPlayAnalytics.OrderedEvents;
		ExpectedGoalsExportData expectedGoalsExportData = orBuildCachedPlayByPlayAnalytics.ExpectedGoals;
		ResolvedPlayByPlaySummary resolvedPlayByPlaySummary = orBuildCachedPlayByPlayAnalytics.ResolvedSummary;
		LiveBoxscoreData liveBoxscoreData = BuildLiveBoxscoreData();
		ResolveBestAvailableFinalScore(list, liveBoxscoreData, out var redScore, out var blueScore);
		bool wasOvertime = DidFinalGameGoToOvertime(list);
		if (liveBoxscoreData != null)
		{
			liveBoxscoreData.RedTeam.Goals = redScore;
			liveBoxscoreData.BlueTeam.Goals = blueScore;
		}
		var (winningGoal, winningTeam) = GetWinningGoalForScore(redScore, blueScore, _goals);
		Dictionary<int, string> dictionary = BuildFrozenStarAssignments(liveBoxscoreData, winningGoal?.Scorer ?? "", winningTeam);
		List<GoalInfo> list2 = _goals.OrderBy((GoalInfo g) => g.Period).ThenBy((GoalInfo g) => g.GameTime).Select(CloneGoalInfo).ToList();
		if (winningGoal != null)
		{
			GoalInfo goalInfo = list2.FirstOrDefault((GoalInfo g) => string.Equals(g.Scorer, winningGoal.Scorer, StringComparison.Ordinal) && string.Equals(g.Team, winningGoal.Team, StringComparison.OrdinalIgnoreCase) && g.Period == winningGoal.Period && Math.Abs(g.GameTime - winningGoal.GameTime) < 0.001f);
			if (goalInfo != null)
			{
				goalInfo.GWG = true;
			}
		}
		List<PlayByPlayEvent> list3 = list.Select(ClonePlayByPlayEvent).ToList();
		ConvertPendingShotAttemptsToMissed(list3);
		return new FrozenGameExportSnapshot
		{
			GameReferenceId = GetCurrentGameReferenceIdOrCreate(),
			FrozenAtUtc = DateTime.UtcNow,
			SourceEventCount = _playByPlayEvents.Count,
			SourceGoalCount = _goals.Count,
			SourceShootoutAttemptCount = GetCurrentShootoutAttemptCount(),
			SourceShootoutCompleted = _shootoutCompletedThisGame,
			RedScore = redScore,
			BlueScore = blueScore,
			WasOvertime = wasOvertime,
			WinningGoalScorerSteamId = winningGoal?.Scorer ?? "",
			WinningTeam = winningTeam,
			Goals = list2,
			ShootoutAttempts = BuildOrderedShootoutAttemptRecords(_currentGameShootoutBoxscoreState).Select(CloneShootoutAttemptRecord).Where((BoxscoreShootoutAttemptRecord attempt) => attempt != null).ToList(),
			PlayByPlayEvents = list3,
			ExpectedGoals = CloneExpectedGoalsExportData(expectedGoalsExportData),
			ResolvedPlayByPlaySummary = CloneResolvedPlayByPlaySummary(resolvedPlayByPlaySummary),
			LiveData = CloneLiveBoxscoreData(liveBoxscoreData),
			Stars = dictionary,
			PlayerRoleParticipation = BuildPlayerRoleParticipationSnapshot()
		};
	}

	private static bool DidFinalGameGoToOvertime(IEnumerable<PlayByPlayEvent> events)
	{
		try
		{
			if (NetworkBehaviourSingleton<GameManager>.Instance != null && NetworkBehaviourSingleton<GameManager>.Instance.GameState != null && NetworkBehaviourSingleton<GameManager>.Instance.GameState.Value.IsOvertime)
			{
				return true;
			}
		}
		catch
		{
		}
		if (events != null && events.Any((PlayByPlayEvent e) => e != null && e.Period > 3))
		{
			return true;
		}
		return GetCurrentPeriod() > 3;
	}

	private static void ResolveBestAvailableFinalScore(IEnumerable<PlayByPlayEvent> events, LiveBoxscoreData liveBoxscoreData, out int redScore, out int blueScore)
	{
		if (TryGetFinalScoreFromGameEnd(events, out redScore, out blueScore))
		{
			return;
		}
		redScore = liveBoxscoreData?.RedTeam?.Goals ?? 0;
		blueScore = liveBoxscoreData?.BlueTeam?.Goals ?? 0;
		if (redScore > 0 || blueScore > 0)
		{
			return;
		}
		try
		{
			if (NetworkBehaviourSingleton<GameManager>.Instance != null && NetworkBehaviourSingleton<GameManager>.Instance.GameState != null)
			{
				redScore = NetworkBehaviourSingleton<GameManager>.Instance.GameState.Value.RedScore;
				blueScore = NetworkBehaviourSingleton<GameManager>.Instance.GameState.Value.BlueScore;
				if (redScore > 0 || blueScore > 0)
				{
					return;
				}
			}
		}
		catch
		{
		}
		redScore = _goals.Count((GoalInfo g) => string.Equals(g.Team, "Red", StringComparison.OrdinalIgnoreCase));
		blueScore = _goals.Count((GoalInfo g) => string.Equals(g.Team, "Blue", StringComparison.OrdinalIgnoreCase));
	}

	private static bool TryGetFinalScoreFromGameEnd(IEnumerable<PlayByPlayEvent> events, out int redScore, out int blueScore)
	{
		redScore = 0;
		blueScore = 0;
		if (events == null)
		{
			return false;
		}
		PlayByPlayEvent playByPlayEvent = events.Where((PlayByPlayEvent e) => e != null && e.EventType == PlayByPlayEventType.GameEnd).OrderByDescending((PlayByPlayEvent e) => e.Period).ThenByDescending((PlayByPlayEvent e) => e.GameTime).FirstOrDefault();
		if (playByPlayEvent == null)
		{
			return false;
		}
		return TryParseBlueRedScoreState(playByPlayEvent.ScoreState, out blueScore, out redScore);
	}

	private static bool TryParseBlueRedScoreState(string scoreState, out int blueScore, out int redScore)
	{
		blueScore = 0;
		redScore = 0;
		if (string.IsNullOrWhiteSpace(scoreState))
		{
			return false;
		}
		string[] array = scoreState.Split('-');
		if (array.Length != 2)
		{
			return false;
		}
		return int.TryParse(array[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out blueScore) && int.TryParse(array[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out redScore);
	}

	private static (GoalInfo WinningGoal, PlayerTeam WinningTeam) GetWinningGoalForScore(int redScore, int blueScore, IEnumerable<GoalInfo> goals)
	{
		PlayerTeam playerTeam = PlayerTeam.None;
		if (blueScore > redScore)
		{
			playerTeam = PlayerTeam.Blue;
		}
		else if (redScore > blueScore)
		{
			playerTeam = PlayerTeam.Red;
		}
		if (playerTeam == PlayerTeam.None || goals == null)
		{
			return (null, playerTeam);
		}
		int num = 0;
		int num2 = 0;
		foreach (GoalInfo item in goals.OrderBy((GoalInfo g) => g.Period).ThenBy((GoalInfo g) => g.GameTime))
		{
			if (string.Equals(item.Team, "Blue", StringComparison.OrdinalIgnoreCase))
			{
				num++;
			}
			else if (string.Equals(item.Team, "Red", StringComparison.OrdinalIgnoreCase))
			{
				num2++;
			}
			if (playerTeam == PlayerTeam.Blue && string.Equals(item.Team, "Blue", StringComparison.OrdinalIgnoreCase) && num > num2 && num == redScore + 1)
			{
				return (item, playerTeam);
			}
			if (playerTeam == PlayerTeam.Red && string.Equals(item.Team, "Red", StringComparison.OrdinalIgnoreCase) && num2 > num && num2 == blueScore + 1)
			{
				return (item, playerTeam);
			}
		}
		return (null, playerTeam);
	}

	private static Dictionary<int, string> BuildFrozenStarAssignments(LiveBoxscoreData liveBoxscoreData, string winningGoalScorerSteamId, PlayerTeam winningTeam)
	{
		Dictionary<int, string> dictionary = new Dictionary<int, string>();
		foreach (int item in _stars.Keys.OrderBy((int rank) => rank))
		{
			dictionary[item] = _stars.TryGetValue(item, out var value) ? value ?? "" : "";
		}
		bool flag = dictionary.Values.Any((string steamId) => !string.IsNullOrWhiteSpace(steamId));
		if (flag || liveBoxscoreData?.Players == null)
		{
			return dictionary;
		}
		List<BoxscorePlayerSnapshot> list = liveBoxscoreData.Players.Where((BoxscorePlayerSnapshot p) => p != null && (p.Team == PlayerTeam.Red || p.Team == PlayerTeam.Blue) && !string.IsNullOrWhiteSpace(p.SteamId)).OrderByDescending((BoxscorePlayerSnapshot p) => CalculatePreviousGameStarPoints(p, winningTeam, winningGoalScorerSteamId)).ThenByDescending((BoxscorePlayerSnapshot p) => p.Points).ThenByDescending((BoxscorePlayerSnapshot p) => p.Sog).ToList();
		for (int i = 0; i < 3; i++)
		{
			dictionary[i + 1] = (i < list.Count) ? list[i].SteamId : "";
		}
		return dictionary;
	}

	private static void ConvertPendingShotAttemptsToMissed(IEnumerable<PlayByPlayEvent> events)
	{
		if (events == null)
		{
			return;
		}
		foreach (PlayByPlayEvent item in events)
		{
			if (item != null && item.EventType == PlayByPlayEventType.Shot && string.Equals(item.Outcome, "attempt", StringComparison.OrdinalIgnoreCase))
			{
				item.Outcome = "missed";
			}
		}
	}

	private static GoalInfo CloneGoalInfo(GoalInfo source)
	{
		if (source == null)
		{
			return null;
		}
		return new GoalInfo
		{
			GameTime = source.GameTime,
			Period = source.Period,
			Team = source.Team ?? "",
			Scorer = source.Scorer ?? "",
			ScorerPosition = source.ScorerPosition ?? "",
			PrimaryAssist = source.PrimaryAssist,
			PrimaryAssistPosition = source.PrimaryAssistPosition ?? "",
			SecondaryAssist = source.SecondaryAssist,
			SecondaryAssistPosition = source.SecondaryAssistPosition ?? "",
			GWG = source.GWG
		};
	}

	private static PlayByPlayEvent ClonePlayByPlayEvent(PlayByPlayEvent source)
	{
		if (source == null)
		{
			return null;
		}
		return new PlayByPlayEvent
		{
			EventId = source.EventId,
			EventType = source.EventType,
			GameTime = source.GameTime,
			Period = source.Period,
			PlayerSteamId = source.PlayerSteamId ?? "",
			PlayerName = source.PlayerName ?? "",
			PlayerTeam = source.PlayerTeam,
			PlayerPosition = source.PlayerPosition ?? "",
			PlayerJersey = source.PlayerJersey,
			PlayerSpeed = source.PlayerSpeed,
			Zone = source.Zone,
			Position = source.Position,
			Velocity = source.Velocity,
			ForceMagnitude = source.ForceMagnitude,
			Outcome = source.Outcome ?? "",
			Flags = source.Flags ?? "",
			Team = source.Team ?? "",
			TeamInPossession = source.TeamInPossession ?? "",
			CurrentPlayInPossession = source.CurrentPlayInPossession ?? "",
			ScoreState = source.ScoreState ?? "",
			TeamForwardsSteamID = source.TeamForwardsSteamID ?? "",
			TeamDefencemenSteamID = source.TeamDefencemenSteamID ?? "",
			TeamGoalieSteamID = source.TeamGoalieSteamID ?? "",
			OpposingTeamForwardsSteamID = source.OpposingTeamForwardsSteamID ?? "",
			OpposingTeamDefencemenSteamID = source.OpposingTeamDefencemenSteamID ?? "",
			OpposingTeamGoalieSteamID = source.OpposingTeamGoalieSteamID ?? "",
			Timestamp = source.Timestamp,
			Xg = source.Xg
		};
	}

	private static ExpectedGoalsExportData CloneExpectedGoalsExportData(ExpectedGoalsExportData source)
	{
		ExpectedGoalsExportData expectedGoalsExportData = new ExpectedGoalsExportData();
		if (source == null)
		{
			return expectedGoalsExportData;
		}
		expectedGoalsExportData.Enabled = source.Enabled;
		expectedGoalsExportData.ModelVersion = source.ModelVersion ?? "";
		expectedGoalsExportData.TrainedThrough = source.TrainedThrough ?? "";
		expectedGoalsExportData.ModelType = source.ModelType ?? "";
		expectedGoalsExportData.ShotsEvaluated = source.ShotsEvaluated;
		foreach (KeyValuePair<string, double> item in source.ShooterXgBySteamId)
		{
			expectedGoalsExportData.ShooterXgBySteamId[item.Key] = item.Value;
		}
		foreach (KeyValuePair<string, double> item2 in source.GoalieXgaBySteamId)
		{
			expectedGoalsExportData.GoalieXgaBySteamId[item2.Key] = item2.Value;
		}
		return expectedGoalsExportData;
	}

	private static ResolvedPlayByPlaySummary CloneResolvedPlayByPlaySummary(ResolvedPlayByPlaySummary source)
	{
		ResolvedPlayByPlaySummary resolvedPlayByPlaySummary = new ResolvedPlayByPlaySummary();
		if (source == null)
		{
			return resolvedPlayByPlaySummary;
		}
		foreach (KeyValuePair<string, int> item in source.GoalsBySteamId)
		{
			resolvedPlayByPlaySummary.GoalsBySteamId[item.Key] = item.Value;
		}
		foreach (KeyValuePair<string, int> item2 in source.SogBySteamId)
		{
			resolvedPlayByPlaySummary.SogBySteamId[item2.Key] = item2.Value;
		}
		foreach (KeyValuePair<string, (int ShotsFaced, int GoalsAllowed)> item3 in source.GoalieSummary)
		{
			resolvedPlayByPlaySummary.GoalieSummary[item3.Key] = item3.Value;
		}
		foreach (int item4 in source.OfficialGoalShotEventIds)
		{
			resolvedPlayByPlaySummary.OfficialGoalShotEventIds.Add(item4);
		}
		foreach (int item5 in source.OwnGoalShotEventIds)
		{
			resolvedPlayByPlaySummary.OwnGoalShotEventIds.Add(item5);
		}
		return resolvedPlayByPlaySummary;
	}

	private static LiveBoxscoreData CloneLiveBoxscoreData(LiveBoxscoreData source)
	{
		LiveBoxscoreData liveBoxscoreData = new LiveBoxscoreData();
		if (source == null)
		{
			return liveBoxscoreData;
		}
		liveBoxscoreData.Players = source.Players?.Select(CloneBoxscorePlayerSnapshot).Where((BoxscorePlayerSnapshot p) => p != null).ToList() ?? new List<BoxscorePlayerSnapshot>();
		liveBoxscoreData.RedTeam = CloneBoxscoreTeamSnapshot(source.RedTeam) ?? new BoxscoreTeamSnapshot
		{
			Team = PlayerTeam.Red,
			Label = "Red"
		};
		liveBoxscoreData.BlueTeam = CloneBoxscoreTeamSnapshot(source.BlueTeam) ?? new BoxscoreTeamSnapshot
		{
			Team = PlayerTeam.Blue,
			Label = "Blue"
		};
		liveBoxscoreData.ExpectedGoals = CloneExpectedGoalsExportData(source.ExpectedGoals);
		return liveBoxscoreData;
	}

	private static BoxscorePlayerSnapshot CloneBoxscorePlayerSnapshot(BoxscorePlayerSnapshot source)
	{
		if (source == null)
		{
			return null;
		}
		return new BoxscorePlayerSnapshot
		{
			SteamId = source.SteamId ?? "",
			Name = source.Name ?? "",
			Team = source.Team,
			TeamLabel = source.TeamLabel ?? "spectator",
			Position = source.Position ?? "",
			IsGoalie = source.IsGoalie,
			Goals = source.Goals,
			Assists = source.Assists,
			Sog = source.Sog,
			IndividualCorsiFor = source.IndividualCorsiFor,
			OnIceTeamShotAttempts = source.OnIceTeamShotAttempts,
			IndividualCorsiForPercentage = source.IndividualCorsiForPercentage,
			Blocks = source.Blocks,
			Passes = source.Passes,
			Exits = source.Exits,
			Entries = source.Entries,
			Hits = source.Hits,
			HitsTaken = source.HitsTaken,
			Turnovers = source.Turnovers,
			Takeaways = source.Takeaways,
			PuckTouches = source.PuckTouches,
			PossessionTimeSeconds = source.PossessionTimeSeconds,
			TimeOnIceSeconds = source.TimeOnIceSeconds,
			DefensiveZonePossessionTimeSeconds = source.DefensiveZonePossessionTimeSeconds,
			NeutralZonePossessionTimeSeconds = source.NeutralZonePossessionTimeSeconds,
			OffensiveZonePossessionTimeSeconds = source.OffensiveZonePossessionTimeSeconds,
			DefensiveZoneTimeOnIceSeconds = source.DefensiveZoneTimeOnIceSeconds,
			NeutralZoneTimeOnIceSeconds = source.NeutralZoneTimeOnIceSeconds,
			OffensiveZoneTimeOnIceSeconds = source.OffensiveZoneTimeOnIceSeconds,
			PostHits = source.PostHits,
			FaceoffWins = source.FaceoffWins,
			FaceoffLosses = source.FaceoffLosses,
			Xg = source.Xg,
			PlusMinus = source.PlusMinus,
			ShotsFaced = source.ShotsFaced,
			Saves = source.Saves,
			GoalsAllowed = source.GoalsAllowed,
			SavePercentage = source.SavePercentage,
			BodySaves = source.BodySaves,
			PadSaves = source.PadSaves,
			StickSaves = source.StickSaves,
			Xga = source.Xga,
			Gsax = source.Gsax
		};
	}

	private static BoxscoreTeamSnapshot CloneBoxscoreTeamSnapshot(BoxscoreTeamSnapshot source)
	{
		if (source == null)
		{
			return null;
		}
		return new BoxscoreTeamSnapshot
		{
			Team = source.Team,
			Label = source.Label ?? "",
			Goals = source.Goals,
			Assists = source.Assists,
			ShotsOnGoal = source.ShotsOnGoal,
			ExpectedGoals = source.ExpectedGoals,
			Saves = source.Saves,
			ExpectedGoalsAgainst = source.ExpectedGoalsAgainst,
			FaceoffWins = source.FaceoffWins,
			FaceoffTotal = source.FaceoffTotal,
			Passes = source.Passes,
			Hits = source.Hits,
			Blocks = source.Blocks,
			Giveaways = source.Giveaways,
			Takeaways = source.Takeaways,
			ZoneEntries = source.ZoneEntries,
			ZoneExits = source.ZoneExits,
			PossessionTimeSeconds = source.PossessionTimeSeconds,
			DefensiveZonePossessionTimeSeconds = source.DefensiveZonePossessionTimeSeconds,
			NeutralZonePossessionTimeSeconds = source.NeutralZonePossessionTimeSeconds,
			OffensiveZonePossessionTimeSeconds = source.OffensiveZonePossessionTimeSeconds
		};
	}
}
