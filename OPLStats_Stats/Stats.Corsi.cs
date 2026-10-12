using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Codebase;

namespace OPLStats_Stats;

public partial class Stats
{
	private static void IncrementShotAttemptForPlayer(string playerSteamId, PlayerTeam team, PlayByPlayEvent shotEvent = null)
	{
		if (string.IsNullOrWhiteSpace(playerSteamId) || !IsTrackedStatTeam(team))
		{
			return;
		}
		if (!_shotAttempts.TryGetValue(playerSteamId, out var value))
		{
			_shotAttempts.Add(playerSteamId, 0);
			value = 0;
		}
		value++;
		_shotAttempts[playerSteamId] = value;
		QueueStatUpdate("oomtm450_statsSHOT_ATTEMPTS" + playerSteamId, value.ToString(CultureInfo.InvariantCulture));
		if (!_teamShotAttempts.TryGetValue(team, out var value2))
		{
			_teamShotAttempts.Add(team, 0);
			value2 = 0;
		}
		value2++;
		_teamShotAttempts[team] = value2;
		QueueStatUpdate("oomtm450_statsTEAM_SHOT_ATTEMPTS" + team, value2.ToString(CultureInfo.InvariantCulture));
		IncrementOnIceTeamShotAttempts(team, shotEvent);
	}

	private static void IncrementOnIceTeamShotAttempts(PlayerTeam team, PlayByPlayEvent shotEvent = null)
	{
		if (!IsTrackedStatTeam(team))
		{
			return;
		}
		foreach (string item in GetOnIcePlayerSteamIdsForShotAttempt(team, shotEvent))
		{
			if (string.IsNullOrWhiteSpace(item))
			{
				continue;
			}
			if (!_onIceTeamShotAttempts.TryGetValue(item, out var value))
			{
				_onIceTeamShotAttempts.Add(item, 0);
				value = 0;
			}
			value++;
			_onIceTeamShotAttempts[item] = value;
			QueueStatUpdate("oomtm450_statsON_ICE_TEAM_SHOT_ATTEMPTS" + item, value.ToString(CultureInfo.InvariantCulture));
		}
	}

	private static HashSet<string> GetOnIcePlayerSteamIdsForShotAttempt(PlayerTeam team, PlayByPlayEvent shotEvent = null)
	{
		HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
		if (shotEvent != null)
		{
			bool flag = shotEvent.PlayerTeam == (int)team;
			hashSet.UnionWith(ExtractSteamIdsFromRosterField(flag ? shotEvent.TeamForwardsSteamID : shotEvent.OpposingTeamForwardsSteamID));
			hashSet.UnionWith(ExtractSteamIdsFromRosterField(flag ? shotEvent.TeamDefencemenSteamID : shotEvent.OpposingTeamDefencemenSteamID));
			hashSet.UnionWith(ExtractSteamIdsFromRosterField(flag ? shotEvent.TeamGoalieSteamID : shotEvent.OpposingTeamGoalieSteamID));
		}
		if (hashSet.Count == 0)
		{
			foreach (Player item in MonoBehaviourSingleton<PlayerManager>.Instance?.GetPlayers() ?? new List<Player>())
			{
				if (item == null || !item || item.Team != team)
				{
					continue;
				}
				string playerPosition = GetPlayerPosition(item);
				if (!IsTrackedShotAttemptRosterPosition(playerPosition))
				{
					continue;
				}
				string text = item.SteamId.Value.ToString();
				if (!string.IsNullOrWhiteSpace(text))
				{
					hashSet.Add(text);
				}
			}
		}
		return hashSet;
	}

	private static bool IsTrackedStatTeam(PlayerTeam team)
	{
		return team == PlayerTeam.Red || team == PlayerTeam.Blue;
	}

	private static bool IsTrackedShotAttemptRosterPosition(string playerPosition)
	{
		return playerPosition == "C" || playerPosition == "LW" || playerPosition == "RW" || playerPosition == "LD" || playerPosition == "RD" || playerPosition == "G";
	}

	private static double CalculateIndividualCorsiForPercentage(int individualCorsiFor, int onIceTeamShotAttempts)
	{
		if (individualCorsiFor <= 0 || onIceTeamShotAttempts <= 0)
		{
			return 0.0;
		}
		return (double)individualCorsiFor / (double)onIceTeamShotAttempts * 100.0;
	}

	private static string FormatIndividualCorsiForPercentage(double value)
	{
		return value.ToString("0.0", CultureInfo.InvariantCulture) + "%";
	}
}
