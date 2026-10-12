using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Codebase;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;

namespace OPLStats_Stats;

public partial class Stats
{
	[HarmonyPatch(typeof(GameManager), "Server_NotifyGoalScoredRpc")]
	public class GameManager_Server_NotifyGoalScored_Patch
	{
		[HarmonyPrefix]
		public static bool Prefix(PlayerTeam byTeam, NetworkObjectReference goalPlayerNetworkObjectReference, NetworkObjectReference assistPlayerNetworkObjectReference, NetworkObjectReference secondAssistPlayerNetworkObjectReference, NetworkObjectReference puckNetworkObjectReference)
		{
			try
			{
				if (!ServerFunc.IsDedicatedServer() || RulesetModEnabled() || !_logic)
				{
					return true;
				}
				ResolveGoalNotificationParticipants(goalPlayerNetworkObjectReference, assistPlayerNetworkObjectReference, secondAssistPlayerNetworkObjectReference, puckNetworkObjectReference, out var goalPlayer, out var assistPlayer, out var secondAssistPlayer, out var puck);
				if (WasGoalRecentlyPreProcessed(byTeam, puck))
				{
					return true;
				}
				MarkRecentGoalPreProcessing(byTeam, puck);
				RecoverGoalParticipantsFromTracking(byTeam, ref goalPlayer, ref assistPlayer, ref secondAssistPlayer);
				PrepareCurrentGoalTrackingState(byTeam, goalPlayer, announceOwnGoal: true);
				if (goalPlayer != null && (bool)goalPlayer)
				{
					SendSavePercDuringGoal(byTeam, SendSOGDuringGoal(goalPlayer));
				}
				else
				{
					SendSavePercDuringGoal(byTeam, saveWasCounted: false);
				}
			}
			catch (Exception arg)
			{
				Logging.LogError($"Error in GameManager_Server_NotifyGoalScored_Patch Prefix().\n{arg}", ServerConfig);
			}
			return true;
		}

		[HarmonyPostfix]
		public static void Postfix(PlayerTeam byTeam, NetworkObjectReference goalPlayerNetworkObjectReference, NetworkObjectReference assistPlayerNetworkObjectReference, NetworkObjectReference secondAssistPlayerNetworkObjectReference, NetworkObjectReference puckNetworkObjectReference)
		{
			try
			{
				if (!ServerFunc.IsDedicatedServer() || !_logic)
				{
					return;
				}
				ResolveGoalNotificationParticipants(goalPlayerNetworkObjectReference, assistPlayerNetworkObjectReference, secondAssistPlayerNetworkObjectReference, puckNetworkObjectReference, out var goalPlayer, out var assistPlayer, out var secondAssistPlayer, out var puck);
				if (WasGoalHookRecentlyHandled(byTeam, goalPlayer, assistPlayer, secondAssistPlayer, puck))
				{
					return;
				}
				RecoverGoalParticipantsFromTracking(byTeam, ref goalPlayer, ref assistPlayer, ref secondAssistPlayer);
				PrepareCurrentGoalTrackingState(byTeam, goalPlayer, announceOwnGoal: false);
				FinalizeGoalTracking(byTeam, goalPlayer, assistPlayer, secondAssistPlayer, puck);
				MarkRecentGoalHookInvocation(byTeam, goalPlayer, assistPlayer, secondAssistPlayer, puck);
			}
			catch (Exception arg)
			{
				Logging.LogError($"Error in GameManager_Server_NotifyGoalScored_Patch Postfix().\n{arg}", ServerConfig);
			}
		}
	}

	private static void ResolveGoalNotificationParticipants(NetworkObjectReference goalPlayerNetworkObjectReference, NetworkObjectReference assistPlayerNetworkObjectReference, NetworkObjectReference secondAssistPlayerNetworkObjectReference, NetworkObjectReference puckNetworkObjectReference, out Player goalPlayer, out Player assistPlayer, out Player secondAssistPlayer, out Puck puck)
	{
		goalPlayer = NetworkingUtils.GetPlayerFromNetworkObjectReference(goalPlayerNetworkObjectReference);
		assistPlayer = NetworkingUtils.GetPlayerFromNetworkObjectReference(assistPlayerNetworkObjectReference);
		secondAssistPlayer = NetworkingUtils.GetPlayerFromNetworkObjectReference(secondAssistPlayerNetworkObjectReference);
		puck = NetworkingUtils.GetPuckFromNetworkObjectReference(puckNetworkObjectReference);
	}

	private static void RecoverGoalParticipantsFromTracking(PlayerTeam byTeam, ref Player goalPlayer, ref Player assistPlayer, ref Player secondAssistPlayer)
	{
		if ((goalPlayer == null || !(bool)goalPlayer) && MonoBehaviourSingleton<PlayerManager>.Instance != null)
		{
			goalPlayer = ResolveTrackedAttackingPlayer(byTeam);
		}
		if (goalPlayer != null && (bool)goalPlayer)
		{
			List<Player> list = ResolveGoalAssistCandidates(byTeam, goalPlayer);
			if ((assistPlayer == null || !(bool)assistPlayer) && list.Count > 0)
			{
				assistPlayer = list[0];
			}
			if ((secondAssistPlayer == null || !(bool)secondAssistPlayer) && list.Count > 1)
			{
				secondAssistPlayer = list[1];
			}
		}
		SanitizeGoalParticipants(ref goalPlayer, ref assistPlayer, ref secondAssistPlayer);
	}

	private static Player ResolveTrackedAttackingPlayer(PlayerTeam byTeam)
	{
		if (MonoBehaviourSingleton<PlayerManager>.Instance == null || !_lastPlayerOnPuckTipIncludedSteamId.TryGetValue(byTeam, out var value) || string.IsNullOrWhiteSpace(value.SteamId))
		{
			return null;
		}
		Player playerBySteamId = MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayerBySteamId(value.SteamId);
		if (playerBySteamId == null || !(bool)playerBySteamId || playerBySteamId.Team != byTeam)
		{
			return null;
		}
		return playerBySteamId;
	}

	private static List<Player> ResolveGoalAssistCandidates(PlayerTeam byTeam, Player goalPlayer)
	{
		List<Player> list = new List<Player>();
		if (goalPlayer == null || !(bool)goalPlayer || MonoBehaviourSingleton<PlayerManager>.Instance == null || _playByPlayEvents.Count == 0)
		{
			return list;
		}
		string text = goalPlayer.SteamId.Value.ToString();
		HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal)
		{
			text
		};
		int num = FindRecentShotIndexForGoal(goalPlayer);
		if (num >= 0)
		{
			PlayByPlayEvent playByPlayEvent = _playByPlayEvents[num];
			for (int i = num - 1; i >= 0 && list.Count < 2; i--)
			{
				PlayByPlayEvent playByPlayEvent2 = _playByPlayEvents[i];
				if (IsGoalAssistBoundaryEvent(playByPlayEvent, playByPlayEvent2))
				{
					break;
				}
				if (!IsGoalAssistCandidateEvent(playByPlayEvent2) || playByPlayEvent2.PlayerTeam != (int)byTeam || string.IsNullOrWhiteSpace(playByPlayEvent2.PlayerSteamId) || hashSet.Contains(playByPlayEvent2.PlayerSteamId) || !IsEligibleSkaterForAssistCredit(playByPlayEvent2))
				{
					continue;
				}
				Player playerBySteamId = MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayerBySteamId(playByPlayEvent2.PlayerSteamId);
				if (playerBySteamId != null && (bool)playerBySteamId)
				{
					hashSet.Add(playByPlayEvent2.PlayerSteamId);
					list.Add(playerBySteamId);
				}
			}
		}
		if (list.Count >= 2)
		{
			return list;
		}
		float currentGameTime = GetCurrentGameTime();
		int currentPeriod = GetCurrentPeriod();
		for (int j = _playByPlayEvents.Count - 1; j >= 0 && list.Count < 2; j--)
		{
			PlayByPlayEvent playByPlayEvent3 = _playByPlayEvents[j];
			if (playByPlayEvent3 == null)
			{
				continue;
			}
			if (playByPlayEvent3.Period != currentPeriod)
			{
				break;
			}
			if (currentGameTime - playByPlayEvent3.GameTime > 12f)
			{
				break;
			}
			if (playByPlayEvent3.PlayerTeam != (int)byTeam)
			{
				if (playByPlayEvent3.EventType != PlayByPlayEventType.Hit && playByPlayEvent3.EventType != PlayByPlayEventType.Block && playByPlayEvent3.EventType != PlayByPlayEventType.Save && playByPlayEvent3.EventType != PlayByPlayEventType.PostHit)
				{
					break;
				}
				continue;
			}
			if (!IsGoalAssistCandidateEvent(playByPlayEvent3) || string.IsNullOrWhiteSpace(playByPlayEvent3.PlayerSteamId) || hashSet.Contains(playByPlayEvent3.PlayerSteamId) || !IsEligibleSkaterForAssistCredit(playByPlayEvent3))
			{
				continue;
			}
			Player playerBySteamId2 = MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayerBySteamId(playByPlayEvent3.PlayerSteamId);
			if (playerBySteamId2 != null && (bool)playerBySteamId2)
			{
				hashSet.Add(playByPlayEvent3.PlayerSteamId);
				list.Add(playerBySteamId2);
			}
		}
		return list;
	}

	private static int FindRecentShotIndexForGoal(Player goalPlayer)
	{
		if (goalPlayer == null || !(bool)goalPlayer || _playByPlayEvents.Count == 0)
		{
			return -1;
		}
		string text = goalPlayer.SteamId.Value.ToString();
		float currentGameTime = GetCurrentGameTime();
		int currentPeriod = GetCurrentPeriod();
		for (int i = _playByPlayEvents.Count - 1; i >= 0; i--)
		{
			PlayByPlayEvent playByPlayEvent = _playByPlayEvents[i];
			if (playByPlayEvent == null)
			{
				continue;
			}
			if (playByPlayEvent.Period != currentPeriod)
			{
				break;
			}
			if (currentGameTime - playByPlayEvent.GameTime > 6f)
			{
				break;
			}
			if (playByPlayEvent.EventType == PlayByPlayEventType.Shot && playByPlayEvent.PlayerTeam == (int)goalPlayer.Team && string.Equals(playByPlayEvent.PlayerSteamId, text, StringComparison.Ordinal))
			{
				return i;
			}
		}
		return -1;
	}

	private static bool IsGoalAssistBoundaryEvent(PlayByPlayEvent shotEvent, PlayByPlayEvent candidateEvent)
	{
		if (candidateEvent == null || shotEvent == null || candidateEvent.Period != shotEvent.Period || IsPossessionBoundaryEvent(candidateEvent))
		{
			return true;
		}
		if (!IsSameAssistPossessionChain(shotEvent, candidateEvent))
		{
			return true;
		}
		return IsAssistShotSequenceEvent(candidateEvent);
	}

	private static bool IsSameAssistPossessionChain(PlayByPlayEvent shotEvent, PlayByPlayEvent candidateEvent)
	{
		if (shotEvent == null || candidateEvent == null)
		{
			return false;
		}
		if (!string.IsNullOrWhiteSpace(shotEvent.TeamInPossession) && !string.IsNullOrWhiteSpace(candidateEvent.TeamInPossession) && !string.Equals(shotEvent.TeamInPossession, candidateEvent.TeamInPossession, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		if (candidateEvent.PlayerTeam != shotEvent.PlayerTeam && candidateEvent.EventType != PlayByPlayEventType.Hit && candidateEvent.EventType != PlayByPlayEventType.Block && candidateEvent.EventType != PlayByPlayEventType.Save && candidateEvent.EventType != PlayByPlayEventType.PostHit)
		{
			return false;
		}
		return true;
	}

	private static bool IsAssistShotSequenceEvent(PlayByPlayEvent playByPlayEvent)
	{
		if (playByPlayEvent == null)
		{
			return false;
		}
		return playByPlayEvent.EventType == PlayByPlayEventType.Shot || playByPlayEvent.EventType == PlayByPlayEventType.Save || playByPlayEvent.EventType == PlayByPlayEventType.Block || playByPlayEvent.EventType == PlayByPlayEventType.PostHit || playByPlayEvent.EventType == PlayByPlayEventType.Goal || playByPlayEvent.EventType == PlayByPlayEventType.OwnGoal;
	}

	private static bool IsEligibleSkaterForAssistCredit(PlayByPlayEvent playByPlayEvent)
	{
		if (playByPlayEvent == null || string.IsNullOrEmpty(playByPlayEvent.PlayerSteamId))
		{
			return false;
		}
		if (playByPlayEvent.PlayerTeam != (int)PlayerTeam.Blue && playByPlayEvent.PlayerTeam != (int)PlayerTeam.Red)
		{
			return false;
		}
		return !string.Equals(playByPlayEvent.PlayerPosition, "G", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsGoalAssistCandidateEvent(PlayByPlayEvent playByPlayEvent)
	{
		if (playByPlayEvent == null)
		{
			return false;
		}
		if ((playByPlayEvent.Outcome ?? "") == "failed")
		{
			return false;
		}
		return playByPlayEvent.EventType == PlayByPlayEventType.Pass || playByPlayEvent.EventType == PlayByPlayEventType.Touch || playByPlayEvent.EventType == PlayByPlayEventType.Reception;
	}

	private static void SanitizeGoalParticipants(ref Player goalPlayer, ref Player assistPlayer, ref Player secondAssistPlayer)
	{
		if (goalPlayer != null && !(bool)goalPlayer)
		{
			goalPlayer = null;
		}
		if (assistPlayer != null && !(bool)assistPlayer)
		{
			assistPlayer = null;
		}
		if (secondAssistPlayer != null && !(bool)secondAssistPlayer)
		{
			secondAssistPlayer = null;
		}
		string text = goalPlayer?.SteamId.Value.ToString();
		string text2 = assistPlayer?.SteamId.Value.ToString();
		if (!string.IsNullOrWhiteSpace(text) && string.Equals(text2, text, StringComparison.Ordinal))
		{
			assistPlayer = null;
			text2 = null;
		}
		string text3 = secondAssistPlayer?.SteamId.Value.ToString();
		if (!string.IsNullOrWhiteSpace(text3) && (string.Equals(text3, text, StringComparison.Ordinal) || string.Equals(text3, text2, StringComparison.Ordinal)))
		{
			secondAssistPlayer = null;
			text3 = null;
		}
		if (assistPlayer == null && secondAssistPlayer != null)
		{
			assistPlayer = secondAssistPlayer;
			secondAssistPlayer = null;
		}
	}

	private static void PrepareCurrentGoalTrackingState(PlayerTeam byTeam, Player goalPlayer, bool announceOwnGoal)
	{
		_isCurrentGoalOwnGoal = goalPlayer == null || !(bool)goalPlayer;
		_currentOwnGoalPlayerSteamId = "";
		if (!_isCurrentGoalOwnGoal)
		{
			return;
		}
		Player player = ResolveOwnGoalPlayer(byTeam);
		if (player == null || !(bool)player)
		{
			return;
		}
		_currentOwnGoalPlayerSteamId = player.SteamId.Value.ToString();
		if (announceOwnGoal)
		{
			SendSystemChatMessage($"OWN GOAL BY {player.Username.Value}");
		}
	}

	private static string BuildGoalLifecycleSignature(PlayerTeam byTeam, Puck puck)
	{
		string text = (puck != null) ? puck.NetworkObjectId.ToString(CultureInfo.InvariantCulture) : $"nopuck:{Math.Round(GetCurrentGameTime(), 1).ToString("0.0", CultureInfo.InvariantCulture)}";
		return string.Join("|", byTeam.ToString(), GetCurrentPeriod().ToString(CultureInfo.InvariantCulture), text);
	}

	private static void MarkRecentGoalPreProcessing(PlayerTeam byTeam, Puck puck)
	{
		_lastGoalPreprocessSignature = BuildGoalLifecycleSignature(byTeam, puck);
		_lastGoalPreprocessAtUtc = DateTime.UtcNow;
	}

	private static bool WasGoalRecentlyPreProcessed(PlayerTeam byTeam, Puck puck)
	{
		if (string.IsNullOrEmpty(_lastGoalPreprocessSignature) || (DateTime.UtcNow - _lastGoalPreprocessAtUtc).TotalSeconds > 5.0)
		{
			return false;
		}
		return string.Equals(_lastGoalPreprocessSignature, BuildGoalLifecycleSignature(byTeam, puck), StringComparison.Ordinal);
	}

	private static bool IsDuplicateTrackedGoal(PlayerTeam byTeam, string scorerSteamId, int period, float gameTime)
	{
		if (string.IsNullOrWhiteSpace(scorerSteamId))
		{
			return false;
		}
		if (string.Equals(_lastGoalDedupeSteamId, scorerSteamId, StringComparison.Ordinal) && _lastGoalDedupePeriod == period && Math.Abs(_lastGoalDedupeTime - gameTime) < 1f)
		{
			return true;
		}
		string text = (byTeam == PlayerTeam.Blue) ? "Blue" : "Red";
		return _goals.Any((GoalInfo g) => string.Equals(g.Scorer, scorerSteamId, StringComparison.Ordinal) && g.Period == period && string.Equals(g.Team, text, StringComparison.OrdinalIgnoreCase) && Math.Abs(g.GameTime - gameTime) < 1f);
	}

	private static void MarkTrackedGoalDedupe(GoalInfo goalInfo)
	{
		if (goalInfo == null)
		{
			return;
		}
		_lastGoalDedupeSteamId = goalInfo.Scorer ?? "";
		_lastGoalDedupePeriod = goalInfo.Period;
		_lastGoalDedupeTime = goalInfo.GameTime;
	}

	private static void AuditGoalTrackingAgainstOfficialStats(bool applyCorrections)
	{
		if (!applyCorrections && !TrackingDiagnosticsEnabled()) return;
		if (!ServerFunc.IsDedicatedServer() || MonoBehaviourSingleton<PlayerManager>.Instance == null)
		{
			return;
		}
		List<Player> players = MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayers();
		if (players == null || players.Count == 0)
		{
			return;
		}
		Dictionary<string, int> dictionary = new Dictionary<string, int>(StringComparer.Ordinal);
		Dictionary<string, int> dictionary2 = new Dictionary<string, int>(StringComparer.Ordinal);
		foreach (Player item in players)
		{
			if (item == null || !(bool)item)
			{
				continue;
			}
			string text = item.SteamId.Value.ToString();
			if (string.IsNullOrWhiteSpace(text))
			{
				continue;
			}
			dictionary[text] = Math.Max(0, item.Goals.Value);
			dictionary2[text] = Math.Max(0, item.Assists.Value);
		}
		bool flag = false;
		if (applyCorrections)
		{
			flag = TrimTrackedGoalsToOfficialCounts(dictionary) || flag;
			flag = TrimTrackedAssistsToOfficialCounts(dictionary2) || flag;
		}
		Dictionary<string, int> trackedAssistCounts = BuildTrackedAssistCounts();
		foreach (KeyValuePair<string, int> item2 in dictionary)
		{
			int num = _goals.Count((GoalInfo g) => string.Equals(g.Scorer, item2.Key, StringComparison.Ordinal));
			if (num != item2.Value)
			{
				if (TrackingDiagnosticsEnabled())
				{
					LogTrackingDiagnostic("goal-audit-goals-" + item2.Key, $"Goal tracking audit mismatch for {item2.Key}: trackedGoals={num}, officialGoals={item2.Value}.", minIntervalMilliseconds: 1500);
				}
			}
		}
		foreach (KeyValuePair<string, int> item3 in dictionary2)
		{
			trackedAssistCounts.TryGetValue(item3.Key, out var value);
			if (value != item3.Value)
			{
				if (TrackingDiagnosticsEnabled())
				{
					LogTrackingDiagnostic("goal-audit-assists-" + item3.Key, $"Goal tracking audit mismatch for {item3.Key}: trackedAssists={value}, officialAssists={item3.Value}.", minIntervalMilliseconds: 1500);
				}
			}
		}
		if (NetworkBehaviourSingleton<GameManager>.Instance != null)
		{
			int num2 = _goals.Count((GoalInfo g) => string.Equals(g.Team, "Red", StringComparison.OrdinalIgnoreCase));
			int num3 = _goals.Count((GoalInfo g) => string.Equals(g.Team, "Blue", StringComparison.OrdinalIgnoreCase));
			if (num2 != NetworkBehaviourSingleton<GameManager>.Instance.GameState.Value.RedScore || num3 != NetworkBehaviourSingleton<GameManager>.Instance.GameState.Value.BlueScore)
			{
				if (TrackingDiagnosticsEnabled())
				{
					LogTrackingDiagnostic("goal-audit-team-score", $"Goal tracking audit mismatch for team totals: trackedRed={num2}, trackedBlue={num3}, officialRed={NetworkBehaviourSingleton<GameManager>.Instance.GameState.Value.RedScore}, officialBlue={NetworkBehaviourSingleton<GameManager>.Instance.GameState.Value.BlueScore}.", minIntervalMilliseconds: 1500);
				}
			}
		}
		if (flag && _expectedGoalsModel != null && _expectedGoalsModel.Enabled)
		{
			RefreshLiveExpectedGoalsFromPlayByPlay();
			FlushPendingStatUpdatesImmediately();
		}
	}

	private static bool TrimTrackedGoalsToOfficialCounts(Dictionary<string, int> officialGoalsBySteamId)
	{
		bool flag = false;
		foreach (IGrouping<string, GoalInfo> item in _goals.Where((GoalInfo g) => !string.IsNullOrWhiteSpace(g.Scorer)).GroupBy((GoalInfo g) => g.Scorer, StringComparer.Ordinal).ToList())
		{
			if (!officialGoalsBySteamId.TryGetValue(item.Key, out var value))
			{
				continue;
			}
			int num = item.Count() - value;
			if (num <= 0)
			{
				continue;
			}
			foreach (GoalInfo item2 in item.OrderByDescending((GoalInfo g) => g.Period).ThenByDescending((GoalInfo g) => g.GameTime).Take(num).ToList())
			{
				_goals.Remove(item2);
				flag = true;
				if (TrackingDiagnosticsEnabled())
				{
					LogTrackingDiagnostic("goal-audit-correct-goal-" + item.Key, $"Removed duplicate tracked goal for {item.Key} at period={item2.Period}, gameTime={item2.GameTime:0.00}.", minIntervalMilliseconds: 500);
				}
			}
		}
		return flag;
	}

	private static bool TrimTrackedAssistsToOfficialCounts(Dictionary<string, int> officialAssistsBySteamId)
	{
		bool flag = false;
		Dictionary<string, int> trackedAssistCounts = BuildTrackedAssistCounts();
		foreach (KeyValuePair<string, int> item in trackedAssistCounts.ToList())
		{
			if (!officialAssistsBySteamId.TryGetValue(item.Key, out var value))
			{
				continue;
			}
			bool flag2 = false;
			int num = item.Value - value;
			if (num <= 0)
			{
				continue;
			}
			foreach (GoalInfo item2 in _goals.OrderByDescending((GoalInfo g) => g.Period).ThenByDescending((GoalInfo g) => g.GameTime).ToList())
			{
				if (num <= 0)
				{
					break;
				}
				if (string.Equals(item2.SecondaryAssist, item.Key, StringComparison.Ordinal))
				{
					item2.SecondaryAssist = null;
					num--;
					flag = true;
					flag2 = true;
				}
				if (num > 0 && string.Equals(item2.PrimaryAssist, item.Key, StringComparison.Ordinal))
				{
					item2.PrimaryAssist = null;
					num--;
					flag = true;
					flag2 = true;
				}
				if (flag2)
				{
					NormalizeGoalAssistOrdering(item2);
				}
			}
			if (flag2)
			{
				if (TrackingDiagnosticsEnabled())
				{
					LogTrackingDiagnostic("goal-audit-correct-assist-" + item.Key, $"Trimmed tracked assists for {item.Key} back to the official total.", minIntervalMilliseconds: 500);
				}
			}
		}
		return flag;
	}

	private static Dictionary<string, int> BuildTrackedAssistCounts()
	{
		Dictionary<string, int> dictionary = new Dictionary<string, int>(StringComparer.Ordinal);
		foreach (GoalInfo goal in _goals)
		{
			AddCount(dictionary, goal.PrimaryAssist);
			AddCount(dictionary, goal.SecondaryAssist);
		}
		return dictionary;
	}

	private static void NormalizeGoalAssistOrdering(GoalInfo goalInfo)
	{
		if (goalInfo == null)
		{
			return;
		}
		if (string.IsNullOrWhiteSpace(goalInfo.PrimaryAssist) && !string.IsNullOrWhiteSpace(goalInfo.SecondaryAssist))
		{
			goalInfo.PrimaryAssist = goalInfo.SecondaryAssist;
			goalInfo.SecondaryAssist = null;
		}
		if (!string.IsNullOrWhiteSpace(goalInfo.PrimaryAssist) && string.Equals(goalInfo.PrimaryAssist, goalInfo.Scorer, StringComparison.Ordinal))
		{
			goalInfo.PrimaryAssist = null;
		}
		if (!string.IsNullOrWhiteSpace(goalInfo.SecondaryAssist) && (string.Equals(goalInfo.SecondaryAssist, goalInfo.Scorer, StringComparison.Ordinal) || string.Equals(goalInfo.SecondaryAssist, goalInfo.PrimaryAssist, StringComparison.Ordinal)))
		{
			goalInfo.SecondaryAssist = null;
		}
		if (string.IsNullOrWhiteSpace(goalInfo.PrimaryAssist) && !string.IsNullOrWhiteSpace(goalInfo.SecondaryAssist))
		{
			goalInfo.PrimaryAssist = goalInfo.SecondaryAssist;
			goalInfo.SecondaryAssist = null;
		}
	}

	private static string _lastGoalPreprocessSignature = "";

	private static DateTime _lastGoalPreprocessAtUtc = DateTime.MinValue;

	private static string _lastGoalDedupeSteamId = "";

	private static int _lastGoalDedupePeriod = -1;

	private static float _lastGoalDedupeTime = -1f;
}
