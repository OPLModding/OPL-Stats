using System;
using System.IO;
using Codebase;
using Newtonsoft.Json;
using Unity.Netcode;

namespace OPLStats_Stats;

public partial class Stats
{
	private static DateTime _lastLiveStatsExportAtUtc = DateTime.MinValue;

	private static bool _liveStatsExportInProgress;

	private static int GetLiveStatsExportIntervalSeconds()
	{
		return Math.Max(10, Math.Min(15, ServerConfig?.LiveStatsExportIntervalSeconds ?? 15));
	}

	private static void TryExportLiveGameStats(bool force = false)
	{
		if (!ServerFunc.IsDedicatedServer() || ServerConfig == null || !ServerConfig.EnableLiveStatsExport || _playByPlayEvents.Count == 0 || _liveStatsExportInProgress)
		{
			return;
		}
		if (!force && (NetworkBehaviourSingleton<GameManager>.Instance == null || NetworkBehaviourSingleton<GameManager>.Instance.Phase != GamePhase.Play))
		{
			return;
		}
		DateTime utcNow = DateTime.UtcNow;
		if (!force && (utcNow - _lastLiveStatsExportAtUtc).TotalSeconds < GetLiveStatsExportIntervalSeconds())
		{
			return;
		}
		_lastLiveStatsExportAtUtc = utcNow;
		_liveStatsExportInProgress = true;
		try
		{
			FrozenGameExportSnapshot orCreateFrozenGameExportSnapshot = GetOrCreateFrozenGameExportSnapshot();
			Lazy<string> contents = CaptureExportJson(orCreateFrozenGameExportSnapshot);
			string text = Path.Combine(Path.GetFullPath("."), "stats", "temp");

			string text2 = string.IsNullOrWhiteSpace(orCreateFrozenGameExportSnapshot.GameReferenceId) ? GetCurrentGameReferenceIdOrCreate() : orCreateFrozenGameExportSnapshot.GameReferenceId;
			string text3 = Path.Combine(text, GetSanitizedExportFileHeaderName() + "_" + text2 + "_stats_live.json");
            _fileWork.Enqueue(() => {
                Directory.CreateDirectory(text);
                WriteAllTextAtomically(text3, contents.Value);
            }, error => {
                if (error != null) Logging.LogError($"Can't write the live stats JSON in the stats/temp folder. (Permission error ?)\n{error}", ServerConfig);
                else if (force) Logging.Log($"Live JSON exported to {text3}", ServerConfig);
            });
		}
		catch (Exception arg)
		{
			Logging.LogError($"Can't write the live stats JSON in the stats/temp folder. (Permission error ?)\n{arg}", ServerConfig);
		}
		finally
		{
			_liveStatsExportInProgress = false;
		}
	}

	private static void WriteAllTextAtomically(string path, string contents)
	{
		string text = path + ".writing";
		File.WriteAllText(text, contents);
		if (File.Exists(path))
		{
			File.Replace(text, path, null);
		}
		else
		{
			File.Move(text, path);
		}
	}

	private static void ResetLiveStatsExportState()
	{
		_lastLiveStatsExportAtUtc = DateTime.MinValue;
		_liveStatsExportInProgress = false;
	}
}
