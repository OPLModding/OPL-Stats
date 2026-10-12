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
	private static void Client_ResetSOG()
	{
		foreach (string item in new List<string>(_sog.Keys))
		{
			_sog[item] = 0;
			if (_sogLabels.TryGetValue(item, out var value))
			{
				value.text = "0";
				Player playerBySteamId = MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayerBySteamId(item);
				if (playerBySteamId != null && (bool)playerBySteamId && PlayerFunc.IsGoalie(playerBySteamId))
				{
					value.text = "0.000";
				}
			}
		}
	}

	private static void Client_ResetSavePerc()
	{
		_savePerc.Clear();
	}

	private static void Client_ResetPasses()
	{
		foreach (string item in new List<string>(_passes.Keys))
		{
			_passes[item] = 0;
		}
	}

	private static void Client_ResetBlocks()
	{
		foreach (string item in new List<string>(_blocks.Keys))
		{
			_blocks[item] = 0;
		}
	}

	private static void Client_ResetHits()
	{
		foreach (string item in new List<string>(_hits.Keys))
		{
			_hits[item] = 0;
		}
	}

	private static void Client_ResetTakeaways()
	{
		foreach (string item in new List<string>(_takeaways.Keys))
		{
			_takeaways[item] = 0;
		}
	}

	private static void Client_ResetTurnovers()
	{
		foreach (string item in new List<string>(_turnovers.Keys))
		{
			_turnovers[item] = 0;
		}
	}

	private static void Client_ResetExits()
	{
		foreach (string item in new List<string>(_exits.Keys))
		{
			_exits[item] = 0;
		}
	}

	private static void Client_ResetEntries()
	{
		foreach (string item in new List<string>(_entries.Keys))
		{
			_entries[item] = 0;
		}
	}

	private static void Client_ResetShotAttempts()
	{
		foreach (string item in new List<string>(_shotAttempts.Keys))
		{
			_shotAttempts[item] = 0;
		}
		foreach (string item2 in new List<string>(_onIceTeamShotAttempts.Keys))
		{
			_onIceTeamShotAttempts[item2] = 0;
		}
	}

	private static void Client_ResetExpectedGoalsFor()
	{
		foreach (string item in new List<string>(_expectedGoalsFor.Keys))
		{
			_expectedGoalsFor[item] = 0.0;
		}
	}

	private static void Client_ResetExpectedGoalsAgainst()
	{
		foreach (string item in new List<string>(_expectedGoalsAgainst.Keys))
		{
			_expectedGoalsAgainst[item] = 0.0;
		}
	}

	private static void Client_ResetHomePlateSogs()
	{
		foreach (string item in new List<string>(_homePlateSogs.Keys))
		{
			_homePlateSogs[item] = 0;
		}
	}

	private static void Client_ResetPuckTouches()
	{
		foreach (string item in new List<string>(_puckTouches.Keys))
		{
			_puckTouches[item] = 0;
		}
	}

	private static void Client_ResetLocalMatchState()
	{
		_playByPlayEvents.Clear();
		_onIceTeamShotAttempts.Clear();
		_timeOnIceSeconds.Clear();
		_timeOnIceByZone.Clear();
		ResetLiveTimeOnIceSamplingState();
		_possessionTimeByZone.Clear();
		_plusMinus.Clear();
		_goals.Clear();
		_postHits.Clear();
		_postHitShotPositions.Clear();
		foreach (int item in new List<int>(_stars.Keys))
		{
			_stars[item] = "";
		}
		SetClientBoxscoreAvailableTabs(CreateBaseBoxscoreTabList(), refreshVisiblePanel: false);
		_boxscoreShootoutRefreshToken++;
		_boxscoreRequestSequence++;
	}

	private static void Client_ResetPossessionTime()
	{
		foreach (string item in new List<string>(_possessionTimeSeconds.Keys))
		{
			_possessionTimeSeconds[item] = 0.0;
		}
		_lastPossessionTouchTime.Clear();
		_lastPossessionUpdateTime.Clear();
		_lastPuckTouchTime.Clear();
		_lastPlayerZone.Clear();
	}

	private static void Client_ResetPuckBattles()
	{
		foreach (string item in new List<string>(_puckBattleWins.Keys))
		{
			_puckBattleWins[item] = 0;
		}
		foreach (string item2 in new List<string>(_puckBattleLosses.Keys))
		{
			_puckBattleLosses[item2] = 0;
		}
	}

	private static void Client_ResetStickSaves()
	{
		_stickSaves.Clear();
	}

	private static void Client_ResetPadSaves()
	{
		_padSaves.Clear();
	}

	private static void Client_ResetBodySaves()
	{
		_bodySaves.Clear();
	}

	private static void Client_ResetHomePlateSaves()
	{
		_homePlateSaves.Clear();
	}

	private static void Client_ResetHomePlateShots()
	{
		_homePlateShots.Clear();
	}

	private static void Client_ResetTeamStats()
	{
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
		if (_clientConfig.LogClientSideStats)
		{
			WriteClientTeamStatsToFile();
		}
	}

	private static void Client_RefreshTooltipsAndLabelsAfterReset()
	{
		try
		{
			ResetClientScoreboardTrackingForVisibleRefresh("RESETALL");
			ApplyScoreboardEnabledState(enable: true, force: true);
			if (_boxscoreVisible)
			{
				_boxscoreSummaryLabel.text = BuildBoxscoreSummary();
				SetBoxscoreLoadingState("Waiting for new game stats...");
				RequestBoxscoreSnapshot(_boxscoreActiveTab);
			}
			foreach (KeyValuePair<string, Label> sogLabel in _sogLabels)
			{
				if (sogLabel.Value != null)
				{
					bool value;
					bool flag = _playerTooltipIsGoalie.TryGetValue(sogLabel.Key, out value) && value;
					sogLabel.Value.text = (flag ? GetGoalieSavePerc(0, 0) : "0");
				}
			}
			Logging.Log($"RESET_ALL refresh done: reset {_sogLabels.Count} player labels and invalidated live scoreboard/boxscore state", _clientConfig);
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error in Client_RefreshTooltipsAndLabelsAfterReset: {arg}", _clientConfig);
		}
	}
}
