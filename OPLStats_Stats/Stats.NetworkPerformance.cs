using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Codebase;
using Newtonsoft.Json;
using Unity.Netcode;

namespace OPLStats_Stats;

public partial class Stats
{
	private sealed class NetworkPerformanceAccumulator
	{
		internal int ApplicationSamples;
		internal double ApplicationWindowSeconds;
		internal long ApplicationFrames;
		internal double ApplicationFrameTimeWeightedSeconds;
		internal double ApplicationP95FrameTimeWeightedSeconds;
		internal double ApplicationMaxFrameTimeSeconds;

		internal int NetworkSamples;
		internal double NetworkWindowSeconds;
		internal long NetworkSentWireBytes;
		internal long NetworkReceivedWireBytes;
		internal long NetworkSentPackets;
		internal long NetworkReceivedPackets;
		internal double NetworkMaxStallSeconds;
		internal double NetworkRoundTripTimeWeightedMilliseconds;
		internal int NetworkRoundTripTimeSamples;

		internal int ObjectSynchronizationSamples;
		internal double ObjectSynchronizationWindowSeconds;
		internal long ObjectSynchronizationTicks;
		internal double ObjectSynchronizationTickDeltaWeightedSeconds;
		internal double ObjectSynchronizationP95TickDeltaWeightedSeconds;
		internal double ObjectSynchronizationMaxTickDeltaSeconds;
		internal double ObjectSynchronizationLossWeighted;
		internal long ObjectSynchronizationOutOfOrderTicks;
		internal double ObjectSynchronizationTimelineWeighted;
		internal double ObjectSynchronizationTimescaleWeighted;
		internal long ObjectSynchronizationSnaps;
		internal double ObjectSynchronizationExtrapolationWeighted;

		internal bool HasData => ApplicationSamples > 0 || NetworkSamples > 0 || ObjectSynchronizationSamples > 0;

		internal void AddApplicationDiagnostics(object diagnostics)
		{
			double windowTime = ReadDiagnosticDouble(diagnostics, "WindowTime");
			if (windowTime <= 0.0)
			{
				return;
			}
			ApplicationSamples++;
			ApplicationWindowSeconds += windowTime;
			long frameCount = Math.Max(0, ReadDiagnosticLong(diagnostics, "FrameCount"));
			ApplicationFrames += frameCount;
			ApplicationFrameTimeWeightedSeconds += ReadDiagnosticDouble(diagnostics, "AverageFrameTime") * frameCount;
			ApplicationP95FrameTimeWeightedSeconds += ReadDiagnosticDouble(diagnostics, "P95FrameTime") * Math.Max(1, frameCount);
			ApplicationMaxFrameTimeSeconds = Math.Max(ApplicationMaxFrameTimeSeconds, ReadDiagnosticDouble(diagnostics, "MaxFrameTime"));
		}

		internal void AddNetworkDiagnostics(object diagnostics, double roundTripTimeMilliseconds)
		{
			double windowTime = ReadDiagnosticDouble(diagnostics, "WindowTime");
			if (windowTime <= 0.0)
			{
				return;
			}
			NetworkSamples++;
			NetworkWindowSeconds += windowTime;
			NetworkSentWireBytes += ReadDiagnosticLong(diagnostics, "SentWireByteCount");
			NetworkReceivedWireBytes += ReadDiagnosticLong(diagnostics, "ReceivedWireByteCount");
			NetworkSentPackets += ReadDiagnosticLong(diagnostics, "SentPacketCount");
			NetworkReceivedPackets += ReadDiagnosticLong(diagnostics, "ReceivedPacketCount");
			NetworkMaxStallSeconds = Math.Max(NetworkMaxStallSeconds, ReadDiagnosticDouble(diagnostics, "MaxStallTime"));
			if (roundTripTimeMilliseconds >= 0.0 && !double.IsNaN(roundTripTimeMilliseconds) && !double.IsInfinity(roundTripTimeMilliseconds))
			{
				NetworkRoundTripTimeWeightedMilliseconds += roundTripTimeMilliseconds;
				NetworkRoundTripTimeSamples++;
			}
		}

		internal void AddObjectSynchronizationDiagnostics(object diagnostics)
		{
			double windowTime = ReadDiagnosticDouble(diagnostics, "WindowTime");
			if (windowTime <= 0.0)
			{
				return;
			}
			ObjectSynchronizationSamples++;
			ObjectSynchronizationWindowSeconds += windowTime;
			long tickCount = Math.Max(0, ReadDiagnosticLong(diagnostics, "TickCount"));
			ObjectSynchronizationTicks += tickCount;
			ObjectSynchronizationTickDeltaWeightedSeconds += ReadDiagnosticDouble(diagnostics, "AverageTickDeltaTime") * Math.Max(1, tickCount);
			ObjectSynchronizationP95TickDeltaWeightedSeconds += ReadDiagnosticDouble(diagnostics, "P95TickDeltaTime") * Math.Max(1, tickCount);
			ObjectSynchronizationMaxTickDeltaSeconds = Math.Max(ObjectSynchronizationMaxTickDeltaSeconds, ReadDiagnosticDouble(diagnostics, "MaxTickDeltaTime"));
			ObjectSynchronizationLossWeighted += ReadDiagnosticDouble(diagnostics, "TickLossPercentage") * windowTime;
			ObjectSynchronizationOutOfOrderTicks += Math.Max(0, ReadDiagnosticLong(diagnostics, "OutOfOrderTickCount"));
			ObjectSynchronizationTimelineWeighted += ReadDiagnosticDouble(diagnostics, "AverageTimelinePosition") * windowTime;
			ObjectSynchronizationTimescaleWeighted += ReadDiagnosticDouble(diagnostics, "AverageTimelineTimescale") * windowTime;
			ObjectSynchronizationSnaps += Math.Max(0, ReadDiagnosticLong(diagnostics, "SnapCount"));
			ObjectSynchronizationExtrapolationWeighted += ReadDiagnosticDouble(diagnostics, "ExtrapolationPercentage") * windowTime;
		}

		internal NetworkPerformancePeriodReport ToReport(int period, string gameReferenceId, bool isFinal)
		{
			return new NetworkPerformancePeriodReport
			{
				Period = period,
				GameReferenceId = gameReferenceId ?? "",
				Final = isFinal,
				Application = new NetworkPerformanceApplicationReport
				{
					Samples = ApplicationSamples,
					WindowSeconds = Round(ApplicationWindowSeconds),
					Frames = ApplicationFrames,
					AverageFramesPerSecond = Rate(ApplicationFrames, ApplicationWindowSeconds),
					AverageFrameTimeMs = AverageMilliseconds(ApplicationFrameTimeWeightedSeconds, ApplicationFrames),
					AverageP95FrameTimeMs = AverageMilliseconds(ApplicationP95FrameTimeWeightedSeconds, ApplicationFrames),
					MaxFrameTimeMs = RoundMilliseconds(ApplicationMaxFrameTimeSeconds)
				},
				Network = new NetworkPerformanceNetworkReport
				{
					Samples = NetworkSamples,
					WindowSeconds = Round(NetworkWindowSeconds),
					SentWireBytes = NetworkSentWireBytes,
					ReceivedWireBytes = NetworkReceivedWireBytes,
					SentPackets = NetworkSentPackets,
					ReceivedPackets = NetworkReceivedPackets,
					AverageSentKilobytesPerSecond = Rate(NetworkSentWireBytes / 1024.0, NetworkWindowSeconds),
					AverageReceivedKilobytesPerSecond = Rate(NetworkReceivedWireBytes / 1024.0, NetworkWindowSeconds),
					AverageSentPacketsPerSecond = Rate(NetworkSentPackets, NetworkWindowSeconds),
					AverageReceivedPacketsPerSecond = Rate(NetworkReceivedPackets, NetworkWindowSeconds),
					AverageRoundTripTimeMs = NetworkRoundTripTimeSamples > 0 ? Round(NetworkRoundTripTimeWeightedMilliseconds / NetworkRoundTripTimeSamples) : 0.0,
					MaxStallMs = RoundMilliseconds(NetworkMaxStallSeconds)
				},
				ObjectSynchronization = new NetworkPerformanceObjectSynchronizationReport
				{
					Samples = ObjectSynchronizationSamples,
					WindowSeconds = Round(ObjectSynchronizationWindowSeconds),
					Ticks = ObjectSynchronizationTicks,
					AverageTicksPerSecond = Rate(ObjectSynchronizationTicks, ObjectSynchronizationWindowSeconds),
					AverageTickDeltaMs = AverageMilliseconds(ObjectSynchronizationTickDeltaWeightedSeconds, ObjectSynchronizationTicks),
					AverageP95TickDeltaMs = AverageMilliseconds(ObjectSynchronizationP95TickDeltaWeightedSeconds, ObjectSynchronizationTicks),
					MaxTickDeltaMs = RoundMilliseconds(ObjectSynchronizationMaxTickDeltaSeconds),
					AverageTickLossPercentage = Rate(ObjectSynchronizationLossWeighted, ObjectSynchronizationWindowSeconds),
					OutOfOrderTicks = ObjectSynchronizationOutOfOrderTicks,
					AverageTimelinePosition = Rate(ObjectSynchronizationTimelineWeighted, ObjectSynchronizationWindowSeconds),
					AverageTimelineTimescale = Rate(ObjectSynchronizationTimescaleWeighted, ObjectSynchronizationWindowSeconds),
					Snaps = ObjectSynchronizationSnaps,
					AverageExtrapolationPercentage = Rate(ObjectSynchronizationExtrapolationWeighted, ObjectSynchronizationWindowSeconds)
				}
			};
		}

		private static double Rate(double value, double seconds)
		{
			return seconds > 0.0 ? Round(value / seconds) : 0.0;
		}

		private static double AverageMilliseconds(double weightedSeconds, long divisor)
		{
			return divisor > 0 ? Round(weightedSeconds / divisor * 1000.0) : 0.0;
		}

		private static double RoundMilliseconds(double seconds)
		{
			return Round(seconds * 1000.0);
		}

		private static double Round(double value)
		{
			return Math.Round(value, 4, MidpointRounding.AwayFromZero);
		}
	}

	private static double ReadDiagnosticDouble(object diagnostics, string memberName)
	{
		if (diagnostics == null)
		{
			return 0.0;
		}
		try
		{
			Type type = diagnostics.GetType();
			FieldInfo field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			object value = field != null ? field.GetValue(diagnostics) : type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(diagnostics, null);
			return value == null ? 0.0 : Convert.ToDouble(value, CultureInfo.InvariantCulture);
		}
		catch
		{
			return 0.0;
		}
	}

	private static long ReadDiagnosticLong(object diagnostics, string memberName)
	{
		double value = ReadDiagnosticDouble(diagnostics, memberName);
		if (value <= 0.0)
		{
			return 0L;
		}
		return value >= long.MaxValue ? long.MaxValue : (long)value;
	}

	private sealed class NetworkPerformanceStoredPlayerReport
	{
		[JsonProperty("steamId")]
		public string SteamId { get; set; } = "";

		[JsonProperty("name")]
		public string Name { get; set; } = "";

		[JsonProperty("team")]
		public string Team { get; set; } = "";

		[JsonProperty("reportedByClientId")]
		public ulong ReportedByClientId { get; set; }

		[JsonProperty("report")]
		public NetworkPerformancePeriodReport Report { get; set; } = new NetworkPerformancePeriodReport();
	}

	private sealed class NetworkPerformancePeriodReport
	{
		[JsonProperty("period")]
		public int Period { get; set; }

		[JsonProperty("gameReferenceId")]
		public string GameReferenceId { get; set; } = "";

		[JsonProperty("final")]
		public bool Final { get; set; }

		[JsonProperty("application")]
		public NetworkPerformanceApplicationReport Application { get; set; } = new NetworkPerformanceApplicationReport();

		[JsonProperty("network")]
		public NetworkPerformanceNetworkReport Network { get; set; } = new NetworkPerformanceNetworkReport();

		[JsonProperty("objectSynchronization")]
		public NetworkPerformanceObjectSynchronizationReport ObjectSynchronization { get; set; } = new NetworkPerformanceObjectSynchronizationReport();
	}

	private sealed class NetworkPerformanceApplicationReport
	{
		[JsonProperty("samples")]
		public int Samples { get; set; }

		[JsonProperty("windowSeconds")]
		public double WindowSeconds { get; set; }

		[JsonProperty("frames")]
		public long Frames { get; set; }

		[JsonProperty("averageFramesPerSecond")]
		public double AverageFramesPerSecond { get; set; }

		[JsonProperty("averageFrameTimeMs")]
		public double AverageFrameTimeMs { get; set; }

		[JsonProperty("averageP95FrameTimeMs")]
		public double AverageP95FrameTimeMs { get; set; }

		[JsonProperty("maxFrameTimeMs")]
		public double MaxFrameTimeMs { get; set; }
	}

	private sealed class NetworkPerformanceNetworkReport
	{
		[JsonProperty("samples")]
		public int Samples { get; set; }

		[JsonProperty("windowSeconds")]
		public double WindowSeconds { get; set; }

		[JsonProperty("sentWireBytes")]
		public long SentWireBytes { get; set; }

		[JsonProperty("receivedWireBytes")]
		public long ReceivedWireBytes { get; set; }

		[JsonProperty("sentPackets")]
		public long SentPackets { get; set; }

		[JsonProperty("receivedPackets")]
		public long ReceivedPackets { get; set; }

		[JsonProperty("averageSentKilobytesPerSecond")]
		public double AverageSentKilobytesPerSecond { get; set; }

		[JsonProperty("averageReceivedKilobytesPerSecond")]
		public double AverageReceivedKilobytesPerSecond { get; set; }

		[JsonProperty("averageSentPacketsPerSecond")]
		public double AverageSentPacketsPerSecond { get; set; }

		[JsonProperty("averageReceivedPacketsPerSecond")]
		public double AverageReceivedPacketsPerSecond { get; set; }

		[JsonProperty("averageRoundTripTimeMs")]
		public double AverageRoundTripTimeMs { get; set; }

		[JsonProperty("maxStallMs")]
		public double MaxStallMs { get; set; }
	}

	private sealed class NetworkPerformanceObjectSynchronizationReport
	{
		[JsonProperty("samples")]
		public int Samples { get; set; }

		[JsonProperty("windowSeconds")]
		public double WindowSeconds { get; set; }

		[JsonProperty("ticks")]
		public long Ticks { get; set; }

		[JsonProperty("averageTicksPerSecond")]
		public double AverageTicksPerSecond { get; set; }

		[JsonProperty("averageTickDeltaMs")]
		public double AverageTickDeltaMs { get; set; }

		[JsonProperty("averageP95TickDeltaMs")]
		public double AverageP95TickDeltaMs { get; set; }

		[JsonProperty("maxTickDeltaMs")]
		public double MaxTickDeltaMs { get; set; }

		[JsonProperty("averageTickLossPercentage")]
		public double AverageTickLossPercentage { get; set; }

		[JsonProperty("outOfOrderTicks")]
		public long OutOfOrderTicks { get; set; }

		[JsonProperty("averageTimelinePosition")]
		public double AverageTimelinePosition { get; set; }

		[JsonProperty("averageTimelineTimescale")]
		public double AverageTimelineTimescale { get; set; }

		[JsonProperty("snaps")]
		public long Snaps { get; set; }

		[JsonProperty("averageExtrapolationPercentage")]
		public double AverageExtrapolationPercentage { get; set; }
	}

	private sealed class NetworkPerformanceReportEnvelope
	{
		[JsonProperty("period")]
		public int Period { get; set; }

		[JsonProperty("gameReferenceId")]
		public string GameReferenceId { get; set; } = "";

		[JsonProperty("final")]
		public bool Final { get; set; }

		[JsonProperty("report")]
		public NetworkPerformancePeriodReport Report { get; set; } = new NetworkPerformancePeriodReport();
	}

	private sealed class NetworkPerformanceExportPeriod
	{
		[JsonProperty("period")]
		public int Period { get; set; }

		[JsonProperty("players")]
		public List<NetworkPerformanceStoredPlayerReport> Players { get; set; } = new List<NetworkPerformanceStoredPlayerReport>();
	}

	private static readonly Dictionary<int, NetworkPerformanceAccumulator> _clientNetworkPerformanceAccumulators = new Dictionary<int, NetworkPerformanceAccumulator>();

	private static readonly Dictionary<int, Dictionary<string, NetworkPerformanceStoredPlayerReport>> _serverNetworkPerformanceReports = new Dictionary<int, Dictionary<string, NetworkPerformanceStoredPlayerReport>>();

	private static readonly Dictionary<int, NetworkPerformancePeriodReport> _clientNetworkPerformanceSentReports = new Dictionary<int, NetworkPerformancePeriodReport>();

	private static string _clientNetworkPerformanceGameReference = "";

	private static bool _clientNetworkPerformanceFinalSent;

	private static void AddNetworkPerformanceEventListeners()
	{
		EventManager.AddEventListener("Event_Everyone_OnGameStateChanged", Event_OnNetworkPerformanceGameStateChanged);
		EventManager.AddEventListener("Event_OnApplicationDiagnostics", Event_OnNetworkPerformanceApplicationDiagnostics);
		EventManager.AddEventListener("Event_Client_OnNetworkDiagnostics", Event_OnNetworkPerformanceNetworkDiagnostics);
		EventManager.AddEventListener("Event_Client_OnObjectSynchronizationDiagnostics", Event_OnNetworkPerformanceObjectSynchronizationDiagnostics);
	}

	private static void RemoveNetworkPerformanceEventListeners()
	{
		EventManager.RemoveEventListener("Event_Everyone_OnGameStateChanged", Event_OnNetworkPerformanceGameStateChanged);
		EventManager.RemoveEventListener("Event_OnApplicationDiagnostics", Event_OnNetworkPerformanceApplicationDiagnostics);
		EventManager.RemoveEventListener("Event_Client_OnNetworkDiagnostics", Event_OnNetworkPerformanceNetworkDiagnostics);
		EventManager.RemoveEventListener("Event_Client_OnObjectSynchronizationDiagnostics", Event_OnNetworkPerformanceObjectSynchronizationDiagnostics);
	}

	private static void ResetClientNetworkPerformanceTracking()
	{
		_clientNetworkPerformanceAccumulators.Clear();
		_clientNetworkPerformanceSentReports.Clear();
		_clientNetworkPerformanceGameReference = "";
		_clientNetworkPerformanceFinalSent = false;
	}

	private static void ResetServerNetworkPerformanceTracking()
	{
		_serverNetworkPerformanceReports.Clear();
	}

	private static bool IsNetworkPerformanceLivePhase(GamePhase phase)
	{
		return phase == GamePhase.Play || phase == GamePhase.FaceOff;
	}

	private static bool IsClientNetworkPerformanceGameActive(out int period)
	{
		period = 0;
		if (ServerFunc.IsDedicatedServer() || string.IsNullOrWhiteSpace(_clientNetworkPerformanceGameReference) || NetworkBehaviourSingleton<GameManager>.Instance == null)
		{
			return false;
		}
		GameManager gameManager = NetworkBehaviourSingleton<GameManager>.Instance;
		if (!IsNetworkPerformanceLivePhase(gameManager.Phase) || gameManager.Period <= 0)
		{
			return false;
		}
		period = gameManager.Period;
		return true;
	}

	private static NetworkPerformanceAccumulator GetClientNetworkPerformanceAccumulator(int period)
	{
		if (!_clientNetworkPerformanceAccumulators.TryGetValue(period, out NetworkPerformanceAccumulator accumulator))
		{
			accumulator = new NetworkPerformanceAccumulator();
			_clientNetworkPerformanceAccumulators[period] = accumulator;
		}
		return accumulator;
	}

	private static void Event_OnNetworkPerformanceGameStateChanged(Dictionary<string, object> message)
	{
		if (ServerFunc.IsDedicatedServer() || message == null || !message.TryGetValue("oldGameState", out object oldValue) || !message.TryGetValue("newGameState", out object newValue) || !(oldValue is GameState oldState) || !(newValue is GameState newState))
		{
			return;
		}
		try
		{
			bool oldLive = IsNetworkPerformanceLivePhase(oldState.Phase);
			bool newLive = IsNetworkPerformanceLivePhase(newState.Phase);
			if (oldLive && !newLive && oldState.Period > 0)
			{
				FinalizeClientNetworkPerformancePeriod(oldState.Period, newState.Phase == GamePhase.GameOver || newState.Phase == GamePhase.PostGame || newState.Phase == GamePhase.Warmup || newState.Phase == GamePhase.PreGame);
			}
			if (newLive && newState.Period > 0)
			{
				GetClientNetworkPerformanceAccumulator(newState.Period);
			}
			if ((newState.Phase == GamePhase.Warmup || newState.Phase == GamePhase.PreGame) && _clientNetworkPerformanceFinalSent)
			{
				ResetClientNetworkPerformanceTracking();
			}
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error in Event_OnNetworkPerformanceGameStateChanged.\n{arg}", _clientConfig);
		}
	}

	private static void Event_OnNetworkPerformanceApplicationDiagnostics(Dictionary<string, object> message)
	{
		if (message == null || !message.TryGetValue("applicationDiagnostics", out object diagnostics) || diagnostics == null || !IsClientNetworkPerformanceGameActive(out int period))
		{
			return;
		}
		GetClientNetworkPerformanceAccumulator(period).AddApplicationDiagnostics(diagnostics);
	}

	private static void Event_OnNetworkPerformanceNetworkDiagnostics(Dictionary<string, object> message)
	{
		if (message == null || !message.TryGetValue("networkDiagnostics", out object diagnostics) || diagnostics == null || !IsClientNetworkPerformanceGameActive(out int period))
		{
			return;
		}
		double roundTripTimeMilliseconds = -1.0;
		try
		{
			Player localPlayer = MonoBehaviourSingleton<PlayerManager>.Instance?.GetLocalPlayer();
			if (localPlayer != null && (bool)localPlayer && localPlayer.Ping != null)
			{
				roundTripTimeMilliseconds = localPlayer.Ping.Value;
			}
		}
		catch
		{
		}
		GetClientNetworkPerformanceAccumulator(period).AddNetworkDiagnostics(diagnostics, roundTripTimeMilliseconds);
	}

	private static void Event_OnNetworkPerformanceObjectSynchronizationDiagnostics(Dictionary<string, object> message)
	{
		if (message == null || !message.TryGetValue("diagnosticsData", out object diagnostics) || diagnostics == null || !IsClientNetworkPerformanceGameActive(out int period))
		{
			return;
		}
		GetClientNetworkPerformanceAccumulator(period).AddObjectSynchronizationDiagnostics(diagnostics);
	}

	private static void Event_OnNetworkPerformanceGameReference(Dictionary<string, object> message)
	{
		if (ServerFunc.IsDedicatedServer())
		{
			return;
		}
		string gameReferenceId = message != null && message.TryGetValue("gameReferenceId", out object value) ? value?.ToString() : "";
		if (string.IsNullOrWhiteSpace(gameReferenceId))
		{
			return;
		}
		if (!string.Equals(_clientNetworkPerformanceGameReference, gameReferenceId, StringComparison.Ordinal))
		{
			ResetClientNetworkPerformanceTracking();
			_clientNetworkPerformanceGameReference = gameReferenceId;
		}
	}

	private static void FinalizeClientNetworkPerformancePeriod(int period, bool isFinal)
	{
		if (period <= 0 || string.IsNullOrWhiteSpace(_clientNetworkPerformanceGameReference))
		{
			return;
		}
		if (!_clientNetworkPerformanceAccumulators.TryGetValue(period, out NetworkPerformanceAccumulator accumulator) || !accumulator.HasData)
		{
			return;
		}
		NetworkPerformancePeriodReport report = accumulator.ToReport(period, _clientNetworkPerformanceGameReference, isFinal);
		_clientNetworkPerformanceSentReports[period] = report;
		_clientNetworkPerformanceAccumulators.Remove(period);
		SendClientNetworkPerformanceReport(report);
		if (isFinal)
		{
			_clientNetworkPerformanceFinalSent = true;
			foreach (NetworkPerformancePeriodReport previousReport in _clientNetworkPerformanceSentReports.Values.Where((NetworkPerformancePeriodReport item) => item.Period != report.Period).ToList())
			{
				SendClientNetworkPerformanceReport(previousReport);
			}
		}
	}

	private static void SendClientNetworkPerformanceReport(NetworkPerformancePeriodReport report)
	{
		if (report == null || ServerFunc.IsDedicatedServer() || NetworkManager.Singleton == null || !NetworkManager.Singleton.IsConnectedClient)
		{
			return;
		}
		try
		{
			NetworkPerformanceReportEnvelope envelope = new NetworkPerformanceReportEnvelope
			{
				Period = report.Period,
				GameReferenceId = report.GameReferenceId,
				Final = report.Final,
				Report = report
			};
			NetworkCommunication.SendData(NETWORK_PERFORMANCE_REPORT, JsonConvert.SerializeObject(envelope, Formatting.None), 0uL, "oomtm450_stats_client", _clientConfig, NetworkDelivery.ReliableFragmentedSequenced);
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error sending network performance report.\n{arg}", _clientConfig);
		}
	}

	private static void BroadcastNetworkPerformanceGameReference()
	{
		if (!ServerFunc.IsDedicatedServer() || string.IsNullOrWhiteSpace(_currentGameReferenceId) || NetworkManager.Singleton == null || !NetworkManager.Singleton.IsListening)
		{
			return;
		}
		NetworkCommunication.SendDataToAll(NETWORK_PERFORMANCE_GAME, _currentGameReferenceId, "oomtm450_stats_server", ServerConfig, NetworkDelivery.ReliableSequenced);
	}

	private static void SendNetworkPerformanceGameReference(ulong clientId)
	{
		if (!ServerFunc.IsDedicatedServer() || string.IsNullOrWhiteSpace(_currentGameReferenceId) || NetworkManager.Singleton == null || !NetworkManager.Singleton.IsListening)
		{
			return;
		}
		GameManager gameManager = NetworkBehaviourSingleton<GameManager>.Instance;
		if (gameManager == null || !IsNetworkPerformanceLivePhase(gameManager.Phase))
		{
			return;
		}
		NetworkCommunication.SendData(NETWORK_PERFORMANCE_GAME, _currentGameReferenceId, clientId, "oomtm450_stats_server", ServerConfig, NetworkDelivery.ReliableSequenced);
	}

	private static void ReceiveNetworkPerformanceReport(ulong clientId, string payload)
	{
		if (!ServerFunc.IsDedicatedServer() || clientId == 0 || string.IsNullOrWhiteSpace(payload) || payload.Length > 60000)
		{
			return;
		}
		try
		{
			NetworkPerformanceReportEnvelope envelope = JsonConvert.DeserializeObject<NetworkPerformanceReportEnvelope>(payload);
			if (envelope == null || envelope.Report == null || envelope.Period <= 0 || envelope.Period > 32 || envelope.Report.Period != envelope.Period)
			{
				return;
			}
			Player player = MonoBehaviourSingleton<PlayerManager>.Instance?.GetPlayerByClientId(clientId);
			if (player == null || !(bool)player || player.SteamId == null)
			{
				return;
			}
			string steamId = player.SteamId.Value.ToString();
			if (string.IsNullOrWhiteSpace(steamId) || string.IsNullOrWhiteSpace(_currentGameReferenceId))
			{
				return;
			}
			if (!string.IsNullOrWhiteSpace(envelope.GameReferenceId) && !string.Equals(envelope.GameReferenceId, _currentGameReferenceId, StringComparison.Ordinal))
			{
				return;
			}
			if (!string.IsNullOrWhiteSpace(envelope.Report.GameReferenceId) && !string.Equals(envelope.Report.GameReferenceId, _currentGameReferenceId, StringComparison.Ordinal))
			{
				return;
			}
			if (!_serverNetworkPerformanceReports.TryGetValue(envelope.Period, out Dictionary<string, NetworkPerformanceStoredPlayerReport> periodReports))
			{
				periodReports = new Dictionary<string, NetworkPerformanceStoredPlayerReport>(StringComparer.Ordinal);
				_serverNetworkPerformanceReports[envelope.Period] = periodReports;
			}
			periodReports[steamId] = new NetworkPerformanceStoredPlayerReport
			{
				SteamId = steamId,
				Name = player.Username?.Value.ToString() ?? steamId,
				Team = player.Team.ToString(),
				ReportedByClientId = clientId,
				Report = envelope.Report
			};
			if (envelope.Final)
			{
				ExportNetworkPerformanceReport(final: true);
			}
			else
			{
				ExportNetworkPerformanceReport(final: false);
			}
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error receiving network performance report from client {clientId}.\n{arg}", ServerConfig);
		}
	}

	private static void ExportNetworkPerformanceReport(bool final)
	{
		if (!ServerFunc.IsDedicatedServer() || _serverNetworkPerformanceReports.Count == 0 || string.IsNullOrWhiteSpace(_currentGameReferenceId))
		{
			return;
		}
		try
		{
			Dictionary<string, object> payload = BuildNetworkPerformanceExportPayload(final);
			string json = JsonConvert.SerializeObject(payload, Formatting.Indented);
			string directory = Path.Combine(Path.GetFullPath("."), final ? "stats" : Path.Combine("stats", "temp"));
			string stem = Path.Combine(directory, GetSanitizedExportFileHeaderName() + "_" + _currentGameReferenceId);
			string path = stem + (final ? "_network_performance.json" : "_network_performance_live.json");
			_fileWork.Enqueue(() =>
			{
				Directory.CreateDirectory(directory);
				WriteAllTextAtomically(path, json);
			}, error =>
			{
				if (error != null)
				{
					Logging.LogError($"Can't write network performance JSON to {path}.\n{error}", ServerConfig);
				}
				else
				{
					Logging.Log($"Network performance JSON exported to {path}", ServerConfig);
				}
			});
		}
		catch (Exception arg)
		{
			Logging.LogError($"Can't export network performance JSON.\n{arg}", ServerConfig);
		}
	}

	private static Dictionary<string, object> BuildNetworkPerformanceExportPayload(bool final)
	{
		List<NetworkPerformanceExportPeriod> periods = _serverNetworkPerformanceReports.OrderBy((KeyValuePair<int, Dictionary<string, NetworkPerformanceStoredPlayerReport>> item) => item.Key).Select((KeyValuePair<int, Dictionary<string, NetworkPerformanceStoredPlayerReport>> item) => new NetworkPerformanceExportPeriod
		{
			Period = item.Key,
			Players = item.Value.Values.OrderBy((NetworkPerformanceStoredPlayerReport player) => player.Team, StringComparer.Ordinal).ThenBy((NetworkPerformanceStoredPlayerReport player) => player.Name, StringComparer.OrdinalIgnoreCase).Select(CloneNetworkPerformanceStoredPlayerReport).ToList()
		}).ToList();
		return new Dictionary<string, object>
		{
			{ "exportType", "network_performance" },
			{ "schemaVersion", 1 },
			{ "gameReferenceId", _currentGameReferenceId },
			{ "generatedAtUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) },
			{ "final", final },
			{ "source", "client_f5_debug_overlay_diagnostics" },
			{ "periods", periods },
			{ "periodCount", periods.Count },
			{ "playerReportCount", periods.Sum((NetworkPerformanceExportPeriod period) => period.Players.Count) }
		};
	}

	private static NetworkPerformanceStoredPlayerReport CloneNetworkPerformanceStoredPlayerReport(NetworkPerformanceStoredPlayerReport source)
	{
		return new NetworkPerformanceStoredPlayerReport
		{
			SteamId = source.SteamId,
			Name = source.Name,
			Team = source.Team,
			ReportedByClientId = source.ReportedByClientId,
			Report = source.Report
		};
	}
}
