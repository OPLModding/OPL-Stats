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
	internal abstract class Check
	{
		internal bool HasToCheck { get; set; }

		internal int FramesChecked { get; set; }
	}

	internal class SaveCheck : Check
	{
		internal string ShooterSteamId { get; set; } = "";

		internal PlayerTeam ShooterTeam { get; set; } = PlayerTeam.Blue;

		internal bool HitStick { get; set; }

		internal string SaveSurface { get; set; } = "Body";
	}

	internal class BlockCheck : Check
	{
		internal string BlockerSteamId { get; set; } = "";

		internal PlayerTeam ShooterTeam { get; set; } = PlayerTeam.Blue;

		internal bool ShotWasOnNet { get; set; }

		internal float BlockGameTime { get; set; }
	}

	internal class Possession
	{
		internal string SteamId { get; set; } = "";

		internal PlayerTeam Team { get; set; }

		internal DateTime Date { get; set; } = DateTime.MinValue;
	}

	internal class GoalInfo
	{
		[JsonIgnore]
		internal bool IsTracked;

		private void SetTrackedValue<T>(ref T field, T value)
		{
			if (EqualityComparer<T>.Default.Equals(field, value)) return;
			field = value;
			if (IsTracked) MarkAnalyticsChanged(float.NegativeInfinity);
		}

		private float _gameTime;
		public float GameTime { get => _gameTime; set => SetTrackedValue(ref _gameTime, value); }

		private int _period;
		public int Period { get => _period; set => SetTrackedValue(ref _period, value); }

		private string _team = "";
		public string Team { get => _team; set => SetTrackedValue(ref _team, value); }

		private string _scorer = "";
		public string Scorer { get => _scorer; set => SetTrackedValue(ref _scorer, value); }

		private string _scorerPosition = "";
		public string ScorerPosition { get => _scorerPosition; set => SetTrackedValue(ref _scorerPosition, value); }

		private string _primaryAssist;
		public string PrimaryAssist { get => _primaryAssist; set => SetTrackedValue(ref _primaryAssist, value); }

		private string _primaryAssistPosition = "";
		public string PrimaryAssistPosition { get => _primaryAssistPosition; set => SetTrackedValue(ref _primaryAssistPosition, value); }

		private string _secondaryAssist;
		public string SecondaryAssist { get => _secondaryAssist; set => SetTrackedValue(ref _secondaryAssist, value); }

		private string _secondaryAssistPosition = "";
		public string SecondaryAssistPosition { get => _secondaryAssistPosition; set => SetTrackedValue(ref _secondaryAssistPosition, value); }

		private bool _gWG;
		public bool GWG { get => _gWG; set => SetTrackedValue(ref _gWG, value); }
	}

	internal class PostHitPosition
	{
		public float X { get; set; }

		public float Y { get; set; }

		public float Z { get; set; }

		public float GameTimeSeconds { get; set; }
	}

	internal class PlayByPlayEvent
	{
		[JsonIgnore]
		internal bool IsTracked;

		private void SetTrackedValue<T>(ref T field, T value, bool orderingChanged)
		{
			if (EqualityComparer<T>.Default.Equals(field, value)) return;
			float previousTime = GameTime;
			field = value;
			if (IsTracked) EventDataChanged(this, previousTime, orderingChanged);
		}

		private int _eventId;
		public int EventId { get => _eventId; set => SetTrackedValue(ref _eventId, value, true); }

		private PlayByPlayEventType _eventType;
		public PlayByPlayEventType EventType { get => _eventType; set => SetTrackedValue(ref _eventType, value, false); }

		private float _gameTime;
		public float GameTime { get => _gameTime; set => SetTrackedValue(ref _gameTime, value, true); }

		private int _period;
		public int Period { get => _period; set => SetTrackedValue(ref _period, value, false); }

		private string _playerSteamId = "";
		public string PlayerSteamId { get => _playerSteamId; set => SetTrackedValue(ref _playerSteamId, value, false); }

		private string _playerName = "";
		public string PlayerName { get => _playerName; set => SetTrackedValue(ref _playerName, value, false); }

		private int _playerTeam;
		public int PlayerTeam { get => _playerTeam; set => SetTrackedValue(ref _playerTeam, value, false); }

		private string _playerPosition = "";
		public string PlayerPosition { get => _playerPosition; set => SetTrackedValue(ref _playerPosition, value, false); }

		private int _playerJersey;
		public int PlayerJersey { get => _playerJersey; set => SetTrackedValue(ref _playerJersey, value, false); }

		private float _playerSpeed;
		public float PlayerSpeed { get => _playerSpeed; set => SetTrackedValue(ref _playerSpeed, value, false); }

		private EventZone _zone;
		public EventZone Zone { get => _zone; set => SetTrackedValue(ref _zone, value, false); }

		private Vector3 _position;
		public Vector3 Position { get => _position; set => SetTrackedValue(ref _position, value, false); }

		private Vector3 _velocity;
		public Vector3 Velocity { get => _velocity; set => SetTrackedValue(ref _velocity, value, false); }

		private float _forceMagnitude;
		public float ForceMagnitude { get => _forceMagnitude; set => SetTrackedValue(ref _forceMagnitude, value, false); }

		private string _outcome = "successful";
		public string Outcome { get => _outcome; set => SetTrackedValue(ref _outcome, value, false); }

		private string _flags = "";
		public string Flags { get => _flags; set => SetTrackedValue(ref _flags, value, false); }

		private string _team = "";
		public string Team { get => _team; set => SetTrackedValue(ref _team, value, false); }

		private string _teamInPossession = "";
		public string TeamInPossession { get => _teamInPossession; set => SetTrackedValue(ref _teamInPossession, value, false); }

		private string _currentPlayInPossession = "";
		public string CurrentPlayInPossession { get => _currentPlayInPossession; set => SetTrackedValue(ref _currentPlayInPossession, value, false); }

		private string _scoreState = "";
		public string ScoreState { get => _scoreState; set => SetTrackedValue(ref _scoreState, value, false); }

		private string _teamForwardsSteamID = "";
		public string TeamForwardsSteamID { get => _teamForwardsSteamID; set => SetTrackedValue(ref _teamForwardsSteamID, value, false); }

		private string _teamDefencemenSteamID = "";
		public string TeamDefencemenSteamID { get => _teamDefencemenSteamID; set => SetTrackedValue(ref _teamDefencemenSteamID, value, false); }

		private string _teamGoalieSteamID = "";
		public string TeamGoalieSteamID { get => _teamGoalieSteamID; set => SetTrackedValue(ref _teamGoalieSteamID, value, false); }

		private string _opposingTeamForwardsSteamID = "";
		public string OpposingTeamForwardsSteamID { get => _opposingTeamForwardsSteamID; set => SetTrackedValue(ref _opposingTeamForwardsSteamID, value, false); }

		private string _opposingTeamDefencemenSteamID = "";
		public string OpposingTeamDefencemenSteamID { get => _opposingTeamDefencemenSteamID; set => SetTrackedValue(ref _opposingTeamDefencemenSteamID, value, false); }

		private string _opposingTeamGoalieSteamID = "";
		public string OpposingTeamGoalieSteamID { get => _opposingTeamGoalieSteamID; set => SetTrackedValue(ref _opposingTeamGoalieSteamID, value, false); }

		private DateTime _timestamp;
		public DateTime Timestamp { get => _timestamp; set => SetTrackedValue(ref _timestamp, value, false); }

		public double Xg { get; set; }
	}

	internal class ExpectedGoalsExportData
	{
		public bool Enabled { get; set; }

		public string ModelVersion { get; set; } = "";

		public string TrainedThrough { get; set; } = "";

		public string ModelType { get; set; } = "";

		public int ShotsEvaluated { get; set; }

		public Dictionary<string, double> ShooterXgBySteamId { get; } = new Dictionary<string, double>();

		public Dictionary<string, double> GoalieXgaBySteamId { get; } = new Dictionary<string, double>();
	}

	internal class ResolvedPlayByPlaySummary
	{
		public Dictionary<string, int> GoalsBySteamId { get; } = new Dictionary<string, int>(StringComparer.Ordinal);

		public Dictionary<string, int> SogBySteamId { get; } = new Dictionary<string, int>(StringComparer.Ordinal);

		public Dictionary<string, (int ShotsFaced, int GoalsAllowed)> GoalieSummary { get; } = new Dictionary<string, (int ShotsFaced, int GoalsAllowed)>(StringComparer.Ordinal);

		public HashSet<int> OfficialGoalShotEventIds { get; } = new HashSet<int>();

		public HashSet<int> OwnGoalShotEventIds { get; } = new HashSet<int>();
	}

	internal class BoxscoreOverlayView : UIView
	{
	}

	internal class BoxscoreSnapshotBase
	{
		public string RequestedTab { get; set; } = "";

		public string Summary { get; set; } = "";

		public string Error { get; set; } = "";

		public List<string> AvailableTabs { get; set; } = new List<string>();
	}

	internal class BoxscoreStarSnapshot
	{
		public int Rank { get; set; }

		public string Name { get; set; } = "";

		public string Team { get; set; } = "";

		public string Position { get; set; } = "";

		public string Summary { get; set; } = "";

		public double Score { get; set; }
	}

	internal class BoxscoreLeaderRowSnapshot
	{
		public string Category { get; set; } = "";

		public string Name { get; set; } = "";

		public string Team { get; set; } = "";

		public string Position { get; set; } = "";

		public string Value { get; set; } = "";

		public string Detail { get; set; } = "";
	}

	internal class BoxscoreLeadersSnapshot : BoxscoreSnapshotBase
	{
		public List<BoxscoreStarSnapshot> Stars { get; set; } = new List<BoxscoreStarSnapshot>();

		public List<BoxscoreLeaderRowSnapshot> Leaders { get; set; } = new List<BoxscoreLeaderRowSnapshot>();
	}

	internal class BoxscoreTeamStatRowSnapshot
	{
		public string Label { get; set; } = "";

		public string RedValue { get; set; } = "";

		public string BlueValue { get; set; } = "";

		public bool HighlightRed { get; set; }

		public bool HighlightBlue { get; set; }
	}

	internal class BoxscoreGameStatsSnapshot : BoxscoreSnapshotBase
	{
		public int RedScore { get; set; }

		public int BlueScore { get; set; }

		public List<BoxscoreTeamStatRowSnapshot> Rows { get; set; } = new List<BoxscoreTeamStatRowSnapshot>();
	}

	internal class BoxscorePlayerStatsRowSnapshot
	{
		public string Team { get; set; } = "";

		public bool IsGoalie { get; set; }

		public List<string> Cells { get; set; } = new List<string>();
	}

	internal class BoxscorePlayerStatsSnapshot : BoxscoreSnapshotBase
	{
		public List<string> SkaterColumns { get; set; } = new List<string>();

		public List<BoxscorePlayerStatsRowSnapshot> SkaterRows { get; set; } = new List<BoxscorePlayerStatsRowSnapshot>();

		public List<string> GoalieColumns { get; set; } = new List<string>();

		public List<BoxscorePlayerStatsRowSnapshot> GoalieRows { get; set; } = new List<BoxscorePlayerStatsRowSnapshot>();
	}

	internal class BoxscorePlayerSnapshot
	{
		public string SteamId { get; set; } = "";

		public string Name { get; set; } = "";

		public PlayerTeam Team { get; set; } = PlayerTeam.None;

		public string TeamLabel { get; set; } = "spectator";

		public string Position { get; set; } = "";

		public bool IsGoalie { get; set; }

		public int Goals { get; set; }

		public int Assists { get; set; }

		public int Sog { get; set; }

		public int IndividualCorsiFor { get; set; }

		public int OnIceTeamShotAttempts { get; set; }

		public double IndividualCorsiForPercentage { get; set; }

		public int Blocks { get; set; }

		public int Passes { get; set; }

		public int Exits { get; set; }

		public int Entries { get; set; }

		public int Hits { get; set; }

		public int HitsTaken { get; set; }

		public int Turnovers { get; set; }

		public int Takeaways { get; set; }

		public int PuckTouches { get; set; }

		public double PossessionTimeSeconds { get; set; }

		public double TimeOnIceSeconds { get; set; }

		public double DefensiveZonePossessionTimeSeconds { get; set; }

		public double NeutralZonePossessionTimeSeconds { get; set; }

		public double OffensiveZonePossessionTimeSeconds { get; set; }

		public double DefensiveZoneTimeOnIceSeconds { get; set; }

		public double NeutralZoneTimeOnIceSeconds { get; set; }

		public double OffensiveZoneTimeOnIceSeconds { get; set; }

		public int PostHits { get; set; }

		public int FaceoffWins { get; set; }

		public int FaceoffLosses { get; set; }

		public double Xg { get; set; }

		public int PlusMinus { get; set; }

		public int ShotsFaced { get; set; }

		public int Saves { get; set; }

		public int GoalsAllowed { get; set; }

		public double SavePercentage { get; set; }

		public int BodySaves { get; set; }

		public int PadSaves { get; set; }

		public int StickSaves { get; set; }

		public double Xga { get; set; }

		public double Gsax { get; set; }

		public int Points => Goals + Assists;
	}

	internal class BoxscoreTeamSnapshot
	{
		public PlayerTeam Team { get; set; }

		public string Label { get; set; } = "";

		public int Goals { get; set; }

		public int Assists { get; set; }

		public int ShotsOnGoal { get; set; }

		public double ExpectedGoals { get; set; }

		public int Saves { get; set; }

		public double ExpectedGoalsAgainst { get; set; }

	public int FaceoffWins { get; set; }

	public int FaceoffTotal { get; set; }

	public int Passes { get; set; }

	public int Hits { get; set; }

		public int Blocks { get; set; }

		public int Giveaways { get; set; }

		public int Takeaways { get; set; }

		public int ZoneEntries { get; set; }

	public int ZoneExits { get; set; }

	public double PossessionTimeSeconds { get; set; }

	public double DefensiveZonePossessionTimeSeconds { get; set; }

		public double NeutralZonePossessionTimeSeconds { get; set; }

		public double OffensiveZonePossessionTimeSeconds { get; set; }
	}

	internal class LiveBoxscoreData
	{
		public List<BoxscorePlayerSnapshot> Players { get; set; } = new List<BoxscorePlayerSnapshot>();

		public BoxscoreTeamSnapshot RedTeam { get; set; } = new BoxscoreTeamSnapshot
		{
			Team = PlayerTeam.Red,
			Label = "Red"
		};

		public BoxscoreTeamSnapshot BlueTeam { get; set; } = new BoxscoreTeamSnapshot
		{
			Team = PlayerTeam.Blue,
			Label = "Blue"
		};

		public ExpectedGoalsExportData ExpectedGoals { get; set; } = new ExpectedGoalsExportData();
	}

	internal class FrozenGameExportSnapshot
	{
		public string GameReferenceId { get; set; } = "";

		public DateTime FrozenAtUtc { get; set; } = DateTime.UtcNow;

		public int SourceEventCount { get; set; }

		public int SourceGoalCount { get; set; }

		public int SourceShootoutAttemptCount { get; set; }

		public bool SourceShootoutCompleted { get; set; }

		public int RedScore { get; set; }

		public int BlueScore { get; set; }

		public bool WasOvertime { get; set; }

		public string WinningGoalScorerSteamId { get; set; } = "";

		public PlayerTeam WinningTeam { get; set; } = PlayerTeam.None;

		public List<GoalInfo> Goals { get; set; } = new List<GoalInfo>();

		public List<BoxscoreShootoutAttemptRecord> ShootoutAttempts { get; set; } = new List<BoxscoreShootoutAttemptRecord>();

		public List<PlayByPlayEvent> PlayByPlayEvents { get; set; } = new List<PlayByPlayEvent>();

		public ExpectedGoalsExportData ExpectedGoals { get; set; } = new ExpectedGoalsExportData();

		public ResolvedPlayByPlaySummary ResolvedPlayByPlaySummary { get; set; } = new ResolvedPlayByPlaySummary();

		public LiveBoxscoreData LiveData { get; set; } = new LiveBoxscoreData();

		public Dictionary<int, string> Stars { get; set; } = new Dictionary<int, string>();

		public Dictionary<string, PlayerRoleParticipationSnapshot> PlayerRoleParticipation { get; set; } = new Dictionary<string, PlayerRoleParticipationSnapshot>(StringComparer.Ordinal);
	}

	internal class PlayerRoleParticipationSnapshot
	{
		public bool PlayedSkater { get; set; }

		public bool PlayedGoalie { get; set; }

		public string LastSkaterPosition { get; set; } = "";

		public double SkaterTimeOnIceSeconds { get; set; }

		public double GoalieTimeOnIceSeconds { get; set; }

		public double[] SkaterTimeOnIceByZone { get; set; } = new double[3];

		public double[] GoalieTimeOnIceByZone { get; set; } = new double[3];
	}

	internal enum PlayByPlayEventType
	{
		Touch,
		Takeaway,
		Turnover,
		Pass,
		Reception,
		Shot,
		Save,
		Goal,
		OwnGoal,
		Hit,
		Block,
		PuckBattle,
		Faceoff,
		FaceoffOutcome,
		DZExit,
		OZEntry,
		PostHit,
		GameEnd
	}

	internal enum EventZone
	{
		Defensive,
		Neutral,
		Offensive
	}

	private static readonly string MOD_VERSION = "1.0";

	private static readonly string SERVER_SIGNATURE_TOKEN = "domastatsbranch";
}
