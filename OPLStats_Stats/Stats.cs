using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Codebase;
using HarmonyLib;
using Newtonsoft.Json;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;
using OPLStats_Stats.Configs;

namespace OPLStats_Stats;

public partial class Stats : IPuckPlugin
{
	[HarmonyPatch(typeof(PuckManager), "Server_SpawnPuck")]
	public class PuckManager_Server_SpawnPuck_Patch
	{
		[HarmonyPostfix]
		public static void Postfix(ref Puck __result, Vector3 position, Quaternion rotation, bool isReplay)
		{
			try
			{
				if (!(!ServerFunc.IsDedicatedServer() || isReplay) && (NetworkBehaviourSingleton<GameManager>.Instance.Phase == GamePhase.Play || NetworkBehaviourSingleton<GameManager>.Instance.Phase == GamePhase.FaceOff))
				{
					ResetPostHitTrackingState();
					__result.gameObject.AddComponent<PuckRaycast>();
					_puckRaycast = __result.gameObject.GetComponent<PuckRaycast>();
					if (!isReplay && NetworkBehaviourSingleton<GameManager>.Instance != null && NetworkBehaviourSingleton<GameManager>.Instance.Phase == GamePhase.FaceOff && !_faceoffRecordedForCurrentPeriod)
					{
						_pendingBlueFaceoffOutcome = null;
						_pendingRedFaceoffOutcome = null;
						RecordFaceoffEvent();
						_faceoffRecordedForCurrentPeriod = true;
						_trackingFaceoffOutcome = true;
						_faceoffPossessionChainCount = 0;
						_faceoffPossessionTeam = PlayerTeam.None;
						_faceoffTotalIncremented = false;
					}
				}
			}
			catch (Exception arg)
			{
				Logging.LogError($"Error in PuckManager_Server_SpawnPuck_Patch Postfix().\n{arg}", ServerConfig);
			}
		}
	}

	[HarmonyPatch(typeof(BaseGameMode<BaseGameModeConfig>), "ScoreGoal")]
	public class BaseGameMode_ScoreGoal_Patch
	{
		[HarmonyPrefix]
		public static bool Prefix(PlayerTeam byTeam, ref Player goalPlayer, ref Player assistPlayer, ref Player secondAssistPlayer, Puck puck)
		{
			return true;
		}

		[HarmonyPostfix]
		public static void Postfix(PlayerTeam byTeam, Player goalPlayer, Player assistPlayer, Player secondAssistPlayer, Puck puck)
		{
		}
	}

	[HarmonyPatch(typeof(BaseGameMode<BaseGameModeConfig>), "OnGoalScored")]
	public class BaseGameMode_OnGoalScored_Patch
	{
		[HarmonyPostfix]
		public static void Postfix(PlayerTeam byTeam, Player goalPlayer, Player assistPlayer, Player secondAssistPlayer, Puck puck)
		{
			try
			{
				if (!ServerFunc.IsDedicatedServer() || !_logic)
				{
					return;
				}
				if (!WasGoalHookRecentlyHandled(byTeam, goalPlayer, assistPlayer, secondAssistPlayer, puck))
				{
					RecoverGoalParticipantsFromTracking(byTeam, ref goalPlayer, ref assistPlayer, ref secondAssistPlayer);
					PrepareCurrentGoalTrackingState(byTeam, goalPlayer, announceOwnGoal: false);
					if (!WasGoalRecentlyPreProcessed(byTeam, puck))
					{
						if (goalPlayer != null && (bool)goalPlayer)
						{
							SendSavePercDuringGoal(byTeam, SendSOGDuringGoal(goalPlayer));
						}
						else
						{
							SendSavePercDuringGoal(byTeam, saveWasCounted: false);
						}
					}
					FinalizeGoalTracking(byTeam, goalPlayer, assistPlayer, secondAssistPlayer, puck);
					MarkRecentGoalHookInvocation(byTeam, goalPlayer, assistPlayer, secondAssistPlayer, puck);
				}
				AuditGoalTrackingAgainstOfficialStats(applyCorrections: true);
			}
			catch (Exception arg)
			{
				Logging.LogError($"Error in BaseGameMode_OnGoalScored_Patch Postfix().\n{arg}", ServerConfig);
			}
		}
	}

	private static void AdjustGoalParticipantsFromLastTouch(PlayerTeam byTeam, ref Player goalPlayer, ref Player assistPlayer, ref Player secondAssistPlayer)
	{
		if (goalPlayer == null || MonoBehaviourSingleton<PlayerManager>.Instance == null)
		{
			return;
		}
		Player player = (from x in MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayers()
			where x.SteamId.Value.ToString() == _lastPlayerOnPuckTipIncludedSteamId[byTeam].SteamId
			select x).FirstOrDefault();
		if (player == null || player.SteamId.Value.ToString() == goalPlayer.SteamId.Value.ToString())
		{
			return;
		}
		secondAssistPlayer = assistPlayer;
		assistPlayer = goalPlayer;
		goalPlayer = player;
		while (assistPlayer != null && assistPlayer.SteamId.Value.ToString() == goalPlayer.SteamId.Value.ToString())
		{
			assistPlayer = secondAssistPlayer;
			secondAssistPlayer = null;
		}
		if (secondAssistPlayer != null && (secondAssistPlayer.SteamId.Value.ToString() == assistPlayer?.SteamId.Value.ToString() || secondAssistPlayer.SteamId.Value.ToString() == goalPlayer.SteamId.Value.ToString()))
		{
			secondAssistPlayer = null;
		}
	}

	private static Player ResolveOwnGoalPlayer(PlayerTeam byTeam)
	{
		PlayerTeam defendingTeam = TeamFunc.GetOtherTeam(byTeam);
		return (from x in MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayers()
			where x.SteamId.Value.ToString() == _lastPlayerOnPuckTipIncludedSteamId[defendingTeam].SteamId
			select x).FirstOrDefault();
	}

	private static string BuildGoalHookSignature(PlayerTeam byTeam, Player goalPlayer, Player assistPlayer, Player secondAssistPlayer, Puck puck)
	{
		return BuildGoalLifecycleSignature(byTeam, puck);
	}

	private static void MarkRecentGoalHookInvocation(PlayerTeam byTeam, Player goalPlayer, Player assistPlayer, Player secondAssistPlayer, Puck puck)
	{
		_lastGoalHookSignature = BuildGoalHookSignature(byTeam, goalPlayer, assistPlayer, secondAssistPlayer, puck);
		_lastGoalHookAtUtc = DateTime.UtcNow;
	}

	private static bool WasGoalHookRecentlyHandled(PlayerTeam byTeam, Player goalPlayer, Player assistPlayer, Player secondAssistPlayer, Puck puck)
	{
		if (string.IsNullOrEmpty(_lastGoalHookSignature) || (DateTime.UtcNow - _lastGoalHookAtUtc).TotalSeconds > 5.0)
		{
			return false;
		}
		return string.Equals(_lastGoalHookSignature, BuildGoalHookSignature(byTeam, goalPlayer, assistPlayer, secondAssistPlayer, puck), StringComparison.Ordinal);
	}

	private static void FinalizeGoalTracking(PlayerTeam byTeam, Player goalPlayer, Player assistPlayer, Player secondAssistPlayer, Puck puck)
	{
		bool isCurrentGoalOwnGoal = _isCurrentGoalOwnGoal;
		string text = ((goalPlayer != null && (bool)goalPlayer) ? goalPlayer.SteamId.Value.ToString() : "");
		float currentGameTime = GetCurrentGameTime();
		int currentPeriod = GetCurrentPeriod();
		bool flag = !isCurrentGoalOwnGoal && !string.IsNullOrWhiteSpace(text) && IsDuplicateTrackedGoal(byTeam, text, currentPeriod, currentGameTime);
		PlayByPlayEvent playByPlayEvent = null;
		if (puck != null)
		{
			Vector3 position = puck.transform.position;
			if (isCurrentGoalOwnGoal)
			{
				Player player = ((!string.IsNullOrEmpty(_currentOwnGoalPlayerSteamId)) ? MonoBehaviourSingleton<PlayerManager>.Instance?.GetPlayerBySteamId(_currentOwnGoalPlayerSteamId) : null);
				if (player != null && (bool)player)
				{
					RecordPlayByPlayEventInternal(PlayByPlayEventType.OwnGoal, player, position, Vector3.zero);
					if (_playByPlayEvents.Count > 0)
					{
						PlayByPlayEvent playByPlayEvent2 = _playByPlayEvents[_playByPlayEvents.Count - 1];
						if (playByPlayEvent2.EventType == PlayByPlayEventType.OwnGoal)
						{
							playByPlayEvent2.PlayerSteamId = "";
						}
					}
				}
			}
			else if (!flag && goalPlayer != null && (bool)goalPlayer)
			{
				RecordPlayByPlayEventInternal(PlayByPlayEventType.Goal, goalPlayer, position, Vector3.zero);
				if (_playByPlayEvents.Count > 0)
				{
					playByPlayEvent = _playByPlayEvents[_playByPlayEvents.Count - 1];
					if (playByPlayEvent.EventType != PlayByPlayEventType.Goal)
					{
						playByPlayEvent = (from e in _playByPlayEvents
							where e.EventType == PlayByPlayEventType.Goal && e.PlayerSteamId == goalPlayer.SteamId.Value.ToString() && e.GameTime > 0f
							orderby e.GameTime descending
							select e).FirstOrDefault();
					}
				}
			}
		}
		if (!isCurrentGoalOwnGoal && !flag && goalPlayer != null && (bool)goalPlayer)
		{
			float gameTime = playByPlayEvent?.GameTime ?? currentGameTime;
			int period = playByPlayEvent?.Period ?? currentPeriod;
			GoalInfo value = new GoalInfo
			{
				GameTime = gameTime,
				Period = period,
				Team = ((byTeam == PlayerTeam.Blue) ? "Blue" : "Red"),
				Scorer = goalPlayer.SteamId.Value.ToString(),
				ScorerPosition = GetPlayerPosition(goalPlayer),
				PrimaryAssist = ((assistPlayer != null) ? assistPlayer.SteamId.Value.ToString() : null),
				PrimaryAssistPosition = ((assistPlayer != null) ? GetPlayerPosition(assistPlayer) : ""),
				SecondaryAssist = ((secondAssistPlayer != null) ? secondAssistPlayer.SteamId.Value.ToString() : null),
				SecondaryAssistPosition = ((secondAssistPlayer != null) ? GetPlayerPosition(secondAssistPlayer) : ""),
				GWG = false
			};
			_goals.Add(value);
			MarkTrackedGoalDedupe(value);
		}
		if (_expectedGoalsModel != null && _expectedGoalsModel.Enabled)
		{
			RefreshLiveExpectedGoalsFromPlayByPlay();
			FlushPendingStatUpdatesImmediately();
		}
		_currentOwnGoalPlayerSteamId = "";
		_currentTeamInPossession = PlayerTeam.None;
		_currentPlayInPossession = 0;
		_lastEvent = null;
	}

	[HarmonyPatch(typeof(UIScoreboard), "RemovePlayer")]
	public class UIScoreboard_RemovePlayer_Patch
	{
		[HarmonyPostfix]
		public static void Postfix(Player player)
		{
			try
			{
				if (!ServerFunc.IsDedicatedServer())
				{
					string key = player.SteamId.Value.ToString();
					_sogLabels.Remove(key);
					_scoreboardSogFollowUpScheduledAt.Remove(key);
					if (_playerTooltips.TryGetValue(key, out var value))
					{
						value.parent?.Remove(value);
						_playerTooltips.Remove(key);
					}
					_playerTooltipNameLabels.Remove(key);
					_playerTooltipContainers.Remove(key);
					_playerTooltipIsGoalie.Remove(key);
					_playerTooltipLabels.Remove(key);
					_hasUpdatedUIScoreboard.Remove(player.SteamId.Value.ToString());
				}
			}
			catch (Exception arg)
			{
				Logging.LogError($"Error in UIScoreboard_RemovePlayer_Patch Postfix().\n{arg}", _clientConfig);
			}
		}
	}

	[HarmonyPatch(typeof(PlayerInput), "Update")]
	public class PlayerInput_Update_BoxscoreHotkey_Patch
	{
		[HarmonyPostfix]
		public static void Postfix(PlayerInput __instance)
		{
			try
			{
				if (__instance == null || !__instance.IsOwner || ServerFunc.IsDedicatedServer() || Keyboard.current == null)
				{
					return;
				}
				TryShowBoxscoreHintPromptForJoin();
				bool boxscoreVisible = _boxscoreVisible;
				bool wasF7Pressed = Keyboard.current.f7Key.wasPressedThisFrame;
				bool wasEscapePressed = Keyboard.current.escapeKey.wasPressedThisFrame;
				if (!boxscoreVisible)
				{
					if (!wasF7Pressed)
					{
						return;
					}
					if (GlobalStateManager.UIState.Phase != UIPhase.Playing || GlobalStateManager.UIState.IsInteracting)
					{
						return;
					}
					ShowBoxscorePanel();
					return;
				}
				if (wasF7Pressed || wasEscapePressed)
				{
					HideBoxscorePanel(setPauseSuppression: true);
				}
			}
			catch (Exception arg)
			{
				Logging.LogError($"Error in PlayerInput_Update_BoxscoreHotkey_Patch Postfix().\n{arg}", _clientConfig);
			}
		}
	}

	[HarmonyPatch(typeof(UIManager), "OnPauseActionPerformed")]
	public class UIManager_OnPauseActionPerformed_BoxscorePatch
	{
		[HarmonyPrefix]
		public static bool Prefix()
		{
			try
			{
				if (_boxscoreVisible)
				{
					HideBoxscorePanel(setPauseSuppression: true);
					return false;
				}
				if (_boxscorePauseSuppressionFrame == Time.frameCount)
				{
					return false;
				}
			}
			catch (Exception arg)
			{
				Logging.LogError($"Error in UIManager_OnPauseActionPerformed_BoxscorePatch Prefix().\n{arg}", _clientConfig);
			}
			return true;
		}
	}

	[HarmonyPatch(typeof(PhysicsManager), "Update")]
	public class PhysicsManager_Update_Patch
	{
		[HarmonyPostfix]
		public static void Postfix()
		{
			try
			{
				_fileWork.PumpCompletions();
				if (!ServerFunc.IsDedicatedServer() || !_logic)
				{
					return;
				}
				Server_RegisterNamedMessageHandler();
				DateTime utcNow = DateTime.UtcNow;
				TryExportLiveGameStats();
				if ((utcNow - _lastStatBatchSendTime).TotalSeconds >= 5.0)
				{
					try
					{
						SendBatchedStatUpdates();
					}
					catch (Exception arg)
					{
						Logging.LogError($"Unexpected error calling SendBatchedStatUpdates (this should not happen): {arg}", ServerConfig);
					}
				}
				if ((utcNow - _lastZoneFlagScanTime).TotalSeconds >= 6.0)
				{
					AddZoneFlagsToPlayByPlayEvents();
					_lastZoneFlagScanTime = utcNow;
				}
				bool sendSavePercDuringGoalNextFrame = _sendSavePercDuringGoalNextFrame;
				if (sendSavePercDuringGoalNextFrame)
				{
					_sendSavePercDuringGoalNextFrame = false;
					if (_sendSavePercDuringGoalNextFrame_Player != null && (bool)_sendSavePercDuringGoalNextFrame_Player && !_lastShotWasCounted[_sendSavePercDuringGoalNextFrame_Player.Team])
					{
						SendSavePercDuringGoal(_sendSavePercDuringGoalNextFrame_Player.Team, SendSOGDuringGoal(_sendSavePercDuringGoalNextFrame_Player));
					}
					_sendSavePercDuringGoalNextFrame_Player = null;
				}
				if (MonoBehaviourSingleton<PlayerManager>.Instance == null || MonoBehaviourSingleton<PuckManager>.Instance == null || NetworkBehaviourSingleton<GameManager>.Instance.Phase != GamePhase.Play || _paused)
				{
					return;
				}
				ResolvePossessionFromTouchState();
				if (_puckRaycast == null || _puckRaycast.PuckIsGoingToNet == null)
				{
					LogTrackingDiagnostic("raycast-missing", "PuckRaycast is unavailable during PhysicsManager_Update. Possession diagnostics remain active, but shot-raycast tracking is skipped.", minIntervalMilliseconds: 5000);
					return;
				}
				foreach (PlayerTeam item2 in TrackedTeams)
				{
					float currentGameTime = GetCurrentGameTime();
					bool flag = _puckRaycast.PuckIsGoingToNet.TryGetValue(item2, out var puckIsGoingToNet) && puckIsGoingToNet;
					bool value;
					bool flag2 = _previousRaycastState.TryGetValue(item2, out value) && value;
					if (flag)
					{
						_raycastTrueFrames[item2] = ((!_raycastTrueFrames.TryGetValue(item2, out var value2)) ? 1 : (value2 + 1));
					}
					else
					{
						_raycastTrueFrames[item2] = 0;
					}
					if (flag && _raycastTrueFrames[item2] >= 2 && !_shotRecordedForRaycast[item2])
					{
						TeamFunc.GetOtherTeam(item2);
						_shotRecordedForRaycast[item2] = true;
					}
					PlayerTeam attackingTeamForMissed = TeamFunc.GetOtherTeam(item2);
					foreach (PlayByPlayEvent shotEvent in GetShotResolutionCandidates(attackingTeamForMissed, attempts: true))
					{
						float shotTimeoutByZone = GetShotTimeoutByZone(shotEvent.Zone);
						if (currentGameTime - shotEvent.GameTime > shotTimeoutByZone && !HasShotResultBetween(shotEvent.GameTime, shotEvent.GameTime + 3f))
						{
							shotEvent.Outcome = "missed";
						}
					}
					if (!flag && flag2)
					{
						PlayerTeam attackingTeamForReset = TeamFunc.GetOtherTeam(item2);
						float resetGameTime = GetCurrentGameTime();
						foreach (PlayByPlayEvent shotEvent2 in GetShotResolutionCandidates(attackingTeamForReset, attempts: false, earliest: resetGameTime - 2f))
						{
							if (!HasShotResultBetween(shotEvent2.GameTime, shotEvent2.GameTime + 4f) && shotEvent2.Outcome != "blocked")
							{
								shotEvent2.Outcome = "missed";
								if (shotEvent2.Flags == "HomePlate")
								{
									shotEvent2.Flags = "";
								}
							}
						}
						_shotRecordedForRaycast[item2] = false;
						_raycastTrueFrames[item2] = 0;
					}
					_previousRaycastState[item2] = flag;
				}
				if (!sendSavePercDuringGoalNextFrame)
				{
					foreach (PlayerTeam item3 in CheckTeams)
					{
						SaveCheck saveCheck = _checkIfPuckWasSaved[item3];
						int value3;
						if (!saveCheck.HasToCheck)
						{
							continue;
						}
						else if (!(_puckRaycast.PuckIsGoingToNet.TryGetValue(item3, out var puckIsStillGoingToNet) && puckIsStillGoingToNet) && !_lastShotWasCounted[saveCheck.ShooterTeam])
						{
							if (!_sog.TryGetValue(saveCheck.ShooterSteamId, out value3))
							{
								_sog.Add(saveCheck.ShooterSteamId, 0);
							}
							_sog[saveCheck.ShooterSteamId]++;
							string dataName = "oomtm450_statsSOG" + saveCheck.ShooterSteamId;
							value3 = _sog[saveCheck.ShooterSteamId];
							QueueStatUpdate(dataName, value3.ToString());
							LogSOG(saveCheck.ShooterSteamId, _sog[saveCheck.ShooterSteamId]);
							if (!_teamShots.TryGetValue(saveCheck.ShooterTeam, out value3))
							{
								_teamShots.Add(saveCheck.ShooterTeam, 0);
							}
							_teamShots[saveCheck.ShooterTeam]++;
							string dataName2 = "oomtm450_statsTEAM_SHOTS" + saveCheck.ShooterTeam;
							value3 = _teamShots[saveCheck.ShooterTeam];
							QueueStatUpdate(dataName2, value3.ToString());
							_lastShotWasCounted[saveCheck.ShooterTeam] = true;
							Player otherTeamGoalie = PlayerFunc.GetOtherTeamGoalie(saveCheck.ShooterTeam);
							if (otherTeamGoalie != null)
							{
								string text = otherTeamGoalie.SteamId.Value.ToString();
								if (!_savePerc.TryGetValue(text, out (int, int) value4))
								{
									_savePerc.Add(text, (0, 0));
									value4 = (0, 0);
								}
								LockDictionary<string, (int Saves, int Shots)> savePerc = _savePerc;
								int item = ++value4.Item1;
								value3 = ++value4.Item2;
								var (saves, sog) = (savePerc[text] = (item, value3));
								QueueStatUpdate("oomtm450_statsSAVEPERC" + text, _savePerc[text].ToString());
								LogSavePerc(text, saves, sog);
								if (!string.IsNullOrEmpty(saveCheck.ShooterSteamId))
								{
									_lastShotAttemptGameTime[saveCheck.ShooterSteamId] = -1f;
								}
								Puck puck = MonoBehaviourSingleton<PuckManager>.Instance?.GetPuck();
								bool flag3 = false;
								if (otherTeamGoalie != null && (bool)otherTeamGoalie && puck != null)
								{
									Vector3 position = puck.transform.position;
									Vector3 velocity = puck.GetComponent<Rigidbody>()?.linearVelocity ?? Vector3.zero;
									float saveCheckGameTime = GetCurrentGameTime();
									PlayByPlayEvent lastShot = _playByPlayEvents.LastOrDefault((PlayByPlayEvent e) => e.PlayerSteamId == saveCheck.ShooterSteamId && e.EventType == PlayByPlayEventType.Shot && e.GameTime >= saveCheckGameTime - 3f);
									if (lastShot != null && DetermineShotFlag(lastShot.Position, (PlayerTeam)lastShot.PlayerTeam) == "HomePlate")
									{
										flag3 = true;
									}
									if (!flag3 && _pendingShotReleases.TryGetValue(saveCheck.ShooterTeam, out (string, Vector3, Vector3, Vector3, DateTime) value5) && !string.IsNullOrEmpty(value5.Item1) && value5.Item1 == saveCheck.ShooterSteamId && DetermineShotFlag(value5.Item3, saveCheck.ShooterTeam) == "HomePlate")
									{
										flag3 = true;
									}
									string text2 = ((!string.IsNullOrWhiteSpace(saveCheck.SaveSurface)) ? saveCheck.SaveSurface : (saveCheck.HitStick ? "Stick" : "Body"));
									if (flag3)
									{
										text2 += ",HomePlate";
									}
									RecordPlayByPlayEventInternal(PlayByPlayEventType.Save, otherTeamGoalie, position, velocity, "successful", text2);
									if (lastShot != null && lastShot.Outcome == "missed")
									{
										lastShot.Outcome = "on net";
										if (string.IsNullOrEmpty(lastShot.Flags))
										{
											string flags = DetermineShotFlag(lastShot.Position, (PlayerTeam)lastShot.PlayerTeam);
											lastShot.Flags = flags;
										}
									}
									if (lastShot != null && lastShot.Outcome == "blocked")
									{
										lastShot.Outcome = "on net";
										if (string.IsNullOrEmpty(lastShot.Flags))
										{
											string flags2 = DetermineShotFlag(lastShot.Position, (PlayerTeam)lastShot.PlayerTeam);
											lastShot.Flags = flags2;
										}
										float saveCheckGameTimeForBlock = GetCurrentGameTime();
										PlayByPlayEvent playByPlayEvent = (from e in _playByPlayEvents
											where e.EventType == PlayByPlayEventType.Block && e.GameTime >= lastShot.GameTime && e.GameTime <= saveCheckGameTimeForBlock && e.GameTime <= lastShot.GameTime + 3f
											orderby e.GameTime descending
											select e).FirstOrDefault();
										if (playByPlayEvent != null && playByPlayEvent.Outcome == "successful")
										{
											playByPlayEvent.Outcome = "failed";
										}
										else if (playByPlayEvent == null)
										{
											BlockCheck value6;
											BlockCheck blockCheck = (_checkIfPuckWasBlocked.TryGetValue(saveCheck.ShooterTeam, out value6) ? value6 : null);
											if (blockCheck != null && !string.IsNullOrEmpty(blockCheck.BlockerSteamId))
											{
												Player playerBySteamId = MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayerBySteamId(blockCheck.BlockerSteamId);
												if (playerBySteamId != null && (bool)playerBySteamId && puck != null)
												{
													RecordPlayByPlayEventInternal(PlayByPlayEventType.Block, playerBySteamId, position, velocity, "failed");
												}
											}
										}
									}
									UpdateBodyStickSavesFromPbp();
									if (flag3)
									{
										if (!_homePlateSaves.TryGetValue(text, out var value7))
										{
											_homePlateSaves.Add(text, 0);
											value7 = 0;
										}
										value3 = (_homePlateSaves[text] = ++value7);
										int num2 = value3;
										QueueStatUpdate("oomtm450_statsHOME_PLATE_SAVES" + text, num2.ToString());
										if (!_homePlateShots.TryGetValue(text, out var value8))
										{
											_homePlateShots.Add(text, 0);
											value8 = 0;
										}
										value3 = (_homePlateShots[text] = ++value8);
										int num4 = value3;
										QueueStatUpdate("oomtm450_statsHOME_PLATE_SHOTS_FACED" + text, num4.ToString());
									}
								}
							}
							float currentGameTime2 = GetCurrentGameTime();
							PlayByPlayEvent playByPlayEvent2 = _playByPlayEvents.LastOrDefault((PlayByPlayEvent e) => e.PlayerSteamId == saveCheck.ShooterSteamId && e.EventType == PlayByPlayEventType.Shot && e.GameTime >= currentGameTime2 - 3f);
							if (playByPlayEvent2 != null)
							{
								if (playByPlayEvent2.Outcome == "attempt")
								{
									playByPlayEvent2.Outcome = "on net";
									string text3 = (playByPlayEvent2.Flags = DetermineShotFlag(playByPlayEvent2.Position, (PlayerTeam)playByPlayEvent2.PlayerTeam));
									if (text3 == "HomePlate")
									{
										if (!_homePlateSogs.TryGetValue(saveCheck.ShooterSteamId, out value3))
										{
											_homePlateSogs.Add(saveCheck.ShooterSteamId, 0);
										}
										_homePlateSogs[saveCheck.ShooterSteamId]++;
										string dataName3 = "oomtm450_statsHOME_PLATE_SOGS" + saveCheck.ShooterSteamId;
										value3 = _homePlateSogs[saveCheck.ShooterSteamId];
										QueueStatUpdate(dataName3, value3.ToString());
										if (!_teamHomePlateSogs.TryGetValue(saveCheck.ShooterTeam, out value3))
										{
											_teamHomePlateSogs.Add(saveCheck.ShooterTeam, 0);
										}
										_teamHomePlateSogs[saveCheck.ShooterTeam]++;
										string dataName4 = "oomtm450_statsTEAM_HOME_PLATE_SOGS" + saveCheck.ShooterTeam;
										value3 = _teamHomePlateSogs[saveCheck.ShooterTeam];
										QueueStatUpdate(dataName4, value3.ToString());
									}
								}
							}
							else
							{
								bool flag4 = false;
								if (_pendingShotReleases.TryGetValue(saveCheck.ShooterTeam, out (string, Vector3, Vector3, Vector3, DateTime) value9) && !string.IsNullOrEmpty(value9.Item1) && value9.Item1 == saveCheck.ShooterSteamId && (float)(DateTime.UtcNow - value9.Item5).TotalSeconds <= 5f)
								{
									Player playerBySteamId2 = MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayerBySteamId(value9.Item1);
									if (playerBySteamId2 != null && (bool)playerBySteamId2 && _playByPlayEvents.LastOrDefault((PlayByPlayEvent e) => e.PlayerSteamId == saveCheck.ShooterSteamId && e.EventType == PlayByPlayEventType.Shot && e.GameTime >= currentGameTime2 - 5f) == null)
									{
										string text5 = DetermineShotFlag(value9.Item3, playerBySteamId2.Team);
										PlayByPlayEvent playByPlayEvent5 = RecordPlayByPlayEventInternal(PlayByPlayEventType.Shot, playerBySteamId2, value9.Item3, value9.Item4, "on net", text5);
										IncrementShotAttemptForPlayer(saveCheck.ShooterSteamId, saveCheck.ShooterTeam, playByPlayEvent5);
										if (text5 == "HomePlate")
										{
											if (!_homePlateSogs.TryGetValue(saveCheck.ShooterSteamId, out value3))
											{
												_homePlateSogs.Add(saveCheck.ShooterSteamId, 0);
											}
											_homePlateSogs[saveCheck.ShooterSteamId]++;
											string dataName7 = "oomtm450_statsHOME_PLATE_SOGS" + saveCheck.ShooterSteamId;
											value3 = _homePlateSogs[saveCheck.ShooterSteamId];
											QueueStatUpdate(dataName7, value3.ToString());
											if (!_teamHomePlateSogs.TryGetValue(saveCheck.ShooterTeam, out value3))
											{
												_teamHomePlateSogs.Add(saveCheck.ShooterTeam, 0);
											}
											_teamHomePlateSogs[saveCheck.ShooterTeam]++;
											string dataName8 = "oomtm450_statsTEAM_HOME_PLATE_SOGS" + saveCheck.ShooterTeam;
											value3 = _teamHomePlateSogs[saveCheck.ShooterTeam];
											QueueStatUpdate(dataName8, value3.ToString());
										}
										flag4 = true;
									}
								}
								if (!flag4)
								{
									Player playerBySteamId3 = MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayerBySteamId(saveCheck.ShooterSteamId);
									if (playerBySteamId3 != null && (bool)playerBySteamId3)
									{
										PlayByPlayEvent playByPlayEvent3 = _playByPlayEvents.LastOrDefault((PlayByPlayEvent e) => e.PlayerSteamId == saveCheck.ShooterSteamId && e.EventType == PlayByPlayEventType.Touch && e.GameTime >= currentGameTime2 - 3f);
										if (playByPlayEvent3 == null)
										{
											playByPlayEvent3 = _playByPlayEvents.LastOrDefault((PlayByPlayEvent e) => e.PlayerSteamId == saveCheck.ShooterSteamId && e.EventType == PlayByPlayEventType.Touch && e.GameTime >= currentGameTime2 - 12f);
										}
										if (playByPlayEvent3 == null)
										{
											playByPlayEvent3 = _playByPlayEvents.LastOrDefault((PlayByPlayEvent e) => e.PlayerTeam == (int)saveCheck.ShooterTeam && e.EventType == PlayByPlayEventType.Touch && e.GameTime >= currentGameTime2 - 12f);
										}
										Vector3 vector = playByPlayEvent3?.Position ?? Vector3.zero;
										Vector3 velocity2 = playByPlayEvent3?.Velocity ?? Vector3.zero;
										float? gameTimeOverride = playByPlayEvent3?.GameTime;
										float? playerSpeedOverride = playByPlayEvent3?.PlayerSpeed;
										if (vector == Vector3.zero)
										{
											PlayByPlayEvent playByPlayEvent4 = _playByPlayEvents.LastOrDefault((PlayByPlayEvent e) => e.PlayerSteamId == saveCheck.ShooterSteamId && e.EventType == PlayByPlayEventType.Touch);
											if (playByPlayEvent4 != null && playByPlayEvent4.Position != Vector3.zero)
											{
												vector = playByPlayEvent4.Position;
												velocity2 = playByPlayEvent4.Velocity;
												gameTimeOverride = playByPlayEvent4.GameTime;
												playerSpeedOverride = playByPlayEvent4.PlayerSpeed;
											}
										}
										string text6 = ((vector != Vector3.zero) ? DetermineShotFlag(vector, playerBySteamId3.Team) : "");
										PlayByPlayEvent playByPlayEvent6 = RecordPlayByPlayEventInternal(PlayByPlayEventType.Shot, playerBySteamId3, vector, velocity2, "on net", text6, playerSpeedOverride, skipPossessionReset: false, gameTimeOverride);
										IncrementShotAttemptForPlayer(saveCheck.ShooterSteamId, saveCheck.ShooterTeam, playByPlayEvent6);
										if (text6 == "HomePlate")
										{
											if (!_homePlateSogs.TryGetValue(saveCheck.ShooterSteamId, out value3))
											{
												_homePlateSogs.Add(saveCheck.ShooterSteamId, 0);
											}
											_homePlateSogs[saveCheck.ShooterSteamId]++;
											string dataName11 = "oomtm450_statsHOME_PLATE_SOGS" + saveCheck.ShooterSteamId;
											value3 = _homePlateSogs[saveCheck.ShooterSteamId];
											QueueStatUpdate(dataName11, value3.ToString());
											if (!_teamHomePlateSogs.TryGetValue(saveCheck.ShooterTeam, out value3))
											{
												_teamHomePlateSogs.Add(saveCheck.ShooterTeam, 0);
											}
											_teamHomePlateSogs[saveCheck.ShooterTeam]++;
											string dataName12 = "oomtm450_statsTEAM_HOME_PLATE_SOGS" + saveCheck.ShooterTeam;
											value3 = _teamHomePlateSogs[saveCheck.ShooterTeam];
											QueueStatUpdate(dataName12, value3.ToString());
											if (otherTeamGoalie != null && (bool)otherTeamGoalie)
											{
												string text7 = otherTeamGoalie.SteamId.Value.ToString();
												if (!_homePlateSaves.TryGetValue(text7, out var value10))
												{
													_homePlateSaves.Add(text7, 0);
													value10 = 0;
												}
												value3 = (_homePlateSaves[text7] = ++value10);
												int num6 = value3;
												QueueStatUpdate("oomtm450_statsHOME_PLATE_SAVES" + text7, num6.ToString());
												if (!_homePlateShots.TryGetValue(text7, out var value11))
												{
													_homePlateShots.Add(text7, 0);
													value11 = 0;
												}
												value3 = (_homePlateShots[text7] = ++value11);
												int num8 = value3;
												QueueStatUpdate("oomtm450_statsHOME_PLATE_SHOTS_FACED" + text7, num8.ToString());
											}
										}
								}
							}
						}
						RefreshLiveExpectedGoalsFromPlayByPlay();
						_checkIfPuckWasSaved[item3] = new SaveCheck();
						_checkIfPuckWasBlocked[item3] = new BlockCheck();
					}
						else
						{
							value3 = ++saveCheck.FramesChecked;
							if (value3 > NetworkBehaviourSingleton<ServerManager>.Instance.ServerConfig.tickRate)
							{
								_checkIfPuckWasSaved[item3] = new SaveCheck();
							}
						}
					}
					foreach (PlayerTeam item4 in CheckTeams)
					{
						BlockCheck blockCheck2 = _checkIfPuckWasBlocked[item4];
						if (!blockCheck2.HasToCheck)
						{
							continue;
						}
						else if (!(_puckRaycast.PuckIsGoingToNet.TryGetValue(item4, out var puckIsStillGoingToNetForBlock) && puckIsStillGoingToNetForBlock) && !_lastBlockWasCounted[blockCheck2.ShooterTeam])
						{
							bool flag5 = _lastShotWasCounted[blockCheck2.ShooterTeam];
							float currentGameTime3 = GetCurrentGameTime();
							bool flag6 = _playByPlayEvents.Any((PlayByPlayEvent e) => e.EventType == PlayByPlayEventType.Save && e.GameTime >= currentGameTime3 - 3f);
							string shooterSteamId = _lastPlayerOnPuckTipIncludedSteamId[blockCheck2.ShooterTeam].SteamId;
							bool flag7 = !string.IsNullOrEmpty(shooterSteamId) && _playByPlayEvents.Any((PlayByPlayEvent e) => e.PlayerSteamId == shooterSteamId && e.EventType == PlayByPlayEventType.Shot && e.PlayerTeam == (int)blockCheck2.ShooterTeam && e.GameTime >= currentGameTime3 - 3f);
							if (!flag5 && !flag6 && flag7)
							{
								ProcessBlock(blockCheck2.BlockerSteamId, shooterSteamId, blockCheck2.ShooterTeam, blockCheck2.BlockGameTime);
								_lastBlockWasCounted[blockCheck2.ShooterTeam] = true;
							}
							else if ((flag5 || flag6) && flag7)
							{
								Player playerBySteamId4 = MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayerBySteamId(blockCheck2.BlockerSteamId);
								Puck puck2 = MonoBehaviourSingleton<PuckManager>.Instance?.GetPuck();
								if (playerBySteamId4 != null && (bool)playerBySteamId4 && puck2 != null)
								{
									Vector3 position2 = puck2.transform.position;
									Vector3 velocity3 = puck2.GetComponent<Rigidbody>()?.linearVelocity ?? Vector3.zero;
									RecordPlayByPlayEventInternal(PlayByPlayEventType.Block, playerBySteamId4, position2, velocity3, "failed", null, blockCheck2.BlockGameTime);
								}
								_lastBlockWasCounted[blockCheck2.ShooterTeam] = true;
							}
							PlayerFunc.GetOtherTeamGoalie(blockCheck2.ShooterTeam);
							_checkIfPuckWasSaved[item4] = new SaveCheck();
							_checkIfPuckWasBlocked[item4] = new BlockCheck();
						}
						else if (++blockCheck2.FramesChecked > NetworkBehaviourSingleton<ServerManager>.Instance.ServerConfig.tickRate)
						{
							_checkIfPuckWasBlocked[item4] = new BlockCheck();
						}
					}
				}
				Puck puck3 = MonoBehaviourSingleton<PuckManager>.Instance.GetPuck();
				if ((bool)puck3)
				{
					_puckZCoordinateDifference = (puck3.Rigidbody.transform.position.z - _puckLastCoordinate.z) / 240f * (float)NetworkBehaviourSingleton<ServerManager>.Instance.ServerConfig.tickRate;
					_puckLastCoordinate = new Vector3(puck3.Rigidbody.transform.position.x, puck3.Rigidbody.transform.position.y, puck3.Rigidbody.transform.position.z);
				}
			}
			catch (Exception arg2)
			{
				Logging.LogError($"Error in PhysicsManager_Update_Patch Postfix().\n{arg2}", ServerConfig);
			}
		}
	}

	[HarmonyPatch(typeof(UIScoreboard), "AddPlayer")]
	public class UIScoreboard_AddPlayer_Patch
	{
		[HarmonyPostfix]
		public static void Postfix(UIScoreboard __instance, Player player)
		{
			try
			{
				if (ServerFunc.IsDedicatedServer())
				{
					return;
				}
				EnsureClientScoreboardState();
				if (!IsClientStatsUiAuthorized())
				{
					return;
				}
				TryApplyScoreboardStarTag(player);
				RefreshScoreboardSogCellForPlayer(player);
			}
			catch (Exception arg)
			{
				Logging.LogError($"Error in UIScoreboard_AddPlayer_Patch Postfix().\n{arg}", _clientConfig);
			}
		}
	}

	[HarmonyPatch(typeof(UIScoreboard), "StylePlayer")]
	public class UIScoreboard_StylePlayer_Patch
	{
		[HarmonyPostfix]
		public static void Postfix(UIScoreboard __instance, Player player)
		{
			try
			{
				if (ServerFunc.IsDedicatedServer())
				{
					return;
				}
				if (!IsClientStatsUiAuthorized())
				{
					return;
				}
				TryApplyScoreboardStarTag(player);
				RefreshScoreboardSogCellForPlayer(player, scheduleFollowUp: false);
			}
			catch (Exception arg)
			{
				Logging.LogError($"Error in UIScoreboard_StylePlayer_Patch Postfix().\n{arg}", _clientConfig);
			}
		}
	}

	[HarmonyPatch(typeof(UIScoreboard), "SortPlayers")]
	public class UIScoreboard_SortPlayers_Patch
	{
		[HarmonyPostfix]
		public static void Postfix(UIScoreboard __instance)
		{
			try
			{
				if (ServerFunc.IsDedicatedServer())
				{
					return;
				}
				Dictionary<Player, VisualElement> scoreboardPlayerMap = GetScoreboardPlayerMap();
				if (!IsClientStatsUiAuthorized() || scoreboardPlayerMap == null || scoreboardPlayerMap.Count == 0)
				{
					return;
				}
				foreach (KeyValuePair<Player, VisualElement> item in scoreboardPlayerMap)
				{
					Player key = item.Key;
					if (key != null && key)
					{
						RefreshScoreboardSogCellForPlayer(key, scheduleFollowUp: false);
					}
				}
			}
			catch (Exception arg)
			{
				Logging.LogError($"Error in UIScoreboard_SortPlayers_Patch Postfix().\n{arg}", _clientConfig);
			}
		}
	}

	[HarmonyPatch(typeof(UIView), "Show")]
	public class UIView_Show_Patch
	{
		[HarmonyPostfix]
		public static void Postfix(UIView __instance, bool __result)
		{
			try
			{
				if (!__result || ServerFunc.IsDedicatedServer() || !(__instance is UIScoreboard))
				{
					return;
				}
				Logging.Log("Scoreboard show hook fired. Running client scoreboard refresh.", _clientConfig);
				RefreshClientScoreboardVisibleState("UIView.Show", scheduleFollowUp: false, forceResetTracking: false);
			}
			catch (Exception arg)
			{
				Logging.LogError($"Error in UIView_Show_Patch Postfix().\n{arg}", _clientConfig);
			}
		}
	}

	[HarmonyPatch(typeof(UIView), "Hide")]
	public class UIView_Hide_Patch
	{
		[HarmonyPostfix]
		public static void Postfix(UIView __instance)
		{
			try
			{
				if (ServerFunc.IsDedicatedServer() || !(__instance is UIScoreboard))
				{
					return;
				}
				HideVisibleScoreboardTooltips(clearHoveredRows: true);
			}
			catch (Exception arg)
			{
				Logging.LogError($"Error in UIView_Hide_Patch Postfix().\n{arg}", _clientConfig);
			}
		}
	}

	[HarmonyPatch(typeof(Puck), "OnCollisionEnter")]
	public class Puck_OnCollisionEnter_Patch
	{
		[HarmonyPostfix]
		public static void Postfix(Puck __instance, Collision collision)
		{
			TraceCollisionHookEntry("Enter", __instance, collision);
			if (!ServerFunc.IsDedicatedServer() || _paused || NetworkBehaviourSingleton<GameManager>.Instance.Phase != GamePhase.Play || !_logic)
			{
				return;
			}
			try
			{
				if (ServerConfig.TrackPostHits)
				{
					string collisionObjectName = collision.gameObject?.name ?? "";
					if (ServerConfig.PostHitDiagnosticLogging && Mathf.Abs(__instance.transform.position.z) > 35f)
					{
						Logging.Log($"Post hit diagnostic collision near goal: {collisionObjectName}", ServerConfig);
					}
					if (IsGoalPostName(collisionObjectName) && !string.IsNullOrEmpty(_lastPostHitShooterSteamId))
					{
						Player playerBySteamId = MonoBehaviourSingleton<PlayerManager>.Instance?.GetPlayerBySteamId(_lastPostHitShooterSteamId);
						bool flag = (DateTime.UtcNow - _lastPostHitTime).TotalSeconds >= ServerConfig.PostHitCooldownSeconds;
						bool flag2 = (DateTime.UtcNow - _lastPostHitStickContactTime).TotalSeconds <= ServerConfig.PostShotFreshnessSeconds;
						bool flag3 = Mathf.Abs(_lastPostHitShotOrigin.z) <= ServerConfig.PostOriginZFilter;
						bool flag4 = playerBySteamId == null || !IsOwnGoalPost(playerBySteamId.Team, __instance.transform.position.z);
						if (flag && flag2 && flag3 && flag4)
						{
							_lastPostHitTime = DateTime.UtcNow;
							RecordPostHit(_lastPostHitShooterSteamId, __instance.transform.position, __instance.GetComponent<Rigidbody>()?.velocity ?? Vector3.zero, collisionObjectName, GetCurrentGameTime());
						}
					}
				}
				Player player = null;
				PlayerBody playerBodyV = null;
				Stick stick = SystemFunc.GetStick(collision);
				if (!stick)
				{
					playerBodyV = SystemFunc.GetPlayerBody(collision);
					Player player2 = SystemFunc.GetPlayer(playerBodyV);
					if (!(UnityEngine.Object)(object)playerBodyV || !player2)
					{
						if (TrackingDiagnosticsEnabled())
						{
							LogTrackingDiagnostic("collision-enter-unresolved", "OnCollisionEnter could not resolve a player from collision. " + GetCollisionDiagnosticLabel(collision), minIntervalMilliseconds: 1500);
						}
						return;
					}
					player = player2;
				}
				else
				{
					Player player3 = SystemFunc.GetPlayer(stick);
					if (!player3)
					{
						if (TrackingDiagnosticsEnabled())
						{
							LogTrackingDiagnostic("collision-enter-stick-without-player", "OnCollisionEnter resolved a stick without an owning player. " + GetCollisionDiagnosticLabel(collision), minIntervalMilliseconds: 1500);
						}
						return;
					}
					player = player3;
				}
				if (stick)
				{
					UpdatePostHitShooterFromStick(stick, MonoBehaviourSingleton<PuckManager>.Instance?.GetPuck()?.transform.position ?? Vector3.zero);
				}
				string text = player.SteamId.Value.ToString();
				if (stick != null || playerBodyV != null)
				{
					if (stick != null)
					{
						UpdatePostHitShooterFromStick(stick, __instance.transform.position);
					}
					if (TrackingDiagnosticsEnabled())
					{
						LogTrackingDiagnostic("collision-enter-touch-" + text, $"OnCollisionEnter resolved {GetDiagnosticPlayerLabel(player)} via {(stick != null ? "stick" : "body")} at puckPos={FormatVector3(__instance.transform.position)}.", minIntervalMilliseconds: 500);
					}
					ProcessPuckTouch(text, player, __instance);
					_lastPossessionTouchTime[text] = DateTime.UtcNow;
				}
				if (!_playersCurrentPuckTouch.TryGetValue(text, out var value3))
				{
					value3 = new Stopwatch();
					value3.Start();
					_playersCurrentPuckTouch.Add(text, value3);
				}
				string item = _lastPlayerOnPuckTipIncludedSteamId[_lastTeamOnPuckTipIncluded].SteamId;
				if (!_lastTimeOnCollisionStayOrExitWasCalled.TryGetValue(text, out var value4))
				{
					value4 = new Stopwatch();
					value4.Start();
					_lastTimeOnCollisionStayOrExitWasCalled.Add(text, value4);
				}
				else if (value4.ElapsedMilliseconds > ServerConfig.MaxPossessionMilliseconds || (!string.IsNullOrEmpty(item) && item != text))
				{
					value3.Restart();
					if (!string.IsNullOrEmpty(item) && item != text && _playersCurrentPuckTouch.TryGetValue(item, out var value5))
					{
						value5.Reset();
					}
				}
				PlayerTeam otherTeam = TeamFunc.GetOtherTeam(player.Team);
				if (_puckRaycast.PuckIsGoingToNet[player.Team])
				{
					if (PlayerFunc.IsGoalie(player) && (double)Math.Abs(player.PlayerBody.Rigidbody.transform.position.z) > 13.5)
					{
						PlayerTeam playerTeam = otherTeam;
						string item2 = _lastPlayerOnPuckTipIncludedSteamId[playerTeam].SteamId;
						if (!string.IsNullOrEmpty(item2))
						{
							bool flag = stick != null;
							string goalieSaveSurfaceName = GetGoalieSaveSurfaceName(stick, collision);
							if (_checkIfPuckWasSaved.TryGetValue(player.Team, out var value6) && value6.HasToCheck)
							{
								flag = value6.HitStick || flag;
								goalieSaveSurfaceName = MergeGoalieSaveSurface(value6.SaveSurface, goalieSaveSurfaceName);
							}
							_checkIfPuckWasSaved[player.Team] = new SaveCheck
							{
								HasToCheck = true,
								ShooterSteamId = item2,
								ShooterTeam = playerTeam,
								HitStick = flag,
								SaveSurface = goalieSaveSurfaceName
							};
						}
					}
					else
					{
						PlayerTeam playerTeam2 = otherTeam;
						if (!string.IsNullOrEmpty(_lastPlayerOnPuckTipIncludedSteamId[playerTeam2].SteamId))
						{
							_checkIfPuckWasBlocked[player.Team] = new BlockCheck
							{
								HasToCheck = true,
								BlockerSteamId = player.SteamId.Value.ToString(),
								ShooterTeam = playerTeam2,
								BlockGameTime = GetCurrentGameTime()
							};
						}
					}
				}
				else
				{
					if (_lastTeamOnPuckTipIncluded != otherTeam || !PlayerFunc.IsGoalie(player) || !((double)Math.Abs(player.PlayerBody.Rigidbody.transform.position.z) > 13.5) || ((player.Team != PlayerTeam.Blue || !(_puckZCoordinateDifference > ServerConfig.GoalieSaveCreaseSystemZDelta)) && (player.Team != PlayerTeam.Red || !(_puckZCoordinateDifference < 0f - ServerConfig.GoalieSaveCreaseSystemZDelta))))
					{
						return;
					}
					double num = 0.0;
					double num2 = 0.0;
					double num3 = 0.0;
					double num4 = 0.0;
					if (player.Team != PlayerTeam.Blue)
					{
						(num, num2) = ZoneFunc.ICE_X_POSITIONS[IceElement.RedTeam_BluePaint];
						(num3, num4) = ZoneFunc.ICE_Z_POSITIONS[IceElement.RedTeam_BluePaint];
					}
					else
					{
						(num, num2) = ZoneFunc.ICE_X_POSITIONS[IceElement.BlueTeam_BluePaint];
						(num3, num4) = ZoneFunc.ICE_Z_POSITIONS[IceElement.BlueTeam_BluePaint];
					}
					bool flag2 = true;
					if ((double)(player.PlayerBody.Rigidbody.transform.position.x - ServerConfig.GoalieRadius) < num || (double)(player.PlayerBody.Rigidbody.transform.position.x + ServerConfig.GoalieRadius) > num2 || (double)(player.PlayerBody.Rigidbody.transform.position.z - ServerConfig.GoalieRadius) < num3 || (double)(player.PlayerBody.Rigidbody.transform.position.z + ServerConfig.GoalieRadius) > num4)
					{
						flag2 = false;
					}
					if (!flag2)
					{
						return;
					}
					PlayerTeam otherTeam2 = TeamFunc.GetOtherTeam(player.Team);
					string item3 = _lastPlayerOnPuckTipIncludedSteamId[otherTeam2].SteamId;
					if (!string.IsNullOrEmpty(item3))
					{
						bool flag3 = stick != null;
						string goalieSaveSurfaceName2 = GetGoalieSaveSurfaceName(stick, collision);
						if (_checkIfPuckWasSaved.TryGetValue(player.Team, out var value7) && value7.HasToCheck)
						{
							flag3 = value7.HitStick || flag3;
							goalieSaveSurfaceName2 = MergeGoalieSaveSurface(value7.SaveSurface, goalieSaveSurfaceName2);
						}
						_checkIfPuckWasSaved[player.Team] = new SaveCheck
						{
							HasToCheck = true,
							ShooterSteamId = item3,
							ShooterTeam = otherTeam2,
							HitStick = flag3,
							SaveSurface = goalieSaveSurfaceName2
						};
					}
				}
			}
			catch (Exception arg)
			{
				Logging.LogError($"Error in Puck_OnCollisionEnter_Patch Postfix().\n{arg}", ServerConfig);
			}
		}
	}

	[HarmonyPatch(typeof(Puck), "OnCollisionStay")]
	public class Puck_OnCollisionStay_Patch
	{
		[HarmonyPostfix]
		public static void Postfix(Puck __instance, Collision collision)
		{
			TraceCollisionHookEntry("Stay", __instance, collision);
			try
			{
				if (!ServerFunc.IsDedicatedServer() || _paused || NetworkBehaviourSingleton<GameManager>.Instance.Phase != GamePhase.Play || !_logic)
				{
					return;
				}
				Stick stick = SystemFunc.GetStick(collision);
				Player player;
				if (!stick)
				{
					PlayerBody playerBodyV = SystemFunc.GetPlayerBody(collision);
					Player player2 = SystemFunc.GetPlayer(playerBodyV);
					if (!(UnityEngine.Object)(object)playerBodyV || !player2)
					{
						if (TrackingDiagnosticsEnabled())
						{
							LogTrackingDiagnostic("collision-stay-unresolved", "OnCollisionStay could not resolve a player from collision. " + GetCollisionDiagnosticLabel(collision), minIntervalMilliseconds: 1500);
						}
						return;
					}
					player = player2;
				}
				else
				{
					Player player3 = SystemFunc.GetPlayer(stick);
					if (!player3)
					{
						if (TrackingDiagnosticsEnabled())
						{
							LogTrackingDiagnostic("collision-stay-stick-without-player", "OnCollisionStay resolved a stick without an owning player. " + GetCollisionDiagnosticLabel(collision), minIntervalMilliseconds: 1500);
						}
						return;
					}
					player = player3;
				}
				string text = player.SteamId.Value.ToString();
				if (!_lastTimeOnCollisionStayOrExitWasCalled.TryGetValue(text, out var value))
				{
					value = new Stopwatch();
					value.Start();
					_lastTimeOnCollisionStayOrExitWasCalled.Add(text, value);
				}
				value.Restart();
				string item = _lastPlayerOnPuckTipIncludedSteamId[player.Team].SteamId;
				if (text != item)
				{
					_lastPlayerOnPuckTipIncludedSteamId[player.Team] = (text, DateTime.UtcNow);
				}
				_lastTeamOnPuckTipIncluded = player.Team;
				bool flag = PuckFunc.PuckIsTipped(text, ServerConfig.MaxTippedMilliseconds, _playersCurrentPuckTouch, _lastTimeOnCollisionStayOrExitWasCalled);
				if (flag && !_wasTippedLastFrame)
				{
					List<string> list = new List<string>();
					HashSet<PlayerTeam> hashSet = new HashSet<PlayerTeam>();
					DateTime utcNow = DateTime.UtcNow;
					foreach (KeyValuePair<string, Stopwatch> item2 in _playersCurrentPuckTouch)
					{
						string key = item2.Key;
						Player playerBySteamId = MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayerBySteamId(key);
						if (!(playerBySteamId != null) || !playerBySteamId || PlayerFunc.IsGoalie(playerBySteamId) || !item2.Value.IsRunning || item2.Value.ElapsedMilliseconds >= ServerConfig.MaxTippedMilliseconds * 2)
						{
							continue;
						}
						if (_lastTimeOnCollisionStayOrExitWasCalled.TryGetValue(key, out var value2))
						{
							if (value2.IsRunning && value2.ElapsedMilliseconds < ServerConfig.MaxTippedMilliseconds * 2)
							{
								list.Add(key);
								hashSet.Add(playerBySteamId.Team);
							}
						}
						else
						{
							list.Add(key);
							hashSet.Add(playerBySteamId.Team);
						}
					}
					if (hashSet.Contains(PlayerTeam.Red) && hashSet.Contains(PlayerTeam.Blue) && list.Count >= 2 && (utcNow - _lastPuckBattleTime).TotalMilliseconds >= (double)PUCK_BATTLE_COOLDOWN_MS)
					{
						PlayerTeam playerTeam = PlayerTeam.None;
						if (_currentTeamInPossession != PlayerTeam.None)
						{
							playerTeam = _currentTeamInPossession;
							if (playerTeam == PlayerTeam.Red)
							{
							}
						}
						else if (_lastPossession.Team != PlayerTeam.None)
						{
							playerTeam = _lastPossession.Team;
							if (playerTeam == PlayerTeam.Red)
							{
							}
						}
						else if (_lastTeamOnPuck != PlayerTeam.None)
						{
							playerTeam = _lastTeamOnPuck;
							_ = 2;
						}
						Puck puck = MonoBehaviourSingleton<PuckManager>.Instance?.GetPuck();
						if (puck != null)
						{
							Vector3 position = puck.transform.position;
							Vector3 velocity = puck.GetComponent<Rigidbody>()?.velocity ?? Vector3.zero;
							string text2 = playerTeam switch
							{
								PlayerTeam.None => null,
								PlayerTeam.Red => "Blue",
								_ => "Red",
							};
							foreach (string item3 in list)
							{
								Player playerBySteamId2 = MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayerBySteamId(item3);
								if (playerBySteamId2 != null && (bool)playerBySteamId2)
								{
									string flags = ((playerBySteamId2.Team == playerTeam) ? "defending" : "contesting");
									string teamInPossessionOverride = text2;
									RecordPlayByPlayEventInternal(PlayByPlayEventType.PuckBattle, playerBySteamId2, position, velocity, "neutral", flags, null, skipPossessionReset: true, null, teamInPossessionOverride);
								}
							}
							_lastPuckBattleTime = utcNow;
						}
					}
				}
				if (!flag)
				{
					_lastTeamOnPuck = player.Team;
					_lastPlayerOnPuckSteamId[player.Team] = (text, DateTime.UtcNow);
				}
				_wasTippedLastFrame = flag;
			}
			catch (Exception arg)
			{
				Logging.LogError($"Error in Puck_OnCollisionStay_Patch Postfix().\n{arg}", ServerConfig);
			}
		}
	}

	[HarmonyPatch(typeof(Puck), "OnCollisionExit")]
	public class Puck_OnCollisionExit_Patch
	{
		[HarmonyPostfix]
		public static void Postfix(Puck __instance, Collision collision)
		{
			TraceCollisionHookEntry("Exit", __instance, collision);
			try
			{
				if (!ServerFunc.IsDedicatedServer() || _paused || NetworkBehaviourSingleton<GameManager>.Instance.Phase != GamePhase.Play || !_logic)
				{
					return;
				}
				PlayerBody playerBodyV = null;
				Stick stick = SystemFunc.GetStick(collision);
				Player player = null;
				if (stick)
				{
					UpdatePostHitShooterFromStick(stick, __instance.transform.position);
					player = SystemFunc.GetPlayer(stick);
					if (!player)
					{
						if (TrackingDiagnosticsEnabled())
						{
							LogTrackingDiagnostic("collision-exit-stick-without-player", "OnCollisionExit resolved a stick without an owning player. " + GetCollisionDiagnosticLabel(collision), minIntervalMilliseconds: 1500);
						}
						return;
					}
				}
				else
				{
					playerBodyV = SystemFunc.GetPlayerBody(collision);
					if ((UnityEngine.Object)(object)playerBodyV)
					{
						player = SystemFunc.GetPlayer(playerBodyV);
					}
				}
				if (player == null || !player)
				{
					if (TrackingDiagnosticsEnabled())
					{
						LogTrackingDiagnostic("collision-exit-unresolved", "OnCollisionExit could not resolve a player from collision. " + GetCollisionDiagnosticLabel(collision), minIntervalMilliseconds: 1500);
					}
					return;
				}
				string text = player.SteamId.Value.ToString();
				if (!__instance.IsTouchingStick && !PlayerFunc.IsGoalie(player))
				{
					_lastShotWasCounted[player.Team] = false;
					_lastBlockWasCounted[player.Team] = false;
					Puck puck = MonoBehaviourSingleton<PuckManager>.Instance?.GetPuck();
					if (player != null && (bool)player && puck != null)
					{
						Vector3 position = puck.transform.position;
						Vector3 vector = puck.GetComponent<Rigidbody>()?.linearVelocity ?? Vector3.zero;
						Vector3 position2 = player.PlayerBody.Rigidbody.transform.position;
						float magnitude = vector.magnitude;
						bool flag = player.Team == PlayerTeam.Blue;
						bool flag2 = (flag ? (vector.z < 0f) : (vector.z > 0f));
						bool flag3 = true;
						flag3 = ((!flag) ? (vector.z > 0f && position.z < 40f) : (vector.z < 0f && position.z > -40f));
						Vector3 vector2 = new Vector3(0f, 1f, flag ? (-40f) : 40f) - position;
						float magnitude2 = vector2.magnitude;
						bool flag4 = true;
						if (magnitude > 0.1f && magnitude2 > 0.1f)
						{
							Vector3 vector3 = new Vector3(vector2.x, vector2.y * 0.5f, vector2.z);
							Vector3 vector4 = new Vector3(vector.x, vector.y * 0.5f, vector.z);
							float magnitude3 = vector3.magnitude;
							float magnitude4 = vector4.magnitude;
							if (magnitude3 > 0.001f && magnitude4 > 0.001f)
							{
								Vector3 lhs = vector3 / magnitude3;
								Vector3 rhs = vector4 / magnitude4;
								float num = Vector3.Dot(lhs, rhs);
								float num2 = ((magnitude2 > 50f) ? 0.978f : 0.951f);
								flag4 = num >= num2;
							}
						}
						float num3 = 12f;
						if (magnitude2 > 50f)
						{
							num3 = 25f;
						}
						bool flag5 = false;
						if (_lastPlayerOnPuckTipIncludedSteamId.TryGetValue(player.Team, out (string, DateTime) value))
						{
							flag5 = value.Item1 == text;
						}
						float currentGameTime = GetCurrentGameTime();
						float value2;
						float num4 = (_lastShotAttemptGameTime.TryGetValue(text, out value2) ? value2 : (-1f));
						float num5 = currentGameTime - num4;
						if (magnitude >= num3 && flag2 && flag3 && flag4 && flag5 && (num4 < 0f || num5 >= 1f))
						{
							PlayByPlayEvent playByPlayEvent = RecordPlayByPlayEventInternal(flags: DetermineShotFlag(position, player.Team), eventType: PlayByPlayEventType.Shot, player: player, position: position, velocity: vector, outcome: "attempt");
							IncrementShotAttemptForPlayer(text, player.Team, playByPlayEvent);
							_lastShotAttemptGameTime[text] = currentGameTime;
						}
						if (magnitude >= 12f && flag2 && flag3 && flag4 && flag5 && (num4 < 0f || num5 >= 1f))
						{
							_pendingShotReleases[player.Team] = (text, position2, position, vector, DateTime.UtcNow);
						}
					}
				}
				if (__instance.IsTouchingStick || stick != null || playerBodyV != null)
				{
					if (!_lastTimeOnCollisionStayOrExitWasCalled.TryGetValue(text, out var value4))
					{
						value4 = new Stopwatch();
						value4.Start();
						_lastTimeOnCollisionStayOrExitWasCalled.Add(text, value4);
					}
					value4.Restart();
					_lastPlayerOnPuckTipIncludedSteamId[player.Team] = (text, DateTime.UtcNow);
					_lastTeamOnPuckTipIncluded = player.Team;
					if (!PuckFunc.PuckIsTipped(text, ServerConfig.MaxTippedMilliseconds, _playersCurrentPuckTouch, _lastTimeOnCollisionStayOrExitWasCalled))
					{
						_lastTeamOnPuck = player.Team;
						_lastPlayerOnPuckSteamId[player.Team] = (text, DateTime.UtcNow);
					}
				}
			}
			catch (Exception arg)
			{
				Logging.LogError($"Error in Puck_OnCollisionExit_Patch Postfix().\n{arg}", ServerConfig);
			}
		}
	}

	[HarmonyPatch(typeof(PlayerBody), "OnCollisionEnter")]
	public class PlayerBodyV2_OnCollisionEnter_Patch
	{
		[HarmonyPostfix]
		public static void Postfix(PlayerBody __instance, Collision collision)
		{
			if (!ServerFunc.IsDedicatedServer() || _paused || NetworkBehaviourSingleton<GameManager>.Instance.Phase != GamePhase.Play || !_logic)
			{
				return;
			}
			try
			{
				PlayerBody playerBodyV = SystemFunc.GetPlayerBody(collision);
				Player player = SystemFunc.GetPlayer(playerBodyV);
				Player player2 = SystemFunc.GetPlayer(__instance);
				if (!(UnityEngine.Object)(object)playerBodyV || !player || !player.IsCharacterSpawned || !(UnityEngine.Object)(object)__instance || !player2 || !player2.IsCharacterSpawned || player.Team == player2.Team)
				{
					return;
				}
				string text = player.SteamId.Value.ToString();
				if (!_playerIsDown.TryGetValue(text, out var value))
				{
					value = false;
				}
				string text2 = player2.SteamId.Value.ToString();
				bool value2;
				if (!value && (SystemFunc.GetHasFallen(playerBodyV) || SystemFunc.GetHasSlipped(playerBodyV)))
				{
					if (_playerIsDown.TryGetValue(text, out value2))
					{
						_playerIsDown[text] = true;
					}
					else
					{
						_playerIsDown.Add(text, value: true);
					}
					if (SystemFunc.GetHasFallen(player2.PlayerBody) || SystemFunc.GetHasSlipped(player2.PlayerBody))
					{
						if (_playerIsDown.TryGetValue(text2, out value2))
						{
							_playerIsDown[text2] = true;
						}
						else
						{
							_playerIsDown.Add(text2, value: true);
						}
						return;
					}
					ProcessHit(player2.SteamId.Value.ToString(), player.SteamId.Value.ToString());
				}
				if (SystemFunc.GetHasFallen(player2.PlayerBody) || SystemFunc.GetHasSlipped(player2.PlayerBody))
				{
					if (_playerIsDown.TryGetValue(text2, out value2))
					{
						_playerIsDown[text2] = true;
					}
					else
					{
						_playerIsDown.Add(text2, value: true);
					}
				}
			}
			catch (Exception arg)
			{
				Logging.LogError(string.Format("Error in {0} Postfix().\n{1}", "PlayerBodyV2_OnCollisionEnter_Patch", arg), ServerConfig);
			}
		}
	}

	[HarmonyPatch(typeof(PlayerBody), "OnStandUp")]
	public class PlayerBodyV2_OnStandUp_Patch
	{
		[HarmonyPostfix]
		public static void Postfix(PlayerBody __instance)
		{
			if (!ServerFunc.IsDedicatedServer() || _paused || NetworkBehaviourSingleton<GameManager>.Instance.Phase != GamePhase.Play || !_logic)
			{
				return;
			}
			try
			{
				Player player = SystemFunc.GetPlayer(__instance);
				if (!player)
				{
					return;
				}
				string text = player.SteamId.Value.ToString();
				if (_playerIsDown.TryGetValue(text, out var _))
				{
					_playerIsDown[text] = false;
				}
				else
				{
					_playerIsDown.Add(text, value: false);
				}
			}
			catch (Exception arg)
			{
				Logging.LogError(string.Format("Error in {0} Postfix().\n{1}", "PlayerBodyV2_OnStandUp_Patch", arg), ServerConfig);
			}
		}
	}

	[HarmonyPatch(typeof(GameManager), "Server_SetGameState")]
	public class GameManager_Server_SetGameState_Patch
	{
		[HarmonyPrefix]
		public static bool Prefix(GameManager __instance, GamePhase? phase, int? tick, int? period, int? blueScore, int? redScore, bool? isOvertime)
		{
			try
			{
				if (!ServerFunc.IsDedicatedServer() || !_logic)
				{
					return true;
				}
				if (!phase.HasValue)
				{
					return true;
				}
				GamePhase previousPhase = __instance.GameState.Value.Phase;
				if (previousPhase == GamePhase.Play && phase.Value != GamePhase.Play)
				{
					SampleLiveTimeOnIceFromGameClock();
					TryExportLiveGameStats(force: true);
				}
				GamePhase newPhase = phase.Value;
				if (newPhase == previousPhase)
				{
					return true;
				}
				if (newPhase == GamePhase.Play)
				{
					_lastRecordedPhase = newPhase;
				}
				if (newPhase == GamePhase.PreGame || newPhase == GamePhase.Warmup || newPhase == GamePhase.FaceOff || newPhase == GamePhase.GameOver || newPhase == GamePhase.PostGame)
				{
					ResetPuckWasSavedOrBlockedChecks();
					_puckLastCoordinate = Vector3.zero;
					_puckZCoordinateDifference = 0f;
					switch (newPhase)
					{
					case GamePhase.PreGame:
						if (_warmupLiveResetAppliedForUpcomingGame)
						{
							FinalizeWarmupStartedGameReset();
						}
						else if (previousPhase == GamePhase.Warmup || previousPhase == GamePhase.GameOver || previousPhase == GamePhase.PostGame || previousPhase == GamePhase.Play)
						{
							if (previousPhase == GamePhase.Play && _playByPlayEvents.Count > 0 && !_exportedAtResetGameState && !_exportedAtGameOver)
							{
								int num7 = 0;
								int count2 = _playByPlayEvents.Count;
								try
								{
									num7 = (from e in _playByPlayEvents
										where !string.IsNullOrEmpty(e.PlayerSteamId)
										select e.PlayerSteamId).Distinct().Count();
								}
								catch
								{
								}
								if (!ServerConfig.EnableExportLimit || (num7 >= 8 && count2 >= 300))
								{
									Logging.Log("Early game end (Play -> PreGame) - exporting before reset", ServerConfig);
									if (!_playByPlayEvents.Any((PlayByPlayEvent e) => e.EventType == PlayByPlayEventType.GameEnd))
									{
										int currentPeriod3 = GetCurrentPeriod();
										float num8 = ((_playByPlayEvents.Count > 0) ? _playByPlayEvents.Max((PlayByPlayEvent e) => e.GameTime) : 0f);
										float num9 = ((num8 > 0f) ? num8 : GetCurrentGameTime());
										PlayByPlayEvent playByPlayEvent2 = new PlayByPlayEvent
										{
											EventId = _nextPlayByPlayEventId++,
											EventType = PlayByPlayEventType.GameEnd,
											GameTime = num9,
											Period = currentPeriod3,
											PlayerSteamId = "",
											PlayerName = "",
											PlayerTeam = 0,
											PlayerPosition = "",
											PlayerJersey = 0,
											PlayerSpeed = 0f,
											Zone = EventZone.Neutral,
											Position = Vector3.zero,
											Velocity = Vector3.zero,
											ForceMagnitude = 0f,
											Outcome = "end",
											Flags = "",
											Team = "",
											TeamInPossession = ((_currentTeamInPossession == PlayerTeam.None) ? "" : ((_currentTeamInPossession == PlayerTeam.Red) ? "Blue" : "Red")),
											CurrentPlayInPossession = _currentPlayInPossession.ToString(),
											ScoreState = GetScoreState(PlayerTeam.None),
											Timestamp = DateTime.UtcNow
										};
										CaptureTeamRosterData(playByPlayEvent2);
										int index2 = _playByPlayEvents.Count;
										for (int num10 = 0; num10 < _playByPlayEvents.Count; num10++)
										{
											if (_playByPlayEvents[num10].GameTime >= num9)
											{
												index2 = num10;
												break;
											}
										}
										_playByPlayEvents.Insert(index2, playByPlayEvent2);
									}
									ExportGameStats();
									_exportedAtResetGameState = true;
									if (NetworkBehaviourSingleton<GameManager>.Instance != null && CanSendSystemChatMessage())
									{
										string text2 = GetCurrentGameReferenceIdOrCreate();
										string arg3 = GetSanitizedExportFileHeaderName() + "_" + text2 + "_stats";
										int num11 = ((NetworkBehaviourSingleton<GameManager>.Instance.GameState != null) ? NetworkBehaviourSingleton<GameManager>.Instance.GameState.Value.RedScore : 0);
										int num12 = ((NetworkBehaviourSingleton<GameManager>.Instance.GameState != null) ? NetworkBehaviourSingleton<GameManager>.Instance.GameState.Value.BlueScore : 0);
										SendSystemChatMessage($"Game ended early. Stats exported - Final Score: Red {num11} - Blue {num12} | File: {arg3}");
									}
								}
							}
							ResetForExplicitNewGameStart();
						}
						_faceoffRecordedForCurrentPeriod = false;
						_lastRecordedPhase = newPhase;
						break;
					case GamePhase.Warmup:
					{
						bool flag = previousPhase == GamePhase.GameOver || previousPhase == GamePhase.PostGame;
						if (!_exportedAtResetGameState && previousPhase != GamePhase.None && previousPhase != GamePhase.Warmup && (!flag || !_exportedAtGameOver) && _playByPlayEvents.Count > 0)
						{
							int num = 0;
							int count = _playByPlayEvents.Count;
							try
							{
								num = (from e in _playByPlayEvents
									where !string.IsNullOrEmpty(e.PlayerSteamId)
									select e.PlayerSteamId).Distinct().Count();
							}
							catch (Exception arg)
							{
								Logging.LogError($"Error counting unique players for early game end export: {arg}", ServerConfig);
							}
							if (!ServerConfig.EnableExportLimit || (num >= 8 && count >= 300))
							{
								if (!ServerConfig.EnableExportLimit)
								{
									Logging.Log($"Early game end detected (transition to Warmup from {previousPhase}) - exporting stats (export limit disabled)", ServerConfig);
								}
								else
								{
									Logging.Log($"Early game end detected (transition to Warmup from {previousPhase}) - exporting stats", ServerConfig);
								}
								if (!_playByPlayEvents.Any((PlayByPlayEvent e) => e.EventType == PlayByPlayEventType.GameEnd))
								{
									int currentPeriod2 = GetCurrentPeriod();
									float num2 = ((_playByPlayEvents.Count > 0) ? _playByPlayEvents.Max((PlayByPlayEvent e) => e.GameTime) : 0f);
									float num3 = ((num2 > 0f) ? num2 : GetCurrentGameTime());
									PlayByPlayEvent playByPlayEvent = new PlayByPlayEvent
									{
										EventId = _nextPlayByPlayEventId++,
										EventType = PlayByPlayEventType.GameEnd,
										GameTime = num3,
										Period = currentPeriod2,
										PlayerSteamId = "",
										PlayerName = "",
										PlayerTeam = 0,
										PlayerPosition = "",
										PlayerJersey = 0,
										PlayerSpeed = 0f,
										Zone = EventZone.Neutral,
										Position = Vector3.zero,
										Velocity = Vector3.zero,
										ForceMagnitude = 0f,
										Outcome = "end",
										Flags = "",
										Team = "",
										TeamInPossession = ((_currentTeamInPossession == PlayerTeam.None) ? "" : ((_currentTeamInPossession == PlayerTeam.Red) ? "Blue" : "Red")),
										CurrentPlayInPossession = _currentPlayInPossession.ToString(),
										ScoreState = GetScoreState(PlayerTeam.None),
										Timestamp = DateTime.UtcNow
									};
									CaptureTeamRosterData(playByPlayEvent);
									int index = _playByPlayEvents.Count;
									for (int num4 = 0; num4 < _playByPlayEvents.Count; num4++)
									{
										if (_playByPlayEvents[num4].GameTime >= num3)
										{
											index = num4;
											break;
										}
									}
									_playByPlayEvents.Insert(index, playByPlayEvent);
								}
								ExportGameStats();
								if (NetworkBehaviourSingleton<GameManager>.Instance != null && CanSendSystemChatMessage())
								{
									string text = GetCurrentGameReferenceIdOrCreate();
									string arg2 = GetSanitizedExportFileHeaderName() + "_" + text + "_stats";
									int num5 = ((NetworkBehaviourSingleton<GameManager>.Instance.GameState != null) ? NetworkBehaviourSingleton<GameManager>.Instance.GameState.Value.RedScore : 0);
									int num6 = ((NetworkBehaviourSingleton<GameManager>.Instance.GameState != null) ? NetworkBehaviourSingleton<GameManager>.Instance.GameState.Value.BlueScore : 0);
									SendSystemChatMessage($"Game ended early. Stats exported - Final Score: Red {num5} - Blue {num6} | File: {arg2}");
								}
							}
							else if (ServerConfig.EnableExportLimit)
							{
								Logging.Log($"Early game end detected (transition to Warmup from {previousPhase}) - Export skipped: {num} unique players (need 8+), {count} events (need 300+)", ServerConfig);
							}
						}
						CapturePreviousGameSnapshotIfAvailable(ResolveWarmupSnapshotCaptureReason());
						PrepareWarmupStartedGameReset();
						_lastRecordedPhase = newPhase;
						break;
					}
					case GamePhase.FaceOff:
					{
						if (_warmupLiveResetAppliedForUpcomingGame)
						{
							FinalizeWarmupStartedGameReset();
						}
						if (_gameStartTime == 0f)
						{
							_gameStartTime = Time.time;
							if (string.IsNullOrEmpty(_currentGameReferenceId))
							{
									_currentGameReferenceId = CreateGameReferenceId(DateTime.UtcNow);
							}
						}
						BroadcastNetworkPerformanceGameReference();
						int currentPeriod = GetCurrentPeriod();
						if (_lastTrackedPeriod == 0 && currentPeriod > 0)
						{
							_lastTrackedPeriod = currentPeriod;
						}
						if (currentPeriod > 0 && currentPeriod != _lastTrackedPeriod && _lastTrackedPeriod > 0)
						{
							ResolvePendingFaceoffOnTrackingStop();
							TryExportLiveGameStats(force: true);
							Logging.Log($"Period transition detected: {_lastTrackedPeriod} -> {currentPeriod}. Resolved pending faceoff.", ServerConfig);
						}
						_lastTrackedPeriod = currentPeriod;
						if (!_faceoffRecordedForCurrentPeriod && (previousPhase == GamePhase.PreGame || previousPhase == GamePhase.None || previousPhase == GamePhase.Warmup))
						{
							ResolvePendingFaceoffOnTrackingStop();
							RecordFaceoffEvent();
							_faceoffRecordedForCurrentPeriod = true;
							_trackingFaceoffOutcome = true;
							_faceoffPossessionChainCount = 0;
							_faceoffPossessionTeam = PlayerTeam.None;
							_faceoffTotalIncremented = false;
						}
						_lastRecordedPhase = newPhase;
						break;
					}
					case GamePhase.GameOver:
					case GamePhase.PostGame:
						_lastRecordedPhase = newPhase;
						break;
					default:
						_lastRecordedPhase = newPhase;
						break;
					}
					foreach (PlayerTeam item5 in new List<PlayerTeam>(_lastPlayerOnPuckTipIncludedSteamId.Keys))
					{
						_lastPlayerOnPuckTipIncludedSteamId[item5] = ("", DateTime.MinValue);
					}
					foreach (PlayerTeam item6 in new List<PlayerTeam>(_lastPlayerOnPuckSteamId.Keys))
					{
						_lastPlayerOnPuckSteamId[item6] = ("", DateTime.MinValue);
					}
					foreach (PlayerTeam item7 in new List<PlayerTeam>(_lastShotWasCounted.Keys))
					{
						_lastShotWasCounted[item7] = true;
					}
					foreach (PlayerTeam item8 in new List<PlayerTeam>(_lastBlockWasCounted.Keys))
					{
						_lastBlockWasCounted[item8] = true;
					}
					foreach (Stopwatch value11 in _playersLastTimePuckPossession.Values)
					{
						value11.Stop();
					}
					_playersLastTimePuckPossession.Clear();
					_lastPossession = new Possession();
					_currentTeamInPossession = PlayerTeam.None;
					_currentPlayInPossession = 0;
					foreach (Stopwatch value12 in _lastTimeOnCollisionStayOrExitWasCalled.Values)
					{
						value12.Stop();
					}
					_lastTimeOnCollisionStayOrExitWasCalled.Clear();
					foreach (Stopwatch value13 in _playersCurrentPuckTouch.Values)
					{
						value13.Stop();
					}
					_playersCurrentPuckTouch.Clear();
					if (newPhase == GamePhase.GameOver)
					{
						if (!_playByPlayEvents.Any((PlayByPlayEvent e) => e.EventType == PlayByPlayEventType.GameEnd))
						{
							int currentPeriod4 = GetCurrentPeriod();
							float num13 = ((_playByPlayEvents.Count > 0) ? _playByPlayEvents.Max((PlayByPlayEvent e) => e.GameTime) : 0f);
							float num14 = ((currentPeriod4 != 3 && (!(num13 >= 895f) || !(num13 <= 905f))) ? ((num13 > 0f) ? num13 : GetCurrentGameTime()) : 900f);
							PlayByPlayEvent playByPlayEvent3 = new PlayByPlayEvent
							{
								EventId = _nextPlayByPlayEventId++,
								EventType = PlayByPlayEventType.GameEnd,
								GameTime = num14,
								Period = currentPeriod4,
								PlayerSteamId = "",
								PlayerName = "",
								PlayerTeam = 0,
								PlayerPosition = "",
								PlayerJersey = 0,
								PlayerSpeed = 0f,
								Zone = EventZone.Neutral,
								Position = Vector3.zero,
								Velocity = Vector3.zero,
								ForceMagnitude = 0f,
								Outcome = "end",
								Flags = "",
								Team = "",
								TeamInPossession = ((_currentTeamInPossession == PlayerTeam.None) ? "" : ((_currentTeamInPossession == PlayerTeam.Red) ? "Blue" : "Red")),
								CurrentPlayInPossession = _currentPlayInPossession.ToString(),
								ScoreState = GetScoreState(PlayerTeam.None),
								Timestamp = DateTime.UtcNow
							};
							CaptureTeamRosterData(playByPlayEvent3);
							int index3 = _playByPlayEvents.Count;
							for (int num15 = 0; num15 < _playByPlayEvents.Count; num15++)
							{
								if (_playByPlayEvents[num15].GameTime >= num14)
								{
									index3 = num15;
									break;
								}
							}
							_playByPlayEvents.Insert(index3, playByPlayEvent3);
							Logging.Log($"Recorded GameEnd event at gameTime {num14:F3} (period {currentPeriod4}).", ServerConfig);
						}
						string text3 = "";
						PlayerTeam playerTeam = PlayerTeam.None;
						try
						{
							int currentBlueScore = __instance.GameState.Value.BlueScore;
							int currentRedScore = __instance.GameState.Value.RedScore;
							if (currentBlueScore > currentRedScore)
							{
								playerTeam = PlayerTeam.Blue;
								int num16 = 0;
								int num17 = 0;
								foreach (GoalInfo item9 in _goals.OrderBy((GoalInfo g) => g.GameTime))
								{
									if (item9.Team == "Blue")
									{
										num16++;
									}
									else
									{
										num17++;
									}
									if (item9.Team == "Blue" && num16 > num17 && num16 == currentRedScore + 1)
									{
										text3 = item9.Scorer;
										break;
									}
								}
							}
							else if (currentRedScore > currentBlueScore)
							{
								playerTeam = PlayerTeam.Red;
								int num18 = 0;
								int num19 = 0;
								foreach (GoalInfo item10 in _goals.OrderBy((GoalInfo g) => g.GameTime))
								{
									if (item10.Team == "Blue")
									{
										num18++;
									}
									else
									{
										num19++;
									}
									if (item10.Team == "Red" && num19 > num18 && num19 == currentBlueScore + 1)
									{
										text3 = item10.Scorer;
										break;
									}
								}
							}
							LogGWG(text3);
						}
						catch
						{
						}
						Dictionary<string, double> dictionary = new Dictionary<string, double>();
						foreach (Player player in MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayers())
						{
							if (player == null || !player)
							{
								continue;
							}
							string key = player.SteamId.Value.ToString();
							dictionary.Add(key, 0.0);
							double num20 = ((text3 == player.SteamId.Value.ToString()) ? 0.5 : 0.0);
							double num21 = ((playerTeam == player.Team) ? 1.1 : 1.0);
							if (PlayerFunc.IsGoalie(player))
							{
								if (_savePerc.TryGetValue(key, out (int, int) saveTuple))
								{
									int num22 = saveTuple.Item2 - saveTuple.Item1;
									dictionary[key] += (double)num22 * -10.0;
									dictionary[key] += (double)saveTuple.Item2 * 10.0;
									if (num22 == 0 && saveTuple.Item2 > 0)
									{
										dictionary[key] += 100.0;
									}
								}
								if (_passes.TryGetValue(key, out var value2))
								{
									dictionary[key] += (double)value2 * 2.5;
								}
								dictionary[key] += 175.0 * num20;
								dictionary[key] += (double)player.Goals.Value * 175.0;
								dictionary[key] += (double)player.Assists.Value * 30.0;
							}
							else
							{
								if (_sog.TryGetValue(key, out var value3))
								{
									dictionary[key] += (double)value3 * 7.5;
								}
								if (_passes.TryGetValue(key, out var value4))
								{
									dictionary[key] += (double)value4 * 2.5;
								}
								if (_blocks.TryGetValue(key, out var value5))
								{
									dictionary[key] += (double)value5 * 5.0;
								}
								dictionary[key] += 70.0 * num20;
								dictionary[key] += (double)player.Goals.Value * 70.0;
								dictionary[key] += (double)player.Assists.Value * 30.0;
							}
							if (_hits.TryGetValue(key, out var value6))
							{
								dictionary[key] += (double)value6 * 2.5;
							}
							if (_takeaways.TryGetValue(key, out var value7))
							{
								dictionary[key] += (double)value7 * 5.0;
							}
							if (_turnovers.TryGetValue(key, out var value8))
							{
								dictionary[key] -= (double)value8 * 5.0;
							}
							if (_exits.TryGetValue(key, out var value9))
							{
								dictionary[key] += (double)value9 * 1.0;
							}
							if (_entries.TryGetValue(key, out var value10))
							{
								dictionary[key] += (double)value10 * 1.0;
							}
							dictionary[key] *= num21;
						}
						dictionary = dictionary.OrderByDescending((KeyValuePair<string, double> x) => x.Value).ToDictionary((KeyValuePair<string, double> x) => x.Key, (KeyValuePair<string, double> x) => x.Value);
						if (dictionary.Count >= 1)
						{
							_stars[1] = dictionary.ElementAt(0).Key;
						}
						else
						{
							_stars[1] = "";
						}
						if (dictionary.Count >= 2)
						{
							_stars[2] = dictionary.ElementAt(1).Key;
						}
						else
						{
							_stars[2] = "";
						}
						if (dictionary.Count >= 3)
						{
							_stars[3] = dictionary.ElementAt(2).Key;
						}
						else
						{
							_stars[3] = "";
						}
						SendSystemChatMessage("STARS OF THE MATCH");
						foreach (KeyValuePair<int, string> item11 in _stars.OrderByDescending((KeyValuePair<int, string> x) => x.Key))
						{
							if (!string.IsNullOrEmpty(item11.Value))
							{
								Player playerBySteamId = MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayerBySteamId(item11.Value);
								if (playerBySteamId != null && (bool)playerBySteamId)
								{
									SendSystemChatMessage(string.Format("The {0} star is... #{1} {2} !", (item11.Key == 1) ? "first" : ((item11.Key == 2) ? "second" : "third"), playerBySteamId.Number.Value, playerBySteamId.Username.Value));
								}
								SafeSendDataToAll("oomtm450_statsSTAR", $"{item11.Value};{item11.Key}");
								LogStar(item11.Value, item11.Key);
							}
						}
						int num23 = 0;
						int count3 = _playByPlayEvents.Count;
						try
						{
							num23 = (from e in _playByPlayEvents
								where !string.IsNullOrEmpty(e.PlayerSteamId)
								select e.PlayerSteamId).Distinct().Count();
						}
						catch (Exception arg4)
						{
							Logging.LogError($"Error counting unique players for export: {arg4}", ServerConfig);
						}
						if (!ServerConfig.EnableExportLimit || (num23 >= 8 && count3 >= 300))
						{
							ExportGameStats();
							_exportedAtGameOver = true;
							string text4 = GetCurrentGameReferenceIdOrCreate();
							string arg5 = GetSanitizedExportFileHeaderName() + "_" + text4 + "_stats";
							int redScore2 = __instance.GameState.Value.RedScore;
							int blueScore2 = __instance.GameState.Value.BlueScore;
							if (CanSendSystemChatMessage())
							{
								SendSystemChatMessage($"Game ended. Stats exported - Final Score: Red {redScore2} - Blue {blueScore2} | File: {arg5}");
							}
						}
						else if (ServerConfig.EnableExportLimit)
						{
							Logging.Log($"Export skipped (JSON and CSV): {num23} unique players (need 8+), {count3} events (need 300+)", ServerConfig);
						}
						CapturePreviousGameSnapshotIfAvailable(ResolveGameOverSnapshotCaptureReason());
					}
				}
			}
			catch (Exception arg6)
			{
				Logging.LogError($"Error in GameManager_Server_SetGameState_Patch Prefix().\n{arg6}", ServerConfig);
			}
			return true;
		}

		[HarmonyPostfix]
		public static void Postfix(GameManager __instance, GamePhase? phase, int? tick, int? period, int? blueScore, int? redScore, bool? isOvertime)
		{
			try
			{
				if (!ServerFunc.IsDedicatedServer() || !_logic)
				{
					return;
				}
				SampleLiveTimeOnIceFromGameClock();
			}
			catch (Exception arg)
			{
				Logging.LogError($"Error in GameManager_Server_SetGameState_Patch Postfix().\n{arg}", ServerConfig);
			}
		}
	}

	[HarmonyPatch(typeof(UIChat), "GetChatMessagePrefix")]
	public static class UIChat_GetChatMessagePrefix_Patch
	{
		public static void Postfix(ChatMessage chatMessage, ref string __result)
		{
			if (!chatMessage.IsSystem && chatMessage.SteamID.HasValue)
			{
				string text = chatMessage.SteamID.Value.ToString();
				if (!string.IsNullOrEmpty(text))
				{
					__result = GetStarTag(text) + __result;
				}
			}
		}
	}

	[HarmonyPatch(typeof(Application), "OpenURL")]
	public static class Application_OpenURL_Patch
	{
		public static bool Prefix(string url)
		{
			if (string.IsNullOrEmpty(url) || !url.Contains("steamcommunity.com"))
			{
				return true;
			}
			try
			{
				Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
				for (int i = 0; i < assemblies.Length; i++)
				{
					Type type = assemblies[i].GetType("Steamworks.SteamFriends");
					if (!(type == null))
					{
						MethodInfo method = type.GetMethod("ActivateGameOverlayToWebPage", BindingFlags.Static | BindingFlags.Public, null, new Type[1] { typeof(string) }, null);
						if (method != null)
						{
							method.Invoke(null, new object[1] { url });
							return false;
						}
						method = type.GetMethod("OpenWebOverlay", BindingFlags.Static | BindingFlags.Public, null, new Type[2]
						{
							typeof(string),
							typeof(bool)
						}, null);
						if (method != null)
						{
							method.Invoke(null, new object[2] { url, false });
							return false;
						}
					}
				}
			}
			catch
			{
			}
			return true;
		}
	}

	[HarmonyPatch(typeof(ChatManager), "Client_SendChatMessageRpc")]
	public static class UIChatController_Event_Server_OnChatCommand_Patch
	{
		public static void Postfix(string content, RpcParams rpcParams)
		{
			try
			{
				if (!ServerFunc.IsDedicatedServer() || !_logic)
				{
					return;
				}
				if (string.IsNullOrEmpty(content) || !content.StartsWith("/endgame", StringComparison.OrdinalIgnoreCase))
				{
					return;
				}
				if (NetworkBehaviourSingleton<GameManager>.Instance != null && NetworkBehaviourSingleton<GameManager>.Instance.Phase == GamePhase.Warmup)
				{
					Logging.Log("Early game end export command received during warmup - no game to export", ServerConfig);
					if (CanSendSystemChatMessage())
					{
						SendSystemChatMessage("Cannot export stats during warmup - no game has started yet.");
					}
					return;
				}
				Logging.Log("Early game end export command received", ServerConfig);
				int num = 0;
				int count = _playByPlayEvents.Count;
				try
				{
					num = (from e in _playByPlayEvents
						where !string.IsNullOrEmpty(e.PlayerSteamId)
						select e.PlayerSteamId).Distinct().Count();
				}
				catch (Exception arg)
				{
					Logging.LogError($"Error counting unique players for /endgame export: {arg}", ServerConfig);
				}
				if (!ServerConfig.EnableExportLimit || (num >= 8 && count >= 300))
				{
					if (!ServerConfig.EnableExportLimit)
					{
						Logging.Log("Export requested via /endgame - Export limit disabled, exporting regardless of criteria", ServerConfig);
					}
					ExportGameStats(forceExport: true);
					if (CanSendSystemChatMessage())
					{
						string text4 = GetCurrentGameReferenceIdOrCreate();
						string arg2 = GetSanitizedExportFileHeaderName() + "_" + text4 + "_stats";
						int num2 = ((NetworkBehaviourSingleton<GameManager>.Instance != null && NetworkBehaviourSingleton<GameManager>.Instance.GameState != null) ? NetworkBehaviourSingleton<GameManager>.Instance.GameState.Value.RedScore : 0);
						int num3 = ((NetworkBehaviourSingleton<GameManager>.Instance != null && NetworkBehaviourSingleton<GameManager>.Instance.GameState != null) ? NetworkBehaviourSingleton<GameManager>.Instance.GameState.Value.BlueScore : 0);
						SendSystemChatMessage($"Game stats exported early - Final Score: Red {num2} - Blue {num3} | File: {arg2}");
					}
				}
				else if (ServerConfig.EnableExportLimit && CanSendSystemChatMessage())
				{
					SendSystemChatMessage($"Cannot export stats - Game does not meet criteria: {num} unique players (need 8+), {count} events (need 300+)");
				}
			}
			catch (Exception arg3)
			{
				Logging.LogError($"Error in chat command handler: {arg3}", ServerConfig);
			}
		}
	}


private static readonly ReadOnlyCollection<string> OLD_MOD_VERSIONS = new ReadOnlyCollection<string>(new List<string>
{
	"0.1.0", "0.1.1", "0.1.2", "0.2.0", "0.2.1", "0.2.2", "0.3.0", "0.4.0", "0.4.1", "0.5.0",
	"0.6.0"
});

private static readonly ReadOnlyCollection<string> CONFLICTING_WORKSHOP_MOD_IDS = new ReadOnlyCollection<string>(new List<string>
{
	"3561076649", "3608968085"
});

private static readonly ReadOnlyCollection<string> DATA_NAMES_TO_IGNORE = new ReadOnlyCollection<string>(new List<string>
{
	"eventName",
	"oomtm450_stats_SERVER_SIGNATURE",
	"oomtm450_statsNETWORKPERFORMANCE",
	"oomtm450_statsNETWORKPERFORMANCEGAME",
	"oomtm450_statsBOXSCORE_REQUEST",
	"oomtm450_statsBOXSCORE_LEADERS",
	"oomtm450_statsBOXSCORE_GAMESTATS",
	"oomtm450_statsBOXSCORE_PLAYERSTATS",
	"oomtm450_statsBOXSCORE_SHOOTOUT",
	"oomtm450_statsBOXSCORE_PLAYERDETAIL_REQUEST",
	"oomtm450_statsBOXSCORE_PLAYERDETAIL",
	"oomtm450_statsPREVIOUSGAME_SNAPSHOT",
	"oomtm450_statsPREVIOUSGAME_BOXSCORE_LEADERS",
	"oomtm450_statsPREVIOUSGAME_BOXSCORE_GAMESTATS",
	"oomtm450_statsPREVIOUSGAME_BOXSCORE_PLAYERSTATS",
	"oomtm450_statsPREVIOUSGAME_BOXSCORE_SHOOTOUT"
});

	private const string BATCH_SOG = "oomtm450_statsBATCHSOG";

	private const string BATCH_SAVEPERC = "oomtm450_statsBATCHSAVEPERC";

	private const string BATCH_BLOCK = "oomtm450_statsBATCHBLOCK";

	private const string BATCH_HIT = "oomtm450_statsBATCHHIT";

	private const string BATCH_TAKEAWAY = "oomtm450_statsBATCHTAKEAWAY";

	private const string BATCH_TURNOVER = "oomtm450_statsBATCHTURNOVER";

	private const string BATCH_PASS = "oomtm450_statsBATCHPASS";

	private const string BATCH_PUCK_TOUCH = "oomtm450_statsBATCHPUCKTOUCH";

	private const string BATCH_EXIT = "oomtm450_statsBATCHEXIT";

	private const string BATCH_ENTRY = "oomtm450_statsBATCHENTRY";

	private const string BATCH_POSSESSION_TIME = "oomtm450_statsBATCHPOSSESSIONTIME";

	private const string BATCH_PUCK_BATTLE_WINS = "oomtm450_statsBATCHPUCKBATTLEWINS";

	private const string BATCH_PUCK_BATTLE_LOSSES = "oomtm450_statsBATCHPUCKBATTLELOSSES";

	private const string BATCH_SHOT_ATTEMPTS = "oomtm450_statsBATCHSHOTATTEMPTS";

	private const string BATCH_ON_ICE_TEAM_SHOT_ATTEMPTS = "oomtm450_statsBATCHONICETEAMSHOTATTEMPTS";

	private const string BATCH_HOME_PLATE_SOGS = "oomtm450_statsBATCHHOMEPLATESOGS";

	private const string BATCH_TEAM_SHOTS = "oomtm450_statsBATCHTEAMSHOTS";

	private const string BATCH_TEAM_SHOT_ATTEMPTS = "oomtm450_statsBATCHTEAMSHOTATTEMPTS";

	private const string BATCH_TEAM_HOME_PLATE_SOGS = "oomtm450_statsBATCHTEAMHOMEPLATESOGS";

	private const string BATCH_TEAM_PASSES = "oomtm450_statsBATCHTEAMPASSES";

	private const string BATCH_TEAM_FACEOFF_WINS = "oomtm450_statsBATCHTEAMFACEOFFWINS";

	private const string BATCH_TEAM_FACEOFF_TOTAL = "oomtm450_statsBATCHTEAMFACEOFFTOTAL";

	private const string BATCH_TEAM_POSSESSION_TIME = "oomtm450_statsBATCHTEAMPOSSESSIONTIME";

	private const string BATCH_TEAM_PUCK_BATTLE_WINS = "oomtm450_statsBATCHTEAMPUCKBATTLEWINS";

	private const string BATCH_TEAM_PUCK_BATTLE_LOSSES = "oomtm450_statsBATCHTEAMPUCKBATTLELOSSES";

	private const string BATCH_TEAM_TAKEAWAYS = "oomtm450_statsBATCHTEAMTAKEAWAYS";

	private const string BATCH_TEAM_TURNOVERS = "oomtm450_statsBATCHTEAMTURNOVERS";

	private const string BATCH_TEAM_EXITS = "oomtm450_statsBATCHTEAMEXITS";

	private const string BATCH_TEAM_ENTRIES = "oomtm450_statsBATCHTEAMENTRIES";

	private const string BATCH_STICK_SAVES = "oomtm450_statsBATCHSTICKSAVES";

	private const string BATCH_BODY_SAVES = "oomtm450_statsBATCHBODYSAVES";

	private const string BATCH_PAD_SAVES = "oomtm450_statsBATCHPADSAVES";

	private const string BATCH_HOME_PLATE_SAVES = "oomtm450_statsBATCHHOMEPLATESAVES";

	private const string BATCH_HOME_PLATE_SHOTS_FACED = "oomtm450_statsBATCHHOMEPLATESHOTSFACED";

	private const string RESET_ALL = "oomtm450_statsRESETALL";

	private const string SERVER_SIGNATURE = "oomtm450_stats_SERVER_SIGNATURE";

	private const string NETWORK_PERFORMANCE_REPORT = "oomtm450_statsNETWORKPERFORMANCE";

	private const string NETWORK_PERFORMANCE_GAME = "oomtm450_statsNETWORKPERFORMANCEGAME";

	private const string STAR = "oomtm450_statsSTAR";

	private const string BOXSCORE_REQUEST = "oomtm450_statsBOXSCORE_REQUEST";

	private const string BOXSCORE_LEADERS = "oomtm450_statsBOXSCORE_LEADERS";

	private const string BOXSCORE_GAMESTATS = "oomtm450_statsBOXSCORE_GAMESTATS";

	private const string BOXSCORE_PLAYERSTATS = "oomtm450_statsBOXSCORE_PLAYERSTATS";

	private const string BOXSCORE_SHOOTOUT = "oomtm450_statsBOXSCORE_SHOOTOUT";

	private const string PREVIOUS_GAME_SNAPSHOT = "oomtm450_statsPREVIOUSGAME_SNAPSHOT";

	private const string PREVIOUS_GAME_CLEAR = "oomtm450_statsPREVIOUSGAME_CLEAR";

	private const string PREVIOUS_GAME_BOXSCORE_LEADERS = "oomtm450_statsPREVIOUSGAME_BOXSCORE_LEADERS";

	private const string PREVIOUS_GAME_BOXSCORE_GAMESTATS = "oomtm450_statsPREVIOUSGAME_BOXSCORE_GAMESTATS";

	private const string PREVIOUS_GAME_BOXSCORE_PLAYERSTATS = "oomtm450_statsPREVIOUSGAME_BOXSCORE_PLAYERSTATS";

	private const string PREVIOUS_GAME_BOXSCORE_SHOOTOUT = "oomtm450_statsPREVIOUSGAME_BOXSCORE_SHOOTOUT";

	private const string BOXSCORE_TAB_LEADERS = "Leaders";

	private const string BOXSCORE_TAB_GAMESTATS = "Game Stats";

	private const string BOXSCORE_TAB_PLAYERSTATS = "Player Stats";

	private const string BOXSCORE_TAB_SHOOTOUT = "Shootout";

	private const string SOG_HEADER_CELL_NAME = "SOGHeaderCell";

	private const string SOG_HEADER_LABEL_NAME = "SOGHeaderLabel";

	private const string SOG_CELL_NAME = "SOGCell";

	private const string SOG_LABEL = "SOGLabel";

	private const string HIT_HEADER_LABEL_NAME = "HitHeaderLabel";

	private const string HIT_LABEL = "HitLabel";

	private const string TURNOVER_HEADER_LABEL_NAME = "TurnoverHeaderLabel";

	private const string TURNOVER_LABEL = "TurnoverLabel";

	private const string TAKEAWAY_HEADER_LABEL_NAME = "TakeawayHeaderLabel";

	private const string TAKEAWAY_LABEL = "TakeawayLabel";

	private static bool? _rulesetModEnabled = null;

	private static bool _sendSavePercDuringGoalNextFrame = false;

	private static Player _sendSavePercDuringGoalNextFrame_Player = null;

	private static Vector3 _puckLastCoordinate = Vector3.zero;

	private static float _puckZCoordinateDifference = 0f;

	private static readonly LockDictionary<ulong, string> _players_ClientId_SteamId = new LockDictionary<ulong, string>();

	private static readonly LockDictionary<ulong, DateTime> _sentOutOfDateMessage = new LockDictionary<ulong, DateTime>();

	private static readonly LockDictionary<PlayerTeam, SaveCheck> _checkIfPuckWasSaved = new LockDictionary<PlayerTeam, SaveCheck>
	{
		{
			PlayerTeam.Red,
			new SaveCheck()
		},
		{
			PlayerTeam.Blue,
			new SaveCheck()
		}
	};

	private static readonly LockDictionary<PlayerTeam, BlockCheck> _checkIfPuckWasBlocked = new LockDictionary<PlayerTeam, BlockCheck>
	{
		{
			PlayerTeam.Red,
			new BlockCheck()
		},
		{
			PlayerTeam.Blue,
			new BlockCheck()
		}
	};

	private static readonly LockDictionary<PlayerTeam, bool> _lastShotWasCounted = new LockDictionary<PlayerTeam, bool>
	{
		{
			PlayerTeam.Red,
			true
		},
		{
			PlayerTeam.Blue,
			true
		}
	};

	private static readonly LockDictionary<PlayerTeam, bool> _lastBlockWasCounted = new LockDictionary<PlayerTeam, bool>
	{
		{
			PlayerTeam.Red,
			true
		},
		{
			PlayerTeam.Blue,
			true
		}
	};

	private static readonly LockDictionary<PlayerTeam, (string SteamId, DateTime Time)> _lastPlayerOnPuckTipIncludedSteamId = new LockDictionary<PlayerTeam, (string, DateTime)>
	{
		{
			PlayerTeam.Red,
			("", DateTime.MinValue)
		},
		{
			PlayerTeam.Blue,
			("", DateTime.MinValue)
		}
	};

	private static readonly LockDictionary<PlayerTeam, (string SteamId, DateTime Time)> _lastPlayerOnPuckSteamId = new LockDictionary<PlayerTeam, (string, DateTime)>
	{
		{
			PlayerTeam.Red,
			("", DateTime.MinValue)
		},
		{
			PlayerTeam.Blue,
			("", DateTime.MinValue)
		}
	};

	private static readonly LockDictionary<string, Stopwatch> _playersCurrentPuckTouch = new LockDictionary<string, Stopwatch>();

	private static readonly LockDictionary<string, Stopwatch> _playersLastTimePuckPossession = new LockDictionary<string, Stopwatch>();

	private static readonly LockDictionary<string, Stopwatch> _lastTimeOnCollisionStayOrExitWasCalled = new LockDictionary<string, Stopwatch>();

	private static readonly LockDictionary<string, bool> _playerIsDown = new LockDictionary<string, bool>();

	private static Possession _lastPossession = new Possession();

	private static PlayerTeam _lastTeamOnPuckTipIncluded = PlayerTeam.Blue;

	private static PlayerTeam _lastTeamOnPuck = PlayerTeam.Red;

	private static PuckRaycast _puckRaycast;

	private static bool _paused = false;

	private static bool _logic = true;

	private static readonly Harmony _harmony = new Harmony("oomtm450_stats");

	private static bool _harmonyPatched = false;

	private static bool _hasRegisteredWithNamedMessageHandler = false;

	private static CustomMessagingManager _registeredNamedMessageHandlerManager = null;

	private static string _registeredNamedMessageHandlerName = "";

	private static readonly LockDictionary<string, int> _sog = new LockDictionary<string, int>();

	private static readonly LockDictionary<string, int> _shotAttempts = new LockDictionary<string, int>();

	private static readonly LockDictionary<string, int> _onIceTeamShotAttempts = new LockDictionary<string, int>();

	private static readonly LockDictionary<string, double> _expectedGoalsFor = new LockDictionary<string, double>();

	private static readonly LockDictionary<string, double> _expectedGoalsAgainst = new LockDictionary<string, double>();

	private static readonly LockDictionary<string, int> _homePlateSogs = new LockDictionary<string, int>();

	private static readonly LockDictionary<string, (int Saves, int Shots)> _savePerc = new LockDictionary<string, (int, int)>();

	private static readonly LockDictionary<string, int> _stickSaves = new LockDictionary<string, int>();

	private static readonly LockDictionary<string, int> _bodySaves = new LockDictionary<string, int>();

	private static readonly LockDictionary<string, int> _padSaves = new LockDictionary<string, int>();

	private static readonly LockDictionary<string, int> _homePlateSaves = new LockDictionary<string, int>();

	private static readonly LockDictionary<string, int> _homePlateShots = new LockDictionary<string, int>();

	private static readonly LockDictionary<string, int> _blocks = new LockDictionary<string, int>();

	private static readonly LockDictionary<string, int> _hits = new LockDictionary<string, int>();

	private static readonly LockDictionary<string, int> _hitsTaken = new LockDictionary<string, int>();

	private static readonly LockDictionary<string, int> _takeaways = new LockDictionary<string, int>();

	private static readonly LockDictionary<string, int> _turnovers = new LockDictionary<string, int>();

	private static readonly LockDictionary<string, int> _passes = new LockDictionary<string, int>();

	private static readonly LockDictionary<string, int> _puckTouches = new LockDictionary<string, int>();

	private static readonly LockDictionary<string, DateTime> _lastPuckTouchTime = new LockDictionary<string, DateTime>();

	private static readonly LockDictionary<string, int> _exits = new LockDictionary<string, int>();

	private static readonly LockDictionary<string, int> _entries = new LockDictionary<string, int>();

	private static readonly LockDictionary<string, DateTime> _lastPossessionTouchTime = new LockDictionary<string, DateTime>();

	private static readonly LockDictionary<string, DateTime> _lastPossessionUpdateTime = new LockDictionary<string, DateTime>();

	private static readonly LockDictionary<string, double> _possessionTimeSeconds = new LockDictionary<string, double>();

	private static readonly LockDictionary<string, double> _timeOnIceSeconds = new LockDictionary<string, double>();

	private static readonly LockDictionary<string, double[]> _possessionTimeByZone = new LockDictionary<string, double[]>();

	private static readonly LockDictionary<string, double[]> _timeOnIceByZone = new LockDictionary<string, double[]>();

	private static readonly LockDictionary<string, int> _plusMinus = new LockDictionary<string, int>();

	private static readonly LockDictionary<string, DateTime> _lastTakeawayTime = new LockDictionary<string, DateTime>();

	private static readonly LockDictionary<string, DateTime> _lastTurnoverTime = new LockDictionary<string, DateTime>();

	private static readonly LockDictionary<string, List<(float GameTime, int TurnoverEventId, int TakeawayEventId)>> _recentTurnovers = new LockDictionary<string, List<(float, int, int)>>();

	private static readonly LockDictionary<string, EventZone> _lastPlayerZone = new LockDictionary<string, EventZone>();

	private static readonly LockDictionary<string, DateTime> _possessionStartTime = new LockDictionary<string, DateTime>();

	private static readonly LockDictionary<string, (string SteamId, DateTime StartTime, bool Validated, int PreviousPlayCount)> _pendingTakeaways = new LockDictionary<string, (string, DateTime, bool, int)>();

	private static readonly LockDictionary<string, (string SteamId, DateTime StartTime, bool Validated, int PreviousPlayCount)> _pendingTurnovers = new LockDictionary<string, (string, DateTime, bool, int)>();

	private static bool _isValidatingTurnoversTakeaways = false;

	private static readonly LockDictionary<string, int> _puckBattleWins = new LockDictionary<string, int>();

	private static readonly LockDictionary<string, int> _puckBattleLosses = new LockDictionary<string, int>();

	private static bool _wasTippedLastFrame = false;

	private static DateTime _lastPuckBattleTime = DateTime.MinValue;

	private static readonly int PUCK_BATTLE_COOLDOWN_MS = 500;

	private static readonly LockDictionary<PlayerTeam, (string ShooterSteamId, Vector3 ShooterPosition, Vector3 PuckPosition, Vector3 PuckVelocity, DateTime ReleaseTime)> _pendingShotReleases = new LockDictionary<PlayerTeam, (string, Vector3, Vector3, Vector3, DateTime)>();

	private static readonly LockDictionary<PlayerTeam, bool> _previousRaycastState = new LockDictionary<PlayerTeam, bool>
	{
		{
			PlayerTeam.Red,
			false
		},
		{
			PlayerTeam.Blue,
			false
		}
	};

	private static readonly LockDictionary<PlayerTeam, bool> _shotRecordedForRaycast = new LockDictionary<PlayerTeam, bool>
	{
		{
			PlayerTeam.Red,
			false
		},
		{
			PlayerTeam.Blue,
			false
		}
	};

	private static readonly LockDictionary<PlayerTeam, int> _raycastTrueFrames = new LockDictionary<PlayerTeam, int>
	{
		{
			PlayerTeam.Red,
			0
		},
		{
			PlayerTeam.Blue,
			0
		}
	};

	private static readonly LockDictionary<string, float> _lastShotAttemptGameTime = new LockDictionary<string, float>();

	private static readonly LockDictionary<string, int> _postHits = new LockDictionary<string, int>();

	private static readonly LockDictionary<string, List<PostHitPosition>> _postHitShotPositions = new LockDictionary<string, List<PostHitPosition>>();

	private static readonly TrackedList<GoalInfo> _goals = new TrackedList<GoalInfo>(GoalCollectionChanged);

	private static readonly LockDictionary<int, string> _stars = new LockDictionary<int, string>
	{
		{ 1, "" },
		{ 2, "" },
		{ 3, "" }
	};

	private static readonly TrackedList<PlayByPlayEvent> _playByPlayEvents = new TrackedList<PlayByPlayEvent>(EventCollectionChanged);

	private static int _nextPlayByPlayEventId = 0;

	private static int _currentPeriod = 1;

	private static float _gameStartTime = 0f;

	private static float _lastWholeSecondGameTime = 0f;

	private static float _lastUnityTimeForGameTime = 0f;

	private static int _lastCountdownValue = -1;

	private static float _fractionalSecondTimer = 0f;

	private static float _lastLiveToiSampleGameTime = -1f;

	private static string _currentGameReferenceId = "";

	private static FrozenGameExportSnapshot _frozenGameExportSnapshot = null;

	private static int _currentGameNumber = 0;

	private static PlayerTeam _currentTeamInPossession = PlayerTeam.None;

	private static int _currentPlayInPossession = 0;

	private static PlayByPlayEvent _lastEvent = null;

	private static readonly LockDictionary<PlayerTeam, bool> _teamHasExitedDZ = new LockDictionary<PlayerTeam, bool>
	{
		{
			PlayerTeam.Red,
			false
		},
		{
			PlayerTeam.Blue,
			false
		}
	};

	private static readonly LockDictionary<PlayerTeam, bool> _teamHasEnteredOZ = new LockDictionary<PlayerTeam, bool>
	{
		{
			PlayerTeam.Red,
			false
		},
		{
			PlayerTeam.Blue,
			false
		}
	};

	private static bool _trackingFaceoffOutcome = false;

	private static int _faceoffPossessionChainCount = 0;

	private static PlayerTeam _faceoffPossessionTeam = PlayerTeam.None;

	private static int _lastFaceoffEventId = -1;

	private static GamePhase _lastRecordedPhase = GamePhase.None;

	private static bool _exportedAtGameOver = false;

	private static bool _exportedAtResetGameState = false;

	private static bool _warmupLiveResetAppliedForUpcomingGame = false;

	private static bool _isShootoutActive = false;

	private static bool _shootoutCompletedThisGame = false;

	private static DateTime _lastWarmupVoteSuccessAtUtc = DateTime.MinValue;

	private static DateTime _lastForfeitCommandAtUtc = DateTime.MinValue;

	private static string _lastPostHitShooterSteamId = "";

	private static Vector3 _lastPostHitShotOrigin = Vector3.zero;

	private static DateTime _lastPostHitStickContactTime = DateTime.MinValue;

	private static DateTime _lastPostHitTime = DateTime.MinValue;

	private static bool _faceoffRecordedForCurrentPeriod = false;

	private static bool _faceoffTotalIncremented = false;

	private static int _lastTrackedPeriod = 0;

	private static PlayByPlayEvent _pendingBlueFaceoffOutcome = null;

	private static PlayByPlayEvent _pendingRedFaceoffOutcome = null;

	private static bool _isCurrentGoalOwnGoal = false;

	private static string _currentOwnGoalPlayerSteamId = "";

	private static string _lastGoalHookSignature = "";

	private static DateTime _lastGoalHookAtUtc = DateTime.MinValue;

	internal static ClientConfig _clientConfig = new ClientConfig();

	private static ExpectedGoalsModelConfig _expectedGoalsModel = new ExpectedGoalsModelConfig();

	private static DateTime _lastDateTimeAskStartupData = DateTime.MinValue;

private static bool _serverHasResponded = false;

private static bool _serverSignatureConfirmed = false;

private static readonly LockDictionary<string, DateTime> _trackingDiagnosticLastLogTime = new LockDictionary<string, DateTime>();

private static readonly LockDictionary<string, int> _trackingDiagnosticSuppressedCount = new LockDictionary<string, int>();

private static readonly LockDictionary<string, int> _collisionHookInvocationCounts = new LockDictionary<string, int>();

private static bool _serverStatsCompatibilityDisabled = false;

private static string _serverStatsCompatibilityDisabledReason = "";

private static bool _askForKick = false;

	private static PreviousGameSnapshot _serverPreviousGameSnapshot = null;

	private static PreviousGameSnapshot _clientPreviousGameSnapshot = null;

	private static bool _addServerModVersionOutOfDateMessage = false;

	private static int _askServerForStartupDataCount = 0;

	private static readonly HashSet<string> _hasUpdatedUIScoreboard = new HashSet<string>();

	private static bool _teamTooltipsSetup = false;

	private static bool _scoreboardUiEnabled = false;

	private static readonly LockDictionary<string, Label> _sogLabels = new LockDictionary<string, Label>();

	private static readonly LockDictionary<string, VisualElement> _playerTooltips = new LockDictionary<string, VisualElement>();

	private static readonly LockDictionary<PlayerTeam, VisualElement> _teamTooltips = new LockDictionary<PlayerTeam, VisualElement>();

	private static readonly LockDictionary<PlayerTeam, TeamTooltipLabels> _teamTooltipLabels = new LockDictionary<PlayerTeam, TeamTooltipLabels>();

	private static readonly LockDictionary<string, Label> _playerTooltipNameLabels = new LockDictionary<string, Label>();

	private static readonly LockDictionary<string, VisualElement> _playerTooltipContainers = new LockDictionary<string, VisualElement>();

	private static readonly LockDictionary<string, bool> _playerTooltipIsGoalie = new LockDictionary<string, bool>();

	private static readonly LockDictionary<string, PlayerTooltipLabels> _playerTooltipLabels = new LockDictionary<string, PlayerTooltipLabels>();

	private static readonly LockDictionary<string, DateTime> _scoreboardSogFollowUpScheduledAt = new LockDictionary<string, DateTime>();

	private static readonly LockDictionary<string, bool> _scoreboardRowHovered = new LockDictionary<string, bool>();

	private static Dictionary<VisualElement, float> _tooltipMoveTimes = new Dictionary<VisualElement, float>();

	private static BoxscoreOverlayView _boxscoreOverlayView = null;

	private static UIManager _boxscoreRegisteredUiManager = null;

	private static Action<UIView> _boxscoreOverlayVisibilityHandler = null;

	private static Action<UIView> _boxscoreOverlayFocusHandler = null;

	private static VisualElement _boxscoreOverlay = null;

	private static VisualElement _boxscorePanel = null;

	private static VisualElement _boxscoreResizeHandle = null;

	private static Label _boxscoreSummaryLabel = null;

	private static Label _boxscoreLoadingLabel = null;

	private static VisualElement _boxscoreHintContainer = null;

	private static Label _boxscoreHintLabel = null;

	private static VisualElement _boxscoreContentRoot = null;

	private static readonly LockDictionary<string, Button> _boxscoreTabButtons = new LockDictionary<string, Button>();

	private static string _boxscoreActiveTab = "Leaders";

	private static bool _boxscoreVisible = false;

	private static int _boxscoreRequestSequence = 0;

	private static int _boxscorePauseSuppressionFrame = -1;

	private static bool _boxscoreResizeActive = false;

	private static int _boxscoreResizePointerId = -1;

	private static bool _boxscoreHintShownThisJoin = false;

	private static int _boxscoreHintPromptSequence = 0;

	private static DateTime _boxscoreHintShownAtUtc = DateTime.MinValue;

	private static Vector2 _boxscoreResizeStartPointerPosition = Vector2.zero;

	private static float _boxscoreResizeStartWidth = 0f;

	private static float _boxscoreResizeStartHeight = 0f;

	private const float BOXSCORE_DEFAULT_WIDTH = 720f;

	private const float BOXSCORE_DEFAULT_HEIGHT = 904f;

	private const float BOXSCORE_MIN_WIDTH = 720f;

	private const float BOXSCORE_MIN_HEIGHT = 500f;

	private const float BOXSCORE_VIEWPORT_MARGIN = 72f;

	private const float BOXSCORE_HINT_LEFT = 16f;

	private const float BOXSCORE_HINT_BOTTOM = 68f;

	private const double BOXSCORE_HINT_FADE_SECONDS = 3.0;

	private const float BOXSCORE_TEAM_STATS_SIDE_WIDTH = 170f;

	private static readonly LockDictionary<PlayerTeam, int> _teamShots = new LockDictionary<PlayerTeam, int>();

	private static readonly LockDictionary<PlayerTeam, int> _teamShotAttempts = new LockDictionary<PlayerTeam, int>();

	private static readonly LockDictionary<PlayerTeam, int> _teamHomePlateSogs = new LockDictionary<PlayerTeam, int>();

	private static readonly LockDictionary<PlayerTeam, int> _teamPasses = new LockDictionary<PlayerTeam, int>();

	private static readonly LockDictionary<PlayerTeam, double> _teamPossessionTime = new LockDictionary<PlayerTeam, double>();

	private static readonly LockDictionary<PlayerTeam, int> _teamPuckBattleWins = new LockDictionary<PlayerTeam, int>();

	private static readonly LockDictionary<PlayerTeam, int> _teamPuckBattleLosses = new LockDictionary<PlayerTeam, int>();

	private static readonly LockDictionary<PlayerTeam, int> _teamFaceoffWins = new LockDictionary<PlayerTeam, int>();

	private static readonly LockDictionary<PlayerTeam, int> _teamFaceoffTotal = new LockDictionary<PlayerTeam, int>();

	private static readonly LockDictionary<PlayerTeam, int> _teamTakeaways = new LockDictionary<PlayerTeam, int>();

	private static readonly LockDictionary<PlayerTeam, int> _teamTurnovers = new LockDictionary<PlayerTeam, int>();

	private static readonly LockDictionary<PlayerTeam, int> _teamExits = new LockDictionary<PlayerTeam, int>();

	private static readonly LockDictionary<PlayerTeam, int> _teamEntries = new LockDictionary<PlayerTeam, int>();

	private static PlayerTeam _currentTeamPossession = PlayerTeam.None;

	private static DateTime _teamPossessionStartTime = DateTime.UtcNow;

	private static readonly LockDictionary<PlayerTeam, DateTime> _teamLastEventTime = new LockDictionary<PlayerTeam, DateTime>();

	private static readonly LockDictionary<string, PendingStatValue> _pendingStatUpdates = new LockDictionary<string, PendingStatValue>();

	private static DateTime _lastStatBatchSendTime = DateTime.UtcNow;

	private static int _batchingFailureCount = 0;

	private static DateTime _lastBatchingFailureTime = DateTime.MinValue;

	private static bool _batchingDisabled = false;

	private const int MAX_BATCHING_FAILURES = 3;

	private const double BATCHING_DISABLE_DURATION_SECONDS = 60.0;

	private static DateTime _lastZoneFlagScanTime = DateTime.UtcNow;

	private static int _lastProcessedZoneFlagEventId = -1;

	private const double STAT_BATCH_INTERVAL_SECONDS = 5.0;

	internal static OPLStats_Stats.Configs.ServerConfig ServerConfig { get; set; } = new OPLStats_Stats.Configs.ServerConfig();

	private static void ResetSupplementalServerState(List<Player> players = null)
	{
		HashSet<string> hashSet = new HashSet<string>((players ?? MonoBehaviourSingleton<PlayerManager>.Instance?.GetPlayers() ?? new List<Player>()).Where((Player x) => x != null && (bool)x).Select((Player x) => x.SteamId.Value.ToString()));
		ResetPlayerDoubleArrayDictionary(_possessionTimeByZone, hashSet);
		ResetPlayerDoubleArrayDictionary(_timeOnIceByZone, hashSet);
		_playerHeatGrids.Clear();
		ResetPlayerIntDictionary(_postHits, hashSet);
		ResetPlayerIntDictionary(_hitsTaken, hashSet);
		ResetPlayerPositionListDictionary(_postHitShotPositions, hashSet);
		_isShootoutActive = false;
		_shootoutCompletedThisGame = false;
		ClearCurrentGameShootoutBoxscoreState();
		ResetPostHitTrackingState();
	}

	private static void ResetPlayerDoubleArrayDictionary(LockDictionary<string, double[]> dictionary, HashSet<string> activeSteamIds)
	{
		foreach (string key in new List<string>(dictionary.Keys))
		{
			if (activeSteamIds.Count > 0 && activeSteamIds.Contains(key))
			{
				dictionary[key] = new double[3];
			}
			else
			{
				dictionary.Remove(key);
			}
		}
	}

	private static void ResetPlayerIntDictionary(LockDictionary<string, int> dictionary, HashSet<string> activeSteamIds)
	{
		foreach (string key in new List<string>(dictionary.Keys))
		{
			if (activeSteamIds.Count > 0 && activeSteamIds.Contains(key))
			{
				dictionary[key] = 0;
			}
			else
			{
				dictionary.Remove(key);
			}
		}
	}

	private static void ResetPlayerPositionListDictionary(LockDictionary<string, List<PostHitPosition>> dictionary, HashSet<string> activeSteamIds)
	{
		foreach (string key in new List<string>(dictionary.Keys))
		{
			if (activeSteamIds.Count > 0 && activeSteamIds.Contains(key))
			{
				dictionary[key] = new List<PostHitPosition>();
			}
			else
			{
				dictionary.Remove(key);
			}
		}
	}

	private static void ResetAllServerStatsAndPlayByPlay()
	{
		ResetServerNetworkPerformanceTracking();
		List<Player> list = ((MonoBehaviourSingleton<PlayerManager>.Instance != null) ? MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayers() : new List<Player>());
		ResetSupplementalServerState(list);
		foreach (string key in new List<string>(_savePerc.Keys))
		{
			if (list.Count > 0 && list.FirstOrDefault((Player x) => x.SteamId.Value.ToString() == key) != null)
			{
				_savePerc[key] = (0, 0);
			}
			else
			{
				_savePerc.Remove(key);
			}
		}
		foreach (string key2 in new List<string>(_stickSaves.Keys))
		{
			if (list.Count > 0 && list.FirstOrDefault((Player x) => x.SteamId.Value.ToString() == key2) != null)
			{
				_stickSaves[key2] = 0;
			}
			else
			{
				_stickSaves.Remove(key2);
			}
		}
		foreach (string key3 in new List<string>(_bodySaves.Keys))
		{
			if (list.Count > 0 && list.FirstOrDefault((Player x) => x.SteamId.Value.ToString() == key3) != null)
			{
				_bodySaves[key3] = 0;
			}
			else
			{
				_bodySaves.Remove(key3);
			}
		}
		foreach (string key3b in new List<string>(_padSaves.Keys))
		{
			if (list.Count > 0 && list.FirstOrDefault((Player x) => x.SteamId.Value.ToString() == key3b) != null)
			{
				_padSaves[key3b] = 0;
			}
			else
			{
				_padSaves.Remove(key3b);
			}
		}
		foreach (string key4 in new List<string>(_blocks.Keys))
		{
			if (list.Count > 0 && list.FirstOrDefault((Player x) => x.SteamId.Value.ToString() == key4) != null)
			{
				_blocks[key4] = 0;
			}
			else
			{
				_blocks.Remove(key4);
			}
		}
		foreach (string key5 in new List<string>(_hits.Keys))
		{
			if (list.Count > 0 && list.FirstOrDefault((Player x) => x.SteamId.Value.ToString() == key5) != null)
			{
				_hits[key5] = 0;
			}
			else
			{
				_hits.Remove(key5);
			}
		}
		foreach (string key6 in new List<string>(_takeaways.Keys))
		{
			if (list.Count > 0 && list.FirstOrDefault((Player x) => x.SteamId.Value.ToString() == key6) != null)
			{
				_takeaways[key6] = 0;
			}
			else
			{
				_takeaways.Remove(key6);
			}
		}
		foreach (string key7 in new List<string>(_turnovers.Keys))
		{
			if (list.Count > 0 && list.FirstOrDefault((Player x) => x.SteamId.Value.ToString() == key7) != null)
			{
				_turnovers[key7] = 0;
			}
			else
			{
				_turnovers.Remove(key7);
			}
		}
		foreach (string key8 in new List<string>(_puckTouches.Keys))
		{
			if (list.Count > 0 && list.FirstOrDefault((Player x) => x.SteamId.Value.ToString() == key8) != null)
			{
				_puckTouches[key8] = 0;
			}
			else
			{
				_puckTouches.Remove(key8);
			}
		}
		foreach (string key9 in new List<string>(_shotAttempts.Keys))
		{
			if (list.Count > 0 && list.FirstOrDefault((Player x) => x.SteamId.Value.ToString() == key9) != null)
			{
				_shotAttempts[key9] = 0;
			}
			else
			{
				_shotAttempts.Remove(key9);
			}
		}
		foreach (string key9b in new List<string>(_onIceTeamShotAttempts.Keys))
		{
			if (list.Count > 0 && list.FirstOrDefault((Player x) => x.SteamId.Value.ToString() == key9b) != null)
			{
				_onIceTeamShotAttempts[key9b] = 0;
			}
			else
			{
				_onIceTeamShotAttempts.Remove(key9b);
			}
		}
		foreach (string key10 in new List<string>(_exits.Keys))
		{
			if (list.Count > 0 && list.FirstOrDefault((Player x) => x.SteamId.Value.ToString() == key10) != null)
			{
				_exits[key10] = 0;
			}
			else
			{
				_exits.Remove(key10);
			}
		}
		foreach (string key11 in new List<string>(_entries.Keys))
		{
			if (list.Count > 0 && list.FirstOrDefault((Player x) => x.SteamId.Value.ToString() == key11) != null)
			{
				_entries[key11] = 0;
			}
			else
			{
				_entries.Remove(key11);
			}
		}
		foreach (string key12 in new List<string>(_passes.Keys))
		{
			if (list.Count > 0 && list.FirstOrDefault((Player x) => x.SteamId.Value.ToString() == key12) != null)
			{
				_passes[key12] = 0;
			}
			else
			{
				_passes.Remove(key12);
			}
		}
		foreach (string key13 in new List<string>(_sog.Keys))
		{
			if (list.Count > 0 && list.FirstOrDefault((Player x) => x.SteamId.Value.ToString() == key13) != null)
			{
				_sog[key13] = 0;
			}
			else
			{
				_sog.Remove(key13);
			}
		}
		foreach (string key14 in new List<string>(_possessionTimeSeconds.Keys))
		{
			if (list.Count > 0 && list.FirstOrDefault((Player x) => x.SteamId.Value.ToString() == key14) != null)
			{
				_possessionTimeSeconds[key14] = 0.0;
			}
			else
			{
				_possessionTimeSeconds.Remove(key14);
			}
		}
		_lastPossessionTouchTime.Clear();
		_lastPossessionUpdateTime.Clear();
		_trackingDiagnosticLastLogTime.Clear();
		_trackingDiagnosticSuppressedCount.Clear();
		_collisionHookInvocationCounts.Clear();
		_lastPuckTouchTime.Clear();
		_lastPlayerZone.Clear();
		_savePerc.Clear();
		_stickSaves.Clear();
		_bodySaves.Clear();
		_padSaves.Clear();
		_homePlateSogs.Clear();
		_homePlateSaves.Clear();
		_homePlateShots.Clear();
		_puckBattleWins.Clear();
		_puckBattleLosses.Clear();
		foreach (int item in new List<int>(_stars.Keys))
		{
			_stars[item] = "";
		}
		_teamShots.Clear();
		_teamShotAttempts.Clear();
		_teamHomePlateSogs.Clear();
		_teamPasses.Clear();
		_teamPossessionTime.Clear();
		_teamPuckBattleWins.Clear();
		_teamPuckBattleLosses.Clear();
		_teamFaceoffWins.Clear();
		_teamFaceoffTotal.Clear();
		_teamTakeaways.Clear();
		_teamTurnovers.Clear();
		_teamExits.Clear();
		_teamEntries.Clear();
		_expectedGoalsFor.Clear();
		_expectedGoalsAgainst.Clear();
		_onIceTeamShotAttempts.Clear();
		_recentTurnovers.Clear();
		_timeOnIceSeconds.Clear();
		ResetPlayerRoleTracking();
		ResetLiveStatsExportState();
		_plusMinus.Clear();
		_currentTeamPossession = PlayerTeam.None;
		_teamPossessionStartTime = DateTime.UtcNow;
		_teamLastEventTime.Clear();
		_goals.Clear();
		_lastGoalDedupeSteamId = "";
		_lastGoalDedupePeriod = -1;
		_lastGoalDedupeTime = -1f;
		_lastGoalPreprocessSignature = "";
		_lastGoalPreprocessAtUtc = DateTime.MinValue;
		_lastGoalHookSignature = "";
		_lastGoalHookAtUtc = DateTime.MinValue;
		_lastPossession = new Possession();
		_wasTippedLastFrame = false;
		_playByPlayEvents.Clear();
		_nextPlayByPlayEventId = 0;
		_lastProcessedZoneFlagEventId = -1;
        _lastProcessedZoneFlagIndex = -1;
		_currentGameReferenceId = CreateGameReferenceId(DateTime.UtcNow);
		ClearFrozenGameExportSnapshot();
		ResetPreviewDerivedDataCaches();
		_currentTeamInPossession = PlayerTeam.None;
		_currentPlayInPossession = 0;
		_gameStartTime = Time.time;
		_currentPeriod = 1;
		_lastTrackedPeriod = 0;
		_faceoffRecordedForCurrentPeriod = false;
		_lastEvent = null;
		_lastWholeSecondGameTime = 0f;
		_lastUnityTimeForGameTime = 0f;
		_lastCountdownValue = -1;
		ResetLiveTimeOnIceSamplingState();
		foreach (PlayerTeam item in new List<PlayerTeam>(_pendingShotReleases.Keys))
		{
			_pendingShotReleases[item] = ("", Vector3.zero, Vector3.zero, Vector3.zero, DateTime.MinValue);
		}
		foreach (PlayerTeam item2 in new List<PlayerTeam>(_previousRaycastState.Keys))
		{
			_previousRaycastState[item2] = false;
		}
		foreach (PlayerTeam item3 in new List<PlayerTeam>(_raycastTrueFrames.Keys))
		{
			_raycastTrueFrames[item3] = 0;
		}
		foreach (PlayerTeam item4 in new List<PlayerTeam>(_shotRecordedForRaycast.Keys))
		{
			_shotRecordedForRaycast[item4] = false;
		}
		_lastShotAttemptGameTime.Clear();
		foreach (PlayerTeam item5 in new List<PlayerTeam>(_teamHasExitedDZ.Keys))
		{
			_teamHasExitedDZ[item5] = false;
		}
		foreach (PlayerTeam item6 in new List<PlayerTeam>(_teamHasEnteredOZ.Keys))
		{
			_teamHasEnteredOZ[item6] = false;
		}
		_pendingStatUpdates.Clear();
		_sentOutOfDateMessage.Clear();
	}

	private static void ResetForExplicitNewGameStart()
	{
		AdvanceGameCounterForNewTrackedGame();
		ClearServerPreviousGameSnapshot(broadcastToClients: true);
		ResetLiveStatsForNewGameStart();
		_warmupLiveResetAppliedForUpcomingGame = false;
	}

	private static void ResetLiveStatsForNewGameStart()
	{
		_lastWarmupVoteSuccessAtUtc = DateTime.MinValue;
		_lastForfeitCommandAtUtc = DateTime.MinValue;
		ResetAllServerStatsAndPlayByPlay();
		SafeSendDataToAll("oomtm450_statsRESETALL", "1", NetworkDelivery.ReliableSequenced);
		_exportedAtGameOver = false;
		_exportedAtResetGameState = false;
	}

	private static void PrepareWarmupStartedGameReset()
	{
		ResetLiveStatsForNewGameStart();
		_warmupLiveResetAppliedForUpcomingGame = true;
	}

	private static void FinalizeWarmupStartedGameReset()
	{
		AdvanceGameCounterForNewTrackedGame();
		ClearServerPreviousGameSnapshot(broadcastToClients: true);
		_warmupLiveResetAppliedForUpcomingGame = false;
	}

	public bool OnEnable()
	{
		//IL_02ce->IL02e9: Incompatible stack heights: 5 vs 1
		try
		{
			if (_harmonyPatched)
			{
				return true;
			}
			if (ServerFunc.IsDedicatedServer() && TryGetConflictingWorkshopStatsModId(out var modId))
			{
				Logging.Log($"Compatibility safeguard engaged. Detected conflicting workshop mod {modId}; DomaStatsBranch will remain disabled on this server to avoid interference.", ServerConfig, bypassConfig: true);
				return true;
			}
			Logging.Log("Enabling...", ServerConfig, bypassConfig: true);
			_harmony.PatchAll();
			Logging.Log("Enabled.", ServerConfig, bypassConfig: true);
			Logging.Log("Stats mod loaded", _clientConfig);
			NetworkCommunication.AddToNotLogList(DATA_NAMES_TO_IGNORE);
			if (ServerFunc.IsDedicatedServer())
			{
				Server_RegisterNamedMessageHandler();
				_serverPreviousGameSnapshot = null;
				ClearCurrentGameShootoutBoxscoreState();
				Logging.Log("Setting server sided config.", ServerConfig, bypassConfig: true);
				ServerConfig = OPLStats_Stats.Configs.ServerConfig.ReadConfig();
				_expectedGoalsModel = ExpectedGoalsModelConfig.ReadConfig();
				ResetPreviewDerivedDataCaches();
				LoadGameCounterState();
				LoadServerPreviousGameSnapshotFromTempFile();
				ReconcileGameCounterWithSnapshot();
				Logging.Log($"Expected goals model {( _expectedGoalsModel.Enabled ? "enabled" : "disabled" )}: {_expectedGoalsModel.ModelVersion}", ServerConfig, bypassConfig: true);
				try
				{
					if (NetworkBehaviourSingleton<ServerManager>.Instance?.ServerConfig != null)
					{
						string text = NetworkBehaviourSingleton<ServerManager>.Instance.ServerConfig.name;
						if (!string.IsNullOrEmpty(text))
						{
							int num = text.IndexOf('|');
							if (num >= 0)
							{
								text = text.Substring(0, num).Trim();
							}
							text = text.Replace(" ", "");
							text = StripHtmlTags(text);
							text = text.Replace("(", "").Replace(")", "");
							char[] invalidFileNameChars = Path.GetInvalidFileNameChars();
							foreach (char c in invalidFileNameChars)
							{
								text = text.Replace(c.ToString(), "");
							}
							text = text.Trim();
							if (string.IsNullOrEmpty(text))
							{
								text = "puck";
							}
							ServerConfig.FileHeaderName = text;
							Logging.Log("FileHeaderName set from server name: " + ServerConfig.FileHeaderName, ServerConfig, bypassConfig: true);
							try
							{
								ServerConfig.Save();
								Logging.Log("Updated server config file with FileHeaderName: " + ServerConfig.FileHeaderName, ServerConfig, bypassConfig: true);
							}
							catch (Exception arg)
							{
								Logging.LogError($"Can't write the server config file after updating FileHeaderName. (Permission error ?)\n{arg}", ServerConfig);
							}
						}
					}
				}
				catch (Exception arg2)
				{
					Logging.LogError($"Error setting FileHeaderName from server name: {arg2}", ServerConfig);
				}
			}
			else
			{
				_clientPreviousGameSnapshot = null;
				ResetBoxscoreHintPromptState();
				Logging.Log("Setting client sided config.", ServerConfig, bypassConfig: true);
				_clientConfig = ClientConfig.ReadConfig();
			}
			Logging.Log("Subscribing to events.", ServerConfig, bypassConfig: true);
			if (ServerFunc.IsDedicatedServer())
			{
								EventManager.AddEventListener("Event_Everyone_OnClientConnected", Event_OnClientConnected);
								EventManager.AddEventListener("Event_Everyone_OnClientDisconnected", Event_OnClientDisconnected);
								EventManager.AddEventListener("Event_Everyone_OnPlayerGameStateChanged", Event_OnPlayerGameStateChanged);
								EventManager.AddEventListener("oomtm450_stats", Event_OnStatsTrigger);
								EventManager.AddEventListener("oomtm450_ruleset", Event_OnRulesetTrigger);
								EventManager.AddEventListener("Event_Server_OnChatCommand", Event_OnServerChatCommand);
								EventManager.AddEventListener("Event_Server_OnVoteSuccess", Event_OnVoteSuccess);
								EventManager.AddEventListener("Event_Shootout_Begin", Event_OnShootoutBegin);
								EventManager.AddEventListener("Event_Shootout_CurrentShooter", Event_OnShootoutCurrentShooter);
								EventManager.AddEventListener("Event_Shootout_AttemptResult", Event_OnShootoutAttemptResult);
								EventManager.AddEventListener("Event_Shootout_Win", Event_OnShootoutWin);
			}
			else
			{
								EventManager.AddEventListener("Event_OnClientStopped", Event_OnClientStopped);
								AddNetworkPerformanceEventListeners();
			}
			_combinedBatchClients.Clear();
            Application.quitting -= DrainFileWork;
            Application.quitting += DrainFileWork;
			_harmonyPatched = true;
			_logic = true;
			return true;
		}
		catch (Exception arg3)
		{
			Logging.LogError($"Failed to enable.\n{arg3}", ServerConfig);
			return false;
		}
	}

	public bool OnDisable()
	{
		//IL_00d4->IL0130: Incompatible stack heights: 5 vs 1
		//IL_00eb->IL0130: Incompatible stack heights: 5 vs 1
		//IL_00df->IL0130: Incompatible stack heights: 5 vs 1
		try
		{
			DrainFileWork();
            _combinedBatchClients.Clear();
            Application.quitting -= DrainFileWork;
            if (!_harmonyPatched)
			{
				return true;
			}
			Logging.Log("Disabling...", ServerConfig, bypassConfig: true);
			Logging.Log("Unsubscribing from events.", ServerConfig, bypassConfig: true);
			NetworkCommunication.RemoveFromNotLogList(DATA_NAMES_TO_IGNORE);
			if (ServerFunc.IsDedicatedServer())
			{
								EventManager.RemoveEventListener("Event_Everyone_OnClientConnected", Event_OnClientConnected);
								EventManager.RemoveEventListener("Event_Everyone_OnClientDisconnected", Event_OnClientDisconnected);
								EventManager.RemoveEventListener("Event_Everyone_OnPlayerGameStateChanged", Event_OnPlayerGameStateChanged);
								EventManager.RemoveEventListener("oomtm450_stats", Event_OnStatsTrigger);
								EventManager.RemoveEventListener("oomtm450_ruleset", Event_OnRulesetTrigger);
								EventManager.RemoveEventListener("Event_Server_OnChatCommand", Event_OnServerChatCommand);
								EventManager.RemoveEventListener("Event_Server_OnVoteSuccess", Event_OnVoteSuccess);
								EventManager.RemoveEventListener("Event_Shootout_Begin", Event_OnShootoutBegin);
								EventManager.RemoveEventListener("Event_Shootout_CurrentShooter", Event_OnShootoutCurrentShooter);
								EventManager.RemoveEventListener("Event_Shootout_AttemptResult", Event_OnShootoutAttemptResult);
				EventManager.RemoveEventListener("Event_Shootout_Win", Event_OnShootoutWin);
				ClearCurrentGameShootoutBoxscoreState();
				NetworkManager.Singleton?.CustomMessagingManager?.UnregisterNamedMessageHandler("oomtm450_stats_client");
			}
			else
			{
								EventManager.RemoveEventListener("Event_OnClientStopped", Event_OnClientStopped);
								RemoveNetworkPerformanceEventListeners();
				Event_OnClientStopped(new Dictionary<string, object>());
				NetworkManager.Singleton?.CustomMessagingManager?.UnregisterNamedMessageHandler("oomtm450_stats_server");
				DisposeBoxscorePanel();
			}
			_hasRegisteredWithNamedMessageHandler = false;
			_registeredNamedMessageHandlerManager = null;
			_registeredNamedMessageHandlerName = "";
			_rulesetModEnabled = null;
			_serverHasResponded = false;
			_serverSignatureConfirmed = false;
			_serverStatsCompatibilityDisabled = false;
			_serverStatsCompatibilityDisabledReason = "";
			_lastWarmupVoteSuccessAtUtc = DateTime.MinValue;
			_lastForfeitCommandAtUtc = DateTime.MinValue;
			_askServerForStartupDataCount = 0;
			ApplyScoreboardEnabledState(enable: false, force: true);
			_harmony.UnpatchSelf();
			DrainFileWork();
			Logging.Log("Disabled.", ServerConfig, bypassConfig: true);
			_harmonyPatched = false;
			_logic = true;
			return true;
		}
		catch (Exception arg)
		{
			Logging.LogError($"Failed to disable.\n{arg}", ServerConfig);
			return false;
		}
	}

	private static bool CanSendSystemChatMessage()
	{
		return NetworkBehaviourSingleton<ChatManager>.Instance != null;
	}

	private static void SendSystemChatMessage(string message)
	{
		ChatManager instance = NetworkBehaviourSingleton<ChatManager>.Instance;
		if (instance == null || string.IsNullOrEmpty(message))
		{
			return;
		}
		ChatMessage chatMessage = new ChatMessage
		{
			SteamID = null,
			Username = null,
			Team = null,
			Content = message,
			Timestamp = Utils.GetTimestamp(),
			IsQuickChat = false,
			IsTeamChat = false,
			IsSystem = true
		};
		if (ServerFunc.IsDedicatedServer())
		{
			Type type = instance.GetType();
			MethodInfo method = type.GetMethod("Server_BroadcastChatMessage", BindingFlags.Instance | BindingFlags.Public, null, new Type[1] { typeof(string) }, null);
			if (method != null)
			{
				method.Invoke(instance, new object[1] { message });
				return;
			}
			MethodInfo method2 = type.GetMethod("Server_BroadcastChatMessage", BindingFlags.Instance | BindingFlags.Public, null, new Type[1] { typeof(ChatMessage) }, null);
			if (method2 != null)
			{
				method2.Invoke(instance, new object[1] { chatMessage });
				return;
			}
			MethodInfo method3 = type.GetMethod("Server_SendChatMessageRpc", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[2]
			{
				typeof(ChatMessage),
				typeof(RpcParams)
			}, null);
			if (method3 != null)
			{
				method3.Invoke(instance, new object[2] { chatMessage, default(RpcParams) });
				return;
			}
			Logging.Log("No compatible server chat broadcast method was found for system messages.", ServerConfig);
			return;
		}
		instance.AddChatMessage(chatMessage);
	}

	private static UIScoreboard GetUIScoreboard()
	{
		return MonoBehaviourSingleton<UIManager>.Instance?.Scoreboard;
	}

	private static VisualElement GetScoreboardContainer()
	{
		UIScoreboard uIScoreboard = GetUIScoreboard();
		if (uIScoreboard == null)
		{
			return null;
		}
		if (SystemFunc.TryGetPrivateField(typeof(UIScoreboard), uIScoreboard, "scoreboard", out VisualElement value))
		{
			return value;
		}
		return MonoBehaviourSingleton<UIManager>.Instance?.RootVisualElement;
	}

	private static Dictionary<Player, VisualElement> GetScoreboardPlayerMap()
	{
		UIScoreboard uIScoreboard = GetUIScoreboard();
		if (uIScoreboard == null)
		{
			return null;
		}
		if (SystemFunc.TryGetPrivateField(typeof(UIScoreboard), uIScoreboard, "playerVisualElementMap", out Dictionary<Player, VisualElement> value))
		{
			return value;
		}
		return null;
	}

	public static void Event_OnStatsTrigger(Dictionary<string, object> message)
	{
		try
		{
			foreach (KeyValuePair<string, object> item in message)
			{
				string key = item.Key;
				if (!(key == "oomtm450_statsSOG"))
				{
					if (key == "logic")
					{
						bool flag = ((item.Value is bool value) ? value : bool.Parse(item.Value.ToString()));
						_logic = flag;
						if (flag)
						{
							_isShootoutActive = false;
						}
					}
					continue;
				}
				string text = item.Value?.ToString();
				_sendSavePercDuringGoalNextFrame_Player = MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayerBySteamId(text);
				if (_sendSavePercDuringGoalNextFrame_Player == null || !_sendSavePercDuringGoalNextFrame_Player)
				{
					Logging.LogError("_sendSavePercDuringGoalNextFrame_Player is null.", ServerConfig);
				}
				else
				{
					_sendSavePercDuringGoalNextFrame = true;
				}
			}
		}
		catch (Exception arg)
		{
			Logging.LogError(string.Format("Error in {0}.\n{1}", "Event_OnStatsTrigger", arg), ServerConfig);
		}
	}

	public static void Event_OnRulesetTrigger(Dictionary<string, object> message)
	{
		try
		{
			foreach (KeyValuePair<string, object> item in message)
			{
				if (item.Key == "pause")
				{
					_paused = ((item.Value is bool value2) ? value2 : bool.Parse(item.Value.ToString()));
				}
			}
		}
		catch (Exception arg)
		{
			Logging.LogError(string.Format("Error in {0}.\n{1}", "Event_OnRulesetTrigger", arg), ServerConfig);
		}
	}

	public static void Event_OnShootoutBegin(Dictionary<string, object> message)
	{
		if (!ServerFunc.IsDedicatedServer())
		{
			return;
		}
		try
		{
			_isShootoutActive = true;
			ResetPostHitTrackingState();
			InitializeCurrentGameShootoutBoxscoreState(message);
		}
		catch (Exception arg2)
		{
			Logging.LogError($"Error in Event_OnShootoutBegin.\n{arg2}", ServerConfig);
		}
	}

	public static void Event_OnShootoutWin(Dictionary<string, object> message)
	{
		_isShootoutActive = false;
		_shootoutCompletedThisGame = true;
		FinalizeCurrentGameShootoutState(message);
		ResetPostHitTrackingState();
	}

	private static void ResetPostHitTrackingState()
	{
		_lastPostHitShooterSteamId = "";
		_lastPostHitShotOrigin = Vector3.zero;
		_lastPostHitStickContactTime = DateTime.MinValue;
		_lastPostHitTime = DateTime.MinValue;
	}

	private static int GetCompletedOvertimePeriods()
	{
		int num = GetCurrentPeriod();
		if (_playByPlayEvents.Count > 0)
		{
			num = Math.Max(num, _playByPlayEvents.Max((PlayByPlayEvent e) => e.Period));
		}
		return Math.Max(0, num - 3);
	}

	private static void EnsureGameEndEventForExport(float? gameTimeOverride = null, int? periodOverride = null)
	{
		if (_playByPlayEvents.Any((PlayByPlayEvent e) => e.EventType == PlayByPlayEventType.GameEnd))
		{
			return;
		}
		int num = periodOverride ?? ((_playByPlayEvents.Count > 0) ? _playByPlayEvents.Max((PlayByPlayEvent e) => e.Period) : GetCurrentPeriod());
		float num2 = gameTimeOverride ?? ((_playByPlayEvents.Count > 0) ? _playByPlayEvents.Max((PlayByPlayEvent e) => e.GameTime) : GetCurrentGameTime());
		PlayByPlayEvent playByPlayEvent = new PlayByPlayEvent
		{
			EventId = _nextPlayByPlayEventId++,
			EventType = PlayByPlayEventType.GameEnd,
			GameTime = num2,
			Period = num,
			PlayerSteamId = "",
			PlayerName = "",
			PlayerTeam = 0,
			PlayerPosition = "",
			PlayerJersey = 0,
			PlayerSpeed = 0f,
			Zone = EventZone.Neutral,
			Position = Vector3.zero,
			Velocity = Vector3.zero,
			ForceMagnitude = 0f,
			Outcome = "end",
			Flags = "",
			Team = "",
			TeamInPossession = ((_currentTeamInPossession == PlayerTeam.None) ? "" : ((_currentTeamInPossession == PlayerTeam.Red) ? "Blue" : "Red")),
			CurrentPlayInPossession = _currentPlayInPossession.ToString(),
			ScoreState = GetScoreState(PlayerTeam.None),
			Timestamp = DateTime.UtcNow
		};
		CaptureTeamRosterData(playByPlayEvent);
		int index = _playByPlayEvents.Count;
		for (int i = 0; i < _playByPlayEvents.Count; i++)
		{
			if (_playByPlayEvents[i].GameTime >= num2)
			{
				index = i;
				break;
			}
		}
		_playByPlayEvents.Insert(index, playByPlayEvent);
		if (_expectedGoalsModel != null && _expectedGoalsModel.Enabled)
		{
			RefreshLiveExpectedGoalsFromPlayByPlay();
		}
	}

	public static void Event_OnClientConnected(Dictionary<string, object> message)
	{
		if (!ServerFunc.IsDedicatedServer())
		{
			return;
		}
		try
		{
			Server_RegisterNamedMessageHandler();
			ulong num = (ulong)message["clientId"];
            _combinedBatchClients.Remove(num);
			MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayerByClientId(num).SteamId.Value.ToString();
			try
			{
				_players_ClientId_SteamId.Add(num, "");
			}
			catch
			{
				_players_ClientId_SteamId.Remove(num);
				_players_ClientId_SteamId.Add(num, "");
			}
			NetworkCommunication.SendData("oomtm450_stats_MOD_VERSION", MOD_VERSION, num, "oomtm450_stats_server", ServerConfig);
			NetworkCommunication.SendData(SERVER_SIGNATURE, SERVER_SIGNATURE_TOKEN, num, "oomtm450_stats_server", ServerConfig);
			SendNetworkPerformanceGameReference(num);
			CheckForRulesetMod();
		}
		catch (Exception arg)
		{
			Logging.LogError(string.Format("Error in {0}.\n{1}", "Event_OnClientConnected", arg), ServerConfig);
		}
	}

	public static void Event_OnClientDisconnected(Dictionary<string, object> message)
	{
		if (!ServerFunc.IsDedicatedServer())
		{
			return;
		}
		try
		{
			ulong num = (ulong)message["clientId"];
            _combinedBatchClients.Remove(num);
			string key;
			try
			{
				key = _players_ClientId_SteamId[num];
			}
			catch
			{
				Logging.LogError(string.Format("Client Id {0} steam Id not found in {1}.", num, "_players_ClientId_SteamId"), ServerConfig);
				return;
			}
			_sentOutOfDateMessage.Remove(num);
			_playerIsDown.Remove(key);
			_playersCurrentPuckTouch.Remove(key);
			_playersLastTimePuckPossession.Remove(key);
			_lastTimeOnCollisionStayOrExitWasCalled.Remove(key);
			_players_ClientId_SteamId.Remove(num);
		}
		catch (Exception arg)
		{
			Logging.LogError(string.Format("Error in {0}.\n{1}", "Event_OnClientDisconnected", arg), ServerConfig);
		}
	}

	public static void Event_OnClientStopped(Dictionary<string, object> message)
	{
		if (NetworkManager.Singleton == null || ServerFunc.IsDedicatedServer())
		{
			return;
		}
		try
		{
			ServerConfig = new OPLStats_Stats.Configs.ServerConfig();
			_hasRegisteredWithNamedMessageHandler = false;
			_registeredNamedMessageHandlerManager = null;
			_registeredNamedMessageHandlerName = "";
			_serverHasResponded = false;
			_serverSignatureConfirmed = false;
			_serverStatsCompatibilityDisabled = false;
			_serverStatsCompatibilityDisabledReason = "";
			_askServerForStartupDataCount = 0;
			_clientPreviousGameSnapshot = null;
			SetClientBoxscoreAvailableTabs(CreateBaseBoxscoreTabList(), refreshVisiblePanel: false);
			ResetBoxscoreHintPromptState();
			Client_ResetLocalMatchState();
			_stickSaves.Clear();
			_bodySaves.Clear();
			_homePlateSaves.Clear();
			_homePlateShots.Clear();
			_passes.Clear();
			_blocks.Clear();
			_hits.Clear();
			_takeaways.Clear();
			_turnovers.Clear();
			_exits.Clear();
			_entries.Clear();
			_shotAttempts.Clear();
			_onIceTeamShotAttempts.Clear();
			_puckBattleWins.Clear();
			_puckBattleLosses.Clear();
			Client_ResetSOG();
			Client_ResetSavePerc();
			Client_ResetPuckTouches();
			Client_ResetPossessionTime();
			Client_ResetPuckBattles();
			Client_ResetExpectedGoalsFor();
			Client_ResetExpectedGoalsAgainst();
			Client_ResetHomePlateSogs();
			Client_ResetTeamStats();
			ResetClientNetworkPerformanceTracking();
			DisposeBoxscorePanel();
			ApplyScoreboardEnabledState(enable: false, force: true);
		}
		catch (Exception arg)
		{
			Logging.LogError(string.Format("Error in {0}.\n{1}", "Event_OnClientStopped", arg), _clientConfig);
		}
	}

	public static void Event_OnPlayerGameStateChanged(Dictionary<string, object> message)
	{
		Dictionary<ulong, string> dictionary = new Dictionary<ulong, string>();
		foreach (KeyValuePair<ulong, string> item in _players_ClientId_SteamId)
		{
			if (string.IsNullOrEmpty(item.Value))
			{
				dictionary.Add(item.Key, MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayerByClientId(item.Key).SteamId.Value.ToString());
			}
		}
		foreach (KeyValuePair<ulong, string> item2 in dictionary)
		{
			if (!string.IsNullOrEmpty(item2.Value))
			{
				_players_ClientId_SteamId[item2.Key] = item2.Value;
				Logging.Log($"Added clientId {item2.Key} linked to Steam Id {item2.Value}.", ServerConfig);
			}
		}
		string text = ((Player)message["player"]).SteamId.Value.ToString();
		if (string.IsNullOrEmpty(text))
		{
			return;
		}
		PlayerRole? playerRole = null;
		if (message.TryGetValue("newGameState", out var value3) && value3 is PlayerGameState playerGameState)
		{
			PlayerRole role = playerGameState.Role;
			if (message.TryGetValue("oldGameState", out var value4) && value4 is PlayerGameState playerGameState2 && playerGameState2.Role == role)
			{
				return;
			}
			playerRole = role;
		}
		else if (message.TryGetValue("newRole", out var value5) && value5 is PlayerRole playerRole2)
		{
			playerRole = playerRole2;
		}
		if (!playerRole.HasValue)
		{
			return;
		}
		if (playerRole.Value != PlayerRole.Goalie)
		{
			if (!_sog.TryGetValue(text, out var _))
			{
				_sog.Add(text, 0);
			}
			QueueStatUpdate("oomtm450_statsSOG" + text, _sog[text].ToString());
		}
		else
		{
			if (!_savePerc.TryGetValue(text, out (int, int) _))
			{
				_savePerc.Add(text, (0, 0));
			}
			QueueStatUpdate("oomtm450_statsSAVEPERC" + text, _savePerc[text].ToString());
		}
	}

	private static void ProcessHit(string hitterSteamId, string hitteeSteamId)
	{
		if (_isShootoutActive)
		{
			return;
		}
		if (!_hits.TryGetValue(hitterSteamId, out var _))
		{
			_hits.Add(hitterSteamId, 0);
		}
		_hits[hitterSteamId]++;
		QueueStatUpdate("oomtm450_statsHIT" + hitterSteamId, _hits[hitterSteamId].ToString());
		LogHit(hitterSteamId, _hits[hitterSteamId]);
		if (!string.IsNullOrWhiteSpace(hitteeSteamId))
		{
			if (!_hitsTaken.TryGetValue(hitteeSteamId, out var _))
			{
				_hitsTaken.Add(hitteeSteamId, 0);
			}
			_hitsTaken[hitteeSteamId]++;
		}
		Player playerBySteamId = MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayerBySteamId(hitterSteamId);
		Player playerBySteamId2 = MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayerBySteamId(hitteeSteamId);
		if (playerBySteamId != null && (bool)playerBySteamId)
		{
			Vector3 position = playerBySteamId.transform.position;
			Vector3 velocity = playerBySteamId.GetComponent<Rigidbody>()?.velocity ?? Vector3.zero;
			RecordPlayByPlayEventInternal(PlayByPlayEventType.Hit, playerBySteamId, position, velocity);
		}
		if (playerBySteamId2 != null && (bool)playerBySteamId2)
		{
			Vector3 position2 = playerBySteamId2.transform.position;
			Vector3 velocity2 = playerBySteamId2.GetComponent<Rigidbody>()?.velocity ?? Vector3.zero;
			RecordPlayByPlayEventInternal(PlayByPlayEventType.Hit, playerBySteamId2, position2, velocity2, "failed");
		}
	}

	private static void ProcessBlock(string blockerSteamId, string shooterSteamId, PlayerTeam shooterTeam, float blockGameTime = 0f)
	{
		if (_isShootoutActive)
		{
			return;
		}
		if (!_blocks.TryGetValue(blockerSteamId, out var _))
		{
			_blocks.Add(blockerSteamId, 0);
		}
		_blocks[blockerSteamId]++;
		QueueStatUpdate("oomtm450_statsBLOCK" + blockerSteamId, _blocks[blockerSteamId].ToString());
		LogBlock(blockerSteamId, _blocks[blockerSteamId]);
		string outcome = "successful";
		if (!string.IsNullOrEmpty(shooterSteamId))
		{
			float currentGameTime = GetCurrentGameTime();
			PlayByPlayEvent shotEvent = _playByPlayEvents.LastOrDefault((PlayByPlayEvent e) => e.PlayerSteamId == shooterSteamId && e.EventType == PlayByPlayEventType.Shot && e.PlayerTeam == (int)shooterTeam && (e.Outcome == "on net" || e.Outcome == "attempt" || e.Outcome == "blocked") && e.GameTime >= currentGameTime - 5f);
			if (shotEvent != null)
			{
				if (_playByPlayEvents.Any((PlayByPlayEvent e) => (e.EventType == PlayByPlayEventType.Save || e.EventType == PlayByPlayEventType.Goal) && e.GameTime >= shotEvent.GameTime && e.GameTime <= shotEvent.GameTime + 5f))
				{
					outcome = "failed";
					shotEvent.Outcome = "on net";
					if (string.IsNullOrEmpty(shotEvent.Flags))
					{
						string flags = DetermineShotFlag(shotEvent.Position, (PlayerTeam)shotEvent.PlayerTeam);
						shotEvent.Flags = flags;
					}
				}
				else
				{
					shotEvent.Outcome = "blocked";
					if (shotEvent.Flags == "HomePlate")
					{
						shotEvent.Flags = "";
					}
				}
			}
		}
		Player playerBySteamId = MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayerBySteamId(blockerSteamId);
		Puck puck = MonoBehaviourSingleton<PuckManager>.Instance?.GetPuck();
		if (playerBySteamId != null && (bool)playerBySteamId && puck != null)
		{
			Vector3 position = puck.transform.position;
			Vector3 velocity = puck.GetComponent<Rigidbody>()?.velocity ?? Vector3.zero;
			float value2 = ((blockGameTime > 0f) ? blockGameTime : GetCurrentGameTime());
			RecordPlayByPlayEventInternal(PlayByPlayEventType.Block, playerBySteamId, position, velocity, outcome, null, null, skipPossessionReset: false, value2);
		}
		RefreshLiveExpectedGoalsFromPlayByPlay();
	}

	private static void ProcessTakeaways(string takeawaySteamId)
	{
		if (_isShootoutActive)
		{
			return;
		}
		DateTime utcNow = DateTime.UtcNow;
		if (_lastTakeawayTime.TryGetValue(takeawaySteamId, out var value) && (utcNow - value).TotalSeconds < 1.0)
		{
			return;
		}
		_lastTakeawayTime[takeawaySteamId] = utcNow;
		if (!_takeaways.TryGetValue(takeawaySteamId, out var value2))
		{
			_takeaways.Add(takeawaySteamId, 0);
		}
		_takeaways[takeawaySteamId]++;
		string dataName = "oomtm450_statsTAKEAWAY" + takeawaySteamId;
		value2 = _takeaways[takeawaySteamId];
		QueueStatUpdate(dataName, value2.ToString());
		LogTakeaways(takeawaySteamId, _takeaways[takeawaySteamId]);
		Player playerBySteamId = MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayerBySteamId(takeawaySteamId);
		if (playerBySteamId != null && (bool)playerBySteamId)
		{
			PlayerTeam value3 = playerBySteamId.Team;
			if (!_teamTakeaways.TryGetValue(value3, out value2))
			{
				_teamTakeaways.Add(value3, 0);
			}
			_teamTakeaways[value3]++;
			QueueStatUpdate("oomtm450_statsTEAM_TAKEAWAYS" + value3, _teamTakeaways[value3].ToString());
		}
	}

private static string GetGoalieSaveSurfaceName(Stick stick, Collision collision)
{
	if (stick != null)
	{
		return "Stick";
	}
	string text = BuildCollisionHierarchyNameText(collision);
	if (string.IsNullOrWhiteSpace(text))
	{
		return "Body";
	}
	string[] array = new string[3] { "head", "torso", "groin" };
	foreach (string value in array)
	{
		if (text.Contains(value))
		{
			return "Body";
		}
	}
	string[] array2 = new string[12] { "goaliepad", "goalie pad", "goalie_pad", "goalie pads", "goaliepads", "leftpad", "rightpad", "legpad", "leg pad", "knee pad", "kneepad", "pad" };
	foreach (string value2 in array2)
	{
		if (text.Contains(value2))
		{
			return "Pad";
		}
	}
	return "Body";
}

private static GameObject GetCollisionHitGameObject(Collision collision)
{
	return collision?.collider?.gameObject ?? collision?.gameObject;
}

private static string BuildCollisionHierarchyNameText(Collision collision)
{
	GameObject collisionHitGameObject = GetCollisionHitGameObject(collision);
	if (collisionHitGameObject == null)
	{
		return "";
	}
	List<string> list = new List<string>();
	Transform transform = collisionHitGameObject.transform;
	int num = 0;
	while (transform != null && num < 4)
	{
		if (!string.IsNullOrWhiteSpace(transform.name))
		{
			list.Add(transform.name.ToLowerInvariant());
		}
		transform = transform.parent;
		num++;
	}
	return string.Join(" ", list);
}

private static string MergeGoalieSaveSurface(string existingSurface, string incomingSurface)
	{
		if (string.Equals(existingSurface, "Stick", StringComparison.OrdinalIgnoreCase) || string.Equals(incomingSurface, "Stick", StringComparison.OrdinalIgnoreCase))
		{
			return "Stick";
		}
		if (string.Equals(existingSurface, "Pad", StringComparison.OrdinalIgnoreCase) || string.Equals(incomingSurface, "Pad", StringComparison.OrdinalIgnoreCase))
		{
			return "Pad";
		}
		return "Body";
	}

	private static int GetZoneArrayIndex(EventZone zone)
	{
		return zone switch
		{
			EventZone.Defensive => 0,
			EventZone.Neutral => 1,
			EventZone.Offensive => 2,
			_ => 1
		};
	}

	private static double[] GetOrCreateZoneAccumulator(LockDictionary<string, double[]> dictionary, string steamId)
	{
		if (!dictionary.TryGetValue(steamId, out double[] value) || value == null || value.Length < 3)
		{
			value = new double[3];
			dictionary[steamId] = value;
		}
		return value;
	}

	private static bool IsGoalPostName(string objectName)
	{
		if (string.IsNullOrWhiteSpace(objectName) || ServerConfig.PostNameFilters == null)
		{
			return false;
		}
		string text = objectName.ToLowerInvariant();
		return ServerConfig.PostNameFilters.Any((string filter) => !string.IsNullOrWhiteSpace(filter) && text.Contains(filter.ToLowerInvariant()));
	}

	private static bool IsOwnGoalPost(PlayerTeam team, float hitZ)
	{
		return team switch
		{
			PlayerTeam.Blue => hitZ > 35f,
			PlayerTeam.Red => hitZ < -35f,
			_ => false
		};
	}

	private static void UpdatePostHitShooterFromStick(Stick stick, Vector3 puckPosition)
	{
		Player player = SystemFunc.GetPlayer(stick);
		if (!ServerConfig.TrackPostHits || _isShootoutActive || stick == null || !player)
		{
			return;
		}
		string text = player.SteamId.Value.ToString();
		if (!string.IsNullOrEmpty(text) && text != "0")
		{
			_lastPostHitShooterSteamId = text;
			_lastPostHitShotOrigin = puckPosition;
			_lastPostHitStickContactTime = DateTime.UtcNow;
		}
	}

	private static void RecordSupplementalPlayByPlayEvent(PlayByPlayEventType eventType, Player player, Vector3 position, Vector3 velocity, string outcome, string flags, float gameTime)
	{
		if (player == null || !player)
		{
			return;
		}
		PlayByPlayEvent playByPlayEvent = new PlayByPlayEvent
		{
			EventId = _nextPlayByPlayEventId++,
			EventType = eventType,
			GameTime = gameTime,
			Period = GetCurrentPeriod(),
			PlayerSteamId = player.SteamId.Value.ToString(),
			PlayerName = player.Username.Value.ToString(),
			PlayerTeam = (int)player.Team,
			PlayerPosition = GetPlayerPosition(player),
			PlayerJersey = player.Number.Value,
			PlayerSpeed = GetPlayerSpeed(player),
			Zone = DetermineEventZone(position, player.Team),
			Position = position,
			Velocity = velocity,
			ForceMagnitude = velocity.magnitude,
			Outcome = outcome ?? "successful",
			Flags = flags ?? "",
			Team = ((player.Team == PlayerTeam.Red) ? "Blue" : "Red"),
			TeamInPossession = ((_currentTeamInPossession == PlayerTeam.None) ? "" : ((_currentTeamInPossession == PlayerTeam.Red) ? "Blue" : "Red")),
			CurrentPlayInPossession = _currentPlayInPossession.ToString(),
			ScoreState = GetScoreState(player.Team),
			Timestamp = DateTime.UtcNow
		};
		CaptureTeamRosterData(playByPlayEvent);
		int index = _playByPlayEvents.Count;
		for (int i = 0; i < _playByPlayEvents.Count; i++)
		{
			if (_playByPlayEvents[i].GameTime >= gameTime)
			{
				index = i;
				break;
			}
		}
		_playByPlayEvents.Insert(index, playByPlayEvent);
		if (_expectedGoalsModel != null && _expectedGoalsModel.Enabled)
		{
			RefreshLiveExpectedGoalsFromPlayByPlay();
		}
	}

	private static void RecordPostHit(string shooterSteamId, Vector3 hitPosition, Vector3 hitVelocity, string collisionObjectName, float gameTime)
	{
		if (!ServerConfig.TrackPostHits || string.IsNullOrEmpty(shooterSteamId))
		{
			return;
		}
		Player playerBySteamId = MonoBehaviourSingleton<PlayerManager>.Instance?.GetPlayerBySteamId(shooterSteamId);
		if (!_postHits.TryGetValue(shooterSteamId, out var value))
		{
			_postHits.Add(shooterSteamId, 0);
		}
		_postHits[shooterSteamId]++;
		if (!_postHitShotPositions.TryGetValue(shooterSteamId, out List<PostHitPosition> value2) || value2 == null)
		{
			value2 = new List<PostHitPosition>();
			_postHitShotPositions[shooterSteamId] = value2;
		}
		value2.Add(new PostHitPosition
		{
			X = (float)Math.Round(hitPosition.x, 3),
			Y = (float)Math.Round(hitPosition.y, 3),
			Z = (float)Math.Round(hitPosition.z, 3),
			GameTimeSeconds = (float)Math.Round(gameTime, 3)
		});
		if (playerBySteamId != null && (bool)playerBySteamId)
		{
			RecordSupplementalPlayByPlayEvent(PlayByPlayEventType.PostHit, playerBySteamId, hitPosition, hitVelocity, "successful", collisionObjectName, gameTime);
		}
		Logging.Log($"Post hit recorded for {shooterSteamId} at {gameTime:F3}s ({hitPosition.x:F2}, {hitPosition.y:F2}, {hitPosition.z:F2})", ServerConfig);
	}

	private static void ProcessPuckTouch(string playerSteamId, Player player = null, Puck puck = null)
	{
		if (_isShootoutActive)
		{
			return;
		}
		DateTime utcNow = DateTime.UtcNow;
		bool flag = true;
		if (_lastPuckTouchTime.TryGetValue(playerSteamId, out var value) && (utcNow - value).TotalSeconds < 0.4)
		{
			flag = false;
		}
		if (!(player != null) || !player || !(puck != null))
		{
			return;
		}
		Vector3 position = puck.transform.position;
		Vector3 velocity = puck.GetComponent<Rigidbody>()?.velocity ?? Vector3.zero;
		EventZone value2 = DetermineEventZone(position, player.Team);
		_lastPlayerZone[playerSteamId] = value2;
		if (!flag)
		{
			if (TrackingDiagnosticsEnabled())
			{
				LogTrackingDiagnostic("touch-debounced-" + playerSteamId, $"Skipped touch for {GetDiagnosticPlayerLabel(player)} because the debounce window is still active. zone={value2}, puckPos={FormatVector3(position)}.", minIntervalMilliseconds: 1000);
			}
			return;
		}
		bool flag2 = false;
		string text = null;
		if (_lastEvent != null && _lastEvent.EventType == PlayByPlayEventType.Touch && _lastEvent.PlayerSteamId != playerSteamId && _lastEvent.PlayerTeam == (int)player.Team && (DateTime.UtcNow - _lastEvent.Timestamp).TotalMilliseconds < 6000.0)
		{
			flag2 = true;
			text = _lastEvent.PlayerSteamId;
		}
		int value3;
		if (flag2 && !string.IsNullOrEmpty(text) && _lastEvent.EventType == PlayByPlayEventType.Touch)
		{
			_lastEvent.EventType = PlayByPlayEventType.Pass;
			_lastEvent.Outcome = "successful";
			Player playerBySteamId = MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayerBySteamId(text);
			if (!_passes.TryGetValue(text, out value3))
			{
				_passes.Add(text, 0);
			}
			_passes[text]++;
			string dataName = "oomtm450_statsPASS" + text;
			value3 = _passes[text];
			QueueStatUpdate(dataName, value3.ToString());
			LogPass(text, _passes[text]);
			if (playerBySteamId != null && (bool)playerBySteamId && !PlayerFunc.IsGoalie(playerBySteamId))
			{
				PlayerTeam value4 = playerBySteamId.Team;
				if (!_teamPasses.TryGetValue(value4, out value3))
				{
					_teamPasses.Add(value4, 0);
				}
				_teamPasses[value4]++;
				string dataName2 = "oomtm450_statsTEAM_PASSES" + value4;
				value3 = _teamPasses[value4];
				QueueStatUpdate(dataName2, value3.ToString());
			}
			_lastPlayerZone[playerSteamId] = value2;
		}
		RecordPlayByPlayEventInternal(PlayByPlayEventType.Touch, player, position, velocity);
		_lastPuckTouchTime[playerSteamId] = utcNow;
		if (!_puckTouches.TryGetValue(playerSteamId, out value3))
		{
			_puckTouches.Add(playerSteamId, 0);
		}
		_puckTouches[playerSteamId]++;
		QueueStatUpdate("oomtm450_statsPUCK_TOUCH" + playerSteamId, _puckTouches[playerSteamId].ToString());
		LogPuckTouch(playerSteamId, _puckTouches[playerSteamId]);
		if (TrackingDiagnosticsEnabled())
		{
			LogTrackingDiagnostic("touch-recorded-" + playerSteamId, $"Recorded touch for {GetDiagnosticPlayerLabel(player)}. zone={value2}, puckPos={FormatVector3(position)}, puckVel={FormatVector3(velocity)}, totalTouches={_puckTouches[playerSteamId]}.", minIntervalMilliseconds: 250);
		}
	}

	private static string FormatTimeAsMinutesSeconds(int totalSeconds)
	{
		int num = Math.Max(0, totalSeconds);
		int num2 = num / 60;
		int num3 = num % 60;
		return $"{num2}:{num3:D2}";
	}

	private static string FormatTimeAsMinutesSeconds(double timeSeconds)
	{
		return FormatTimeAsMinutesSeconds((int)timeSeconds);
	}

	private static string DetermineShotFlag(Vector3 position, PlayerTeam playerTeam)
	{
		float x = position.x;
		float z = position.z;
		if (playerTeam == PlayerTeam.Red)
		{
			float num = Mathf.Abs(z);
			if (num < 18f || num > 40f)
			{
				return "Outside";
			}
			if (num >= 18f && num <= 30f && x >= -11.01f && x <= 11.01f)
			{
				return "HomePlate";
			}
			if (num > 30f && num <= 40f)
			{
				float num2 = (num - 30f) / 10f;
				float num3 = -11f + num2 * 9f;
				float num4 = 11f + num2 * -9f;
				if (x >= num3 - 0.01f && x <= num4 + 0.01f)
				{
					return "HomePlate";
				}
			}
		}
		else
		{
			float num5 = Mathf.Abs(z);
			if (num5 < 18f || num5 > 40f)
			{
				return "Outside";
			}
			if (num5 >= 18f && num5 <= 30f && x >= -11.01f && x <= 11.01f)
			{
				return "HomePlate";
			}
			if (num5 > 30f && num5 <= 40f)
			{
				float num6 = (num5 - 30f) / 10f;
				float num7 = -11f + num6 * 9f;
				float num8 = 11f + num6 * -9f;
				if (x >= num7 - 0.01f && x <= num8 + 0.01f)
				{
					return "HomePlate";
				}
			}
		}
		return "Outside";
	}

	private static void UpdateTeamPossessionTime(PlayerTeam currentTeam)
	{
		if (_isShootoutActive)
		{
			return;
		}
		DateTime utcNow = DateTime.UtcNow;
		double value2;
		if (_currentTeamPossession != currentTeam)
		{
			if (_currentTeamPossession != PlayerTeam.None && _teamLastEventTime.TryGetValue(_currentTeamPossession, out var value) && (utcNow - value).TotalSeconds <= 5.0)
			{
				double totalSeconds = (utcNow - _teamPossessionStartTime).TotalSeconds;
				if (!_teamPossessionTime.TryGetValue(_currentTeamPossession, out value2))
				{
					_teamPossessionTime.Add(_currentTeamPossession, 0.0);
				}
				_teamPossessionTime[_currentTeamPossession] += totalSeconds;
				QueueStatUpdate("oomtm450_statsTEAM_POSSESSION_TIME" + _currentTeamPossession, _teamPossessionTime[_currentTeamPossession], "F2");
				if (TrackingDiagnosticsEnabled())
				{
					LogTrackingDiagnostic("team-possession-" + _currentTeamPossession, $"Team possession updated for {_currentTeamPossession}: +{totalSeconds:F2}s => {_teamPossessionTime[_currentTeamPossession]:F2}s.", minIntervalMilliseconds: 1000);
				}
			}
			_currentTeamPossession = currentTeam;
			_teamPossessionStartTime = utcNow;
			if (currentTeam != PlayerTeam.None)
			{
				_teamLastEventTime[currentTeam] = utcNow;
			}
		}
		else
		{
			if (_currentTeamPossession == PlayerTeam.None)
			{
				return;
			}
			if (_teamLastEventTime.TryGetValue(_currentTeamPossession, out var value3) && (utcNow - value3).TotalSeconds > 5.0)
			{
				_teamPossessionStartTime = utcNow;
				return;
			}
			double totalSeconds2 = (utcNow - _teamPossessionStartTime).TotalSeconds;
			if (!_teamPossessionTime.TryGetValue(_currentTeamPossession, out value2))
			{
				_teamPossessionTime.Add(_currentTeamPossession, 0.0);
			}
			_teamPossessionTime[_currentTeamPossession] += totalSeconds2;
			QueueStatUpdate("oomtm450_statsTEAM_POSSESSION_TIME" + _currentTeamPossession, _teamPossessionTime[_currentTeamPossession], "F2");
			if (TrackingDiagnosticsEnabled())
			{
				LogTrackingDiagnostic("team-possession-" + _currentTeamPossession, $"Team possession updated for {_currentTeamPossession}: +{totalSeconds2:F2}s => {_teamPossessionTime[_currentTeamPossession]:F2}s.", minIntervalMilliseconds: 1000);
			}
			_teamPossessionStartTime = utcNow;
		}
	}

	private static void ValidatePendingTurnoversTakeaways(Player player)
	{
		if (player == null || !player || PlayerFunc.IsGoalie(player) || _currentPlayInPossession != 1 || _currentTeamInPossession != player.Team || _isValidatingTurnoversTakeaways)
		{
			return;
		}
		_isValidatingTurnoversTakeaways = true;
		try
		{
			if (_playByPlayEvents.Count > 0)
			{
				int num = Math.Min(5, _playByPlayEvents.Count);
				for (int num2 = _playByPlayEvents.Count - 1; num2 >= _playByPlayEvents.Count - num; num2--)
				{
					if (_playByPlayEvents[num2].PlayerTeam == (int)player.Team && (_playByPlayEvents[num2].EventType == PlayByPlayEventType.Takeaway || _playByPlayEvents[num2].EventType == PlayByPlayEventType.Turnover))
					{
						return;
					}
				}
			}
			PlayerTeam playerTeam = PlayerTeam.None;
			if (_playByPlayEvents.Count > 1)
			{
				int num3 = _playByPlayEvents.Count - 2;
				while (num3 >= 0 && num3 >= _playByPlayEvents.Count - 22)
				{
					if (_playByPlayEvents[num3].PlayerTeam != (int)player.Team)
					{
						playerTeam = (PlayerTeam)_playByPlayEvents[num3].PlayerTeam;
						break;
					}
					num3--;
				}
			}
			if (playerTeam == PlayerTeam.None && _lastPossession.Team != PlayerTeam.None && _lastPossession.Team != player.Team)
			{
				playerTeam = _lastPossession.Team;
			}
			if (playerTeam == PlayerTeam.None || playerTeam == player.Team)
			{
				return;
			}
			string text = player.SteamId.Value.ToString();
			PlayByPlayEvent playByPlayEvent = null;
			int num4 = 0;
			if (_playByPlayEvents.Count > 0)
			{
				float num5 = 0f;
				int num6 = _playByPlayEvents.Count - 1;
				while (num6 >= 0 && num6 >= _playByPlayEvents.Count - 22)
				{
					if (_playByPlayEvents[num6].PlayerTeam == (int)playerTeam)
					{
						num5 = _playByPlayEvents[num6].GameTime;
						break;
					}
					num6--;
				}
				float num7 = float.MaxValue;
				for (int i = 0; i < _playByPlayEvents.Count; i++)
				{
					if (_playByPlayEvents[i].PlayerTeam != (int)player.Team || !(_playByPlayEvents[i].GameTime > num5))
					{
						continue;
					}
					string text2 = _playByPlayEvents[i].Outcome ?? "";
					bool num8 = _playByPlayEvents[i].EventType == PlayByPlayEventType.Hit;
					bool flag = _playByPlayEvents[i].EventType == PlayByPlayEventType.Shot;
					bool flag2 = text2 == "successful" || text2 == "neutral" || text2 == "" || flag;
					if (!num8 && flag2)
					{
						num4++;
						if (_playByPlayEvents[i].GameTime < num7)
						{
							num7 = _playByPlayEvents[i].GameTime;
							text = _playByPlayEvents[i].PlayerSteamId;
							playByPlayEvent = _playByPlayEvents[i];
						}
					}
				}
			}
			if (num4 < 2)
			{
				return;
			}
			PlayByPlayEvent playByPlayEvent2 = null;
			if (_playByPlayEvents.Count > 1)
			{
				for (int num9 = _playByPlayEvents.Count - 2; num9 >= 0 && num9 >= _playByPlayEvents.Count - 22; num9--)
				{
					if (_playByPlayEvents[num9].PlayerTeam == (int)playerTeam)
					{
						switch (_playByPlayEvents[num9].Outcome ?? "")
						{
						case "successful":
						case "neutral":
						case "":
							break;
						default:
							continue;
						}
						playByPlayEvent2 = _playByPlayEvents[num9];
						break;
					}
				}
			}
			bool flag3 = false;
			if (!((playByPlayEvent2 == null || _playByPlayEvents.Count <= 0) ? ((DateTime.UtcNow - _lastPossession.Date).TotalMilliseconds < 4000.0) : (_playByPlayEvents[_playByPlayEvents.Count - 1].GameTime - playByPlayEvent2.GameTime <= 4f)))
			{
				return;
			}
			int num10 = 0;
			if (playByPlayEvent2 != null && int.TryParse(playByPlayEvent2.CurrentPlayInPossession, out var result))
			{
				num10 = result;
			}
			if (num10 == 0 && _playByPlayEvents.Count > 1)
			{
				int num11 = 0;
				for (int num12 = _playByPlayEvents.Count - 2; num12 >= 0 && num12 >= _playByPlayEvents.Count - 22; num12--)
				{
					if (_playByPlayEvents[num12].PlayerTeam == (int)playerTeam)
					{
						switch (_playByPlayEvents[num12].Outcome ?? "")
						{
						case "successful":
						case "neutral":
						case "":
							num11++;
							continue;
						}
						break;
					}
				}
				num10 = num11;
			}
			if (num10 < 2)
			{
				return;
			}
			if (playByPlayEvent2 != null && playByPlayEvent2.EventType == PlayByPlayEventType.Shot)
			{
				return;
			}
			string text3 = "";
			PlayByPlayEvent playByPlayEvent3 = null;
			if (_playByPlayEvents.Count > 1)
			{
				int num13 = _playByPlayEvents.Count - 2;
				while (num13 >= 0 && num13 >= _playByPlayEvents.Count - 22)
				{
					if (_playByPlayEvents[num13].PlayerTeam == (int)playerTeam)
					{
						if ((_playByPlayEvents[num13].Outcome ?? "") == "failed")
						{
							playByPlayEvent3 = _playByPlayEvents[num13];
							break;
						}
					}
					else if (_playByPlayEvents[num13].PlayerTeam != (int)playerTeam)
					{
						break;
					}
					num13--;
				}
			}
			if (playByPlayEvent3 != null)
			{
				text3 = playByPlayEvent3.PlayerSteamId;
			}
			else if (playByPlayEvent2 != null)
			{
				text3 = playByPlayEvent2.PlayerSteamId;
			}
			else
			{
				int num14 = _playByPlayEvents.Count - 2;
				while (num14 >= 0 && num14 >= _playByPlayEvents.Count - 22)
				{
					if (_playByPlayEvents[num14].PlayerTeam == (int)playerTeam)
					{
						text3 = _playByPlayEvents[num14].PlayerSteamId;
						break;
					}
					if (_playByPlayEvents[num14].PlayerTeam != (int)playerTeam)
					{
						break;
					}
					num14--;
				}
			}
			if (string.IsNullOrEmpty(text3))
			{
				return;
			}
			float turnoverGameTime = 0f;
			if (playByPlayEvent2 != null && playByPlayEvent != null)
			{
				float gameTime = playByPlayEvent2.GameTime;
				float gameTime2 = playByPlayEvent.GameTime;
				turnoverGameTime = gameTime + 0.01f;
				if (turnoverGameTime >= gameTime2)
				{
					turnoverGameTime = (gameTime + gameTime2) / 2f;
				}
			}
			else if (playByPlayEvent2 != null)
			{
				turnoverGameTime = playByPlayEvent2.GameTime + 0.01f;
			}
			else if (playByPlayEvent != null)
			{
				turnoverGameTime = playByPlayEvent.GameTime - 0.01f;
			}
			else
			{
				turnoverGameTime = GetCurrentGameTime();
			}
			bool flag4 = false;
			if (turnoverGameTime > 0f)
			{
				foreach (PlayByPlayEvent playByPlayEvent6 in _playByPlayEvents)
				{
					if (playByPlayEvent6.PlayerSteamId == text3 && playByPlayEvent6.EventType == PlayByPlayEventType.Shot && Math.Abs(playByPlayEvent6.GameTime - turnoverGameTime) <= 5f)
					{
						flag4 = true;
						break;
					}
				}
			}
			if (flag4)
			{
				return;
			}
			ProcessTakeaways(text);
			ProcessTurnovers(text3);
			Player playerBySteamId = MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayerBySteamId(text);
			Player playerBySteamId2 = MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayerBySteamId(text3);
			Puck puck = MonoBehaviourSingleton<PuckManager>.Instance?.GetPuck();
			if (!(playerBySteamId != null) || !playerBySteamId || !(playerBySteamId2 != null) || !playerBySteamId2 || !(puck != null))
			{
				return;
			}
			Vector3 position = puck.transform.position;
			Vector3 velocity = puck.GetComponent<Rigidbody>()?.velocity ?? Vector3.zero;
			string teamInPossessionOverride = ((playerBySteamId.Team == PlayerTeam.Red) ? "Blue" : "Red");
			string teamInPossessionOverride2 = ((playerBySteamId2.Team == PlayerTeam.Red) ? "Blue" : "Red");
			RecordPlayByPlayEventInternal(PlayByPlayEventType.Takeaway, playerBySteamId, position, velocity, "successful", "", null, skipPossessionReset: true, (turnoverGameTime > 0f) ? new float?(turnoverGameTime) : ((float?)null), teamInPossessionOverride);
			RecordPlayByPlayEventInternal(PlayByPlayEventType.Turnover, playerBySteamId2, position, velocity, "failed", "", null, skipPossessionReset: true, (turnoverGameTime > 0f) ? new float?(turnoverGameTime) : ((float?)null), teamInPossessionOverride2);
			if (!(turnoverGameTime > 0f) || _playByPlayEvents.Count < 2)
			{
				return;
			}
			PlayByPlayEvent playByPlayEvent4 = _playByPlayEvents[_playByPlayEvents.Count - 2];
			PlayByPlayEvent playByPlayEvent5 = _playByPlayEvents[_playByPlayEvents.Count - 1];
			if (playByPlayEvent4.EventType == PlayByPlayEventType.Takeaway && playByPlayEvent5.EventType == PlayByPlayEventType.Turnover)
			{
				if (!_recentTurnovers.TryGetValue(text3, out List<(float, int, int)> value))
				{
					value = new List<(float, int, int)>();
					_recentTurnovers.Add(text3, value);
				}
				value.Add((turnoverGameTime, playByPlayEvent5.EventId, playByPlayEvent4.EventId));
				value.RemoveAll(((float, int, int) t) => turnoverGameTime - t.Item1 > 1f);
			}
		}
		finally
		{
			_isValidatingTurnoversTakeaways = false;
		}
	}

	private static void ProcessTurnovers(string turnoverSteamId)
	{
		if (_isShootoutActive)
		{
			return;
		}
		DateTime utcNow = DateTime.UtcNow;
		if (_lastTurnoverTime.TryGetValue(turnoverSteamId, out var value) && (utcNow - value).TotalSeconds < 1.0)
		{
			return;
		}
		_lastTurnoverTime[turnoverSteamId] = utcNow;
		if (!_turnovers.TryGetValue(turnoverSteamId, out var value2))
		{
			_turnovers.Add(turnoverSteamId, 0);
		}
		_turnovers[turnoverSteamId]++;
		string dataName = "oomtm450_statsTURNOVER" + turnoverSteamId;
		value2 = _turnovers[turnoverSteamId];
		QueueStatUpdate(dataName, value2.ToString());
		LogTurnovers(turnoverSteamId, _turnovers[turnoverSteamId]);
		Player playerBySteamId = MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayerBySteamId(turnoverSteamId);
		if (playerBySteamId != null && (bool)playerBySteamId)
		{
			PlayerTeam value3 = playerBySteamId.Team;
			if (!_teamTurnovers.TryGetValue(value3, out value2))
			{
				_teamTurnovers.Add(value3, 0);
			}
			_teamTurnovers[value3]++;
			QueueStatUpdate("oomtm450_statsTEAM_TURNOVERS" + value3, _teamTurnovers[value3].ToString());
		}
	}

	private static void CancelTurnoverIfShotOccurred(string playerSteamId, float shotGameTime)
	{
		if (string.IsNullOrEmpty(playerSteamId) || shotGameTime <= 0f || !_recentTurnovers.TryGetValue(playerSteamId, out List<(float, int, int)> value))
		{
			return;
		}
		foreach (var turnover in value.Where<(float, int, int)>(((float GameTime, int TurnoverEventId, int TakeawayEventId) t) => Math.Abs(shotGameTime - t.GameTime) <= 5f).ToList())
		{
			string text = _playByPlayEvents.FirstOrDefault((PlayByPlayEvent e) => e.EventId == turnover.Item3)?.PlayerSteamId;
			if (_turnovers.TryGetValue(playerSteamId, out var value2) && value2 > 0)
			{
				_turnovers[playerSteamId] = value2 - 1;
				QueueStatUpdate("oomtm450_statsTURNOVER" + playerSteamId, _turnovers[playerSteamId].ToString());
				LogTurnovers(playerSteamId, _turnovers[playerSteamId]);
			}
			Player playerBySteamId = MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayerBySteamId(playerSteamId);
			if (playerBySteamId != null && (bool)playerBySteamId)
			{
				PlayerTeam value3 = playerBySteamId.Team;
				if (_teamTurnovers.TryGetValue(value3, out var value4) && value4 > 0)
				{
					_teamTurnovers[value3] = value4 - 1;
					QueueStatUpdate("oomtm450_statsTEAM_TURNOVERS" + value3, _teamTurnovers[value3].ToString());
				}
			}
			if (!string.IsNullOrEmpty(text))
			{
				if (_takeaways.TryGetValue(text, out var value5) && value5 > 0)
				{
					_takeaways[text] = value5 - 1;
					QueueStatUpdate("oomtm450_statsTAKEAWAY" + text, _takeaways[text].ToString());
					LogTakeaways(text, _takeaways[text]);
				}
				Player playerBySteamId2 = MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayerBySteamId(text);
				if (playerBySteamId2 != null && (bool)playerBySteamId2)
				{
					PlayerTeam value6 = playerBySteamId2.Team;
					if (_teamTakeaways.TryGetValue(value6, out var value7) && value7 > 0)
					{
						_teamTakeaways[value6] = value7 - 1;
						QueueStatUpdate("oomtm450_statsTEAM_TAKEAWAYS" + value6, _teamTakeaways[value6].ToString());
					}
				}
			}
			_playByPlayEvents.RemoveAll((PlayByPlayEvent e) => e.EventId == turnover.Item2 || e.EventId == turnover.Item3);
			value.Remove(turnover);
		}
	}

	private static void ResetLiveTimeOnIceSamplingState()
	{
		_lastLiveToiSampleGameTime = -1f;
	}

	private static void CalculateTimeOnIce()
	{
		SampleLiveTimeOnIceFromGameClock();
	}

	private static void SampleLiveTimeOnIceFromGameClock()
	{
		if (!ServerFunc.IsDedicatedServer() || _paused || _isShootoutActive || !_logic || NetworkBehaviourSingleton<GameManager>.Instance == null)
		{
			return;
		}
		try
		{
			GameManager instance = NetworkBehaviourSingleton<GameManager>.Instance;
			if (instance.Phase != GamePhase.Play)
			{
				ResetLiveTimeOnIceSamplingState();
				return;
			}
			float currentGameTime = GetCurrentGameTime();
			if (currentGameTime < 0f)
			{
				ResetLiveTimeOnIceSamplingState();
				return;
			}
			List<Player> players = MonoBehaviourSingleton<PlayerManager>.Instance?.GetPlayers() ?? new List<Player>();
			foreach (Player player in players)
			{
				if (player != null && player && PlayerFunc.IsPlayerPlaying(player))
				{
					ObservePlayerRole(player);
				}
			}
			if (_lastLiveToiSampleGameTime < 0f)
			{
				_lastLiveToiSampleGameTime = currentGameTime;
				return;
			}
			float num = currentGameTime - _lastLiveToiSampleGameTime;
			if (num <= 0f)
			{
				_lastLiveToiSampleGameTime = currentGameTime;
				return;
			}
			if (num > 5f)
			{
				_lastLiveToiSampleGameTime = currentGameTime;
				return;
			}
			bool flag = false;
			foreach (Player item in players)
			{
				if (item == null || !item || !PlayerFunc.IsPlayerPlaying(item))
				{
					continue;
				}
				string text = item.SteamId.Value.ToString();
				if (string.IsNullOrWhiteSpace(text))
				{
					continue;
				}
				if (!_timeOnIceSeconds.TryGetValue(text, out var value))
				{
					_timeOnIceSeconds.Add(text, 0.0);
					value = 0.0;
				}
				_timeOnIceSeconds[text] = value + (double)num;
				EventZone eventZone = EventZone.Neutral;
				if (item.PlayerBody != null)
				{
					eventZone = DetermineEventZone(item.PlayerBody.transform.position, item.Team);
					_lastPlayerZone[text] = eventZone;
					RecordPlayerHeatSample(text, item.PlayerBody.transform.position, num);
				}
				else if (_lastPlayerZone.TryGetValue(text, out var value2))
				{
					eventZone = value2;
				}
				if (ServerConfig.TrackZoneBreakdowns)
				{
					double[] orCreateZoneAccumulator = GetOrCreateZoneAccumulator(_timeOnIceByZone, text);
					orCreateZoneAccumulator[GetZoneArrayIndex(eventZone)] += num;
				}
				AddPlayerRoleTime(item, num, eventZone);
				flag = true;
			}
			if (flag)
			{
				MarkPreviewTimeOnIceChanged();
			}
			_lastLiveToiSampleGameTime = currentGameTime;
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error in SampleLiveTimeOnIceFromGameClock: {arg}", ServerConfig);
			ResetLiveTimeOnIceSamplingState();
		}
	}

	private static HashSet<string> ExtractSteamIdsFromRosterField(string rosterField)
	{
		HashSet<string> hashSet = new HashSet<string>();
		if (string.IsNullOrEmpty(rosterField))
		{
			return hashSet;
		}
		string[] array = rosterField.Split(';');
		for (int i = 0; i < array.Length; i++)
		{
			string text = array[i].Trim();
			if (text.StartsWith("=\"") && text.EndsWith("\""))
			{
				string text2 = text.Substring(2, text.Length - 3);
				if (!string.IsNullOrEmpty(text2))
				{
					hashSet.Add(text2);
				}
			}
		}
		return hashSet;
	}

	private static double[] NormalizeZoneTimeOnIceToTotal(double[] zoneTimeOnIce, double timeOnIceSeconds)
	{
		double[] array = (zoneTimeOnIce != null && zoneTimeOnIce.Length >= 3) ? new double[3] { zoneTimeOnIce[0], zoneTimeOnIce[1], zoneTimeOnIce[2] } : new double[3];
		if (timeOnIceSeconds <= 0.0)
		{
			return array;
		}
		double num = array.Sum();
		if (num <= 0.0)
		{
			array[1] = timeOnIceSeconds;
			return array;
		}
		double num2 = timeOnIceSeconds / num;
		array[0] *= num2;
		array[1] *= num2;
		array[2] *= num2;
		return array;
	}

	private static int[] AllocateProportionalUnits(double[] values, int totalUnits, int fallbackIndex = 1)
	{
		int[] array = new int[3];
		if (totalUnits <= 0)
		{
			return array;
		}
		double[] array2 = (values != null && values.Length >= 3) ? new double[3] { Math.Max(0.0, values[0]), Math.Max(0.0, values[1]), Math.Max(0.0, values[2]) } : new double[3];
		double num = array2.Sum();
		if (num <= 0.0)
		{
			int num3 = ((fallbackIndex < 0) ? 0 : ((fallbackIndex > 2) ? 2 : fallbackIndex));
			array[num3] = totalUnits;
			return array;
		}
		double[] array3 = new double[3];
		int num2 = 0;
		for (int i = 0; i < 3; i++)
		{
			double num4 = array2[i] / num * (double)totalUnits;
			int num5 = Math.Max(0, (int)Math.Floor(num4));
			array[i] = num5;
			num2 += num5;
			array3[i] = num4 - (double)num5;
		}
		int num6 = totalUnits - num2;
		foreach (int item in Enumerable.Range(0, 3).OrderByDescending((int i) => array3[i]).ThenBy((int i) => i).Take(Math.Max(0, num6)))
		{
			array[item]++;
		}
		return array;
	}

	private static double[] RoundZoneTimeOnIceToDisplayTenths(double[] zoneTimeOnIce, double timeOnIceSeconds)
	{
		double[] array = NormalizeZoneTimeOnIceToTotal(zoneTimeOnIce, timeOnIceSeconds);
		double num = Math.Round(timeOnIceSeconds, 1);
		int[] array2 = AllocateProportionalUnits(array.Select((double x) => x * 10.0).ToArray(), (int)Math.Round(num * 10.0), 1);
		return new double[3]
		{
			(double)array2[0] / 10.0,
			(double)array2[1] / 10.0,
			(double)array2[2] / 10.0
		};
	}

	private static int[] AllocateZoneTimeOnIceDisplaySeconds(double[] zoneTimeOnIce, double timeOnIceSeconds)
	{
		return AllocateProportionalUnits(NormalizeZoneTimeOnIceToTotal(zoneTimeOnIce, timeOnIceSeconds), Math.Max(0, (int)timeOnIceSeconds), 1);
	}

	private static void CalculatePlusMinus()
	{
		_plusMinus.Clear();
		CachedPlayByPlayAnalytics orBuildCachedPlayByPlayAnalytics = GetOrBuildCachedPlayByPlayAnalytics();
		List<PlayByPlayEvent> list = orBuildCachedPlayByPlayAnalytics.OrderedEvents;
		ResolvedPlayByPlaySummary resolvedPlayByPlaySummary = orBuildCachedPlayByPlayAnalytics.ResolvedSummary;
		IEnumerable<PlayByPlayEvent> enumerable = list.Where((PlayByPlayEvent e) => e.EventType == PlayByPlayEventType.Shot && resolvedPlayByPlaySummary.OfficialGoalShotEventIds.Contains(e.EventId));
		foreach (PlayByPlayEvent item in enumerable.Concat(GetOfficialOwnGoalEvents(list).Select((ValueTuple<PlayByPlayEvent, int> x) => x.Item1)))
		{
			if (item.PlayerTeam == 0 && item.EventType != PlayByPlayEventType.OwnGoal)
			{
				continue;
			}
			HashSet<string> hashSet = new HashSet<string>();
			HashSet<string> hashSet2 = new HashSet<string>();
			if (item.EventType == PlayByPlayEventType.OwnGoal)
			{
				hashSet.UnionWith(ExtractSteamIdsFromRosterField(item.OpposingTeamForwardsSteamID));
				hashSet.UnionWith(ExtractSteamIdsFromRosterField(item.OpposingTeamDefencemenSteamID));
				hashSet2.UnionWith(ExtractSteamIdsFromRosterField(item.TeamForwardsSteamID));
				hashSet2.UnionWith(ExtractSteamIdsFromRosterField(item.TeamDefencemenSteamID));
			}
			else
			{
				hashSet.UnionWith(ExtractSteamIdsFromRosterField(item.TeamForwardsSteamID));
				hashSet.UnionWith(ExtractSteamIdsFromRosterField(item.TeamDefencemenSteamID));
				hashSet2.UnionWith(ExtractSteamIdsFromRosterField(item.OpposingTeamForwardsSteamID));
				hashSet2.UnionWith(ExtractSteamIdsFromRosterField(item.OpposingTeamDefencemenSteamID));
			}
			foreach (string item2 in hashSet.ToList())
			{
				Player player = MonoBehaviourSingleton<PlayerManager>.Instance?.GetPlayerBySteamId(item2);
				if (player != null && (bool)player && PlayerFunc.IsGoalie(player))
				{
					hashSet.Remove(item2);
				}
			}
			foreach (string item3 in hashSet2.ToList())
			{
				Player player2 = MonoBehaviourSingleton<PlayerManager>.Instance?.GetPlayerBySteamId(item3);
				if (player2 != null && (bool)player2 && PlayerFunc.IsGoalie(player2))
				{
					hashSet2.Remove(item3);
				}
			}
			int value;
			foreach (string item4 in hashSet)
			{
				if (!string.IsNullOrEmpty(item4))
				{
					if (!_plusMinus.TryGetValue(item4, out value))
					{
						_plusMinus.Add(item4, 0);
					}
					_plusMinus[item4]++;
				}
			}
			foreach (string item5 in hashSet2)
			{
				if (!string.IsNullOrEmpty(item5))
				{
					if (!_plusMinus.TryGetValue(item5, out value))
					{
						_plusMinus.Add(item5, 0);
					}
					_plusMinus[item5]--;
				}
			}
		}
	}

	private static void ProcessPuckBattleWin(string winnerSteamId, string loserSteamId)
	{
		if (_isShootoutActive)
		{
			return;
		}
		if (!_puckBattleWins.TryGetValue(winnerSteamId, out var value))
		{
			_puckBattleWins.Add(winnerSteamId, 0);
		}
		_puckBattleWins[winnerSteamId]++;
		string dataName = "oomtm450_statsPUCK_BATTLE_WINS" + winnerSteamId;
		value = _puckBattleWins[winnerSteamId];
		QueueStatUpdate(dataName, value.ToString());
		Player playerBySteamId = MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayerBySteamId(winnerSteamId);
		if (playerBySteamId != null && (bool)playerBySteamId && !PlayerFunc.IsGoalie(playerBySteamId))
		{
			PlayerTeam value2 = playerBySteamId.Team;
			if (!_teamPuckBattleWins.TryGetValue(value2, out value))
			{
				_teamPuckBattleWins.Add(value2, 0);
			}
			_teamPuckBattleWins[value2]++;
			QueueStatUpdate("oomtm450_statsTEAM_PUCK_BATTLE_WINS" + value2, _teamPuckBattleWins[value2].ToString());
		}
		ProcessPuckBattleLoss(loserSteamId);
	}

	private static void ProcessPuckBattleLoss(string loserSteamId)
	{
		if (!_puckBattleLosses.TryGetValue(loserSteamId, out var value))
		{
			_puckBattleLosses.Add(loserSteamId, 0);
		}
		_puckBattleLosses[loserSteamId]++;
		string dataName = "oomtm450_statsPUCK_BATTLE_LOSSES" + loserSteamId;
		value = _puckBattleLosses[loserSteamId];
		QueueStatUpdate(dataName, value.ToString());
		Player playerBySteamId = MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayerBySteamId(loserSteamId);
		if (playerBySteamId != null && (bool)playerBySteamId && !PlayerFunc.IsGoalie(playerBySteamId))
		{
			PlayerTeam value2 = playerBySteamId.Team;
			if (!_teamPuckBattleLosses.TryGetValue(value2, out value))
			{
				_teamPuckBattleLosses.Add(value2, 0);
			}
			_teamPuckBattleLosses[value2]++;
			QueueStatUpdate("oomtm450_statsTEAM_PUCK_BATTLE_LOSSES" + value2, _teamPuckBattleLosses[value2].ToString());
		}
	}

	private static bool TrackingDiagnosticsEnabled()
	{
		return ServerConfig != null && ServerConfig.TrackingDiagnosticLogging;
	}

	private static void LogTrackingDiagnostic(string key, string message, bool bypassThrottle = false, int minIntervalMilliseconds = -1)
	{
		if (!TrackingDiagnosticsEnabled())
		{
			return;
		}
		int num = ((minIntervalMilliseconds > 0) ? minIntervalMilliseconds : Math.Max(250, ServerConfig.TrackingDiagnosticCooldownMilliseconds));
		DateTime utcNow = DateTime.UtcNow;
		if (!bypassThrottle && !string.IsNullOrEmpty(key))
		{
			if (_trackingDiagnosticLastLogTime.TryGetValue(key, out var value) && (utcNow - value).TotalMilliseconds < (double)num)
			{
				if (_trackingDiagnosticSuppressedCount.TryGetValue(key, out var value2))
				{
					_trackingDiagnosticSuppressedCount[key] = value2 + 1;
				}
				else
				{
					_trackingDiagnosticSuppressedCount.Add(key, 1);
				}
				return;
			}
			_trackingDiagnosticLastLogTime[key] = utcNow;
		}
		if (!string.IsNullOrEmpty(key) && _trackingDiagnosticSuppressedCount.TryGetValue(key, out var value3) && value3 > 0)
		{
			_trackingDiagnosticSuppressedCount[key] = 0;
			message = $"{message} (suppressed {value3} similar events)";
		}
		Logging.Log("[tracking] " + message, ServerConfig, bypassConfig: true);
	}

	private static void TraceCollisionHookEntry(string hookName, Puck puck, Collision collision)
	{
		if (!ServerFunc.IsDedicatedServer() || !TrackingDiagnosticsEnabled())
		{
			return;
		}
		int value = 1;
		if (_collisionHookInvocationCounts.TryGetValue(hookName, out var value2))
		{
			value = value2 + 1;
			_collisionHookInvocationCounts[hookName] = value;
		}
		else
		{
			_collisionHookInvocationCounts.Add(hookName, value);
		}
		GamePhase gamePhase = NetworkBehaviourSingleton<GameManager>.Instance?.Phase ?? GamePhase.None;
		Stick component = collision?.gameObject?.GetComponent<Stick>();
		PlayerBody component2 = collision?.gameObject?.GetComponent<PlayerBody>();
		PlayerBody componentInParent = collision?.gameObject?.GetComponentInParent<PlayerBody>();
		bool bypassThrottle = value <= 3;
		int minIntervalMilliseconds = (hookName == "Stay") ? 2000 : 750;
		string text = ((component2 != null) ? "self" : ((componentInParent != null) ? "parent" : "none"));
		LogTrackingDiagnostic("collision-hook-entry-" + hookName, $"Entered Puck.OnCollision{hookName} count={value}, phase={gamePhase}, paused={_paused}, logic={_logic}, isTouchingStick={(puck != null && puck.IsTouchingStick)}, touchingStick={(puck != null && puck.TouchingStick ? puck.TouchingStick.name : "<none>")}, collisionHasStick={(bool)component}, playerBodySource={text}, {GetCollisionDiagnosticLabel(collision)}.", bypassThrottle, minIntervalMilliseconds);
	}

	private static string GetDiagnosticPlayerLabel(Player player)
	{
		if (!(player != null) || !player)
		{
			return "<no-player>";
		}
		string text = player.Username.Value.ToString();
		if (string.IsNullOrWhiteSpace(text))
		{
			text = "<unnamed>";
		}
		string text2 = player.SteamId.Value.ToString();
		if (text2.Length > 6)
		{
			text2 = text2.Substring(text2.Length - 6);
		}
		return $"{text}/{player.Team}/{text2}";
	}

private static string GetCollisionDiagnosticLabel(Collision collision)
{
	GameObject gameObject = collision?.gameObject;
	GameObject gameObject2 = GetCollisionHitGameObject(collision);
	Rigidbody componentInParent = gameObject?.GetComponentInParent<Rigidbody>();
	string text = gameObject?.name ?? "<null>";
	string text2 = gameObject2?.name ?? "<null>";
	string text3 = gameObject2?.transform?.parent?.name ?? "<none>";
	string text4 = componentInParent?.name ?? "<none>";
	return $"object={text}, hitObject={text2}, parent={text3}, rigidbody={text4}, contacts={(collision != null ? collision.contactCount : 0)}";
}

	private static string FormatVector3(Vector3 value)
	{
		return $"({value.x:F2}, {value.y:F2}, {value.z:F2})";
	}

	private static string DescribeTouchState(int maxEntries = 6)
	{
		if (_playersCurrentPuckTouch.Count == 0)
		{
			return "touches=0";
		}
		List<KeyValuePair<string, Stopwatch>> list = new List<KeyValuePair<string, Stopwatch>>();
		foreach (KeyValuePair<string, Stopwatch> item in _playersCurrentPuckTouch)
		{
			list.Add(item);
		}
		IEnumerable<string> values = list.OrderByDescending((KeyValuePair<string, Stopwatch> x) => x.Value?.ElapsedMilliseconds ?? -1L).Take(maxEntries).Select(delegate(KeyValuePair<string, Stopwatch> x)
		{
			long num = x.Value?.ElapsedMilliseconds ?? (-1);
			long num2 = (_lastTimeOnCollisionStayOrExitWasCalled.TryGetValue(x.Key, out var value) ? value.ElapsedMilliseconds : (-1));
			Player playerBySteamId = MonoBehaviourSingleton<PlayerManager>.Instance?.GetPlayerBySteamId(x.Key);
			string text = ((playerBySteamId != null && (bool)playerBySteamId) ? GetDiagnosticPlayerLabel(playerBySteamId) : x.Key);
			return $"{text}:touch={num}ms,lastContact={num2}ms";
		});
		return string.Join(" | ", values);
	}

	private static string DescribeLastPlayByPlayEvent()
	{
		if (_lastEvent == null)
		{
			return "<none>";
		}
		return $"{_lastEvent.EventType}/{_lastEvent.PlayerSteamId}/{_lastEvent.Outcome}/zone={_lastEvent.Zone}/poss={_lastEvent.TeamInPossession}/play={_lastEvent.CurrentPlayInPossession}";
	}

	private static void ResolvePossessionFromTouchState()
	{
		if (!ServerFunc.IsDedicatedServer() || _paused || !_logic || MonoBehaviourSingleton<PlayerManager>.Instance == null || MonoBehaviourSingleton<PuckManager>.Instance == null || NetworkBehaviourSingleton<GameManager>.Instance == null || NetworkBehaviourSingleton<GameManager>.Instance.Phase != GamePhase.Play)
		{
			return;
		}
		Puck puck = MonoBehaviourSingleton<PuckManager>.Instance.GetPuck();
		string playerSteamIdInPossession = PlayerFunc.GetPlayerSteamIdInPossession(ServerConfig.MinPossessionMilliseconds, ServerConfig.MaxPossessionMilliseconds, _playersCurrentPuckTouch, _lastTimeOnCollisionStayOrExitWasCalled);
		if (string.IsNullOrEmpty(playerSteamIdInPossession))
		{
			if (_playersCurrentPuckTouch.Count > 0 && TrackingDiagnosticsEnabled())
			{
				LogTrackingDiagnostic("possession-none", $"No possession candidate resolved. touchState={DescribeTouchState()}; lastPbp={DescribeLastPlayByPlayEvent()}.", minIntervalMilliseconds: 2000);
			}
			if (!string.IsNullOrEmpty(_lastPossession.SteamId))
			{
				_lastPossessionUpdateTime.Remove(_lastPossession.SteamId);
			}
			UpdateTeamPossessionTime(PlayerTeam.None);
			return;
		}
		Player playerBySteamId = MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayerBySteamId(playerSteamIdInPossession);
		if (!PlayerFunc.IsPlayerPlaying(playerBySteamId) || PlayerFunc.IsGoalie(playerBySteamId))
		{
			if (TrackingDiagnosticsEnabled())
			{
				LogTrackingDiagnostic("possession-invalid", $"Possession resolved to an invalid player candidate ({playerSteamIdInPossession}). touchState={DescribeTouchState()}.", minIntervalMilliseconds: 2000);
			}
			if (!string.IsNullOrEmpty(_lastPossession.SteamId))
			{
				_lastPossessionUpdateTime.Remove(_lastPossession.SteamId);
			}
			UpdateTeamPossessionTime(PlayerTeam.None);
			return;
		}
		bool flag = !string.Equals(_lastPossession.SteamId, playerSteamIdInPossession, StringComparison.Ordinal);
		if (flag)
		{
			if (!string.IsNullOrEmpty(_lastPossession.SteamId))
			{
				_lastPossessionUpdateTime.Remove(_lastPossession.SteamId);
			}
			if (TrackingDiagnosticsEnabled())
			{
				LogTrackingDiagnostic("possession-switch", $"Resolved possession -> {GetDiagnosticPlayerLabel(playerBySteamId)}. previous={_lastPossession.SteamId}/{_lastPossession.Team}. touchState={DescribeTouchState()}.", bypassThrottle: true);
			}
		}
		UpdateResolvedPlayerPossessionTime(playerBySteamId, puck, DateTime.UtcNow);
		MaybeEmitResolvedPossessionTouch(playerBySteamId, puck, flag);
		_lastPossession.SteamId = playerSteamIdInPossession;
		_lastPossession.Team = playerBySteamId.Team;
		_lastPossession.Date = DateTime.UtcNow;
		UpdateTeamPossessionTime(playerBySteamId.Team);
	}

	private static void UpdateResolvedPlayerPossessionTime(Player player, Puck puck, DateTime utcNow)
	{
		if (!(player != null) || !player || PlayerFunc.IsGoalie(player))
		{
			return;
		}
		string text = player.SteamId.Value.ToString();
		if (string.IsNullOrWhiteSpace(text))
		{
			return;
		}
		if (_lastPossessionUpdateTime.TryGetValue(text, out var value))
		{
			double totalSeconds = (utcNow - value).TotalSeconds;
			if (totalSeconds > 0.0 && totalSeconds <= 1.0)
			{
				if (!_possessionTimeSeconds.TryGetValue(text, out var value2))
				{
					_possessionTimeSeconds.Add(text, 0.0);
					value2 = 0.0;
				}
				value2 += totalSeconds;
				_possessionTimeSeconds[text] = value2;
				QueueStatUpdate("oomtm450_statsPOSSESSION_TIME" + text, value2, "F2");
				if (ServerConfig.TrackZoneBreakdowns && puck != null)
				{
					EventZone eventZone = DetermineEventZone(puck.transform.position, player.Team);
					double[] orCreateZoneAccumulator = GetOrCreateZoneAccumulator(_possessionTimeByZone, text);
					orCreateZoneAccumulator[GetZoneArrayIndex(eventZone)] += totalSeconds;
				}
				if (TrackingDiagnosticsEnabled())
				{
					LogTrackingDiagnostic("player-possession-" + text, $"Accumulated possession for {GetDiagnosticPlayerLabel(player)}: +{totalSeconds:F2}s => {_possessionTimeSeconds[text]:F2}s.", minIntervalMilliseconds: 1000);
				}
			}
		}
		_lastPossessionUpdateTime[text] = utcNow;
		_lastPossessionTouchTime[text] = utcNow;
		if (!_possessionStartTime.ContainsKey(text))
		{
			_possessionStartTime[text] = utcNow;
		}
	}

	private static void MaybeEmitResolvedPossessionTouch(Player player, Puck puck, bool forceTouch)
	{
		if (_isShootoutActive || !(player != null) || !player || PlayerFunc.IsGoalie(player) || !(puck != null))
		{
			return;
		}
		string text = player.SteamId.Value.ToString();
		if (string.IsNullOrWhiteSpace(text))
		{
			return;
		}
		DateTime utcNow = DateTime.UtcNow;
		EventZone eventZone = DetermineEventZone(puck.transform.position, player.Team);
		bool flag = forceTouch;
		double totalSeconds = double.MaxValue;
		if (_lastPuckTouchTime.TryGetValue(text, out var value))
		{
			totalSeconds = (utcNow - value).TotalSeconds;
		}
		if (!flag && (!_lastPuckTouchTime.TryGetValue(text, out _) || totalSeconds >= 2.0))
		{
			flag = true;
		}
		if (!flag)
		{
			if (!_lastPlayerZone.TryGetValue(text, out var value2) || value2 != eventZone)
			{
				flag = true;
			}
		}
		if (!flag)
		{
			return;
		}
		if (TrackingDiagnosticsEnabled())
		{
			LogTrackingDiagnostic("touch-fallback-" + text, $"Emitting fallback touch for {GetDiagnosticPlayerLabel(player)}. force={forceTouch}, zone={eventZone}, lastTouchAge={(double.IsPositiveInfinity(totalSeconds) || totalSeconds == double.MaxValue ? -1.0 : totalSeconds):F2}s.", minIntervalMilliseconds: 500);
		}
		ProcessPuckTouch(text, player, puck);
	}

	private static bool RulesetModEnabled()
	{
		if (_rulesetModEnabled.HasValue)
		{
			return _rulesetModEnabled.Value;
		}
		return false;
	}

	private static string GetStarTag(string playerSteamId)
	{
		string result = "";
		if (_stars[1] == playerSteamId)
		{
			result = "<color=#FFD700FF><b>ÃƒÆ’Ã‚Â¢Ãƒâ€¹Ã…â€œÃƒÂ¢Ã¢â€šÂ¬Ã‚Â¦</b></color> ";
		}
		else if (_stars[2] == playerSteamId)
		{
			result = "<color=#C0C0C0FF><b>ÃƒÆ’Ã‚Â¢Ãƒâ€¹Ã…â€œÃƒÂ¢Ã¢â€šÂ¬Ã‚Â¦</b></color> ";
		}
		else if (_stars[3] == playerSteamId)
		{
			result = "<color=#CD7F32FF><b>ÃƒÆ’Ã‚Â¢Ãƒâ€¹Ã…â€œÃƒÂ¢Ã¢â€šÂ¬Ã‚Â¦</b></color> ";
		}
		return result;
	}

	private static string StripHtmlTags(string text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return text;
		}
		while (text.Contains("<") && text.Contains(">"))
		{
			int num = text.IndexOf("<");
			int num2 = text.IndexOf(">", num);
			if (num < 0 || num2 <= num)
			{
				break;
			}
			text = text.Remove(num, num2 - num + 1);
		}
		return text;
	}

	private static string StripStarTags(string text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return text;
		}
		text = text.Replace("<color=#FFD700FF><b>ÃƒÆ’Ã‚Â¢Ãƒâ€¹Ã…â€œÃƒÂ¢Ã¢â€šÂ¬Ã‚Â¦</b></color> ", "");
		text = text.Replace("<color=#C0C0C0FF><b>ÃƒÆ’Ã‚Â¢Ãƒâ€¹Ã…â€œÃƒÂ¢Ã¢â€šÂ¬Ã‚Â¦</b></color> ", "");
		text = text.Replace("<color=#CD7F32FF><b>ÃƒÆ’Ã‚Â¢Ãƒâ€¹Ã…â€œÃƒÂ¢Ã¢â€šÂ¬Ã‚Â¦</b></color> ", "");
		return text;
	}

}
