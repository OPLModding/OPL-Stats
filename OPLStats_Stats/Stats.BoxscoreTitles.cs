using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Codebase;
using Newtonsoft.Json;
using Unity.Netcode;
using UnityEngine;

namespace OPLStats_Stats;

public partial class Stats
{
	private sealed class GameCounterState
	{
		public int LastAssignedGameNumber { get; set; }
	}

	private const int BOXSCORE_REGULATION_PERIODS_FALLBACK = 3;

	private const double WARMPUP_VOTE_REASON_WINDOW_SECONDS = 8.0;

	private const double FORFEIT_REASON_WINDOW_SECONDS = 20.0;

	private static readonly Regex Boxscore360HzRegex = new Regex("\\b360\\s*hz\\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private static readonly Regex BoxscoreRepeatedWhitespaceRegex = new Regex("\\s{2,}", RegexOptions.Compiled);

	private static readonly Regex BoxscoreRepeatedPipeRegex = new Regex("\\|{2,}", RegexOptions.Compiled);

	private static readonly Regex BoxscoreTrimSeparatorRegex = new Regex("^[\\s\\|\\-]+|[\\s\\|\\-]+$", RegexOptions.Compiled);

	private static readonly Regex BoxscorePipeSpacingRegex = new Regex("\\s*\\|\\s*", RegexOptions.Compiled);

	private static string GetGameCounterStatePath()
	{
		string directoryName = Path.GetDirectoryName(OPLStats_Stats.Configs.ServerConfig.GetDefaultPath());
		if (string.IsNullOrWhiteSpace(directoryName))
		{
			directoryName = Path.Combine(Path.GetFullPath("."), "config", "doma_stats");
		}
		return Path.Combine(directoryName, "domastats_game_counter.json");
	}

	private static void LoadGameCounterState()
	{
		_currentGameNumber = 0;
		if (!ServerFunc.IsDedicatedServer())
		{
			return;
		}
		try
		{
			string gameCounterStatePath = GetGameCounterStatePath();
			if (!File.Exists(gameCounterStatePath))
			{
				return;
			}
			GameCounterState gameCounterState = JsonConvert.DeserializeObject<GameCounterState>(File.ReadAllText(gameCounterStatePath));
			_currentGameNumber = Math.Max(0, gameCounterState?.LastAssignedGameNumber ?? 0);
		}
		catch (Exception arg)
		{
			_currentGameNumber = 0;
			Logging.LogError($"Error loading game counter state: {arg}", ServerConfig);
		}
	}

	private static void SaveGameCounterState()
	{
		if (!ServerFunc.IsDedicatedServer())
		{
			return;
		}
		try
		{
			string gameCounterStatePath = GetGameCounterStatePath();
			string directoryName = Path.GetDirectoryName(gameCounterStatePath);
			if (!string.IsNullOrWhiteSpace(directoryName))
			{
				Directory.CreateDirectory(directoryName);
			}
			GameCounterState value = new GameCounterState
			{
				LastAssignedGameNumber = Math.Max(0, _currentGameNumber)
			};
			File.WriteAllText(gameCounterStatePath, JsonConvert.SerializeObject(value, Formatting.Indented));
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error saving game counter state: {arg}", ServerConfig);
		}
	}

	private static void ReconcileGameCounterWithSnapshot()
	{
		if (!ServerFunc.IsDedicatedServer() || _serverPreviousGameSnapshot == null)
		{
			return;
		}
		if (_serverPreviousGameSnapshot.GameNumber <= _currentGameNumber)
		{
			return;
		}
		_currentGameNumber = _serverPreviousGameSnapshot.GameNumber;
		SaveGameCounterState();
	}

	private static void AdvanceGameCounterForNewTrackedGame()
	{
		if (!ServerFunc.IsDedicatedServer())
		{
			return;
		}
		_currentGameNumber = Math.Max(0, _currentGameNumber) + 1;
		SaveGameCounterState();
	}

	private static string NormalizeBoxscoreServerDisplayName(string serverName)
	{
		string text = serverName ?? "";
		text = Boxscore360HzRegex.Replace(text, "");
		text = BoxscoreRepeatedPipeRegex.Replace(text, "|");
		text = BoxscorePipeSpacingRegex.Replace(text, " | ");
		text = BoxscoreRepeatedWhitespaceRegex.Replace(text, " ");
		text = BoxscoreTrimSeparatorRegex.Replace(text, "");
		text = BoxscoreRepeatedWhitespaceRegex.Replace(text, " ");
		text = text.Trim();
		return string.IsNullOrWhiteSpace(text) ? "Server" : text;
	}

	private static string GetCurrentBoxscoreServerDisplayName()
	{
		string text = "";
		try
		{
			if (NetworkBehaviourSingleton<ServerManager>.Instance != null)
			{
				text = NetworkBehaviourSingleton<ServerManager>.Instance.Server.Value.Name.ToString();
			}
		}
		catch
		{
		}
		if (string.IsNullOrWhiteSpace(text))
		{
			try
			{
				text = NetworkBehaviourSingleton<ServerManager>.Instance?.ServerConfig?.name ?? "";
			}
			catch
			{
			}
		}
		if (string.IsNullOrWhiteSpace(text) && ServerConfig != null && !string.IsNullOrWhiteSpace(ServerConfig.FileHeaderName))
		{
			text = ServerConfig.FileHeaderName;
		}
		return NormalizeBoxscoreServerDisplayName(text);
	}

	private static int GetCurrentBoxscoreGameNumber()
	{
		return Math.Max(1, _currentGameNumber);
	}

	private static int GetBoxscoreRegulationPeriodCount()
	{
		return BOXSCORE_REGULATION_PERIODS_FALLBACK;
	}

	private static string FormatBoxscoreLocalTimestamp(DateTime utcTimestamp)
	{
		return FormatBoxscoreTimestampWithConfiguredTimeZone(utcTimestamp);
	}

	private static string FormatBoxscoreLiveClock(float totalGameTimeSeconds, int currentPeriod)
	{
		if (currentPeriod <= 0)
		{
			return "0:00";
		}
		float num = totalGameTimeSeconds - (float)Math.Max(0, currentPeriod - 1) * 300f;
		if (num < 0f)
		{
			num = 0f;
		}
		return FormatTimeAsMinutesSeconds(num);
	}

	private static float GetLiveBoxscoreDisplayGameTime(GamePhase phase)
	{
		if (phase == GamePhase.FaceOff || phase == GamePhase.Play)
		{
			return Math.Max(0f, GetCurrentGameTime());
		}
		float num = 0f;
		try
		{
			if (_playByPlayEvents.Count > 0)
			{
				num = _playByPlayEvents.Max((PlayByPlayEvent e) => e.GameTime);
			}
		}
		catch
		{
		}
		if (num <= 0f)
		{
			num = Mathf.Max(0f, _lastWholeSecondGameTime);
		}
		return num;
	}

	private static string BuildShootoutBoxscoreSummary()
	{
		string currentBoxscoreServerDisplayName = GetCurrentBoxscoreServerDisplayName();
		int currentBoxscoreGameNumber = GetCurrentBoxscoreGameNumber();
		int completedOvertimePeriods = GetCompletedOvertimePeriods();
		if (completedOvertimePeriods > 0)
		{
			return $"{currentBoxscoreServerDisplayName} - Game# {currentBoxscoreGameNumber} - Shootout - After OT {completedOvertimePeriods}";
		}
		return $"{currentBoxscoreServerDisplayName} - Game# {currentBoxscoreGameNumber} - Shootout";
	}

	private static string BuildLiveBoxscoreSummary()
	{
		GameManager instance = NetworkBehaviourSingleton<GameManager>.Instance;
		GamePhase phase = instance?.Phase ?? GamePhase.None;
		if (phase == GamePhase.Warmup)
		{
			return $"{GetCurrentBoxscoreServerDisplayName()} - Warmup - No previous game";
		}
		if (_isShootoutActive)
		{
			return BuildShootoutBoxscoreSummary();
		}
		string currentBoxscoreServerDisplayName = GetCurrentBoxscoreServerDisplayName();
		int currentBoxscoreGameNumber = GetCurrentBoxscoreGameNumber();
		if (phase == GamePhase.GameOver || phase == GamePhase.PostGame)
		{
			return $"{currentBoxscoreServerDisplayName} - Game# {currentBoxscoreGameNumber} - Complete - {FormatBoxscoreTimestampWithConfiguredTimeZone(DateTime.UtcNow)}";
		}
		int currentPeriod = Math.Max(1, GetCurrentPeriod());
		bool flag = false;
		try
		{
			flag = instance != null && instance.GameState != null && instance.GameState.Value.IsOvertime;
		}
		catch
		{
		}
		string text = flag ? $"OT {Math.Max(1, currentPeriod - GetBoxscoreRegulationPeriodCount())}" : $"Period {currentPeriod}";
		string text2 = ((phase == GamePhase.PreGame) ? "0:00" : FormatBoxscoreLiveClock(GetLiveBoxscoreDisplayGameTime(phase), currentPeriod));
		return $"{currentBoxscoreServerDisplayName} - Game# {currentBoxscoreGameNumber} - {text} - {text2}";
	}

	private static string BuildPreviousGameSummary(LiveBoxscoreData liveBoxscoreData, string captureReason, int gameNumber, string serverDisplayName, DateTime capturedAtUtc)
	{
		string text;
		switch (NormalizePreviousGameCaptureReason(captureReason))
		{
		case "forfeit":
			text = "Complete (Forfeit)";
			break;
		case "admin":
			text = "Interrupted (Admin)";
			break;
		case "warmup":
			text = "Interrupted (Warmup)";
			break;
		default:
			text = "Complete";
			break;
		}
		string text2 = string.IsNullOrWhiteSpace(serverDisplayName) ? GetCurrentBoxscoreServerDisplayName() : NormalizeBoxscoreServerDisplayName(serverDisplayName);
		return $"{text2} - Game# {Math.Max(1, gameNumber)} - {text} - {FormatBoxscoreLocalTimestamp(capturedAtUtc)}";
	}

	private static string ResolveWarmupSnapshotCaptureReason()
	{
		if ((DateTime.UtcNow - _lastWarmupVoteSuccessAtUtc).TotalSeconds <= WARMPUP_VOTE_REASON_WINDOW_SECONDS)
		{
			_lastWarmupVoteSuccessAtUtc = DateTime.MinValue;
			return "warmup";
		}
		return "admin";
	}

	private static string ResolveGameOverSnapshotCaptureReason()
	{
		if ((DateTime.UtcNow - _lastForfeitCommandAtUtc).TotalSeconds <= FORFEIT_REASON_WINDOW_SECONDS)
		{
			_lastForfeitCommandAtUtc = DateTime.MinValue;
			return "forfeit";
		}
		return "gameover";
	}

	public static void Event_OnServerChatCommand(Dictionary<string, object> message)
	{
		try
		{
			if (!ServerFunc.IsDedicatedServer() || message == null || !message.TryGetValue("command", out var value) || !(value is string text) || string.IsNullOrWhiteSpace(text))
			{
				return;
			}
			GamePhase? nullable = NetworkBehaviourSingleton<GameManager>.Instance?.Phase;
			if (!nullable.HasValue || (nullable != GamePhase.Play && nullable != GamePhase.FaceOff && nullable != GamePhase.Intermission && nullable != GamePhase.Replay))
			{
				return;
			}
			string text2 = text.Trim().ToLowerInvariant();
			if (text2 == "/ff" || text2 == "/forfeit")
			{
				_lastForfeitCommandAtUtc = DateTime.UtcNow;
			}
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error in Event_OnServerChatCommand.\n{arg}", ServerConfig);
		}
	}

	public static void Event_OnVoteSuccess(Dictionary<string, object> message)
	{
		try
		{
			if (!ServerFunc.IsDedicatedServer() || message == null || !message.TryGetValue("vote", out var value) || value == null)
			{
				return;
			}
			object value2 = value.GetType().GetProperty("Type")?.GetValue(value, null);
			if (string.Equals(value2?.ToString(), "Warmup", StringComparison.OrdinalIgnoreCase))
			{
				_lastWarmupVoteSuccessAtUtc = DateTime.UtcNow;
			}
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error in Event_OnVoteSuccess.\n{arg}", ServerConfig);
		}
	}
}
