using System;
using System.Collections.Generic;
using System.IO;
using Codebase;
using Codebase.Configs;
using Newtonsoft.Json;

namespace OPLStats_Stats.Configs;

public class ExpectedGoalsModelConfig : IConfig
{
	private static readonly string[] SupportedFeatureIds = new string[37]
	{
		"distance_to_net",
		"distance_squared",
		"lateral_distance",
		"shot_angle_deg",
		"shot_height",
		"home_plate",
		"outside",
		"offensive_zone",
		"neutral_zone",
		"defensive_zone",
		"force_magnitude",
		"puck_velocity",
		"player_speed_kph",
		"current_play_in_possession",
		"time_since_last_same_team_event",
		"distance_from_last_same_team_event",
		"lateral_change_from_last_same_team_event",
		"rebound_within_2s",
		"previous_same_team_shot_within_4s",
		"entry_within_6s",
		"same_team_event_within_2s",
		"midrange_entry",
		"sharp_close",
		"distance_spline_10",
		"distance_spline_20",
		"distance_spline_30",
		"angle_spline_15",
		"angle_spline_35",
		"angle_spline_55",
		"close_range",
		"slot_range",
		"point_range",
		"home_plate_rebound",
		"close_rebound",
		"home_plate_entry",
		"entry_rebound",
		"lateral_rebound"
	};

	[JsonIgnore]
	private readonly string _configPath = GetDefaultPath();

	public bool LogInfo { get; set; } = true;

	public bool Enabled { get; set; }

	public string ModelVersion { get; set; } = "xg_logistic_v1";

	public string TrainedThrough { get; set; } = "";

	public string Notes { get; set; } = "Runtime xG model template. Replace the intercept and feature weights with values from your latest offline training run, then set Enabled to true.";

	public string ModelType { get; set; } = "logistic";

	public double Intercept { get; set; }

	public double MinimumProbability { get; set; } = 0.0001;

	public double MaximumProbability { get; set; } = 0.9999;

	public List<string> IncludedShotOutcomes { get; set; } = new List<string> { "goal", "on net", "missed", "blocked" };

	public List<string> SupportedFeatures { get; set; } = new List<string>(SupportedFeatureIds);

	public Dictionary<string, double> Weights { get; set; } = CreateDefaultWeights();

	[JsonIgnore]
	public string ModName { get; } = "DomaStatsBranch";

	internal static string GetDefaultPath()
	{
		return Path.Combine(GetPluginDirectory(), "xg_model.json");
	}

	internal static string[] GetLegacyPaths()
	{
		string pluginDirectory = GetPluginDirectory();
		return new string[2]
		{
			Path.Combine(pluginDirectory, "stats_xg_model.json"),
			Path.Combine(pluginDirectory, "domastatsbranch_xg_model.json")
		};
	}

	private static string GetPluginDirectory()
	{
		string location = typeof(ExpectedGoalsModelConfig).Assembly.Location;
		if (!string.IsNullOrWhiteSpace(location))
		{
			string directoryName = Path.GetDirectoryName(location);
			if (!string.IsNullOrWhiteSpace(directoryName))
			{
				return directoryName;
			}
		}
		return Path.GetFullPath(".");
	}

	private static Dictionary<string, double> CreateDefaultWeights()
	{
		Dictionary<string, double> dictionary = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
		string[] supportedFeatureIds = SupportedFeatureIds;
		foreach (string key in supportedFeatureIds)
		{
			dictionary[key] = 0.0;
		}
		return dictionary;
	}

	internal void Normalize()
	{
		if (string.IsNullOrWhiteSpace(ModelVersion))
		{
			ModelVersion = "xg_logistic_v1";
		}
		if (string.IsNullOrWhiteSpace(ModelType))
		{
			ModelType = "logistic";
		}
		if (string.IsNullOrWhiteSpace(Notes))
		{
			Notes = "Runtime xG model template. Replace the intercept and feature weights with values from your latest offline training run, then set Enabled to true.";
		}
		if (IncludedShotOutcomes == null || IncludedShotOutcomes.Count == 0)
		{
			IncludedShotOutcomes = new List<string> { "goal", "on net", "missed", "blocked" };
		}
		if (SupportedFeatures == null || SupportedFeatures.Count == 0)
		{
			SupportedFeatures = new List<string>(SupportedFeatureIds);
		}
		else
		{
			SupportedFeatures = new List<string>(SupportedFeatureIds);
		}
		if (Weights == null)
		{
			Weights = CreateDefaultWeights();
		}
		else
		{
			foreach (string key in new List<string>(Weights.Keys))
			{
				if (Array.IndexOf(SupportedFeatureIds, key) < 0)
				{
					Weights.Remove(key);
				}
			}
			foreach (string supportedFeatureId in SupportedFeatureIds)
			{
				if (!Weights.ContainsKey(supportedFeatureId))
				{
					Weights[supportedFeatureId] = 0.0;
				}
			}
		}
		if (MinimumProbability < 0.0)
		{
			MinimumProbability = 0.0;
		}
		if (MaximumProbability > 1.0)
		{
			MaximumProbability = 1.0;
		}
		if (MaximumProbability < MinimumProbability)
		{
			MaximumProbability = MinimumProbability;
		}
	}

	public override string ToString()
	{
		return JsonConvert.SerializeObject(this, Formatting.Indented);
	}

	internal static ExpectedGoalsModelConfig SetConfig(string json)
	{
		if (string.IsNullOrWhiteSpace(json))
		{
			return new ExpectedGoalsModelConfig();
		}
		return JsonConvert.DeserializeObject<ExpectedGoalsModelConfig>(json) ?? new ExpectedGoalsModelConfig();
	}

	internal static ExpectedGoalsModelConfig ReadConfig()
	{
		ExpectedGoalsModelConfig expectedGoalsModelConfig = new ExpectedGoalsModelConfig();
		try
		{
			if (File.Exists(expectedGoalsModelConfig._configPath))
			{
				expectedGoalsModelConfig = SetConfig(File.ReadAllText(expectedGoalsModelConfig._configPath));
				Logging.Log("Expected goals model read.", expectedGoalsModelConfig, bypassConfig: true);
			}
			else
			{
				string[] legacyPaths = GetLegacyPaths();
				for (int i = 0; i < legacyPaths.Length; i++)
				{
					if (File.Exists(legacyPaths[i]))
					{
						expectedGoalsModelConfig = SetConfig(File.ReadAllText(legacyPaths[i]));
						Logging.Log("Expected goals model read from legacy plugin filename. Rename the bundled file to xg_model.json.", expectedGoalsModelConfig, bypassConfig: true);
						break;
					}
				}
			}
			expectedGoalsModelConfig.Normalize();
			if (!File.Exists(expectedGoalsModelConfig._configPath) && !legacyPathsExist())
			{
				Logging.LogError("Expected goals model file not found. Bundle xg_model.json beside DomaStatsBranch.dll to enable live xG/xGA/GSAx.", expectedGoalsModelConfig);
			}
			return expectedGoalsModelConfig;
		}
		catch (Exception arg)
		{
			Logging.LogError($"Can't read the expected goals model file.\n{arg}", expectedGoalsModelConfig ?? new ExpectedGoalsModelConfig());
			return expectedGoalsModelConfig ?? new ExpectedGoalsModelConfig();
		}
	}

	private static bool legacyPathsExist()
	{
		string[] legacyPaths = GetLegacyPaths();
		for (int i = 0; i < legacyPaths.Length; i++)
		{
			if (File.Exists(legacyPaths[i]))
			{
				return true;
			}
		}
		return false;
	}
}
