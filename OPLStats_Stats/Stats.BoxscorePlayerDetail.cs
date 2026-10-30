using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Codebase;
using Newtonsoft.Json;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UIElements;

namespace OPLStats_Stats;

// Click a player's name in the boxscore "Player Stats" tab to open a detail screen for that player.
// The client asks the server for the player's detail snapshot (full stat line plus shot locations)
// and renders it in place of the tab content; the Back button returns to the Player Stats tab.
public partial class Stats
{
	internal class BoxscorePlayerShotSnapshot
	{
		public int Period { get; set; }

		public float GameTime { get; set; }

		public string Outcome { get; set; } = "";

		// Position in the rink-map frame: attacking end at +Y, X mirrored to match (see ToAttackFrame).
		public float X { get; set; }

		public float Y { get; set; }

		// Distance out from the goal line along the ice (always positive, towards centre ice).
		public float Depth { get; set; }

		public float Distance { get; set; }

		public float Angle { get; set; }

		public double Xg { get; set; }

		public bool HomePlate { get; set; }
	}

	internal class BoxscorePlayerDetailSnapshot : BoxscoreSnapshotBase
	{
		public string SteamId { get; set; } = "";

		public BoxscorePlayerSnapshot Player { get; set; } = null;

		public double StarPoints { get; set; }

		public bool ExpectedGoalsEnabled { get; set; }

		public bool ShotDataAvailable { get; set; }

		public List<BoxscorePlayerShotSnapshot> Shots { get; set; } = new List<BoxscorePlayerShotSnapshot>();

		public bool HeatDataAvailable { get; set; }

		// Flat triples of (gridX, gridZ, deciseconds) for non-empty cells, in the attack frame.
		public List<int> HeatCells { get; set; } = new List<int>();
	}

	// Player position heatmap grid. Cells are HEAT_CELL_SIZE ice units square and cover the rink symmetrically around the centre.
	private const float HEAT_CELL_SIZE = 2f;

	private const int HEAT_GRID_X = 24;

	private const int HEAT_GRID_Z = 48;

	private const float RINK_HALF_WIDTH = HEAT_GRID_X * HEAT_CELL_SIZE / 2f;

	private const float RINK_HALF_LENGTH = HEAT_GRID_Z * HEAT_CELL_SIZE / 2f;

	private static readonly LockDictionary<string, float[]> _playerHeatGrids = new LockDictionary<string, float[]>();

	private static void RecordPlayerHeatSample(string steamId, Vector3 position, float seconds)
	{
		if (string.IsNullOrWhiteSpace(steamId) || seconds <= 0f)
		{
			return;
		}
		if (!_playerHeatGrids.TryGetValue(steamId, out float[] grid) || grid == null)
		{
			grid = new float[HEAT_GRID_X * HEAT_GRID_Z];
			_playerHeatGrids[steamId] = grid;
		}
		int gx = Mathf.Clamp(Mathf.FloorToInt((position.x + RINK_HALF_WIDTH) / HEAT_CELL_SIZE), 0, HEAT_GRID_X - 1);
		int gz = Mathf.Clamp(Mathf.FloorToInt((position.z + RINK_HALF_LENGTH) / HEAT_CELL_SIZE), 0, HEAT_GRID_Z - 1);
		grid[gz * HEAT_GRID_X + gx] += seconds;
	}

	// Rotates a world position so the end the team attacks is always at +Y. Blue attacks the net at z = -40.
	private static void ToAttackFrame(bool attacksNegativeZ, float x, float z, out float frameX, out float frameY)
	{
		frameX = attacksNegativeZ ? -x : x;
		frameY = attacksNegativeZ ? -z : z;
	}

	private const string BOXSCORE_PLAYERDETAIL_REQUEST = "oomtm450_statsBOXSCORE_PLAYERDETAIL_REQUEST";

	private const string BOXSCORE_PLAYERDETAIL = "oomtm450_statsBOXSCORE_PLAYERDETAIL";

	// Keeps the serialized snapshot well under the 64 KB message limit in NetworkCommunication.SendData.
	private const int BOXSCORE_PLAYERDETAIL_MAX_SHOTS = 250;

	private const long BOXSCORE_PLAYERDETAIL_TIMEOUT_MS = 6000L;

	private static string _boxscoreDetailSteamId = null;

	private static int _boxscoreDetailRequestSequence = 0;

	private static bool _boxscoreDetailRendered = false;

	#region Server

	private static void SendBoxscorePlayerDetail(ulong clientId, string steamId)
	{
		BoxscorePlayerDetailSnapshot snapshot = BuildBoxscorePlayerDetailSnapshot(steamId);
		NetworkCommunication.SendData(BOXSCORE_PLAYERDETAIL, JsonConvert.SerializeObject(snapshot), clientId, "oomtm450_stats_server", ServerConfig);
	}

	private static BoxscorePlayerDetailSnapshot BuildBoxscorePlayerDetailSnapshot(string steamId)
	{
		BoxscorePlayerDetailSnapshot snapshot = new BoxscorePlayerDetailSnapshot
		{
			RequestedTab = BOXSCORE_TAB_PLAYERSTATS,
			SteamId = steamId ?? "",
			AvailableTabs = BuildAvailableTabsForCurrentGameBoxscore()
		};
		try
		{
			bool isPreviousGame = ShouldServePreviousGameSnapshotFromServer() && _serverPreviousGameSnapshot?.LiveData != null;
			LiveBoxscoreData liveData;
			if (isPreviousGame)
			{
				liveData = _serverPreviousGameSnapshot.LiveData;
				snapshot.Summary = _serverPreviousGameSnapshot.Summary ?? "";
			}
			else
			{
				liveData = _currentGameShootoutBoxscoreState != null && _currentGameShootoutBoxscoreState.HasShootoutOccurred && _currentGameShootoutBoxscoreState.FrozenLiveData != null
					? _currentGameShootoutBoxscoreState.FrozenLiveData
					: BuildLiveBoxscoreData();
				snapshot.Summary = BuildBoxscoreSummary();
			}
			BoxscorePlayerSnapshot player = liveData?.Players?.FirstOrDefault((BoxscorePlayerSnapshot p) => string.Equals(p.SteamId, steamId, StringComparison.Ordinal));
			if (player == null)
			{
				snapshot.Error = "Player not found.";
				return snapshot;
			}
			snapshot.Player = player;
			snapshot.StarPoints = CalculateCurrentStarPoints(player);
			snapshot.ExpectedGoalsEnabled = liveData.ExpectedGoals != null && liveData.ExpectedGoals.Enabled;
			// Play-by-play events and position samples are not kept in the previous-game snapshot, so the maps are only available for the current game.
			bool attacksNegativeZ = player.Team == PlayerTeam.Blue;
			if (!isPreviousGame && _playerHeatGrids.TryGetValue(steamId, out float[] heatGrid) && heatGrid != null)
			{
				for (int gz = 0; gz < HEAT_GRID_Z; gz++)
				{
					for (int gx = 0; gx < HEAT_GRID_X; gx++)
					{
						int deciseconds = (int)Math.Round(heatGrid[gz * HEAT_GRID_X + gx] * 10f);
						if (deciseconds <= 0)
						{
							continue;
						}
						// The grid is symmetric about the centre, so a 180 degree rotation maps cell (gx, gz) to (max - gx, max - gz).
						snapshot.HeatCells.Add(attacksNegativeZ ? HEAT_GRID_X - 1 - gx : gx);
						snapshot.HeatCells.Add(attacksNegativeZ ? HEAT_GRID_Z - 1 - gz : gz);
						snapshot.HeatCells.Add(deciseconds);
					}
				}
				snapshot.HeatDataAvailable = snapshot.HeatCells.Count > 0;
			}
			if (!isPreviousGame && !player.IsGoalie)
			{
				snapshot.ShotDataAvailable = true;
				List<PlayByPlayEvent> events = GetOrBuildCachedPlayByPlayAnalytics().OrderedEvents ?? new List<PlayByPlayEvent>();
				List<BoxscorePlayerShotSnapshot> shots = new List<BoxscorePlayerShotSnapshot>();
				foreach (PlayByPlayEvent e in events)
				{
					if (e.EventType != PlayByPlayEventType.Shot || !string.Equals(e.PlayerSteamId, steamId, StringComparison.Ordinal))
					{
						continue;
					}
					double netZ = e.PlayerTeam == (int)PlayerTeam.Blue ? -40.0 : 40.0;
					double depth = Math.Abs(netZ - e.Position.z);
					double lateral = Math.Abs(e.Position.x);
					bool homePlate = (e.Flags ?? "").IndexOf("HomePlate", StringComparison.OrdinalIgnoreCase) >= 0 || string.Equals(DetermineShotFlag(e.Position, (PlayerTeam)e.PlayerTeam), "HomePlate", StringComparison.OrdinalIgnoreCase);
					ToAttackFrame(e.PlayerTeam == (int)PlayerTeam.Blue, e.Position.x, e.Position.z, out float frameX, out float frameY);
					shots.Add(new BoxscorePlayerShotSnapshot
					{
						Period = e.Period,
						GameTime = (float)Math.Round(e.GameTime, 1),
						Outcome = e.Outcome ?? "",
						X = (float)Math.Round(frameX, 1),
						Y = (float)Math.Round(frameY, 1),
						Depth = (float)Math.Round(depth, 1),
						Distance = (float)Math.Round(Math.Sqrt(lateral * lateral + depth * depth), 1),
						Angle = (float)Math.Round(Math.Atan2(lateral, Math.Max(0.1, depth)) * 180.0 / Math.PI, 1),
						Xg = Math.Round(e.Xg, 4),
						HomePlate = homePlate
					});
				}
				if (shots.Count > BOXSCORE_PLAYERDETAIL_MAX_SHOTS)
				{
					shots = shots.Skip(shots.Count - BOXSCORE_PLAYERDETAIL_MAX_SHOTS).ToList();
				}
				snapshot.Shots = shots;
			}
		}
		catch (Exception arg)
		{
			snapshot.Error = "Couldn't build player details.";
			Logging.LogError($"Error building player detail snapshot for {steamId}: {arg}", ServerConfig);
		}
		return snapshot;
	}

	#endregion

	#region Client

	private static void ClearBoxscorePlayerDetailState()
	{
		_boxscoreDetailSteamId = null;
		_boxscoreDetailRendered = false;
		_boxscoreDetailRequestSequence++;
	}

	private static void OpenBoxscorePlayerDetail(string steamId)
	{
		if (!_boxscoreVisible || string.IsNullOrWhiteSpace(steamId))
		{
			return;
		}
		try
		{
			_boxscoreRequestSequence++;
			_boxscoreDetailSteamId = steamId;
			_boxscoreDetailRendered = false;
			int sequence = ++_boxscoreDetailRequestSequence;
			if (NetworkManager.Singleton == null || NetworkManager.Singleton.CustomMessagingManager == null)
			{
				ShowBoxscoreDetailMessage("Player details unavailable: no active connection.");
				return;
			}
			SetBoxscoreLoadingState("Loading player details...");
			NetworkCommunication.SendData(BOXSCORE_PLAYERDETAIL_REQUEST, steamId, 0uL, "oomtm450_stats_client", _clientConfig);
			VisualElement scheduleRoot = _boxscoreOverlay ?? _boxscoreContentRoot;
			scheduleRoot?.schedule.Execute((Action)delegate
			{
				if (_boxscoreVisible && !_boxscoreDetailRendered && sequence == _boxscoreDetailRequestSequence && _boxscoreDetailSteamId == steamId)
				{
					ShowBoxscoreDetailMessage("Player details unavailable: the server didn't respond.");
				}
			}).ExecuteLater(BOXSCORE_PLAYERDETAIL_TIMEOUT_MS);
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error opening player detail: {arg}", _clientConfig);
			ShowBoxscoreDetailMessage("Player details request failed.");
		}
	}

	private static void CloseBoxscorePlayerDetail()
	{
		ClearBoxscorePlayerDetailState();
		_renderedBoxscorePayload = null;
		_renderedBoxscoreTab = null;
		_renderedBoxscoreFirstChild = null;
		RequestBoxscoreSnapshot(_boxscoreActiveTab);
	}

	private static Button CreateBoxscoreDetailBackButton()
	{
		Button button = new Button(CloseBoxscorePlayerDetail)
		{
			text = "< Back to " + BOXSCORE_TAB_PLAYERSTATS,
			name = "DomaStats_BoxscoreDetailBack"
		};
		button.style.minHeight = 30f;
		button.style.paddingLeft = 12f;
		button.style.paddingRight = 12f;
		button.style.marginRight = 14f;
		button.style.fontSize = 14.5f;
		button.style.unityTextAlign = TextAnchor.MiddleCenter;
		button.style.backgroundColor = new Color(0.12f, 0.15f, 0.2f, 0.96f);
		button.style.color = new Color(0.9f, 0.93f, 0.98f, 1f);
		button.style.borderTopWidth = 1f;
		button.style.borderBottomWidth = 1f;
		button.style.borderLeftWidth = 1f;
		button.style.borderRightWidth = 1f;
		Color border = new Color(0.8f, 0.83f, 0.89f, 0.35f);
		button.style.borderTopColor = border;
		button.style.borderBottomColor = border;
		button.style.borderLeftColor = border;
		button.style.borderRightColor = border;
		return button;
	}

	private static void ShowBoxscoreDetailMessage(string message)
	{
		if (_boxscoreContentRoot == null)
		{
			return;
		}
		_boxscoreContentRoot.Clear();
		VisualElement row = new VisualElement();
		row.style.flexDirection = FlexDirection.Row;
		row.style.alignItems = Align.Center;
		row.style.marginBottom = 12f;
		row.Add(CreateBoxscoreDetailBackButton());
		_boxscoreContentRoot.Add(row);
		_boxscoreContentRoot.Add(CreateBoxscoreBodyLabel(message, 14f, new Color(1f, 0.72f, 0.72f, 1f)));
	}

	private static void ReceiveBoxscorePlayerDetail(string payload)
	{
		if (!_boxscoreVisible || _boxscoreDetailSteamId == null)
		{
			return;
		}
		BoxscorePlayerDetailSnapshot snapshot = null;
		try
		{
			snapshot = JsonConvert.DeserializeObject<BoxscorePlayerDetailSnapshot>(payload);
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error parsing player detail snapshot: {arg}", _clientConfig);
		}
		if (snapshot == null || !_boxscoreVisible || _boxscoreDetailSteamId == null || !string.Equals(snapshot.SteamId, _boxscoreDetailSteamId, StringComparison.Ordinal))
		{
			return;
		}
		UpdateClientBoxscoreAvailableTabsFromSnapshot(snapshot);
		RenderBoxscorePlayerDetail(snapshot);
		_boxscoreDetailRendered = true;
	}

	private static void MakeBoxscorePlayerNameClickable(VisualElement cell, string steamId)
	{
		Label label = cell as Label;
		if (label == null || string.IsNullOrWhiteSpace(steamId))
		{
			return;
		}
		Color normal = new Color(0.66f, 0.82f, 1f, 1f);
		Color hover = new Color(1f, 1f, 1f, 1f);
		label.style.color = normal;
		label.RegisterCallback<MouseEnterEvent>(delegate
		{
			label.style.color = hover;
			label.style.unityFontStyleAndWeight = FontStyle.Bold;
		});
		label.RegisterCallback<MouseLeaveEvent>(delegate
		{
			label.style.color = normal;
			label.style.unityFontStyleAndWeight = FontStyle.Normal;
		});
		label.RegisterCallback<ClickEvent>(delegate
		{
			OpenBoxscorePlayerDetail(steamId);
		});
	}

	private static VisualElement CreateBoxscoreDetailTile(string label, string value)
	{
		VisualElement tile = new VisualElement();
		tile.style.width = 108f;
		tile.style.marginRight = 8f;
		tile.style.marginBottom = 8f;
		tile.style.paddingTop = 8f;
		tile.style.paddingBottom = 8f;
		tile.style.paddingLeft = 10f;
		tile.style.paddingRight = 10f;
		tile.style.backgroundColor = new Color(0.12f, 0.15f, 0.2f, 0.9f);
		Label valueLabel = new Label(value);
		valueLabel.style.fontSize = 22f;
		valueLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
		valueLabel.style.color = new Color(0.97f, 0.98f, 1f, 1f);
		Label nameLabel = new Label(label);
		nameLabel.style.fontSize = 12f;
		nameLabel.style.color = new Color(0.72f, 0.77f, 0.86f, 1f);
		tile.Add(valueLabel);
		tile.Add(nameLabel);
		return tile;
	}

	private static VisualElement CreateBoxscoreDetailTileGrid(params (string Label, string Value)[] tiles)
	{
		VisualElement grid = new VisualElement();
		grid.style.flexDirection = FlexDirection.Row;
		grid.style.flexWrap = Wrap.Wrap;
		grid.style.marginLeft = 10f;
		grid.style.marginRight = 10f;
		grid.style.marginBottom = 14f;
		foreach ((string Label, string Value) tile in tiles)
		{
			grid.Add(CreateBoxscoreDetailTile(tile.Label, tile.Value));
		}
		return grid;
	}

	private static VisualElement CreateBoxscoreDetailZoneBar(string title, double defensive, double neutral, double offensive)
	{
		VisualElement container = new VisualElement();
		container.style.marginLeft = 10f;
		container.style.marginRight = 10f;
		container.style.marginBottom = 14f;
		Label titleLabel = new Label(title);
		titleLabel.style.fontSize = 13f;
		titleLabel.style.color = new Color(0.78f, 0.82f, 0.9f, 1f);
		titleLabel.style.marginBottom = 4f;
		container.Add(titleLabel);
		double total = Math.Max(0.0, defensive) + Math.Max(0.0, neutral) + Math.Max(0.0, offensive);
		if (total <= 0.0)
		{
			container.Add(CreateBoxscoreBodyLabel("No data yet.", 13f));
			return container;
		}
		Color[] colors = { new Color(0.78f, 0.42f, 0.38f, 1f), new Color(0.55f, 0.58f, 0.66f, 1f), new Color(0.35f, 0.7f, 0.5f, 1f) };
		string[] names = { "D-Zone", "N-Zone", "O-Zone" };
		double[] values = { Math.Max(0.0, defensive), Math.Max(0.0, neutral), Math.Max(0.0, offensive) };
		VisualElement bar = new VisualElement();
		bar.style.flexDirection = FlexDirection.Row;
		bar.style.height = 14f;
		bar.style.marginBottom = 4f;
		VisualElement legend = new VisualElement();
		legend.style.flexDirection = FlexDirection.Row;
		legend.style.flexWrap = Wrap.Wrap;
		for (int i = 0; i < 3; i++)
		{
			VisualElement segment = new VisualElement();
			segment.style.flexGrow = (float)Math.Max(values[i], 0.0001);
			segment.style.flexBasis = 0f;
			segment.style.backgroundColor = colors[i];
			bar.Add(segment);
			Label legendLabel = new Label($"{names[i]} {FormatTimeAsMinutesSeconds(values[i])} ({values[i] / total * 100.0:0}%)");
			legendLabel.style.fontSize = 13f;
			legendLabel.style.color = colors[i];
			legendLabel.style.marginRight = 16f;
			legend.Add(legendLabel);
		}
		container.Add(bar);
		container.Add(legend);
		return container;
	}

	private static Color GetBoxscoreShotOutcomeColor(string outcome)
	{
		switch ((outcome ?? "").ToLowerInvariant())
		{
		case "goal":
			return new Color(0.3f, 0.85f, 0.4f, 1f);
		case "on net":
			return new Color(0.95f, 0.82f, 0.3f, 1f);
		case "missed":
			return new Color(0.65f, 0.68f, 0.74f, 1f);
		case "blocked":
			return new Color(0.92f, 0.5f, 0.28f, 1f);
		default:
			return new Color(0.9f, 0.9f, 0.95f, 0.8f);
		}
	}

	private static string GetBoxscoreShotOutcomeLabel(string outcome)
	{
		switch ((outcome ?? "").ToLowerInvariant())
		{
		case "goal": return "Goal";
		case "on net": return "On net";
		case "missed": return "Missed";
		case "blocked": return "Blocked";
		case "attempt": return "Attempt";
		default: return string.IsNullOrWhiteSpace(outcome) ? "-" : outcome;
		}
	}

	// The rink is drawn sideways: the length of the ice runs left to right with the attacking end (+Y) on the right,
	// and the width runs top to bottom.
	private const float RINK_MAP_SCALE = 4f;

	// Corner radius of the boards, in ice units.
	private const float RINK_CORNER_RADIUS = 9f;

	private static float RinkMapX(float y)
	{
		return (RINK_HALF_LENGTH + y) * RINK_MAP_SCALE;
	}

	private static float RinkMapY(float x)
	{
		return (RINK_HALF_WIDTH - x) * RINK_MAP_SCALE;
	}

	private static VisualElement CreateBoxscoreRinkBase()
	{
		float width = RINK_HALF_LENGTH * 2f * RINK_MAP_SCALE;
		float height = RINK_HALF_WIDTH * 2f * RINK_MAP_SCALE;
		float radius = RINK_CORNER_RADIUS * RINK_MAP_SCALE;
		VisualElement rink = new VisualElement();
		rink.style.width = width;
		rink.style.height = height;
		rink.style.minWidth = width;
		rink.style.minHeight = height;
		rink.style.overflow = Overflow.Hidden;
		rink.style.backgroundColor = new Color(0.82f, 0.88f, 0.95f, 0.1f);
		rink.style.borderTopLeftRadius = radius;
		rink.style.borderTopRightRadius = radius;
		rink.style.borderBottomLeftRadius = radius;
		rink.style.borderBottomRightRadius = radius;
		rink.style.borderTopWidth = 2f;
		rink.style.borderBottomWidth = 2f;
		rink.style.borderLeftWidth = 2f;
		rink.style.borderRightWidth = 2f;
		Color border = new Color(1f, 1f, 1f, 0.45f);
		rink.style.borderTopColor = border;
		rink.style.borderBottomColor = border;
		rink.style.borderLeftColor = border;
		rink.style.borderRightColor = border;
		AddRinkCentreCircle(rink);
		// Goal lines, blue lines and the centre line.
		AddRinkLine(rink, 40f, new Color(0.9f, 0.3f, 0.3f, 0.7f));
		AddRinkLine(rink, -40f, new Color(0.9f, 0.3f, 0.3f, 0.7f));
		AddRinkLine(rink, 13f, new Color(0.4f, 0.55f, 0.95f, 0.8f));
		AddRinkLine(rink, -13f, new Color(0.4f, 0.55f, 0.95f, 0.8f));
		AddRinkLine(rink, 0f, new Color(0.9f, 0.3f, 0.3f, 0.5f));
		AddRinkNet(rink, 40f);
		AddRinkNet(rink, -40f);
		return rink;
	}

	private static void AddRinkCentreCircle(VisualElement rink)
	{
		float diameter = 20f * RINK_MAP_SCALE;
		VisualElement circle = new VisualElement();
		circle.style.position = Position.Absolute;
		circle.style.width = diameter;
		circle.style.height = diameter;
		circle.style.left = RinkMapX(0f) - diameter / 2f;
		circle.style.top = RinkMapY(0f) - diameter / 2f;
		circle.style.borderTopLeftRadius = diameter / 2f;
		circle.style.borderTopRightRadius = diameter / 2f;
		circle.style.borderBottomLeftRadius = diameter / 2f;
		circle.style.borderBottomRightRadius = diameter / 2f;
		circle.style.borderTopWidth = 1f;
		circle.style.borderBottomWidth = 1f;
		circle.style.borderLeftWidth = 1f;
		circle.style.borderRightWidth = 1f;
		Color color = new Color(0.4f, 0.55f, 0.95f, 0.5f);
		circle.style.borderTopColor = color;
		circle.style.borderBottomColor = color;
		circle.style.borderLeftColor = color;
		circle.style.borderRightColor = color;
		rink.Add(circle);
	}

	private static void AddRinkLine(VisualElement rink, float y, Color color)
	{
		VisualElement line = new VisualElement();
		line.style.position = Position.Absolute;
		line.style.top = 0f;
		line.style.bottom = 0f;
		line.style.width = 2f;
		line.style.left = RinkMapX(y) - 1f;
		line.style.backgroundColor = color;
		rink.Add(line);
	}

	private static void AddRinkNet(VisualElement rink, float y)
	{
		VisualElement net = new VisualElement();
		float netWidth = 5f * RINK_MAP_SCALE;
		net.style.position = Position.Absolute;
		net.style.width = 5f;
		net.style.height = netWidth;
		net.style.left = RinkMapX(y) - 2.5f;
		net.style.top = RinkMapY(0f) - netWidth / 2f;
		net.style.backgroundColor = new Color(0.95f, 0.95f, 1f, 0.9f);
		rink.Add(net);
	}

	private static VisualElement CreateBoxscoreShotMap(List<BoxscorePlayerShotSnapshot> shots)
	{
		VisualElement map = CreateBoxscoreRinkBase();
		const float dot = 10f;
		foreach (BoxscorePlayerShotSnapshot shot in shots)
		{
			if (Math.Abs(shot.X) > RINK_HALF_WIDTH || Math.Abs(shot.Y) > RINK_HALF_LENGTH)
			{
				continue;
			}
			VisualElement marker = new VisualElement();
			marker.style.position = Position.Absolute;
			marker.style.width = dot;
			marker.style.height = dot;
			marker.style.left = RinkMapX(shot.Y) - dot / 2f;
			marker.style.top = RinkMapY(shot.X) - dot / 2f;
			marker.style.backgroundColor = GetBoxscoreShotOutcomeColor(shot.Outcome);
			marker.style.borderTopLeftRadius = dot / 2f;
			marker.style.borderTopRightRadius = dot / 2f;
			marker.style.borderBottomLeftRadius = dot / 2f;
			marker.style.borderBottomRightRadius = dot / 2f;
			marker.style.borderTopWidth = 1f;
			marker.style.borderBottomWidth = 1f;
			marker.style.borderLeftWidth = 1f;
			marker.style.borderRightWidth = 1f;
			Color outline = new Color(0f, 0f, 0f, 0.6f);
			marker.style.borderTopColor = outline;
			marker.style.borderBottomColor = outline;
			marker.style.borderLeftColor = outline;
			marker.style.borderRightColor = outline;
			map.Add(marker);
		}
		return map;
	}

	private static Color GetBoxscoreHeatColor(float t)
	{
		Color[] stops =
		{
			new Color(0.15f, 0.35f, 0.95f, 0.30f),
			new Color(0.2f, 0.8f, 0.85f, 0.55f),
			new Color(0.95f, 0.85f, 0.2f, 0.75f),
			new Color(0.95f, 0.2f, 0.15f, 0.90f)
		};
		float scaled = Mathf.Clamp01(t) * (stops.Length - 1);
		int index = Mathf.Min(Mathf.FloorToInt(scaled), stops.Length - 2);
		return Color.Lerp(stops[index], stops[index + 1], scaled - index);
	}

	private static VisualElement CreateBoxscoreHeatMap(List<int> cells)
	{
		VisualElement map = CreateBoxscoreRinkBase();
		float max = 0f;
		for (int i = 2; i < cells.Count; i += 3)
		{
			max = Math.Max(max, cells[i]);
		}
		if (max <= 0f)
		{
			return map;
		}
		float cellPixels = HEAT_CELL_SIZE * RINK_MAP_SCALE;
		// Heat cells go under the rink lines so the lines stay visible.
		for (int i = 0; i + 2 < cells.Count; i += 3)
		{
			int gx = cells[i];
			int gz = cells[i + 1];
			if (gx < 0 || gx >= HEAT_GRID_X || gz < 0 || gz >= HEAT_GRID_Z)
			{
				continue;
			}
			VisualElement cell = new VisualElement();
			cell.style.position = Position.Absolute;
			cell.style.width = cellPixels;
			cell.style.height = cellPixels;
			cell.style.left = gz * cellPixels;
			cell.style.top = (HEAT_GRID_X - gx - 1) * cellPixels;
			cell.style.backgroundColor = GetBoxscoreHeatColor(Mathf.Sqrt(cells[i + 2] / max));
			map.Insert(0, cell);
		}
		return map;
	}

	private static VisualElement CreateBoxscoreHeatMapLegend(double totalSeconds)
	{
		VisualElement legend = new VisualElement();
		legend.style.flexDirection = FlexDirection.Row;
		legend.style.alignItems = Align.Center;
		legend.style.marginTop = 6f;
		Label less = new Label("Less");
		less.style.fontSize = 13f;
		less.style.color = new Color(0.78f, 0.82f, 0.9f, 1f);
		less.style.marginRight = 6f;
		legend.Add(less);
		for (int i = 0; i < 6; i++)
		{
			VisualElement swatch = new VisualElement();
			swatch.style.width = 18f;
			swatch.style.height = 10f;
			swatch.style.backgroundColor = GetBoxscoreHeatColor(i / 5f);
			legend.Add(swatch);
		}
		Label more = new Label($"More   ({FormatTimeAsMinutesSeconds(totalSeconds)} tracked)");
		more.style.fontSize = 13f;
		more.style.color = new Color(0.78f, 0.82f, 0.9f, 1f);
		more.style.marginLeft = 6f;
		legend.Add(more);
		return legend;
	}

	private static VisualElement CreateBoxscoreMapBlock(string title, VisualElement map, VisualElement legend)
	{
		VisualElement block = new VisualElement();
		block.style.marginRight = 24f;
		block.style.marginBottom = 18f;
		Label titleLabel = new Label(title);
		titleLabel.style.fontSize = 14f;
		titleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
		titleLabel.style.color = new Color(0.9f, 0.93f, 0.98f, 1f);
		titleLabel.style.marginBottom = 4f;
		block.Add(titleLabel);
		block.Add(map);
		if (legend != null)
		{
			block.Add(legend);
		}
		return block;
	}

	private static void RenderBoxscorePlayerDetailMaps(VisualElement content, BoxscorePlayerDetailSnapshot snapshot, Color accent)
	{
		content.Add(CreateBoxscorePlayerStatsSectionTitle("Rink Maps (attacking end on the right)", accent));
		if (!snapshot.ShotDataAvailable && !snapshot.HeatDataAvailable)
		{
			Label note = CreateBoxscoreBodyLabel(snapshot.Player != null && snapshot.Player.IsGoalie ? "No position data yet." : "Rink maps aren't available for the previous game.", 13f);
			note.style.marginLeft = 10f;
			note.style.marginBottom = 18f;
			content.Add(note);
			return;
		}
		VisualElement row = new VisualElement();
		row.style.flexDirection = FlexDirection.Row;
		row.style.flexWrap = Wrap.Wrap;
		row.style.marginLeft = 10f;
		if (snapshot.ShotDataAvailable)
		{
			row.Add(CreateBoxscoreMapBlock("Shot locations", CreateBoxscoreShotMap(snapshot.Shots ?? new List<BoxscorePlayerShotSnapshot>()), CreateBoxscoreShotMapLegend()));
		}
		if (snapshot.HeatDataAvailable)
		{
			double totalSeconds = 0.0;
			for (int i = 2; i < snapshot.HeatCells.Count; i += 3)
			{
				totalSeconds += snapshot.HeatCells[i] / 10.0;
			}
			row.Add(CreateBoxscoreMapBlock("Time on ice heatmap", CreateBoxscoreHeatMap(snapshot.HeatCells), CreateBoxscoreHeatMapLegend(totalSeconds)));
		}
		content.Add(row);
	}

	private static VisualElement CreateBoxscoreShotMapLegend()
	{
		VisualElement legend = new VisualElement();
		legend.style.flexDirection = FlexDirection.Row;
		legend.style.flexWrap = Wrap.Wrap;
		legend.style.marginTop = 6f;
		foreach (string outcome in new[] { "goal", "on net", "missed", "blocked", "attempt" })
		{
			Label label = new Label("● " + GetBoxscoreShotOutcomeLabel(outcome));
			label.style.fontSize = 13f;
			label.style.color = GetBoxscoreShotOutcomeColor(outcome);
			label.style.marginRight = 14f;
			legend.Add(label);
		}
		return legend;
	}

	private static VisualElement CreateBoxscoreShotTable(List<BoxscorePlayerShotSnapshot> shots, bool showXg, Color accent)
	{
		string[] columns = showXg ? new[] { "Per", "Time", "Result", "Dist", "Angle", "Area", "xG" } : new[] { "Per", "Time", "Result", "Dist", "Angle", "Area" };
		float[] widths = showXg ? new[] { 54f, 70f, 90f, 66f, 70f, 100f, 66f } : new[] { 54f, 70f, 90f, 66f, 70f, 100f };
		VisualElement table = new VisualElement();
		table.style.marginLeft = 10f;
		table.style.marginRight = 10f;
		table.style.marginBottom = 22f;
		VisualElement header = new VisualElement();
		header.style.flexDirection = FlexDirection.Row;
		header.style.backgroundColor = new Color(0.16f, 0.19f, 0.24f, 0.98f);
		for (int i = 0; i < columns.Length; i++)
		{
			header.Add(CreateBoxscoreTableCell(columns[i], widths[i], isHeader: true, TextAnchor.MiddleCenter, new Color(0.98f, 0.99f, 1f, 1f), new Color(0f, 0f, 0f, 0f)));
		}
		table.Add(header);
		int rowIndex = 0;
		foreach (BoxscorePlayerShotSnapshot shot in shots)
		{
			VisualElement row = new VisualElement();
			row.style.flexDirection = FlexDirection.Row;
			row.style.backgroundColor = new Color(accent.r, accent.g, accent.b, 0.11f + ((rowIndex % 2 == 0) ? 0.03f : 0f));
			row.style.borderBottomWidth = 1f;
			row.style.borderBottomColor = new Color(1f, 1f, 1f, 0.05f);
			Color text = new Color(0.88f, 0.9f, 0.95f, 1f);
			List<string> cells = new List<string>
			{
				shot.Period.ToString(CultureInfo.InvariantCulture),
				FormatTimeAsMinutesSeconds(shot.GameTime),
				GetBoxscoreShotOutcomeLabel(shot.Outcome),
				shot.Distance.ToString("0.0", CultureInfo.InvariantCulture),
				shot.Angle.ToString("0", CultureInfo.InvariantCulture) + "°",
				shot.HomePlate ? "Home plate" : "Outside"
			};
			if (showXg)
			{
				cells.Add(shot.Xg.ToString("0.00", CultureInfo.InvariantCulture));
			}
			for (int j = 0; j < cells.Count; j++)
			{
				row.Add(CreateBoxscoreTableCell(cells[j], widths[j], isHeader: false, TextAnchor.MiddleCenter, j == 2 ? GetBoxscoreShotOutcomeColor(shot.Outcome) : text, new Color(0f, 0f, 0f, 0f)));
			}
			table.Add(row);
			rowIndex++;
		}
		return table;
	}

	private static void RenderBoxscorePlayerDetailShots(VisualElement content, BoxscorePlayerDetailSnapshot snapshot, Color accent)
	{
		content.Add(CreateBoxscorePlayerStatsSectionTitle("Shots", accent));
		if (snapshot.Shots == null || snapshot.Shots.Count == 0)
		{
			Label none = CreateBoxscoreBodyLabel("No shots recorded yet.", 14f);
			none.style.marginLeft = 10f;
			none.style.marginBottom = 18f;
			content.Add(none);
			return;
		}
		List<BoxscorePlayerShotSnapshot> shots = snapshot.Shots;
		int goals = shots.Count(s => s.Outcome == "goal");
		int onNet = shots.Count(s => s.Outcome == "on net");
		int missed = shots.Count(s => s.Outcome == "missed");
		int blocked = shots.Count(s => s.Outcome == "blocked");
		int homePlate = shots.Count(s => s.HomePlate);
		double avgDistance = shots.Average(s => (double)s.Distance);
		List<(string, string)> tiles = new List<(string, string)>
		{
			("Attempts", shots.Count.ToString(CultureInfo.InvariantCulture)),
			("Goals", goals.ToString(CultureInfo.InvariantCulture)),
			("On net", onNet.ToString(CultureInfo.InvariantCulture)),
			("Missed", missed.ToString(CultureInfo.InvariantCulture)),
			("Blocked", blocked.ToString(CultureInfo.InvariantCulture)),
			("Home plate", $"{homePlate} ({homePlate * 100.0 / shots.Count:0}%)"),
			("Avg distance", avgDistance.ToString("0.0", CultureInfo.InvariantCulture))
		};
		if (snapshot.ExpectedGoalsEnabled)
		{
			tiles.Add(("Avg xG / shot", shots.Average(s => s.Xg).ToString("0.000", CultureInfo.InvariantCulture)));
		}
		content.Add(CreateBoxscoreDetailTileGrid(tiles.ToArray()));
		content.Add(CreateBoxscoreShotTable(shots, snapshot.ExpectedGoalsEnabled, accent));
	}

	private static void RenderBoxscorePlayerDetail(BoxscorePlayerDetailSnapshot snapshot)
	{
		if (_boxscoreContentRoot == null || snapshot == null)
		{
			return;
		}
		_boxscoreContentRoot.Clear();
		if (_boxscoreSummaryLabel != null)
		{
			_boxscoreSummaryLabel.text = string.IsNullOrWhiteSpace(snapshot.Summary) ? BuildBoxscoreSummary() : snapshot.Summary;
		}
		if (!string.IsNullOrWhiteSpace(snapshot.Error) || snapshot.Player == null)
		{
			ShowBoxscoreDetailMessage(string.IsNullOrWhiteSpace(snapshot.Error) ? "Player not found." : snapshot.Error);
			return;
		}
		BoxscorePlayerSnapshot player = snapshot.Player;
		Color accent = GetTeamAccentColor(GetTeamDisplayLabel(player.Team));
		ScrollView scrollView = CreateVerticalBoxscoreScrollView();
		VisualElement header = new VisualElement();
		header.style.flexDirection = FlexDirection.Row;
		header.style.alignItems = Align.Center;
		header.style.marginBottom = 10f;
		header.Add(CreateBoxscoreDetailBackButton());
		Label nameLabel = new Label(player.Name);
		nameLabel.style.fontSize = 24f;
		nameLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
		nameLabel.style.color = accent;
		nameLabel.style.marginRight = 12f;
		Label subtitle = new Label($"{GetTeamDisplayLabel(player.Team)}  |  {(string.IsNullOrWhiteSpace(player.Position) ? (player.IsGoalie ? "G" : "-") : player.Position)}");
		subtitle.style.fontSize = 14f;
		subtitle.style.color = new Color(0.78f, 0.82f, 0.9f, 1f);
		header.Add(nameLabel);
		header.Add(subtitle);
		_boxscoreContentRoot.Add(header);
		_boxscoreContentRoot.Add(scrollView);
		VisualElement content = new VisualElement();
		content.style.flexDirection = FlexDirection.Column;
		content.style.paddingBottom = 12f;
		scrollView.Add(content);

		content.Add(CreateBoxscorePlayerStatsSectionTitle("Influence", accent));
		content.Add(CreateBoxscoreDetailTileGrid(
			("Influence", player.Influence.ToString("0.0", CultureInfo.InvariantCulture)),
			("Team share", (player.Influence / INFLUENCE_TEAM_POOL).ToString("0.0%", CultureInfo.InvariantCulture)),
			("Team pool", INFLUENCE_TEAM_POOL.ToString("0", CultureInfo.InvariantCulture))));

		if (player.IsGoalie)
		{
			content.Add(CreateBoxscorePlayerStatsSectionTitle("Goaltending", accent));
			List<(string, string)> goalieTiles = new List<(string, string)>
			{
				("Shots faced", player.ShotsFaced.ToString(CultureInfo.InvariantCulture)),
				("Saves", player.Saves.ToString(CultureInfo.InvariantCulture)),
				("Goals against", player.GoalsAllowed.ToString(CultureInfo.InvariantCulture)),
				("SV%", player.SavePercentage.ToString("0.000", CultureInfo.InvariantCulture)),
				("Body saves", player.BodySaves.ToString(CultureInfo.InvariantCulture)),
				("Pad saves", player.PadSaves.ToString(CultureInfo.InvariantCulture)),
				("Stick saves", player.StickSaves.ToString(CultureInfo.InvariantCulture))
			};
			if (snapshot.ExpectedGoalsEnabled)
			{
				goalieTiles.Add(("xGA", player.Xga.ToString("0.00", CultureInfo.InvariantCulture)));
				goalieTiles.Add(("GSAx", FormatSignedDecimal(player.Gsax, "0.00")));
			}
			content.Add(CreateBoxscoreDetailTileGrid(goalieTiles.ToArray()));
		}
		else
		{
			content.Add(CreateBoxscorePlayerStatsSectionTitle("Scoring & Shooting", accent));
			List<(string, string)> scoringTiles = new List<(string, string)>
			{
				("Goals", player.Goals.ToString(CultureInfo.InvariantCulture)),
				("Assists", player.Assists.ToString(CultureInfo.InvariantCulture)),
				("Points", player.Points.ToString(CultureInfo.InvariantCulture)),
				("SOG", player.Sog.ToString(CultureInfo.InvariantCulture)),
				("Shot attempts (iCF)", player.IndividualCorsiFor.ToString(CultureInfo.InvariantCulture)),
				("iCF%", FormatIndividualCorsiForPercentage(player.IndividualCorsiForPercentage)),
				("Post hits", player.PostHits.ToString(CultureInfo.InvariantCulture)),
				("Star points", snapshot.StarPoints.ToString("0.0", CultureInfo.InvariantCulture)),
				("+/-", player.PlusMinus > 0 ? "+" + player.PlusMinus.ToString(CultureInfo.InvariantCulture) : player.PlusMinus.ToString(CultureInfo.InvariantCulture))
			};
			if (snapshot.ExpectedGoalsEnabled)
			{
				scoringTiles.Insert(4, ("xG", player.Xg.ToString("0.00", CultureInfo.InvariantCulture)));
			}
			content.Add(CreateBoxscoreDetailTileGrid(scoringTiles.ToArray()));

			content.Add(CreateBoxscorePlayerStatsSectionTitle("Play Driving", accent));
			content.Add(CreateBoxscoreDetailTileGrid(
				("Passes", player.Passes.ToString(CultureInfo.InvariantCulture)),
				("Zone entries", player.Entries.ToString(CultureInfo.InvariantCulture)),
				("Zone exits", player.Exits.ToString(CultureInfo.InvariantCulture)),
				("Hits", player.Hits.ToString(CultureInfo.InvariantCulture)),
				("Hits taken", player.HitsTaken.ToString(CultureInfo.InvariantCulture)),
				("Blocks", player.Blocks.ToString(CultureInfo.InvariantCulture)),
				("Turnovers", player.Turnovers.ToString(CultureInfo.InvariantCulture)),
				("Takeaways", player.Takeaways.ToString(CultureInfo.InvariantCulture)),
				("Faceoffs", FormatFaceoffPercentage(player.FaceoffWins, player.FaceoffLosses))));
		}

		content.Add(CreateBoxscorePlayerStatsSectionTitle("Ice Time & Possession", accent));
		content.Add(CreateBoxscoreDetailTileGrid(
			("Time on ice", FormatTimeAsMinutesSeconds(player.TimeOnIceSeconds)),
			("Puck possession", FormatTimeAsMinutesSeconds(player.PossessionTimeSeconds)),
			("Puck touches", player.PuckTouches.ToString(CultureInfo.InvariantCulture))));
		content.Add(CreateBoxscoreDetailZoneBar("Time on ice by zone", player.DefensiveZoneTimeOnIceSeconds, player.NeutralZoneTimeOnIceSeconds, player.OffensiveZoneTimeOnIceSeconds));
		content.Add(CreateBoxscoreDetailZoneBar("Puck possession by zone", player.DefensiveZonePossessionTimeSeconds, player.NeutralZonePossessionTimeSeconds, player.OffensiveZonePossessionTimeSeconds));

		RenderBoxscorePlayerDetailMaps(content, snapshot, accent);
		if (snapshot.ShotDataAvailable)
		{
			RenderBoxscorePlayerDetailShots(content, snapshot, accent);
		}
	}

	#endregion
}
