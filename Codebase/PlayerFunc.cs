using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Codebase;

public class PlayerFunc
{
	public const string GOALIE_POSITION = "G";

	public const string LEFT_WINGER_POSITION = "LW";

	public const string CENTER_POSITION = "C";

	public const string RIGHT_WINGER_POSITION = "RW";

	public const string LEFT_DEFENDER_POSITION = "LD";

	public const string RIGHT_DEFENDER_POSITION = "RD";

	public static bool IsPlayerPlaying(Player player)
	{
		if ((bool)player && player.Role != PlayerRole.None && player.IsCharacterSpawned)
		{
			if (player.Team != PlayerTeam.Red)
			{
				return player.Team == PlayerTeam.Blue;
			}
			return true;
		}
		return false;
	}

	public static Player GetTeamGoalie(PlayerTeam team)
	{
		return MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayersByTeam(team).FirstOrDefault((Player x) => x.Role == PlayerRole.Goalie);
	}

	public static Player GetOtherTeamGoalie(PlayerTeam team)
	{
		return MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayersByTeam(TeamFunc.GetOtherTeam(team)).FirstOrDefault((Player x) => x.Role == PlayerRole.Goalie);
	}

	public static bool IsRole(PlayerPosition pPosition, PlayerRole role, bool hasToBeClaimed = true)
	{
		bool flag = pPosition.Role == role;
		if (hasToBeClaimed)
		{
			if (flag)
			{
				return pPosition.IsClaimed;
			}
			return false;
		}
		return flag;
	}

	public static bool IsAttacker(PlayerPosition pPosition, bool hasToBeClaimed = true)
	{
		return IsRole(pPosition, PlayerRole.Attacker, hasToBeClaimed);
	}

	public static bool IsGoalie(PlayerPosition pPosition, bool hasToBeClaimed = true)
	{
		return IsRole(pPosition, PlayerRole.Goalie, hasToBeClaimed);
	}

	public static bool IsGoalie(Player player)
	{
		return player.Role == PlayerRole.Goalie;
	}

	public static string GetPlayerSteamIdInPossession(int minPossessionMilliseconds, int maxPossessionMilliseconds, LockDictionary<string, Stopwatch> playersCurrentPuckTouch, LockDictionary<string, Stopwatch> lastTimeOnCollisionStayOrExitWasCalled, bool checkForChallenge = true)
	{
		int num = 0;
		string text = "";
		foreach (KeyValuePair<string, Stopwatch> item in playersCurrentPuckTouch)
		{
			if (lastTimeOnCollisionStayOrExitWasCalled.TryGetValue(item.Key, out var value) && value.ElapsedMilliseconds > maxPossessionMilliseconds)
			{
				continue;
			}
			num++;
			text = item.Key;
		}
		if (num == 1)
		{
			return text;
		}
		num = 0;
		text = "";
		long num2 = long.MaxValue;
		foreach (KeyValuePair<string, Stopwatch> item2 in playersCurrentPuckTouch)
		{
			if (lastTimeOnCollisionStayOrExitWasCalled.TryGetValue(item2.Key, out var value2) && value2.ElapsedMilliseconds > maxPossessionMilliseconds)
			{
				continue;
			}
			long elapsedMilliseconds = item2.Value.ElapsedMilliseconds;
			if (elapsedMilliseconds <= minPossessionMilliseconds)
			{
				continue;
			}
			num++;
			if (elapsedMilliseconds < num2)
			{
				num2 = elapsedMilliseconds;
				text = item2.Key;
			}
		}
		if (num > 1)
		{
			if (checkForChallenge)
			{
				return "";
			}
			return text;
		}
		if (num == 1)
		{
			return text;
		}
		return "";
	}

	public static string GetPlayerSteamIdInPossession(int minPossessionMilliseconds, int maxPossessionMilliseconds, int maxTippedMilliseconds, LockDictionary<string, Stopwatch> playersLastTimePuckPossession, LockDictionary<string, Stopwatch> playersCurrentPuckTouch, bool checkForChallenge = true)
	{
		int num = 0;
		string text = "";
		foreach (KeyValuePair<string, Stopwatch> item in playersCurrentPuckTouch)
		{
			if (playersLastTimePuckPossession.TryGetValue(item.Key, out var value) && value.ElapsedMilliseconds > maxPossessionMilliseconds)
			{
				continue;
			}
			num++;
			text = item.Key;
		}
		if (num == 1)
		{
			return text;
		}
		num = 0;
		text = "";
		long num2 = long.MaxValue;
		foreach (KeyValuePair<string, Stopwatch> item2 in playersCurrentPuckTouch)
		{
			if (playersLastTimePuckPossession.TryGetValue(item2.Key, out var value2) && value2.ElapsedMilliseconds > maxPossessionMilliseconds)
			{
				continue;
			}
			long elapsedMilliseconds = item2.Value.ElapsedMilliseconds;
			if (elapsedMilliseconds <= minPossessionMilliseconds && elapsedMilliseconds <= maxTippedMilliseconds)
			{
				continue;
			}
			num++;
			if (elapsedMilliseconds < num2)
			{
				num2 = elapsedMilliseconds;
				text = item2.Key;
			}
		}
		if (num > 1)
		{
			if (checkForChallenge)
			{
				return "";
			}
			return text;
		}
		if (num == 1)
		{
			return text;
		}
		text = "";
		num2 = long.MaxValue;
		foreach (KeyValuePair<string, Stopwatch> item3 in playersCurrentPuckTouch)
		{
			long elapsedMilliseconds2 = item3.Value.ElapsedMilliseconds;
			if (elapsedMilliseconds2 > maxTippedMilliseconds && elapsedMilliseconds2 < num2)
			{
				num2 = elapsedMilliseconds2;
				text = item3.Key;
			}
		}
		return text;
	}
}
