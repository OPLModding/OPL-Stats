using Codebase.Configs;
using UnityEngine;

namespace Codebase;

public static class Logging
{
	public static bool IsEnabled(IConfig config, bool bypassConfig = false)
	{
		return bypassConfig || config == null || config.LogInfo;
	}
	public static void Log(string msg, IConfig config, bool bypassConfig = false)
	{
		if (bypassConfig || config == null || config.LogInfo)
		{
			Debug.Log("[" + (config?.ModName ?? "?") + "] " + msg);
		}
	}

	public static void LogError(string msg, IConfig config)
	{
		Debug.LogError("[" + (config?.ModName ?? "?") + "] " + msg);
	}

	public static void LogWarning(string msg, IConfig config, bool bypassConfig = false)
	{
		if (bypassConfig || config == null || config.LogInfo)
		{
			Debug.LogWarning("[" + (config?.ModName ?? "?") + "] " + msg);
		}
	}
}
