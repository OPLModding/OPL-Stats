using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Codebase;
using HarmonyLib;
using Newtonsoft.Json;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;
using OPLStats_Stats.Configs;

namespace OPLStats_Stats;

public partial class Stats
{
	private static int GetPositionOrderPriority(string position, PlayerTeam team)
	{
		int num = ((team != PlayerTeam.Blue) ? 1000 : 0);
		return position switch
		{
			"C" => num + 1,
			"LW" => num + 2,
			"RW" => num + 3,
			"LD" => num + 4,
			"RD" => num + 5,
			"G" => num + 6,
			_ => num + 7,
		};
	}

	private static int GetBoxscoreTeamSortOrder(PlayerTeam team)
	{
		return team switch
		{
			PlayerTeam.Red => 0,
			PlayerTeam.Blue => 1,
			_ => 2,
		};
	}

	private static string ResolvePlayerNameForBoxscore(string playerSteamId, Player livePlayer)
	{
		if (livePlayer != null && (bool)livePlayer)
		{
			return StripHtmlTags(livePlayer.Username.Value.ToString());
		}
		PlayByPlayEvent playByPlayEvent = _playByPlayEvents.LastOrDefault((PlayByPlayEvent e) => e.PlayerSteamId == playerSteamId && !string.IsNullOrEmpty(e.PlayerName));
		if (playByPlayEvent != null && !string.IsNullOrEmpty(playByPlayEvent.PlayerName))
		{
			return StripHtmlTags(playByPlayEvent.PlayerName);
		}
		return playerSteamId;
	}

	private static PlayerTeam ResolvePlayerTeamForBoxscore(string playerSteamId, Player livePlayer)
	{
		if (livePlayer != null && (bool)livePlayer && (livePlayer.Team == PlayerTeam.Red || livePlayer.Team == PlayerTeam.Blue))
		{
			return livePlayer.Team;
		}
		List<PlayByPlayEvent> list = _playByPlayEvents.Where((PlayByPlayEvent e) => e.PlayerSteamId == playerSteamId && (e.PlayerTeam == (int)PlayerTeam.Blue || e.PlayerTeam == (int)PlayerTeam.Red)).ToList();
		if (list.Count == 0)
		{
			return PlayerTeam.None;
		}
		var anon = (from e in list
			group e by e.PlayerTeam into g
			select new
			{
				Team = g.Key,
				Count = g.Count()
			}).OrderByDescending(g => g.Count).FirstOrDefault();
		if (anon == null)
		{
			return PlayerTeam.None;
		}
		return (PlayerTeam)anon.Team;
	}

	private static string ResolvePlayerPositionForBoxscore(string playerSteamId, Player livePlayer)
	{
		if (livePlayer != null && (bool)livePlayer)
		{
			string playerPosition = GetPlayerPosition(livePlayer);
			if (!string.IsNullOrEmpty(playerPosition))
			{
				return playerPosition;
			}
		}
		List<PlayByPlayEvent> list = _playByPlayEvents.Where((PlayByPlayEvent e) => e.PlayerSteamId == playerSteamId && !string.IsNullOrEmpty(e.PlayerPosition)).ToList();
		if (list.Count > 0)
		{
			var anon = (from e in list
				group e by e.PlayerPosition into g
				select new
				{
					Position = g.Key,
					Count = g.Count(),
					LastGameTime = g.Max((PlayByPlayEvent e) => e.GameTime)
				}).OrderByDescending(g => g.Count).ThenByDescending(g => g.LastGameTime).FirstOrDefault();
			if (anon != null && !string.IsNullOrEmpty(anon.Position))
			{
				return anon.Position;
			}
		}
		return HasTrackedGoalieStats(playerSteamId) ? "G" : "";
	}

	private static bool HasTrackedGoalieStats(string playerSteamId, IReadOnlyDictionary<string, (int ShotsFaced, int GoalsAllowed)> goalieSummary = null)
	{
		if (string.IsNullOrWhiteSpace(playerSteamId))
		{
			return false;
		}
		if (goalieSummary != null && goalieSummary.ContainsKey(playerSteamId))
		{
			return true;
		}
		if (_savePerc.TryGetValue(playerSteamId, out (int Saves, int Shots) value) && (value.Saves > 0 || value.Shots > 0))
		{
			return true;
		}
		if ((_stickSaves.TryGetValue(playerSteamId, out var value2) && value2 > 0) || (_bodySaves.TryGetValue(playerSteamId, out var value3) && value3 > 0) || (_padSaves.TryGetValue(playerSteamId, out var value4) && value4 > 0) || (_homePlateSaves.TryGetValue(playerSteamId, out var value5) && value5 > 0) || (_homePlateShots.TryGetValue(playerSteamId, out var value6) && value6 > 0))
		{
			return true;
		}
		return false;
	}

	private static (string WinningGoalScorerSteamId, PlayerTeam WinningTeam) GetCurrentWinningGoalScorer()
	{
		ResolveBestAvailableFinalScore(_playByPlayEvents, liveBoxscoreData: null, out var redScore, out var blueScore);
		var (winningGoal, winningTeam) = GetWinningGoalForScore(redScore, blueScore, _goals);
		return (winningGoal?.Scorer ?? "", winningTeam);
	}

	private static double CalculateCurrentStarPoints(BoxscorePlayerSnapshot snapshot)
	{
		if (snapshot == null || snapshot.Team != PlayerTeam.Red && snapshot.Team != PlayerTeam.Blue)
		{
			return 0.0;
		}
		var (winningGoalScorerSteamId, winningTeam) = GetCurrentWinningGoalScorer();
		double num = (winningGoalScorerSteamId == snapshot.SteamId) ? 0.5 : 0.0;
		double num2 = ((winningTeam == snapshot.Team) ? 1.1 : 1.0);
		double num3 = 0.0;
		if (snapshot.IsGoalie)
		{
			num3 += (double)snapshot.GoalsAllowed * -10.0;
			num3 += (double)snapshot.ShotsFaced * 10.0;
			if (snapshot.GoalsAllowed == 0 && snapshot.ShotsFaced > 0)
			{
				num3 += 100.0;
			}
			num3 += (double)snapshot.Passes * 2.5;
			num3 += 175.0 * num;
			num3 += (double)snapshot.Goals * 175.0;
			num3 += (double)snapshot.Assists * 30.0;
		}
		else
		{
			num3 += (double)snapshot.Sog * 7.5;
			num3 += (double)snapshot.Passes * 2.5;
			num3 += (double)snapshot.Blocks * 5.0;
			num3 += 70.0 * num;
			num3 += (double)snapshot.Goals * 70.0;
			num3 += (double)snapshot.Assists * 30.0;
		}
		num3 += (double)snapshot.Hits * 2.5;
		num3 += (double)snapshot.Takeaways * 5.0;
		num3 -= (double)snapshot.Turnovers * 5.0;
		num3 += (double)snapshot.Exits;
		num3 += (double)snapshot.Entries;
		if (NetworkBehaviourSingleton<GameManager>.Instance != null && NetworkBehaviourSingleton<GameManager>.Instance.Phase == GamePhase.Intermission)
		{
			num3 *= num2;
		}
		return num3;
	}

	private static void TryAddLeaderRow(List<BoxscoreLeaderRowSnapshot> rows, string category, IEnumerable<BoxscorePlayerSnapshot> source, Func<BoxscorePlayerSnapshot, double> valueSelector, Func<BoxscorePlayerSnapshot, string> valueFormatter, Func<BoxscorePlayerSnapshot, string> detailFormatter = null, Func<BoxscorePlayerSnapshot, bool> filter = null, bool requirePositive = true)
	{
		if (rows == null || source == null || valueSelector == null || valueFormatter == null)
		{
			return;
		}
		List<BoxscorePlayerSnapshot> list = source.Where((BoxscorePlayerSnapshot p) => p != null && (p.Team == PlayerTeam.Red || p.Team == PlayerTeam.Blue) && (filter == null || filter(p))).OrderByDescending(valueSelector).ThenByDescending((BoxscorePlayerSnapshot p) => p.Points).ThenByDescending((BoxscorePlayerSnapshot p) => p.Sog).ThenBy((BoxscorePlayerSnapshot p) => p.Name).ToList();
		if (list.Count == 0)
		{
			return;
		}
		BoxscorePlayerSnapshot boxscorePlayerSnapshot = list[0];
		double num = valueSelector(boxscorePlayerSnapshot);
		if (requirePositive && num <= 0.00005)
		{
			return;
		}
		rows.Add(new BoxscoreLeaderRowSnapshot
		{
			Category = category,
			Name = boxscorePlayerSnapshot.Name,
			Team = GetTeamDisplayLabel(boxscorePlayerSnapshot.Team),
			Position = boxscorePlayerSnapshot.Position,
			Value = valueFormatter(boxscorePlayerSnapshot),
			Detail = (detailFormatter?.Invoke(boxscorePlayerSnapshot) ?? "")
		});
	}

	private static LiveBoxscoreData BuildLiveBoxscoreData()
	{
		LiveBoxscoreData liveBoxscoreData = new LiveBoxscoreData();
		AuditGoalTrackingAgainstOfficialStats(applyCorrections: false);
		CalculateTimeOnIce();
		if (TryGetCachedLiveBoxscoreData(out var liveBoxscoreData2))
		{
			return liveBoxscoreData2;
		}
        var counts = GetStableBoxscoreCounts();
        Dictionary<string, int> dictionary = counts.FaceoffWins;
        Dictionary<string, int> dictionary2 = counts.FaceoffLosses;
        Dictionary<string, int> dictionary3 = counts.Goals;
        Dictionary<string, int> dictionary4 = counts.Assists;
		CachedPlayByPlayAnalytics orBuildCachedPlayByPlayAnalytics = GetOrBuildCachedPlayByPlayAnalytics();
		List<PlayByPlayEvent> list = orBuildCachedPlayByPlayAnalytics.OrderedEvents;
		ExpectedGoalsExportData expectedGoalsExportData = orBuildCachedPlayByPlayAnalytics.ExpectedGoals;
		ResolvedPlayByPlaySummary resolvedPlayByPlaySummary = orBuildCachedPlayByPlayAnalytics.ResolvedSummary;
		CalculatePlusMinus();
		Dictionary<string, (int ShotsFaced, int GoalsAllowed)> dictionary5 = resolvedPlayByPlaySummary.GoalieSummary;
		liveBoxscoreData.ExpectedGoals = expectedGoalsExportData;
		HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
		void AddKeys(IEnumerable<string> keys)
		{
			if (keys == null)
			{
				return;
			}
			foreach (string key in keys)
			{
				if (!string.IsNullOrWhiteSpace(key))
				{
					hashSet.Add(key);
				}
			}
		}
		AddKeys(_sog.Keys);
		AddKeys(_shotAttempts.Keys);
		AddKeys(_onIceTeamShotAttempts.Keys);
		AddKeys(_blocks.Keys);
		AddKeys(_passes.Keys);
		AddKeys(_puckTouches.Keys);
		AddKeys(_turnovers.Keys);
		AddKeys(_takeaways.Keys);
		AddKeys(_exits.Keys);
		AddKeys(_entries.Keys);
		AddKeys(_hits.Keys);
		AddKeys(_hitsTaken.Keys);
		AddKeys(_possessionTimeSeconds.Keys);
		AddKeys(_timeOnIceSeconds.Keys);
		AddKeys(_possessionTimeByZone.Keys);
		AddKeys(_timeOnIceByZone.Keys);
		AddKeys(_postHits.Keys);
		AddKeys(_savePerc.Keys);
		AddKeys(_padSaves.Keys);
		AddKeys(dictionary5.Keys);
		AddKeys(resolvedPlayByPlaySummary.SogBySteamId.Keys);
		AddKeys(_plusMinus.Keys);
		AddKeys(dictionary3.Keys);
		AddKeys(dictionary4.Keys);
		foreach (PlayByPlayEvent playByPlayEvent in _playByPlayEvents)
		{
			if (!string.IsNullOrWhiteSpace(playByPlayEvent.PlayerSteamId))
			{
				hashSet.Add(playByPlayEvent.PlayerSteamId);
			}
		}
		List<Player> players = MonoBehaviourSingleton<PlayerManager>.Instance?.GetPlayers() ?? new List<Player>();
		foreach (Player item2 in players)
		{
			if (item2 != null && (bool)item2)
			{
				hashSet.Add(item2.SteamId.Value.ToString());
			}
		}
		foreach (string item3 in hashSet)
		{
			Player playerBySteamId = MonoBehaviourSingleton<PlayerManager>.Instance?.GetPlayerBySteamId(item3);
			PlayerTeam playerTeam = ResolvePlayerTeamForBoxscore(item3, playerBySteamId);
			string teamDisplayLabel = GetTeamDisplayLabel(playerTeam);
			string text = ResolvePlayerPositionForBoxscore(item3, playerBySteamId);
			bool flag = string.Equals(text, "G", StringComparison.OrdinalIgnoreCase) || (string.IsNullOrEmpty(text) && HasTrackedGoalieStats(item3, dictionary5));
			double[] array = (_possessionTimeByZone.TryGetValue(item3, out double[] value) && value != null && value.Length >= 3) ? value : new double[3];
			double timeOnIceSecondsValue = (_timeOnIceSeconds.TryGetValue(item3, out var value3) ? value3 : 0.0);
			double[] array2 = NormalizeZoneTimeOnIceToTotal((_timeOnIceByZone.TryGetValue(item3, out double[] rawZoneTimeOnIce) && rawZoneTimeOnIce != null && rawZoneTimeOnIce.Length >= 3) ? rawZoneTimeOnIce : new double[3], timeOnIceSecondsValue);
			int num = 0;
			int num2 = 0;
			int num3 = 0;
			if (dictionary5.TryGetValue(item3, out var value4))
			{
				num = value4.ShotsFaced;
				num3 = value4.GoalsAllowed;
				num2 = Math.Max(0, num - num3);
			}
			else if (_savePerc.TryGetValue(item3, out (int, int) savePercValue))
			{
				num2 = savePercValue.Item1;
				num = savePercValue.Item2;
				num3 = num - num2;
			}
			BoxscorePlayerSnapshot item4 = new BoxscorePlayerSnapshot
			{
				SteamId = item3,
				Name = ResolvePlayerNameForBoxscore(item3, playerBySteamId),
				Team = playerTeam,
				TeamLabel = teamDisplayLabel,
				Position = text,
				IsGoalie = flag,
				Goals = (dictionary3.TryGetValue(item3, out var value5) ? value5 : 0),
				Assists = (dictionary4.TryGetValue(item3, out var value6) ? value6 : 0),
				Sog = (resolvedPlayByPlaySummary.SogBySteamId.TryGetValue(item3, out var value7) ? value7 : 0),
				IndividualCorsiFor = (_shotAttempts.TryGetValue(item3, out var value7b) ? value7b : 0),
				OnIceTeamShotAttempts = (_onIceTeamShotAttempts.TryGetValue(item3, out var value7c) ? value7c : 0),
				Blocks = (_blocks.TryGetValue(item3, out var value8) ? value8 : 0),
				Passes = (_passes.TryGetValue(item3, out var value9) ? value9 : 0),
				Exits = (_exits.TryGetValue(item3, out var value10) ? value10 : 0),
				Entries = (_entries.TryGetValue(item3, out var value11) ? value11 : 0),
				Hits = (_hits.TryGetValue(item3, out var value12) ? value12 : 0),
				HitsTaken = (_hitsTaken.TryGetValue(item3, out var value13) ? value13 : 0),
				Turnovers = (_turnovers.TryGetValue(item3, out var value14) ? value14 : 0),
				Takeaways = (_takeaways.TryGetValue(item3, out var value15) ? value15 : 0),
				PuckTouches = (_puckTouches.TryGetValue(item3, out var value16) ? value16 : 0),
				PossessionTimeSeconds = (_possessionTimeSeconds.TryGetValue(item3, out var value17) ? value17 : 0.0),
				TimeOnIceSeconds = timeOnIceSecondsValue,
				DefensiveZonePossessionTimeSeconds = array[0],
				NeutralZonePossessionTimeSeconds = array[1],
				OffensiveZonePossessionTimeSeconds = array[2],
				DefensiveZoneTimeOnIceSeconds = array2[0],
				NeutralZoneTimeOnIceSeconds = array2[1],
				OffensiveZoneTimeOnIceSeconds = array2[2],
				PostHits = (_postHits.TryGetValue(item3, out var value18) ? value18 : 0),
				FaceoffWins = (dictionary.TryGetValue(item3, out var value19) ? value19 : 0),
				FaceoffLosses = (dictionary2.TryGetValue(item3, out var value20) ? value20 : 0),
				Xg = (expectedGoalsExportData.Enabled && expectedGoalsExportData.ShooterXgBySteamId.TryGetValue(item3, out var value21) ? value21 : 0.0),
				PlusMinus = (flag ? 0 : (_plusMinus.TryGetValue(item3, out var value22) ? value22 : 0)),
				ShotsFaced = num,
				Saves = num2,
				GoalsAllowed = num3,
				SavePercentage = ((num > 0) ? ((double)num2 / (double)num) : 0.0),
				BodySaves = (_bodySaves.TryGetValue(item3, out var value23) ? value23 : 0),
				PadSaves = (_padSaves.TryGetValue(item3, out var value24) ? value24 : 0),
				StickSaves = (_stickSaves.TryGetValue(item3, out var value25) ? value25 : 0),
				Xga = (expectedGoalsExportData.Enabled && expectedGoalsExportData.GoalieXgaBySteamId.TryGetValue(item3, out var goalieXgaValue)) ? goalieXgaValue : 0.0
			};
			item4.IndividualCorsiForPercentage = CalculateIndividualCorsiForPercentage(item4.IndividualCorsiFor, item4.OnIceTeamShotAttempts);
			item4.Gsax = item4.Xga - (double)item4.GoalsAllowed;
			liveBoxscoreData.Players.Add(item4);
		}
		liveBoxscoreData.Players = liveBoxscoreData.Players.OrderBy((BoxscorePlayerSnapshot p) => GetBoxscoreTeamSortOrder(p.Team)).ThenBy((BoxscorePlayerSnapshot p) => p.IsGoalie ? 1 : 0).ThenByDescending((BoxscorePlayerSnapshot p) => p.Points).ThenByDescending((BoxscorePlayerSnapshot p) => p.Sog).ThenBy((BoxscorePlayerSnapshot p) => p.Name).ToList();
		ResolveBestAvailableFinalScore(list, liveBoxscoreData, out var num4, out var num5);
		liveBoxscoreData.RedTeam.Goals = num4;
		liveBoxscoreData.BlueTeam.Goals = num5;
		liveBoxscoreData.RedTeam.ShotsOnGoal = liveBoxscoreData.Players.Where((BoxscorePlayerSnapshot p) => p.Team == PlayerTeam.Red).Sum((BoxscorePlayerSnapshot p) => p.Sog);
		liveBoxscoreData.BlueTeam.ShotsOnGoal = liveBoxscoreData.Players.Where((BoxscorePlayerSnapshot p) => p.Team == PlayerTeam.Blue).Sum((BoxscorePlayerSnapshot p) => p.Sog);
		liveBoxscoreData.RedTeam.FaceoffWins = (_teamFaceoffWins.TryGetValue(PlayerTeam.Red, out var value28) ? value28 : 0);
		liveBoxscoreData.BlueTeam.FaceoffWins = (_teamFaceoffWins.TryGetValue(PlayerTeam.Blue, out var value29) ? value29 : 0);
		liveBoxscoreData.RedTeam.FaceoffTotal = (_teamFaceoffTotal.TryGetValue(PlayerTeam.Red, out var value30) ? value30 : 0);
		liveBoxscoreData.BlueTeam.FaceoffTotal = (_teamFaceoffTotal.TryGetValue(PlayerTeam.Blue, out var value31) ? value31 : 0);
		liveBoxscoreData.RedTeam.Passes = (_teamPasses.TryGetValue(PlayerTeam.Red, out var value32) ? value32 : 0);
		liveBoxscoreData.BlueTeam.Passes = (_teamPasses.TryGetValue(PlayerTeam.Blue, out var value33) ? value33 : 0);
		liveBoxscoreData.RedTeam.PossessionTimeSeconds = (_teamPossessionTime.TryGetValue(PlayerTeam.Red, out var value34) ? value34 : 0.0);
		liveBoxscoreData.BlueTeam.PossessionTimeSeconds = (_teamPossessionTime.TryGetValue(PlayerTeam.Blue, out var value35) ? value35 : 0.0);
		foreach (BoxscorePlayerSnapshot player in liveBoxscoreData.Players)
		{
			BoxscoreTeamSnapshot boxscoreTeamSnapshot = (player.Team == PlayerTeam.Red) ? liveBoxscoreData.RedTeam : ((player.Team == PlayerTeam.Blue) ? liveBoxscoreData.BlueTeam : null);
			if (boxscoreTeamSnapshot == null)
			{
				continue;
			}
			boxscoreTeamSnapshot.Assists += player.Assists;
			boxscoreTeamSnapshot.ExpectedGoals += player.Xg;
			boxscoreTeamSnapshot.Saves += player.Saves;
			boxscoreTeamSnapshot.Hits += player.Hits;
			boxscoreTeamSnapshot.Blocks += player.Blocks;
			boxscoreTeamSnapshot.Giveaways += player.Turnovers;
			boxscoreTeamSnapshot.Takeaways += player.Takeaways;
			boxscoreTeamSnapshot.ZoneEntries += player.Entries;
			boxscoreTeamSnapshot.ZoneExits += player.Exits;
			boxscoreTeamSnapshot.DefensiveZonePossessionTimeSeconds += player.DefensiveZonePossessionTimeSeconds;
			boxscoreTeamSnapshot.NeutralZonePossessionTimeSeconds += player.NeutralZonePossessionTimeSeconds;
			boxscoreTeamSnapshot.OffensiveZonePossessionTimeSeconds += player.OffensiveZonePossessionTimeSeconds;
		}
		liveBoxscoreData.RedTeam.ExpectedGoalsAgainst = liveBoxscoreData.BlueTeam.ExpectedGoals;
		liveBoxscoreData.BlueTeam.ExpectedGoalsAgainst = liveBoxscoreData.RedTeam.ExpectedGoals;
		StoreCachedLiveBoxscoreData(liveBoxscoreData);
		return CloneLiveBoxscoreData(liveBoxscoreData);
	}

	private static BoxscoreLeadersSnapshot BuildLiveBoxscoreLeadersSnapshot()
	{
		BoxscoreLeadersSnapshot boxscoreLeadersSnapshot = new BoxscoreLeadersSnapshot
		{
			RequestedTab = "Leaders",
			Summary = BuildBoxscoreSummary(),
			AvailableTabs = BuildAvailableTabsForCurrentGameBoxscore()
		};
		try
		{
			LiveBoxscoreData liveBoxscoreData = BuildLiveBoxscoreData();
			List<BoxscorePlayerSnapshot> list = liveBoxscoreData.Players.Where((BoxscorePlayerSnapshot p) => p.Team == PlayerTeam.Red || p.Team == PlayerTeam.Blue).ToList();
			int num = 1;
			foreach (BoxscorePlayerSnapshot item in list.OrderByDescending(CalculateCurrentStarPoints).ThenByDescending((BoxscorePlayerSnapshot p) => p.Points).ThenByDescending((BoxscorePlayerSnapshot p) => p.Sog).Take(3))
			{
				boxscoreLeadersSnapshot.Stars.Add(new BoxscoreStarSnapshot
				{
					Rank = num++,
					Name = item.Name,
					Team = GetTeamDisplayLabel(item.Team),
					Position = item.Position,
					Summary = item.IsGoalie ? $"{item.Saves} SV â€¢ {FormatSignedDecimal(item.Gsax, "0.00")} GSAx" : $"{item.Goals}G {item.Assists}A â€¢ {item.Xg:0.00} xG",
					Score = CalculateCurrentStarPoints(item)
				});
			}
			TryAddLeaderRow(boxscoreLeadersSnapshot.Leaders, "Points", list, (BoxscorePlayerSnapshot p) => p.Points, (BoxscorePlayerSnapshot p) => p.Points.ToString(CultureInfo.InvariantCulture));
			TryAddLeaderRow(boxscoreLeadersSnapshot.Leaders, "Goals", list, (BoxscorePlayerSnapshot p) => p.Goals, (BoxscorePlayerSnapshot p) => p.Goals.ToString(CultureInfo.InvariantCulture));
			TryAddLeaderRow(boxscoreLeadersSnapshot.Leaders, "Assists", list, (BoxscorePlayerSnapshot p) => p.Assists, (BoxscorePlayerSnapshot p) => p.Assists.ToString(CultureInfo.InvariantCulture));
			TryAddLeaderRow(boxscoreLeadersSnapshot.Leaders, "Shots on Goal", list, (BoxscorePlayerSnapshot p) => p.Sog, (BoxscorePlayerSnapshot p) => p.Sog.ToString(CultureInfo.InvariantCulture));
			TryAddLeaderRow(boxscoreLeadersSnapshot.Leaders, "iCF", list, (BoxscorePlayerSnapshot p) => p.IndividualCorsiFor, (BoxscorePlayerSnapshot p) => p.IndividualCorsiFor.ToString(CultureInfo.InvariantCulture), null, (BoxscorePlayerSnapshot p) => !p.IsGoalie);
			TryAddLeaderRow(boxscoreLeadersSnapshot.Leaders, "iCF%", list, (BoxscorePlayerSnapshot p) => p.IndividualCorsiForPercentage, (BoxscorePlayerSnapshot p) => FormatIndividualCorsiForPercentage(p.IndividualCorsiForPercentage), (BoxscorePlayerSnapshot p) => $"{p.IndividualCorsiFor}/{p.OnIceTeamShotAttempts}", (BoxscorePlayerSnapshot p) => !p.IsGoalie && p.OnIceTeamShotAttempts > 0);
			TryAddLeaderRow(boxscoreLeadersSnapshot.Leaders, "xGoals", list, (BoxscorePlayerSnapshot p) => p.Xg, (BoxscorePlayerSnapshot p) => p.Xg.ToString("0.00", CultureInfo.InvariantCulture));
			TryAddLeaderRow(boxscoreLeadersSnapshot.Leaders, "Saves", list, (BoxscorePlayerSnapshot p) => p.Saves, (BoxscorePlayerSnapshot p) => p.Saves.ToString(CultureInfo.InvariantCulture), null, (BoxscorePlayerSnapshot p) => p.IsGoalie);
			TryAddLeaderRow(boxscoreLeadersSnapshot.Leaders, "GSAx", list, (BoxscorePlayerSnapshot p) => p.Gsax, (BoxscorePlayerSnapshot p) => FormatSignedDecimal(p.Gsax, "0.00"), null, (BoxscorePlayerSnapshot p) => p.IsGoalie, requirePositive: false);
			TryAddLeaderRow(boxscoreLeadersSnapshot.Leaders, "Hits", list, (BoxscorePlayerSnapshot p) => p.Hits, (BoxscorePlayerSnapshot p) => p.Hits.ToString(CultureInfo.InvariantCulture));
			TryAddLeaderRow(boxscoreLeadersSnapshot.Leaders, "Blocks", list, (BoxscorePlayerSnapshot p) => p.Blocks, (BoxscorePlayerSnapshot p) => p.Blocks.ToString(CultureInfo.InvariantCulture));
			TryAddLeaderRow(boxscoreLeadersSnapshot.Leaders, "Takeaways", list, (BoxscorePlayerSnapshot p) => p.Takeaways, (BoxscorePlayerSnapshot p) => p.Takeaways.ToString(CultureInfo.InvariantCulture));
			TryAddLeaderRow(boxscoreLeadersSnapshot.Leaders, "Faceoff %", list, (BoxscorePlayerSnapshot p) => (p.FaceoffWins + p.FaceoffLosses > 0) ? ((double)p.FaceoffWins / (double)(p.FaceoffWins + p.FaceoffLosses) * 100.0) : 0.0, (BoxscorePlayerSnapshot p) => $"{((p.FaceoffWins + p.FaceoffLosses > 0) ? ((double)p.FaceoffWins / (double)(p.FaceoffWins + p.FaceoffLosses) * 100.0) : 0.0):0}%", (BoxscorePlayerSnapshot p) => $"{p.FaceoffWins}/{p.FaceoffWins + p.FaceoffLosses}", (BoxscorePlayerSnapshot p) => p.FaceoffWins + p.FaceoffLosses > 0);
			TryAddLeaderRow(boxscoreLeadersSnapshot.Leaders, "Zone Entries", list, (BoxscorePlayerSnapshot p) => p.Entries, (BoxscorePlayerSnapshot p) => p.Entries.ToString(CultureInfo.InvariantCulture));
			TryAddLeaderRow(boxscoreLeadersSnapshot.Leaders, "Zone Exits", list, (BoxscorePlayerSnapshot p) => p.Exits, (BoxscorePlayerSnapshot p) => p.Exits.ToString(CultureInfo.InvariantCulture));
		}
		catch (Exception arg)
		{
			boxscoreLeadersSnapshot.Error = "Couldn't build live leaders.";
			Logging.LogError($"Error building leaders snapshot: {arg}", ServerConfig);
		}
		return boxscoreLeadersSnapshot;
	}

	private static BoxscoreGameStatsSnapshot BuildLiveBoxscoreGameStatsSnapshot()
	{
		BoxscoreGameStatsSnapshot boxscoreGameStatsSnapshot = new BoxscoreGameStatsSnapshot
		{
			RequestedTab = "Game Stats",
			Summary = BuildBoxscoreSummary(),
			AvailableTabs = BuildAvailableTabsForCurrentGameBoxscore()
		};
		try
		{
			LiveBoxscoreData liveBoxscoreData = BuildLiveBoxscoreData();
			BoxscoreTeamSnapshot redTeam = liveBoxscoreData.RedTeam;
			BoxscoreTeamSnapshot blueTeam = liveBoxscoreData.BlueTeam;
			boxscoreGameStatsSnapshot.RedScore = redTeam.Goals;
			boxscoreGameStatsSnapshot.BlueScore = blueTeam.Goals;
			void AddRow(string label, string redValue, string blueValue, double redMetricValue, double blueMetricValue)
			{
				boxscoreGameStatsSnapshot.Rows.Add(new BoxscoreTeamStatRowSnapshot
				{
					Label = label,
					RedValue = redValue,
					BlueValue = blueValue,
					HighlightRed = redMetricValue > blueMetricValue,
					HighlightBlue = blueMetricValue > redMetricValue
				});
			}
			double num = (redTeam.FaceoffTotal > 0) ? ((double)redTeam.FaceoffWins / (double)redTeam.FaceoffTotal * 100.0) : 0.0;
			double num2 = (blueTeam.FaceoffTotal > 0) ? ((double)blueTeam.FaceoffWins / (double)blueTeam.FaceoffTotal * 100.0) : 0.0;
			AddRow("Goals", redTeam.Goals.ToString(CultureInfo.InvariantCulture), blueTeam.Goals.ToString(CultureInfo.InvariantCulture), redTeam.Goals, blueTeam.Goals);
			AddRow("Assists", redTeam.Assists.ToString(CultureInfo.InvariantCulture), blueTeam.Assists.ToString(CultureInfo.InvariantCulture), redTeam.Assists, blueTeam.Assists);
			AddRow("Shots on Goal", redTeam.ShotsOnGoal.ToString(CultureInfo.InvariantCulture), blueTeam.ShotsOnGoal.ToString(CultureInfo.InvariantCulture), redTeam.ShotsOnGoal, blueTeam.ShotsOnGoal);
			AddRow("xGoals", redTeam.ExpectedGoals.ToString("0.00", CultureInfo.InvariantCulture), blueTeam.ExpectedGoals.ToString("0.00", CultureInfo.InvariantCulture), redTeam.ExpectedGoals, blueTeam.ExpectedGoals);
			AddRow("Saves", redTeam.Saves.ToString(CultureInfo.InvariantCulture), blueTeam.Saves.ToString(CultureInfo.InvariantCulture), redTeam.Saves, blueTeam.Saves);
			AddRow("Faceoff %", FormatFaceoffPercentage(redTeam.FaceoffWins, Math.Max(0, redTeam.FaceoffTotal - redTeam.FaceoffWins)), FormatFaceoffPercentage(blueTeam.FaceoffWins, Math.Max(0, blueTeam.FaceoffTotal - blueTeam.FaceoffWins)), num, num2);
			AddRow("Hits", redTeam.Hits.ToString(CultureInfo.InvariantCulture), blueTeam.Hits.ToString(CultureInfo.InvariantCulture), redTeam.Hits, blueTeam.Hits);
			AddRow("Blocks", redTeam.Blocks.ToString(CultureInfo.InvariantCulture), blueTeam.Blocks.ToString(CultureInfo.InvariantCulture), redTeam.Blocks, blueTeam.Blocks);
			AddRow("Giveaways", redTeam.Giveaways.ToString(CultureInfo.InvariantCulture), blueTeam.Giveaways.ToString(CultureInfo.InvariantCulture), redTeam.Giveaways, blueTeam.Giveaways);
			AddRow("Takeaways", redTeam.Takeaways.ToString(CultureInfo.InvariantCulture), blueTeam.Takeaways.ToString(CultureInfo.InvariantCulture), redTeam.Takeaways, blueTeam.Takeaways);
			AddRow("ZoneEntries", redTeam.ZoneEntries.ToString(CultureInfo.InvariantCulture), blueTeam.ZoneEntries.ToString(CultureInfo.InvariantCulture), redTeam.ZoneEntries, blueTeam.ZoneEntries);
			AddRow("ZoneExits", redTeam.ZoneExits.ToString(CultureInfo.InvariantCulture), blueTeam.ZoneExits.ToString(CultureInfo.InvariantCulture), redTeam.ZoneExits, blueTeam.ZoneExits);
			AddRow("Ozone Possession", FormatTimeAsMinutesSeconds(redTeam.OffensiveZonePossessionTimeSeconds), FormatTimeAsMinutesSeconds(blueTeam.OffensiveZonePossessionTimeSeconds), redTeam.OffensiveZonePossessionTimeSeconds, blueTeam.OffensiveZonePossessionTimeSeconds);
			AddRow("Nzone Possession", FormatTimeAsMinutesSeconds(redTeam.NeutralZonePossessionTimeSeconds), FormatTimeAsMinutesSeconds(blueTeam.NeutralZonePossessionTimeSeconds), redTeam.NeutralZonePossessionTimeSeconds, blueTeam.NeutralZonePossessionTimeSeconds);
			AddRow("Dzone Possession", FormatTimeAsMinutesSeconds(redTeam.DefensiveZonePossessionTimeSeconds), FormatTimeAsMinutesSeconds(blueTeam.DefensiveZonePossessionTimeSeconds), redTeam.DefensiveZonePossessionTimeSeconds, blueTeam.DefensiveZonePossessionTimeSeconds);
		}
		catch (Exception arg)
		{
			boxscoreGameStatsSnapshot.Error = "Couldn't build live game stats.";
			Logging.LogError($"Error building game stats snapshot: {arg}", ServerConfig);
		}
		return boxscoreGameStatsSnapshot;
	}

	private static BoxscorePlayerStatsSnapshot BuildLiveBoxscorePlayerStatsSnapshot()
	{
		BoxscorePlayerStatsSnapshot boxscorePlayerStatsSnapshot = new BoxscorePlayerStatsSnapshot
		{
			RequestedTab = "Player Stats",
			Summary = BuildBoxscoreSummary(),
			AvailableTabs = BuildAvailableTabsForCurrentGameBoxscore(),
			SkaterColumns = new List<string>
			{
				"Team", "Pos", "Name", "G", "SOG", "iCF", "iCF%", "xG", "A", "SP",
				"Pass", "Exit", "Entry", "Hit", "xHit", "TO", "TKA", "Touches", "Poss", "TOI",
				"DZ Poss", "NZ Poss", "OZ Poss", "DZ TOI", "NZ TOI", "OZ TOI", "Post", "FOW", "FOL", "+/-"
			},
			GoalieColumns = new List<string>
			{
				"Team", "Pos", "Name", "SA", "SV", "BodySv", "PadSv", "StickSv", "SV%", "GA",
				"xGA", "GSAx", "Pass", "Poss", "TOI", "DZ Poss", "NZ Poss", "OZ Poss"
			}
		};
		try
		{
			LiveBoxscoreData liveBoxscoreData = BuildLiveBoxscoreData();
			foreach (BoxscorePlayerSnapshot player in liveBoxscoreData.Players.Where((BoxscorePlayerSnapshot p) => p.Team == PlayerTeam.Red || p.Team == PlayerTeam.Blue))
			{
				int[] array = AllocateZoneTimeOnIceDisplaySeconds(new double[3] { player.DefensiveZoneTimeOnIceSeconds, player.NeutralZoneTimeOnIceSeconds, player.OffensiveZoneTimeOnIceSeconds }, player.TimeOnIceSeconds);
				List<string> list = player.IsGoalie ? new List<string>
				{
					GetTeamDisplayLabel(player.Team).ToUpperInvariant(),
					player.Position,
					player.Name,
					player.ShotsFaced.ToString(CultureInfo.InvariantCulture),
					player.Saves.ToString(CultureInfo.InvariantCulture),
					player.BodySaves.ToString(CultureInfo.InvariantCulture),
					player.PadSaves.ToString(CultureInfo.InvariantCulture),
					player.StickSaves.ToString(CultureInfo.InvariantCulture),
					player.SavePercentage.ToString("0.000", CultureInfo.InvariantCulture),
					player.GoalsAllowed.ToString(CultureInfo.InvariantCulture),
					(liveBoxscoreData.ExpectedGoals.Enabled ? player.Xga.ToString("0.00", CultureInfo.InvariantCulture) : ""),
					(liveBoxscoreData.ExpectedGoals.Enabled ? FormatSignedDecimal(player.Gsax, "0.00") : ""),
					player.Passes.ToString(CultureInfo.InvariantCulture),
					FormatTimeAsMinutesSeconds(player.PossessionTimeSeconds),
					FormatTimeAsMinutesSeconds(player.TimeOnIceSeconds),
					FormatTimeAsMinutesSeconds(player.DefensiveZonePossessionTimeSeconds),
					FormatTimeAsMinutesSeconds(player.NeutralZonePossessionTimeSeconds),
					FormatTimeAsMinutesSeconds(player.OffensiveZonePossessionTimeSeconds)
				} : new List<string>
				{
					GetTeamDisplayLabel(player.Team).ToUpperInvariant(),
					player.Position,
					player.Name,
					player.Goals.ToString(CultureInfo.InvariantCulture),
					player.Sog.ToString(CultureInfo.InvariantCulture),
					player.IndividualCorsiFor.ToString(CultureInfo.InvariantCulture),
					FormatIndividualCorsiForPercentage(player.IndividualCorsiForPercentage),
					(liveBoxscoreData.ExpectedGoals.Enabled ? player.Xg.ToString("0.00", CultureInfo.InvariantCulture) : ""),
					player.Assists.ToString(CultureInfo.InvariantCulture),
					CalculateCurrentStarPoints(player).ToString("0.0", CultureInfo.InvariantCulture),
					player.Passes.ToString(CultureInfo.InvariantCulture),
					player.Exits.ToString(CultureInfo.InvariantCulture),
					player.Entries.ToString(CultureInfo.InvariantCulture),
					player.Hits.ToString(CultureInfo.InvariantCulture),
					player.HitsTaken.ToString(CultureInfo.InvariantCulture),
					player.Turnovers.ToString(CultureInfo.InvariantCulture),
					player.Takeaways.ToString(CultureInfo.InvariantCulture),
					player.PuckTouches.ToString(CultureInfo.InvariantCulture),
					FormatTimeAsMinutesSeconds(player.PossessionTimeSeconds),
					FormatTimeAsMinutesSeconds(player.TimeOnIceSeconds),
					FormatTimeAsMinutesSeconds(player.DefensiveZonePossessionTimeSeconds),
					FormatTimeAsMinutesSeconds(player.NeutralZonePossessionTimeSeconds),
					FormatTimeAsMinutesSeconds(player.OffensiveZonePossessionTimeSeconds),
					FormatTimeAsMinutesSeconds(array[0]),
					FormatTimeAsMinutesSeconds(array[1]),
					FormatTimeAsMinutesSeconds(array[2]),
					player.PostHits.ToString(CultureInfo.InvariantCulture),
					player.FaceoffWins.ToString(CultureInfo.InvariantCulture),
					player.FaceoffLosses.ToString(CultureInfo.InvariantCulture),
					(player.IsGoalie ? "" : ((player.PlusMinus > 0) ? ("+" + player.PlusMinus.ToString(CultureInfo.InvariantCulture)) : player.PlusMinus.ToString(CultureInfo.InvariantCulture)))
				};
				BoxscorePlayerStatsRowSnapshot item = new BoxscorePlayerStatsRowSnapshot
				{
					Team = GetTeamDisplayLabel(player.Team),
					IsGoalie = player.IsGoalie,
					Cells = list
				};
				if (player.IsGoalie)
				{
					boxscorePlayerStatsSnapshot.GoalieRows.Add(item);
				}
				else
				{
					boxscorePlayerStatsSnapshot.SkaterRows.Add(item);
				}
			}
		}
		catch (Exception arg)
		{
			boxscorePlayerStatsSnapshot.Error = "Couldn't build live player stats.";
			Logging.LogError($"Error building player stats snapshot: {arg}", ServerConfig);
		}
		return boxscorePlayerStatsSnapshot;
	}

	private static void ReorderScoreboardPlayers(VisualElement scoreboardContainer)
	{
		if (scoreboardContainer == null || GetUIScoreboard() == null)
		{
			return;
		}
		try
		{
			Dictionary<Player, VisualElement> privateField = GetScoreboardPlayerMap();
			if (privateField == null || privateField.Count == 0)
			{
				return;
			}
			foreach (var item in (from p in (from kvp in privateField
					select new
					{
						Player = kvp.Key,
						VisualElement = kvp.Value,
						Position = GetPlayerPosition(kvp.Key),
						Team = kvp.Key.Team,
						OrderPriority = GetPositionOrderPriority(GetPlayerPosition(kvp.Key), kvp.Key.Team)
					} into p
					orderby p.OrderPriority
					select p).ToList()
				where p.VisualElement != null && p.VisualElement.parent != null
				group p by p.VisualElement.parent).ToList())
			{
				VisualElement parent = item.Key;
				var list = item.OrderBy(p => p.OrderPriority).ToList();
				foreach (VisualElement item2 in (from p in list
					select p.VisualElement into e
					where e.parent == parent
					select e).ToList())
				{
					item2.RemoveFromHierarchy();
				}
				foreach (var item3 in list)
				{
					try
					{
						if (item3.VisualElement.parent == null)
						{
							parent.Add(item3.VisualElement);
						}
					}
					catch (Exception arg)
					{
						Logging.LogError($"Error reordering player {item3.Player.Username.Value}: {arg}", _clientConfig);
					}
				}
			}
		}
		catch (Exception arg2)
		{
			Logging.LogError($"Error reordering scoreboard players: {arg2}", _clientConfig);
		}
	}

}
