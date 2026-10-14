using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Codebase;
using Newtonsoft.Json;
using UnityEngine;

namespace OPLStats_Stats;

public partial class Stats
{
	internal sealed class BoxscoreShootoutAttemptRecord
	{
		public string ShooterName { get; set; } = "";

		public string ShooterSteamId { get; set; } = "";

		public string GoalieSteamId { get; set; } = "";

		public bool Scored { get; set; }

		public bool GameWinner { get; set; }

		public PlayerTeam Team { get; set; } = PlayerTeam.None;

		public int AttemptNumber { get; set; }
	}

	internal sealed class BoxscoreShootoutRowSnapshot
	{
		public int AttemptNumber { get; set; }

		public string RedShooterName { get; set; } = "";

		public string BlueShooterName { get; set; } = "";

		public string RedState { get; set; } = "empty";

		public string BlueState { get; set; } = "empty";

		public bool HighlightRedCurrent { get; set; }

		public bool HighlightBlueCurrent { get; set; }
	}

	internal sealed class BoxscoreShootoutSnapshot : BoxscoreSnapshotBase
	{
		public string RedTeamName { get; set; } = "RED";

		public string BlueTeamName { get; set; } = "BLUE";

		public string RedGoalieName { get; set; } = "";

		public string BlueGoalieName { get; set; } = "";

		public int RedScore { get; set; }

		public int BlueScore { get; set; }

		public int MaxRounds { get; set; } = 5;

		public bool IsActive { get; set; }

		public List<BoxscoreShootoutRowSnapshot> Rows { get; set; } = new List<BoxscoreShootoutRowSnapshot>();
	}

	private sealed class CurrentGameShootoutBoxscoreState
	{
		public bool HasShootoutOccurred { get; set; }

		public bool IsActive { get; set; }

		public int MaxRounds { get; set; } = 5;

		public int RedScore { get; set; }

		public int BlueScore { get; set; }

		public string CurrentShooterName { get; set; } = "";

		public PlayerTeam CurrentShooterTeam { get; set; } = PlayerTeam.None;

		public string RedGoalieName { get; set; } = "";

		public string BlueGoalieName { get; set; } = "";

		public List<string> RedPlannedShooters { get; set; } = new List<string>();

		public List<string> BluePlannedShooters { get; set; } = new List<string>();

		public List<BoxscoreShootoutAttemptRecord> RedAttempts { get; set; } = new List<BoxscoreShootoutAttemptRecord>();

		public List<BoxscoreShootoutAttemptRecord> BlueAttempts { get; set; } = new List<BoxscoreShootoutAttemptRecord>();

		public LiveBoxscoreData FrozenLiveData { get; set; } = null;

		public BoxscoreLeadersSnapshot FrozenLeaders { get; set; } = null;

		public BoxscoreGameStatsSnapshot FrozenGameStats { get; set; } = null;

		public BoxscorePlayerStatsSnapshot FrozenPlayerStats { get; set; } = null;
	}

	private const string BOXSCORE_FIXED_AEST = "AEST";

	private static CurrentGameShootoutBoxscoreState _currentGameShootoutBoxscoreState = null;

	private static List<string> _clientBoxscoreAvailableTabs = CreateBaseBoxscoreTabList();

	private static Type _shootoutControllerType = null;

	private static PropertyInfo _shootoutControllerInstanceProperty = null;

	private static MethodInfo _shootoutControllerGetCurrentStateMethod = null;

	private static MethodInfo _shootoutControllerResolveGoalieForTeamMethod = null;

	private static FieldInfo _shootoutControllerCurrentShooterField = null;

	private static FieldInfo _shootoutControllerCurrentGoalieField = null;

	private static List<string> CreateBaseBoxscoreTabList()
	{
		return new List<string>
		{
			BOXSCORE_TAB_LEADERS,
			BOXSCORE_TAB_GAMESTATS,
			BOXSCORE_TAB_PLAYERSTATS
		};
	}

	private static List<string> NormalizeBoxscoreAvailableTabs(IEnumerable<string> tabs)
	{
		List<string> list = new List<string>();
		foreach (string item in tabs ?? CreateBaseBoxscoreTabList())
		{
			string text = NormalizeBoxscoreTab(item);
			if (!list.Contains(text))
			{
				list.Add(text);
			}
		}
		foreach (string item2 in CreateBaseBoxscoreTabList())
		{
			if (!list.Contains(item2))
			{
				list.Add(item2);
			}
		}
		if (tabs != null && tabs.Any((string tab) => string.Equals(tab, BOXSCORE_TAB_SHOOTOUT, StringComparison.OrdinalIgnoreCase)) && !list.Contains(BOXSCORE_TAB_SHOOTOUT))
		{
			list.Add(BOXSCORE_TAB_SHOOTOUT);
		}
		return list;
	}

	private static bool SnapshotHasShootoutTab(BoxscoreShootoutSnapshot snapshot)
	{
		return snapshot != null && (snapshot.Rows?.Count > 0 || !string.IsNullOrWhiteSpace(snapshot.RedGoalieName) || !string.IsNullOrWhiteSpace(snapshot.BlueGoalieName) || snapshot.RedScore > 0 || snapshot.BlueScore > 0 || snapshot.MaxRounds > 0 || snapshot.IsActive);
	}

	private static bool PreviousGameSnapshotHasShootoutTab(PreviousGameSnapshot snapshot)
	{
		return snapshot != null && SnapshotHasShootoutTab(snapshot.Shootout);
	}

	private static List<string> BuildAvailableTabsForCurrentGameBoxscore()
	{
		List<string> list = CreateBaseBoxscoreTabList();
		if (_currentGameShootoutBoxscoreState?.HasShootoutOccurred == true)
		{
			list.Add(BOXSCORE_TAB_SHOOTOUT);
		}
		return list;
	}

	private static List<string> BuildAvailableTabsForPreviousGameSnapshot(PreviousGameSnapshot snapshot)
	{
		List<string> list = CreateBaseBoxscoreTabList();
		if (PreviousGameSnapshotHasShootoutTab(snapshot))
		{
			list.Add(BOXSCORE_TAB_SHOOTOUT);
		}
		return list;
	}

	private static void SetClientBoxscoreAvailableTabs(IEnumerable<string> tabs, bool refreshVisiblePanel)
	{
		_clientBoxscoreAvailableTabs = NormalizeBoxscoreAvailableTabs(tabs);
		if (ServerFunc.IsDedicatedServer())
		{
			return;
		}
		RefreshBoxscoreTabButtonVisibility(refreshVisiblePanel);
	}

	private static string GetConfiguredBoxscoreTimeZone()
	{
		return string.IsNullOrWhiteSpace(ServerConfig?.BoxscoreTimeZone) ? BOXSCORE_FIXED_AEST : ServerConfig.BoxscoreTimeZone.Trim();
	}

	private static string GetConfiguredExportTimeZone()
	{
		if (!string.IsNullOrWhiteSpace(ServerConfig?.ExportTimeZone))
		{
			return ServerConfig.ExportTimeZone.Trim();
		}
		return GetConfiguredBoxscoreTimeZone();
	}

	private static bool TryParseUtcOffsetTimeZone(string configuredValue, out TimeSpan offset, out string label)
	{
		offset = TimeSpan.Zero;
		label = "";
		if (string.IsNullOrWhiteSpace(configuredValue))
		{
			return false;
		}
		string text = configuredValue.Trim();
		if (!text.StartsWith("UTC", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		if (text.Length == 3)
		{
			label = "UTC";
			offset = TimeSpan.Zero;
			return true;
		}
		string text2 = text.Substring(3).Trim();
		if (text2.Length == 0)
		{
			label = "UTC";
			offset = TimeSpan.Zero;
			return true;
		}
		if (text2[0] != '+' && text2[0] != '-')
		{
			return false;
		}
		if (!TimeSpan.TryParse(text2, CultureInfo.InvariantCulture, out offset))
		{
			if (text2.Length <= 3 && int.TryParse(text2, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
			{
				offset = TimeSpan.FromHours(result);
			}
			else
			{
				return false;
			}
		}
		label = "UTC" + text2;
		return true;
	}

	private static TimeZoneInfo ResolveConfiguredTimeZone(string configuredValue, out string label)
	{
		string text = string.IsNullOrWhiteSpace(configuredValue) ? BOXSCORE_FIXED_AEST : configuredValue.Trim();
		if (string.Equals(text, BOXSCORE_FIXED_AEST, StringComparison.OrdinalIgnoreCase) || string.Equals(text, "Australian Eastern Standard Time", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "AUS Eastern Standard Time", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "Australia/Brisbane", StringComparison.OrdinalIgnoreCase))
		{
			label = BOXSCORE_FIXED_AEST;
			return TimeZoneInfo.CreateCustomTimeZone(BOXSCORE_FIXED_AEST, TimeSpan.FromHours(10.0), "Australian Eastern Standard Time", BOXSCORE_FIXED_AEST);
		}
		if (TryParseUtcOffsetTimeZone(text, out var offset, out label))
		{
			return TimeZoneInfo.CreateCustomTimeZone(label, offset, label, label);
		}
		try
		{
			label = text;
			return TimeZoneInfo.FindSystemTimeZoneById(text);
		}
		catch (Exception arg)
		{
			Logging.LogError($"Invalid BoxscoreTimeZone '{text}', falling back to AEST. {arg.Message}", ServerConfig);
			label = BOXSCORE_FIXED_AEST;
			return TimeZoneInfo.CreateCustomTimeZone(BOXSCORE_FIXED_AEST, TimeSpan.FromHours(10.0), "Australian Eastern Standard Time", BOXSCORE_FIXED_AEST);
		}
	}

	private static TimeZoneInfo ResolveConfiguredBoxscoreTimeZone(out string label)
	{
		return ResolveConfiguredTimeZone(GetConfiguredBoxscoreTimeZone(), out label);
	}

	private static DateTime ConvertUtcToConfiguredTimeZone(DateTime utcTimestamp, string configuredTimeZone, out string label)
	{
		DateTime dateTime = (utcTimestamp.Kind == DateTimeKind.Unspecified) ? DateTime.SpecifyKind(utcTimestamp, DateTimeKind.Utc) : utcTimestamp.ToUniversalTime();
		TimeZoneInfo timeZoneInfo = ResolveConfiguredTimeZone(configuredTimeZone, out label);
		return TimeZoneInfo.ConvertTimeFromUtc(dateTime, timeZoneInfo);
	}

	private static string FormatBoxscoreTimestampWithConfiguredTimeZone(DateTime utcTimestamp)
	{
		DateTime dateTime = ConvertUtcToConfiguredTimeZone(utcTimestamp, GetConfiguredBoxscoreTimeZone(), out var label);
		return $"{dateTime:dd/MM/yyyy HH:mm} ({label})";
	}

	private static Type GetShootoutControllerType()
	{
		if (_shootoutControllerType != null)
		{
			return _shootoutControllerType;
		}
		_shootoutControllerType = AppDomain.CurrentDomain.GetAssemblies().Select((Assembly assembly) => assembly.GetType("PoncePuck.Shootout.ShootoutController", throwOnError: false)).FirstOrDefault((Type type) => type != null);
		if (_shootoutControllerType != null)
		{
			_shootoutControllerInstanceProperty = _shootoutControllerType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
			_shootoutControllerGetCurrentStateMethod = _shootoutControllerType.GetMethod("GetCurrentState", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
			_shootoutControllerResolveGoalieForTeamMethod = _shootoutControllerType.GetMethod("ResolveGoalieForTeam", BindingFlags.Instance | BindingFlags.NonPublic);
			_shootoutControllerCurrentShooterField = _shootoutControllerType.GetField("_currentShooter", BindingFlags.Instance | BindingFlags.NonPublic);
			_shootoutControllerCurrentGoalieField = _shootoutControllerType.GetField("_currentGoalie", BindingFlags.Instance | BindingFlags.NonPublic);
		}
		return _shootoutControllerType;
	}

	private static object GetShootoutControllerInstance()
	{
		return GetShootoutControllerType() == null ? null : _shootoutControllerInstanceProperty?.GetValue(null);
	}

	private static bool TryGetShootoutControllerState(out Dictionary<string, object> state)
	{
		state = null;
		if (GetShootoutControllerType() == null || _shootoutControllerGetCurrentStateMethod == null)
		{
			return false;
		}
		try
		{
			object obj = _shootoutControllerGetCurrentStateMethod.Invoke(null, null);
			if (obj is Dictionary<string, object> dictionary)
			{
				state = dictionary;
				return dictionary.Count > 0;
			}
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error reading shootout controller state: {arg}", ServerConfig);
		}
		return false;
	}

	private static List<string> ReadStringListFromShootoutPayload(Dictionary<string, object> payload, string key)
	{
		List<string> list = new List<string>();
		if (payload == null || !payload.TryGetValue(key, out var value) || value == null)
		{
			return list;
		}
		if (value is List<string> list2)
		{
			return list2.Where((string item) => !string.IsNullOrWhiteSpace(item)).Select((string item) => StripHtmlTags(item)).ToList();
		}
		if (value is IEnumerable<object> enumerable)
		{
			return enumerable.Select((object item) => StripHtmlTags(item?.ToString() ?? "")).Where((string item) => !string.IsNullOrWhiteSpace(item)).ToList();
		}
		return list;
	}

	private static int ReadIntFromShootoutPayload(Dictionary<string, object> payload, string key, int fallbackValue = 0)
	{
		if (payload == null || !payload.TryGetValue(key, out var value) || value == null)
		{
			return fallbackValue;
		}
		try
		{
			return Convert.ToInt32(value, CultureInfo.InvariantCulture);
		}
		catch
		{
			return fallbackValue;
		}
	}

	private static string ReadStringFromShootoutPayload(Dictionary<string, object> payload, string key)
	{
		if (payload == null || !payload.TryGetValue(key, out var value) || value == null)
		{
			return "";
		}
		return StripHtmlTags(value.ToString());
	}

	private static PlayerTeam ParseShootoutTeam(string teamText)
	{
		if (string.Equals(teamText, "Red", StringComparison.OrdinalIgnoreCase))
		{
			return PlayerTeam.Red;
		}
		if (string.Equals(teamText, "Blue", StringComparison.OrdinalIgnoreCase))
		{
			return PlayerTeam.Blue;
		}
		return PlayerTeam.None;
	}

	private static PlayerTeam GetOpposingShootoutTeam(PlayerTeam team)
	{
		if (team == PlayerTeam.Red)
		{
			return PlayerTeam.Blue;
		}
		if (team == PlayerTeam.Blue)
		{
			return PlayerTeam.Red;
		}
		return PlayerTeam.None;
	}

	private static string NormalizeShootoutPlayerName(string playerName)
	{
		string text = StripHtmlTags(playerName ?? "").Trim();
		if (!text.StartsWith("#", StringComparison.Ordinal))
		{
			return text;
		}
		int num = text.IndexOf(' ');
		if (num <= 1)
		{
			return text;
		}
		if (!int.TryParse(text.Substring(1, num - 1), out var _))
		{
			return text;
		}
		return text.Substring(num + 1).Trim();
	}

	private static string GetSafePlayerSteamId(Player player)
	{
		return (player != null && (bool)player) ? player.SteamId.Value.ToString() : "";
	}

	private static Player ResolveShootoutControllerPlayer(FieldInfo fieldInfo)
	{
		object shootoutControllerInstance = GetShootoutControllerInstance();
		if (shootoutControllerInstance == null || fieldInfo == null)
		{
			return null;
		}
		try
		{
			Player player = fieldInfo.GetValue(shootoutControllerInstance) as Player;
			if (player != null && (bool)player)
			{
				return player;
			}
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error resolving shootout controller player from {fieldInfo.Name}: {arg}", ServerConfig);
		}
		return null;
	}

	private static Player FindShootoutPlayerByName(string playerName, PlayerTeam team)
	{
		string text = NormalizeShootoutPlayerName(playerName);
		if (string.IsNullOrWhiteSpace(text))
		{
			return null;
		}
		IEnumerable<Player> enumerable = MonoBehaviourSingleton<PlayerManager>.Instance?.GetPlayers() ?? new List<Player>();
		List<Player> list = enumerable.Where((Player p) => p != null && (bool)p).ToList();
		Player player = list.FirstOrDefault((Player p) => p.Team == team && string.Equals(NormalizeShootoutPlayerName(p.Username.Value.ToString()), text, StringComparison.OrdinalIgnoreCase));
		if (player != null)
		{
			return player;
		}
		List<Player> list2 = list.Where((Player p) => string.Equals(NormalizeShootoutPlayerName(p.Username.Value.ToString()), text, StringComparison.OrdinalIgnoreCase)).ToList();
		return (list2.Count == 1) ? list2[0] : null;
	}

	private static Player ResolveCurrentShootoutShooterPlayer(PlayerTeam shootingTeam, string shooterName)
	{
		Player player = ResolveShootoutControllerPlayer(_shootoutControllerCurrentShooterField);
		if (player != null && (shootingTeam == PlayerTeam.None || player.Team == shootingTeam))
		{
			return player;
		}
		return FindShootoutPlayerByName(shooterName, shootingTeam);
	}

	private static Player ResolveShootoutGoaliePlayer(PlayerTeam team)
	{
		Player player = ResolveShootoutControllerPlayer(_shootoutControllerCurrentGoalieField);
		if (player != null && (team == PlayerTeam.None || player.Team == team))
		{
			return player;
		}
		object shootoutControllerInstance = GetShootoutControllerInstance();
		if (shootoutControllerInstance != null && _shootoutControllerResolveGoalieForTeamMethod != null)
		{
			try
			{
				Player player2 = _shootoutControllerResolveGoalieForTeamMethod.Invoke(shootoutControllerInstance, new object[1] { team }) as Player;
				if (player2 != null && (bool)player2)
				{
					return player2;
				}
			}
			catch (Exception arg)
			{
				Logging.LogError($"Error resolving shootout goalie for {team}: {arg}", ServerConfig);
			}
		}
		IEnumerable<Player> enumerable = MonoBehaviourSingleton<PlayerManager>.Instance?.GetPlayers() ?? new List<Player>();
		return enumerable.FirstOrDefault((Player p) => p != null && (bool)p && p.Team == team && (PlayerFunc.IsGoalie(p) || GetPlayerPosition(p) == "G"));
	}

	private static string ResolveShootoutGoalieName(PlayerTeam team)
	{
		Player player = ResolveShootoutGoaliePlayer(team);
		return (player != null && (bool)player) ? StripHtmlTags(player.Username.Value.ToString()) : "";
	}

	private static void UpdateShootoutGoalieNames(CurrentGameShootoutBoxscoreState state)
	{
		if (state == null)
		{
			return;
		}
		string text = ResolveShootoutGoalieName(PlayerTeam.Red);
		string text2 = ResolveShootoutGoalieName(PlayerTeam.Blue);
		if (!string.IsNullOrWhiteSpace(text))
		{
			state.RedGoalieName = text;
		}
		if (!string.IsNullOrWhiteSpace(text2))
		{
			state.BlueGoalieName = text2;
		}
	}

	private static CurrentGameShootoutBoxscoreState EnsureCurrentGameShootoutBoxscoreState()
	{
		if (_currentGameShootoutBoxscoreState != null)
		{
			return _currentGameShootoutBoxscoreState;
		}
		_currentGameShootoutBoxscoreState = new CurrentGameShootoutBoxscoreState();
		return _currentGameShootoutBoxscoreState;
	}

	private static void CaptureFrozenStandardBoxscoreSnapshotsForShootout(CurrentGameShootoutBoxscoreState state)
	{
		if (state == null || state.FrozenLiveData != null)
		{
			return;
		}
		state.FrozenLiveData = BuildLiveBoxscoreData();
		state.FrozenLeaders = BuildLiveBoxscoreLeadersSnapshot();
		state.FrozenGameStats = BuildLiveBoxscoreGameStatsSnapshot();
		state.FrozenPlayerStats = BuildLiveBoxscorePlayerStatsSnapshot();
	}

	private static void SyncCurrentGameShootoutBoxscoreStateFromController(CurrentGameShootoutBoxscoreState state)
	{
		if (state == null || !TryGetShootoutControllerState(out var state2))
		{
			return;
		}
		List<string> list = ReadStringListFromShootoutPayload(state2, "redShooters");
		List<string> list2 = ReadStringListFromShootoutPayload(state2, "blueShooters");
		if (list.Count > 0)
		{
			state.RedPlannedShooters = list;
		}
		if (list2.Count > 0)
		{
			state.BluePlannedShooters = list2;
		}
		int num = ReadIntFromShootoutPayload(state2, "maxRounds", state.MaxRounds);
		if (num > 0)
		{
			state.MaxRounds = num;
		}
		state.RedScore = ReadIntFromShootoutPayload(state2, "redScore", state.RedScore);
		state.BlueScore = ReadIntFromShootoutPayload(state2, "blueScore", state.BlueScore);
		string text = ReadStringFromShootoutPayload(state2, "currentShooter");
		if (!string.IsNullOrWhiteSpace(text))
		{
			state.CurrentShooterName = text;
		}
		PlayerTeam playerTeam = ParseShootoutTeam(ReadStringFromShootoutPayload(state2, "currentTeam"));
		if (playerTeam != PlayerTeam.None)
		{
			state.CurrentShooterTeam = playerTeam;
		}
		UpdateShootoutGoalieNames(state);
	}

	private static void EnsurePlannedShootoutSlot(CurrentGameShootoutBoxscoreState state, PlayerTeam team, string shooterName, int attemptIndex)
	{
		if (state == null || attemptIndex < 0 || string.IsNullOrWhiteSpace(shooterName))
		{
			return;
		}
		List<string> list = ((team == PlayerTeam.Red) ? state.RedPlannedShooters : state.BluePlannedShooters);
		while (list.Count <= attemptIndex)
		{
			list.Add("");
		}
		if (string.IsNullOrWhiteSpace(list[attemptIndex]))
		{
			list[attemptIndex] = shooterName;
		}
	}

	private static void InitializeCurrentGameShootoutBoxscoreState(Dictionary<string, object> beginPayload)
	{
		if (!ServerFunc.IsDedicatedServer())
		{
			return;
		}
		CurrentGameShootoutBoxscoreState currentGameShootoutBoxscoreState = EnsureCurrentGameShootoutBoxscoreState();
		currentGameShootoutBoxscoreState.HasShootoutOccurred = true;
		currentGameShootoutBoxscoreState.IsActive = true;
		currentGameShootoutBoxscoreState.CurrentShooterName = "";
		currentGameShootoutBoxscoreState.CurrentShooterTeam = PlayerTeam.None;
		currentGameShootoutBoxscoreState.RedAttempts.Clear();
		currentGameShootoutBoxscoreState.BlueAttempts.Clear();
		currentGameShootoutBoxscoreState.RedPlannedShooters = ReadStringListFromShootoutPayload(beginPayload, "redShooters");
		currentGameShootoutBoxscoreState.BluePlannedShooters = ReadStringListFromShootoutPayload(beginPayload, "blueShooters");
		currentGameShootoutBoxscoreState.MaxRounds = Math.Max(1, ReadIntFromShootoutPayload(beginPayload, "maxRounds", 5));
		CaptureFrozenStandardBoxscoreSnapshotsForShootout(currentGameShootoutBoxscoreState);
		SyncCurrentGameShootoutBoxscoreStateFromController(currentGameShootoutBoxscoreState);
	}

	private static void UpdateCurrentGameShootoutCurrentShooter(Dictionary<string, object> payload)
	{
		CurrentGameShootoutBoxscoreState currentGameShootoutBoxscoreState = _currentGameShootoutBoxscoreState;
		if (currentGameShootoutBoxscoreState == null)
		{
			return;
		}
		currentGameShootoutBoxscoreState.IsActive = true;
		currentGameShootoutBoxscoreState.CurrentShooterName = ReadStringFromShootoutPayload(payload, "shooter");
		currentGameShootoutBoxscoreState.CurrentShooterTeam = ParseShootoutTeam(ReadStringFromShootoutPayload(payload, "team"));
		if (currentGameShootoutBoxscoreState.CurrentShooterTeam == PlayerTeam.Red)
		{
			EnsurePlannedShootoutSlot(currentGameShootoutBoxscoreState, PlayerTeam.Red, currentGameShootoutBoxscoreState.CurrentShooterName, currentGameShootoutBoxscoreState.RedAttempts.Count);
		}
		else if (currentGameShootoutBoxscoreState.CurrentShooterTeam == PlayerTeam.Blue)
		{
			EnsurePlannedShootoutSlot(currentGameShootoutBoxscoreState, PlayerTeam.Blue, currentGameShootoutBoxscoreState.CurrentShooterName, currentGameShootoutBoxscoreState.BlueAttempts.Count);
		}
		SyncCurrentGameShootoutBoxscoreStateFromController(currentGameShootoutBoxscoreState);
	}

	private static void AppendCurrentGameShootoutAttempt(Dictionary<string, object> payload)
	{
		CurrentGameShootoutBoxscoreState currentGameShootoutBoxscoreState = _currentGameShootoutBoxscoreState;
		if (currentGameShootoutBoxscoreState == null)
		{
			return;
		}
		PlayerTeam playerTeam = ParseShootoutTeam(ReadStringFromShootoutPayload(payload, "team"));
		string text = ReadStringFromShootoutPayload(payload, "shooter");
		if (playerTeam != PlayerTeam.Red && playerTeam != PlayerTeam.Blue)
		{
			return;
		}
		bool flag = false;
		if (payload != null && payload.TryGetValue("scored", out var value) && value != null)
		{
			try
			{
				flag = Convert.ToBoolean(value, CultureInfo.InvariantCulture);
			}
			catch
			{
			}
		}
		Player player = ResolveCurrentShootoutShooterPlayer(playerTeam, text);
		Player player2 = ResolveShootoutGoaliePlayer(GetOpposingShootoutTeam(playerTeam));
		List<BoxscoreShootoutAttemptRecord> list = ((playerTeam == PlayerTeam.Red) ? currentGameShootoutBoxscoreState.RedAttempts : currentGameShootoutBoxscoreState.BlueAttempts);
		int num = currentGameShootoutBoxscoreState.RedAttempts.Count + currentGameShootoutBoxscoreState.BlueAttempts.Count + 1;
		list.Add(new BoxscoreShootoutAttemptRecord
		{
			ShooterName = ((player != null && (bool)player) ? StripHtmlTags(player.Username.Value.ToString()) : NormalizeShootoutPlayerName(text)),
			ShooterSteamId = GetSafePlayerSteamId(player),
			GoalieSteamId = GetSafePlayerSteamId(player2),
			Scored = flag,
			GameWinner = false,
			Team = playerTeam,
			AttemptNumber = num
		});
		EnsurePlannedShootoutSlot(currentGameShootoutBoxscoreState, playerTeam, text, Math.Max(0, list.Count - 1));
		currentGameShootoutBoxscoreState.RedScore = ReadIntFromShootoutPayload(payload, "redScore", currentGameShootoutBoxscoreState.RedScore);
		currentGameShootoutBoxscoreState.BlueScore = ReadIntFromShootoutPayload(payload, "blueScore", currentGameShootoutBoxscoreState.BlueScore);
		currentGameShootoutBoxscoreState.CurrentShooterName = "";
		currentGameShootoutBoxscoreState.CurrentShooterTeam = PlayerTeam.None;
		SyncCurrentGameShootoutBoxscoreStateFromController(currentGameShootoutBoxscoreState);
	}

	private static void FinalizeCurrentGameShootoutState(Dictionary<string, object> payload)
	{
		CurrentGameShootoutBoxscoreState currentGameShootoutBoxscoreState = _currentGameShootoutBoxscoreState;
		if (currentGameShootoutBoxscoreState == null)
		{
			return;
		}
		currentGameShootoutBoxscoreState.IsActive = false;
		currentGameShootoutBoxscoreState.CurrentShooterName = "";
		currentGameShootoutBoxscoreState.CurrentShooterTeam = PlayerTeam.None;
		currentGameShootoutBoxscoreState.RedScore = ReadIntFromShootoutPayload(payload, "redScore", currentGameShootoutBoxscoreState.RedScore);
		currentGameShootoutBoxscoreState.BlueScore = ReadIntFromShootoutPayload(payload, "blueScore", currentGameShootoutBoxscoreState.BlueScore);
		UpdateShootoutGoalieNames(currentGameShootoutBoxscoreState);
		PlayerTeam playerTeam = ParseShootoutTeam(ReadStringFromShootoutPayload(payload, "winningTeam"));
		if (playerTeam == PlayerTeam.None)
		{
			playerTeam = ((currentGameShootoutBoxscoreState.RedScore > currentGameShootoutBoxscoreState.BlueScore) ? PlayerTeam.Red : ((currentGameShootoutBoxscoreState.BlueScore > currentGameShootoutBoxscoreState.RedScore) ? PlayerTeam.Blue : PlayerTeam.None));
		}
		MarkShootoutGameWinningAttempt(currentGameShootoutBoxscoreState, playerTeam);
	}

	private static List<BoxscoreShootoutAttemptRecord> BuildOrderedShootoutAttemptRecords(CurrentGameShootoutBoxscoreState state)
	{
		if (state == null)
		{
			return new List<BoxscoreShootoutAttemptRecord>();
		}
		return state.RedAttempts.Concat(state.BlueAttempts).Where((BoxscoreShootoutAttemptRecord attempt) => attempt != null).OrderBy((BoxscoreShootoutAttemptRecord attempt) => (attempt.AttemptNumber > 0) ? attempt.AttemptNumber : int.MaxValue).ThenBy((BoxscoreShootoutAttemptRecord attempt) => (attempt.Team == PlayerTeam.Red) ? 0 : 1).ToList();
	}

	private static BoxscoreShootoutAttemptRecord CloneShootoutAttemptRecord(BoxscoreShootoutAttemptRecord attempt)
	{
		if (attempt == null)
		{
			return null;
		}
		return new BoxscoreShootoutAttemptRecord
		{
			ShooterName = attempt.ShooterName ?? "",
			ShooterSteamId = attempt.ShooterSteamId ?? "",
			GoalieSteamId = attempt.GoalieSteamId ?? "",
			Scored = attempt.Scored,
			GameWinner = attempt.GameWinner,
			Team = attempt.Team,
			AttemptNumber = attempt.AttemptNumber
		};
	}

	private static int GetCurrentShootoutAttemptCount()
	{
		return (_currentGameShootoutBoxscoreState?.RedAttempts.Count ?? 0) + (_currentGameShootoutBoxscoreState?.BlueAttempts.Count ?? 0);
	}

	private static bool TryResolveShootoutWinnerAfterAttempt(int maxRounds, IReadOnlyList<BoxscoreShootoutAttemptRecord> orderedAttempts, int attemptIndex, out PlayerTeam winner)
	{
		winner = PlayerTeam.None;
		if (orderedAttempts == null || attemptIndex < 0 || attemptIndex >= orderedAttempts.Count)
		{
			return false;
		}
		int num = Math.Max(1, maxRounds);
		int num2 = 0;
		int num3 = 0;
		int num4 = 0;
		int num5 = 0;
		for (int i = 0; i <= attemptIndex; i++)
		{
			BoxscoreShootoutAttemptRecord boxscoreShootoutAttemptRecord = orderedAttempts[i];
			if (boxscoreShootoutAttemptRecord == null)
			{
				continue;
			}
			if (boxscoreShootoutAttemptRecord.Team == PlayerTeam.Red)
			{
				num2++;
				if (boxscoreShootoutAttemptRecord.Scored)
				{
					num4++;
				}
			}
			else if (boxscoreShootoutAttemptRecord.Team == PlayerTeam.Blue)
			{
				num3++;
				if (boxscoreShootoutAttemptRecord.Scored)
				{
					num5++;
				}
			}
		}
		if (num2 < num || num3 < num)
		{
			int num6 = Math.Max(0, num - num2);
			int num7 = Math.Max(0, num - num3);
			if (num4 > num5 + num7)
			{
				winner = PlayerTeam.Red;
				return true;
			}
			if (num5 > num4 + num6)
			{
				winner = PlayerTeam.Blue;
				return true;
			}
			return false;
		}
		if (num2 == num3 && num4 != num5)
		{
			winner = ((num4 > num5) ? PlayerTeam.Red : PlayerTeam.Blue);
			return true;
		}
		return false;
	}

	private static void MarkShootoutGameWinningAttempt(CurrentGameShootoutBoxscoreState state, PlayerTeam winningTeam)
	{
		if (state == null)
		{
			return;
		}
		foreach (BoxscoreShootoutAttemptRecord item in state.RedAttempts)
		{
			if (item != null)
			{
				item.GameWinner = false;
			}
		}
		foreach (BoxscoreShootoutAttemptRecord item2 in state.BlueAttempts)
		{
			if (item2 != null)
			{
				item2.GameWinner = false;
			}
		}
		if (winningTeam == PlayerTeam.None)
		{
			return;
		}
		List<BoxscoreShootoutAttemptRecord> list = BuildOrderedShootoutAttemptRecords(state);
		for (int i = 0; i < list.Count; i++)
		{
			if (!TryResolveShootoutWinnerAfterAttempt(state.MaxRounds, list, i, out var winner) || winner != winningTeam)
			{
				continue;
			}
			for (int j = i; j >= 0; j--)
			{
				if (list[j].Team == winningTeam && list[j].Scored)
				{
					list[j].GameWinner = true;
					return;
				}
			}
			break;
		}
		BoxscoreShootoutAttemptRecord boxscoreShootoutAttemptRecord = list.LastOrDefault((BoxscoreShootoutAttemptRecord attempt) => attempt.Team == winningTeam && attempt.Scored);
		if (boxscoreShootoutAttemptRecord != null)
		{
			boxscoreShootoutAttemptRecord.GameWinner = true;
		}
	}

	private static void ClearCurrentGameShootoutBoxscoreState()
	{
		_currentGameShootoutBoxscoreState = null;
	}

	public static void Event_OnShootoutCurrentShooter(Dictionary<string, object> message)
	{
		if (!ServerFunc.IsDedicatedServer())
		{
			return;
		}
		try
		{
			UpdateCurrentGameShootoutCurrentShooter(message);
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error in Event_OnShootoutCurrentShooter.\n{arg}", ServerConfig);
		}
	}

	public static void Event_OnShootoutAttemptResult(Dictionary<string, object> message)
	{
		if (!ServerFunc.IsDedicatedServer())
		{
			return;
		}
		try
		{
			AppendCurrentGameShootoutAttempt(message);
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error in Event_OnShootoutAttemptResult.\n{arg}", ServerConfig);
		}
	}

	private static BoxscoreLeadersSnapshot CloneLeadersSnapshotWithSummary(BoxscoreLeadersSnapshot source, string summary, IEnumerable<string> availableTabs)
	{
		BoxscoreLeadersSnapshot boxscoreLeadersSnapshot = new BoxscoreLeadersSnapshot
		{
			RequestedTab = BOXSCORE_TAB_LEADERS,
			Summary = summary,
			Error = source?.Error ?? "",
			AvailableTabs = NormalizeBoxscoreAvailableTabs(availableTabs)
		};
		foreach (BoxscoreStarSnapshot item in source?.Stars ?? new List<BoxscoreStarSnapshot>())
		{
			boxscoreLeadersSnapshot.Stars.Add(new BoxscoreStarSnapshot
			{
				Rank = item.Rank,
				Name = item.Name,
				Team = item.Team,
				Position = item.Position,
				Summary = item.Summary,
				Score = item.Score
			});
		}
		foreach (BoxscoreLeaderRowSnapshot item2 in source?.Leaders ?? new List<BoxscoreLeaderRowSnapshot>())
		{
			boxscoreLeadersSnapshot.Leaders.Add(new BoxscoreLeaderRowSnapshot
			{
				Category = item2.Category,
				Name = item2.Name,
				Team = item2.Team,
				Position = item2.Position,
				Value = item2.Value,
				Detail = item2.Detail
			});
		}
		return boxscoreLeadersSnapshot;
	}

	private static BoxscoreGameStatsSnapshot CloneGameStatsSnapshotWithSummary(BoxscoreGameStatsSnapshot source, string summary, IEnumerable<string> availableTabs)
	{
		BoxscoreGameStatsSnapshot boxscoreGameStatsSnapshot = new BoxscoreGameStatsSnapshot
		{
			RequestedTab = BOXSCORE_TAB_GAMESTATS,
			Summary = summary,
			Error = source?.Error ?? "",
			AvailableTabs = NormalizeBoxscoreAvailableTabs(availableTabs),
			RedScore = source?.RedScore ?? 0,
			BlueScore = source?.BlueScore ?? 0
		};
		foreach (BoxscoreTeamStatRowSnapshot item in source?.Rows ?? new List<BoxscoreTeamStatRowSnapshot>())
		{
			boxscoreGameStatsSnapshot.Rows.Add(new BoxscoreTeamStatRowSnapshot
			{
				Label = item.Label,
				RedValue = item.RedValue,
				BlueValue = item.BlueValue,
				HighlightRed = item.HighlightRed,
				HighlightBlue = item.HighlightBlue
			});
		}
		return boxscoreGameStatsSnapshot;
	}

	private static BoxscorePlayerStatsSnapshot ClonePlayerStatsSnapshotWithSummary(BoxscorePlayerStatsSnapshot source, string summary, IEnumerable<string> availableTabs)
	{
		BoxscorePlayerStatsSnapshot boxscorePlayerStatsSnapshot = new BoxscorePlayerStatsSnapshot
		{
			RequestedTab = BOXSCORE_TAB_PLAYERSTATS,
			Summary = summary,
			Error = source?.Error ?? "",
			AvailableTabs = NormalizeBoxscoreAvailableTabs(availableTabs),
			SkaterColumns = new List<string>(source?.SkaterColumns ?? new List<string>()),
			GoalieColumns = new List<string>(source?.GoalieColumns ?? new List<string>())
		};
		foreach (BoxscorePlayerStatsRowSnapshot item in source?.SkaterRows ?? new List<BoxscorePlayerStatsRowSnapshot>())
		{
			boxscorePlayerStatsSnapshot.SkaterRows.Add(new BoxscorePlayerStatsRowSnapshot
			{
				SteamId = item.SteamId,
				Team = item.Team,
				IsGoalie = item.IsGoalie,
				Cells = new List<string>(item.Cells ?? new List<string>())
			});
		}
		foreach (BoxscorePlayerStatsRowSnapshot item2 in source?.GoalieRows ?? new List<BoxscorePlayerStatsRowSnapshot>())
		{
			boxscorePlayerStatsSnapshot.GoalieRows.Add(new BoxscorePlayerStatsRowSnapshot
			{
				SteamId = item2.SteamId,
				Team = item2.Team,
				IsGoalie = item2.IsGoalie,
				Cells = new List<string>(item2.Cells ?? new List<string>())
			});
		}
		return boxscorePlayerStatsSnapshot;
	}

	private static BoxscoreShootoutSnapshot CloneShootoutSnapshotWithSummary(BoxscoreShootoutSnapshot source, string summary, IEnumerable<string> availableTabs)
	{
		if (source == null)
		{
			return null;
		}
		BoxscoreShootoutSnapshot boxscoreShootoutSnapshot = new BoxscoreShootoutSnapshot
		{
			RequestedTab = BOXSCORE_TAB_SHOOTOUT,
			Summary = summary,
			Error = source.Error,
			AvailableTabs = NormalizeBoxscoreAvailableTabs(availableTabs),
			RedTeamName = source.RedTeamName,
			BlueTeamName = source.BlueTeamName,
			RedGoalieName = source.RedGoalieName,
			BlueGoalieName = source.BlueGoalieName,
			RedScore = source.RedScore,
			BlueScore = source.BlueScore,
			MaxRounds = source.MaxRounds,
			IsActive = source.IsActive
		};
		foreach (BoxscoreShootoutRowSnapshot item in source.Rows ?? new List<BoxscoreShootoutRowSnapshot>())
		{
			boxscoreShootoutSnapshot.Rows.Add(new BoxscoreShootoutRowSnapshot
			{
				AttemptNumber = item.AttemptNumber,
				RedShooterName = item.RedShooterName,
				BlueShooterName = item.BlueShooterName,
				RedState = item.RedState,
				BlueState = item.BlueState,
				HighlightRedCurrent = item.HighlightRedCurrent,
				HighlightBlueCurrent = item.HighlightBlueCurrent
			});
		}
		return boxscoreShootoutSnapshot;
	}

	private static BoxscoreShootoutSnapshot BuildCurrentGameShootoutSnapshot(string summary, IEnumerable<string> availableTabs)
	{
		CurrentGameShootoutBoxscoreState currentGameShootoutBoxscoreState = _currentGameShootoutBoxscoreState;
		if (currentGameShootoutBoxscoreState == null || !currentGameShootoutBoxscoreState.HasShootoutOccurred)
		{
			return null;
		}
		int num = currentGameShootoutBoxscoreState.RedAttempts.Count;
		int num2 = currentGameShootoutBoxscoreState.BlueAttempts.Count;
		int num3 = Math.Max(currentGameShootoutBoxscoreState.RedPlannedShooters.Count, currentGameShootoutBoxscoreState.BluePlannedShooters.Count);
		if (currentGameShootoutBoxscoreState.IsActive)
		{
			if (currentGameShootoutBoxscoreState.CurrentShooterTeam == PlayerTeam.Red)
			{
				num3 = Math.Max(num3, num + 1);
			}
			else if (currentGameShootoutBoxscoreState.CurrentShooterTeam == PlayerTeam.Blue)
			{
				num3 = Math.Max(num3, num2 + 1);
			}
		}
		num3 = Math.Max(num3, Math.Max(num, num2));
		BoxscoreShootoutSnapshot boxscoreShootoutSnapshot = new BoxscoreShootoutSnapshot
		{
			RequestedTab = BOXSCORE_TAB_SHOOTOUT,
			Summary = summary,
			AvailableTabs = NormalizeBoxscoreAvailableTabs(availableTabs),
			RedTeamName = "RED",
			BlueTeamName = "BLUE",
			RedGoalieName = string.IsNullOrWhiteSpace(currentGameShootoutBoxscoreState.RedGoalieName) ? "None" : currentGameShootoutBoxscoreState.RedGoalieName,
			BlueGoalieName = string.IsNullOrWhiteSpace(currentGameShootoutBoxscoreState.BlueGoalieName) ? "None" : currentGameShootoutBoxscoreState.BlueGoalieName,
			RedScore = currentGameShootoutBoxscoreState.RedScore,
			BlueScore = currentGameShootoutBoxscoreState.BlueScore,
			MaxRounds = Math.Max(1, currentGameShootoutBoxscoreState.MaxRounds),
			IsActive = currentGameShootoutBoxscoreState.IsActive
		};
		for (int i = 0; i < num3; i++)
		{
			BoxscoreShootoutAttemptRecord boxscoreShootoutAttemptRecord = (i < currentGameShootoutBoxscoreState.RedAttempts.Count) ? currentGameShootoutBoxscoreState.RedAttempts[i] : null;
			BoxscoreShootoutAttemptRecord boxscoreShootoutAttemptRecord2 = (i < currentGameShootoutBoxscoreState.BlueAttempts.Count) ? currentGameShootoutBoxscoreState.BlueAttempts[i] : null;
			string text = boxscoreShootoutAttemptRecord?.ShooterName ?? ((i < currentGameShootoutBoxscoreState.RedPlannedShooters.Count) ? currentGameShootoutBoxscoreState.RedPlannedShooters[i] : "");
			string text2 = boxscoreShootoutAttemptRecord2?.ShooterName ?? ((i < currentGameShootoutBoxscoreState.BluePlannedShooters.Count) ? currentGameShootoutBoxscoreState.BluePlannedShooters[i] : "");
			bool flag = currentGameShootoutBoxscoreState.IsActive && currentGameShootoutBoxscoreState.CurrentShooterTeam == PlayerTeam.Red && i == currentGameShootoutBoxscoreState.RedAttempts.Count;
			bool flag2 = currentGameShootoutBoxscoreState.IsActive && currentGameShootoutBoxscoreState.CurrentShooterTeam == PlayerTeam.Blue && i == currentGameShootoutBoxscoreState.BlueAttempts.Count;
			if (flag && !string.IsNullOrWhiteSpace(currentGameShootoutBoxscoreState.CurrentShooterName))
			{
				text = currentGameShootoutBoxscoreState.CurrentShooterName;
			}
			if (flag2 && !string.IsNullOrWhiteSpace(currentGameShootoutBoxscoreState.CurrentShooterName))
			{
				text2 = currentGameShootoutBoxscoreState.CurrentShooterName;
			}
			boxscoreShootoutSnapshot.Rows.Add(new BoxscoreShootoutRowSnapshot
			{
				AttemptNumber = i + 1,
				RedShooterName = text,
				BlueShooterName = text2,
				RedState = (boxscoreShootoutAttemptRecord != null) ? (boxscoreShootoutAttemptRecord.Scored ? "goal" : "miss") : (string.IsNullOrWhiteSpace(text) ? "empty" : "pending"),
				BlueState = (boxscoreShootoutAttemptRecord2 != null) ? (boxscoreShootoutAttemptRecord2.Scored ? "goal" : "miss") : (string.IsNullOrWhiteSpace(text2) ? "empty" : "pending"),
				HighlightRedCurrent = flag,
				HighlightBlueCurrent = flag2
			});
		}
		return boxscoreShootoutSnapshot;
	}

	private static bool TrySendCurrentGameShootoutBoxscoreSnapshot(ulong clientId, string requestedTab)
	{
		if (!ServerFunc.IsDedicatedServer() || _currentGameShootoutBoxscoreState == null || !_currentGameShootoutBoxscoreState.HasShootoutOccurred)
		{
			return false;
		}
		string text = BuildBoxscoreSummary();
		List<string> list = BuildAvailableTabsForCurrentGameBoxscore();
		switch (NormalizeBoxscoreTab(requestedTab))
		{
		case BOXSCORE_TAB_LEADERS:
			NetworkCommunication.SendData(BOXSCORE_LEADERS, JsonConvert.SerializeObject(CloneLeadersSnapshotWithSummary(_currentGameShootoutBoxscoreState.FrozenLeaders, text, list)), clientId, "oomtm450_stats_server", ServerConfig);
			return true;
		case BOXSCORE_TAB_GAMESTATS:
			NetworkCommunication.SendData(BOXSCORE_GAMESTATS, JsonConvert.SerializeObject(CloneGameStatsSnapshotWithSummary(_currentGameShootoutBoxscoreState.FrozenGameStats, text, list)), clientId, "oomtm450_stats_server", ServerConfig);
			return true;
		case BOXSCORE_TAB_PLAYERSTATS:
			NetworkCommunication.SendData(BOXSCORE_PLAYERSTATS, JsonConvert.SerializeObject(ClonePlayerStatsSnapshotWithSummary(_currentGameShootoutBoxscoreState.FrozenPlayerStats, text, list)), clientId, "oomtm450_stats_server", ServerConfig);
			return true;
		case BOXSCORE_TAB_SHOOTOUT:
		{
			BoxscoreShootoutSnapshot currentGameShootoutSnapshot = BuildCurrentGameShootoutSnapshot(text, list);
			if (currentGameShootoutSnapshot == null)
			{
				return false;
			}
			NetworkCommunication.SendData(BOXSCORE_SHOOTOUT, JsonConvert.SerializeObject(currentGameShootoutSnapshot), clientId, "oomtm450_stats_server", ServerConfig);
			return true;
		}
		default:
			return false;
		}
	}
}
