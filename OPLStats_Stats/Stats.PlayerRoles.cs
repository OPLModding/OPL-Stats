using System;
using System.Collections.Generic;
using Codebase;

namespace OPLStats_Stats;

public partial class Stats
{
	private sealed class PlayerRoleTrackingState
	{
		public bool PlayedSkater;

		public bool PlayedGoalie;

		public string LastSkaterPosition = "";

		public double SkaterTimeOnIceSeconds;

		public double GoalieTimeOnIceSeconds;

		public readonly double[] SkaterTimeOnIceByZone = new double[3];

		public readonly double[] GoalieTimeOnIceByZone = new double[3];
	}

	private static readonly Dictionary<string, PlayerRoleTrackingState> _playerRoleTracking = new Dictionary<string, PlayerRoleTrackingState>(StringComparer.Ordinal);

	private static PlayerRoleTrackingState ObservePlayerRole(string steamId, string position)
	{
		if (string.IsNullOrWhiteSpace(steamId))
		{
			return null;
		}
		if (!_playerRoleTracking.TryGetValue(steamId, out PlayerRoleTrackingState value))
		{
			value = new PlayerRoleTrackingState();
			_playerRoleTracking[steamId] = value;
		}
		if (string.Equals(position, "G", StringComparison.OrdinalIgnoreCase))
		{
			value.PlayedGoalie = true;
		}
		else if (!string.IsNullOrWhiteSpace(position))
		{
			value.PlayedSkater = true;
			value.LastSkaterPosition = position;
		}
		return value;
	}

	private static void ObservePlayerRole(Player player)
	{
		if (player == null || !player)
		{
			return;
		}
		ObservePlayerRole(player.SteamId.Value.ToString(), GetPlayerPosition(player));
	}

	private static void AddPlayerRoleTime(Player player, double elapsedSeconds, EventZone zone)
	{
		if (player == null || !player || elapsedSeconds <= 0.0)
		{
			return;
		}
		string text = player.SteamId.Value.ToString();
		string playerPosition = GetPlayerPosition(player);
		PlayerRoleTrackingState playerRoleTrackingState = ObservePlayerRole(text, playerPosition);
		if (playerRoleTrackingState == null)
		{
			return;
		}
		int zoneArrayIndex = GetZoneArrayIndex(zone);
		if (string.Equals(playerPosition, "G", StringComparison.OrdinalIgnoreCase))
		{
			playerRoleTrackingState.GoalieTimeOnIceSeconds += elapsedSeconds;
			if (ServerConfig.TrackZoneBreakdowns)
			{
				playerRoleTrackingState.GoalieTimeOnIceByZone[zoneArrayIndex] += elapsedSeconds;
			}
		}
		else if (!string.IsNullOrWhiteSpace(playerPosition))
		{
			playerRoleTrackingState.SkaterTimeOnIceSeconds += elapsedSeconds;
			if (ServerConfig.TrackZoneBreakdowns)
			{
				playerRoleTrackingState.SkaterTimeOnIceByZone[zoneArrayIndex] += elapsedSeconds;
			}
		}
	}

	private static Dictionary<string, PlayerRoleParticipationSnapshot> BuildPlayerRoleParticipationSnapshot()
	{
		Dictionary<string, PlayerRoleParticipationSnapshot> dictionary = new Dictionary<string, PlayerRoleParticipationSnapshot>(StringComparer.Ordinal);
		foreach (KeyValuePair<string, PlayerRoleTrackingState> item in _playerRoleTracking)
		{
			PlayerRoleTrackingState value = item.Value;
			if (value == null)
			{
				continue;
			}
			dictionary[item.Key] = new PlayerRoleParticipationSnapshot
			{
				PlayedSkater = value.PlayedSkater,
				PlayedGoalie = value.PlayedGoalie,
				LastSkaterPosition = value.LastSkaterPosition ?? "",
				SkaterTimeOnIceSeconds = value.SkaterTimeOnIceSeconds,
				GoalieTimeOnIceSeconds = value.GoalieTimeOnIceSeconds,
				SkaterTimeOnIceByZone = (double[])value.SkaterTimeOnIceByZone.Clone(),
				GoalieTimeOnIceByZone = (double[])value.GoalieTimeOnIceByZone.Clone()
			};
		}
		return dictionary;
	}

	private static void ResetPlayerRoleTracking()
	{
		_playerRoleTracking.Clear();
	}
}
