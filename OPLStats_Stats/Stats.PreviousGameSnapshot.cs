using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Codebase;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.UIElements;

namespace OPLStats_Stats;

public partial class Stats
{
	internal sealed class PreviousGameSnapshot
	{
		public string GameReferenceId { get; set; } = "";

		public string CaptureReason { get; set; } = "";

		public string Summary { get; set; } = "";

		public int GameNumber { get; set; }

		public string ServerDisplayName { get; set; } = "";

		public DateTime CapturedAtUtc { get; set; } = DateTime.UtcNow;

		public List<string> ParticipantSteamIds { get; set; } = new List<string>();

		public LiveBoxscoreData LiveData { get; set; } = new LiveBoxscoreData();

		public Dictionary<string, double> StarPointsBySteamId { get; set; } = new Dictionary<string, double>();

		public BoxscoreLeadersSnapshot Leaders { get; set; } = new BoxscoreLeadersSnapshot();

		public BoxscoreGameStatsSnapshot GameStats { get; set; } = new BoxscoreGameStatsSnapshot();

		public BoxscorePlayerStatsSnapshot PlayerStats { get; set; } = new BoxscorePlayerStatsSnapshot();

		public BoxscoreShootoutSnapshot Shootout { get; set; } = null;
	}

	private static string GetPreviousGameSnapshotTempPath()
	{
		string directoryName = Path.GetDirectoryName(OPLStats_Stats.Configs.ServerConfig.GetDefaultPath());
		if (string.IsNullOrWhiteSpace(directoryName))
		{
			directoryName = Path.Combine(Path.GetFullPath("."), "config", "doma_stats");
		}
		return Path.Combine(directoryName, "domastats_previous_game.json");
	}

	private static List<string> GetLegacyPreviousGameSnapshotTempPaths()
	{
		string path = Path.Combine(Path.GetFullPath("."), "stats");
		if (!Directory.Exists(path))
		{
			return new List<string>();
		}
		try
		{
			return Directory.GetFiles(path, "domastats_previous_game*.json").ToList();
		}
		catch
		{
			return new List<string>();
		}
	}

	private static void DeleteLegacyPreviousGameSnapshotTempFiles()
	{
		foreach (string legacyPreviousGameSnapshotTempPath in GetLegacyPreviousGameSnapshotTempPaths())
		{
			try
			{
				if (File.Exists(legacyPreviousGameSnapshotTempPath))
				{
					File.Delete(legacyPreviousGameSnapshotTempPath);
				}
			}
			catch (Exception arg)
			{
				Logging.LogError($"Error deleting legacy previous-game snapshot temp file {legacyPreviousGameSnapshotTempPath}: {arg}", ServerConfig);
			}
		}
	}

	private static bool ShouldServePreviousGameSnapshotFromServer()
	{
		if (!ServerFunc.IsDedicatedServer() || _serverPreviousGameSnapshot == null)
		{
			return false;
		}
		return NetworkBehaviourSingleton<GameManager>.Instance != null && NetworkBehaviourSingleton<GameManager>.Instance.Phase == GamePhase.Warmup;
	}

	private static bool ShouldUseClientPreviousGameSnapshot()
	{
		if (ServerFunc.IsDedicatedServer() || _clientPreviousGameSnapshot == null)
		{
			return false;
		}
		return NetworkBehaviourSingleton<GameManager>.Instance != null && NetworkBehaviourSingleton<GameManager>.Instance.Phase == GamePhase.Warmup;
	}

	private static bool IsClientPreviousGameParticipant(string playerSteamId)
	{
		if (!ShouldUseClientPreviousGameSnapshot() || string.IsNullOrWhiteSpace(playerSteamId) || _clientPreviousGameSnapshot?.ParticipantSteamIds == null)
		{
			return false;
		}
		return _clientPreviousGameSnapshot.ParticipantSteamIds.Contains(playerSteamId);
	}

	private static bool TryGetClientPreviousGamePlayerSnapshot(string playerSteamId, out BoxscorePlayerSnapshot playerSnapshot)
	{
		playerSnapshot = null;
		if (!ShouldUseClientPreviousGameSnapshot() || string.IsNullOrWhiteSpace(playerSteamId) || _clientPreviousGameSnapshot?.LiveData?.Players == null)
		{
			return false;
		}
		foreach (BoxscorePlayerSnapshot player in _clientPreviousGameSnapshot.LiveData.Players)
		{
			if (player != null && string.Equals(player.SteamId, playerSteamId, StringComparison.Ordinal))
			{
				playerSnapshot = player;
				return true;
			}
		}
		return false;
	}

	private static bool TryGetClientPreviousGameTeamSnapshot(PlayerTeam team, out BoxscoreTeamSnapshot teamSnapshot)
	{
		teamSnapshot = null;
		if (!ShouldUseClientPreviousGameSnapshot() || _clientPreviousGameSnapshot?.LiveData == null)
		{
			return false;
		}
		switch (team)
		{
		case PlayerTeam.Red:
			teamSnapshot = _clientPreviousGameSnapshot.LiveData.RedTeam;
			break;
		case PlayerTeam.Blue:
			teamSnapshot = _clientPreviousGameSnapshot.LiveData.BlueTeam;
			break;
		}
		return teamSnapshot != null;
	}

	private static string GetPlayerTooltipDisplayName(string playerSteamId, Player player)
	{
		if (IsClientPreviousGameParticipant(playerSteamId) && TryGetClientPreviousGamePlayerSnapshot(playerSteamId, out var playerSnapshot) && !string.IsNullOrWhiteSpace(playerSnapshot.Name))
		{
			return playerSnapshot.Name;
		}
		if (player != null && (bool)player)
		{
			return player.Username.Value.ToString();
		}
		return playerSteamId;
	}

	private static bool UseGoalieLayoutForTooltip(string playerSteamId, Player player)
	{
		if (IsClientPreviousGameParticipant(playerSteamId) && TryGetClientPreviousGamePlayerSnapshot(playerSteamId, out var playerSnapshot))
		{
			return playerSnapshot.IsGoalie;
		}
		if (player == null || !player)
		{
			return false;
		}
		return PlayerFunc.IsGoalie(player) || GetPlayerPosition(player) == "G";
	}

	private static double GetClientPreviousGameStarPoints(string playerSteamId)
	{
		if (!ShouldUseClientPreviousGameSnapshot() || string.IsNullOrWhiteSpace(playerSteamId) || _clientPreviousGameSnapshot?.StarPointsBySteamId == null)
		{
			return 0.0;
		}
		return _clientPreviousGameSnapshot.StarPointsBySteamId.TryGetValue(playerSteamId, out var value) ? value : 0.0;
	}

	private static bool TryGetClientPreviousGameScoringSummary(string playerSteamId, out int goals, out int assists, out int points)
	{
		goals = 0;
		assists = 0;
		points = 0;
		if (!IsClientPreviousGameParticipant(playerSteamId) || !TryGetClientPreviousGamePlayerSnapshot(playerSteamId, out var playerSnapshot))
		{
			return false;
		}
		goals = playerSnapshot.Goals;
		assists = playerSnapshot.Assists;
		points = playerSnapshot.Points;
		return true;
	}

	private static void SetTooltipLabelText(VisualElement tooltip, string labelName, string playerSteamId, string text)
	{
		if (tooltip == null || string.IsNullOrWhiteSpace(labelName) || string.IsNullOrWhiteSpace(playerSteamId))
		{
			return;
		}
		Label label = GetPlayerTooltipLabel(tooltip, labelName, playerSteamId);
		if (label != null)
		{
			label.text = text;
		}
	}

	private static bool TryUpdatePlayerTooltipFromPreviousGameSnapshot(VisualElement tooltip, string playerSteamId)
	{
		if (!IsClientPreviousGameParticipant(playerSteamId) || !TryGetClientPreviousGamePlayerSnapshot(playerSteamId, out var playerSnapshot))
		{
			return false;
		}
		SetTooltipLabelText(tooltip, "TooltipTitle", playerSteamId, string.IsNullOrWhiteSpace(playerSnapshot.Name) ? playerSteamId : playerSnapshot.Name);
		if (playerSnapshot.IsGoalie)
		{
			int num = playerSnapshot.Saves;
			int num2 = playerSnapshot.ShotsFaced;
			double num3 = (num2 > 0) ? ((double)num / (double)num2 * 100.0) : 0.0;
			SetTooltipLabelText(tooltip, "TooltipSVPercent", playerSteamId, $"SV%: {num}/{num2} ({num3:F1}%)");
			SetTooltipLabelText(tooltip, "TooltipExpectedGoalsAgainst", playerSteamId, $"xGA: {playerSnapshot.Xga:F2}");
			SetTooltipLabelText(tooltip, "TooltipGoalsSavedAboveExpected", playerSteamId, "GSAx: " + FormatSignedDecimal(playerSnapshot.Gsax, "0.00"));
			SetTooltipLabelText(tooltip, "TooltipStickSaves", playerSteamId, $"Stick Saves: {playerSnapshot.StickSaves}");
			SetTooltipLabelText(tooltip, "TooltipBodySaves", playerSteamId, $"Body Saves: {playerSnapshot.BodySaves}");
			SetTooltipLabelText(tooltip, "TooltipPadSaves", playerSteamId, $"Pad Saves: {playerSnapshot.PadSaves}");
			SetTooltipLabelText(tooltip, "TooltipPossessionTime", playerSteamId, "Poss Time: " + FormatTimeAsMinutesSeconds(playerSnapshot.PossessionTimeSeconds));
			SetTooltipLabelText(tooltip, "TooltipPasses", playerSteamId, $"Passes: {playerSnapshot.Passes}");
		}
		else
		{
			SetTooltipLabelText(tooltip, "TooltipICF", playerSteamId, $"iCF: {playerSnapshot.IndividualCorsiFor}");
			SetTooltipLabelText(tooltip, "TooltipICFPercent", playerSteamId, $"iCF%: {FormatIndividualCorsiForPercentage(playerSnapshot.IndividualCorsiForPercentage)} ({playerSnapshot.IndividualCorsiFor}/{playerSnapshot.OnIceTeamShotAttempts})");
			SetTooltipLabelText(tooltip, "TooltipExpectedGoals", playerSteamId, $"xG: {playerSnapshot.Xg:F2}");
			SetTooltipLabelText(tooltip, "TooltipPasses", playerSteamId, $"Passes: {playerSnapshot.Passes}");
			SetTooltipLabelText(tooltip, "TooltipExitsEntries", playerSteamId, $"Exits & Entries: {playerSnapshot.Exits} | {playerSnapshot.Entries}");
			SetTooltipLabelText(tooltip, "TooltipPossessionTime", playerSteamId, "Possession: " + FormatTimeAsMinutesSeconds(playerSnapshot.PossessionTimeSeconds));
			SetTooltipLabelText(tooltip, "TooltipTakeaways", playerSteamId, $"Takeaways: {playerSnapshot.Takeaways}");
			SetTooltipLabelText(tooltip, "TooltipTurnovers", playerSteamId, $"Turnovers: {playerSnapshot.Turnovers}");
			SetTooltipLabelText(tooltip, "TooltipBlocks", playerSteamId, $"Blocks: {playerSnapshot.Blocks}");
			SetTooltipLabelText(tooltip, "TooltipHits", playerSteamId, $"Hits: {playerSnapshot.Hits}");
		}
		SetTooltipLabelText(tooltip, "TooltipStarPoints", playerSteamId, $"Star Points: {GetClientPreviousGameStarPoints(playerSteamId):F1}");
		return true;
	}

	private static bool TryUpdateTeamTooltipFromPreviousGameSnapshot(VisualElement tooltip, PlayerTeam team)
	{
		if (!TryGetClientPreviousGameTeamSnapshot(team, out var teamSnapshot))
		{
			return false;
		}
		PlayerTeam playerTeam = (team == PlayerTeam.Red) ? PlayerTeam.Blue : PlayerTeam.Red;
		if (!TryGetClientPreviousGameTeamSnapshot(playerTeam, out var teamSnapshot2))
		{
			return false;
		}
		string text = team.ToString();
		SetTooltipLabelText(tooltip, "TooltipTeamSOGs", text, $"SOGs: {teamSnapshot.ShotsOnGoal}-{teamSnapshot2.ShotsOnGoal}");
		SetTooltipLabelText(tooltip, "TooltipTeamFaceoffPct", text, $"Faceoffs: {FormatSnapshotFaceoffPercentage(teamSnapshot.FaceoffWins, teamSnapshot.FaceoffTotal)}");
		SetTooltipLabelText(tooltip, "TooltipTeamPasses", text, $"Passes: {teamSnapshot.Passes}-{teamSnapshot2.Passes}");
		SetTooltipLabelText(tooltip, "TooltipTeamTakeaways", text, $"Takeaways: {teamSnapshot.Takeaways}-{teamSnapshot2.Takeaways}");
		SetTooltipLabelText(tooltip, "TooltipTeamDZExits", text, $"DZ Exits: {teamSnapshot.ZoneExits}-{teamSnapshot2.ZoneExits}");
		SetTooltipLabelText(tooltip, "TooltipTeamOZEntries", text, $"OZ Entries: {teamSnapshot.ZoneEntries}-{teamSnapshot2.ZoneEntries}");
		double num = teamSnapshot.PossessionTimeSeconds;
		double num2 = teamSnapshot2.PossessionTimeSeconds;
		double num3 = num + num2;
		double num4 = (num3 > 0.0) ? (num / num3 * 100.0) : 0.0;
		SetTooltipLabelText(tooltip, "TooltipTeamPossession", text, $"Possession: {num4:F0}% ({FormatTimeAsMinutesSeconds(num)} - {FormatTimeAsMinutesSeconds(num2)})");
		return true;
	}

	private static bool TrySendPreviousGameBoxscoreSnapshotToClient(ulong clientId, string requestedTab)
	{
		if (!ShouldServePreviousGameSnapshotFromServer() || _serverPreviousGameSnapshot == null)
		{
			return false;
		}
		string text = NormalizeBoxscoreTab(requestedTab);
		switch (text)
		{
		case "Leaders":
			NetworkCommunication.SendData(PREVIOUS_GAME_BOXSCORE_LEADERS, SerializePreviousSnapshot(_serverPreviousGameSnapshot.Leaders), clientId, "oomtm450_stats_server", ServerConfig);
			return true;
		case "Game Stats":
			NetworkCommunication.SendData(PREVIOUS_GAME_BOXSCORE_GAMESTATS, SerializePreviousSnapshot(_serverPreviousGameSnapshot.GameStats), clientId, "oomtm450_stats_server", ServerConfig);
			return true;
		case "Player Stats":
			NetworkCommunication.SendData(PREVIOUS_GAME_BOXSCORE_PLAYERSTATS, SerializePreviousSnapshot(_serverPreviousGameSnapshot.PlayerStats), clientId, "oomtm450_stats_server", ServerConfig);
			return true;
		case "Shootout":
			if (_serverPreviousGameSnapshot.Shootout == null)
			{
				return false;
			}
			NetworkCommunication.SendData(PREVIOUS_GAME_BOXSCORE_SHOOTOUT, SerializePreviousSnapshot(_serverPreviousGameSnapshot.Shootout), clientId, "oomtm450_stats_server", ServerConfig);
			return true;
		default:
			return false;
		}
	}

	private static void SendPreviousGameSnapshotToClient(ulong clientId)
	{
		if (clientId == 0L || !ShouldServePreviousGameSnapshotFromServer() || _serverPreviousGameSnapshot == null)
		{
			return;
		}
		try
		{
			NetworkCommunication.SendData(PREVIOUS_GAME_SNAPSHOT, SerializePreviousSnapshot(_serverPreviousGameSnapshot), clientId, "oomtm450_stats_server", ServerConfig);
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error sending previous-game snapshot to client {clientId}: {arg}", ServerConfig);
		}
	}

	private static void BroadcastPreviousGameSnapshotToClients()
	{
		if (!ServerFunc.IsDedicatedServer() || _serverPreviousGameSnapshot == null)
		{
			return;
		}
		SafeSendDataToAll(PREVIOUS_GAME_SNAPSHOT, SerializePreviousSnapshot(_serverPreviousGameSnapshot));
	}

	private static void ReceivePreviousGameSnapshot(string payload)
	{
		if (string.IsNullOrWhiteSpace(payload))
		{
			ClearClientPreviousGameSnapshot(refreshVisibleBoxscore: true);
			return;
		}
		try
		{
			_clientPreviousGameSnapshot = JsonConvert.DeserializeObject<PreviousGameSnapshot>(payload);
			Logging.Log($"Received previous-game snapshot for {_clientPreviousGameSnapshot?.GameReferenceId ?? "<unknown>"}", _clientConfig);
			SetClientBoxscoreAvailableTabs(BuildAvailableTabsForPreviousGameSnapshot(_clientPreviousGameSnapshot), refreshVisiblePanel: _boxscoreVisible);
			if (_boxscoreVisible && ShouldUseClientPreviousGameSnapshot())
			{
				RequestBoxscoreSnapshot(_boxscoreActiveTab);
			}
			if (IsClientStatsUiAuthorized())
			{
				ApplyScoreboardEnabledState(enable: true, force: true);
			}
			RefreshVisibleTeamTooltips();
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error reading previous-game snapshot on client: {arg}", _clientConfig);
		}
	}

	private static void ClearClientPreviousGameSnapshot(bool refreshVisibleBoxscore)
	{
		_clientPreviousGameSnapshot = null;
		SetClientBoxscoreAvailableTabs(CreateBaseBoxscoreTabList(), refreshVisiblePanel: refreshVisibleBoxscore && _boxscoreVisible);
		if (refreshVisibleBoxscore && _boxscoreVisible)
		{
			if (_boxscoreSummaryLabel != null)
			{
				_boxscoreSummaryLabel.text = BuildBoxscoreSummary();
			}
			RequestBoxscoreSnapshot(_boxscoreActiveTab);
		}
		if (IsClientStatsUiAuthorized())
		{
			ApplyScoreboardEnabledState(enable: true, force: true);
		}
		RefreshVisibleTeamTooltips();
	}

	private static void LoadServerPreviousGameSnapshotFromTempFile()
	{
		if (!ServerFunc.IsDedicatedServer())
		{
			return;
		}
		string previousGameSnapshotTempPath = GetPreviousGameSnapshotTempPath();
		PreviousGameSnapshot previousGameSnapshot = null;
		if (File.Exists(previousGameSnapshotTempPath))
		{
			try
			{
				previousGameSnapshot = JsonConvert.DeserializeObject<PreviousGameSnapshot>(File.ReadAllText(previousGameSnapshotTempPath));
				if (previousGameSnapshot == null || previousGameSnapshot.LiveData == null)
				{
					previousGameSnapshot = null;
					File.Delete(previousGameSnapshotTempPath);
				}
			}
			catch (Exception arg)
			{
				Logging.LogError($"Error loading previous-game snapshot from {previousGameSnapshotTempPath}: {arg}", ServerConfig);
				try
				{
					File.Delete(previousGameSnapshotTempPath);
				}
				catch
				{
				}
			}
		}
		if (previousGameSnapshot == null)
		{
			foreach (string item in GetLegacyPreviousGameSnapshotTempPaths().OrderByDescending((string path) => File.GetLastWriteTimeUtc(path)))
			{
				try
				{
					previousGameSnapshot = JsonConvert.DeserializeObject<PreviousGameSnapshot>(File.ReadAllText(item));
					if (previousGameSnapshot != null && previousGameSnapshot.LiveData != null)
					{
						Logging.Log($"Loaded previous-game snapshot from legacy path {item}", ServerConfig);
						break;
					}
					previousGameSnapshot = null;
				}
				catch (Exception arg2)
				{
					Logging.LogError($"Error loading previous-game snapshot from legacy path {item}: {arg2}", ServerConfig);
				}
			}
		}
		if (previousGameSnapshot == null)
		{
			DeleteLegacyPreviousGameSnapshotTempFiles();
			return;
		}
		UpgradeLoadedPreviousGameSnapshot(previousGameSnapshot);
		_serverPreviousGameSnapshot = previousGameSnapshot;
		Logging.Log($"Loaded previous-game snapshot from {previousGameSnapshotTempPath}", ServerConfig);
		SaveServerPreviousGameSnapshotToTempFile();
	}

	private static void UpgradeLoadedPreviousGameSnapshot(PreviousGameSnapshot previousGameSnapshot)
	{
		if (previousGameSnapshot == null)
		{
			return;
		}
		if (previousGameSnapshot.GameNumber <= 0)
		{
			previousGameSnapshot.GameNumber = GetCurrentBoxscoreGameNumber();
		}
		if (string.IsNullOrWhiteSpace(previousGameSnapshot.ServerDisplayName))
		{
			previousGameSnapshot.ServerDisplayName = GetCurrentBoxscoreServerDisplayName();
		}
		if ((string.IsNullOrWhiteSpace(previousGameSnapshot.Summary) || previousGameSnapshot.Summary.StartsWith("Previous Game", StringComparison.OrdinalIgnoreCase)) && previousGameSnapshot.LiveData != null)
		{
			previousGameSnapshot.Summary = BuildPreviousGameSummary(previousGameSnapshot.LiveData, previousGameSnapshot.CaptureReason, previousGameSnapshot.GameNumber, previousGameSnapshot.ServerDisplayName, previousGameSnapshot.CapturedAtUtc);
		}
		if (previousGameSnapshot.Leaders != null)
		{
			previousGameSnapshot.Leaders.Summary = previousGameSnapshot.Summary;
		}
		if (previousGameSnapshot.GameStats != null)
		{
			previousGameSnapshot.GameStats.Summary = previousGameSnapshot.Summary;
			previousGameSnapshot.GameStats.AvailableTabs = BuildAvailableTabsForPreviousGameSnapshot(previousGameSnapshot);
		}
		if (previousGameSnapshot.PlayerStats != null)
		{
			previousGameSnapshot.PlayerStats.Summary = previousGameSnapshot.Summary;
			previousGameSnapshot.PlayerStats.AvailableTabs = BuildAvailableTabsForPreviousGameSnapshot(previousGameSnapshot);
		}
		if (previousGameSnapshot.Leaders != null)
		{
			previousGameSnapshot.Leaders.AvailableTabs = BuildAvailableTabsForPreviousGameSnapshot(previousGameSnapshot);
		}
		if (previousGameSnapshot.Shootout != null)
		{
			previousGameSnapshot.Shootout.RequestedTab = BOXSCORE_TAB_SHOOTOUT;
			previousGameSnapshot.Shootout.Summary = previousGameSnapshot.Summary;
			previousGameSnapshot.Shootout.AvailableTabs = BuildAvailableTabsForPreviousGameSnapshot(previousGameSnapshot);
		}
	}

	private static void SaveServerPreviousGameSnapshotToTempFile()
	{
		if (!ServerFunc.IsDedicatedServer() || _serverPreviousGameSnapshot == null)
		{
			return;
		}
		try
		{
			string previousGameSnapshotTempPath = GetPreviousGameSnapshotTempPath();
			string directoryName = Path.GetDirectoryName(previousGameSnapshotTempPath);
			if (!string.IsNullOrEmpty(directoryName) && !Directory.Exists(directoryName))
			{
				Directory.CreateDirectory(directoryName);
			}
			File.WriteAllText(previousGameSnapshotTempPath, JsonConvert.SerializeObject(_serverPreviousGameSnapshot, Formatting.Indented));
			DeleteLegacyPreviousGameSnapshotTempFiles();
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error writing previous-game snapshot temp file: {arg}", ServerConfig);
		}
	}

	private static void DeleteServerPreviousGameSnapshotTempFile()
	{
		try
		{
			string previousGameSnapshotTempPath = GetPreviousGameSnapshotTempPath();
			if (File.Exists(previousGameSnapshotTempPath))
			{
				File.Delete(previousGameSnapshotTempPath);
			}
			DeleteLegacyPreviousGameSnapshotTempFiles();
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error deleting previous-game snapshot temp file: {arg}", ServerConfig);
		}
	}

	private static void ClearServerPreviousGameSnapshot(bool broadcastToClients)
	{
		_serverPreviousGameSnapshot = null;
		DeleteServerPreviousGameSnapshotTempFile();
		if (broadcastToClients && ServerFunc.IsDedicatedServer())
		{
			SafeSendDataToAll(PREVIOUS_GAME_CLEAR, "1");
		}
	}

	private static bool HasPreviousGameSnapshotCandidate()
	{
		if (_playByPlayEvents.Count > 0 || _goals.Count > 0)
		{
			return true;
		}
		try
		{
			if (NetworkBehaviourSingleton<GameManager>.Instance != null && NetworkBehaviourSingleton<GameManager>.Instance.GameState != null)
			{
				return NetworkBehaviourSingleton<GameManager>.Instance.GameState.Value.RedScore > 0 || NetworkBehaviourSingleton<GameManager>.Instance.GameState.Value.BlueScore > 0;
			}
		}
		catch
		{
		}
		return false;
	}

	private static bool CapturePreviousGameSnapshotIfAvailable(string captureReason)
	{
		if (!ServerFunc.IsDedicatedServer() || !HasPreviousGameSnapshotCandidate())
		{
			return false;
		}
		string text = string.IsNullOrWhiteSpace(_currentGameReferenceId) ? CreateGameReferenceId(DateTime.UtcNow) : _currentGameReferenceId;
		if (_serverPreviousGameSnapshot != null && string.Equals(_serverPreviousGameSnapshot.GameReferenceId, text, StringComparison.Ordinal))
		{
			return false;
		}
		try
		{
			PreviousGameSnapshot previousGameSnapshot = BuildCurrentPreviousGameSnapshot(captureReason);
			if (previousGameSnapshot == null || previousGameSnapshot.ParticipantSteamIds.Count == 0)
			{
				return false;
			}
			_serverPreviousGameSnapshot = previousGameSnapshot;
			SaveServerPreviousGameSnapshotToTempFile();
			BroadcastPreviousGameSnapshotToClients();
			Logging.Log($"Captured previous-game snapshot for {previousGameSnapshot.GameReferenceId} ({previousGameSnapshot.CaptureReason}) with {previousGameSnapshot.ParticipantSteamIds.Count} participants.", ServerConfig);
			return true;
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error capturing previous-game snapshot: {arg}", ServerConfig);
			return false;
		}
	}

	private static PreviousGameSnapshot BuildCurrentPreviousGameSnapshot(string captureReason)
	{
		FrozenGameExportSnapshot currentFrozenGameExportSnapshot = GetCurrentFrozenGameExportSnapshot();
		LiveBoxscoreData liveBoxscoreData = currentFrozenGameExportSnapshot?.LiveData != null ? CloneLiveBoxscoreData(currentFrozenGameExportSnapshot.LiveData) : (_currentGameShootoutBoxscoreState?.FrozenLiveData ?? BuildLiveBoxscoreData());
		List<BoxscorePlayerSnapshot> list = liveBoxscoreData.Players.Where((BoxscorePlayerSnapshot p) => p != null && (p.Team == PlayerTeam.Red || p.Team == PlayerTeam.Blue)).ToList();
		if (list.Count == 0)
		{
			return null;
		}
		string winningGoalScorerSteamId = currentFrozenGameExportSnapshot?.WinningGoalScorerSteamId ?? "";
		PlayerTeam winningTeam = currentFrozenGameExportSnapshot?.WinningTeam ?? PlayerTeam.None;
		if (winningTeam == PlayerTeam.None)
		{
			(winningGoalScorerSteamId, winningTeam) = GetPreviousGameWinningGoalScorer(liveBoxscoreData.RedTeam.Goals, liveBoxscoreData.BlueTeam.Goals);
		}
		Dictionary<string, double> previousGameStarPoints = BuildPreviousGameStarPoints(list, winningTeam, winningGoalScorerSteamId);
		DateTime utcNow = DateTime.UtcNow;
		int currentBoxscoreGameNumber = GetCurrentBoxscoreGameNumber();
		string currentBoxscoreServerDisplayName = GetCurrentBoxscoreServerDisplayName();
		string text = BuildPreviousGameSummary(liveBoxscoreData, captureReason, currentBoxscoreGameNumber, currentBoxscoreServerDisplayName, utcNow);
		string text2 = NormalizePreviousGameCaptureReason(captureReason);
		List<string> list2 = BuildAvailableTabsForCurrentGameBoxscore();
		BoxscoreLeadersSnapshot leaders = (_currentGameShootoutBoxscoreState?.FrozenLeaders != null) ? CloneLeadersSnapshotWithSummary(_currentGameShootoutBoxscoreState.FrozenLeaders, text, list2) : BuildPreviousGameLeadersSnapshot(liveBoxscoreData, text, previousGameStarPoints);
		BoxscoreGameStatsSnapshot gameStats = (_currentGameShootoutBoxscoreState?.FrozenGameStats != null) ? CloneGameStatsSnapshotWithSummary(_currentGameShootoutBoxscoreState.FrozenGameStats, text, list2) : BuildPreviousGameGameStatsSnapshot(liveBoxscoreData, text);
		BoxscorePlayerStatsSnapshot playerStats = (_currentGameShootoutBoxscoreState?.FrozenPlayerStats != null) ? ClonePlayerStatsSnapshotWithSummary(_currentGameShootoutBoxscoreState.FrozenPlayerStats, text, list2) : BuildPreviousGamePlayerStatsSnapshot(liveBoxscoreData, text, previousGameStarPoints);
		BoxscoreShootoutSnapshot shootout = null;
		if (_currentGameShootoutBoxscoreState?.HasShootoutOccurred == true)
		{
			shootout = CloneShootoutSnapshotWithSummary(BuildCurrentGameShootoutSnapshot(text, list2), text, list2);
		}
		return new PreviousGameSnapshot
		{
			GameReferenceId = string.IsNullOrWhiteSpace(_currentGameReferenceId) ? CreateGameReferenceId(utcNow) : _currentGameReferenceId,
			CaptureReason = text2,
			Summary = text,
			GameNumber = currentBoxscoreGameNumber,
			ServerDisplayName = currentBoxscoreServerDisplayName,
			CapturedAtUtc = utcNow,
			ParticipantSteamIds = list.Select((BoxscorePlayerSnapshot p) => p.SteamId).Where((string steamId) => !string.IsNullOrWhiteSpace(steamId)).Distinct().OrderBy((string steamId) => steamId, StringComparer.Ordinal).ToList(),
			LiveData = liveBoxscoreData,
			StarPointsBySteamId = previousGameStarPoints,
			Leaders = leaders,
			GameStats = gameStats,
			PlayerStats = playerStats,
			Shootout = shootout
		};
	}

	private static string NormalizePreviousGameCaptureReason(string captureReason)
	{
		if (string.IsNullOrWhiteSpace(captureReason))
		{
			return "game-end";
		}
		switch (captureReason.Trim().ToLowerInvariant())
		{
		case "warmup":
		case "warmup-transition":
		case "warmup-vote":
			return "warmup";
		case "admin":
		case "admin-warmup":
			return "admin";
		case "ff":
		case "forfeit":
			return "forfeit";
		default:
			return "game-end";
		}
	}

	private static string BuildPreviousGameSummary(LiveBoxscoreData liveBoxscoreData, string captureReason)
	{
		if (_logic || !_logic)
		{
			return BuildPreviousGameSummary(liveBoxscoreData, captureReason, GetCurrentBoxscoreGameNumber(), GetCurrentBoxscoreServerDisplayName(), DateTime.UtcNow);
		}
		string text;
		switch (NormalizePreviousGameCaptureReason(captureReason))
		{
		case "warmup":
		case "warmup-transition":
			text = "Ended Early";
			break;
		default:
			text = "Final";
			break;
		}
		return $"Previous Game â€¢ {text} â€¢ Red {liveBoxscoreData.RedTeam.Goals} - Blue {liveBoxscoreData.BlueTeam.Goals}";
	}

	private static (string WinningGoalScorerSteamId, PlayerTeam WinningTeam) GetPreviousGameWinningGoalScorer(int redScore, int blueScore)
	{
		string text = "";
		PlayerTeam playerTeam = PlayerTeam.None;
		try
		{
			if (blueScore > redScore)
			{
				playerTeam = PlayerTeam.Blue;
				int num = 0;
				int num2 = 0;
				foreach (GoalInfo item in _goals.OrderBy((GoalInfo g) => g.GameTime))
				{
					if (item.Team == "Blue")
					{
						num++;
					}
					else
					{
						num2++;
					}
					if (item.Team == "Blue" && num > num2 && num == redScore + 1)
					{
						text = item.Scorer;
						break;
					}
				}
			}
			else if (redScore > blueScore)
			{
				playerTeam = PlayerTeam.Red;
				int num3 = 0;
				int num4 = 0;
				foreach (GoalInfo item2 in _goals.OrderBy((GoalInfo g) => g.GameTime))
				{
					if (item2.Team == "Blue")
					{
						num3++;
					}
					else
					{
						num4++;
					}
					if (item2.Team == "Red" && num4 > num3 && num4 == blueScore + 1)
					{
						text = item2.Scorer;
						break;
					}
				}
			}
		}
		catch
		{
		}
		return (text, playerTeam);
	}

	private static Dictionary<string, double> BuildPreviousGameStarPoints(IEnumerable<BoxscorePlayerSnapshot> players, PlayerTeam winningTeam, string winningGoalScorerSteamId)
	{
		Dictionary<string, double> dictionary = new Dictionary<string, double>();
		foreach (BoxscorePlayerSnapshot player in players)
		{
			if (player != null && !string.IsNullOrWhiteSpace(player.SteamId))
			{
				dictionary[player.SteamId] = CalculatePreviousGameStarPoints(player, winningTeam, winningGoalScorerSteamId);
			}
		}
		return dictionary;
	}

	private static double CalculatePreviousGameStarPoints(BoxscorePlayerSnapshot snapshot, PlayerTeam winningTeam, string winningGoalScorerSteamId)
	{
		if (snapshot == null || snapshot.Team != PlayerTeam.Red && snapshot.Team != PlayerTeam.Blue)
		{
			return 0.0;
		}
		double num = (winningGoalScorerSteamId == snapshot.SteamId) ? 0.5 : 0.0;
		double num2 = (winningTeam == snapshot.Team) ? 1.1 : 1.0;
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
		num3 += snapshot.Exits;
		num3 += snapshot.Entries;
		return num3 * num2;
	}

	private static string BuildPreviousGameStarSummary(BoxscorePlayerSnapshot player)
	{
		return player.IsGoalie ? $"{player.Saves} SV â€¢ {FormatSignedDecimal(player.Gsax, "0.00")} GSAx" : $"{player.Goals}G {player.Assists}A â€¢ {player.Xg:0.00} xG";
	}

	private static BoxscoreLeadersSnapshot BuildPreviousGameLeadersSnapshot(LiveBoxscoreData liveBoxscoreData, string summary, IReadOnlyDictionary<string, double> starPointsBySteamId)
	{
		BoxscoreLeadersSnapshot boxscoreLeadersSnapshot = new BoxscoreLeadersSnapshot
		{
			RequestedTab = "Leaders",
			Summary = summary,
			AvailableTabs = CreateBaseBoxscoreTabList()
		};
		List<BoxscorePlayerSnapshot> list = liveBoxscoreData.Players.Where((BoxscorePlayerSnapshot p) => p.Team == PlayerTeam.Red || p.Team == PlayerTeam.Blue).ToList();
		int num = 1;
		foreach (BoxscorePlayerSnapshot item in list.OrderByDescending((BoxscorePlayerSnapshot p) => starPointsBySteamId.TryGetValue(p.SteamId, out var value) ? value : 0.0).ThenByDescending((BoxscorePlayerSnapshot p) => p.Points).ThenByDescending((BoxscorePlayerSnapshot p) => p.Sog).Take(3))
		{
			boxscoreLeadersSnapshot.Stars.Add(new BoxscoreStarSnapshot
			{
				Rank = num++,
				Name = item.Name,
				Team = GetTeamDisplayLabel(item.Team),
				Position = item.Position,
				Summary = BuildPreviousGameStarSummary(item),
				Score = starPointsBySteamId.TryGetValue(item.SteamId, out var value2) ? value2 : 0.0
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
		return boxscoreLeadersSnapshot;
	}

	private static BoxscoreGameStatsSnapshot BuildPreviousGameGameStatsSnapshot(LiveBoxscoreData liveBoxscoreData, string summary)
	{
		BoxscoreGameStatsSnapshot boxscoreGameStatsSnapshot = new BoxscoreGameStatsSnapshot
		{
			RequestedTab = "Game Stats",
			Summary = summary,
			AvailableTabs = CreateBaseBoxscoreTabList(),
			RedScore = liveBoxscoreData.RedTeam.Goals,
			BlueScore = liveBoxscoreData.BlueTeam.Goals
		};
		BoxscoreTeamSnapshot redTeam = liveBoxscoreData.RedTeam;
		BoxscoreTeamSnapshot blueTeam = liveBoxscoreData.BlueTeam;
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
		return boxscoreGameStatsSnapshot;
	}

	private static BoxscorePlayerStatsSnapshot BuildPreviousGamePlayerStatsSnapshot(LiveBoxscoreData liveBoxscoreData, string summary, IReadOnlyDictionary<string, double> starPointsBySteamId)
	{
		BoxscorePlayerStatsSnapshot boxscorePlayerStatsSnapshot = new BoxscorePlayerStatsSnapshot
		{
			RequestedTab = "Player Stats",
			Summary = summary,
			AvailableTabs = CreateBaseBoxscoreTabList(),
			SkaterColumns = new List<string>
			{
				"Team", "Pos", "Name", "G", "SOG", "iCF", "iCF%", "xG", "A", "SP", "INF",
				"Pass", "Exit", "Entry", "Hit", "xHit", "TO", "TKA", "Touches", "Poss", "TOI",
				"DZ Poss", "NZ Poss", "OZ Poss", "DZ TOI", "NZ TOI", "OZ TOI", "Post", "FOW", "FOL", "+/-"
			},
			GoalieColumns = new List<string>
			{
				"Team", "Pos", "Name", "SA", "SV", "BodySv", "PadSv", "StickSv", "SV%", "GA",
				"xGA", "GSAx", "INF", "Pass", "Poss", "TOI", "DZ Poss", "NZ Poss", "OZ Poss"
			}
		};
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
				(liveBoxscoreData.ExpectedGoals?.Enabled == true ? player.Xga.ToString("0.00", CultureInfo.InvariantCulture) : ""),
				(liveBoxscoreData.ExpectedGoals?.Enabled == true ? FormatSignedDecimal(player.Gsax, "0.00") : ""),
				player.Influence.ToString("0.0", CultureInfo.InvariantCulture),
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
				(liveBoxscoreData.ExpectedGoals?.Enabled == true ? player.Xg.ToString("0.00", CultureInfo.InvariantCulture) : ""),
				player.Assists.ToString(CultureInfo.InvariantCulture),
				(starPointsBySteamId.TryGetValue(player.SteamId, out var value) ? value : 0.0).ToString("0.0", CultureInfo.InvariantCulture),
				player.Influence.ToString("0.0", CultureInfo.InvariantCulture),
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
				(player.PlusMinus > 0) ? ("+" + player.PlusMinus.ToString(CultureInfo.InvariantCulture)) : player.PlusMinus.ToString(CultureInfo.InvariantCulture)
			};
			BoxscorePlayerStatsRowSnapshot item = new BoxscorePlayerStatsRowSnapshot
			{
				SteamId = player.SteamId,
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
		return boxscorePlayerStatsSnapshot;
	}

	private static string FormatSnapshotFaceoffPercentage(int wins, int total)
	{
		if (total <= 0)
		{
			return "0% (0/0)";
		}
		double num = (double)wins / (double)total * 100.0;
		return $"{num:F0}% ({wins}/{total})";
	}
}
