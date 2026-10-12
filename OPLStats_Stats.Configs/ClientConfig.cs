using System;
using System.IO;
using Codebase;
using Codebase.Configs;
using Newtonsoft.Json;

namespace OPLStats_Stats.Configs;

public class ClientConfig : IConfig
{
	[JsonIgnore]
	private readonly string _configPath = GetDefaultPath();

	public bool LogInfo { get; set; } = true;

	public bool LogClientSideStats { get; set; }

	public float BoxscoreWindowWidth { get; set; } = 720f;

	public float BoxscoreWindowHeight { get; set; } = 904f;

	public double BoxscoreHintDisplaySeconds { get; set; } = 30.0;

	[JsonIgnore]
	public string ModName { get; } = "DomaStatsBranch";

	internal static string GetDefaultPath()
	{
		return Path.Combine(Path.GetFullPath("."), "config", "doma_stats", "clientconfig.json");
	}

	internal static string[] GetLegacyPaths()
	{
		return new string[1] { Path.Combine(Path.GetFullPath("."), "stats", "dalf_stats_clientconfig.json") };
	}

	public override string ToString()
	{
		return JsonConvert.SerializeObject(this, Formatting.Indented);
	}

	internal static ClientConfig SetConfig(string json)
	{
		if (string.IsNullOrWhiteSpace(json))
		{
			return new ClientConfig();
		}
		return JsonConvert.DeserializeObject<ClientConfig>(json) ?? new ClientConfig();
	}

	internal static ClientConfig ReadConfig()
	{
		ClientConfig clientConfig = new ClientConfig();
		try
		{
			if (File.Exists(clientConfig._configPath))
			{
				clientConfig = SetConfig(File.ReadAllText(clientConfig._configPath));
				Logging.Log("Client config read.", clientConfig, bypassConfig: true);
			}
			else
			{
				string[] legacyPaths = GetLegacyPaths();
				for (int i = 0; i < legacyPaths.Length; i++)
				{
					if (File.Exists(legacyPaths[i]))
					{
						clientConfig = SetConfig(File.ReadAllText(legacyPaths[i]));
						Logging.Log("Client config read from legacy path. It will be re-saved to config/doma_stats/clientconfig.json.", clientConfig, bypassConfig: true);
						break;
					}
				}
			}
			if (clientConfig.BoxscoreHintDisplaySeconds < 0.0)
			{
				clientConfig.BoxscoreHintDisplaySeconds = 30.0;
			}
			clientConfig.Save();
			return clientConfig;
		}
		catch (Exception arg)
		{
			Logging.LogError($"Can't read/write the client config file/folder. (Permission error ?)\n{arg}", clientConfig ?? new ClientConfig());
			return clientConfig ?? new ClientConfig();
		}
	}

	internal void Save()
	{
		if (string.IsNullOrEmpty(_configPath))
		{
			Logging.LogError("Can't write the client config file. (_configPath null or empty)", this);
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
			Logging.LogError($"Can't write the client config file. (Permission error ?)\n{arg}", this);
		}
		Logging.Log("Wrote client config : " + ToString(), this, bypassConfig: true);
	}
}
