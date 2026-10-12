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

public partial class Stats
{
	private static bool SendSOGDuringGoal(Player player)
	{
		ResetPuckWasSavedOrBlockedChecks();
		if (!_lastShotWasCounted[player.Team])
		{
			string playerSteamId = player.SteamId.Value.ToString();
			if (string.IsNullOrEmpty(playerSteamId))
			{
				return true;
			}
			if (!_sog.TryGetValue(playerSteamId, out var value))
			{
				_sog.Add(playerSteamId, 0);
			}
			_sog[playerSteamId]++;
			int sog = _sog[playerSteamId];
			QueueStatUpdate("oomtm450_statsSOG" + playerSteamId, sog.ToString());
			LogSOG(playerSteamId, sog);
			if (!_teamShots.TryGetValue(player.Team, out value))
			{
				_teamShots.Add(player.Team, 0);
			}
			_teamShots[player.Team]++;
			string dataName = "oomtm450_statsTEAM_SHOTS" + player.Team;
			value = _teamShots[player.Team];
			QueueStatUpdate(dataName, value.ToString());
			_lastShotWasCounted[player.Team] = true;
			float currentGameTime = GetCurrentGameTime();
			PlayByPlayEvent playByPlayEvent = _playByPlayEvents.LastOrDefault((PlayByPlayEvent e) => e.PlayerSteamId == playerSteamId && e.EventType == PlayByPlayEventType.Shot && (e.Outcome == "attempt" || e.Outcome == "on net" || e.Outcome == "goal") && e.GameTime >= currentGameTime - 3f);
			bool flag = playByPlayEvent != null;
			int value2;
			bool flag2 = playByPlayEvent != null || (_shotAttempts.TryGetValue(playerSteamId, out value2) && value2 > 0);
			if (!flag)
			{
				PlayerTeam attackingTeam = player.Team;
				PlayByPlayEvent playByPlayEvent2 = _playByPlayEvents.LastOrDefault((PlayByPlayEvent e) => e.PlayerSteamId == playerSteamId && e.EventType == PlayByPlayEventType.Touch && e.GameTime >= currentGameTime - 12f);
				if (playByPlayEvent2 == null)
				{
					playByPlayEvent2 = _playByPlayEvents.LastOrDefault((PlayByPlayEvent e) => e.PlayerTeam == (int)attackingTeam && e.EventType == PlayByPlayEventType.Touch && e.GameTime >= currentGameTime - 12f);
				}
				Vector3 vector = playByPlayEvent2?.Position ?? Vector3.zero;
				Vector3 velocity = playByPlayEvent2?.Velocity ?? Vector3.zero;
				float? gameTimeOverride = playByPlayEvent2?.GameTime;
				float? playerSpeedOverride = playByPlayEvent2?.PlayerSpeed;
				if (vector == Vector3.zero)
				{
					PlayByPlayEvent playByPlayEvent3 = _playByPlayEvents.LastOrDefault((PlayByPlayEvent e) => e.PlayerSteamId == playerSteamId && e.EventType == PlayByPlayEventType.Touch);
					if (playByPlayEvent3 != null && playByPlayEvent3.Position != Vector3.zero)
					{
						vector = playByPlayEvent3.Position;
						velocity = playByPlayEvent3.Velocity;
						gameTimeOverride = playByPlayEvent3.GameTime;
						playerSpeedOverride = playByPlayEvent3.PlayerSpeed;
					}
				}
				string text = ((vector != Vector3.zero) ? DetermineShotFlag(vector, player.Team) : "");
				if (playByPlayEvent != null)
				{
					playByPlayEvent.Outcome = "goal";
					if (string.IsNullOrEmpty(playByPlayEvent.Flags))
					{
						playByPlayEvent.Flags = text;
					}
					if (gameTimeOverride.HasValue)
					{
						playByPlayEvent.GameTime = gameTimeOverride.Value;
					}
					if (playerSpeedOverride.HasValue)
					{
						playByPlayEvent.PlayerSpeed = playerSpeedOverride.Value;
					}
					if (vector != Vector3.zero)
					{
						playByPlayEvent.Position = vector;
						playByPlayEvent.Velocity = velocity;
						playByPlayEvent.ForceMagnitude = velocity.magnitude;
					}
				}
				else
				{
					playByPlayEvent = RecordPlayByPlayEventInternal(PlayByPlayEventType.Shot, player, vector, velocity, "goal", text, playerSpeedOverride, skipPossessionReset: false, gameTimeOverride);
				}
				if (!flag2)
				{
					IncrementShotAttemptForPlayer(playerSteamId, player.Team, playByPlayEvent);
				}
				bool flag3 = false;
				if (playByPlayEvent2 != null && text == "HomePlate")
				{
					if (!_homePlateSogs.TryGetValue(playerSteamId, out value))
					{
						_homePlateSogs.Add(playerSteamId, 0);
					}
					_homePlateSogs[playerSteamId]++;
					string dataName4 = "oomtm450_statsHOME_PLATE_SOGS" + playerSteamId;
					value = _homePlateSogs[playerSteamId];
					QueueStatUpdate(dataName4, value.ToString());
					if (!_teamHomePlateSogs.TryGetValue(player.Team, out value))
					{
						_teamHomePlateSogs.Add(player.Team, 0);
					}
					_teamHomePlateSogs[player.Team]++;
					string dataName5 = "oomtm450_statsTEAM_HOME_PLATE_SOGS" + player.Team;
					value = _teamHomePlateSogs[player.Team];
					QueueStatUpdate(dataName5, value.ToString());
					flag3 = true;
				}
				if (!flag3 && _pendingShotReleases.TryGetValue(attackingTeam, out (string, Vector3, Vector3, Vector3, DateTime) value3) && !string.IsNullOrEmpty(value3.Item1) && value3.Item1 == playerSteamId && DetermineShotFlag(value3.Item3, player.Team) == "HomePlate")
				{
					if (!_homePlateSogs.TryGetValue(playerSteamId, out value))
					{
						_homePlateSogs.Add(playerSteamId, 0);
					}
					_homePlateSogs[playerSteamId]++;
					string dataName6 = "oomtm450_statsHOME_PLATE_SOGS" + playerSteamId;
					value = _homePlateSogs[playerSteamId];
					QueueStatUpdate(dataName6, value.ToString());
					if (!_teamHomePlateSogs.TryGetValue(player.Team, out value))
					{
						_teamHomePlateSogs.Add(player.Team, 0);
					}
					_teamHomePlateSogs[player.Team]++;
					QueueStatUpdate("oomtm450_statsTEAM_HOME_PLATE_SOGS" + player.Team, _teamHomePlateSogs[player.Team].ToString());
				}
			}
			else
			{
				PlayByPlayEvent playByPlayEvent4 = playByPlayEvent ?? _playByPlayEvents.LastOrDefault((PlayByPlayEvent e) => e.PlayerSteamId == playerSteamId && e.EventType == PlayByPlayEventType.Shot && e.GameTime >= GetCurrentGameTime() - 3f);
				if (playByPlayEvent4 != null)
				{
					if (playByPlayEvent4.Outcome == "missed")
					{
						playByPlayEvent4.Outcome = "goal";
					}
					else
					{
						playByPlayEvent4.Outcome = "goal";
					}
					if (string.IsNullOrEmpty(playByPlayEvent4.Flags))
					{
						string flags = DetermineShotFlag(playByPlayEvent4.Position, (PlayerTeam)playByPlayEvent4.PlayerTeam);
						playByPlayEvent4.Flags = flags;
					}
					if (playByPlayEvent4.Flags == "HomePlate" && playByPlayEvent4.Outcome == "goal")
					{
						if (!_homePlateSogs.TryGetValue(playerSteamId, out value))
						{
							_homePlateSogs.Add(playerSteamId, 0);
						}
						_homePlateSogs[playerSteamId]++;
						string dataName7 = "oomtm450_statsHOME_PLATE_SOGS" + playerSteamId;
						value = _homePlateSogs[playerSteamId];
						QueueStatUpdate(dataName7, value.ToString());
						if (!_teamHomePlateSogs.TryGetValue(player.Team, out value))
						{
							_teamHomePlateSogs.Add(player.Team, 0);
						}
						_teamHomePlateSogs[player.Team]++;
						QueueStatUpdate("oomtm450_statsTEAM_HOME_PLATE_SOGS" + player.Team, _teamHomePlateSogs[player.Team].ToString());
					}
				}
			}
			Player otherTeamGoalie = PlayerFunc.GetOtherTeamGoalie(player.Team);
			if (otherTeamGoalie != null)
			{
				string text2 = otherTeamGoalie.SteamId.Value.ToString();
				bool flag4 = false;
				PlayByPlayEvent playByPlayEvent5 = _playByPlayEvents.LastOrDefault((PlayByPlayEvent e) => e.PlayerSteamId == playerSteamId && e.EventType == PlayByPlayEventType.Shot && e.GameTime >= GetCurrentGameTime() - 3f);
				if (playByPlayEvent5 != null)
				{
					if (DetermineShotFlag(playByPlayEvent5.Position, (PlayerTeam)playByPlayEvent5.PlayerTeam) == "HomePlate")
					{
						flag4 = true;
					}
				}
				else if (!flag)
				{
					PlayerTeam value4 = player.Team;
					if (_pendingShotReleases.TryGetValue(value4, out (string, Vector3, Vector3, Vector3, DateTime) value5) && !string.IsNullOrEmpty(value5.Item1) && value5.Item1 == playerSteamId && DetermineShotFlag(value5.Item3, player.Team) == "HomePlate")
					{
						flag4 = true;
					}
				}
				if (flag4)
				{
					if (!_homePlateShots.TryGetValue(text2, out var value6))
					{
						_homePlateShots.Add(text2, 0);
						value6 = 0;
					}
					value = (_homePlateShots[text2] = ++value6);
					int num2 = value;
					QueueStatUpdate("oomtm450_statsHOME_PLATE_SHOTS_FACED" + text2, num2.ToString());
				}
			}
			RefreshLiveExpectedGoalsFromPlayByPlay();
			FlushPendingStatUpdatesImmediately();
			return false;
		}
		RefreshLiveExpectedGoalsFromPlayByPlay();
		FlushPendingStatUpdatesImmediately();
		return true;
	}

	private static void ResetPuckWasSavedOrBlockedChecks()
	{
		foreach (PlayerTeam item in new List<PlayerTeam>(_checkIfPuckWasSaved.Keys))
		{
			_checkIfPuckWasSaved[item] = new SaveCheck();
		}
		foreach (PlayerTeam item2 in new List<PlayerTeam>(_checkIfPuckWasBlocked.Keys))
		{
			_checkIfPuckWasBlocked[item2] = new BlockCheck();
		}
	}

	private static void SendSavePercDuringGoal(PlayerTeam team, bool saveWasCounted)
	{
		Player otherTeamGoalie = PlayerFunc.GetOtherTeamGoalie(team);
		if (otherTeamGoalie == null)
		{
			return;
		}
		string _goaliePlayerSteamId = otherTeamGoalie.SteamId.Value.ToString();
		if (!_savePerc.TryGetValue(_goaliePlayerSteamId, out (int, int) value))
		{
			_savePerc.Add(_goaliePlayerSteamId, (0, 0));
			value = (0, 0);
		}
		if (saveWasCounted)
		{
			float currentGameTime = GetCurrentGameTime();
			PlayByPlayEvent lastSaveEvent = (from e in _playByPlayEvents
				where e.PlayerSteamId == _goaliePlayerSteamId && e.EventType == PlayByPlayEventType.Save && e.GameTime >= currentGameTime - 3f
				orderby e.GameTime descending
				select e).FirstOrDefault();
			if (lastSaveEvent != null && !_playByPlayEvents.Any((PlayByPlayEvent e) => e.EventType == PlayByPlayEventType.Touch && e.PlayerTeam == (int)team && e.GameTime > lastSaveEvent.GameTime && e.GameTime < currentGameTime && e.Outcome == "successful"))
			{
				if (lastSaveEvent.Outcome == "successful")
				{
					lastSaveEvent.Outcome = "failed";
				}
				UpdateBodyStickSavesFromPbp();
				if (lastSaveEvent.Flags != null && lastSaveEvent.Flags.Contains("HomePlate") && _homePlateSaves.TryGetValue(_goaliePlayerSteamId, out var value2) && value2 > 0)
				{
					int num = (_homePlateSaves[_goaliePlayerSteamId] = --value2);
					int num3 = num;
					QueueStatUpdate("oomtm450_statsHOME_PLATE_SAVES" + _goaliePlayerSteamId, num3.ToString());
				}
			}
		}
		var (saves, sog) = (_savePerc[_goaliePlayerSteamId] = (saveWasCounted ? (--value.Item1, value.Item2) : (value.Item1, ++value.Item2)));
		QueueStatUpdate("oomtm450_statsSAVEPERC" + _goaliePlayerSteamId, _savePerc[_goaliePlayerSteamId].ToString());
		LogSavePerc(_goaliePlayerSteamId, saves, sog);
	}

	private static void LogSavePerc(string goaliePlayerSteamId, int saves, int sog)
	{
		Logging.Log($"playerSteamId:{goaliePlayerSteamId},saveperc:{GetGoalieSavePerc(saves, sog)},saves:{saves},sog:{sog}", ServerConfig);
	}

	private static void LogStickSave(string playerSteamId, int stickSaves)
	{
		Logging.Log($"playerSteamId:{playerSteamId},sticksv:{stickSaves}", ServerConfig);
	}

	private static void LogSOG(string playerSteamId, int sog)
	{
		Logging.Log($"playerSteamId:{playerSteamId},sog:{sog}", ServerConfig);
	}

	private static void LogBlock(string playerSteamId, int block)
	{
		Logging.Log($"playerSteamId:{playerSteamId},block:{block}", ServerConfig);
	}

	private static void LogHit(string playerSteamId, int hit)
	{
		Logging.Log($"playerSteamId:{playerSteamId},hit:{hit}", ServerConfig);
	}

	private static void LogTakeaways(string playerSteamId, int takeaway)
	{
		Logging.Log($"playerSteamId:{playerSteamId},takeaway:{takeaway}", ServerConfig);
	}

	private static void LogTurnovers(string playerSteamId, int turnover)
	{
		Logging.Log($"playerSteamId:{playerSteamId},turnover:{turnover}", ServerConfig);
	}

	private static void LogPass(string playerSteamId, int pass)
	{
		Logging.Log($"playerSteamId:{playerSteamId},pass:{pass}", ServerConfig);
	}

	private static void LogPuckTouch(string playerSteamId, int puckTouch)
	{
		Logging.Log($"playerSteamId:{playerSteamId},pucktouch:{puckTouch}", ServerConfig);
	}

	private static void LogGWG(string playerSteamId)
	{
		Logging.Log("playerSteamId:" + playerSteamId + ",gwg:1", ServerConfig);
	}

	private static void LogStar(string playerSteamId, int starIndex)
	{
		Logging.Log($"playerSteamId:{playerSteamId},star:{starIndex}", ServerConfig);
	}

	private static PlayByPlayEvent RecordPlayByPlayEventInternal(PlayByPlayEventType eventType, Player player, Vector3 position, Vector3 velocity, string outcome = "successful", string flags = "", float? playerSpeedOverride = null, bool skipPossessionReset = false, float? gameTimeOverride = null, string teamInPossessionOverride = null)
	{
		if (!ServerFunc.IsDedicatedServer() || _paused || NetworkBehaviourSingleton<GameManager>.Instance == null || NetworkBehaviourSingleton<GameManager>.Instance.Phase != GamePhase.Play || !_logic || player == null || !player)
		{
			return null;
		}
		if (_isShootoutActive)
		{
			return null;
		}
		if (_gameStartTime == 0f)
		{
			_gameStartTime = Time.time;
		}
		if (string.IsNullOrEmpty(_currentGameReferenceId))
		{
			_currentGameReferenceId = CreateGameReferenceId(DateTime.UtcNow);
		}
		try
		{
			string text = player.SteamId.Value.ToString();
			if (string.IsNullOrEmpty(text))
			{
				return null;
			}
			string playerPosition = GetPlayerPosition(player);
			ObservePlayerRole(text, playerPosition);
			EventZone zone = DetermineEventZone(position, player.Team);
			float num = gameTimeOverride ?? GetCurrentGameTime();
			int currentPeriod = GetCurrentPeriod();
			PlayerTeam value = player.Team;
			bool flag = eventType == PlayByPlayEventType.PuckBattle;
			bool flag2 = eventType == PlayByPlayEventType.Hit && outcome == "failed";
			bool flag3 = eventType == PlayByPlayEventType.Save;
			bool flag4 = eventType == PlayByPlayEventType.Block;
			bool flag5 = eventType == PlayByPlayEventType.Touch && outcome == "failed";
			if (!flag5 || _currentTeamInPossession == value || _currentTeamInPossession == PlayerTeam.None)
			{
				if (!skipPossessionReset && !flag && !flag2 && !flag3 && !flag4 && !flag5 && _currentTeamInPossession != value)
				{
					if (_lastEvent != null && _lastEvent.PlayerTeam == (int)_currentTeamInPossession)
					{
						PlayByPlayEvent lastTouch = _playByPlayEvents.LastOrDefault((PlayByPlayEvent e) => e.PlayerTeam == (int)_currentTeamInPossession && (e.EventType == PlayByPlayEventType.Touch || e.EventType == PlayByPlayEventType.Takeaway));
						if (lastTouch != null)
						{
							bool flag6 = false;
							bool flag7 = false;
							List<PlayByPlayEvent> list = _playByPlayEvents.Where((PlayByPlayEvent e) => e.EventId > lastTouch.EventId).ToList();
							bool flag8 = false;
							foreach (PlayByPlayEvent item in list)
							{
								if (item.EventType == PlayByPlayEventType.Block && item.PlayerTeam != (int)_currentTeamInPossession)
								{
									flag8 = true;
									continue;
								}
								if (item.EventType == PlayByPlayEventType.Shot || item.EventType == PlayByPlayEventType.Goal)
								{
									flag6 = true;
									break;
								}
								if (flag8 && item.PlayerTeam == (int)_currentTeamInPossession && (item.EventType == PlayByPlayEventType.Touch || item.EventType == PlayByPlayEventType.Pass || item.EventType == PlayByPlayEventType.Shot || item.EventType == PlayByPlayEventType.Goal))
								{
									flag7 = true;
									break;
								}
								if (item.PlayerTeam != (int)_currentTeamInPossession && item.EventType != PlayByPlayEventType.Block && item.EventType != PlayByPlayEventType.Hit)
								{
									break;
								}
							}
							if (!flag6 && !flag7 && lastTouch.EventType == PlayByPlayEventType.Touch)
							{
								lastTouch.Outcome = "failed";
							}
						}
					}
					if (_trackingFaceoffOutcome)
					{
						if (_faceoffPossessionTeam != PlayerTeam.None && _faceoffPossessionTeam != value && _faceoffPossessionChainCount > 0)
						{
							RecordFaceoffOutcome(_faceoffPossessionTeam, won: false);
						}
						_faceoffPossessionChainCount = 0;
						_faceoffPossessionTeam = value;
					}
					_currentTeamInPossession = value;
					_currentPlayInPossession = 0;
					_teamHasExitedDZ[value] = false;
					_teamHasEnteredOZ[value] = false;
				}
				else if (!skipPossessionReset && !flag && !flag2 && !flag3 && !flag4 && _currentTeamInPossession == value)
				{
					_currentPlayInPossession++;
					if (_trackingFaceoffOutcome && (eventType == PlayByPlayEventType.Touch || eventType == PlayByPlayEventType.Takeaway || eventType == PlayByPlayEventType.Pass || eventType == PlayByPlayEventType.Shot || eventType == PlayByPlayEventType.Goal))
					{
						if (_faceoffPossessionTeam == value)
						{
							_faceoffPossessionChainCount++;
							if (_faceoffPossessionChainCount >= 2)
							{
								RecordFaceoffOutcome(value, won: true);
								_trackingFaceoffOutcome = false;
								_pendingBlueFaceoffOutcome = null;
								_pendingRedFaceoffOutcome = null;
							}
						}
						else
						{
							if (_faceoffPossessionTeam != PlayerTeam.None && _faceoffPossessionChainCount > 0)
							{
								RecordFaceoffOutcome(_faceoffPossessionTeam, won: false);
							}
							_faceoffPossessionTeam = value;
							_faceoffPossessionChainCount = 1;
						}
					}
				}
				else if (skipPossessionReset)
				{
					_ = 11;
				}
			}
			string outcome2 = ((eventType == PlayByPlayEventType.PuckBattle) ? "neutral" : outcome);
			string flags2 = flags ?? "";
			PlayByPlayEvent playByPlayEvent = new PlayByPlayEvent
			{
				EventId = _nextPlayByPlayEventId++,
				EventType = eventType,
				GameTime = num,
				Period = currentPeriod,
				PlayerSteamId = text,
				PlayerName = player.Username.Value.ToString(),
				PlayerTeam = (int)value,
				PlayerPosition = playerPosition,
				PlayerJersey = player.Number.Value,
				PlayerSpeed = (playerSpeedOverride ?? GetPlayerSpeed(player)),
				Zone = zone,
				Position = position,
				Velocity = velocity,
				ForceMagnitude = velocity.magnitude,
				Outcome = outcome2,
				Flags = flags2,
				Team = ((value == PlayerTeam.Red) ? "Blue" : "Red"),
				TeamInPossession = (teamInPossessionOverride ?? (((!skipPossessionReset && (eventType != PlayByPlayEventType.Touch || !(outcome == "failed") || _currentTeamInPossession == value)) || _currentTeamInPossession == PlayerTeam.None) ? ((value == PlayerTeam.Red) ? "Blue" : "Red") : ((_currentTeamInPossession == PlayerTeam.Red) ? "Blue" : "Red"))),
				CurrentPlayInPossession = ((eventType == PlayByPlayEventType.Takeaway || eventType == PlayByPlayEventType.Turnover) ? "0" : (((skipPossessionReset || (eventType == PlayByPlayEventType.Touch && outcome == "failed" && _currentTeamInPossession != value)) && _currentTeamInPossession != PlayerTeam.None) ? _currentPlayInPossession.ToString() : ((_currentTeamInPossession == value) ? _currentPlayInPossession.ToString() : "0"))),
				ScoreState = GetScoreState(value),
				Timestamp = DateTime.UtcNow
			};
			CaptureTeamRosterData(playByPlayEvent);
			if (gameTimeOverride.HasValue)
			{
				int index = _playByPlayEvents.Count;
				for (int num2 = 0; num2 < _playByPlayEvents.Count; num2++)
				{
					if (_playByPlayEvents[num2].GameTime >= num)
					{
						index = num2;
						break;
					}
				}
				_playByPlayEvents.Insert(index, playByPlayEvent);
			}
			else
			{
				_playByPlayEvents.Add(playByPlayEvent);
			}
			_lastEvent = playByPlayEvent;
			if (eventType == PlayByPlayEventType.Shot && num > 0f)
			{
				CancelTurnoverIfShotOccurred(text, num);
			}
			int num3;
			switch (outcome)
			{
			default:
				num3 = ((eventType == PlayByPlayEventType.Shot) ? 1 : 0);
				break;
			case "successful":
			case "neutral":
			case "":
				num3 = 1;
				break;
			}
			bool flag9 = (byte)num3 != 0;
			bool flag10 = !flag && !flag2 && !flag3 && !flag4 && (eventType != PlayByPlayEventType.Touch || !(outcome == "failed"));
			if (_currentPlayInPossession == 1 && _currentTeamInPossession == value && flag9 && flag10)
			{
				ValidatePendingTurnoversTakeaways(player);
			}
			DateTime utcNow = DateTime.UtcNow;
			_teamLastEventTime[value] = utcNow;
			if (_currentTeamPossession == value && _teamPossessionStartTime < utcNow.AddSeconds(-5.0))
			{
				_teamPossessionStartTime = utcNow;
			}
			if (!string.IsNullOrEmpty(flags))
			{
				string[] array = flags.Split(',');
				for (int num4 = 0; num4 < array.Length; num4++)
				{
					string text2 = array[num4].Trim();
					int value2;
					if (text2 == "DZExit")
					{
						if (!_exits.TryGetValue(text, out value2))
						{
							_exits.Add(text, 0);
						}
						_exits[text]++;
						string dataName = "oomtm450_statsEXIT" + text;
						value2 = _exits[text];
						QueueStatUpdate(dataName, value2.ToString());
						if (player != null && (bool)player)
						{
							PlayerTeam value3 = player.Team;
							if (!_teamExits.TryGetValue(value3, out value2))
							{
								_teamExits.Add(value3, 0);
							}
							_teamExits[value3]++;
							string dataName2 = "oomtm450_statsTEAM_EXITS" + value3;
							value2 = _teamExits[value3];
							QueueStatUpdate(dataName2, value2.ToString());
						}
					}
					else
					{
						if (!(text2 == "OZEntry"))
						{
							continue;
						}
						if (!_entries.TryGetValue(text, out value2))
						{
							_entries.Add(text, 0);
						}
						_entries[text]++;
						string dataName3 = "oomtm450_statsENTRY" + text;
						value2 = _entries[text];
						QueueStatUpdate(dataName3, value2.ToString());
						if (player != null && (bool)player)
						{
							PlayerTeam value4 = player.Team;
							if (!_teamEntries.TryGetValue(value4, out value2))
							{
								_teamEntries.Add(value4, 0);
							}
							_teamEntries[value4]++;
							string dataName4 = "oomtm450_statsTEAM_ENTRIES" + value4;
							value2 = _teamEntries[value4];
							QueueStatUpdate(dataName4, value2.ToString());
						}
					}
				}
			}
			if (_expectedGoalsModel != null && _expectedGoalsModel.Enabled)
			{
				RefreshLiveExpectedGoalsFromPlayByPlay();
			}
			if (TrackingDiagnosticsEnabled())
			{
				LogTrackingDiagnostic("pbp-" + playByPlayEvent.EventId, $"Recorded PBP {eventType} for {GetDiagnosticPlayerLabel(player)} outcome={outcome2}, flags={flags2}, zone={zone}, teamInPossession={playByPlayEvent.TeamInPossession}, currentPlay={playByPlayEvent.CurrentPlayInPossession}, gameTime={num:F2}.", bypassThrottle: true);
			}
			_lastEvent = playByPlayEvent;
			return playByPlayEvent;
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error recording play-by-play event: {arg}", ServerConfig);
		}
		return null;
	}

	private static void RecordFaceoffEvent()
	{
		if (!ServerFunc.IsDedicatedServer() || _paused || NetworkBehaviourSingleton<GameManager>.Instance == null || !_logic)
		{
			return;
		}
		try
		{
			ResolvePendingFaceoffOnTrackingStop();
			if (!(MonoBehaviourSingleton<PlayerManager>.Instance == null))
			{
				List<Player> players = MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayers();
				if (players != null)
				{
					float currentGameTime = GetCurrentGameTime();
					int currentPeriod = GetCurrentPeriod();
					Vector3 zero = Vector3.zero;
					PlayByPlayEvent playByPlayEvent = new PlayByPlayEvent
					{
						EventId = _nextPlayByPlayEventId++,
						EventType = PlayByPlayEventType.Faceoff,
						GameTime = currentGameTime,
						Period = currentPeriod,
						PlayerSteamId = "",
						PlayerName = "",
						PlayerTeam = 0,
						PlayerPosition = "",
						PlayerJersey = 0,
						PlayerSpeed = 0f,
						Zone = EventZone.Neutral,
						Position = zero,
						Velocity = Vector3.zero,
						ForceMagnitude = 0f,
						Outcome = "",
						Team = "",
						TeamInPossession = "",
						CurrentPlayInPossession = "0",
						ScoreState = GetScoreState(PlayerTeam.None),
						Timestamp = DateTime.UtcNow
					};
					CaptureTeamRosterData(playByPlayEvent);
					_playByPlayEvents.Add(playByPlayEvent);
					_lastFaceoffEventId = playByPlayEvent.EventId;
					_pendingBlueFaceoffOutcome = RecordFaceoffOutcomePlaceholder(PlayerTeam.Red, players, currentGameTime, currentPeriod, zero);
					_pendingRedFaceoffOutcome = RecordFaceoffOutcomePlaceholder(PlayerTeam.Blue, players, currentGameTime, currentPeriod, zero);
					UpdateFaceoffStatsFromPbp();
					_currentTeamInPossession = PlayerTeam.None;
					_currentPlayInPossession = 0;
					_lastEvent = playByPlayEvent;
				}
			}
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error recording faceoff event: {arg}", ServerConfig);
		}
	}

	private static PlayByPlayEvent RecordFaceoffOutcomePlaceholder(PlayerTeam team, IEnumerable<Player> allPlayers, float gameTime, int period, Vector3 centerIce)
	{
		Player player = allPlayers.FirstOrDefault((Player p) => p != null && (bool)p && p.Team == team && GetPlayerPosition(p) == "C");
		PlayByPlayEvent playByPlayEvent = ((!(player != null)) ? new PlayByPlayEvent
		{
			EventId = _nextPlayByPlayEventId++,
			EventType = PlayByPlayEventType.FaceoffOutcome,
			GameTime = gameTime,
			Period = period,
			PlayerSteamId = "",
			PlayerName = "",
			PlayerTeam = (int)team,
			PlayerPosition = "",
			PlayerJersey = 0,
			PlayerSpeed = 0f,
			Zone = EventZone.Neutral,
			Position = centerIce,
			Velocity = Vector3.zero,
			ForceMagnitude = 0f,
			Outcome = "pending",
			Team = ((team == PlayerTeam.Red) ? "Blue" : "Red"),
			TeamInPossession = "",
			CurrentPlayInPossession = "0",
			ScoreState = GetScoreState(team),
			Timestamp = DateTime.UtcNow
		} : new PlayByPlayEvent
		{
			EventId = _nextPlayByPlayEventId++,
			EventType = PlayByPlayEventType.FaceoffOutcome,
			GameTime = gameTime,
			Period = period,
			PlayerSteamId = player.SteamId.Value.ToString(),
			PlayerName = player.Username.Value.ToString(),
			PlayerTeam = (int)team,
			PlayerPosition = "C",
			PlayerJersey = player.Number.Value,
			PlayerSpeed = GetPlayerSpeed(player),
			Zone = EventZone.Neutral,
			Position = centerIce,
			Velocity = Vector3.zero,
			ForceMagnitude = 0f,
			Outcome = "pending",
			Team = ((team == PlayerTeam.Red) ? "Blue" : "Red"),
			TeamInPossession = "",
			CurrentPlayInPossession = "0",
			ScoreState = GetScoreState(team),
			Timestamp = DateTime.UtcNow
		});
		CaptureTeamRosterData(playByPlayEvent);
		_playByPlayEvents.Add(playByPlayEvent);
		return playByPlayEvent;
	}

	private static void ResolvePendingFaceoffOnTrackingStop()
	{
		if (!_trackingFaceoffOutcome)
		{
			return;
		}
		try
		{
			PlayerTeam playerTeam = _faceoffPossessionTeam;
			if (playerTeam == PlayerTeam.None)
			{
				playerTeam = ((_lastEvent == null || _lastEvent.PlayerTeam <= 0) ? PlayerTeam.Red : ((PlayerTeam)_lastEvent.PlayerTeam));
			}
			string teamInPossession = ((playerTeam == PlayerTeam.Red) ? "Blue" : "Red");
			if (_pendingBlueFaceoffOutcome != null)
			{
				_pendingBlueFaceoffOutcome.Outcome = ((PlayerTeam.Red == playerTeam) ? "successful" : "failed");
				_pendingBlueFaceoffOutcome.TeamInPossession = teamInPossession;
			}
			if (_pendingRedFaceoffOutcome != null)
			{
				_pendingRedFaceoffOutcome.Outcome = ((PlayerTeam.Blue == playerTeam) ? "successful" : "failed");
				_pendingRedFaceoffOutcome.TeamInPossession = teamInPossession;
			}
			UpdateFaceoffStatsFromPbp();
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error resolving pending faceoff on tracking stop: {arg}", ServerConfig);
		}
	}

	private static void RecordFaceoffOutcome(PlayerTeam winningTeam, bool won)
	{
		if (!ServerFunc.IsDedicatedServer() || _paused || NetworkBehaviourSingleton<GameManager>.Instance == null || NetworkBehaviourSingleton<GameManager>.Instance.Phase != GamePhase.FaceOff || !_logic)
		{
			return;
		}
		try
		{
			if (MonoBehaviourSingleton<PlayerManager>.Instance == null || MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayers() == null)
			{
				return;
			}
			GetCurrentGameTime();
			GetCurrentPeriod();
			_ = Vector3.zero;
			if (won)
			{
				_ = 2;
				string teamInPossession = ((winningTeam == PlayerTeam.Red) ? "Blue" : "Red");
				if (_pendingBlueFaceoffOutcome != null)
				{
					_pendingBlueFaceoffOutcome.Outcome = ((PlayerTeam.Red == winningTeam) ? "successful" : "failed");
					_pendingBlueFaceoffOutcome.TeamInPossession = teamInPossession;
					_pendingBlueFaceoffOutcome = null;
				}
				if (_pendingRedFaceoffOutcome != null)
				{
					_pendingRedFaceoffOutcome.Outcome = ((PlayerTeam.Blue == winningTeam) ? "successful" : "failed");
					_pendingRedFaceoffOutcome.TeamInPossession = teamInPossession;
					_pendingRedFaceoffOutcome = null;
				}
				UpdateFaceoffStatsFromPbp();
			}
			else
			{
				PlayerTeam playerTeam = ((winningTeam == PlayerTeam.Red) ? PlayerTeam.Blue : PlayerTeam.Red);
				string teamInPossession2 = ((playerTeam == PlayerTeam.Red) ? "Blue" : "Red");
				if (_pendingBlueFaceoffOutcome != null)
				{
					_pendingBlueFaceoffOutcome.Outcome = ((PlayerTeam.Red == playerTeam) ? "successful" : "failed");
					_pendingBlueFaceoffOutcome.TeamInPossession = teamInPossession2;
					_pendingBlueFaceoffOutcome = null;
				}
				if (_pendingRedFaceoffOutcome != null)
				{
					_pendingRedFaceoffOutcome.Outcome = ((PlayerTeam.Blue == playerTeam) ? "successful" : "failed");
					_pendingRedFaceoffOutcome.TeamInPossession = teamInPossession2;
					_pendingRedFaceoffOutcome = null;
				}
				UpdateFaceoffStatsFromPbp();
			}
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error recording faceoff outcome: {arg}", ServerConfig);
		}
	}

	private static void RecordFaceoffOutcomeForTeam(PlayerTeam team, bool won, IEnumerable<Player> allPlayers, float gameTime, int period, Vector3 centerIce)
	{
		Player player = allPlayers.FirstOrDefault((Player p) => p != null && (bool)p && p.Team == team && GetPlayerPosition(p) == "C");
		if (player != null)
		{
			PlayByPlayEvent playByPlayEvent = new PlayByPlayEvent
			{
				EventId = _nextPlayByPlayEventId++,
				EventType = PlayByPlayEventType.FaceoffOutcome,
				GameTime = gameTime,
				Period = period,
				PlayerSteamId = player.SteamId.Value.ToString(),
				PlayerName = player.Username.Value.ToString(),
				PlayerTeam = (int)team,
				PlayerPosition = "C",
				PlayerJersey = player.Number.Value,
				PlayerSpeed = GetPlayerSpeed(player),
				Zone = EventZone.Neutral,
				Position = centerIce,
				Velocity = Vector3.zero,
				ForceMagnitude = 0f,
				Outcome = (won ? "successful" : "failed"),
				Team = ((team == PlayerTeam.Red) ? "Blue" : "Red"),
				TeamInPossession = ((!won) ? ((team == PlayerTeam.Red) ? "Red" : "Blue") : ((team == PlayerTeam.Red) ? "Blue" : "Red")),
				CurrentPlayInPossession = "0",
				ScoreState = GetScoreState(team),
				Timestamp = DateTime.UtcNow
			};
			CaptureTeamRosterData(playByPlayEvent);
			_playByPlayEvents.Add(playByPlayEvent);
		}
		else
		{
			PlayByPlayEvent playByPlayEvent2 = new PlayByPlayEvent
			{
				EventId = _nextPlayByPlayEventId++,
				EventType = PlayByPlayEventType.FaceoffOutcome,
				GameTime = gameTime,
				Period = period,
				PlayerSteamId = "",
				PlayerName = "",
				PlayerTeam = (int)team,
				PlayerPosition = "",
				PlayerJersey = 0,
				PlayerSpeed = 0f,
				Zone = EventZone.Neutral,
				Position = centerIce,
				Velocity = Vector3.zero,
				ForceMagnitude = 0f,
				Outcome = (won ? "successful" : "failed"),
				Team = ((team == PlayerTeam.Red) ? "Blue" : "Red"),
				TeamInPossession = ((!won) ? ((team == PlayerTeam.Red) ? "Red" : "Blue") : ((team == PlayerTeam.Red) ? "Blue" : "Red")),
				CurrentPlayInPossession = "0",
				ScoreState = GetScoreState(team),
				Timestamp = DateTime.UtcNow
			};
			CaptureTeamRosterData(playByPlayEvent2);
			_playByPlayEvents.Add(playByPlayEvent2);
		}
	}

	private static EventZone DetermineEventZone(Vector3 position, PlayerTeam playerTeam)
	{
		if (Mathf.Abs(position.z) <= 13f)
		{
			return EventZone.Neutral;
		}
		bool num = playerTeam == PlayerTeam.Blue;
		bool flag = position.z > 0f;
		if (num == flag)
		{
			return EventZone.Defensive;
		}
		return EventZone.Offensive;
	}

	private static float GetShotTimeoutByZone(EventZone zone)
	{
		return zone switch
		{
			EventZone.Offensive => 2f,
			EventZone.Neutral => 4f,
			EventZone.Defensive => 6f,
			_ => 2f,
		};
	}

	private static float GetCurrentGameTime()
	{
		if (NetworkBehaviourSingleton<GameManager>.Instance == null)
		{
			return 0f;
		}
		try
		{
			int time = NetworkBehaviourSingleton<GameManager>.Instance.GameState.Value.Tick;
			float num = 300f - (float)time;
			int currentPeriod = GetCurrentPeriod();
			float num2 = ((currentPeriod <= 0) ? num : ((float)(currentPeriod - 1) * 300f + num));
			float time2 = Time.time;
			if (time != _lastCountdownValue)
			{
				_lastWholeSecondGameTime = num2;
				_lastUnityTimeForGameTime = time2;
				_lastCountdownValue = time;
				_fractionalSecondTimer = 0f;
			}
			if (NetworkBehaviourSingleton<GameManager>.Instance.Phase == GamePhase.FaceOff)
			{
				if (_lastUnityTimeForGameTime <= 0f || _lastCountdownValue == -1)
				{
					_lastWholeSecondGameTime = num2;
					_lastUnityTimeForGameTime = time2;
					_lastCountdownValue = time;
					_fractionalSecondTimer = 0f;
					return num2 + 0.001f;
				}
				float a = time2 - _lastUnityTimeForGameTime;
				float fractionalSecondTimer = _fractionalSecondTimer;
				float num3 = Mathf.Min(a, 1f);
				if (num3 > fractionalSecondTimer)
				{
					_fractionalSecondTimer = num3;
				}
				return _lastWholeSecondGameTime + _fractionalSecondTimer;
			}
			if (_lastCountdownValue == -1)
			{
				_lastWholeSecondGameTime = num2;
				_lastUnityTimeForGameTime = time2;
				_lastCountdownValue = time;
				_fractionalSecondTimer = 0f;
			}
			return num2;
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error in GetCurrentGameTime: {arg}", ServerConfig);
			return 0f;
		}
	}

	private static string GetScoreState(PlayerTeam teamInPossession)
	{
		if (NetworkBehaviourSingleton<GameManager>.Instance == null)
		{
			return "0-0";
		}
		try
		{
			GameState value = NetworkBehaviourSingleton<GameManager>.Instance.GameState.Value;
			int blueScore = value.BlueScore;
			int redScore = value.RedScore;
			return teamInPossession switch
			{
				PlayerTeam.Red => $"{blueScore}-{redScore}",
				PlayerTeam.Blue => $"{redScore}-{blueScore}",
				_ => $"{blueScore}-{redScore}",
			};
		}
		catch
		{
			return "0-0";
		}
	}

	private static int GetCurrentPeriod()
	{
		if (NetworkBehaviourSingleton<GameManager>.Instance == null)
		{
			return 0;
		}
		try
		{
			int period = NetworkBehaviourSingleton<GameManager>.Instance.GameState.Value.Period;
			if (period > 0)
			{
				return period;
			}
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error in GetCurrentPeriod: {arg}", ServerConfig);
		}
		return 0;
	}

	private static string GetPlayerPosition(Player player)
	{
		if (player == null || !player)
		{
			return "";
		}
		try
		{
			if (PlayerFunc.IsGoalie(player))
			{
				return "G";
			}
			FieldInfo field = PlayerPositionField;
			if (field != null)
			{
				object value = field.GetValue(player);
				if (value != null)
				{
					string text = value.ToString();
					if (text.Contains("("))
					{
						text = text.Substring(0, text.IndexOf("(")).Trim();
					}
					return text;
				}
			}
		}
		catch
		{
		}
		return "";
	}

	private static readonly FieldInfo PlayerPositionField = typeof(Player).GetField("PlayerPosition", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

	private static float GetPlayerSpeed(Player player)
	{
		if (player == null || !player)
		{
			return 0f;
		}
		try
		{
			if ((UnityEngine.Object)(object)player.PlayerBody != null && player.PlayerBody.Rigidbody != null)
			{
				return player.PlayerBody.Rigidbody.linearVelocity.magnitude;
			}
		}
		catch
		{
		}
		return 0f;
	}

	private static void CaptureTeamRosterData(PlayByPlayEvent gameEvent)
	{
		try
		{
			if (MonoBehaviourSingleton<PlayerManager>.Instance == null)
			{
				return;
			}
			List<Player> players = MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayers();
			if (players != null)
			{
				PlayerTeam playerTeam = (PlayerTeam)gameEvent.PlayerTeam;
				PlayerTeam opposingTeam;
				if (playerTeam == PlayerTeam.None && (gameEvent.EventType == PlayByPlayEventType.Faceoff || gameEvent.EventType == PlayByPlayEventType.FaceoffOutcome || gameEvent.EventType == PlayByPlayEventType.GameEnd))
				{
					playerTeam = PlayerTeam.Blue;
					opposingTeam = PlayerTeam.Red;
				}
				else
				{
					opposingTeam = ((playerTeam == PlayerTeam.Red) ? PlayerTeam.Blue : PlayerTeam.Red);
				}
                var rosters = new List<string>[6];
                for (int i = 0; i < rosters.Length; i++) rosters[i] = new List<string>();
                foreach (Player player in players)
                {
                    if (player == null || !player || (player.Team != playerTeam && player.Team != opposingTeam)) continue;
                    string position = GetPlayerPosition(player);
                    int role = position == "C" || position == "LW" || position == "RW" ? 0 : position == "LD" || position == "RD" ? 1 : position == "G" ? 2 : -1;
                    if (role < 0) continue;
                    string id = $"=\"{player.SteamId.Value}\"";
                    if (player.Team == playerTeam) rosters[role].Add(id);
                    if (player.Team == opposingTeam) rosters[role + 3].Add(id);
                }
                gameEvent.TeamForwardsSteamID = string.Join(";", rosters[0]);
                gameEvent.TeamDefencemenSteamID = string.Join(";", rosters[1]);
                gameEvent.TeamGoalieSteamID = string.Join(";", rosters[2]);
                gameEvent.OpposingTeamForwardsSteamID = string.Join(";", rosters[3]);
                gameEvent.OpposingTeamDefencemenSteamID = string.Join(";", rosters[4]);
                gameEvent.OpposingTeamGoalieSteamID = string.Join(";", rosters[5]);
			}
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error capturing team roster data: {arg}", ServerConfig);
		}
	}

	private static void RefreshLiveExpectedGoalsFromPlayByPlay()
	{
		if (!ServerFunc.IsDedicatedServer())
		{
			return;
		}
		try
		{
			BuildAnnotatedOrderedPlayByPlayEvents(out var expectedGoalsExportData);
			ApplyExpectedGoalsAggregate(_expectedGoalsFor, expectedGoalsExportData.ShooterXgBySteamId, "oomtm450_statsEXPECTED_GOALS");
			ApplyExpectedGoalsAggregate(_expectedGoalsAgainst, expectedGoalsExportData.GoalieXgaBySteamId, "oomtm450_statsEXPECTED_GOALS_AGAINST");
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error refreshing live expected model stats: {arg}", ServerConfig);
		}
	}

	private static void ApplyExpectedGoalsAggregate(LockDictionary<string, double> destination, Dictionary<string, double> values, string dataNamePrefix)
	{
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (string item in new List<string>(destination.Keys))
		{
			hashSet.Add(item);
		}
		if (values != null)
		{
			foreach (string key in values.Keys)
			{
				hashSet.Add(key);
			}
		}
		foreach (string item2 in hashSet.OrderBy((string x) => x, StringComparer.Ordinal))
		{
			double num = ((values != null && values.TryGetValue(item2, out var value)) ? value : 0.0);
			double value2 = 0.0;
			bool flag = destination.TryGetValue(item2, out value2);
			if (flag)
			{
				destination[item2] = num;
			}
			else
			{
				destination.Add(item2, num);
			}
			if (!flag || Math.Abs(value2 - num) > 0.00005)
			{
				QueueStatUpdate(dataNamePrefix + item2, num.ToString("0.####", CultureInfo.InvariantCulture));
				if (TrackingDiagnosticsEnabled())
				{
					LogTrackingDiagnostic("expected-model-" + dataNamePrefix + item2, $"Updated {dataNamePrefix}{item2} => {num:0.####}.", minIntervalMilliseconds: 1000);
				}
			}
		}
	}

	private static List<PlayByPlayEvent> BuildAnnotatedOrderedPlayByPlayEvents(out ExpectedGoalsExportData expectedGoalsExportData)
	{
		CachedPlayByPlayAnalytics orBuildCachedPlayByPlayAnalytics = GetOrBuildCachedPlayByPlayAnalytics();
		expectedGoalsExportData = orBuildCachedPlayByPlayAnalytics.ExpectedGoals;
		return orBuildCachedPlayByPlayAnalytics.OrderedEvents;
	}

	private static void ResetPlayByPlayModelAnnotations(List<PlayByPlayEvent> orderedEvents)
	{
		if (orderedEvents == null)
		{
			return;
		}
		foreach (PlayByPlayEvent orderedEvent in orderedEvents)
		{
			orderedEvent.Xg = 0.0;
		}
	}

	private static void AnnotateExpectedGoals(List<PlayByPlayEvent> orderedEvents, ExpectedGoalsExportData expectedGoalsExportData)
	{
		if (_expectedGoalsModel == null || !_expectedGoalsModel.Enabled || orderedEvents == null || orderedEvents.Count == 0)
		{
			return;
		}
		HashSet<string> hashSet = new HashSet<string>((_expectedGoalsModel.IncludedShotOutcomes ?? new List<string>()).Select(NormalizeShotOutcome), StringComparer.OrdinalIgnoreCase);
		if (hashSet.Count == 0)
		{
			hashSet.UnionWith(new string[4] { "goal", "on net", "missed", "blocked" });
		}
		Dictionary<int, double> dictionary = InferAttackingNetZByTeam(orderedEvents, hashSet);
		string modelSignature = GetExpectedGoalsModelCacheSignature();
		for (int i = 0; i < orderedEvents.Count; i++)
		{
			PlayByPlayEvent playByPlayEvent = orderedEvents[i];
			if (!IsEligibleShotForExpectedGoals(playByPlayEvent, hashSet))
			{
				continue;
			}
			double num;
			double netZ = dictionary[playByPlayEvent.PlayerTeam];
			if (_shotXgCache.TryGetValue(playByPlayEvent, out CachedShotXg cachedShot) && playByPlayEvent.GameTime < _analyticsDirtyFromTime && cachedShot.NetZ == netZ && cachedShot.ModelSignature == modelSignature)
			{
				num = cachedShot.Probability;
			}
			else
			{
				num = CalculateExpectedGoalProbability(orderedEvents, i, dictionary);
				_shotXgCache[playByPlayEvent] = new CachedShotXg { Probability = num, NetZ = netZ, ModelSignature = modelSignature };
			}
			playByPlayEvent.Xg = num;
			expectedGoalsExportData.ShotsEvaluated++;
			AddExpectedGoalsValue(expectedGoalsExportData.ShooterXgBySteamId, playByPlayEvent.PlayerSteamId, num);
			AddExpectedGoalsValue(expectedGoalsExportData.GoalieXgaBySteamId, GetOpposingGoalieSteamIdForShot(playByPlayEvent), num);
		}
	}

	private static bool IsPossessionBoundaryEvent(PlayByPlayEvent playByPlayEvent)
	{
		if (playByPlayEvent == null)
		{
			return true;
		}
		return playByPlayEvent.EventType == PlayByPlayEventType.Faceoff || playByPlayEvent.EventType == PlayByPlayEventType.FaceoffOutcome || playByPlayEvent.EventType == PlayByPlayEventType.GameEnd || playByPlayEvent.EventType == PlayByPlayEventType.Goal || playByPlayEvent.EventType == PlayByPlayEventType.OwnGoal;
	}

	private static bool IsEligibleShotForExpectedGoals(PlayByPlayEvent playByPlayEvent, HashSet<string> includedOutcomes)
	{
		if (playByPlayEvent == null || playByPlayEvent.EventType != PlayByPlayEventType.Shot)
		{
			return false;
		}
		if (playByPlayEvent.PlayerTeam != (int)PlayerTeam.Blue && playByPlayEvent.PlayerTeam != (int)PlayerTeam.Red)
		{
			return false;
		}
		return includedOutcomes.Contains(NormalizeShotOutcome(playByPlayEvent.Outcome));
	}

	private static Dictionary<int, double> InferAttackingNetZByTeam(List<PlayByPlayEvent> orderedEvents, HashSet<string> includedOutcomes)
	{
		Dictionary<int, double> dictionary = new Dictionary<int, double>
		{
			[(int)PlayerTeam.Red] = 40.0,
			[(int)PlayerTeam.Blue] = -40.0
		};
		Dictionary<int, double> dictionary2 = new Dictionary<int, double>
		{
			[(int)PlayerTeam.Red] = 0.0,
			[(int)PlayerTeam.Blue] = 0.0
		};
		Dictionary<int, int> dictionary3 = new Dictionary<int, int>
		{
			[(int)PlayerTeam.Red] = 0,
			[(int)PlayerTeam.Blue] = 0
		};
		for (int i = 0; i < orderedEvents.Count; i++)
		{
			PlayByPlayEvent playByPlayEvent = orderedEvents[i];
			if (playByPlayEvent == null || playByPlayEvent.Zone != EventZone.Offensive || !IsEligibleShotForExpectedGoals(playByPlayEvent, includedOutcomes))
			{
				continue;
			}
			dictionary2[playByPlayEvent.PlayerTeam] += playByPlayEvent.Position.z;
			dictionary3[playByPlayEvent.PlayerTeam]++;
		}
		if (dictionary3[(int)PlayerTeam.Red] > 0)
		{
			dictionary[(int)PlayerTeam.Red] = ((dictionary2[(int)PlayerTeam.Red] / dictionary3[(int)PlayerTeam.Red] >= 0.0) ? 40.0 : -40.0);
		}
		if (dictionary3[(int)PlayerTeam.Blue] > 0)
		{
			dictionary[(int)PlayerTeam.Blue] = ((dictionary2[(int)PlayerTeam.Blue] / dictionary3[(int)PlayerTeam.Blue] >= 0.0) ? 40.0 : -40.0);
		}
		return dictionary;
	}

	private static double CalculateExpectedGoalProbability(List<PlayByPlayEvent> orderedEvents, int shotEventIndex, Dictionary<int, double> attackingNetZByTeam)
	{
		if (_expectedGoalsModel == null || !_expectedGoalsModel.Enabled || orderedEvents == null || shotEventIndex < 0 || shotEventIndex >= orderedEvents.Count)
		{
			return 0.0;
		}
		Dictionary<string, double> dictionary = BuildExpectedGoalFeatures(orderedEvents, shotEventIndex, attackingNetZByTeam);
		double num = _expectedGoalsModel.Intercept;
		if (_expectedGoalsModel.Weights != null)
		{
			foreach (KeyValuePair<string, double> weight in _expectedGoalsModel.Weights)
			{
				if (dictionary.TryGetValue(weight.Key, out var value))
				{
					num += value * weight.Value;
				}
			}
		}
		double value2 = 1.0 / (1.0 + Math.Exp(-ClampDouble(num, -60.0, 60.0)));
		return ClampDouble(value2, _expectedGoalsModel.MinimumProbability, _expectedGoalsModel.MaximumProbability);
	}

	private static Dictionary<string, double> BuildExpectedGoalFeatures(List<PlayByPlayEvent> orderedEvents, int shotEventIndex, Dictionary<int, double> attackingNetZByTeam)
	{
		PlayByPlayEvent playByPlayEvent = orderedEvents[shotEventIndex];
		Dictionary<string, double> dictionary = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
		double num = ((playByPlayEvent.PlayerTeam == (int)PlayerTeam.Blue) ? (-40.0) : 40.0);
		if (attackingNetZByTeam != null && attackingNetZByTeam.TryGetValue(playByPlayEvent.PlayerTeam, out var value))
		{
			num = value;
		}
		double num2 = num - playByPlayEvent.Position.z;
		double num3 = Math.Abs(playByPlayEvent.Position.x);
		double num4 = Math.Sqrt(num3 * num3 + num2 * num2);
		double num5 = Math.Atan2(num3, Math.Max(0.1, Math.Abs(num2))) * 180.0 / Math.PI;
		string text = playByPlayEvent.Flags ?? "";
		string text2 = DetermineShotFlag(playByPlayEvent.Position, (PlayerTeam)playByPlayEvent.PlayerTeam);
		bool flag = text.IndexOf("HomePlate", StringComparison.OrdinalIgnoreCase) >= 0 || string.Equals(text2, "HomePlate", StringComparison.OrdinalIgnoreCase);
		PlayByPlayEvent playByPlayEvent2 = FindPreviousSameTeamEvent(orderedEvents, shotEventIndex);
		PlayByPlayEvent playByPlayEvent3 = FindPreviousSameTeamShot(orderedEvents, shotEventIndex);
		double num6 = 10.0;
		double num7 = 0.0;
		double num8 = 0.0;
		bool flag2 = false;
		if (playByPlayEvent2 != null)
		{
			num6 = ClampDouble(playByPlayEvent.GameTime - playByPlayEvent2.GameTime, 0.0, 10.0);
			double num9 = playByPlayEvent.Position.x - playByPlayEvent2.Position.x;
			double num10 = playByPlayEvent.Position.z - playByPlayEvent2.Position.z;
			num7 = Math.Sqrt(num9 * num9 + num10 * num10);
			num8 = Math.Abs(num9);
			flag2 = num6 <= 2.0;
		}
		bool flag3 = false;
		bool flag4 = false;
		if (playByPlayEvent3 != null)
		{
			double num11 = playByPlayEvent.GameTime - playByPlayEvent3.GameTime;
			flag3 = num11 >= 0.0 && num11 <= 4.0;
			flag4 = num11 >= 0.0 && num11 <= 2.0;
		}
		bool flag5 = false;
		for (int i = shotEventIndex - 1; i >= 0; i--)
		{
			PlayByPlayEvent playByPlayEvent4 = orderedEvents[i];
			double num12 = playByPlayEvent.GameTime - playByPlayEvent4.GameTime;
			if (num12 < 0.0)
			{
				continue;
			}
			if (num12 > 6.0)
			{
				break;
			}
			if (playByPlayEvent4.PlayerTeam == playByPlayEvent.PlayerTeam && !string.IsNullOrEmpty(playByPlayEvent4.Flags) && playByPlayEvent4.Flags.IndexOf("OZEntry", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				flag5 = true;
				break;
			}
		}
		int num13 = ParseScoreDiff(playByPlayEvent.ScoreState);
		int num14 = ParseCurrentPlayInPossession(playByPlayEvent.CurrentPlayInPossession);
		string text3 = playByPlayEvent.PlayerPosition ?? "";
		int count = ExtractSteamIdsFromRosterField(playByPlayEvent.TeamForwardsSteamID).Count + ExtractSteamIdsFromRosterField(playByPlayEvent.TeamDefencemenSteamID).Count;
		int count2 = ExtractSteamIdsFromRosterField(playByPlayEvent.OpposingTeamForwardsSteamID).Count + ExtractSteamIdsFromRosterField(playByPlayEvent.OpposingTeamDefencemenSteamID).Count;
		bool flag6 = ExtractSteamIdsFromRosterField(playByPlayEvent.OpposingTeamGoalieSteamID).Count > 0;
		bool flag7 = num4 > 15.0 && num4 <= 25.0 && flag5;
		bool flag8 = num4 <= 15.0 && num5 >= 45.0;
		double num15 = Math.Max(0.0, num4 - 10.0);
		double num16 = Math.Max(0.0, num4 - 20.0);
		double num17 = Math.Max(0.0, num4 - 30.0);
		double num18 = Math.Max(0.0, num5 - 15.0);
		double num19 = Math.Max(0.0, num5 - 35.0);
		double num20 = Math.Max(0.0, num5 - 55.0);
		bool flag9 = num4 <= 12.0;
		bool flag10 = num4 <= 20.0 && flag;
		bool flag11 = num4 >= 30.0;
		bool flag12 = flag && flag4;
		bool flag13 = num4 <= 15.0 && flag4;
		bool flag14 = flag && flag5;
		bool flag15 = flag5 && flag4;
		double num21 = flag4 ? num8 : 0.0;
		dictionary["distance_to_net"] = num4;
		dictionary["distance_squared"] = num4 * num4;
		dictionary["lateral_distance"] = num3;
		dictionary["shot_angle_deg"] = num5;
		dictionary["shot_height"] = playByPlayEvent.Position.y;
		dictionary["home_plate"] = (flag ? 1.0 : 0.0);
		dictionary["outside"] = (flag ? 0.0 : 1.0);
		dictionary["offensive_zone"] = ((playByPlayEvent.Zone == EventZone.Offensive) ? 1.0 : 0.0);
		dictionary["neutral_zone"] = ((playByPlayEvent.Zone == EventZone.Neutral) ? 1.0 : 0.0);
		dictionary["defensive_zone"] = ((playByPlayEvent.Zone == EventZone.Defensive) ? 1.0 : 0.0);
		dictionary["force_magnitude"] = playByPlayEvent.ForceMagnitude;
		dictionary["puck_velocity"] = playByPlayEvent.Velocity.magnitude;
		dictionary["player_speed_kph"] = playByPlayEvent.PlayerSpeed * 3.6f;
		dictionary["score_diff"] = num13;
		dictionary["leading"] = ((num13 > 0) ? 1.0 : 0.0);
		dictionary["trailing"] = ((num13 < 0) ? 1.0 : 0.0);
		dictionary["tied"] = ((num13 == 0) ? 1.0 : 0.0);
		dictionary["current_play_in_possession"] = num14;
		dictionary["period"] = playByPlayEvent.Period;
		dictionary["time_since_last_same_team_event"] = num6;
		dictionary["distance_from_last_same_team_event"] = num7;
		dictionary["lateral_change_from_last_same_team_event"] = num8;
		dictionary["rebound_within_2s"] = (flag4 ? 1.0 : 0.0);
		dictionary["previous_same_team_shot_within_4s"] = (flag3 ? 1.0 : 0.0);
		dictionary["entry_within_6s"] = (flag5 ? 1.0 : 0.0);
		dictionary["same_team_event_within_2s"] = (flag2 ? 1.0 : 0.0);
		dictionary["midrange_entry"] = (flag7 ? 1.0 : 0.0);
		dictionary["sharp_close"] = (flag8 ? 1.0 : 0.0);
		dictionary["distance_spline_10"] = num15;
		dictionary["distance_spline_20"] = num16;
		dictionary["distance_spline_30"] = num17;
		dictionary["angle_spline_15"] = num18;
		dictionary["angle_spline_35"] = num19;
		dictionary["angle_spline_55"] = num20;
		dictionary["close_range"] = (flag9 ? 1.0 : 0.0);
		dictionary["slot_range"] = (flag10 ? 1.0 : 0.0);
		dictionary["point_range"] = (flag11 ? 1.0 : 0.0);
		dictionary["home_plate_rebound"] = (flag12 ? 1.0 : 0.0);
		dictionary["close_rebound"] = (flag13 ? 1.0 : 0.0);
		dictionary["home_plate_entry"] = (flag14 ? 1.0 : 0.0);
		dictionary["entry_rebound"] = (flag15 ? 1.0 : 0.0);
		dictionary["lateral_rebound"] = num21;
		dictionary["own_skaters_on_ice"] = count;
		dictionary["opposing_skaters_on_ice"] = count2;
		dictionary["opposing_goalie_present"] = (flag6 ? 1.0 : 0.0);
		dictionary["is_center"] = (string.Equals(text3, "C", StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0);
		dictionary["is_wing"] = ((string.Equals(text3, "LW", StringComparison.OrdinalIgnoreCase) || string.Equals(text3, "RW", StringComparison.OrdinalIgnoreCase)) ? 1.0 : 0.0);
		dictionary["is_defense"] = ((string.Equals(text3, "LD", StringComparison.OrdinalIgnoreCase) || string.Equals(text3, "RD", StringComparison.OrdinalIgnoreCase)) ? 1.0 : 0.0);
		dictionary["is_goalie"] = (string.Equals(text3, "G", StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0);
		return dictionary;
	}

	private static PlayByPlayEvent FindPreviousSameTeamEvent(List<PlayByPlayEvent> orderedEvents, int shotEventIndex)
	{
		PlayByPlayEvent playByPlayEvent = orderedEvents[shotEventIndex];
		for (int i = shotEventIndex - 1; i >= 0; i--)
		{
			PlayByPlayEvent playByPlayEvent2 = orderedEvents[i];
			if (playByPlayEvent2.PlayerTeam == playByPlayEvent.PlayerTeam && playByPlayEvent2.EventType != PlayByPlayEventType.Hit && playByPlayEvent2.EventType != PlayByPlayEventType.PostHit && playByPlayEvent2.EventType != PlayByPlayEventType.GameEnd)
			{
				return playByPlayEvent2;
			}
		}
		return null;
	}

	private static PlayByPlayEvent FindPreviousSameTeamShot(List<PlayByPlayEvent> orderedEvents, int shotEventIndex)
	{
		PlayByPlayEvent playByPlayEvent = orderedEvents[shotEventIndex];
		for (int i = shotEventIndex - 1; i >= 0; i--)
		{
			PlayByPlayEvent playByPlayEvent2 = orderedEvents[i];
			if (playByPlayEvent2.PlayerTeam == playByPlayEvent.PlayerTeam && playByPlayEvent2.EventType == PlayByPlayEventType.Shot)
			{
				return playByPlayEvent2;
			}
		}
		return null;
	}

	private static string GetOpposingGoalieSteamIdForShot(PlayByPlayEvent shotEvent)
	{
		if (shotEvent == null)
		{
			return "";
		}
		HashSet<string> hashSet = ExtractSteamIdsFromRosterField(shotEvent.OpposingTeamGoalieSteamID);
		if (hashSet.Count == 0)
		{
			return "";
		}
		return hashSet.OrderBy((string x) => x).FirstOrDefault() ?? "";
	}

	private static string GetTeamGoalieSteamIdForEvent(PlayByPlayEvent playByPlayEvent)
	{
		if (playByPlayEvent == null)
		{
			return "";
		}
		HashSet<string> hashSet = ExtractSteamIdsFromRosterField(playByPlayEvent.TeamGoalieSteamID);
		if (hashSet.Count == 0)
		{
			return "";
		}
		return hashSet.OrderBy((string x) => x).FirstOrDefault() ?? "";
	}

	private static string GetOpposingGoalieSteamIdForEvent(PlayByPlayEvent playByPlayEvent)
	{
		if (playByPlayEvent == null)
		{
			return "";
		}
		HashSet<string> hashSet = ExtractSteamIdsFromRosterField(playByPlayEvent.OpposingTeamGoalieSteamID);
		if (hashSet.Count == 0)
		{
			return "";
		}
		return hashSet.OrderBy((string x) => x).FirstOrDefault() ?? "";
	}

	private static void AddCount(Dictionary<string, int> dictionary, string steamId, int amount = 1)
	{
		if (dictionary == null || string.IsNullOrWhiteSpace(steamId) || amount == 0)
		{
			return;
		}
		if (!dictionary.ContainsKey(steamId))
		{
			dictionary[steamId] = 0;
		}
		dictionary[steamId] += amount;
	}

	private static void AddGoalieSummaryValue(Dictionary<string, (int ShotsFaced, int GoalsAllowed)> dictionary, string goalieSteamId, int shotsFacedDelta, int goalsAllowedDelta)
	{
		if (dictionary == null || string.IsNullOrWhiteSpace(goalieSteamId))
		{
			return;
		}
		if (!dictionary.TryGetValue(goalieSteamId, out var value))
		{
			value = (ShotsFaced: 0, GoalsAllowed: 0);
		}
		value.ShotsFaced = Math.Max(0, value.ShotsFaced + shotsFacedDelta);
		value.GoalsAllowed = Math.Max(0, value.GoalsAllowed + goalsAllowedDelta);
		if (value.GoalsAllowed > value.ShotsFaced)
		{
			value.ShotsFaced = value.GoalsAllowed;
		}
		dictionary[goalieSteamId] = value;
	}

	private static List<(PlayByPlayEvent Event, int Index)> GetOfficialOwnGoalEvents(List<PlayByPlayEvent> orderedEvents)
	{
		List<(PlayByPlayEvent Event, int Index)> list = new List<(PlayByPlayEvent Event, int Index)>();
		if (orderedEvents == null)
		{
			return list;
		}
		float num = -999f;
		int num2 = -1;
		int num3 = int.MinValue;
		for (int i = 0; i < orderedEvents.Count; i++)
		{
			PlayByPlayEvent playByPlayEvent = orderedEvents[i];
			if (playByPlayEvent == null || playByPlayEvent.EventType != PlayByPlayEventType.OwnGoal)
			{
				continue;
			}
			if (playByPlayEvent.Period == num2 && playByPlayEvent.PlayerTeam == num3 && Math.Abs(playByPlayEvent.GameTime - num) <= 1.5f)
			{
				continue;
			}
			list.Add((playByPlayEvent, i));
			num = playByPlayEvent.GameTime;
			num2 = playByPlayEvent.Period;
			num3 = playByPlayEvent.PlayerTeam;
		}
		return list;
	}

	private static int FindBestShotIndexForOfficialGoal(List<PlayByPlayEvent> orderedEvents, GoalInfo goalInfo, HashSet<int> reservedShotIndices)
	{
		if (orderedEvents == null || goalInfo == null || string.IsNullOrWhiteSpace(goalInfo.Scorer))
		{
			return -1;
		}
		int num = string.Equals(goalInfo.Team, "Red", StringComparison.OrdinalIgnoreCase) ? ((int)PlayerTeam.Red) : (string.Equals(goalInfo.Team, "Blue", StringComparison.OrdinalIgnoreCase) ? ((int)PlayerTeam.Blue) : 0);
		int? num2 = null;
		for (int i = orderedEvents.Count - 1; i >= 0; i--)
		{
			PlayByPlayEvent playByPlayEvent = orderedEvents[i];
			if (playByPlayEvent == null)
			{
				continue;
			}
			if (playByPlayEvent.Period != goalInfo.Period)
			{
				if (playByPlayEvent.Period < goalInfo.Period)
				{
					break;
				}
				continue;
			}
			if (playByPlayEvent.GameTime > goalInfo.GameTime + 1f)
			{
				continue;
			}
			if (goalInfo.GameTime - playByPlayEvent.GameTime > 15f)
			{
				break;
			}
			if (reservedShotIndices.Contains(i) || playByPlayEvent.EventType != PlayByPlayEventType.Shot || !string.Equals(playByPlayEvent.PlayerSteamId, goalInfo.Scorer, StringComparison.Ordinal))
			{
				continue;
			}
			if (num != 0 && playByPlayEvent.PlayerTeam != num)
			{
				continue;
			}
			string text = NormalizeShotOutcome(playByPlayEvent.Outcome);
			if (string.Equals(text, "goal", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "on net", StringComparison.OrdinalIgnoreCase))
			{
				return i;
			}
			if (!num2.HasValue)
			{
				num2 = i;
			}
		}
		return num2 ?? -1;
	}

	private static int FindBestShotIndexForOwnGoalEvent(List<PlayByPlayEvent> orderedEvents, int ownGoalEventIndex, HashSet<int> reservedShotIndices)
	{
		if (orderedEvents == null || ownGoalEventIndex <= 0 || ownGoalEventIndex >= orderedEvents.Count)
		{
			return -1;
		}
		PlayByPlayEvent playByPlayEvent = orderedEvents[ownGoalEventIndex];
		if (playByPlayEvent == null)
		{
			return -1;
		}
		int num = (playByPlayEvent.PlayerTeam == (int)PlayerTeam.Red) ? ((int)PlayerTeam.Blue) : ((playByPlayEvent.PlayerTeam == (int)PlayerTeam.Blue) ? ((int)PlayerTeam.Red) : 0);
		if (num == 0)
		{
			return -1;
		}
		int? num2 = null;
		for (int i = ownGoalEventIndex - 1; i >= 0; i--)
		{
			PlayByPlayEvent playByPlayEvent2 = orderedEvents[i];
			if (playByPlayEvent2 == null)
			{
				continue;
			}
			if (playByPlayEvent2.Period != playByPlayEvent.Period)
			{
				break;
			}
			if (playByPlayEvent.GameTime - playByPlayEvent2.GameTime > 15f)
			{
				break;
			}
			if (reservedShotIndices.Contains(i) || playByPlayEvent2.EventType != PlayByPlayEventType.Shot || playByPlayEvent2.PlayerTeam != num)
			{
				continue;
			}
			string text = NormalizeShotOutcome(playByPlayEvent2.Outcome);
			if (string.Equals(text, "goal", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "on net", StringComparison.OrdinalIgnoreCase))
			{
				return i;
			}
			if (!num2.HasValue)
			{
				num2 = i;
			}
		}
		return num2 ?? -1;
	}

	private static string FindOpposingGoalieSteamIdForOfficialGoalEvent(List<PlayByPlayEvent> orderedEvents, GoalInfo goalInfo)
	{
		if (orderedEvents == null || goalInfo == null || string.IsNullOrWhiteSpace(goalInfo.Scorer))
		{
			return "";
		}
		int num = string.Equals(goalInfo.Team, "Red", StringComparison.OrdinalIgnoreCase) ? ((int)PlayerTeam.Red) : (string.Equals(goalInfo.Team, "Blue", StringComparison.OrdinalIgnoreCase) ? ((int)PlayerTeam.Blue) : 0);
		for (int i = orderedEvents.Count - 1; i >= 0; i--)
		{
			PlayByPlayEvent playByPlayEvent = orderedEvents[i];
			if (playByPlayEvent == null)
			{
				continue;
			}
			if (playByPlayEvent.Period != goalInfo.Period)
			{
				if (playByPlayEvent.Period < goalInfo.Period)
				{
					break;
				}
				continue;
			}
			if (playByPlayEvent.GameTime > goalInfo.GameTime + 2f)
			{
				continue;
			}
			if (goalInfo.GameTime - playByPlayEvent.GameTime > 8f)
			{
				break;
			}
			if (playByPlayEvent.EventType == PlayByPlayEventType.Goal && string.Equals(playByPlayEvent.PlayerSteamId, goalInfo.Scorer, StringComparison.Ordinal) && (num == 0 || playByPlayEvent.PlayerTeam == num))
			{
				return GetOpposingGoalieSteamIdForEvent(playByPlayEvent);
			}
		}
		return "";
	}

	private static string GetResolvedShotOutcome(PlayByPlayEvent shotEvent, HashSet<int> officialGoalShotEventIds, HashSet<int> ownGoalShotEventIds)
	{
		if (shotEvent == null || shotEvent.EventType != PlayByPlayEventType.Shot)
		{
			return NormalizeShotOutcome(shotEvent?.Outcome);
		}
		if (officialGoalShotEventIds != null && officialGoalShotEventIds.Contains(shotEvent.EventId))
		{
			return "goal";
		}
		string text = NormalizeShotOutcome(shotEvent.Outcome);
		if ((ownGoalShotEventIds != null && ownGoalShotEventIds.Contains(shotEvent.EventId)) || string.Equals(text, "goal", StringComparison.OrdinalIgnoreCase))
		{
			return "on net";
		}
		return text;
	}

	private static ResolvedPlayByPlaySummary BuildResolvedPlayByPlaySummary(List<PlayByPlayEvent> orderedEvents, bool applyResolvedShotOutcomes = false)
	{
		if (!applyResolvedShotOutcomes)
		{
			CachedPlayByPlayAnalytics cachedPlayByPlayAnalytics = _cachedPlayByPlayAnalytics;
			if (cachedPlayByPlayAnalytics != null && cachedPlayByPlayAnalytics.Fingerprint == _analyticsRevision && ReferenceEquals(cachedPlayByPlayAnalytics.OrderedEvents, orderedEvents))
			{
				return cachedPlayByPlayAnalytics.ResolvedSummary;
			}
		}
		ResolvedPlayByPlaySummary resolvedPlayByPlaySummary = new ResolvedPlayByPlaySummary();
		foreach (GoalInfo item in _goals.OrderBy((GoalInfo g) => g.Period).ThenBy((GoalInfo g) => g.GameTime))
		{
			AddCount(resolvedPlayByPlaySummary.GoalsBySteamId, item.Scorer);
		}
		if (orderedEvents == null || orderedEvents.Count == 0)
		{
			return resolvedPlayByPlaySummary;
		}
		HashSet<int> hashSet = new HashSet<int>();
		foreach (GoalInfo item2 in _goals.OrderBy((GoalInfo g) => g.Period).ThenBy((GoalInfo g) => g.GameTime))
		{
			int num = FindBestShotIndexForOfficialGoal(orderedEvents, item2, hashSet);
			if (num >= 0)
			{
				hashSet.Add(num);
				resolvedPlayByPlaySummary.OfficialGoalShotEventIds.Add(orderedEvents[num].EventId);
				continue;
			}
			AddCount(resolvedPlayByPlaySummary.SogBySteamId, item2.Scorer);
			AddGoalieSummaryValue(resolvedPlayByPlaySummary.GoalieSummary, FindOpposingGoalieSteamIdForOfficialGoalEvent(orderedEvents, item2), 1, 1);
		}
		foreach (var item3 in GetOfficialOwnGoalEvents(orderedEvents))
		{
			int num2 = FindBestShotIndexForOwnGoalEvent(orderedEvents, item3.Index, hashSet);
			bool flag = num2 >= 0;
			if (flag)
			{
				hashSet.Add(num2);
				resolvedPlayByPlaySummary.OwnGoalShotEventIds.Add(orderedEvents[num2].EventId);
			}
			AddGoalieSummaryValue(resolvedPlayByPlaySummary.GoalieSummary, GetTeamGoalieSteamIdForEvent(item3.Event), flag ? 0 : 1, 1);
		}
		foreach (PlayByPlayEvent orderedEvent in orderedEvents)
		{
			if (orderedEvent == null || orderedEvent.EventType != PlayByPlayEventType.Shot)
			{
				continue;
			}
			string resolvedShotOutcome = GetResolvedShotOutcome(orderedEvent, resolvedPlayByPlaySummary.OfficialGoalShotEventIds, resolvedPlayByPlaySummary.OwnGoalShotEventIds);
			if (applyResolvedShotOutcomes && !string.Equals(orderedEvent.Outcome, resolvedShotOutcome, StringComparison.OrdinalIgnoreCase))
			{
				orderedEvent.Outcome = resolvedShotOutcome;
			}
			if (!string.Equals(resolvedShotOutcome, "on net", StringComparison.OrdinalIgnoreCase) && !string.Equals(resolvedShotOutcome, "goal", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			AddCount(resolvedPlayByPlaySummary.SogBySteamId, orderedEvent.PlayerSteamId);
			string opposingGoalieSteamIdForShot = GetOpposingGoalieSteamIdForShot(orderedEvent);
			if (string.Equals(resolvedShotOutcome, "goal", StringComparison.OrdinalIgnoreCase))
			{
				AddGoalieSummaryValue(resolvedPlayByPlaySummary.GoalieSummary, opposingGoalieSteamIdForShot, 1, 1);
			}
			else
			{
				AddGoalieSummaryValue(resolvedPlayByPlaySummary.GoalieSummary, opposingGoalieSteamIdForShot, 1, 0);
			}
		}
		return resolvedPlayByPlaySummary;
	}

	private static Dictionary<string, (int ShotsFaced, int GoalsAllowed)> BuildGoalieShotSummary(List<PlayByPlayEvent> orderedEvents)
	{
		return BuildResolvedPlayByPlaySummary(orderedEvents).GoalieSummary;
	}

	private static void AddExpectedGoalsValue(Dictionary<string, double> dictionary, string steamId, double value)
	{
		if (dictionary == null || string.IsNullOrEmpty(steamId))
		{
			return;
		}
		if (!dictionary.ContainsKey(steamId))
		{
			dictionary[steamId] = 0.0;
		}
		dictionary[steamId] += value;
	}

	private static string NormalizeShotOutcome(string outcome)
	{
		string text = (outcome ?? "").Trim().ToLowerInvariant();
		if (text == "attempt")
		{
			return "missed";
		}
		return text;
	}

	private static int ParseScoreDiff(string scoreState)
	{
		if (string.IsNullOrWhiteSpace(scoreState))
		{
			return 0;
		}
		string[] array = scoreState.Split('-');
		if (array.Length != 2)
		{
			return 0;
		}
		if (!int.TryParse(array[0], out var result))
		{
			return 0;
		}
		if (!int.TryParse(array[1], out var result2))
		{
			return 0;
		}
		return result - result2;
	}

	private static int ParseCurrentPlayInPossession(string currentPlayInPossession)
	{
		if (string.IsNullOrWhiteSpace(currentPlayInPossession))
		{
			return 0;
		}
		if (!int.TryParse(currentPlayInPossession, out var result))
		{
			return 0;
		}
		return result;
	}

	private static double ClampDouble(double value, double minimum, double maximum)
	{
		if (value < minimum)
		{
			return minimum;
		}
		if (value > maximum)
		{
			return maximum;
		}
		return value;
	}

}
