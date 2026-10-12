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
	private static void Server_RegisterNamedMessageHandler()
	{
		if (NetworkManager.Singleton == null || NetworkManager.Singleton.CustomMessagingManager == null)
		{
			return;
		}
		CustomMessagingManager customMessagingManager = NetworkManager.Singleton.CustomMessagingManager;
		if (_hasRegisteredWithNamedMessageHandler && ReferenceEquals(_registeredNamedMessageHandlerManager, customMessagingManager) && string.Equals(_registeredNamedMessageHandlerName, "oomtm450_stats_client", StringComparison.Ordinal))
		{
			return;
		}
		try
		{
			if (_registeredNamedMessageHandlerManager != null && !string.IsNullOrEmpty(_registeredNamedMessageHandlerName))
			{
				_registeredNamedMessageHandlerManager.UnregisterNamedMessageHandler(_registeredNamedMessageHandlerName);
			}
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error unregistering previous named message handler {_registeredNamedMessageHandlerName}: {arg}", ServerConfig);
		}
		Logging.Log("RegisterNamedMessageHandler oomtm450_stats_client.", ServerConfig);
		_combinedBatchClients.Clear();
		customMessagingManager.RegisterNamedMessageHandler("oomtm450_stats_client", ReceiveData);
		_hasRegisteredWithNamedMessageHandler = true;
		_registeredNamedMessageHandlerManager = customMessagingManager;
		_registeredNamedMessageHandlerName = "oomtm450_stats_client";
	}

	private static void CheckForRulesetMod()
	{
		if (!_rulesetModEnabled.HasValue || !_rulesetModEnabled.Value)
		{
			_rulesetModEnabled = ModManager.EnabledMods.Any((Mod mod) => mod.Id == "3501446576" || mod.Id == "3500559233");
			Logging.Log($"Ruleset mod is enabled : {_rulesetModEnabled}.", ServerConfig, bypassConfig: true);
		}
	}

	private static bool TryGetConflictingWorkshopStatsModId(out string modId)
	{
		modId = "";
		try
		{
			Mod mod = ModManager.EnabledMods.FirstOrDefault((Mod enabledMod) => enabledMod != null && !string.IsNullOrWhiteSpace(enabledMod.Id) && CONFLICTING_WORKSHOP_MOD_IDS.Contains(enabledMod.Id));
			if (mod == null)
			{
				return false;
			}
			modId = mod.Id;
			return true;
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error checking for conflicting workshop stats mods: {arg}", ServerConfig);
			return false;
		}
	}

    public static void ReceiveData(ulong clientId, FastBufferReader reader)
    {
        var data = NetworkCommunication.GetData(clientId, reader, clientId == 0 ? (Codebase.Configs.IConfig)_clientConfig : ServerConfig);
        _statReceiveDepth++;
        try { ReceiveDataPayload(clientId, data.DataName, data.DataStr); }
        finally
        {
            _statReceiveDepth--;
            if (_statReceiveDepth == 0 && _clientTeamFilesDirty)
            {
                _clientTeamFilesDirty = false;
                WriteClientTeamStatsToFile();
            }
        }
    }

    private static void ReceiveDataPayload(ulong clientId, string text, string text2)
    {
        try
        {
			bool num = clientId == 0;
			bool flag = text2 != null && text2.Length <= 4 && !text2.Contains(';');
			if (num && text != null && text.StartsWith("oomtm450_stats") && text != "oomtm450_statsRESETALL" && text.Length <= "oomtm450_stats".Length + 5 && (text2 == "1" || flag))
			{
				text = "oomtm450_statsRESETALL";
				text2 = "1";
			}
			if (num && !_serverStatsCompatibilityDisabled && !_serverSignatureConfirmed && text != "oomtm450_stats_MOD_VERSION" && text != SERVER_SIGNATURE && text != "oomtm450_statsRESETALL" && text != PREVIOUS_GAME_SNAPSHOT && text != PREVIOUS_GAME_CLEAR && text != NETWORK_PERFORMANCE_GAME && text != NETWORK_PERFORMANCE_CONFIG)
			{
				return;
			}
			switch (text)
			{
			case NETWORK_PERFORMANCE_GAME:
				if (num)
				{
					Event_OnNetworkPerformanceGameReference(new Dictionary<string, object> { { "gameReferenceId", text2 } });
				}
				return;
			case NETWORK_PERFORMANCE_CONFIG:
				if (num)
				{
					Event_OnNetworkPerformanceConfig(new Dictionary<string, object> { { "enabled", text2 } });
				}
				return;
			case NETWORK_PERFORMANCE_REPORT:
				if (!num)
				{
					ReceiveNetworkPerformanceReport(clientId, text2);
				}
				return;
            case STAT_CAPABILITIES:
                if (clientId != 0 && text2 == "combined-v1") _combinedBatchClients.Add(clientId);
                return;
            case COMBINED_STAT_BATCH:
                if (num && _serverSignatureConfirmed && !_serverStatsCompatibilityDisabled)
                {
                    if (!StatBatchCodec.TryDecode(text2, CombinedBatchNames, out var entries))
                    {
                        Logging.LogError("Invalid combined stat batch received", _clientConfig);
                        return;
                    }
                    foreach (var entry in entries) ReceiveDataPayload(clientId, entry.Key, entry.Value);
                }
                return;
			case "oomtm450_stats_MOD_VERSION":
				_serverHasResponded = true;
				if (MOD_VERSION == text2)
				{
					_serverStatsCompatibilityDisabled = false;
					if (_serverSignatureConfirmed)
					{
						_serverStatsCompatibilityDisabledReason = "";
					}
					if (!(GetUIScoreboard() != null))
					{
						if (_boxscoreVisible && _serverSignatureConfirmed)
						{
							RequestBoxscoreSnapshot(_boxscoreActiveTab);
						}
						return;
					}
					VisualElement privateField = GetScoreboardContainer();
					if (_serverSignatureConfirmed && privateField != null && !_hasUpdatedUIScoreboard.Contains("header"))
					{
						ApplyScoreboardEnabledState(enable: true, force: true);
					}
					else if (_serverSignatureConfirmed && privateField != null && _hasUpdatedUIScoreboard.Contains("header") && _teamTooltips.Count == 0)
					{
						SetupTeamTooltips(privateField);
					}
					if (_serverSignatureConfirmed && _hasUpdatedUIScoreboard.Contains("header"))
					{
						privateField?.schedule.Execute((Action)delegate
						{
							ApplyScoreboardEnabledState(enable: true, force: true);
						}).ExecuteLater(100L);
					}
					if (_boxscoreVisible && _serverSignatureConfirmed)
					{
						RequestBoxscoreSnapshot(_boxscoreActiveTab);
					}
				}
				else
				{
					_serverStatsCompatibilityDisabled = true;
					_serverStatsCompatibilityDisabledReason = (OLD_MOD_VERSIONS.Contains(text2) ? $"Detected incompatible legacy stats mod on server ({text2})." : $"Detected incompatible stats mod on server ({text2}).");
					_addServerModVersionOutOfDateMessage = false;
					_askForKick = false;
					ApplyScoreboardEnabledState(enable: false, force: true);
					if (_boxscoreVisible)
					{
						SetBoxscoreLoadingState("Boxscore unavailable: server is running an incompatible stats mod.");
					}
					Logging.Log(_serverStatsCompatibilityDisabledReason, _clientConfig);
				}
				return;
			case SERVER_SIGNATURE:
				if (string.Equals(text2, SERVER_SIGNATURE_TOKEN, StringComparison.Ordinal))
				{
					_serverSignatureConfirmed = true;
                    NetworkCommunication.SendData(STAT_CAPABILITIES, "combined-v1", 0, "oomtm450_stats_client", _clientConfig);
					_serverStatsCompatibilityDisabled = false;
					_serverStatsCompatibilityDisabledReason = "";
					ApplyScoreboardEnabledState(enable: IsClientStatsUiAuthorized(), force: true);
					if (_boxscoreVisible)
					{
						RequestBoxscoreSnapshot(_boxscoreActiveTab);
					}
				}
				else
				{
					_serverSignatureConfirmed = false;
					_serverStatsCompatibilityDisabled = true;
					_serverStatsCompatibilityDisabledReason = "Detected incompatible stats mod on server.";
					ApplyScoreboardEnabledState(enable: false, force: true);
					if (_boxscoreVisible)
					{
						SetBoxscoreLoadingState(_serverStatsCompatibilityDisabledReason);
					}
				}
				return;
			case "oomtm450_stats_kick":
				if (!(text2 != "1"))
				{
					if (!_sentOutOfDateMessage.TryGetValue(clientId, out var value9))
					{
						value9 = DateTime.MinValue;
						_sentOutOfDateMessage.Add(clientId, value9);
					}
					DateTime utcNow = DateTime.UtcNow;
					if (value9 + TimeSpan.FromSeconds(900.0) < utcNow && !string.IsNullOrEmpty(MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayerByClientId(clientId).Username.Value.ToString()))
					{
						Logging.Log($"Warning client {clientId} mod out of date.", ServerConfig);
						SendSystemChatMessage(string.Format("{0}: {1} Mod is out of date. Please resubscribe from Stats Tooltip UI <a href=\"{2}\">[Link]</a> in the workshop and restart your game to update.", MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayerByClientId(clientId).Username.Value, "Stats", "https://steamcommunity.com/sharedfiles/filedetails/?id=3608968085"));
						_sentOutOfDateMessage[clientId] = utcNow;
					}
				}
				return;
			case "oomtm450_statsASKDATA":
				if (text2 != "1")
				{
					return;
				}
				NetworkCommunication.SendData("oomtm450_stats_MOD_VERSION", MOD_VERSION, clientId, "oomtm450_stats_server", ServerConfig);
				NetworkCommunication.SendData(SERVER_SIGNATURE, SERVER_SIGNATURE_TOKEN, clientId, "oomtm450_stats_server", ServerConfig);
				if (_sog.Count != 0)
				{
					string text3 = "";
					foreach (string item in new List<string>(_sog.Keys))
					{
						text3 = text3 + item + ";" + _sog[item] + ";";
					}
					text3 = text3.Remove(text3.Length - 1);
					NetworkCommunication.SendData("oomtm450_statsBATCHSOG", text3, clientId, "oomtm450_stats_server", ServerConfig);
				}
				if (_savePerc.Count != 0)
				{
					string text4 = "";
					foreach (string item2 in new List<string>(_savePerc.Keys))
					{
						text4 = text4 + item2 + ";" + _savePerc[item2].ToString() + ";";
					}
					text4 = text4.Remove(text4.Length - 1);
					NetworkCommunication.SendData("oomtm450_statsBATCHSAVEPERC", text4, clientId, "oomtm450_stats_server", ServerConfig);
				}
				if (_hits.Count != 0)
				{
					string text5 = "";
					foreach (string item3 in new List<string>(_hits.Keys))
					{
						text5 = text5 + item3 + ";" + _hits[item3] + ";";
					}
					text5 = text5.Remove(text5.Length - 1);
					NetworkCommunication.SendData("oomtm450_statsBATCHHIT", text5, clientId, "oomtm450_stats_server", ServerConfig);
				}
				if (_turnovers.Count != 0)
				{
					string text6 = "";
					foreach (string item4 in new List<string>(_turnovers.Keys))
					{
						text6 = text6 + item4 + ";" + _turnovers[item4] + ";";
					}
					text6 = text6.Remove(text6.Length - 1);
					NetworkCommunication.SendData("oomtm450_statsBATCHTURNOVER", text6, clientId, "oomtm450_stats_server", ServerConfig);
				}
				if (_takeaways.Count != 0)
				{
					string text7 = "";
					foreach (string item5 in new List<string>(_takeaways.Keys))
					{
						text7 = text7 + item5 + ";" + _takeaways[item5] + ";";
					}
					text7 = text7.Remove(text7.Length - 1);
					NetworkCommunication.SendData("oomtm450_statsBATCHTAKEAWAY", text7, clientId, "oomtm450_stats_server", ServerConfig);
				}
				if (_passes.Count != 0)
				{
					string text8 = "";
					foreach (string item6 in new List<string>(_passes.Keys))
					{
						text8 = text8 + item6 + ";" + _passes[item6] + ";";
					}
					text8 = text8.Remove(text8.Length - 1);
					NetworkCommunication.SendData("oomtm450_statsBATCHPASS", text8, clientId, "oomtm450_stats_server", ServerConfig);
				}
				if (_puckTouches.Count != 0)
				{
					string text9 = "";
					foreach (string item7 in new List<string>(_puckTouches.Keys))
					{
						text9 = text9 + item7 + ";" + _puckTouches[item7] + ";";
					}
					text9 = text9.Remove(text9.Length - 1);
					NetworkCommunication.SendData("oomtm450_statsBATCHPUCKTOUCH", text9, clientId, "oomtm450_stats_server", ServerConfig);
				}
				if (_exits.Count != 0)
				{
					string text10 = "";
					foreach (string item8 in new List<string>(_exits.Keys))
					{
						text10 = text10 + item8 + ";" + _exits[item8] + ";";
					}
					text10 = text10.Remove(text10.Length - 1);
					NetworkCommunication.SendData("oomtm450_statsBATCHEXIT", text10, clientId, "oomtm450_stats_server", ServerConfig);
				}
				if (_entries.Count != 0)
				{
					string text11 = "";
					foreach (string item9 in new List<string>(_entries.Keys))
					{
						text11 = text11 + item9 + ";" + _entries[item9] + ";";
					}
					text11 = text11.Remove(text11.Length - 1);
					NetworkCommunication.SendData("oomtm450_statsBATCHENTRY", text11, clientId, "oomtm450_stats_server", ServerConfig);
				}
				if (_possessionTimeSeconds.Count != 0)
				{
					string text12 = "";
					foreach (string item10 in new List<string>(_possessionTimeSeconds.Keys))
					{
						text12 = text12 + item10 + ";" + _possessionTimeSeconds[item10].ToString("F2") + ";";
					}
					text12 = text12.Remove(text12.Length - 1);
					NetworkCommunication.SendData("oomtm450_statsBATCHPOSSESSIONTIME", text12, clientId, "oomtm450_stats_server", ServerConfig);
				}
				if (_puckBattleWins.Count != 0)
				{
					string text13 = "";
					foreach (string item11 in new List<string>(_puckBattleWins.Keys))
					{
						text13 = text13 + item11 + ";" + _puckBattleWins[item11] + ";";
					}
					text13 = text13.Remove(text13.Length - 1);
					NetworkCommunication.SendData("oomtm450_statsBATCHPUCKBATTLEWINS", text13, clientId, "oomtm450_stats_server", ServerConfig);
				}
				if (_puckBattleLosses.Count != 0)
				{
					string text14 = "";
					foreach (string item12 in new List<string>(_puckBattleLosses.Keys))
					{
						text14 = text14 + item12 + ";" + _puckBattleLosses[item12] + ";";
					}
					text14 = text14.Remove(text14.Length - 1);
					NetworkCommunication.SendData("oomtm450_statsBATCHPUCKBATTLELOSSES", text14, clientId, "oomtm450_stats_server", ServerConfig);
				}
				if (_shotAttempts.Count != 0)
				{
					string text15 = "";
					foreach (string item13 in new List<string>(_shotAttempts.Keys))
					{
						text15 = text15 + item13 + ";" + _shotAttempts[item13] + ";";
					}
					text15 = text15.Remove(text15.Length - 1);
					NetworkCommunication.SendData("oomtm450_statsBATCHSHOTATTEMPTS", text15, clientId, "oomtm450_stats_server", ServerConfig);
				}
				if (_onIceTeamShotAttempts.Count != 0)
				{
					string text15b = "";
					foreach (string item13b in new List<string>(_onIceTeamShotAttempts.Keys))
					{
						text15b = text15b + item13b + ";" + _onIceTeamShotAttempts[item13b] + ";";
					}
					text15b = text15b.Remove(text15b.Length - 1);
					NetworkCommunication.SendData("oomtm450_statsBATCHONICETEAMSHOTATTEMPTS", text15b, clientId, "oomtm450_stats_server", ServerConfig);
				}
				if (_expectedGoalsFor.Count != 0)
				{
					string expectedGoalsForBatch = "";
					foreach (string item14 in new List<string>(_expectedGoalsFor.Keys))
					{
						expectedGoalsForBatch = expectedGoalsForBatch + item14 + ";" + _expectedGoalsFor[item14].ToString("0.####", CultureInfo.InvariantCulture) + ";";
					}
					expectedGoalsForBatch = expectedGoalsForBatch.Remove(expectedGoalsForBatch.Length - 1);
					NetworkCommunication.SendData("oomtm450_statsBATCHEXPECTEDGOALS", expectedGoalsForBatch, clientId, "oomtm450_stats_server", ServerConfig);
				}
				if (_expectedGoalsAgainst.Count != 0)
				{
					string expectedGoalsAgainstBatch = "";
					foreach (string item16 in new List<string>(_expectedGoalsAgainst.Keys))
					{
						expectedGoalsAgainstBatch = expectedGoalsAgainstBatch + item16 + ";" + _expectedGoalsAgainst[item16].ToString("0.####", CultureInfo.InvariantCulture) + ";";
					}
					expectedGoalsAgainstBatch = expectedGoalsAgainstBatch.Remove(expectedGoalsAgainstBatch.Length - 1);
					NetworkCommunication.SendData("oomtm450_statsBATCHEXPECTEDGOALSAGAINST", expectedGoalsAgainstBatch, clientId, "oomtm450_stats_server", ServerConfig);
				}
				if (_homePlateSogs.Count != 0)
				{
					string text16 = "";
					foreach (string item18 in new List<string>(_homePlateSogs.Keys))
					{
						text16 = text16 + item18 + ";" + _homePlateSogs[item18] + ";";
					}
					text16 = text16.Remove(text16.Length - 1);
					NetworkCommunication.SendData("oomtm450_statsBATCHHOMEPLATESOGS", text16, clientId, "oomtm450_stats_server", ServerConfig);
				}
				if (_teamShots.Count != 0)
				{
					string text17 = "";
					foreach (PlayerTeam item17 in new List<PlayerTeam>(_teamShots.Keys))
					{
						text17 = text17 + item17.ToString() + ";" + _teamShots[item17] + ";";
					}
					text17 = text17.Remove(text17.Length - 1);
					NetworkCommunication.SendData("oomtm450_statsBATCHTEAMSHOTS", text17, clientId, "oomtm450_stats_server", ServerConfig);
				}
				if (_teamShotAttempts.Count != 0)
				{
					string text18 = "";
					foreach (PlayerTeam item18 in new List<PlayerTeam>(_teamShotAttempts.Keys))
					{
						text18 = text18 + item18.ToString() + ";" + _teamShotAttempts[item18] + ";";
					}
					text18 = text18.Remove(text18.Length - 1);
					NetworkCommunication.SendData("oomtm450_statsBATCHTEAMSHOTATTEMPTS", text18, clientId, "oomtm450_stats_server", ServerConfig);
				}
				if (_teamHomePlateSogs.Count != 0)
				{
					string text19 = "";
					foreach (PlayerTeam item19 in new List<PlayerTeam>(_teamHomePlateSogs.Keys))
					{
						text19 = text19 + item19.ToString() + ";" + _teamHomePlateSogs[item19] + ";";
					}
					text19 = text19.Remove(text19.Length - 1);
					NetworkCommunication.SendData("oomtm450_statsBATCHTEAMHOMEPLATESOGS", text19, clientId, "oomtm450_stats_server", ServerConfig);
				}
				if (_teamFaceoffWins.Count != 0)
				{
					string text20 = "";
					foreach (PlayerTeam item18 in new List<PlayerTeam>(_teamFaceoffWins.Keys))
					{
						text20 = text20 + item18.ToString() + ";" + _teamFaceoffWins[item18] + ";";
					}
					text20 = text20.Remove(text20.Length - 1);
					NetworkCommunication.SendData("oomtm450_statsBATCHTEAMFACEOFFWINS", text20, clientId, "oomtm450_stats_server", ServerConfig);
				}
				if (_teamFaceoffTotal.Count != 0)
				{
					string text21 = "";
					foreach (PlayerTeam item19 in new List<PlayerTeam>(_teamFaceoffTotal.Keys))
					{
						text21 = text21 + item19.ToString() + ";" + _teamFaceoffTotal[item19] + ";";
					}
					text21 = text21.Remove(text21.Length - 1);
					NetworkCommunication.SendData("oomtm450_statsBATCHTEAMFACEOFFTOTAL", text21, clientId, "oomtm450_stats_server", ServerConfig);
				}
				if (_teamTakeaways.Count != 0)
				{
					string text22 = "";
					foreach (PlayerTeam item20 in new List<PlayerTeam>(_teamTakeaways.Keys))
					{
						text22 = text22 + item20.ToString() + ";" + _teamTakeaways[item20] + ";";
					}
					text22 = text22.Remove(text22.Length - 1);
					NetworkCommunication.SendData("oomtm450_statsBATCHTEAMTAKEAWAYS", text22, clientId, "oomtm450_stats_server", ServerConfig);
				}
				if (_teamTurnovers.Count != 0)
				{
					string text23 = "";
					foreach (PlayerTeam item21 in new List<PlayerTeam>(_teamTurnovers.Keys))
					{
						text23 = text23 + item21.ToString() + ";" + _teamTurnovers[item21] + ";";
					}
					text23 = text23.Remove(text23.Length - 1);
					NetworkCommunication.SendData("oomtm450_statsBATCHTEAMTURNOVERS", text23, clientId, "oomtm450_stats_server", ServerConfig);
				}
				if (_teamExits.Count != 0)
				{
					string text24 = "";
					foreach (PlayerTeam item22 in new List<PlayerTeam>(_teamExits.Keys))
					{
						text24 = text24 + item22.ToString() + ";" + _teamExits[item22] + ";";
					}
					text24 = text24.Remove(text24.Length - 1);
					NetworkCommunication.SendData("oomtm450_statsBATCHTEAMEXITS", text24, clientId, "oomtm450_stats_server", ServerConfig);
				}
				if (_teamEntries.Count != 0)
				{
					string text25 = "";
					foreach (PlayerTeam item23 in new List<PlayerTeam>(_teamEntries.Keys))
					{
						text25 = text25 + item23.ToString() + ";" + _teamEntries[item23] + ";";
					}
					text25 = text25.Remove(text25.Length - 1);
					NetworkCommunication.SendData("oomtm450_statsBATCHTEAMENTRIES", text25, clientId, "oomtm450_stats_server", ServerConfig);
				}
				if (_teamPasses.Count != 0)
				{
					string text26 = "";
					foreach (PlayerTeam item24 in new List<PlayerTeam>(_teamPasses.Keys))
					{
						text26 = text26 + item24.ToString() + ";" + _teamPasses[item24] + ";";
					}
					text26 = text26.Remove(text26.Length - 1);
					NetworkCommunication.SendData("oomtm450_statsBATCHTEAMPASSES", text26, clientId, "oomtm450_stats_server", ServerConfig);
				}
				if (_teamPossessionTime.Count != 0)
				{
					string text27 = "";
					foreach (PlayerTeam item25 in new List<PlayerTeam>(_teamPossessionTime.Keys))
					{
						text27 = text27 + item25.ToString() + ";" + _teamPossessionTime[item25].ToString("F2") + ";";
					}
					text27 = text27.Remove(text27.Length - 1);
					NetworkCommunication.SendData("oomtm450_statsBATCHTEAMPOSSESSIONTIME", text27, clientId, "oomtm450_stats_server", ServerConfig);
				}
				if (_teamPuckBattleWins.Count != 0)
				{
					string text28 = "";
					foreach (PlayerTeam item26 in new List<PlayerTeam>(_teamPuckBattleWins.Keys))
					{
						text28 = text28 + item26.ToString() + ";" + _teamPuckBattleWins[item26] + ";";
					}
					text28 = text28.Remove(text28.Length - 1);
					NetworkCommunication.SendData("oomtm450_statsBATCHTEAMPUCKBATTLEWINS", text28, clientId, "oomtm450_stats_server", ServerConfig);
				}
				if (_teamPuckBattleLosses.Count != 0)
				{
					string text29 = "";
					foreach (PlayerTeam item27 in new List<PlayerTeam>(_teamPuckBattleLosses.Keys))
					{
						text29 = text29 + item27.ToString() + ";" + _teamPuckBattleLosses[item27] + ";";
					}
					text29 = text29.Remove(text29.Length - 1);
					NetworkCommunication.SendData("oomtm450_statsBATCHTEAMPUCKBATTLELOSSES", text29, clientId, "oomtm450_stats_server", ServerConfig);
				}
				if (_stickSaves.Count != 0)
				{
					string text30 = "";
					foreach (string item28 in new List<string>(_stickSaves.Keys))
					{
						text30 = text30 + item28 + ";" + _stickSaves[item28] + ";";
					}
					text30 = text30.Remove(text30.Length - 1);
					NetworkCommunication.SendData("oomtm450_statsBATCHSTICKSAVES", text30, clientId, "oomtm450_stats_server", ServerConfig);
				}
				if (_padSaves.Count != 0)
				{
					string text30b = "";
					foreach (string item28b in new List<string>(_padSaves.Keys))
					{
						text30b = text30b + item28b + ";" + _padSaves[item28b] + ";";
					}
					text30b = text30b.Remove(text30b.Length - 1);
					NetworkCommunication.SendData("oomtm450_statsBATCHPADSAVES", text30b, clientId, "oomtm450_stats_server", ServerConfig);
				}
				if (_bodySaves.Count != 0)
				{
					string text31 = "";
					foreach (string item29 in new List<string>(_bodySaves.Keys))
					{
						text31 = text31 + item29 + ";" + _bodySaves[item29] + ";";
					}
					text31 = text31.Remove(text31.Length - 1);
					NetworkCommunication.SendData("oomtm450_statsBATCHBODYSAVES", text31, clientId, "oomtm450_stats_server", ServerConfig);
				}
				if (_homePlateSaves.Count != 0)
				{
					string text32 = "";
					foreach (string item30 in new List<string>(_homePlateSaves.Keys))
					{
						text32 = text32 + item30 + ";" + _homePlateSaves[item30] + ";";
					}
					text32 = text32.Remove(text32.Length - 1);
					NetworkCommunication.SendData("oomtm450_statsBATCHHOMEPLATESAVES", text32, clientId, "oomtm450_stats_server", ServerConfig);
				}
				if (_homePlateShots.Count != 0)
				{
					string text33 = "";
					foreach (string item31 in new List<string>(_homePlateShots.Keys))
					{
						text33 = text33 + item31 + ";" + _homePlateShots[item31] + ";";
					}
					text33 = text33.Remove(text33.Length - 1);
					NetworkCommunication.SendData("oomtm450_statsBATCHHOMEPLATESHOTSFACED", text33, clientId, "oomtm450_stats_server", ServerConfig);
				}
				{
					foreach (int item32 in new List<int>(_stars.Keys))
					{
						NetworkCommunication.SendData("oomtm450_statsSTAR", $"{_stars[item32]};{item32}", clientId, "oomtm450_stats_server", ServerConfig);
					}
					SendPreviousGameSnapshotToClient(clientId);
					return;
				}
			case "oomtm450_statsBOXSCORE_REQUEST":
				if (clientId != 0L)
				{
					SendRequestedBoxscoreSnapshot(clientId, text2);
				}
				return;
			case PREVIOUS_GAME_SNAPSHOT:
				if (num)
				{
					ReceivePreviousGameSnapshot(text2);
				}
				return;
			case PREVIOUS_GAME_CLEAR:
				if (num && text2 == "1")
				{
					ClearClientPreviousGameSnapshot(refreshVisibleBoxscore: true);
				}
				return;
			case "oomtm450_statsBOXSCORE_LEADERS":
			case PREVIOUS_GAME_BOXSCORE_LEADERS:
				if (num)
				{
					ReceiveBoxscoreLeadersSnapshot(text2);
				}
				return;
			case "oomtm450_statsBOXSCORE_GAMESTATS":
			case PREVIOUS_GAME_BOXSCORE_GAMESTATS:
				if (num)
				{
					ReceiveBoxscoreGameStatsSnapshot(text2);
				}
				return;
			case "oomtm450_statsBOXSCORE_PLAYERSTATS":
			case PREVIOUS_GAME_BOXSCORE_PLAYERSTATS:
				if (num)
				{
					ReceiveBoxscorePlayerStatsSnapshot(text2);
				}
				return;
			case BOXSCORE_SHOOTOUT:
			case PREVIOUS_GAME_BOXSCORE_SHOOTOUT:
				if (num)
				{
					ReceiveBoxscoreShootoutSnapshot(text2);
				}
				return;
			case "oomtm450_statsRESETALL":
				if (!(text2 != "1"))
				{
					Client_ResetLocalMatchState();
					Client_ResetSOG();
					Client_ResetSavePerc();
					Client_ResetPasses();
					Client_ResetBlocks();
					Client_ResetHits();
					Client_ResetTakeaways();
					Client_ResetTurnovers();
					Client_ResetExits();
					Client_ResetEntries();
					Client_ResetShotAttempts();
					Client_ResetExpectedGoalsFor();
					Client_ResetExpectedGoalsAgainst();
					Client_ResetHomePlateSogs();
					Client_ResetPuckTouches();
				Client_ResetPossessionTime();
				Client_ResetPuckBattles();
				Client_ResetStickSaves();
				Client_ResetPadSaves();
				Client_ResetBodySaves();
				Client_ResetHomePlateSaves();
					Client_ResetHomePlateShots();
					Client_ResetTeamStats();
					Client_RefreshTooltipsAndLabelsAfterReset();
				}
				return;
			case "oomtm450_statsBATCHSOG":
			{
				string[] array30 = text2.Split(';');
				string playerSteamId17 = "";
				for (int num25 = 0; num25 < array30.Length; num25++)
				{
					if (num25 % 2 == 0)
					{
						playerSteamId17 = array30[num25];
					}
					else
					{
						ReceiveData_SOG(playerSteamId17, array30[num25]);
					}
				}
				return;
			}
			case "oomtm450_statsBATCHSAVEPERC":
			{
				string[] array24 = text2.Split(';');
				string playerSteamId13 = "";
				for (int num19 = 0; num19 < array24.Length; num19++)
				{
					if (num19 % 2 == 0)
					{
						playerSteamId13 = array24[num19];
					}
					else
					{
						ReceiveData_SavePerc(playerSteamId13, array24[num19]);
					}
				}
				return;
			}
			case "oomtm450_statsSTAR":
			{
				string[] array14 = text2.Split(';');
				string playerSteamId9 = "";
				for (int num9 = 0; num9 < array14.Length; num9++)
				{
					if (num9 % 2 == 0)
					{
						playerSteamId9 = array14[num9];
					}
					else
					{
						ReceiveData_Star(playerSteamId9, array14[num9]);
					}
				}
				return;
			}
			case "oomtm450_statsBATCHBLOCK":
			{
				string[] array7 = text2.Split(';');
				string playerSteamId5 = "";
				for (int num2 = 0; num2 < array7.Length; num2++)
				{
					if (num2 % 2 == 0)
					{
						playerSteamId5 = array7[num2];
					}
					else
					{
						ReceiveData_Block(playerSteamId5, array7[num2]);
					}
				}
				return;
			}
			case "oomtm450_statsBATCHHIT":
			{
				string[] array2 = text2.Split(';');
				string playerSteamId2 = "";
				for (int j = 0; j < array2.Length; j++)
				{
					if (j % 2 == 0)
					{
						playerSteamId2 = array2[j];
					}
					else
					{
						ReceiveData_Hit(playerSteamId2, array2[j]);
					}
				}
				return;
			}
			case "oomtm450_statsBATCHTURNOVER":
			{
				string[] array4 = text2.Split(';');
				string playerSteamId4 = "";
				for (int l = 0; l < array4.Length; l++)
				{
					if (l % 2 == 0)
					{
						playerSteamId4 = array4[l];
					}
					else
					{
						ReceiveData_Turnover(playerSteamId4, array4[l]);
					}
				}
				return;
			}
			case "oomtm450_statsBATCHTAKEAWAY":
			{
				string[] array25 = text2.Split(';');
				string playerSteamId14 = "";
				for (int num20 = 0; num20 < array25.Length; num20++)
				{
					if (num20 % 2 == 0)
					{
						playerSteamId14 = array25[num20];
					}
					else
					{
						ReceiveData_Takeaway(playerSteamId14, array25[num20]);
					}
				}
				return;
			}
			case "oomtm450_statsBATCHPASS":
			{
				string[] array20 = text2.Split(';');
				string playerSteamId12 = "";
				for (int num15 = 0; num15 < array20.Length; num15++)
				{
					if (num15 % 2 == 0)
					{
						playerSteamId12 = array20[num15];
					}
					else
					{
						ReceiveData_Pass(playerSteamId12, array20[num15]);
					}
				}
				return;
			}
			case "oomtm450_statsBATCHPUCKTOUCH":
			{
				string[] array19 = text2.Split(';');
				string playerSteamId11 = "";
				for (int num14 = 0; num14 < array19.Length; num14++)
				{
					if (num14 % 2 == 0)
					{
						playerSteamId11 = array19[num14];
					}
					else
					{
						ReceiveData_PuckTouch(playerSteamId11, array19[num14]);
					}
				}
				return;
			}
			case "oomtm450_statsBATCHEXIT":
			{
				string[] array15 = text2.Split(';');
				string playerSteamId10 = "";
				for (int num10 = 0; num10 < array15.Length; num10++)
				{
					if (num10 % 2 == 0)
					{
						playerSteamId10 = array15[num10];
					}
					else
					{
						ReceiveData_Exit(playerSteamId10, array15[num10]);
					}
				}
				return;
			}
			case "oomtm450_statsBATCHENTRY":
			{
				string[] array10 = text2.Split(';');
				string playerSteamId8 = "";
				for (int num5 = 0; num5 < array10.Length; num5++)
				{
					if (num5 % 2 == 0)
					{
						playerSteamId8 = array10[num5];
					}
					else
					{
						ReceiveData_Entry(playerSteamId8, array10[num5]);
					}
				}
				return;
			}
			case "oomtm450_statsBATCHPOSSESSIONTIME":
			{
				string[] array9 = text2.Split(';');
				string playerSteamId7 = "";
				for (int num4 = 0; num4 < array9.Length; num4++)
				{
					if (num4 % 2 == 0)
					{
						playerSteamId7 = array9[num4];
					}
					else
					{
						ReceiveData_PossessionTime(playerSteamId7, array9[num4]);
					}
				}
				return;
			}
			case "oomtm450_statsBATCHPUCKBATTLEWINS":
			{
				string[] array = text2.Split(';');
				string playerSteamId = "";
				for (int i = 0; i < array.Length; i++)
				{
					if (i % 2 == 0)
					{
						playerSteamId = array[i];
					}
					else
					{
						ReceiveData_PuckBattleWins(playerSteamId, array[i]);
					}
				}
				return;
			}
			case "oomtm450_statsBATCHPUCKBATTLELOSSES":
			{
				string[] array32 = text2.Split(';');
				string playerSteamId19 = "";
				for (int num27 = 0; num27 < array32.Length; num27++)
				{
					if (num27 % 2 == 0)
					{
						playerSteamId19 = array32[num27];
					}
					else
					{
						ReceiveData_PuckBattleLosses(playerSteamId19, array32[num27]);
					}
				}
				return;
			}
			case "oomtm450_statsBATCHSHOTATTEMPTS":
			{
				string[] array31 = text2.Split(';');
				string playerSteamId18 = "";
				for (int num26 = 0; num26 < array31.Length; num26++)
				{
					if (num26 % 2 == 0)
					{
						playerSteamId18 = array31[num26];
					}
					else
					{
						ReceiveData_ShotAttempts(playerSteamId18, array31[num26]);
					}
				}
				return;
			}
			case "oomtm450_statsBATCHONICETEAMSHOTATTEMPTS":
			{
				string[] array31b = text2.Split(';');
				string playerSteamId18b = "";
				for (int num26b = 0; num26b < array31b.Length; num26b++)
				{
					if (num26b % 2 == 0)
					{
						playerSteamId18b = array31b[num26b];
					}
					else
					{
						ReceiveData_OnIceTeamShotAttempts(playerSteamId18b, array31b[num26b]);
					}
				}
				return;
			}
			case "oomtm450_statsBATCHEXPECTEDGOALS":
			{
				string[] array33 = text2.Split(';');
				string playerSteamId20 = "";
				for (int num28 = 0; num28 < array33.Length; num28++)
				{
					if (num28 % 2 == 0)
					{
						playerSteamId20 = array33[num28];
					}
					else
					{
						ReceiveData_ExpectedGoalsFor(playerSteamId20, array33[num28]);
					}
				}
				return;
			}
			case "oomtm450_statsBATCHEXPECTEDGOALSAGAINST":
			{
				string[] array35 = text2.Split(';');
				string playerSteamId22 = "";
				for (int num30 = 0; num30 < array35.Length; num30++)
				{
					if (num30 % 2 == 0)
					{
						playerSteamId22 = array35[num30];
					}
					else
					{
						ReceiveData_ExpectedGoalsAgainst(playerSteamId22, array35[num30]);
					}
				}
				return;
			}
			case "oomtm450_statsBATCHTEAMSHOTS":
			{
				string[] array26 = text2.Split(';');
				string value13 = "";
				for (int num21 = 0; num21 < array26.Length; num21++)
				{
					PlayerTeam result12;
					if (num21 % 2 == 0)
					{
						value13 = array26[num21];
					}
					else if (Enum.TryParse<PlayerTeam>(value13, out result12))
					{
						ReceiveData_TeamShots(result12, array26[num21]);
					}
				}
				return;
			}
			case "oomtm450_statsBATCHTEAMSHOTATTEMPTS":
			{
				string[] array27 = text2.Split(';');
				string value14 = "";
				for (int num22 = 0; num22 < array27.Length; num22++)
				{
					PlayerTeam result13;
					if (num22 % 2 == 0)
					{
						value14 = array27[num22];
					}
					else if (Enum.TryParse<PlayerTeam>(value14, out result13))
					{
						ReceiveData_TeamShotAttempts(result13, array27[num22]);
					}
				}
				return;
			}
			case "oomtm450_statsBATCHHOMEPLATESOGS":
			{
				string[] array28 = text2.Split(';');
				string playerSteamId15 = "";
				for (int num23 = 0; num23 < array28.Length; num23++)
				{
					if (num23 % 2 == 0)
					{
						playerSteamId15 = array28[num23];
					}
					else
					{
						ReceiveData_HomePlateSogs(playerSteamId15, array28[num23]);
					}
				}
				return;
			}
			case "oomtm450_statsBATCHTEAMHOMEPLATESOGS":
			{
				string[] array23 = text2.Split(';');
				string value12 = "";
				for (int num18 = 0; num18 < array23.Length; num18++)
				{
					PlayerTeam result11;
					if (num18 % 2 == 0)
					{
						value12 = array23[num18];
					}
					else if (Enum.TryParse<PlayerTeam>(value12, out result11))
					{
						ReceiveData_TeamHomePlateSogs(result11, array23[num18]);
					}
				}
				return;
			}
			case "oomtm450_statsBATCHTEAMPASSES":
			{
				string[] array22 = text2.Split(';');
				string value11 = "";
				for (int num17 = 0; num17 < array22.Length; num17++)
				{
					PlayerTeam result10;
					if (num17 % 2 == 0)
					{
						value11 = array22[num17];
					}
					else if (Enum.TryParse<PlayerTeam>(value11, out result10))
					{
						ReceiveData_TeamPasses(result10, array22[num17]);
					}
				}
				return;
			}
			case "oomtm450_statsBATCHTEAMFACEOFFWINS":
			{
				string[] array17 = text2.Split(';');
				string value7 = "";
				for (int num12 = 0; num12 < array17.Length; num12++)
				{
					PlayerTeam result7;
					if (num12 % 2 == 0)
					{
						value7 = array17[num12];
					}
					else if (Enum.TryParse<PlayerTeam>(value7, out result7))
					{
						ReceiveData_TeamFaceoffWins(result7, array17[num12]);
					}
				}
				return;
			}
			case "oomtm450_statsBATCHTEAMFACEOFFTOTAL":
			{
				string[] array21 = text2.Split(';');
				string value10 = "";
				for (int num16 = 0; num16 < array21.Length; num16++)
				{
					PlayerTeam result9;
					if (num16 % 2 == 0)
					{
						value10 = array21[num16];
					}
					else if (Enum.TryParse<PlayerTeam>(value10, out result9))
					{
						ReceiveData_TeamFaceoffTotal(result9, array21[num16]);
					}
				}
				return;
			}
			case "oomtm450_statsBATCHTEAMTAKEAWAYS":
			{
				string[] array16 = text2.Split(';');
				string value6 = "";
				for (int num11 = 0; num11 < array16.Length; num11++)
				{
					PlayerTeam result6;
					if (num11 % 2 == 0)
					{
						value6 = array16[num11];
					}
					else if (Enum.TryParse<PlayerTeam>(value6, out result6))
					{
						ReceiveData_TeamTakeaways(result6, array16[num11]);
					}
				}
				return;
			}
			case "oomtm450_statsBATCHTEAMTURNOVERS":
			{
				string[] array18 = text2.Split(';');
				string value8 = "";
				for (int num13 = 0; num13 < array18.Length; num13++)
				{
					PlayerTeam result8;
					if (num13 % 2 == 0)
					{
						value8 = array18[num13];
					}
					else if (Enum.TryParse<PlayerTeam>(value8, out result8))
					{
						ReceiveData_TeamTurnovers(result8, array18[num13]);
					}
				}
				return;
			}
			case "oomtm450_statsBATCHTEAMEXITS":
			{
				string[] array13 = text2.Split(';');
				string value5 = "";
				for (int num8 = 0; num8 < array13.Length; num8++)
				{
					PlayerTeam result5;
					if (num8 % 2 == 0)
					{
						value5 = array13[num8];
					}
					else if (Enum.TryParse<PlayerTeam>(value5, out result5))
					{
						ReceiveData_TeamExits(result5, array13[num8]);
					}
				}
				return;
			}
			case "oomtm450_statsBATCHTEAMENTRIES":
			{
				string[] array12 = text2.Split(';');
				string value4 = "";
				for (int num7 = 0; num7 < array12.Length; num7++)
				{
					PlayerTeam result4;
					if (num7 % 2 == 0)
					{
						value4 = array12[num7];
					}
					else if (Enum.TryParse<PlayerTeam>(value4, out result4))
					{
						ReceiveData_TeamEntries(result4, array12[num7]);
					}
				}
				return;
			}
			case "oomtm450_statsBATCHTEAMPOSSESSIONTIME":
			{
				string[] array6 = text2.Split(';');
				string value2 = "";
				for (int n = 0; n < array6.Length; n++)
				{
					PlayerTeam result2;
					if (n % 2 == 0)
					{
						value2 = array6[n];
					}
					else if (Enum.TryParse<PlayerTeam>(value2, out result2))
					{
						ReceiveData_TeamPossessionTime(result2, array6[n]);
					}
				}
				return;
			}
			case "oomtm450_statsBATCHTEAMPUCKBATTLEWINS":
			{
				string[] array11 = text2.Split(';');
				string value3 = "";
				for (int num6 = 0; num6 < array11.Length; num6++)
				{
					PlayerTeam result3;
					if (num6 % 2 == 0)
					{
						value3 = array11[num6];
					}
					else if (Enum.TryParse<PlayerTeam>(value3, out result3))
					{
						ReceiveData_TeamPuckBattleWins(result3, array11[num6]);
					}
				}
				return;
			}
			case "oomtm450_statsBATCHTEAMPUCKBATTLELOSSES":
			{
				string[] array5 = text2.Split(';');
				string value = "";
				for (int m = 0; m < array5.Length; m++)
				{
					PlayerTeam result;
					if (m % 2 == 0)
					{
						value = array5[m];
					}
					else if (Enum.TryParse<PlayerTeam>(value, out result))
					{
						ReceiveData_TeamPuckBattleLosses(result, array5[m]);
					}
				}
				return;
			}
			case "oomtm450_statsBATCHSTICKSAVES":
			{
				string[] array8 = text2.Split(';');
				for (int num3 = 0; num3 < array8.Length; num3 += 2)
				{
					if (num3 + 1 < array8.Length)
					{
						string playerSteamId6 = array8[num3];
						string dataStr2 = array8[num3 + 1];
						ReceiveData_StickSaves(playerSteamId6, dataStr2);
					}
				}
				return;
			}
			case "oomtm450_statsBATCHPADSAVES":
			{
				string[] array34 = text2.Split(';');
				for (int num30 = 0; num30 < array34.Length; num30 += 2)
				{
					if (num30 + 1 < array34.Length)
					{
						string playerSteamId21 = array34[num30];
						string dataStr5 = array34[num30 + 1];
						ReceiveData_PadSaves(playerSteamId21, dataStr5);
					}
				}
				return;
			}
			case "oomtm450_statsBATCHBODYSAVES":
			{
				string[] array33 = text2.Split(';');
				for (int num28 = 0; num28 < array33.Length; num28 += 2)
				{
					if (num28 + 1 < array33.Length)
					{
						string playerSteamId20 = array33[num28];
						string dataStr4 = array33[num28 + 1];
						ReceiveData_BodySaves(playerSteamId20, dataStr4);
					}
				}
				return;
			}
			case "oomtm450_statsBATCHHOMEPLATESAVES":
			{
				string[] array3 = text2.Split(';');
				for (int k = 0; k < array3.Length; k += 2)
				{
					if (k + 1 < array3.Length)
					{
						string playerSteamId3 = array3[k];
						string dataStr = array3[k + 1];
						ReceiveData_HomePlateSaves(playerSteamId3, dataStr);
					}
				}
				return;
			}
			case "oomtm450_statsBATCHHOMEPLATESHOTSFACED":
			{
				string[] array29 = text2.Split(';');
				for (int num24 = 0; num24 < array29.Length; num24 += 2)
				{
					if (num24 + 1 < array29.Length)
					{
						string playerSteamId16 = array29[num24];
						string dataStr3 = array29[num24 + 1];
						ReceiveData_HomePlateShotsFaced(playerSteamId16, dataStr3);
					}
				}
				return;
			}
			}
			bool num29 = clientId == 0;
			bool flag2 = text2 != null && text2.Length <= 4 && !text2.Contains(';');
			if (num29 && flag2 && text != null && text.StartsWith("oomtm450_stats"))
			{
				Client_ResetSOG();
				Client_ResetSavePerc();
				Client_ResetPasses();
				Client_ResetBlocks();
				Client_ResetHits();
				Client_ResetTakeaways();
				Client_ResetTurnovers();
				Client_ResetExits();
				Client_ResetEntries();
				Client_ResetShotAttempts();
				Client_ResetExpectedGoalsFor();
				Client_ResetExpectedGoalsAgainst();
				Client_ResetHomePlateSogs();
				Client_ResetPuckTouches();
				Client_ResetPossessionTime();
				Client_ResetPuckBattles();
				Client_ResetStickSaves();
				Client_ResetPadSaves();
				Client_ResetBodySaves();
				Client_ResetHomePlateSaves();
				Client_ResetHomePlateShots();
				Client_ResetTeamStats();
				Client_RefreshTooltipsAndLabelsAfterReset();
			}
			else if (text.StartsWith("oomtm450_statsEXPECTED_GOALS_AGAINST"))
			{
				string text34 = text.Replace("oomtm450_statsEXPECTED_GOALS_AGAINST", "");
				if (string.IsNullOrEmpty(text34))
				{
					return;
				}
				ReceiveData_ExpectedGoalsAgainst(text34, text2);
			}
			else if (text.StartsWith("oomtm450_statsEXPECTED_GOALS"))
			{
				string text35 = text.Replace("oomtm450_statsEXPECTED_GOALS", "");
				if (string.IsNullOrEmpty(text35))
				{
					return;
				}
				ReceiveData_ExpectedGoalsFor(text35, text2);
			}
			else if (text.StartsWith("oomtm450_statsSOG"))
			{
				string text34 = text.Replace("oomtm450_statsSOG", "");
				if (string.IsNullOrEmpty(text34))
				{
					return;
				}
				ReceiveData_SOG(text34, text2);
			}
			if (text.StartsWith("oomtm450_statsSAVEPERC"))
			{
				string text35 = text.Replace("oomtm450_statsSAVEPERC", "");
				if (string.IsNullOrEmpty(text35))
				{
					return;
				}
				ReceiveData_SavePerc(text35, text2);
			}
			if (text.StartsWith("oomtm450_statsBLOCK"))
			{
				string text36 = text.Replace("oomtm450_statsBLOCK", "");
				if (string.IsNullOrEmpty(text36))
				{
					return;
				}
				ReceiveData_Block(text36, text2);
			}
			if (text.StartsWith("oomtm450_statsHIT"))
			{
				string text37 = text.Replace("oomtm450_statsHIT", "");
				if (string.IsNullOrEmpty(text37))
				{
					return;
				}
				ReceiveData_Hit(text37, text2);
			}
			if (text.StartsWith("oomtm450_statsTURNOVER"))
			{
				string text38 = text.Replace("oomtm450_statsTURNOVER", "");
				if (string.IsNullOrEmpty(text38))
				{
					return;
				}
				ReceiveData_Turnover(text38, text2);
			}
			if (text.StartsWith("oomtm450_statsTAKEAWAY"))
			{
				string text39 = text.Replace("oomtm450_statsTAKEAWAY", "");
				if (string.IsNullOrEmpty(text39))
				{
					return;
				}
				ReceiveData_Takeaway(text39, text2);
			}
			if (text.StartsWith("oomtm450_statsPASS"))
			{
				string text40 = text.Replace("oomtm450_statsPASS", "");
				if (string.IsNullOrEmpty(text40))
				{
					return;
				}
				ReceiveData_Pass(text40, text2);
			}
			if (text.StartsWith("oomtm450_statsPUCK_TOUCH"))
			{
				string text41 = text.Replace("oomtm450_statsPUCK_TOUCH", "");
				if (string.IsNullOrEmpty(text41))
				{
					return;
				}
				ReceiveData_PuckTouch(text41, text2);
			}
			if (text.StartsWith("oomtm450_statsEXIT"))
			{
				string text42 = text.Replace("oomtm450_statsEXIT", "");
				if (string.IsNullOrEmpty(text42))
				{
					return;
				}
				ReceiveData_Exit(text42, text2);
			}
			if (text.StartsWith("oomtm450_statsENTRY"))
			{
				string text43 = text.Replace("oomtm450_statsENTRY", "");
				if (string.IsNullOrEmpty(text43))
				{
					return;
				}
				ReceiveData_Entry(text43, text2);
			}
			if (text.StartsWith("oomtm450_statsPOSSESSION_TIME"))
			{
				string text44 = text.Replace("oomtm450_statsPOSSESSION_TIME", "");
				if (string.IsNullOrEmpty(text44))
				{
					return;
				}
				ReceiveData_PossessionTime(text44, text2);
			}
			if (text.StartsWith("oomtm450_statsPUCK_BATTLE_WINS"))
			{
				string text45 = text.Replace("oomtm450_statsPUCK_BATTLE_WINS", "");
				if (string.IsNullOrEmpty(text45))
				{
					return;
				}
				ReceiveData_PuckBattleWins(text45, text2);
			}
			if (text.StartsWith("oomtm450_statsPUCK_BATTLE_LOSSES"))
			{
				string text46 = text.Replace("oomtm450_statsPUCK_BATTLE_LOSSES", "");
				if (string.IsNullOrEmpty(text46))
				{
					return;
				}
				ReceiveData_PuckBattleLosses(text46, text2);
			}
			if (text.StartsWith("oomtm450_statsSHOT_ATTEMPTS"))
			{
				string text47 = text.Replace("oomtm450_statsSHOT_ATTEMPTS", "");
				if (string.IsNullOrEmpty(text47))
				{
					return;
				}
				ReceiveData_ShotAttempts(text47, text2);
			}
			if (text.StartsWith("oomtm450_statsON_ICE_TEAM_SHOT_ATTEMPTS"))
			{
				string text47b = text.Replace("oomtm450_statsON_ICE_TEAM_SHOT_ATTEMPTS", "");
				if (string.IsNullOrEmpty(text47b))
				{
					return;
				}
				ReceiveData_OnIceTeamShotAttempts(text47b, text2);
			}
			if (text.StartsWith("oomtm450_statsHOME_PLATE_SOGS"))
			{
				string text48 = text.Replace("oomtm450_statsHOME_PLATE_SOGS", "");
				if (string.IsNullOrEmpty(text48))
				{
					return;
				}
				ReceiveData_HomePlateSogs(text48, text2);
			}
			if (text.StartsWith("oomtm450_statsSTICK_SAVES"))
			{
				string text49 = text.Replace("oomtm450_statsSTICK_SAVES", "");
				if (string.IsNullOrEmpty(text49))
				{
					return;
				}
				ReceiveData_StickSaves(text49, text2);
			}
			if (text.StartsWith("oomtm450_statsPAD_SAVES"))
			{
				string text49b = text.Replace("oomtm450_statsPAD_SAVES", "");
				if (string.IsNullOrEmpty(text49b))
				{
					return;
				}
				ReceiveData_PadSaves(text49b, text2);
			}
			if (text.StartsWith("oomtm450_statsBODY_SAVES"))
			{
				string text50 = text.Replace("oomtm450_statsBODY_SAVES", "");
				if (string.IsNullOrEmpty(text50))
				{
					return;
				}
				ReceiveData_BodySaves(text50, text2);
			}
			if (text.StartsWith("oomtm450_statsHOME_PLATE_SAVES"))
			{
				string text51 = text.Replace("oomtm450_statsHOME_PLATE_SAVES", "");
				if (string.IsNullOrEmpty(text51))
				{
					return;
				}
				ReceiveData_HomePlateSaves(text51, text2);
			}
			if (text.StartsWith("oomtm450_statsHOME_PLATE_SHOTS_FACED"))
			{
				string text52 = text.Replace("oomtm450_statsHOME_PLATE_SHOTS_FACED", "");
				if (string.IsNullOrEmpty(text52))
				{
					return;
				}
				ReceiveData_HomePlateShotsFaced(text52, text2);
			}
			if (text.StartsWith("oomtm450_statsTEAM_SHOTS"))
			{
				string value15 = text.Replace("oomtm450_statsTEAM_SHOTS", "");
				if (string.IsNullOrEmpty(value15))
				{
					return;
				}
				if (Enum.TryParse<PlayerTeam>(value15, out var result14))
				{
					ReceiveData_TeamShots(result14, text2);
				}
			}
			if (text.StartsWith("oomtm450_statsTEAM_SHOT_ATTEMPTS"))
			{
				string value16 = text.Replace("oomtm450_statsTEAM_SHOT_ATTEMPTS", "");
				if (string.IsNullOrEmpty(value16))
				{
					return;
				}
				if (Enum.TryParse<PlayerTeam>(value16, out var result15))
				{
					ReceiveData_TeamShotAttempts(result15, text2);
				}
			}
			if (text.StartsWith("oomtm450_statsTEAM_HOME_PLATE_SOGS"))
			{
				string value17 = text.Replace("oomtm450_statsTEAM_HOME_PLATE_SOGS", "");
				if (string.IsNullOrEmpty(value17))
				{
					return;
				}
				if (Enum.TryParse<PlayerTeam>(value17, out var result16))
				{
					ReceiveData_TeamHomePlateSogs(result16, text2);
				}
			}
			if (text.StartsWith("oomtm450_statsTEAM_PASSES"))
			{
				string value18 = text.Replace("oomtm450_statsTEAM_PASSES", "");
				if (string.IsNullOrEmpty(value18))
				{
					return;
				}
				if (Enum.TryParse<PlayerTeam>(value18, out var result17))
				{
					ReceiveData_TeamPasses(result17, text2);
				}
			}
			if (text.StartsWith("oomtm450_statsTEAM_TAKEAWAYS"))
			{
				string value19 = text.Replace("oomtm450_statsTEAM_TAKEAWAYS", "");
				if (string.IsNullOrEmpty(value19))
				{
					return;
				}
				if (Enum.TryParse<PlayerTeam>(value19, out var result18))
				{
					ReceiveData_TeamTakeaways(result18, text2);
				}
			}
			if (text.StartsWith("oomtm450_statsTEAM_TURNOVERS"))
			{
				string value20 = text.Replace("oomtm450_statsTEAM_TURNOVERS", "");
				if (string.IsNullOrEmpty(value20))
				{
					return;
				}
				if (Enum.TryParse<PlayerTeam>(value20, out var result19))
				{
					ReceiveData_TeamTurnovers(result19, text2);
				}
			}
			if (text.StartsWith("oomtm450_statsTEAM_EXITS"))
			{
				string value21 = text.Replace("oomtm450_statsTEAM_EXITS", "");
				if (string.IsNullOrEmpty(value21))
				{
					return;
				}
				if (Enum.TryParse<PlayerTeam>(value21, out var result20))
				{
					ReceiveData_TeamExits(result20, text2);
				}
			}
			if (text.StartsWith("oomtm450_statsTEAM_ENTRIES"))
			{
				string value22 = text.Replace("oomtm450_statsTEAM_ENTRIES", "");
				if (string.IsNullOrEmpty(value22))
				{
					return;
				}
				if (Enum.TryParse<PlayerTeam>(value22, out var result21))
				{
					ReceiveData_TeamEntries(result21, text2);
				}
			}
			if (text.StartsWith("oomtm450_statsTEAM_POSSESSION_TIME"))
			{
				string value23 = text.Replace("oomtm450_statsTEAM_POSSESSION_TIME", "");
				if (string.IsNullOrEmpty(value23))
				{
					return;
				}
				if (Enum.TryParse<PlayerTeam>(value23, out var result22))
				{
					ReceiveData_TeamPossessionTime(result22, text2);
				}
			}
			if (text.StartsWith("oomtm450_statsTEAM_PUCK_BATTLE_WINS"))
			{
				string value24 = text.Replace("oomtm450_statsTEAM_PUCK_BATTLE_WINS", "");
				if (string.IsNullOrEmpty(value24))
				{
					return;
				}
				if (Enum.TryParse<PlayerTeam>(value24, out var result23))
				{
					ReceiveData_TeamPuckBattleWins(result23, text2);
				}
			}
			if (text.StartsWith("oomtm450_statsTEAM_PUCK_BATTLE_LOSSES"))
			{
				string value25 = text.Replace("oomtm450_statsTEAM_PUCK_BATTLE_LOSSES", "");
				if (!string.IsNullOrEmpty(value25) && Enum.TryParse<PlayerTeam>(value25, out var result24))
				{
					ReceiveData_TeamPuckBattleLosses(result24, text2);
				}
			}
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error in ReceiveData.\n{arg}", ServerConfig);
		}
	}

	private static void ReceiveData_SOG(string playerSteamId, string dataStr)
	{
		int value = int.Parse(dataStr);
		if (_sog.TryGetValue(playerSteamId, out var _))
		{
			_sog[playerSteamId] = value;
			Player playerBySteamId = MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayerBySteamId(playerSteamId);
			if (playerBySteamId != null && (bool)playerBySteamId && !PlayerFunc.IsGoalie(playerBySteamId) && _sogLabels.TryGetValue(playerSteamId, out var value2) && value2 != null)
			{
				value2.text = value.ToString();
			}
		}
		else
		{
			_sog.Add(playerSteamId, value);
		}
		RefreshVisiblePlayerTooltip(playerSteamId);
	}

	private static void ReceiveData_SavePerc(string playerSteamId, string dataStr)
	{
		string[] array = SystemFunc.RemoveWhitespace(dataStr.Replace("(", "").Replace(")", "")).Split(',');
		int num = int.Parse(array[0]);
		int num2 = int.Parse(array[1]);
		if (_savePerc.TryGetValue(playerSteamId, out (int, int) _))
		{
			_savePerc[playerSteamId] = (num, num2);
			Player playerBySteamId = MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayerBySteamId(playerSteamId);
			if ((bool)playerBySteamId && PlayerFunc.IsGoalie(playerBySteamId) && _sogLabels.TryGetValue(playerSteamId, out var value) && value != null)
			{
				value.text = GetGoalieSavePerc(num, num2);
			}
		}
		else
		{
			_savePerc.Add(playerSteamId, (num, num2));
		}
		RefreshVisiblePlayerTooltip(playerSteamId);
	}

	private static void ReceiveData_Star(string playerSteamId, string dataStr)
	{
		int num = int.Parse(dataStr);
		if (_stars.TryGetValue(num, out var _))
		{
			_stars[num] = playerSteamId;
		}
		else
		{
			_stars.Add(num, playerSteamId);
		}
	}

	private static void ReceiveData_Block(string playerSteamId, string dataStr)
	{
		int value = int.Parse(dataStr);
		if (_blocks.TryGetValue(playerSteamId, out var _))
		{
			_blocks[playerSteamId] = value;
		}
		else
		{
			_blocks.Add(playerSteamId, value);
		}
	}

	private static void ReceiveData_Hit(string playerSteamId, string dataStr)
	{
		int value = int.Parse(dataStr);
		if (_hits.TryGetValue(playerSteamId, out var _))
		{
			_hits[playerSteamId] = value;
		}
		else
		{
			_hits.Add(playerSteamId, value);
		}
	}

	private static void ReceiveData_Turnover(string playerSteamId, string dataStr)
	{
		int value = int.Parse(dataStr);
		if (_turnovers.TryGetValue(playerSteamId, out var _))
		{
			_turnovers[playerSteamId] = value;
		}
		else
		{
			_turnovers.Add(playerSteamId, value);
		}
	}

	private static void ReceiveData_Takeaway(string playerSteamId, string dataStr)
	{
		int value = int.Parse(dataStr);
		if (_takeaways.TryGetValue(playerSteamId, out var _))
		{
			_takeaways[playerSteamId] = value;
		}
		else
		{
			_takeaways.Add(playerSteamId, value);
		}
	}

	private static void ReceiveData_Pass(string playerSteamId, string dataStr)
	{
		int value = int.Parse(dataStr);
		if (_passes.TryGetValue(playerSteamId, out var _))
		{
			_passes[playerSteamId] = value;
		}
		else
		{
			_passes.Add(playerSteamId, value);
		}
		RefreshVisiblePlayerTooltip(playerSteamId);
	}

	private static void ReceiveData_PuckTouch(string playerSteamId, string dataStr)
	{
		int value = int.Parse(dataStr);
		if (_puckTouches.TryGetValue(playerSteamId, out var _))
		{
			_puckTouches[playerSteamId] = value;
		}
		else
		{
			_puckTouches.Add(playerSteamId, value);
		}
	}

	private static void ReceiveData_Exit(string playerSteamId, string dataStr)
	{
		int value = int.Parse(dataStr);
		if (_exits.TryGetValue(playerSteamId, out var _))
		{
			_exits[playerSteamId] = value;
		}
		else
		{
			_exits.Add(playerSteamId, value);
		}
		RefreshVisiblePlayerTooltip(playerSteamId);
	}

	private static void ReceiveData_Entry(string playerSteamId, string dataStr)
	{
		int value = int.Parse(dataStr);
		if (_entries.TryGetValue(playerSteamId, out var _))
		{
			_entries[playerSteamId] = value;
		}
		else
		{
			_entries.Add(playerSteamId, value);
		}
		RefreshVisiblePlayerTooltip(playerSteamId);
	}

	private static void ReceiveData_PossessionTime(string playerSteamId, string dataStr)
	{
		double value = double.Parse(dataStr);
		if (_possessionTimeSeconds.TryGetValue(playerSteamId, out var _))
		{
			_possessionTimeSeconds[playerSteamId] = value;
		}
		else
		{
			_possessionTimeSeconds.Add(playerSteamId, value);
		}
		RefreshVisiblePlayerTooltip(playerSteamId);
	}

	private static void ReceiveData_PuckBattleWins(string playerSteamId, string dataStr)
	{
		int value = int.Parse(dataStr);
		if (_puckBattleWins.TryGetValue(playerSteamId, out var _))
		{
			_puckBattleWins[playerSteamId] = value;
		}
		else
		{
			_puckBattleWins.Add(playerSteamId, value);
		}
	}

	private static void ReceiveData_PuckBattleLosses(string playerSteamId, string dataStr)
	{
		int value = int.Parse(dataStr);
		if (_puckBattleLosses.TryGetValue(playerSteamId, out var _))
		{
			_puckBattleLosses[playerSteamId] = value;
		}
		else
		{
			_puckBattleLosses.Add(playerSteamId, value);
		}
	}

	private static void ReceiveData_ShotAttempts(string playerSteamId, string dataStr)
	{
		int value = int.Parse(dataStr);
		if (_shotAttempts.TryGetValue(playerSteamId, out var _))
		{
			_shotAttempts[playerSteamId] = value;
		}
		else
		{
			_shotAttempts.Add(playerSteamId, value);
		}
		RefreshVisiblePlayerTooltip(playerSteamId);
	}

	private static void ReceiveData_OnIceTeamShotAttempts(string playerSteamId, string dataStr)
	{
		int value = int.Parse(dataStr);
		if (_onIceTeamShotAttempts.TryGetValue(playerSteamId, out var _))
		{
			_onIceTeamShotAttempts[playerSteamId] = value;
		}
		else
		{
			_onIceTeamShotAttempts.Add(playerSteamId, value);
		}
		RefreshVisiblePlayerTooltip(playerSteamId);
	}

	private static void ReceiveData_ExpectedGoalsFor(string playerSteamId, string dataStr)
	{
		double value = double.Parse(dataStr, CultureInfo.InvariantCulture);
		if (_expectedGoalsFor.TryGetValue(playerSteamId, out var _))
		{
			_expectedGoalsFor[playerSteamId] = value;
		}
		else
		{
			_expectedGoalsFor.Add(playerSteamId, value);
		}
		RefreshVisiblePlayerTooltip(playerSteamId);
	}

	private static void ReceiveData_ExpectedGoalsAgainst(string playerSteamId, string dataStr)
	{
		double value = double.Parse(dataStr, CultureInfo.InvariantCulture);
		if (_expectedGoalsAgainst.TryGetValue(playerSteamId, out var _))
		{
			_expectedGoalsAgainst[playerSteamId] = value;
		}
		else
		{
			_expectedGoalsAgainst.Add(playerSteamId, value);
		}
		RefreshVisiblePlayerTooltip(playerSteamId);
	}

	private static void ReceiveData_HomePlateSogs(string playerSteamId, string dataStr)
	{
		int value = int.Parse(dataStr);
		if (_homePlateSogs.TryGetValue(playerSteamId, out var _))
		{
			_homePlateSogs[playerSteamId] = value;
		}
		else
		{
			_homePlateSogs.Add(playerSteamId, value);
		}
	}

	private static void ReceiveData_TeamShots(PlayerTeam team, string dataStr)
	{
		int value = int.Parse(dataStr);
		if (_teamShots.TryGetValue(team, out var _))
		{
			_teamShots[team] = value;
		}
		else
		{
			_teamShots.Add(team, value);
		}
		RefreshVisibleTeamTooltips();
		WriteClientTeamStatsToFile();
	}

	private static void ReceiveData_TeamShotAttempts(PlayerTeam team, string dataStr)
	{
		int value = int.Parse(dataStr);
		if (_teamShotAttempts.TryGetValue(team, out var _))
		{
			_teamShotAttempts[team] = value;
		}
		else
		{
			_teamShotAttempts.Add(team, value);
		}
		WriteClientTeamStatsToFile();
	}

	private static void ReceiveData_TeamHomePlateSogs(PlayerTeam team, string dataStr)
	{
		int value = int.Parse(dataStr);
		if (_teamHomePlateSogs.TryGetValue(team, out var _))
		{
			_teamHomePlateSogs[team] = value;
		}
		else
		{
			_teamHomePlateSogs.Add(team, value);
		}
		WriteClientTeamStatsToFile();
	}

	private static void ReceiveData_TeamPasses(PlayerTeam team, string dataStr)
	{
		int value = int.Parse(dataStr);
		if (_teamPasses.TryGetValue(team, out var _))
		{
			_teamPasses[team] = value;
		}
		else
		{
			_teamPasses.Add(team, value);
		}
		RefreshVisibleTeamTooltips();
		WriteClientTeamStatsToFile();
	}

	private static void ReceiveData_TeamFaceoffWins(PlayerTeam team, string dataStr)
	{
		int value = int.Parse(dataStr);
		if (_teamFaceoffWins.TryGetValue(team, out var _))
		{
			_teamFaceoffWins[team] = value;
		}
		else
		{
			_teamFaceoffWins.Add(team, value);
		}
		RefreshVisibleTeamTooltips();
		WriteClientTeamStatsToFile();
	}

	private static void ReceiveData_TeamFaceoffTotal(PlayerTeam team, string dataStr)
	{
		int value = int.Parse(dataStr);
		if (_teamFaceoffTotal.TryGetValue(team, out var _))
		{
			_teamFaceoffTotal[team] = value;
		}
		else
		{
			_teamFaceoffTotal.Add(team, value);
		}
		RefreshVisibleTeamTooltips();
		WriteClientTeamStatsToFile();
	}

	private static void ReceiveData_TeamTakeaways(PlayerTeam team, string dataStr)
	{
		int value = int.Parse(dataStr);
		if (_teamTakeaways.TryGetValue(team, out var _))
		{
			_teamTakeaways[team] = value;
		}
		else
		{
			_teamTakeaways.Add(team, value);
		}
		RefreshVisibleTeamTooltips();
		WriteClientTeamStatsToFile();
	}

	private static void ReceiveData_TeamTurnovers(PlayerTeam team, string dataStr)
	{
		int value = int.Parse(dataStr);
		if (_teamTurnovers.TryGetValue(team, out var _))
		{
			_teamTurnovers[team] = value;
		}
		else
		{
			_teamTurnovers.Add(team, value);
		}
		WriteClientTeamStatsToFile();
	}

	private static void ReceiveData_TeamExits(PlayerTeam team, string dataStr)
	{
		int value = int.Parse(dataStr);
		if (_teamExits.TryGetValue(team, out var _))
		{
			_teamExits[team] = value;
		}
		else
		{
			_teamExits.Add(team, value);
		}
		RefreshVisibleTeamTooltips();
		WriteClientTeamStatsToFile();
	}

	private static void ReceiveData_TeamEntries(PlayerTeam team, string dataStr)
	{
		int value = int.Parse(dataStr);
		if (_teamEntries.TryGetValue(team, out var _))
		{
			_teamEntries[team] = value;
		}
		else
		{
			_teamEntries.Add(team, value);
		}
		RefreshVisibleTeamTooltips();
		WriteClientTeamStatsToFile();
	}

	private static void ReceiveData_TeamPossessionTime(PlayerTeam team, string dataStr)
	{
		double value = double.Parse(dataStr);
		if (_teamPossessionTime.TryGetValue(team, out var _))
		{
			_teamPossessionTime[team] = value;
		}
		else
		{
			_teamPossessionTime.Add(team, value);
		}
		RefreshVisibleTeamTooltips();
		WriteClientTeamStatsToFile();
	}

	private static void ReceiveData_TeamPuckBattleWins(PlayerTeam team, string dataStr)
	{
		int value = int.Parse(dataStr);
		if (_teamPuckBattleWins.TryGetValue(team, out var _))
		{
			_teamPuckBattleWins[team] = value;
		}
		else
		{
			_teamPuckBattleWins.Add(team, value);
		}
		WriteClientTeamStatsToFile();
	}

	private static void ReceiveData_TeamPuckBattleLosses(PlayerTeam team, string dataStr)
	{
		int value = int.Parse(dataStr);
		if (_teamPuckBattleLosses.TryGetValue(team, out var _))
		{
			_teamPuckBattleLosses[team] = value;
		}
		else
		{
			_teamPuckBattleLosses.Add(team, value);
		}
		WriteClientTeamStatsToFile();
	}

	private static void WriteClientTeamStatsToFile()
	{
        if (_statReceiveDepth > 0) { _clientTeamFilesDirty = true; return; }
		if (!_clientConfig.LogClientSideStats)
		{
			return;
		}
		try
		{
			int num = 0;
			int num2 = 0;
			if (NetworkBehaviourSingleton<GameManager>.Instance != null && NetworkBehaviourSingleton<GameManager>.Instance.GameState != null)
			{
				num = NetworkBehaviourSingleton<GameManager>.Instance.GameState.Value.BlueScore;
				num2 = NetworkBehaviourSingleton<GameManager>.Instance.GameState.Value.RedScore;
			}
			int num3 = 0;
			int num4 = 0;
			if (MonoBehaviourSingleton<PlayerManager>.Instance != null)
			{
				foreach (Player player in MonoBehaviourSingleton<PlayerManager>.Instance.GetPlayers())
				{
					if (player == null || !player)
					{
						continue;
					}
					string key = player.SteamId.Value.ToString();
					if (_savePerc.TryGetValue(key, out (int, int) value))
					{
						if (player.Team == PlayerTeam.Red)
						{
							num3 += value.Item1;
						}
						else if (player.Team == PlayerTeam.Blue)
						{
							num4 += value.Item1;
						}
					}
				}
			}
			string text = Path.Combine(Path.GetFullPath("."), "stats");

			int value2;
			int num5 = (_teamShots.TryGetValue(PlayerTeam.Red, out value2) ? value2 : 0);
			int value3;
			int num6 = (_teamShots.TryGetValue(PlayerTeam.Blue, out value3) ? value3 : 0);
			int value4;
			int num7 = (_teamPasses.TryGetValue(PlayerTeam.Red, out value4) ? value4 : 0);
			int value5;
			int num8 = (_teamPasses.TryGetValue(PlayerTeam.Blue, out value5) ? value5 : 0);
			int value6;
			int num9 = (_teamFaceoffWins.TryGetValue(PlayerTeam.Red, out value6) ? value6 : 0);
			int value7;
			int num10 = (_teamFaceoffWins.TryGetValue(PlayerTeam.Blue, out value7) ? value7 : 0);
			int value8;
			int num11 = (_teamExits.TryGetValue(PlayerTeam.Red, out value8) ? value8 : 0);
			int value9;
			int num12 = (_teamExits.TryGetValue(PlayerTeam.Blue, out value9) ? value9 : 0);
			int value10;
			int num13 = (_teamEntries.TryGetValue(PlayerTeam.Red, out value10) ? value10 : 0);
			int value11;
			int num14 = (_teamEntries.TryGetValue(PlayerTeam.Blue, out value11) ? value11 : 0);
			double value12;
			double num15 = (_teamPossessionTime.TryGetValue(PlayerTeam.Red, out value12) ? value12 : 0.0);
			double value13;
			double num16 = (_teamPossessionTime.TryGetValue(PlayerTeam.Blue, out value13) ? value13 : 0.0);
			string path = Path.Combine(text, "PHL_stats_team.csv");
			StringBuilder stringBuilder = new StringBuilder();
			stringBuilder.AppendLine("team,shots,shotAttempts,homePlateSogs,passes,faceoffWins,faceoffTotal,takeaways,turnovers,exits,entries,possessionTime,puckBattleWins,puckBattleLosses,goals,saves");
			PlayerTeam[] array = new PlayerTeam[2]
			{
				PlayerTeam.Red,
				PlayerTeam.Blue
			};
			foreach (PlayerTeam playerTeam in array)
			{
				int num17 = ((playerTeam == PlayerTeam.Red) ? num5 : num6);
				int value14;
				int num18 = (_teamShotAttempts.TryGetValue(playerTeam, out value14) ? value14 : 0);
				int value15;
				int num19 = (_teamHomePlateSogs.TryGetValue(playerTeam, out value15) ? value15 : 0);
				int num20 = ((playerTeam == PlayerTeam.Red) ? num7 : num8);
				int num21 = ((playerTeam == PlayerTeam.Red) ? num9 : num10);
				int value16;
				int num22 = (_teamFaceoffTotal.TryGetValue(playerTeam, out value16) ? value16 : 0);
				int value17;
				int num23 = (_teamTakeaways.TryGetValue(playerTeam, out value17) ? value17 : 0);
				int value18;
				int num24 = (_teamTurnovers.TryGetValue(playerTeam, out value18) ? value18 : 0);
				int num25 = ((playerTeam == PlayerTeam.Red) ? num11 : num12);
				int num26 = ((playerTeam == PlayerTeam.Red) ? num13 : num14);
				double num27 = ((playerTeam == PlayerTeam.Red) ? num15 : num16);
				int value19;
				int num28 = (_teamPuckBattleWins.TryGetValue(playerTeam, out value19) ? value19 : 0);
				int value20;
				int num29 = (_teamPuckBattleLosses.TryGetValue(playerTeam, out value20) ? value20 : 0);
				int num30 = ((playerTeam == PlayerTeam.Red) ? num : num2);
				int num31 = ((playerTeam == PlayerTeam.Red) ? num3 : num4);
				stringBuilder.AppendLine(string.Format("{0},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11},{12},{13},{14},{15}", playerTeam, num17, num18, num19, num20, num21, num22, num23, num24, num25, num26, num27.ToString("R", CultureInfo.InvariantCulture), num28, num29, num30, num31));
			}
            QueueClientFiles(new Dictionary<string, string> {
                { path, stringBuilder.ToString() },
                { Path.Combine(text, "bluegoals.txt"), num.ToString() },
                { Path.Combine(text, "redgoals.txt"), num2.ToString() },
                { Path.Combine(text, "blueshots.txt"), num5.ToString() },
                { Path.Combine(text, "redshots.txt"), num6.ToString() },
                { Path.Combine(text, "bluefaceoffwins.txt"), num9.ToString() },
                { Path.Combine(text, "redfaceoffwins.txt"), num10.ToString() },
                { Path.Combine(text, "blueexits.txt"), num11.ToString() },
                { Path.Combine(text, "redexits.txt"), num12.ToString() },
                { Path.Combine(text, "blueentries.txt"), num13.ToString() },
                { Path.Combine(text, "redentries.txt"), num14.ToString() },
                { Path.Combine(text, "bluepossession.txt"), num15.ToString("R", CultureInfo.InvariantCulture) },
                { Path.Combine(text, "redpossession.txt"), num16.ToString("R", CultureInfo.InvariantCulture) },
                { Path.Combine(text, "bluepasses.txt"), num7.ToString() },
                { Path.Combine(text, "redpasses.txt"), num8.ToString() },
            });
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error writing client team stats file: {arg}", _clientConfig);
		}
	}

	private static void ReceiveData_StickSaves(string playerSteamId, string dataStr)
	{
		int value = int.Parse(dataStr);
		if (_stickSaves.TryGetValue(playerSteamId, out var _))
		{
			_stickSaves[playerSteamId] = value;
		}
		else
		{
			_stickSaves.Add(playerSteamId, value);
		}
	}

	private static void ReceiveData_PadSaves(string playerSteamId, string dataStr)
	{
		int value = int.Parse(dataStr);
		if (_padSaves.TryGetValue(playerSteamId, out var _))
		{
			_padSaves[playerSteamId] = value;
		}
		else
		{
			_padSaves.Add(playerSteamId, value);
		}
	}

	private static void ReceiveData_BodySaves(string playerSteamId, string dataStr)
	{
		int value = int.Parse(dataStr);
		if (_bodySaves.TryGetValue(playerSteamId, out var _))
		{
			_bodySaves[playerSteamId] = value;
		}
		else
		{
			_bodySaves.Add(playerSteamId, value);
		}
	}

	private static void ReceiveData_HomePlateSaves(string playerSteamId, string dataStr)
	{
		int value = int.Parse(dataStr);
		if (_homePlateSaves.TryGetValue(playerSteamId, out var _))
		{
			_homePlateSaves[playerSteamId] = value;
		}
		else
		{
			_homePlateSaves.Add(playerSteamId, value);
		}
	}

	private static void ReceiveData_HomePlateShotsFaced(string playerSteamId, string dataStr)
	{
		int value = int.Parse(dataStr);
		if (_homePlateShots.TryGetValue(playerSteamId, out var _))
		{
			_homePlateShots[playerSteamId] = value;
		}
		else
		{
			_homePlateShots.Add(playerSteamId, value);
		}
	}


	private static void QueueStatUpdate(string dataName, string dataStr)
	{
		_pendingStatUpdates[dataName] = new PendingStatValue(dataStr);
		MarkPreviewLiveStatsChanged();
	}

	private static bool ValidateStringForNetwork(string data, string dataName, out string error)
	{
		error = null;
		if (data == null)
		{
			error = "Data is null";
			return false;
		}
		if (dataName == null)
		{
			error = "Data name is null";
			return false;
		}
		int byteCount = Encoding.UTF8.GetByteCount(dataName);
		if (byteCount > 1024)
		{
			error = $"Data name size ({byteCount} bytes) exceeds limit";
			return false;
		}
		int byteCount2 = Encoding.UTF8.GetByteCount(data);
		int num = byteCount + 8 + byteCount2;
		if (num > 49152)
		{
			error = $"Total size ({num} bytes = {byteCount} name + 8 overhead + {byteCount2} data) exceeds safe limit ({49152} bytes)";
			return false;
		}
		if (byteCount2 > 48000)
		{
			error = $"Data size ({byteCount2} bytes) exceeds safe limit (48000 bytes)";
			return false;
		}
		if (data.Length > 100000)
		{
			error = $"Data length ({data.Length} characters) exceeds sanity limit";
			return false;
		}
		return true;
	}

	private static bool SafeSendDataToAll(string dataName, string dataValue, NetworkDelivery networkDelivery = NetworkDelivery.ReliableFragmentedSequenced)
	{
		if (!ValidateStringForNetwork(dataValue, dataName, out var error))
		{
			Logging.LogError("Validation failed for " + dataName + ": " + error, ServerConfig);
			return false;
		}
		Server_RegisterNamedMessageHandler();
		if (NetworkManager.Singleton == null || NetworkManager.Singleton.CustomMessagingManager == null)
		{
			Logging.LogError("SafeSendDataToAll: NetworkManager not ready, skipping send", ServerConfig);
			return false;
		}
		try
		{
			NetworkCommunication.SendDataToAll(dataName, dataValue, "oomtm450_stats_server", ServerConfig, networkDelivery);
			return true;
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error sending data {dataName}: {arg}", ServerConfig);
			return false;
		}
	}

	private static void FlushPendingStatUpdatesImmediately()
	{
		if (!ServerFunc.IsDedicatedServer() || !_logic || _pendingStatUpdates.Count == 0)
		{
			return;
		}
		try
		{
			SendBatchedStatUpdates();
			_lastStatBatchSendTime = DateTime.UtcNow;
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error forcing immediate stat batch flush: {arg}", ServerConfig);
		}
	}

	private static void SendBatchedStatUpdates()
	{
		DateTime utcNow = DateTime.UtcNow;
		if (_batchingDisabled)
		{
			if (!((utcNow - _lastBatchingFailureTime).TotalSeconds >= 60.0))
			{
				return;
			}
			_batchingDisabled = false;
			_batchingFailureCount = 0;
			Logging.Log("Batching re-enabled after cooldown period", ServerConfig);
		}
		Server_RegisterNamedMessageHandler();
		if (_pendingStatUpdates.Count == 0 || NetworkManager.Singleton == null || NetworkManager.Singleton.CustomMessagingManager == null)
		{
			return;
		}
		try
		{
            var collectedBatches = _combinedBatchClients.Count > 0 ? new List<KeyValuePair<string, string>>() : null;
			Dictionary<string, string> dictionary = new Dictionary<string, string>();
			foreach (KeyValuePair<string, PendingStatValue> pendingStatUpdate in _pendingStatUpdates)
			{
				dictionary[pendingStatUpdate.Key] = pendingStatUpdate.Value.ToString();
			}
			if (_pendingStatUpdates.Count == 0 && dictionary.Count > 0)
			{
				return;
			}
			Dictionary<string, List<(string, string)>> dictionary2 = new Dictionary<string, List<(string, string)>>();
			foreach (KeyValuePair<string, string> item in dictionary)
			{
				string key = item.Key;
				string value = item.Value;
				if (string.IsNullOrEmpty(key) || value == null)
				{
					continue;
				}
				string text = null;
				string text2 = null;
				if (key.StartsWith("oomtm450_statsSOG"))
				{
					text = "oomtm450_statsBATCHSOG";
					text2 = key.Substring("oomtm450_statsSOG".Length);
				}
				else if (key.StartsWith("oomtm450_statsSAVEPERC"))
				{
					text = "oomtm450_statsBATCHSAVEPERC";
					text2 = key.Substring("oomtm450_statsSAVEPERC".Length);
				}
				else if (key.StartsWith("oomtm450_statsBLOCK"))
				{
					text = "oomtm450_statsBATCHBLOCK";
					text2 = key.Substring("oomtm450_statsBLOCK".Length);
				}
				else if (key.StartsWith("oomtm450_statsHIT"))
				{
					text = "oomtm450_statsBATCHHIT";
					text2 = key.Substring("oomtm450_statsHIT".Length);
				}
				else if (key.StartsWith("oomtm450_statsTAKEAWAY"))
				{
					text = "oomtm450_statsBATCHTAKEAWAY";
					text2 = key.Substring("oomtm450_statsTAKEAWAY".Length);
				}
				else if (key.StartsWith("oomtm450_statsTURNOVER"))
				{
					text = "oomtm450_statsBATCHTURNOVER";
					text2 = key.Substring("oomtm450_statsTURNOVER".Length);
				}
				else if (key.StartsWith("oomtm450_statsPASS"))
				{
					text = "oomtm450_statsBATCHPASS";
					text2 = key.Substring("oomtm450_statsPASS".Length);
				}
				else if (key.StartsWith("oomtm450_statsPUCK_TOUCH"))
				{
					text = "oomtm450_statsBATCHPUCKTOUCH";
					text2 = key.Substring("oomtm450_statsPUCK_TOUCH".Length);
				}
				else if (key.StartsWith("oomtm450_statsEXIT"))
				{
					text = "oomtm450_statsBATCHEXIT";
					text2 = key.Substring("oomtm450_statsEXIT".Length);
				}
				else if (key.StartsWith("oomtm450_statsENTRY"))
				{
					text = "oomtm450_statsBATCHENTRY";
					text2 = key.Substring("oomtm450_statsENTRY".Length);
				}
				else if (key.StartsWith("oomtm450_statsPOSSESSION_TIME"))
				{
					text = "oomtm450_statsBATCHPOSSESSIONTIME";
					text2 = key.Substring("oomtm450_statsPOSSESSION_TIME".Length);
				}
				else if (key.StartsWith("oomtm450_statsPUCK_BATTLE_WINS"))
				{
					text = "oomtm450_statsBATCHPUCKBATTLEWINS";
					text2 = key.Substring("oomtm450_statsPUCK_BATTLE_WINS".Length);
				}
				else if (key.StartsWith("oomtm450_statsPUCK_BATTLE_LOSSES"))
				{
					text = "oomtm450_statsBATCHPUCKBATTLELOSSES";
					text2 = key.Substring("oomtm450_statsPUCK_BATTLE_LOSSES".Length);
				}
				else if (key.StartsWith("oomtm450_statsSHOT_ATTEMPTS"))
				{
					text = "oomtm450_statsBATCHSHOTATTEMPTS";
					text2 = key.Substring("oomtm450_statsSHOT_ATTEMPTS".Length);
				}
				else if (key.StartsWith("oomtm450_statsON_ICE_TEAM_SHOT_ATTEMPTS"))
				{
					text = "oomtm450_statsBATCHONICETEAMSHOTATTEMPTS";
					text2 = key.Substring("oomtm450_statsON_ICE_TEAM_SHOT_ATTEMPTS".Length);
				}
				else if (key.StartsWith("oomtm450_statsEXPECTED_GOALS_AGAINST"))
				{
					text = "oomtm450_statsBATCHEXPECTEDGOALSAGAINST";
					text2 = key.Substring("oomtm450_statsEXPECTED_GOALS_AGAINST".Length);
				}
				else if (key.StartsWith("oomtm450_statsEXPECTED_GOALS"))
				{
					text = "oomtm450_statsBATCHEXPECTEDGOALS";
					text2 = key.Substring("oomtm450_statsEXPECTED_GOALS".Length);
				}
				else if (key.StartsWith("oomtm450_statsPAD_SAVES"))
				{
					text = "oomtm450_statsBATCHPADSAVES";
					text2 = key.Substring("oomtm450_statsPAD_SAVES".Length);
				}
				else if (key.StartsWith("oomtm450_statsTEAM_SHOTS"))
				{
					text = "oomtm450_statsBATCHTEAMSHOTS";
					text2 = key.Substring("oomtm450_statsTEAM_SHOTS".Length);
				}
				else if (key.StartsWith("oomtm450_statsTEAM_SHOT_ATTEMPTS"))
				{
					text = "oomtm450_statsBATCHTEAMSHOTATTEMPTS";
					text2 = key.Substring("oomtm450_statsTEAM_SHOT_ATTEMPTS".Length);
				}
				else if (key.StartsWith("oomtm450_statsHOME_PLATE_SOGS"))
				{
					text = "oomtm450_statsBATCHHOMEPLATESOGS";
					text2 = key.Substring("oomtm450_statsHOME_PLATE_SOGS".Length);
				}
				else if (key.StartsWith("oomtm450_statsTEAM_HOME_PLATE_SOGS"))
				{
					text = "oomtm450_statsBATCHTEAMHOMEPLATESOGS";
					text2 = key.Substring("oomtm450_statsTEAM_HOME_PLATE_SOGS".Length);
				}
				else if (key.StartsWith("oomtm450_statsTEAM_PASSES"))
				{
					text = "oomtm450_statsBATCHTEAMPASSES";
					text2 = key.Substring("oomtm450_statsTEAM_PASSES".Length);
				}
				else if (key.StartsWith("oomtm450_statsTEAM_FACEOFF_WINS"))
				{
					text = "oomtm450_statsBATCHTEAMFACEOFFWINS";
					text2 = key.Substring("oomtm450_statsTEAM_FACEOFF_WINS".Length);
				}
				else if (key.StartsWith("oomtm450_statsTEAM_FACEOFF_TOTAL"))
				{
					text = "oomtm450_statsBATCHTEAMFACEOFFTOTAL";
					text2 = key.Substring("oomtm450_statsTEAM_FACEOFF_TOTAL".Length);
				}
				else if (key.StartsWith("oomtm450_statsTEAM_TAKEAWAYS"))
				{
					text = "oomtm450_statsBATCHTEAMTAKEAWAYS";
					text2 = key.Substring("oomtm450_statsTEAM_TAKEAWAYS".Length);
				}
				else if (key.StartsWith("oomtm450_statsTEAM_TURNOVERS"))
				{
					text = "oomtm450_statsBATCHTEAMTURNOVERS";
					text2 = key.Substring("oomtm450_statsTEAM_TURNOVERS".Length);
				}
				else if (key.StartsWith("oomtm450_statsTEAM_EXITS"))
				{
					text = "oomtm450_statsBATCHTEAMEXITS";
					text2 = key.Substring("oomtm450_statsTEAM_EXITS".Length);
				}
				else if (key.StartsWith("oomtm450_statsTEAM_ENTRIES"))
				{
					text = "oomtm450_statsBATCHTEAMENTRIES";
					text2 = key.Substring("oomtm450_statsTEAM_ENTRIES".Length);
				}
				else if (key.StartsWith("oomtm450_statsTEAM_POSSESSION_TIME"))
				{
					text = "oomtm450_statsBATCHTEAMPOSSESSIONTIME";
					text2 = key.Substring("oomtm450_statsTEAM_POSSESSION_TIME".Length);
				}
				else if (key.StartsWith("oomtm450_statsTEAM_PUCK_BATTLE_WINS"))
				{
					text = "oomtm450_statsBATCHTEAMPUCKBATTLEWINS";
					text2 = key.Substring("oomtm450_statsTEAM_PUCK_BATTLE_WINS".Length);
				}
				else if (key.StartsWith("oomtm450_statsTEAM_PUCK_BATTLE_LOSSES"))
				{
					text = "oomtm450_statsBATCHTEAMPUCKBATTLELOSSES";
					text2 = key.Substring("oomtm450_statsTEAM_PUCK_BATTLE_LOSSES".Length);
				}
				else if (key.StartsWith("oomtm450_statsSTICK_SAVES"))
				{
					text = "oomtm450_statsBATCHSTICKSAVES";
					text2 = key.Substring("oomtm450_statsSTICK_SAVES".Length);
				}
				else if (key.StartsWith("oomtm450_statsBODY_SAVES"))
				{
					text = "oomtm450_statsBATCHBODYSAVES";
					text2 = key.Substring("oomtm450_statsBODY_SAVES".Length);
				}
				else if (key.StartsWith("oomtm450_statsHOME_PLATE_SAVES"))
				{
					text = "oomtm450_statsBATCHHOMEPLATESAVES";
					text2 = key.Substring("oomtm450_statsHOME_PLATE_SAVES".Length);
				}
				else
				{
					if (!key.StartsWith("oomtm450_statsHOME_PLATE_SHOTS_FACED"))
					{
						SafeSendDataToAll(key, value);
						continue;
					}
					text = "oomtm450_statsBATCHHOMEPLATESHOTSFACED";
					text2 = key.Substring("oomtm450_statsHOME_PLATE_SHOTS_FACED".Length);
				}
				if (text != null && !string.IsNullOrEmpty(text2) && value != null)
				{
					if (!dictionary2.TryGetValue(text, out var value2))
					{
						value2 = (dictionary2[text] = new List<(string, string)>());
					}
					value2.Add((text2, value));
				}
			}
			if (TrackingDiagnosticsEnabled())
			{
				List<string> list = dictionary2.Where((KeyValuePair<string, List<(string, string)>> x) => x.Key == "oomtm450_statsBATCHPASS" || x.Key == "oomtm450_statsBATCHPUCKTOUCH" || x.Key == "oomtm450_statsBATCHEXIT" || x.Key == "oomtm450_statsBATCHENTRY" || x.Key == "oomtm450_statsBATCHPOSSESSIONTIME" || x.Key == "oomtm450_statsBATCHTEAMPOSSESSIONTIME" || x.Key == "oomtm450_statsBATCHEXPECTEDGOALS" || x.Key == "oomtm450_statsBATCHEXPECTEDASSISTS" || x.Key == "oomtm450_statsBATCHTEAMEXITS" || x.Key == "oomtm450_statsBATCHTEAMENTRIES" || x.Key == "oomtm450_statsBATCHTEAMPASSES").OrderBy((KeyValuePair<string, List<(string, string)>> x) => x.Key, StringComparer.Ordinal).Select((KeyValuePair<string, List<(string, string)>> x) => $"{x.Key}={x.Value.Count}").ToList();
				if (list.Count > 0)
				{
					LogTrackingDiagnostic("batch-summary-advanced", "Prepared advanced stat batches: " + string.Join(", ", list) + $". pendingKeys={dictionary.Count}.", bypassThrottle: true);
				}
				else if (dictionary.Count > 0)
				{
					string message = string.Join(", ", dictionary.Keys.OrderBy((string x) => x, StringComparer.Ordinal).Take(10));
					LogTrackingDiagnostic("batch-summary-none", $"No advanced tracking batches prepared this flush. pendingKeysSample={message}.", minIntervalMilliseconds: 5000);
				}
			}
			foreach (KeyValuePair<string, List<(string, string)>> item2 in dictionary2)
			{
				string key2 = item2.Key;
				List<(string, string)> value3 = item2.Value;
				if (value3.Count <= 0)
				{
					continue;
				}
				int byteCount = Encoding.UTF8.GetByteCount(key2);
				int num = 49152 - byteCount - 8;
				if (num < 1024)
				{
					Logging.LogError($"Batch data name too large ({byteCount} bytes) for {key2}, using minimum batch size", ServerConfig);
					num = 1024;
				}
				List<string> list2 = new List<string>();
				StringBuilder stringBuilder = new StringBuilder();
				int num2 = 0;
				foreach (var item3 in value3)
				{
					string text3 = item3.Item1 ?? "";
					string text4 = item3.Item2 ?? "";
					if (string.IsNullOrEmpty(text3))
					{
						continue;
					}
					string text5 = text3 + ";" + text4 + ";";
					int byteCount2 = Encoding.UTF8.GetByteCount(text5);
					if (num2 > 0 && num2 + byteCount2 > num)
					{
						if (stringBuilder.Length > 0)
						{
							stringBuilder.Length--;
							string text6 = stringBuilder.ToString();
							if (!string.IsNullOrEmpty(text6))
							{
								list2.Add(text6);
							}
						}
						stringBuilder.Clear();
						num2 = 0;
					}
					stringBuilder.Append(text5);
					num2 += byteCount2;
				}
				if (stringBuilder.Length > 0)
				{
					stringBuilder.Length--;
					string text7 = stringBuilder.ToString();
					if (!string.IsNullOrEmpty(text7))
					{
						list2.Add(text7);
					}
				}
				foreach (string item4 in list2)
				{
					if (string.IsNullOrEmpty(item4))
					{
						continue;
					}
					int byteCount3 = Encoding.UTF8.GetByteCount(item4);
					int num3 = byteCount + 8 + byteCount3;
					if (num3 > 49152)
					{
						Logging.LogError($"Batch data still too large ({num3} total bytes = {byteCount} name + {8} overhead + {byteCount3} data) for {key2} after splitting, skipping", ServerConfig);
					}
					else if (byteCount3 > num)
					{
						Logging.LogError($"Batch data still too large ({byteCount3} bytes) for {key2} after splitting, skipping", ServerConfig);
					}
					else if (!SendOrCollectStatBatch(key2, item4, collectedBatches))
					{
						_batchingFailureCount++;
						_lastBatchingFailureTime = utcNow;
						if (_batchingFailureCount >= 3)
						{
							_batchingDisabled = true;
							Logging.LogError($"Batching disabled after {_batchingFailureCount} failures. Will re-enable after {60.0} seconds.", ServerConfig);
							_pendingStatUpdates.Clear();
							return;
						}
					}
					else
					{
						_batchingFailureCount = 0;
					}
				}
				if (list2.Count > 1)
				{
					Logging.Log($"Split {key2} batch into {list2.Count} chunks due to size limit", ServerConfig);
				}
			}
            SendCollectedStatBatches(collectedBatches);
			_pendingStatUpdates.Clear();
			_lastStatBatchSendTime = utcNow;
		}
		catch (Exception arg)
		{
			_batchingFailureCount++;
			_lastBatchingFailureTime = utcNow;
			Logging.LogError($"Critical error in SendBatchedStatUpdates: {arg}. Batching will be disabled if this happens {3} times.", ServerConfig);
			if (_batchingFailureCount >= 3)
			{
				_batchingDisabled = true;
				Logging.LogError($"Batching permanently disabled after {_batchingFailureCount} critical failures. Will re-enable after {60.0} seconds.", ServerConfig);
				_pendingStatUpdates.Clear();
			}
		}
	}

	private static string GetGoalieSavePerc(int saves, int shots)
	{
		if (shots == 0)
		{
			return "0.000";
		}
		return ((double)saves / (double)shots).ToString("0.000", CultureInfo.InvariantCulture);
	}
}
