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
	private static string CreateGameReferenceId(DateTime utcTimestamp)
	{
		return ConvertUtcToConfiguredTimeZone(utcTimestamp, GetConfiguredExportTimeZone(), out _).ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
	}

	private static string GetCurrentGameReferenceIdOrCreate()
	{
		if (string.IsNullOrWhiteSpace(_currentGameReferenceId))
		{
			_currentGameReferenceId = CreateGameReferenceId(DateTime.UtcNow);
		}
		return _currentGameReferenceId;
	}

	private static bool IsReservedWindowsFileName(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return false;
		}
		switch (value.Trim().TrimEnd('.').ToUpperInvariant())
		{
		case "CON":
		case "PRN":
		case "AUX":
		case "NUL":
		case "COM1":
		case "COM2":
		case "COM3":
		case "COM4":
		case "COM5":
		case "COM6":
		case "COM7":
		case "COM8":
		case "COM9":
		case "LPT1":
		case "LPT2":
		case "LPT3":
		case "LPT4":
		case "LPT5":
		case "LPT6":
		case "LPT7":
		case "LPT8":
		case "LPT9":
			return true;
		default:
			return false;
		}
	}

	private static string GetSanitizedExportFileHeaderName()
	{
		string text = StripHtmlTags(ServerConfig?.FileHeaderName);
		if (string.IsNullOrWhiteSpace(text))
		{
			return "puck";
		}
		StringBuilder stringBuilder = new StringBuilder(text.Trim());
		char[] invalidFileNameChars = Path.GetInvalidFileNameChars();
		for (int i = 0; i < stringBuilder.Length; i++)
		{
			if (invalidFileNameChars.Contains(stringBuilder[i]))
			{
				stringBuilder[i] = '_';
			}
		}
		text = stringBuilder.ToString().Trim().TrimEnd('.');
		if (string.IsNullOrWhiteSpace(text))
		{
			return "puck";
		}
		return IsReservedWindowsFileName(text) ? ("_" + text) : text;
	}

	private static void ExportPlayByPlayCSVInternal(FrozenGameExportSnapshot snapshot)
	{
		if (snapshot == null || snapshot.PlayByPlayEvents == null || snapshot.PlayByPlayEvents.Count == 0)
		{
			Logging.Log("No play-by-play events to export", ServerConfig);
			return;
		}
		List<PlayByPlayEvent> list = snapshot.PlayByPlayEvents;
		try
		{
			string text = Path.Combine(Path.GetFullPath("."), "stats");

			string text2 = string.IsNullOrWhiteSpace(snapshot.GameReferenceId) ? GetCurrentGameReferenceIdOrCreate() : snapshot.GameReferenceId;
			string text3 = GetSanitizedExportFileHeaderName();
			string text4 = Path.Combine(text, text3 + "_" + text2 + "_playbyplay.csv");
            _fileWork.Enqueue(() => {
                Directory.CreateDirectory(text);
			StringBuilder stringBuilder = new StringBuilder();
			stringBuilder.AppendLine("sep=,");
			stringBuilder.AppendLine("gameReferenceId,id,period,gameTime,team,teamInPossession,currentPlayInPossession,scoreState,name,zone,outcome,flags,xCoord,yCoord,zCoord,playerJersey,playerPosition,playerName,forcemagnitude,PlayerSpeed,PuckVelocity,playerReferenceSteamID,teamForwardsSteamID,teamDefencemenSteamID,teamGoalieSteamID,opposingTeamForwardsSteamID,opposingTeamDefencemenSteamID,opposingTeamGoalieSteamID,xg");
			int num2 = 0;
			foreach (PlayByPlayEvent playByPlayEvent2 in list)
			{
				stringBuilder.AppendLine(string.Join(",", text2, num2++, playByPlayEvent2.Period, playByPlayEvent2.GameTime.ToString("F3"), CsvEscape(playByPlayEvent2.Team), CsvEscape(playByPlayEvent2.TeamInPossession), CsvEscape(playByPlayEvent2.CurrentPlayInPossession), CsvEscape(playByPlayEvent2.ScoreState), GetEventName(playByPlayEvent2.EventType), GetEventZoneString(playByPlayEvent2.Zone), CsvEscape(playByPlayEvent2.Outcome), CsvEscape(playByPlayEvent2.Flags), playByPlayEvent2.Position.x.ToString("F1"), playByPlayEvent2.Position.y.ToString("F1"), playByPlayEvent2.Position.z.ToString("F1"), playByPlayEvent2.PlayerJersey.ToString(), CsvEscape(playByPlayEvent2.PlayerPosition), CsvEscape(playByPlayEvent2.PlayerName), playByPlayEvent2.ForceMagnitude.ToString("F2"), (playByPlayEvent2.PlayerSpeed * 3.6f).ToString("F1"), playByPlayEvent2.Velocity.magnitude.ToString("F2"), CsvEscape(playByPlayEvent2.PlayerSteamId), playByPlayEvent2.TeamForwardsSteamID, playByPlayEvent2.TeamDefencemenSteamID, playByPlayEvent2.TeamGoalieSteamID, playByPlayEvent2.OpposingTeamForwardsSteamID, playByPlayEvent2.OpposingTeamDefencemenSteamID, playByPlayEvent2.OpposingTeamGoalieSteamID, playByPlayEvent2.Xg.ToString("0.####", CultureInfo.InvariantCulture)));
			}
			File.WriteAllText(text4, stringBuilder.ToString(), Encoding.UTF8);
            }, error => {
                if (error != null) Logging.LogError($"Can't write the play-by-play data in the stats folder. (Permission error ?)\n{error}", ServerConfig);
                else Logging.Log($"Play-by-play data exported to {text4} with {snapshot.PlayByPlayEvents.Count} events", ServerConfig);
            });
		}
		catch (Exception arg)
		{
			Logging.LogError($"Can't write the play-by-play data in the stats folder. (Permission error ?)\n{arg}", ServerConfig);
		}
	}

	private static void ExportGameStats(bool forceExport = false, bool bypassExportLimit = false)
	{
		if (_playByPlayEvents.Count == 0)
		{
			Logging.Log("No play-by-play events to export", ServerConfig);
			return;
		}
		TryExportLiveGameStats(force: true);
		AuditGoalTrackingAgainstOfficialStats(applyCorrections: false);
		int num5 = 0;
		int count = _playByPlayEvents.Count;
		try
		{
			num5 = (from e in _playByPlayEvents
				where !string.IsNullOrEmpty(e.PlayerSteamId)
				select e.PlayerSteamId).Distinct().Count();
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error counting unique players for export: {arg}", ServerConfig);
		}
		bool flag = bypassExportLimit || !ServerConfig.EnableExportLimit || (num5 >= 8 && count >= 300);
		if (bypassExportLimit)
		{
			Logging.Log($"Exporting with export-limit bypass: {num5} unique players, {count} events", ServerConfig);
		}
		else if (forceExport)
		{
			if (flag)
			{
				if (!ServerConfig.EnableExportLimit)
				{
					Logging.Log("Export requested via /endgame - Export limit disabled, exporting regardless of criteria", ServerConfig);
				}
				else
				{
					Logging.Log($"Export requested via /endgame - Criteria met: {num5} unique players (8+), {count} events (300+)", ServerConfig);
				}
			}
			else
			{
				Logging.Log($"Export requested via /endgame - Criteria NOT met: {num5} unique players (need 8+), {count} events (need 300+). Export skipped.", ServerConfig);
			}
		}
		else if (!flag && ServerConfig.EnableExportLimit)
		{
			Logging.Log($"Export skipped (JSON and CSV): {num5} unique players (need 8+), {count} events (need 300+)", ServerConfig);
		}
		if (!flag)
		{
			return;
		}
		FrozenGameExportSnapshot frozenGameExportSnapshot = GetOrCreateFrozenGameExportSnapshot();
        Lazy<string> json = CaptureExportJson(frozenGameExportSnapshot);
        string directory = Path.Combine(Path.GetFullPath("."), "stats");
        string reference = string.IsNullOrWhiteSpace(frozenGameExportSnapshot.GameReferenceId) ? GetCurrentGameReferenceIdOrCreate() : frozenGameExportSnapshot.GameReferenceId;
        string stem = Path.Combine(directory, GetSanitizedExportFileHeaderName() + "_" + reference);
        bool diagnostics = TrackingDiagnosticsEnabled();
        _fileWork.Enqueue(() => {
            Directory.CreateDirectory(directory);
            WriteAllTextAtomically(stem + "_stats.json", json.Value);
        }, error => {
            if (error != null) Logging.LogError($"Can't write the end of game stats in the stats folder. (Permission error ?)\n{error}", ServerConfig);
            else
            {
                if (diagnostics) Logging.Log("Stats:" + json.Value, ServerConfig, bypassConfig: true);
                Logging.Log($"JSON exported: {num5} unique players, {count} events", ServerConfig);
                TryQueueRemoteGameUpload(json.Value, reference, stem);
            }
        });
        ExportPlayByPlayCSVInternal(frozenGameExportSnapshot);
		ExportNetworkPerformanceReport(final: true);
	}

	private static Dictionary<string, object> BuildGameStatsExportPayload(FrozenGameExportSnapshot snapshot)
	{
		LiveBoxscoreData liveBoxscoreData = snapshot?.LiveData ?? new LiveBoxscoreData();
		List<Dictionary<string, object>> list = BuildExportPlayerPayloads(snapshot);
		BoxscoreTeamSnapshot redTeam = liveBoxscoreData.RedTeam ?? new BoxscoreTeamSnapshot
		{
			Team = PlayerTeam.Red,
			Label = "Red"
		};
		BoxscoreTeamSnapshot blueTeam = liveBoxscoreData.BlueTeam ?? new BoxscoreTeamSnapshot
		{
			Team = PlayerTeam.Blue,
			Label = "Blue"
		};
		float num = snapshot?.PlayByPlayEvents?.FirstOrDefault((PlayByPlayEvent e) => e.EventType == PlayByPlayEventType.GameEnd)?.GameTime ?? (snapshot?.PlayByPlayEvents?.Count > 0 ? snapshot.PlayByPlayEvents.Max((PlayByPlayEvent e) => e.GameTime) : 0f);
		Dictionary<string, object> payload = new Dictionary<string, object>
		{
			{ "players", list },
			{
				"expectedGoalsModel",
				new Dictionary<string, object>
				{
					{ "enabled", snapshot?.ExpectedGoals?.Enabled ?? false },
					{ "modelVersion", snapshot?.ExpectedGoals?.ModelVersion ?? "" },
					{ "modelType", snapshot?.ExpectedGoals?.ModelType ?? "" },
					{ "trainedThrough", snapshot?.ExpectedGoals?.TrainedThrough ?? "" },
					{ "shotsEvaluated", snapshot?.ExpectedGoals?.ShotsEvaluated ?? 0 },
					{ "modelFile", ExpectedGoalsModelConfig.GetDefaultPath() }
				}
			},
			{
				"teamStats",
				new Dictionary<string, object>
				{
					{ "blueTeamSogs", redTeam.ShotsOnGoal },
					{ "blueTeamSogsAg", blueTeam.ShotsOnGoal },
					{ "blueTeamPasses", redTeam.Passes },
					{ "blueTeamPassesAg", blueTeam.Passes },
					{ "blueFaceoffsWon", redTeam.FaceoffWins },
					{ "blueFaceoffsLost", Math.Max(0, redTeam.FaceoffTotal - redTeam.FaceoffWins) },
					{ "blueTakeaways", redTeam.Takeaways },
					{ "blueTurnovers", redTeam.Giveaways },
					{ "blueTeamBlocks", blueTeam.Blocks },
					{ "blueTeamBlocksAg", redTeam.Blocks },
					{ "blueDZExits", redTeam.ZoneExits },
					{ "blueDZExitsAllowed", blueTeam.ZoneExits },
					{ "blueOZEntries", redTeam.ZoneEntries },
					{ "blueOZEntriesAllowed", blueTeam.ZoneEntries },
					{ "blueTeamPossessionTime", redTeam.PossessionTimeSeconds },
					{ "blueTeamPossessionTimeAg", blueTeam.PossessionTimeSeconds },
					{ "redTeamSogs", blueTeam.ShotsOnGoal },
					{ "redTeamSogsAg", redTeam.ShotsOnGoal },
					{ "redTeamPasses", blueTeam.Passes },
					{ "redTeamPassesAg", redTeam.Passes },
					{ "redFaceoffsWon", blueTeam.FaceoffWins },
					{ "redFaceoffsLost", Math.Max(0, blueTeam.FaceoffTotal - blueTeam.FaceoffWins) },
					{ "redTakeaways", blueTeam.Takeaways },
					{ "redTurnovers", blueTeam.Giveaways },
					{ "redTeamBlocks", redTeam.Blocks },
					{ "redTeamBlocksAg", blueTeam.Blocks },
					{ "redDZExits", blueTeam.ZoneExits },
					{ "redDZExitsAllowed", redTeam.ZoneExits },
					{ "redOZEntries", blueTeam.ZoneEntries },
					{ "redOZEntriesAllowed", redTeam.ZoneEntries },
					{ "redTeamPossessionTime", blueTeam.PossessionTimeSeconds },
					{ "redTeamPossessionTimeAg", redTeam.PossessionTimeSeconds },
					{
						"goals",
						(snapshot?.Goals ?? new List<GoalInfo>()).OrderBy((GoalInfo g) => g.Period).ThenBy((GoalInfo g) => g.GameTime).Select(delegate(GoalInfo g)
						{
							return new
							{
								gameTime = g.GameTime,
								period = g.Period,
								team = g.Team,
								scorer = g.Scorer,
								primaryAssist = g.PrimaryAssist,
								secondaryAssist = g.SecondaryAssist,
								gwg = g.GWG
							};
						}).ToList()
					},
					{
						"shootoutAttempts",
						(snapshot?.ShootoutAttempts ?? new List<BoxscoreShootoutAttemptRecord>()).OrderBy((BoxscoreShootoutAttemptRecord attempt) => (attempt.AttemptNumber > 0) ? attempt.AttemptNumber : int.MaxValue).Select(delegate(BoxscoreShootoutAttemptRecord attempt)
						{
							return new
							{
								attemptNumber = attempt.AttemptNumber,
								team = (attempt.Team == PlayerTeam.None) ? "" : attempt.Team.ToString(),
								shooter = attempt.ShooterSteamId ?? "",
								goalie = attempt.GoalieSteamId ?? "",
								scored = attempt.Scored,
								gwg = attempt.GameWinner
							};
						}).ToList()
					},
					{ "gwg", snapshot?.WinningGoalScorerSteamId ?? "" },
					{ "stars", snapshot?.Stars != null ? new Dictionary<int, string>(snapshot.Stars) : new Dictionary<int, string>() },
					{ "gameTimeMinutes", (double)num / 60.0 }
				}
			}
		};
		Dictionary<string, object> networkPerformance = BuildBundledNetworkPerformancePayload();
		if (networkPerformance != null)
		{
			payload["networkPerformance"] = networkPerformance;
		}
		return payload;
	}

	private static PlayerTeam ResolveWinningTeamForSnapshot(FrozenGameExportSnapshot snapshot)
	{
		if (snapshot == null)
		{
			return PlayerTeam.None;
		}
		if (snapshot.WinningTeam == PlayerTeam.Red || snapshot.WinningTeam == PlayerTeam.Blue)
		{
			return snapshot.WinningTeam;
		}
		if (snapshot.BlueScore > snapshot.RedScore)
		{
			return PlayerTeam.Blue;
		}
		if (snapshot.RedScore > snapshot.BlueScore)
		{
			return PlayerTeam.Red;
		}
		return PlayerTeam.None;
	}

	private static void AddGoalieResultExportFields(Dictionary<string, object> dictionary, FrozenGameExportSnapshot snapshot, BoxscorePlayerSnapshot player)
	{
		if (dictionary == null || player == null || !player.IsGoalie)
		{
			return;
		}
		int wins = 0;
		int losses = 0;
		int otLosses = 0;
		int shutouts = 0;
		PlayerTeam winningTeam = ResolveWinningTeamForSnapshot(snapshot);
		if ((player.Team == PlayerTeam.Red || player.Team == PlayerTeam.Blue) && (winningTeam == PlayerTeam.Red || winningTeam == PlayerTeam.Blue))
		{
			if (player.Team == winningTeam)
			{
				wins = 1;
				if (player.GoalsAllowed == 0)
				{
					shutouts = 1;
				}
			}
			else if (snapshot != null && snapshot.WasOvertime)
			{
				otLosses = 1;
			}
			else
			{
				losses = 1;
			}
		}
		dictionary["wins"] = wins;
		dictionary["losses"] = losses;
		dictionary["otLosses"] = otLosses;
		dictionary["shutouts"] = shutouts;
	}

	private static List<Dictionary<string, object>> BuildExportPlayerPayloads(FrozenGameExportSnapshot snapshot)
	{
		List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
		if (snapshot?.LiveData?.Players == null)
		{
			return list;
		}
		bool flag = snapshot.ExpectedGoals != null && snapshot.ExpectedGoals.Enabled;
		foreach (BoxscorePlayerSnapshot item in ExpandDualRoleExportPlayers(snapshot))
		{
			if (item == null)
			{
				continue;
			}
			Dictionary<string, object> dictionary = new Dictionary<string, object>
			{
				{ "steamId", item.SteamId ?? "" },
				{ "name", item.Name ?? "" },
				{ "team", item.TeamLabel ?? "spectator" },
				{ "position", item.Position ?? "" },
				{ "goals", item.Goals },
				{ "assists", item.Assists },
				{ "sog", item.Sog },
				{ "iCF", item.IndividualCorsiFor },
				{ "onIceTeamShotAttempts", item.OnIceTeamShotAttempts },
				{ "iCFPct", Math.Round(item.IndividualCorsiForPercentage, 1) },
				{ "passes", item.Passes },
				{ "exits", item.Exits },
				{ "entries", item.Entries },
				{ "blocks", item.Blocks },
				{ "hits", item.Hits },
				{ "xHits", item.HitsTaken },
				{ "turnovers", item.Turnovers },
				{ "takeaways", item.Takeaways },
				{ "puckTouches", item.PuckTouches },
				{ "possessionTimeSeconds", Math.Round(item.PossessionTimeSeconds, 1) },
				{ "timeOnIce", Math.Round(item.TimeOnIceSeconds, 1) },
				{ "D-ZonePossession", Math.Round(item.DefensiveZonePossessionTimeSeconds, 1) },
				{ "N-ZonePossession", Math.Round(item.NeutralZonePossessionTimeSeconds, 1) },
				{ "O-ZonePossession", Math.Round(item.OffensiveZonePossessionTimeSeconds, 1) },
				{ "D-ZoneTOI", Math.Round(item.DefensiveZoneTimeOnIceSeconds, 1) },
				{ "N-ZoneTOI", Math.Round(item.NeutralZoneTimeOnIceSeconds, 1) },
				{ "O-ZoneTOI", Math.Round(item.OffensiveZoneTimeOnIceSeconds, 1) },
				{ "postHits", item.PostHits },
				{ "faceoffWins", item.FaceoffWins },
				{ "faceoffLosses", item.FaceoffLosses },
				{ "influence", Math.Round(item.Influence, 1) }
			};
			if (flag)
			{
				dictionary["xg"] = Math.Round(item.Xg, 4);
			}
			if (!item.IsGoalie)
			{
				dictionary["plusMinus"] = item.PlusMinus;
			}
			if (item.IsGoalie || item.ShotsFaced > 0 || item.Saves > 0 || item.GoalsAllowed > 0)
			{
				dictionary["shotsFaced"] = item.ShotsFaced;
				dictionary["saves"] = item.Saves;
				dictionary["goalsAllowed"] = item.GoalsAllowed;
				dictionary["saveperc"] = item.SavePercentage;
				dictionary["bodySaves"] = item.BodySaves;
				dictionary["padSaves"] = item.PadSaves;
				dictionary["stickSaves"] = item.StickSaves;
				if (flag)
				{
					dictionary["xga"] = Math.Round(item.Xga, 4);
					dictionary["gsax"] = Math.Round(item.Gsax, 4);
				}
				AddGoalieResultExportFields(dictionary, snapshot, item);
			}
			list.Add(dictionary);
		}
		return list;
	}

	private static string CsvEscape(string value)
	{
		if (string.IsNullOrEmpty(value))
		{
			return "";
		}
		if (value.Length >= 15 && value.All(char.IsDigit))
		{
			return "=\"" + value + "\"";
		}
		if (value.Contains(",") || value.Contains("\"") || value.Contains("\n"))
		{
			return "\"" + value.Replace("\"", "\"\"") + "\"";
		}
		return value;
	}

	private static string GetEventName(PlayByPlayEventType eventType)
	{
		return eventType switch
		{
			PlayByPlayEventType.Touch => "touch",
			PlayByPlayEventType.Takeaway => "takeaway",
			PlayByPlayEventType.Turnover => "turnover",
			PlayByPlayEventType.Pass => "pass",
			PlayByPlayEventType.Shot => "shot",
			PlayByPlayEventType.Save => "save",
			PlayByPlayEventType.Goal => "goal",
			PlayByPlayEventType.OwnGoal => "owngoal",
			PlayByPlayEventType.Hit => "hit",
			PlayByPlayEventType.Block => "block",
			PlayByPlayEventType.PuckBattle => "puckbattle",
			PlayByPlayEventType.Faceoff => "faceoff",
			PlayByPlayEventType.FaceoffOutcome => "faceoffoutcome",
			PlayByPlayEventType.DZExit => "dzexit",
			PlayByPlayEventType.OZEntry => "ozentry",
			PlayByPlayEventType.PostHit => "posthit",
			PlayByPlayEventType.GameEnd => "gameend",
			_ => "touch",
		};
	}

	private static string GetEventZoneString(EventZone zone)
	{
		return zone switch
		{
			EventZone.Neutral => "nz",
			EventZone.Defensive => "dz",
			EventZone.Offensive => "oz",
			_ => "nz",
		};
	}

	private static int _lastProcessedZoneFlagIndex = -1;

	private static void AddZoneFlagsToPlayByPlayEvents()
	{
		try
		{
			if (_playByPlayEvents == null || _playByPlayEvents.Count < 2)
			{
				return;
			}
			int num = 1;
			if (_lastProcessedZoneFlagIndex >= 0 && _lastProcessedZoneFlagIndex < _playByPlayEvents.Count && _playByPlayEvents[_lastProcessedZoneFlagIndex].EventId == _lastProcessedZoneFlagEventId)
            {
                num = _lastProcessedZoneFlagIndex + 1;
            }
            else if (_lastProcessedZoneFlagEventId >= 0)
			{
				for (int i = 0; i < _playByPlayEvents.Count; i++)
				{
					if (_playByPlayEvents[i].EventId == _lastProcessedZoneFlagEventId)
					{
						num = i + 1;
						break;
					}
				}
			}
			if (num >= _playByPlayEvents.Count)
			{
				return;
			}
			HashSet<int> hashSet = new HashSet<int>();
			for (int j = num; j < _playByPlayEvents.Count; j++)
			{
				PlayByPlayEvent playByPlayEvent = _playByPlayEvents[j];
				if (playByPlayEvent.EventType == PlayByPlayEventType.Hit || playByPlayEvent.EventType == PlayByPlayEventType.PostHit)
				{
					continue;
				}
				PlayByPlayEvent playByPlayEvent2 = null;
				for (int num2 = j - 1; num2 >= 0; num2--)
				{
					PlayByPlayEvent playByPlayEvent3 = _playByPlayEvents[num2];
					if (playByPlayEvent3.EventType != PlayByPlayEventType.Hit && playByPlayEvent3.EventType != PlayByPlayEventType.PostHit && playByPlayEvent3.EventType != PlayByPlayEventType.Faceoff && (playByPlayEvent3.EventType != PlayByPlayEventType.Touch || !(playByPlayEvent3.Outcome == "failed") || playByPlayEvent3.PlayerTeam == playByPlayEvent.PlayerTeam))
					{
						if (playByPlayEvent3.PlayerTeam == playByPlayEvent.PlayerTeam)
						{
							playByPlayEvent2 = playByPlayEvent3;
							break;
						}
						if (playByPlayEvent3.PlayerTeam != playByPlayEvent.PlayerTeam)
						{
							break;
						}
					}
				}
				if (playByPlayEvent2 == null)
				{
					continue;
				}
				EventZone zone = playByPlayEvent2.Zone;
				EventZone zone2 = playByPlayEvent.Zone;
				string text = "";
				if (zone == EventZone.Defensive && zone2 == EventZone.Neutral)
				{
					text = "DZExit";
				}
				else if (zone == EventZone.Neutral && zone2 == EventZone.Offensive)
				{
					text = "OZEntry";
				}
				else if (zone == EventZone.Defensive && zone2 == EventZone.Offensive)
				{
					text = "DZExit,OZEntry";
				}
				if (string.IsNullOrEmpty(text) || (playByPlayEvent2.PlayerSteamId != playByPlayEvent.PlayerSteamId && playByPlayEvent2.EventType != PlayByPlayEventType.Pass))
				{
					continue;
				}
				bool flag = false;
				if (playByPlayEvent.PlayerTeam != 0)
				{
					for (int num3 = j - 1; num3 >= 0; num3--)
					{
						PlayByPlayEvent playByPlayEvent4 = _playByPlayEvents[num3];
						if (playByPlayEvent4.PlayerTeam == playByPlayEvent.PlayerTeam && !string.IsNullOrEmpty(playByPlayEvent4.Flags))
						{
							if (text.Contains("DZExit") && playByPlayEvent4.Flags.Contains("DZExit"))
							{
								bool flag2 = false;
								for (int k = num3 + 1; k < j; k++)
								{
									PlayByPlayEvent playByPlayEvent5 = _playByPlayEvents[k];
									if (playByPlayEvent5.PlayerTeam != playByPlayEvent.PlayerTeam && playByPlayEvent5.EventType != PlayByPlayEventType.Hit)
									{
										flag2 = true;
										break;
									}
								}
								if (!flag2)
								{
									flag = true;
									break;
								}
							}
							if (text.Contains("OZEntry") && playByPlayEvent4.Flags.Contains("OZEntry"))
							{
								bool flag3 = false;
								for (int l = num3 + 1; l < j; l++)
								{
									PlayByPlayEvent playByPlayEvent6 = _playByPlayEvents[l];
									if (playByPlayEvent6.PlayerTeam != playByPlayEvent.PlayerTeam && playByPlayEvent6.EventType != PlayByPlayEventType.Hit)
									{
										flag3 = true;
										break;
									}
								}
								if (!flag3)
								{
									flag = true;
									break;
								}
							}
						}
					}
				}
				if (flag)
				{
					continue;
				}
				PlayByPlayEvent playByPlayEvent7 = null;
				bool flag4 = false;
				if (playByPlayEvent2.EventType == PlayByPlayEventType.Pass)
				{
					playByPlayEvent7 = playByPlayEvent2;
					if (string.IsNullOrEmpty(playByPlayEvent2.Flags) || !playByPlayEvent2.Flags.Contains(text.Split(',')[0]))
					{
						flag4 = true;
					}
				}
				else if (playByPlayEvent.EventType == PlayByPlayEventType.Touch)
				{
					playByPlayEvent7 = playByPlayEvent;
					if (string.IsNullOrEmpty(playByPlayEvent.Flags) || !playByPlayEvent.Flags.Contains(text.Split(',')[0]))
					{
						flag4 = true;
					}
				}
				if (!(playByPlayEvent7 != null && flag4) || hashSet.Contains(playByPlayEvent7.EventId))
				{
					continue;
				}
				if (string.IsNullOrEmpty(playByPlayEvent7.Flags))
				{
					playByPlayEvent7.Flags = text;
				}
				else
				{
					PlayByPlayEvent playByPlayEvent8 = playByPlayEvent7;
					playByPlayEvent8.Flags = playByPlayEvent8.Flags + "," + text;
				}
				hashSet.Add(playByPlayEvent7.EventId);
				if (TrackingDiagnosticsEnabled())
				{
					LogTrackingDiagnostic("zone-flags-" + playByPlayEvent7.EventId, $"Annotated event {playByPlayEvent7.EventId} for {playByPlayEvent7.PlayerSteamId} with {text}. from={zone} to={zone2}, sourceEvent={playByPlayEvent.EventType}, priorEvent={playByPlayEvent2.EventType}.", bypassThrottle: true);
				}
				if (string.IsNullOrEmpty(playByPlayEvent7.PlayerSteamId))
				{
					continue;
				}
				int value;
				if (text.Contains("DZExit"))
				{
					if (!_exits.TryGetValue(playByPlayEvent7.PlayerSteamId, out value))
					{
						_exits.Add(playByPlayEvent7.PlayerSteamId, 0);
					}
					_exits[playByPlayEvent7.PlayerSteamId]++;
					string dataName = "oomtm450_statsEXIT" + playByPlayEvent7.PlayerSteamId;
					value = _exits[playByPlayEvent7.PlayerSteamId];
					QueueStatUpdate(dataName, value.ToString());
					Player playerBySteamId = MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayerBySteamId(playByPlayEvent7.PlayerSteamId);
					if (playerBySteamId != null && (bool)playerBySteamId)
					{
						PlayerTeam value2 = playerBySteamId.Team;
						if (!_teamExits.TryGetValue(value2, out value))
						{
							_teamExits.Add(value2, 0);
						}
						_teamExits[value2]++;
						string dataName2 = "oomtm450_statsTEAM_EXITS" + value2;
						value = _teamExits[value2];
						QueueStatUpdate(dataName2, value.ToString());
					}
				}
				if (!text.Contains("OZEntry"))
				{
					continue;
				}
				if (!_entries.TryGetValue(playByPlayEvent7.PlayerSteamId, out value))
				{
					_entries.Add(playByPlayEvent7.PlayerSteamId, 0);
				}
				_entries[playByPlayEvent7.PlayerSteamId]++;
				string dataName3 = "oomtm450_statsENTRY" + playByPlayEvent7.PlayerSteamId;
				value = _entries[playByPlayEvent7.PlayerSteamId];
				QueueStatUpdate(dataName3, value.ToString());
				Player playerBySteamId2 = MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayerBySteamId(playByPlayEvent7.PlayerSteamId);
				if (playerBySteamId2 != null && (bool)playerBySteamId2)
				{
					PlayerTeam value3 = playerBySteamId2.Team;
					if (!_teamEntries.TryGetValue(value3, out value))
					{
						_teamEntries.Add(value3, 0);
					}
					_teamEntries[value3]++;
					string dataName4 = "oomtm450_statsTEAM_ENTRIES" + value3;
					value = _teamEntries[value3];
					QueueStatUpdate(dataName4, value.ToString());
				}
			}
			if (_playByPlayEvents.Count > 0)
			{
				_lastProcessedZoneFlagIndex = _playByPlayEvents.Count - 1;
                _lastProcessedZoneFlagEventId = _playByPlayEvents[_lastProcessedZoneFlagIndex].EventId;
			}
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error in AddZoneFlagsToPlayByPlayEvents: {arg}", ServerConfig);
		}
	}

}
