using System;
using System.Collections.Generic;
using System.IO;
using Codebase;
using Codebase.Configs;
using Newtonsoft.Json;

namespace OPLStats_Stats.Configs;

public class ServerConfig : IConfig, ISubConfig
{
	public const string CONFIG_DATA_NAME = "oomtm450_stats_config";

	[JsonIgnore]
	private readonly string _configPath = GetDefaultPath();

	public bool LogInfo { get; set; } = true;

	public float GoalieRadius { get; set; } = 0.75f;

	public float GoalieSaveCreaseSystemZDelta { get; set; } = 0.0125f;

	public int MaxTippedMilliseconds { get; set; } = 91;

	public int MinPossessionMilliseconds { get; set; } = 300;

	public int MaxPossessionMilliseconds { get; set; } = 700;

	public string FileHeaderName { get; set; } = "puck";

	public bool TrackingDiagnosticLogging { get; set; } = false;

	public int TrackingDiagnosticCooldownMilliseconds { get; set; } = 1500;

	public bool EnableExportLimit { get; set; } = true;

	public bool EnableLiveStatsExport { get; set; } = true;

	public bool WriteNetworkPerformanceJson { get; set; } = true;

	public int LiveStatsExportIntervalSeconds { get; set; } = 15;

	public bool TrackZoneBreakdowns { get; set; } = true;

	public double MaxZonePossessionGapSeconds { get; set; } = 5.0;

	public bool TrackPostHits { get; set; } = true;

	public double PostShotFreshnessSeconds { get; set; } = 5.0;

	public double PostHitCooldownSeconds { get; set; } = 0.5;

	public float PostOriginZFilter { get; set; } = 40f;

	public bool PostHitDiagnosticLogging { get; set; } = false;

	public List<string> PostNameFilters { get; set; } = new List<string> { "post", "crossbar" };

	public string BoxscoreTimeZone { get; set; } = "AEST";

	public string ExportTimeZone { get; set; } = "AEST";

	public bool EnableRemoteUpload { get; set; } = false;

	public string RemoteUploadBaseUrl { get; set; } = "https://hlo.raidev.me";

	public string RemoteUploadAuthToken { get; set; } = "";

	// auto = use the server identity's setting on the website; league / pugs / unassigned override it.
	public string RemoteUploadGameType { get; set; } = "auto";

	public int RemoteUploadTimeoutSeconds { get; set; } = 15;

	public bool WriteRemoteUploadMetadataFiles { get; set; } = true;

	[JsonIgnore]
	public string ModName { get; } = "DomaStatsBranch";

	internal static string GetDefaultPath()
	{
		return Path.Combine(Path.GetFullPath("."), "config", "doma_stats", "serverconfig.json");
	}

	internal static string[] GetLegacyPaths()
	{
		return new string[1] { Path.Combine(Path.GetFullPath("."), "oomtm450_stats_serverconfig.json") };
	}

	public void UpdateDefaultValues(ISubConfig oldConfig)
	{
		if (!(oldConfig is OldServerConfig))
		{
			throw new ArgumentException("oldConfig has to be typeof OldServerConfig.", "oldConfig");
		}
		OldServerConfig oldServerConfig = oldConfig as OldServerConfig;
		ServerConfig serverConfig = new ServerConfig();
		if (GoalieRadius == oldServerConfig.GoalieRadius)
		{
			GoalieRadius = serverConfig.GoalieRadius;
		}
		if (GoalieSaveCreaseSystemZDelta == oldServerConfig.GoalieSaveCreaseSystemZDelta)
		{
			GoalieSaveCreaseSystemZDelta = serverConfig.GoalieSaveCreaseSystemZDelta;
		}
		if (MaxTippedMilliseconds == oldServerConfig.MaxTippedMilliseconds)
		{
			MaxTippedMilliseconds = serverConfig.MaxTippedMilliseconds;
		}
		if (MinPossessionMilliseconds == oldServerConfig.MinPossessionMilliseconds)
		{
			MinPossessionMilliseconds = serverConfig.MinPossessionMilliseconds;
		}
		if (MaxPossessionMilliseconds == oldServerConfig.MaxPossessionMilliseconds)
		{
			MaxPossessionMilliseconds = serverConfig.MaxPossessionMilliseconds;
		}
		if (string.IsNullOrEmpty(FileHeaderName))
		{
			FileHeaderName = serverConfig.FileHeaderName;
		}
		if (PostNameFilters == null || PostNameFilters.Count == 0)
		{
			PostNameFilters = new List<string>(serverConfig.PostNameFilters);
		}
		if (LiveStatsExportIntervalSeconds < 10 || LiveStatsExportIntervalSeconds > 15)
		{
			LiveStatsExportIntervalSeconds = serverConfig.LiveStatsExportIntervalSeconds;
		}
		if (MaxZonePossessionGapSeconds <= 0.0)
		{
			MaxZonePossessionGapSeconds = serverConfig.MaxZonePossessionGapSeconds;
		}
		if (PostShotFreshnessSeconds <= 0.0)
		{
			PostShotFreshnessSeconds = serverConfig.PostShotFreshnessSeconds;
		}
		if (PostHitCooldownSeconds <= 0.0)
		{
			PostHitCooldownSeconds = serverConfig.PostHitCooldownSeconds;
		}
		if (PostOriginZFilter <= 0f)
		{
			PostOriginZFilter = serverConfig.PostOriginZFilter;
		}
		if (string.IsNullOrWhiteSpace(BoxscoreTimeZone))
		{
			BoxscoreTimeZone = serverConfig.BoxscoreTimeZone;
		}
		if (string.IsNullOrWhiteSpace(ExportTimeZone))
		{
			ExportTimeZone = !string.IsNullOrWhiteSpace(BoxscoreTimeZone) ? BoxscoreTimeZone : serverConfig.ExportTimeZone;
		}
		if (string.IsNullOrWhiteSpace(RemoteUploadBaseUrl))
		{
			RemoteUploadBaseUrl = serverConfig.RemoteUploadBaseUrl;
		}
		if (RemoteUploadTimeoutSeconds < 1)
		{
			RemoteUploadTimeoutSeconds = serverConfig.RemoteUploadTimeoutSeconds;
		}
		if (RemoteUploadAuthToken == null)
		{
			RemoteUploadAuthToken = "";
		}
		if (string.IsNullOrWhiteSpace(RemoteUploadGameType))
		{
			RemoteUploadGameType = serverConfig.RemoteUploadGameType;
		}
	}

	public override string ToString()
	{
		return JsonConvert.SerializeObject(this, Formatting.Indented);
	}

	internal static ServerConfig SetConfig(string json)
	{
		if (string.IsNullOrWhiteSpace(json))
		{
			return new ServerConfig();
		}
		return JsonConvert.DeserializeObject<ServerConfig>(json) ?? new ServerConfig();
	}

	internal static ServerConfig ReadConfig()
	{
		ServerConfig serverConfig = new ServerConfig();
		try
		{
			if (File.Exists(serverConfig._configPath))
			{
				serverConfig = SetConfig(File.ReadAllText(serverConfig._configPath));
				Logging.Log("Server config read.", serverConfig, bypassConfig: true);
			}
			else
			{
				string[] legacyPaths = GetLegacyPaths();
				for (int i = 0; i < legacyPaths.Length; i++)
				{
					if (File.Exists(legacyPaths[i]))
					{
						serverConfig = SetConfig(File.ReadAllText(legacyPaths[i]));
						Logging.Log("Server config read from legacy path. It will be re-saved to config/doma_stats/serverconfig.json.", serverConfig, bypassConfig: true);
						break;
					}
				}
			}
			serverConfig.UpdateDefaultValues(new OldServerConfig());
			serverConfig.Save();
		}
		catch (Exception arg2)
		{
			Logging.LogError($"Can't read the server config file/folder. (Permission error ?)\n{arg2}", serverConfig);
		}
		return serverConfig;
	}

	internal void Save()
	{
		if (string.IsNullOrEmpty(_configPath))
		{
			Logging.LogError("Can't write the server config file. (_configPath null or empty)", this);
			return;
		}
		try
		{
			string directoryName = Path.GetDirectoryName(_configPath);
			if (!string.IsNullOrEmpty(directoryName) && !Directory.Exists(directoryName))
			{
				Directory.CreateDirectory(directoryName);
			}
			File.WriteAllText(_configPath, ToString());
		}
		catch (Exception arg)
		{
			Logging.LogError($"Can't write the server config file. (Permission error ?)\n{arg}", this);
			return;
		}
		Logging.Log($"Wrote server config : {this}", this, bypassConfig: true);
	}
}
