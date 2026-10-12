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
	private sealed class PlayerTooltipLabels
	{
		public VisualElement Tooltip;

		public Label Title;

		public Label SavePercent;

		public Label ExpectedGoalsAgainst;

		public Label GoalsSavedAboveExpected;

		public Label BodySaves;

		public Label PadSaves;

		public Label StickSaves;

		public Label ExpectedGoals;

		public Label IndividualCorsiFor;

		public Label IndividualCorsiForPercent;

		public Label Passes;

		public Label ExitsEntries;

		public Label PossessionTime;

		public Label Takeaways;

		public Label Turnovers;

		public Label Blocks;

		public Label Hits;

		public Label StarPoints;
	}

	private sealed class TeamTooltipLabels
	{
		public VisualElement Tooltip;

		public Label Title;

		public Label ShotsOnGoal;

		public Label FaceoffPercent;

		public Label Passes;

		public Label Takeaways;

		public Label DefensiveZoneExits;

		public Label OffensiveZoneEntries;

		public Label Possession;
	}

	private static bool IsClientStatsUiAuthorized()
	{
		return _serverHasResponded && _serverSignatureConfirmed && !_serverStatsCompatibilityDisabled;
	}

	private static void EnsureClientScoreboardState()
	{
		if (_serverStatsCompatibilityDisabled)
		{
			ApplyScoreboardEnabledState(enable: false);
			return;
		}
		if (NetworkManager.Singleton == null || NetworkManager.Singleton.CustomMessagingManager == null)
		{
			return;
		}
		if (!_hasRegisteredWithNamedMessageHandler || !_serverHasResponded)
		{
			CustomMessagingManager customMessagingManager = NetworkManager.Singleton.CustomMessagingManager;
			if (!_hasRegisteredWithNamedMessageHandler || !ReferenceEquals(_registeredNamedMessageHandlerManager, customMessagingManager) || !string.Equals(_registeredNamedMessageHandlerName, "oomtm450_stats_server", StringComparison.Ordinal))
			{
				try
				{
					if (_registeredNamedMessageHandlerManager != null && !string.IsNullOrEmpty(_registeredNamedMessageHandlerName))
					{
						_registeredNamedMessageHandlerManager.UnregisterNamedMessageHandler(_registeredNamedMessageHandlerName);
					}
				}
				catch (Exception arg)
				{
					Logging.LogError($"Error unregistering previous named message handler {_registeredNamedMessageHandlerName}: {arg}", _clientConfig);
				}
				Logging.Log("RegisterNamedMessageHandler oomtm450_stats_server.", _clientConfig);
				customMessagingManager.RegisterNamedMessageHandler("oomtm450_stats_server", ReceiveData);
				_hasRegisteredWithNamedMessageHandler = true;
				_registeredNamedMessageHandlerManager = customMessagingManager;
				_registeredNamedMessageHandlerName = "oomtm450_stats_server";
			}
			DateTime utcNow = DateTime.UtcNow;
			if (_lastDateTimeAskStartupData + TimeSpan.FromSeconds(1.0) < utcNow && _askServerForStartupDataCount++ < 10)
			{
				_lastDateTimeAskStartupData = utcNow;
				NetworkCommunication.SendData("oomtm450_statsASKDATA", "1", 0uL, "oomtm450_stats_client", _clientConfig);
			}
		}
		else if (_askForKick)
		{
			_askForKick = false;
			SendSystemChatMessage("Outdated client stat mod version. Please resubscribe from Stats Tooltip UI <a href=\"https://steamcommunity.com/sharedfiles/filedetails/?id=3608968085\">[Link]</a> in the workshop and restart your game to update.");
			NetworkCommunication.SendData("oomtm450_stats_kick", "1", 0uL, "oomtm450_stats_client", _clientConfig);
		}
		else if (_addServerModVersionOutOfDateMessage)
		{
			_addServerModVersionOutOfDateMessage = false;
			SendSystemChatMessage("Server's Stats mod is out of date. Some functionalities might not work properly.");
		}
		if (IsClientStatsUiAuthorized())
		{
			ApplyScoreboardEnabledState(enable: true);
			TryShowBoxscoreHintPromptForJoin();
		}
		else
		{
			ApplyScoreboardEnabledState(enable: false);
		}
	}

	private static bool NeedsScoreboardFullRefresh()
	{
		VisualElement scoreboardContainer = GetScoreboardContainer();
		Dictionary<Player, VisualElement> scoreboardPlayerMap = GetScoreboardPlayerMap();
		if (scoreboardContainer == null || scoreboardPlayerMap == null)
		{
			return false;
		}
		VisualElement scoreboardHeaderRoot = GetScoreboardHeaderLabelContainer(scoreboardContainer);
		if (scoreboardHeaderRoot == null || QueryNamedElement<VisualElement>(scoreboardHeaderRoot, SOG_HEADER_CELL_NAME) == null || QueryNamedElement<Label>(scoreboardHeaderRoot, SOG_HEADER_LABEL_NAME) == null)
		{
			return true;
		}
		foreach (KeyValuePair<Player, VisualElement> item in scoreboardPlayerMap)
		{
			if (item.Key == null || !item.Key)
			{
				continue;
			}
			VisualElement scoreboardPlayerRoot = GetScoreboardPlayerRoot(item.Value);
			if (scoreboardPlayerRoot != null && (QueryNamedElement<VisualElement>(scoreboardPlayerRoot, SOG_CELL_NAME) == null || QueryNamedElement<Label>(scoreboardPlayerRoot, SOG_LABEL) == null))
			{
				return true;
			}
		}
		return false;
	}

	private static void ApplyScoreboardEnabledState(bool enable, bool force = false)
	{
		if (!force && _scoreboardUiEnabled == enable)
		{
			if (!enable)
			{
				return;
			}
			if (!NeedsScoreboardFullRefresh())
			{
				return;
			}
		}
		ScoreboardModifications(enable);
	}

	private static bool ShouldScheduleScoreboardSogFollowUp(string playerSteamId, double minIntervalMilliseconds = 120.0)
	{
		if (string.IsNullOrWhiteSpace(playerSteamId))
		{
			return true;
		}
		DateTime utcNow = DateTime.UtcNow;
		if (_scoreboardSogFollowUpScheduledAt.TryGetValue(playerSteamId, out var value) && utcNow - value < TimeSpan.FromMilliseconds(minIntervalMilliseconds))
		{
			return false;
		}
		_scoreboardSogFollowUpScheduledAt.AddOrUpdate(playerSteamId, utcNow);
		return true;
	}

	private static void RefreshClientScoreboardVisibleState(string reason, bool scheduleFollowUp, bool forceResetTracking)
	{
		try
		{
			if (ServerFunc.IsDedicatedServer())
			{
				return;
			}
			UIScoreboard uIScoreboard = GetUIScoreboard();
			if (uIScoreboard == null)
			{
				Logging.Log($"Scoreboard visible refresh skipped ({reason}): UIScoreboard not available yet.", _clientConfig);
				return;
			}
			if (forceResetTracking)
			{
				ResetClientScoreboardTrackingForVisibleRefresh(reason);
			}
			EnsureClientScoreboardState();
			VisualElement scoreboardContainer = GetScoreboardContainer();
			Dictionary<Player, VisualElement> scoreboardPlayerMap = GetScoreboardPlayerMap();
			if (scoreboardContainer == null || scoreboardPlayerMap == null)
			{
				Logging.Log($"Scoreboard visible refresh incomplete ({reason}): container={(scoreboardContainer != null)}, playerMap={(scoreboardPlayerMap != null)}.", _clientConfig);
			}
			else
			{
				VisualElement scoreboardHeaderRoot = GetScoreboardHeaderRoot(scoreboardContainer);
				bool flag = scoreboardHeaderRoot != null && QueryNamedElement<VisualElement>(scoreboardHeaderRoot, SOG_HEADER_LABEL_NAME) != null;
				int num = CountLiveScoreboardSogLabels(scoreboardPlayerMap);
				if (IsClientStatsUiAuthorized() && (!flag || num < scoreboardPlayerMap.Count))
				{
					ApplyScoreboardEnabledState(enable: true, force: true);
					scoreboardHeaderRoot = GetScoreboardHeaderRoot(scoreboardContainer);
					flag = scoreboardHeaderRoot != null && QueryNamedElement<VisualElement>(scoreboardHeaderRoot, SOG_HEADER_LABEL_NAME) != null;
					num = CountLiveScoreboardSogLabels(scoreboardPlayerMap);
				}
				Logging.Log($"Scoreboard visible refresh applied ({reason}). header={flag}, rows={scoreboardPlayerMap.Count}, sogLabels={num}, serverResponded={_serverHasResponded}.", _clientConfig);
				if (scheduleFollowUp)
				{
					ScheduleClientScoreboardVisibleRefresh(scoreboardContainer, reason);
				}
			}
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error in RefreshClientScoreboardVisibleState ({reason}).\n{arg}", _clientConfig);
		}
	}

	private static void ResetClientScoreboardTrackingForVisibleRefresh(string reason)
	{
		int count = _hasUpdatedUIScoreboard.Count;
		int count2 = _sogLabels.Count;
		int count3 = _playerTooltips.Count;
		int count4 = _teamTooltips.Count;
		foreach (string item in new List<string>(_playerTooltips.Keys))
		{
			RemovePlayerTooltip(item);
		}
		RemoveTeamTooltips();
		_sogLabels.Clear();
		_scoreboardSogFollowUpScheduledAt.Clear();
		_scoreboardRowHovered.Clear();
		_hasUpdatedUIScoreboard.Clear();
		Logging.Log($"Scoreboard visible refresh reset ({reason}). cleared trackedRows={count}, sogLabelCache={count2}, playerTooltips={count3}, teamTooltips={count4}.", _clientConfig);
	}

	private static void ScheduleClientScoreboardVisibleRefresh(VisualElement scoreboardContainer, string reason)
	{
		VisualElement visualElement = scoreboardContainer ?? GetUIScoreboard()?.View ?? MonoBehaviourSingleton<UIManager>.Instance?.RootVisualElement;
		if (visualElement == null)
		{
			Logging.Log($"Scoreboard follow-up refresh skipped ({reason}): no live visual element to schedule on.", _clientConfig);
			return;
		}
		visualElement.schedule.Execute((Action)delegate
		{
			RefreshClientScoreboardVisibleState(reason + " followup-1", scheduleFollowUp: false, forceResetTracking: false);
		}).ExecuteLater(75L);
		visualElement.schedule.Execute((Action)delegate
		{
			RefreshClientScoreboardVisibleState(reason + " followup-2", scheduleFollowUp: false, forceResetTracking: false);
		}).ExecuteLater(175L);
	}

	private static int CountLiveScoreboardSogLabels(Dictionary<Player, VisualElement> scoreboardPlayerMap)
	{
		if (scoreboardPlayerMap == null)
		{
			return 0;
		}
		int num = 0;
		foreach (KeyValuePair<Player, VisualElement> item in scoreboardPlayerMap)
		{
			VisualElement scoreboardPlayerRoot = GetScoreboardPlayerRoot(item.Value);
			if (scoreboardPlayerRoot != null && QueryNamedElement<Label>(scoreboardPlayerRoot, SOG_LABEL) != null)
			{
				num++;
			}
		}
		return num;
	}

	private static void TryApplyScoreboardStarTag(Player player)
	{
		if (player == null || !player)
		{
			return;
		}
		string text = player.SteamId.Value.ToString();
		if (string.IsNullOrEmpty(text) || !_stars.Values.Contains(text))
		{
			return;
		}
		Dictionary<Player, VisualElement> scoreboardPlayerMap = GetScoreboardPlayerMap();
		if (scoreboardPlayerMap == null || !scoreboardPlayerMap.TryGetValue(player, out var value) || value == null)
		{
			return;
		}
		Label scoreboardUsernameLabel = GetScoreboardUsernameLabel(value, player);
		if (scoreboardUsernameLabel != null)
		{
			scoreboardUsernameLabel.text = string.Concat(str1: StripStarTags(scoreboardUsernameLabel.text), str0: GetStarTag(text));
		}
	}

	private static T QueryNamedElement<T>(VisualElement root, string name) where T : VisualElement
	{
		if (root == null || string.IsNullOrEmpty(name))
		{
			return null;
		}
		return root.Q<T>(name);
	}

	private static Label GetPlayerTooltipLabel(VisualElement tooltip, string labelName, string playerSteamId)
	{
		Label cachedPlayerTooltipLabel = GetCachedPlayerTooltipLabel(tooltip, labelName, playerSteamId);
		if (cachedPlayerTooltipLabel != null)
		{
			return cachedPlayerTooltipLabel;
		}
		cachedPlayerTooltipLabel = GetCachedTeamTooltipLabel(tooltip, labelName, playerSteamId);
		if (cachedPlayerTooltipLabel != null)
		{
			return cachedPlayerTooltipLabel;
		}
		return QueryNamedElement<Label>(tooltip, labelName + "_" + playerSteamId);
	}

	private static Label GetCachedPlayerTooltipLabel(VisualElement tooltip, string labelName, string playerSteamId)
	{
		if (tooltip == null || string.IsNullOrWhiteSpace(labelName) || string.IsNullOrWhiteSpace(playerSteamId) || !_playerTooltipLabels.TryGetValue(playerSteamId, out var value) || value == null || !ReferenceEquals(value.Tooltip, tooltip))
		{
			return null;
		}
		switch (labelName)
		{
		case "TooltipTitle":
			return value.Title;
		case "TooltipSVPercent":
			return value.SavePercent;
		case "TooltipExpectedGoalsAgainst":
			return value.ExpectedGoalsAgainst;
		case "TooltipGoalsSavedAboveExpected":
			return value.GoalsSavedAboveExpected;
		case "TooltipBodySaves":
			return value.BodySaves;
		case "TooltipPadSaves":
			return value.PadSaves;
		case "TooltipStickSaves":
			return value.StickSaves;
		case "TooltipExpectedGoals":
			return value.ExpectedGoals;
		case "TooltipICF":
			return value.IndividualCorsiFor;
		case "TooltipICFPercent":
			return value.IndividualCorsiForPercent;
		case "TooltipPasses":
			return value.Passes;
		case "TooltipExitsEntries":
			return value.ExitsEntries;
		case "TooltipPossessionTime":
			return value.PossessionTime;
		case "TooltipTakeaways":
			return value.Takeaways;
		case "TooltipTurnovers":
			return value.Turnovers;
		case "TooltipBlocks":
			return value.Blocks;
		case "TooltipHits":
			return value.Hits;
		case "TooltipStarPoints":
			return value.StarPoints;
		default:
			return null;
		}
	}

	private static PlayerTooltipLabels GetOrCreatePlayerTooltipLabels(VisualElement tooltip, string playerSteamId)
	{
		if (tooltip == null || string.IsNullOrWhiteSpace(playerSteamId))
		{
			return null;
		}
		if (_playerTooltipLabels.TryGetValue(playerSteamId, out var value) && value != null && ReferenceEquals(value.Tooltip, tooltip))
		{
			return value;
		}
		value = new PlayerTooltipLabels
		{
			Tooltip = tooltip,
			Title = QueryNamedElement<Label>(tooltip, "TooltipTitle_" + playerSteamId),
			SavePercent = QueryNamedElement<Label>(tooltip, "TooltipSVPercent_" + playerSteamId),
			ExpectedGoalsAgainst = QueryNamedElement<Label>(tooltip, "TooltipExpectedGoalsAgainst_" + playerSteamId),
			GoalsSavedAboveExpected = QueryNamedElement<Label>(tooltip, "TooltipGoalsSavedAboveExpected_" + playerSteamId),
			BodySaves = QueryNamedElement<Label>(tooltip, "TooltipBodySaves_" + playerSteamId),
			PadSaves = QueryNamedElement<Label>(tooltip, "TooltipPadSaves_" + playerSteamId),
			StickSaves = QueryNamedElement<Label>(tooltip, "TooltipStickSaves_" + playerSteamId),
			ExpectedGoals = QueryNamedElement<Label>(tooltip, "TooltipExpectedGoals_" + playerSteamId),
			IndividualCorsiFor = QueryNamedElement<Label>(tooltip, "TooltipICF_" + playerSteamId),
			IndividualCorsiForPercent = QueryNamedElement<Label>(tooltip, "TooltipICFPercent_" + playerSteamId),
			Passes = QueryNamedElement<Label>(tooltip, "TooltipPasses_" + playerSteamId),
			ExitsEntries = QueryNamedElement<Label>(tooltip, "TooltipExitsEntries_" + playerSteamId),
			PossessionTime = QueryNamedElement<Label>(tooltip, "TooltipPossessionTime_" + playerSteamId),
			Takeaways = QueryNamedElement<Label>(tooltip, "TooltipTakeaways_" + playerSteamId),
			Turnovers = QueryNamedElement<Label>(tooltip, "TooltipTurnovers_" + playerSteamId),
			Blocks = QueryNamedElement<Label>(tooltip, "TooltipBlocks_" + playerSteamId),
			Hits = QueryNamedElement<Label>(tooltip, "TooltipHits_" + playerSteamId),
			StarPoints = QueryNamedElement<Label>(tooltip, "TooltipStarPoints_" + playerSteamId)
		};
		_playerTooltipLabels[playerSteamId] = value;
		return value;
	}

	private static Label GetCachedTeamTooltipLabel(VisualElement tooltip, string labelName, string teamName)
	{
		if (tooltip == null || string.IsNullOrWhiteSpace(labelName) || string.IsNullOrWhiteSpace(teamName))
		{
			return null;
		}
		PlayerTeam key;
		if (string.Equals(teamName, PlayerTeam.Red.ToString(), StringComparison.Ordinal))
		{
			key = PlayerTeam.Red;
		}
		else if (string.Equals(teamName, PlayerTeam.Blue.ToString(), StringComparison.Ordinal))
		{
			key = PlayerTeam.Blue;
		}
		else
		{
			return null;
		}
		if (!_teamTooltipLabels.TryGetValue(key, out var value) || value == null || !ReferenceEquals(value.Tooltip, tooltip))
		{
			return null;
		}
		switch (labelName)
		{
		case "TooltipTeamSOGs":
			return value.ShotsOnGoal;
		case "TooltipTeamFaceoffPct":
			return value.FaceoffPercent;
		case "TooltipTeamPasses":
			return value.Passes;
		case "TooltipTeamTakeaways":
			return value.Takeaways;
		case "TooltipTeamDZExits":
			return value.DefensiveZoneExits;
		case "TooltipTeamOZEntries":
			return value.OffensiveZoneEntries;
		case "TooltipTeamPossession":
			return value.Possession;
		default:
			return null;
		}
	}

	private static TeamTooltipLabels GetOrCreateTeamTooltipLabels(VisualElement tooltip, PlayerTeam team)
	{
		if (tooltip == null)
		{
			return null;
		}
		if (_teamTooltipLabels.TryGetValue(team, out var value) && value != null && ReferenceEquals(value.Tooltip, tooltip))
		{
			return value;
		}
		string text = team.ToString();
		value = new TeamTooltipLabels
		{
			Tooltip = tooltip,
			Title = QueryNamedElement<Label>(tooltip, "TooltipTeamTitle_" + text),
			ShotsOnGoal = QueryNamedElement<Label>(tooltip, "TooltipTeamSOGs_" + text),
			FaceoffPercent = QueryNamedElement<Label>(tooltip, "TooltipTeamFaceoffPct_" + text),
			Passes = QueryNamedElement<Label>(tooltip, "TooltipTeamPasses_" + text),
			Takeaways = QueryNamedElement<Label>(tooltip, "TooltipTeamTakeaways_" + text),
			DefensiveZoneExits = QueryNamedElement<Label>(tooltip, "TooltipTeamDZExits_" + text),
			OffensiveZoneEntries = QueryNamedElement<Label>(tooltip, "TooltipTeamOZEntries_" + text),
			Possession = QueryNamedElement<Label>(tooltip, "TooltipTeamPossession_" + text)
		};
		_teamTooltipLabels[team] = value;
		return value;
	}

	private static VisualElement GetScoreboardHeaderRoot(VisualElement scoreboardContainer)
	{
		if (scoreboardContainer == null)
		{
			return null;
		}
		VisualElement visualElement = QueryNamedElement<VisualElement>(scoreboardContainer, "Header");
		if (visualElement != null)
		{
			return visualElement;
		}
		foreach (VisualElement item in scoreboardContainer.Children())
		{
			if (item == null)
			{
				continue;
			}
			if (QueryNamedElement<Label>(item, "NameLabel") != null || QueryNamedElement<Label>(item, "PlayersLabel") != null || QueryNamedElement<Label>(item, "GoalsLabel") != null)
			{
				return item;
			}
			if (item.childCount == 1)
			{
				VisualElement visualElement2 = item.Children().FirstOrDefault();
				if (visualElement2 != null && (QueryNamedElement<Label>(visualElement2, "NameLabel") != null || QueryNamedElement<Label>(visualElement2, "GoalsLabel") != null))
				{
					return visualElement2;
				}
			}
		}
		return scoreboardContainer.Children().ElementAtOrDefault(1)?.Children().FirstOrDefault() ?? scoreboardContainer.Children().FirstOrDefault();
	}

	private static VisualElement GetScoreboardHeaderLabelContainer(VisualElement scoreboardContainer)
	{
		if (scoreboardContainer == null)
		{
			return null;
		}
		VisualElement visualElement = scoreboardContainer.Children().ElementAtOrDefault(1)?.Children().ElementAtOrDefault(0);
		if (visualElement != null && (QueryNamedElement<Label>(visualElement, "NameLabel") != null || QueryNamedElement<Label>(visualElement, "PlayersLabel") != null || QueryNamedElement<Label>(visualElement, "GoalsLabel") != null))
		{
			return visualElement;
		}
		VisualElement scoreboardHeaderRoot = GetScoreboardHeaderRoot(scoreboardContainer);
		if (scoreboardHeaderRoot == null)
		{
			return null;
		}
		if (scoreboardHeaderRoot.childCount == 1)
		{
			VisualElement visualElement2 = scoreboardHeaderRoot.Children().FirstOrDefault();
			if (visualElement2 != null && (QueryNamedElement<Label>(visualElement2, "NameLabel") != null || QueryNamedElement<Label>(visualElement2, "PlayersLabel") != null || QueryNamedElement<Label>(visualElement2, "GoalsLabel") != null))
			{
				return visualElement2;
			}
		}
		return scoreboardHeaderRoot;
	}

	private static VisualElement GetScoreboardPlayerRoot(VisualElement playerVisualElement)
	{
		if (playerVisualElement == null)
		{
			return null;
		}
		VisualElement visualElement = QueryNamedElement<VisualElement>(playerVisualElement, "Player");
		if (visualElement != null)
		{
			return visualElement;
		}
		return playerVisualElement.childCount switch
		{
			0 => playerVisualElement,
			1 => playerVisualElement.Children().First(),
			_ => playerVisualElement.Children().FirstOrDefault() ?? playerVisualElement,
		};
	}

	private static VisualElement GetScoreboardPlayerStatSlot(VisualElement playerRoot, string slotName, string labelName)
	{
		if (playerRoot == null)
		{
			return null;
		}
		foreach (VisualElement item in playerRoot.Children())
		{
			if (item == null)
			{
				continue;
			}
			if (string.Equals(item.name, slotName, StringComparison.Ordinal) || string.Equals(item.name, labelName, StringComparison.Ordinal))
			{
				return item;
			}
		}
		VisualElement visualElement = QueryNamedElement<VisualElement>(playerRoot, slotName);
		if (visualElement != null)
		{
			return visualElement;
		}
		VisualElement visualElement2 = QueryNamedElement<VisualElement>(playerRoot, labelName);
		if (visualElement2?.parent != null && visualElement2.parent != playerRoot)
		{
			return visualElement2.parent;
		}
		return visualElement2;
	}

	private static VisualElement GetScoreboardHeaderStatSlot(VisualElement headerRoot, string slotName, string labelName)
	{
		if (headerRoot == null)
		{
			return null;
		}
		foreach (VisualElement item in headerRoot.Children())
		{
			if (item == null)
			{
				continue;
			}
			if (string.Equals(item.name, slotName, StringComparison.Ordinal) || string.Equals(item.name, labelName, StringComparison.Ordinal))
			{
				return item;
			}
		}
		VisualElement visualElement = QueryNamedElement<VisualElement>(headerRoot, slotName);
		if (visualElement != null)
		{
			return visualElement;
		}
		VisualElement visualElement2 = QueryNamedElement<VisualElement>(headerRoot, labelName);
		if (visualElement2?.parent != null && visualElement2.parent != headerRoot)
		{
			return visualElement2.parent;
		}
		return visualElement2;
	}

	private static (VisualElement, Label) CreateScoreboardStatCell(string cellName, string labelName, string text)
	{
		VisualElement visualElement = new VisualElement
		{
			name = cellName
		};
		visualElement.pickingMode = PickingMode.Ignore;
		visualElement.style.flexShrink = 0f;
		visualElement.style.flexGrow = 0f;
		visualElement.style.alignItems = Align.Stretch;
		visualElement.style.justifyContent = Justify.Center;
		visualElement.style.overflow = Overflow.Visible;
		Label label = new Label(text)
		{
			name = labelName
		};
		label.pickingMode = PickingMode.Ignore;
		label.style.fontSize = 13f;
		label.style.color = new Color(1f, 1f, 1f, 1f);
		label.style.unityFontStyleAndWeight = FontStyle.Normal;
		label.style.unityTextAlign = TextAnchor.UpperRight;
		label.style.display = DisplayStyle.Flex;
		label.style.visibility = Visibility.Visible;
		label.style.opacity = 1f;
		label.style.width = Length.Percent(100f);
		label.style.minWidth = 0f;
		label.style.flexGrow = 1f;
		label.style.flexShrink = 0f;
		label.style.whiteSpace = WhiteSpace.NoWrap;
		label.style.overflow = Overflow.Visible;
		label.style.textOverflow = TextOverflow.Clip;
		visualElement.Add(label);
		return (visualElement, label);
	}

	private static float GetPlayerTooltipBodyFontSize(float defaultFontSize = 13f)
	{
		return Mathf.Max(19.55f, defaultFontSize * 1.15f);
	}

	private static float GetPlayerTooltipTitleFontSize(float defaultFontSize = 13f)
	{
		return GetPlayerTooltipBodyFontSize(defaultFontSize) * 1.15f;
	}

	private static void ApplyPlayerTooltipFontSizes(VisualElement tooltip, string playerSteamId, float defaultFontSize = 15f)
	{
		if (tooltip == null)
		{
			return;
		}
		float playerTooltipBodyFontSize = GetPlayerTooltipBodyFontSize(Mathf.Max(defaultFontSize, 20f));
		float playerTooltipTitleFontSize = GetPlayerTooltipTitleFontSize(Mathf.Max(defaultFontSize, 20f));
		foreach (Label item in tooltip.Children().OfType<Label>())
		{
			if (item == null)
			{
				continue;
			}
			bool flag = string.Equals(item.name, "TooltipTitle_" + playerSteamId, StringComparison.Ordinal);
			item.style.fontSize = (flag ? playerTooltipTitleFontSize : playerTooltipBodyFontSize);
			item.style.color = new StyleColor(new Color(1f, 1f, 1f, 1f));
			item.style.opacity = 1f;
			item.style.visibility = Visibility.Visible;
			item.style.display = DisplayStyle.Flex;
		}
	}

	private static void RefreshVisiblePlayerTooltipNow(string playerSteamId)
	{
		if (string.IsNullOrWhiteSpace(playerSteamId))
		{
			return;
		}
		if (!IsScoreboardCurrentlyVisible())
		{
			HideVisibleScoreboardTooltips(clearHoveredRows: true);
			return;
		}
		if (!_playerTooltips.TryGetValue(playerSteamId, out var value) || value == null || value.parent == null || value.style.display != DisplayStyle.Flex)
		{
			return;
		}
		Player playerBySteamId = MonoBehaviourSingleton<PlayerManager>.Instance?.GetPlayerBySteamId(playerSteamId);
		if (playerBySteamId == null || !playerBySteamId)
		{
			return;
		}
		UpdateTooltipStats(value, playerSteamId, playerBySteamId);
		value.MarkDirtyRepaint();
	}

	private static bool IsTrackedPlayerTooltip(string playerSteamId, VisualElement tooltip)
	{
		if (string.IsNullOrWhiteSpace(playerSteamId) || tooltip == null)
		{
			return false;
		}
		return _playerTooltips.TryGetValue(playerSteamId, out var value) && ReferenceEquals(value, tooltip);
	}

	private static bool IsTrackedTeamTooltip(PlayerTeam team, VisualElement tooltip)
	{
		if (tooltip == null)
		{
			return false;
		}
		return _teamTooltips.TryGetValue(team, out var value) && ReferenceEquals(value, tooltip);
	}

	private static bool IsScoreboardCurrentlyVisible()
	{
		VisualElement scoreboardContainer = GetScoreboardContainer();
		if (scoreboardContainer == null || scoreboardContainer.panel == null)
		{
			return false;
		}
		Rect worldBound = scoreboardContainer.worldBound;
		return worldBound.width > 1f && worldBound.height > 1f;
	}

	private static void HideVisibleScoreboardTooltips(bool clearHoveredRows)
	{
		if (clearHoveredRows) _scoreboardRowHovered.Clear();
		if (_tooltipsAlreadyHidden) return;
		_tooltipsAlreadyHidden = true;
		foreach (KeyValuePair<string, VisualElement> item in _playerTooltips)
		{
			if (item.Value != null)
			{
				item.Value.style.display = DisplayStyle.None;
			}
		}
		foreach (KeyValuePair<PlayerTeam, VisualElement> item2 in _teamTooltips)
		{
			if (item2.Value != null)
			{
				item2.Value.style.display = DisplayStyle.None;
			}
		}
		if (clearHoveredRows)
		{
			_scoreboardRowHovered.Clear();
		}
	}

	private static void RefreshVisibleTeamTooltipsNow()
	{
		if (!IsScoreboardCurrentlyVisible())
		{
			HideVisibleScoreboardTooltips(clearHoveredRows: true);
			return;
		}
		foreach (KeyValuePair<PlayerTeam, VisualElement> teamTooltip in _teamTooltips)
		{
			if (teamTooltip.Value != null && teamTooltip.Value.parent != null && teamTooltip.Value.style.display == DisplayStyle.Flex)
			{
				UpdateTeamTooltipStats(teamTooltip.Value, teamTooltip.Key);
				teamTooltip.Value.MarkDirtyRepaint();
			}
		}
	}

	private static void ScheduleVisiblePlayerTooltipRefresh(string playerSteamId, VisualElement tooltip)
	{
		if (!IsTrackedPlayerTooltip(playerSteamId, tooltip) || tooltip.parent == null || tooltip.style.display != DisplayStyle.Flex) return;
		StartTooltipRefresh(tooltip,
			() => IsTrackedPlayerTooltip(playerSteamId, tooltip) && tooltip.parent != null && tooltip.style.display == DisplayStyle.Flex,
			() => RefreshVisiblePlayerTooltipNow(playerSteamId));
	}

	private static void ScheduleVisiblePlayerTooltipPositionRefresh(string playerSteamId, VisualElement tooltip, Label nameLabel)
	{
		if (!IsScoreboardCurrentlyVisible())
		{
			HideVisibleScoreboardTooltips(clearHoveredRows: true);
			return;
		}
		if (!IsTrackedPlayerTooltip(playerSteamId, tooltip) || tooltip.parent == null || tooltip.style.display != DisplayStyle.Flex)
		{
			return;
		}
		UpdateTooltipPosition(tooltip, nameLabel);
		tooltip.MarkDirtyRepaint();
	}

	private static void ScheduleVisibleTeamTooltipRefresh(PlayerTeam team, VisualElement tooltip)
	{
		if (!IsTrackedTeamTooltip(team, tooltip) || tooltip.parent == null || tooltip.style.display != DisplayStyle.Flex) return;
		StartTooltipRefresh(tooltip,
			() => IsTrackedTeamTooltip(team, tooltip) && tooltip.parent != null && tooltip.style.display == DisplayStyle.Flex,
			() => UpdateTeamTooltipStats(tooltip, team));
	}

	private static void RefreshScoreboardSogCellForPlayer(Player player, bool scheduleFollowUp = true)
	{
		if (player == null || !player || ServerFunc.IsDedicatedServer() || !IsClientStatsUiAuthorized())
		{
			return;
		}
		try
		{
			string text = player.SteamId.Value.ToString();
			if (string.IsNullOrWhiteSpace(text))
			{
				return;
			}
			Dictionary<Player, VisualElement> scoreboardPlayerMap = GetScoreboardPlayerMap();
			VisualElement value = null;
			if (scoreboardPlayerMap != null && !scoreboardPlayerMap.TryGetValue(player, out value))
			{
				foreach (KeyValuePair<Player, VisualElement> item in scoreboardPlayerMap)
				{
					if (item.Key != null && item.Key && string.Equals(item.Key.SteamId.Value.ToString(), text, StringComparison.Ordinal))
					{
						value = item.Value;
						break;
					}
				}
			}
			VisualElement scoreboardPlayerRoot = GetScoreboardPlayerRoot(value);
			if (scoreboardPlayerRoot == null)
			{
				return;
			}
			void Reapply()
			{
				VisualElement visualElement = QueryNamedElement<VisualElement>(scoreboardPlayerRoot, SOG_CELL_NAME);
				Label label = QueryNamedElement<Label>(scoreboardPlayerRoot, SOG_LABEL);
				if (visualElement == null || label == null)
				{
					ApplyScoreboardEnabledState(enable: true, force: true);
					return;
				}
				label.text = GetScoreboardDisplayText(text, player);
				ApplyScoreboardSogRowLayout(scoreboardPlayerRoot, visualElement, label, text);
				EnsureScoreboardStatCellVisible(visualElement, label);
				ApplyScoreboardScoringLabels(scoreboardPlayerRoot, text, player);
				label.MarkDirtyRepaint();
			}
			Reapply();
			if (scheduleFollowUp && ShouldScheduleScoreboardSogFollowUp(text))
			{
				scoreboardPlayerRoot.schedule.Execute((Action)delegate
				{
					Reapply();
				}).ExecuteLater(1L);
				scoreboardPlayerRoot.schedule.Execute((Action)delegate
				{
					Reapply();
				}).ExecuteLater(40L);
				scoreboardPlayerRoot.schedule.Execute((Action)delegate
				{
					Reapply();
				}).ExecuteLater(120L);
			}
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error refreshing scoreboard SOG cell for {player?.Username.Value}: {arg}", _clientConfig);
		}
	}

	private static void RegisterScoreboardRowVisibilityCallbacks(VisualElement playerRoot, VisualElement sogCell, Label sogLabel, string playerSteamId, Player player)
	{
		if (playerRoot == null || sogCell == null || sogLabel == null || string.IsNullOrWhiteSpace(playerSteamId))
		{
			return;
		}
		if (sogCell.userData as string == "row-visibility-callbacks-registered")
		{
			return;
		}
		sogCell.userData = "row-visibility-callbacks-registered";
		VisualElement visualElement = (VisualElement)(object)GetScoreboardPlayerButton(playerRoot) ?? playerRoot.parent ?? playerRoot;
		void Reapply()
		{
			sogLabel.text = GetScoreboardDisplayText(playerSteamId, player);
			ApplyScoreboardSogRowLayout(playerRoot, sogCell, sogLabel, playerSteamId);
			EnsureScoreboardStatCellVisible(sogCell, sogLabel);
			ApplyScoreboardScoringLabels(playerRoot, playerSteamId, player);
			sogLabel.MarkDirtyRepaint();
		}
		void ScheduleReapply(VisualElement anchor)
		{
			if (anchor == null)
			{
				Reapply();
				return;
			}
			Reapply();
			if (!ShouldScheduleScoreboardSogFollowUp(playerSteamId))
			{
				return;
			}
			anchor.schedule.Execute((Action)delegate
			{
				Reapply();
			}).ExecuteLater(1L);
			anchor.schedule.Execute((Action)delegate
			{
				Reapply();
			}).ExecuteLater(40L);
			anchor.schedule.Execute((Action)delegate
			{
				Reapply();
			}).ExecuteLater(120L);
		}
		void Attach(VisualElement target)
		{
			if (target == null)
			{
				return;
			}
			target.RegisterCallback<MouseEnterEvent>( delegate
			{
				ScheduleReapply(target);
			});
			target.RegisterCallback<PointerEnterEvent>( delegate
			{
				ScheduleReapply(target);
			});
			target.RegisterCallback<MouseLeaveEvent>( delegate
			{
				ScheduleReapply(target);
			});
			target.RegisterCallback<PointerLeaveEvent>( delegate
			{
				ScheduleReapply(target);
			});
		}
		if (visualElement != null)
		{
			visualElement.RegisterCallback<MouseEnterEvent>(delegate
			{
				_scoreboardRowHovered[playerSteamId] = true;
				ScheduleReapply(visualElement);
			});
			visualElement.RegisterCallback<PointerEnterEvent>(delegate
			{
				_scoreboardRowHovered[playerSteamId] = true;
				ScheduleReapply(visualElement);
			});
			visualElement.RegisterCallback<MouseLeaveEvent>(delegate
			{
				_scoreboardRowHovered[playerSteamId] = false;
				ScheduleReapply(visualElement);
			});
			visualElement.RegisterCallback<PointerLeaveEvent>(delegate
			{
				_scoreboardRowHovered[playerSteamId] = false;
				ScheduleReapply(visualElement);
			});
		}
		Attach(playerRoot);
		Attach(GetScoreboardPlayerButton(playerRoot));
		Attach(playerRoot.parent);
		Attach(sogCell);
		Attach(sogLabel);
	}

	private static void RegisterScoreboardLayoutReapplyCallback(VisualElement observedElement, Action reapply)
	{
		if (observedElement == null || reapply == null)
		{
			return;
		}
		if (_scoreboardLayoutCallbacks.TryGetValue(observedElement, out var existing))
		{
			existing.Reapply = reapply;
			return;
		}
		var callback = new ScoreboardLayoutCallback { Reapply = reapply };
		_scoreboardLayoutCallbacks.Add(observedElement, callback);
		observedElement.RegisterCallback<GeometryChangedEvent>(_ => callback.Reapply());
	}

	private static void EnsureScoreboardStatCellVisible(VisualElement cell, Label label, string textOverride = null, bool bold = false)
	{
		if (cell == null || label == null)
		{
			return;
		}
		cell.style.display = DisplayStyle.Flex;
		cell.style.visibility = Visibility.Visible;
		cell.style.opacity = 1f;
		cell.style.overflow = Overflow.Visible;
		label.style.display = DisplayStyle.Flex;
		label.style.visibility = Visibility.Visible;
		label.style.opacity = 1f;
		label.style.unityFontStyleAndWeight = (bold ? FontStyle.Bold : FontStyle.Normal);
		label.style.width = Length.Percent(100f);
		label.style.flexGrow = 1f;
		label.style.flexShrink = 0f;
		label.style.overflow = Overflow.Visible;
		label.style.textOverflow = TextOverflow.Clip;
		if (label.resolvedStyle.color.a <= 0f)
		{
			label.style.color = new Color(1f, 1f, 1f, 1f);
		}
		if (!string.IsNullOrEmpty(textOverride))
		{
			label.text = textOverride;
		}
	}

	private static Color GetReadableScoreboardStatColor(Label referenceLabel)
	{
		Color white = new Color(1f, 1f, 1f, 1f);
		if (referenceLabel == null)
		{
			return white;
		}
		Color color = referenceLabel.resolvedStyle.color;
		if (color.a <= 0f)
		{
			return white;
		}
		float num = color.r * 0.2126f + color.g * 0.7152f + color.b * 0.0722f;
		if (num < 0.2f)
		{
			return white;
		}
		return color;
	}

	private static Color GetScoreboardSogRowColor(string playerSteamId, Label referenceLabel)
	{
		if (!string.IsNullOrWhiteSpace(playerSteamId) && _scoreboardRowHovered.TryGetValue(playerSteamId, out var value) && value)
		{
			return new Color(0f, 0f, 0f, 1f);
		}
		return GetReadableScoreboardStatColor(referenceLabel);
	}

	private static void CopyScoreboardStatCellStyle(VisualElement targetCell, Label targetLabel, VisualElement referenceCell, Label referenceLabel)
	{
		if (targetCell == null || targetLabel == null)
		{
			return;
		}
		float num = 0f;
		if (referenceCell != null)
		{
			num = referenceCell.resolvedStyle.width;
			if (num <= 1f && referenceCell.layout.width > 1f)
			{
				num = referenceCell.layout.width;
			}
			targetCell.style.flexGrow = referenceCell.resolvedStyle.flexGrow;
			targetCell.style.flexShrink = referenceCell.resolvedStyle.flexShrink;
			targetCell.style.marginLeft = referenceCell.resolvedStyle.marginLeft;
			targetCell.style.marginRight = referenceCell.resolvedStyle.marginRight;
			targetCell.style.paddingLeft = referenceCell.resolvedStyle.paddingLeft;
			targetCell.style.paddingRight = referenceCell.resolvedStyle.paddingRight;
		}
		if (num <= 1f && referenceLabel != null)
		{
			num = referenceLabel.resolvedStyle.width;
			if (num <= 1f && referenceLabel.layout.width > 1f)
			{
				num = referenceLabel.layout.width;
			}
		}
		if (num > 1f)
		{
			targetCell.style.width = num;
			targetCell.style.minWidth = num;
		}
		if (referenceLabel == null)
		{
			return;
		}
		if (referenceLabel.resolvedStyle.fontSize > 0f)
		{
			targetLabel.style.fontSize = referenceLabel.resolvedStyle.fontSize;
		}
		if (referenceLabel.resolvedStyle.color.a > 0f)
		{
			targetLabel.style.color = GetReadableScoreboardStatColor(referenceLabel);
		}
		targetLabel.style.unityFontStyleAndWeight = referenceLabel.resolvedStyle.unityFontStyleAndWeight;
		targetLabel.style.unityTextAlign = referenceLabel.resolvedStyle.unityTextAlign;
		targetLabel.style.whiteSpace = referenceLabel.resolvedStyle.whiteSpace;
	}

	private static void ApplyScoreboardSogHeaderLayout(VisualElement headerRoot, VisualElement headerCell, Label headerLabel)
	{
		if (headerRoot == null || headerCell == null || headerLabel == null)
		{
			return;
		}
		VisualElement scoreboardHeaderStatSlot = GetScoreboardHeaderStatSlot(headerRoot, "Goals", "GoalsLabel");
		Label label = QueryNamedElement<Label>(headerRoot, "GoalsLabel");
		if (label == null)
		{
			label = QueryNamedElement<Label>(scoreboardHeaderStatSlot, "GoalsLabel");
		}
		CopyScoreboardStatCellStyle(headerCell, headerLabel, scoreboardHeaderStatSlot, label);
		EnsureScoreboardStatCellVisible(headerCell, headerLabel, "S/SV%", bold: true);
		headerLabel.style.color = new Color(1f, 1f, 1f, 1f);
	}

	private static void ApplyScoreboardSogRowLayout(VisualElement playerRoot, VisualElement sogCell, Label sogLabel, string playerSteamId = null)
	{
		if (playerRoot == null || sogCell == null || sogLabel == null)
		{
			return;
		}
		VisualElement scoreboardPlayerStatSlot = GetScoreboardPlayerStatSlot(playerRoot, "Goals", "GoalsLabel");
		Label label = QueryNamedElement<Label>(playerRoot, "GoalsLabel");
		if (label == null)
		{
			label = QueryNamedElement<Label>(scoreboardPlayerStatSlot, "GoalsLabel");
		}
		CopyScoreboardStatCellStyle(sogCell, sogLabel, scoreboardPlayerStatSlot, label);
		EnsureScoreboardStatCellVisible(sogCell, sogLabel);
		sogLabel.style.color = GetScoreboardSogRowColor(playerSteamId, label);
	}

	private static Label GetScoreboardUsernameLabel(VisualElement playerVisualElement, Player player = null)
	{
		VisualElement scoreboardPlayerRoot = GetScoreboardPlayerRoot(playerVisualElement);
		Label label = QueryNamedElement<Label>(scoreboardPlayerRoot, "UsernameLabel");
		if (label != null)
		{
			return label;
		}
		if (scoreboardPlayerRoot != null)
		{
			foreach (Label item in scoreboardPlayerRoot.Query<Label>().ToList())
			{
				if (player != null && item.text != null && item.text.Contains(player.Username.Value.ToString()))
				{
					return item;
				}
			}
		}
		return null;
	}

	private static VisualElement GetScoreboardPlayerUsernameContainer(VisualElement playerRoot)
	{
		if (playerRoot == null)
		{
			return null;
		}
		VisualElement visualElement = QueryNamedElement<VisualElement>(playerRoot, "Username");
		if (visualElement != null)
		{
			return visualElement;
		}
		return playerRoot.Children().FirstOrDefault((VisualElement child) => child?.name == "Username");
	}

	private static Button GetScoreboardPlayerButton(VisualElement playerRoot)
	{
		if (playerRoot == null)
		{
			return null;
		}
		try
		{
			Button button = playerRoot.Query<Button>().First();
			if (button != null)
			{
				return button;
			}
		}
		catch
		{
		}
		try
		{
			Button button2 = playerRoot.parent?.Query<Button>().First();
			if (button2 != null)
			{
				return button2;
			}
		}
		catch
		{
		}
		return null;
	}

	private static void TranslateVisualElement(VisualElement visualElement, float deltaX, float deltaY = 0f)
	{
		if (visualElement != null)
		{
			visualElement.style.translate = new Vector3(visualElement.resolvedStyle.translate.x + deltaX, visualElement.resolvedStyle.translate.y + deltaY, visualElement.resolvedStyle.translate.z);
		}
	}

	private static void TranslateScoreboardHeaderStats(VisualElement headerRoot, float deltaX)
	{
		if (headerRoot == null)
		{
			return;
		}
		bool flag = false;
		foreach (VisualElement item in headerRoot.Children())
		{
			if (item == null)
			{
				continue;
			}
			if (item.name == "GoalsLabel" || item.name == "AssistsLabel" || item.name == "PointsLabel")
			{
				TranslateVisualElement(item, deltaX);
				flag = true;
			}
			foreach (VisualElement item2 in item.Children())
			{
				if (item2.name == "GoalsLabel" || item2.name == "AssistsLabel" || item2.name == "PointsLabel")
				{
					TranslateVisualElement(item2, deltaX);
					flag = true;
				}
			}
		}
		if (!flag)
		{
			TranslateVisualElement(QueryNamedElement<VisualElement>(headerRoot, "GoalsLabel"), deltaX);
			TranslateVisualElement(QueryNamedElement<VisualElement>(headerRoot, "AssistsLabel"), deltaX);
			TranslateVisualElement(QueryNamedElement<VisualElement>(headerRoot, "PointsLabel"), deltaX);
		}
	}

	private static void TranslateScoreboardPlayerStats(VisualElement playerRoot, float deltaForScoringColumns, float deltaForPing)
	{
		if (playerRoot == null)
		{
			return;
		}
		bool flag = false;
		foreach (VisualElement item in playerRoot.Children())
		{
			if (item == null)
			{
				continue;
			}
			if (item.name == "Goals" || item.name == "Assists" || item.name == "Points")
			{
				TranslateVisualElement(item, deltaForScoringColumns);
				flag = true;
			}
			else if (item.name == "Ping")
			{
				TranslateVisualElement(item, deltaForPing);
				flag = true;
			}
		}
		if (!flag)
		{
			TranslateVisualElement(GetScoreboardPlayerStatSlot(playerRoot, "Goals", "GoalsLabel"), deltaForScoringColumns);
			TranslateVisualElement(GetScoreboardPlayerStatSlot(playerRoot, "Assists", "AssistsLabel"), deltaForScoringColumns);
			TranslateVisualElement(GetScoreboardPlayerStatSlot(playerRoot, "Points", "PointsLabel"), deltaForScoringColumns);
			TranslateVisualElement(GetScoreboardPlayerStatSlot(playerRoot, "Ping", "PingLabel"), deltaForPing);
		}
	}

	private static void EnsureScoreboardPlayerStatsExist(string playerSteamId)
	{
		int value;
		if (!_sog.TryGetValue(playerSteamId, out value))
		{
			_sog.Add(playerSteamId, 0);
		}
		if (!_savePerc.TryGetValue(playerSteamId, out (int, int) _))
		{
			_savePerc.Add(playerSteamId, (0, 0));
		}
		if (!_hits.TryGetValue(playerSteamId, out value))
		{
			_hits.Add(playerSteamId, 0);
		}
		if (!_turnovers.TryGetValue(playerSteamId, out value))
		{
			_turnovers.Add(playerSteamId, 0);
		}
		if (!_takeaways.TryGetValue(playerSteamId, out value))
		{
			_takeaways.Add(playerSteamId, 0);
		}
		if (!_passes.TryGetValue(playerSteamId, out value))
		{
			_passes.Add(playerSteamId, 0);
		}
	}

	private static string GetScoreboardDisplayText(string playerSteamId, Player player)
	{
		if (IsPlayerGoalie(player))
		{
			return _savePerc.TryGetValue(playerSteamId, out var value) ? GetGoalieSavePerc(value.Item1, value.Item2) : GetGoalieSavePerc(0, 0);
		}
		return (_sog.TryGetValue(playerSteamId, out var value2) ? value2 : 0).ToString();
	}

	private static double GetBoxscoreHintDisplaySeconds()
	{
		return Math.Max(0.0, _clientConfig?.BoxscoreHintDisplaySeconds ?? 30.0);
	}

	private static bool EnsureBoxscoreHintPrompt()
	{
		if (ServerFunc.IsDedicatedServer())
		{
			return false;
		}
		VisualElement rootVisualElement = MonoBehaviourSingleton<UIManager>.Instance?.RootVisualElement;
		if (rootVisualElement == null)
		{
			return false;
		}
		if (_boxscoreHintContainer != null && _boxscoreHintContainer.parent == rootVisualElement && _boxscoreHintLabel != null)
		{
			return true;
		}
		RemoveBoxscoreHintPrompt();
		_boxscoreHintContainer = new VisualElement
		{
			name = "DomaStats_BoxscoreHint"
		};
		_boxscoreHintContainer.style.position = Position.Absolute;
		_boxscoreHintContainer.style.left = BOXSCORE_HINT_LEFT;
		_boxscoreHintContainer.style.bottom = BOXSCORE_HINT_BOTTOM;
		_boxscoreHintContainer.style.flexDirection = FlexDirection.Column;
		_boxscoreHintContainer.style.alignItems = Align.FlexStart;
		_boxscoreHintContainer.style.display = DisplayStyle.None;
		_boxscoreHintContainer.style.opacity = 0f;
		_boxscoreHintContainer.pickingMode = PickingMode.Ignore;
		_boxscoreHintLabel = new Label("Press F7 for Boxscore");
		_boxscoreHintLabel.style.fontSize = 14f;
		_boxscoreHintLabel.style.color = new Color(0.45f, 0.45f, 0.45f, 1f);
		_boxscoreHintLabel.style.unityTextAlign = TextAnchor.UpperLeft;
		_boxscoreHintLabel.style.whiteSpace = WhiteSpace.Normal;
		_boxscoreHintLabel.style.opacity = 1f;
		_boxscoreHintLabel.pickingMode = PickingMode.Ignore;
		_boxscoreHintContainer.Add(_boxscoreHintLabel);
		rootVisualElement.Add(_boxscoreHintContainer);
		return true;
	}

	private static void RemoveBoxscoreHintPrompt()
	{
		if (_boxscoreHintContainer != null)
		{
			_boxscoreHintContainer.RemoveFromHierarchy();
			_boxscoreHintContainer = null;
		}
		_boxscoreHintLabel = null;
	}

	private static void ResetBoxscoreHintPromptState()
	{
		_boxscoreHintShownThisJoin = false;
		_boxscoreHintShownAtUtc = DateTime.MinValue;
		_boxscoreHintPromptSequence++;
		RemoveBoxscoreHintPrompt();
	}

	private static void TryShowBoxscoreHintPromptForJoin()
	{
		if (ServerFunc.IsDedicatedServer() || _boxscoreHintShownThisJoin || !IsClientStatsUiAuthorized())
		{
			return;
		}
		if (!EnsureBoxscoreHintPrompt())
		{
			return;
		}
		_boxscoreHintShownThisJoin = true;
		_boxscoreHintShownAtUtc = DateTime.UtcNow;
		_boxscoreHintPromptSequence++;
		UpdateBoxscoreHintPromptState();
		ScheduleBoxscoreHintPromptRefresh(_boxscoreHintPromptSequence);
	}

	private static void ScheduleBoxscoreHintPromptRefresh(int promptSequence)
	{
		if (_boxscoreHintContainer == null || _boxscoreHintContainer.parent == null || promptSequence != _boxscoreHintPromptSequence)
		{
			return;
		}
		UpdateBoxscoreHintPromptState();
		if (_boxscoreHintContainer == null || _boxscoreHintContainer.parent == null || promptSequence != _boxscoreHintPromptSequence)
		{
			return;
		}
		double totalSeconds = (DateTime.UtcNow - _boxscoreHintShownAtUtc).TotalSeconds;
		if (!_boxscoreHintShownThisJoin || totalSeconds >= GetBoxscoreHintDisplaySeconds() + BOXSCORE_HINT_FADE_SECONDS)
		{
			return;
		}
		_boxscoreHintContainer.schedule.Execute((Action)delegate
		{
			ScheduleBoxscoreHintPromptRefresh(promptSequence);
		}).ExecuteLater(50L);
	}

	private static void UpdateBoxscoreHintPromptState()
	{
		if (_boxscoreHintLabel != null)
		{
			_boxscoreHintLabel.text = "Press F7 for Boxscore";
		}
		if (_boxscoreHintContainer == null)
		{
			return;
		}
		if (!_boxscoreHintShownThisJoin)
		{
			_boxscoreHintContainer.style.display = DisplayStyle.None;
			_boxscoreHintContainer.style.opacity = 0f;
			return;
		}
		double totalSeconds = (DateTime.UtcNow - _boxscoreHintShownAtUtc).TotalSeconds;
		double boxscoreHintDisplaySeconds = GetBoxscoreHintDisplaySeconds();
		if (totalSeconds >= boxscoreHintDisplaySeconds + BOXSCORE_HINT_FADE_SECONDS)
		{
			_boxscoreHintContainer.style.display = DisplayStyle.None;
			_boxscoreHintContainer.style.opacity = 0f;
			return;
		}
		if (_boxscoreVisible)
		{
			_boxscoreHintContainer.style.display = DisplayStyle.None;
			return;
		}
		float num = 1f;
		if (totalSeconds > boxscoreHintDisplaySeconds)
		{
			double num2 = (totalSeconds - boxscoreHintDisplaySeconds) / BOXSCORE_HINT_FADE_SECONDS;
			num = Mathf.Clamp01((float)(1.0 - num2));
		}
		_boxscoreHintContainer.style.display = DisplayStyle.Flex;
		_boxscoreHintContainer.style.opacity = num;
	}

	private static void ApplyScoreboardScoringLabels(VisualElement playerRoot, string playerSteamId, Player player)
	{
		if (playerRoot == null || string.IsNullOrWhiteSpace(playerSteamId) || player == null || !player)
		{
			return;
		}
		string text;
		string text2;
		string text3;
		if (TryGetClientPreviousGameScoringSummary(playerSteamId, out var goals, out var assists, out var points))
		{
			text = goals.ToString(CultureInfo.InvariantCulture);
			text2 = assists.ToString(CultureInfo.InvariantCulture);
			text3 = points.ToString(CultureInfo.InvariantCulture);
		}
		else
		{
			bool flag = player.Team == PlayerTeam.Blue || player.Team == PlayerTeam.Red;
			text = (flag ? player.Goals.Value.ToString(CultureInfo.InvariantCulture) : string.Empty);
			text2 = (flag ? player.Assists.Value.ToString(CultureInfo.InvariantCulture) : string.Empty);
			text3 = (flag ? (player.Goals.Value + player.Assists.Value).ToString(CultureInfo.InvariantCulture) : string.Empty);
		}
		SetScoreboardPlayerStatLabelText(playerRoot, "GoalsLabel", text);
		SetScoreboardPlayerStatLabelText(playerRoot, "AssistsLabel", text2);
		SetScoreboardPlayerStatLabelText(playerRoot, "PointsLabel", text3);
	}

	private static void SetScoreboardPlayerStatLabelText(VisualElement playerRoot, string labelName, string text)
	{
		if (playerRoot == null || string.IsNullOrWhiteSpace(labelName))
		{
			return;
		}
		Label label = QueryNamedElement<Label>(playerRoot, labelName);
		if (label == null)
		{
			string slotName = labelName.Replace("Label", "");
			VisualElement scoreboardPlayerStatSlot = GetScoreboardPlayerStatSlot(playerRoot, slotName, labelName);
			if (scoreboardPlayerStatSlot != null)
			{
				label = QueryNamedElement<Label>(scoreboardPlayerStatSlot, labelName);
			}
		}
		if (label == null)
		{
			return;
		}
		label.text = text ?? string.Empty;
		label.style.display = DisplayStyle.Flex;
		label.style.visibility = Visibility.Visible;
		label.style.opacity = 1f;
		label.MarkDirtyRepaint();
	}

	private static void TrySetupScoreboardTooltip(string playerSteamId, Player player, VisualElement playerRoot, Label usernameLabel, Label sogLabel)
	{
		if (string.IsNullOrEmpty(playerSteamId) || player == null || !player || playerRoot == null || usernameLabel == null)
		{
			return;
		}
		VisualElement scoreboardPlayerUsernameContainer = GetScoreboardPlayerUsernameContainer(playerRoot);
		Button scoreboardPlayerButton = GetScoreboardPlayerButton(playerRoot);
		usernameLabel.pickingMode = PickingMode.Position;
		if (scoreboardPlayerUsernameContainer != null)
		{
			scoreboardPlayerUsernameContainer.pickingMode = PickingMode.Position;
		}
		playerRoot.pickingMode = PickingMode.Position;
		if (playerRoot.parent != null)
		{
			playerRoot.parent.pickingMode = PickingMode.Position;
		}
		if (scoreboardPlayerButton != null)
		{
			scoreboardPlayerButton.pickingMode = PickingMode.Position;
		}
		float defaultFontSize = 20f;
		SetupPlayerTooltip(usernameLabel, playerRoot, playerSteamId, player, usernameLabel, defaultFontSize);
	}

	private static void RemovePlayerTooltip(string playerSteamId)
	{
		if (string.IsNullOrEmpty(playerSteamId))
		{
			return;
		}
		if (_playerTooltips.TryGetValue(playerSteamId, out var value))
		{
			DisposeTooltipCallbacks(value);
			_tooltipMoveTimes.Remove(value);
			value.style.display = DisplayStyle.None;
			value.parent?.Remove(value);
			_playerTooltips.Remove(playerSteamId);
		}
		_playerTooltipNameLabels.Remove(playerSteamId);
		_playerTooltipContainers.Remove(playerSteamId);
		_playerTooltipIsGoalie.Remove(playerSteamId);
		_playerTooltipLabels.Remove(playerSteamId);
	}

	private static void RemoveTeamTooltips()
	{
		foreach (KeyValuePair<PlayerTeam, VisualElement> item in new List<KeyValuePair<PlayerTeam, VisualElement>>(_teamTooltips))
		{
			DisposeTooltipCallbacks(item.Value);
			_tooltipMoveTimes.Remove(item.Value);
			if (item.Value != null)
			{
				item.Value.style.display = DisplayStyle.None;
			}
			item.Value?.parent?.Remove(item.Value);
			_teamTooltips.Remove(item.Key);
			_teamTooltipLabels.Remove(item.Key);
		}
		_teamTooltipLabels.Clear();
		_teamTooltipsSetup = false;
	}

	private static void ScoreboardModifications(bool enable)
	{
		_scoreboardUiEnabled = enable;
		if (GetUIScoreboard() == null)
		{
			return;
		}
		VisualElement scoreboardContainer = GetScoreboardContainer();
		Dictionary<Player, VisualElement> scoreboardPlayerMap = GetScoreboardPlayerMap();
		if (scoreboardContainer == null || scoreboardPlayerMap == null)
		{
			return;
		}
		VisualElement scoreboardHeaderRoot = GetScoreboardHeaderLabelContainer(scoreboardContainer);
		if (enable)
		{
			if (_hasUpdatedUIScoreboard.Contains("header") && scoreboardHeaderRoot != null && QueryNamedElement<VisualElement>(scoreboardHeaderRoot, SOG_HEADER_CELL_NAME) == null)
			{
				_hasUpdatedUIScoreboard.Remove("header");
				RemoveTeamTooltips();
				Logging.Log("Scoreboard header was marked updated but live SOG header cell was missing. Rebuilding header using cloned Goals-column sizing.", _clientConfig);
			}
		if (!_hasUpdatedUIScoreboard.Contains("header") && scoreboardHeaderRoot != null)
		{
				QueryNamedElement<VisualElement>(scoreboardHeaderRoot, SOG_HEADER_CELL_NAME)?.RemoveFromHierarchy();
				(VisualElement, Label) valueTuple = CreateScoreboardStatCell(SOG_HEADER_CELL_NAME, SOG_HEADER_LABEL_NAME, "S/SV%");
				VisualElement scoreboardHeaderStatSlot = GetScoreboardHeaderStatSlot(scoreboardHeaderRoot, "Ping", "PingLabel") ?? GetScoreboardHeaderStatSlot(scoreboardHeaderRoot, "Goals", "GoalsLabel");
				if (scoreboardHeaderStatSlot != null)
				{
					scoreboardHeaderRoot.Insert(Mathf.Max(0, scoreboardHeaderRoot.IndexOf(scoreboardHeaderStatSlot)), valueTuple.Item1);
				}
				else
				{
					scoreboardHeaderRoot.Add(valueTuple.Item1);
				}
				ApplyScoreboardSogHeaderLayout(scoreboardHeaderRoot, valueTuple.Item1, valueTuple.Item2);
				RegisterScoreboardLayoutReapplyCallback(valueTuple.Item1, delegate
				{
					ApplyScoreboardSogHeaderLayout(scoreboardHeaderRoot, valueTuple.Item1, valueTuple.Item2);
				});
				RegisterScoreboardLayoutReapplyCallback(GetScoreboardHeaderStatSlot(scoreboardHeaderRoot, "Goals", "GoalsLabel"), delegate
				{
					ApplyScoreboardSogHeaderLayout(scoreboardHeaderRoot, valueTuple.Item1, valueTuple.Item2);
				});
			Logging.Log($"Applied cloned-width scoreboard header positioning. headerContainer={scoreboardHeaderRoot.name ?? "<unnamed>"}, childCount={scoreboardHeaderRoot.childCount}.", _clientConfig);
			_hasUpdatedUIScoreboard.Add("header");
		}
		else if (scoreboardHeaderRoot != null)
		{
			VisualElement visualElement2 = QueryNamedElement<VisualElement>(scoreboardHeaderRoot, SOG_HEADER_CELL_NAME);
			Label headerLabel = QueryNamedElement<Label>(scoreboardHeaderRoot, SOG_HEADER_LABEL_NAME);
			if (visualElement2 != null && headerLabel != null)
			{
				ApplyScoreboardSogHeaderLayout(scoreboardHeaderRoot, visualElement2, headerLabel);
			}
		}
		if (scoreboardHeaderRoot != null)
		{
			SetupTeamTooltips(scoreboardContainer);
			}
		}
		else if (_hasUpdatedUIScoreboard.Contains("header") && scoreboardHeaderRoot != null)
		{
			QueryNamedElement<VisualElement>(scoreboardHeaderRoot, SOG_HEADER_CELL_NAME)?.RemoveFromHierarchy();
		}
		foreach (KeyValuePair<Player, VisualElement> item in scoreboardPlayerMap)
		{
			string text = item.Key.SteamId.Value.ToString();
			if (string.IsNullOrEmpty(text))
			{
				continue;
			}
			VisualElement scoreboardPlayerRoot = GetScoreboardPlayerRoot(item.Value);
			if (scoreboardPlayerRoot == null)
			{
				continue;
			}
			VisualElement visualElement = QueryNamedElement<VisualElement>(scoreboardPlayerRoot, SOG_CELL_NAME);
			Label label = QueryNamedElement<Label>(scoreboardPlayerRoot, SOG_LABEL);
			if (!enable)
			{
				if (visualElement != null)
				{
					visualElement.RemoveFromHierarchy();
				}
				_sogLabels.Remove(text);
				_scoreboardRowHovered.Remove(text);
				_hasUpdatedUIScoreboard.Remove(text);
				RemovePlayerTooltip(text);
				continue;
			}
			EnsureScoreboardPlayerStatsExist(text);
			bool flag = _hasUpdatedUIScoreboard.Contains(text);
			if (flag && (visualElement == null || label == null))
			{
				_hasUpdatedUIScoreboard.Remove(text);
				_sogLabels.Remove(text);
				_scoreboardRowHovered.Remove(text);
				RemovePlayerTooltip(text);
				flag = false;
				Logging.Log($"Scoreboard row for {item.Key.Username.Value} ({text}) was marked updated but live SOG cell was missing. Rebuilding row.", _clientConfig);
			}
			if (!flag)
			{
				if (visualElement != null)
				{
					visualElement.RemoveFromHierarchy();
				}
				(VisualElement, Label) valueTuple2 = CreateScoreboardStatCell(SOG_CELL_NAME, SOG_LABEL, GetScoreboardDisplayText(text, item.Key));
				VisualElement scoreboardPlayerStatSlot = GetScoreboardPlayerStatSlot(scoreboardPlayerRoot, "Ping", "PingLabel") ?? GetScoreboardPlayerStatSlot(scoreboardPlayerRoot, "Goals", "GoalsLabel");
				if (scoreboardPlayerStatSlot != null)
				{
					scoreboardPlayerRoot.Insert(Mathf.Max(0, scoreboardPlayerRoot.IndexOf(scoreboardPlayerStatSlot)), valueTuple2.Item1);
				}
				else
				{
					scoreboardPlayerRoot.Add(valueTuple2.Item1);
				}
				ApplyScoreboardSogRowLayout(scoreboardPlayerRoot, valueTuple2.Item1, valueTuple2.Item2, text);
				RegisterScoreboardLayoutReapplyCallback(valueTuple2.Item1, delegate
				{
					ApplyScoreboardSogRowLayout(scoreboardPlayerRoot, valueTuple2.Item1, valueTuple2.Item2, text);
				});
				RegisterScoreboardLayoutReapplyCallback(GetScoreboardPlayerStatSlot(scoreboardPlayerRoot, "Goals", "GoalsLabel"), delegate
				{
					ApplyScoreboardSogRowLayout(scoreboardPlayerRoot, valueTuple2.Item1, valueTuple2.Item2, text);
				});
				RegisterScoreboardRowVisibilityCallbacks(scoreboardPlayerRoot, valueTuple2.Item1, valueTuple2.Item2, text, item.Key);
				_sogLabels.AddOrUpdate(text, valueTuple2.Item2);
				_hasUpdatedUIScoreboard.Add(text);
				Label scoreboardUsernameLabel = GetScoreboardUsernameLabel(item.Value, item.Key);
				TrySetupScoreboardTooltip(text, item.Key, scoreboardPlayerRoot, scoreboardUsernameLabel, valueTuple2.Item2);
				ApplyScoreboardScoringLabels(scoreboardPlayerRoot, text, item.Key);
				continue;
		}
		_sogLabels.AddOrUpdate(text, label);
		label.text = GetScoreboardDisplayText(text, item.Key);
		ApplyScoreboardSogRowLayout(scoreboardPlayerRoot, visualElement, label, text);
		ApplyScoreboardScoringLabels(scoreboardPlayerRoot, text, item.Key);
		RegisterScoreboardRowVisibilityCallbacks(scoreboardPlayerRoot, visualElement, label, text, item.Key);
		bool flag2 = IsPlayerGoalie(item.Key);
			if (_playerTooltipIsGoalie.TryGetValue(text, out var value) && value != flag2)
			{
				RemovePlayerTooltip(text);
			}
			if (!_playerTooltips.ContainsKey(text))
			{
				Label scoreboardUsernameLabel2 = GetScoreboardUsernameLabel(item.Value, item.Key);
				TrySetupScoreboardTooltip(text, item.Key, scoreboardPlayerRoot, scoreboardUsernameLabel2, label);
			}
		}
		if (enable)
		{
			ReorderScoreboardPlayers(scoreboardContainer);
			return;
		}
		_sog.Clear();
		_savePerc.Clear();
		_sogLabels.Clear();
		_scoreboardRowHovered.Clear();
		_scoreboardSogFollowUpScheduledAt.Clear();
		ResetTooltipPerformanceState();
		_playerTooltips.Clear();
		_playerTooltipNameLabels.Clear();
		_playerTooltipContainers.Clear();
		_playerTooltipIsGoalie.Clear();
		_playerTooltipLabels.Clear();
		_hasUpdatedUIScoreboard.Clear();
		_tooltipMoveTimes.Clear();
		RemoveTeamTooltips();
	}

	private static bool IsPlayerGoalie(Player player)
	{
		if (player == null || !player)
		{
			return false;
		}
		if (PlayerFunc.IsGoalie(player))
		{
			return true;
		}
		return GetPlayerPosition(player) == "G";
	}

	private static Label CreateTooltipLabel(string text, string name, string playerSteamId, Label referenceLabel, float defaultFontSize = 13f)
	{
		Label label = new Label(text)
		{
			name = name + "_" + playerSteamId
		};
		label.text = text;
		label.style.fontSize = GetPlayerTooltipBodyFontSize(defaultFontSize);
		label.style.color = new StyleColor(new Color(1f, 1f, 1f, 1f));
		label.style.unityTextAlign = TextAnchor.UpperLeft;
		label.style.whiteSpace = WhiteSpace.Normal;
		label.style.marginBottom = 2f;
		label.style.width = Length.Percent(100f);
		label.style.minHeight = 18f;
		label.style.height = StyleKeyword.Auto;
		label.style.display = DisplayStyle.Flex;
		label.style.visibility = Visibility.Visible;
		label.style.opacity = 1f;
		label.pickingMode = PickingMode.Ignore;
		label.style.textOverflow = TextOverflow.Clip;
		label.style.overflow = Overflow.Visible;
		label.schedule.Execute((Action)delegate
		{
			label.text = text;
			label.MarkDirtyRepaint();
		});
		return label;
	}

	private static Color GetColorFromSteamId(string steamId)
	{
		if (string.IsNullOrEmpty(steamId))
		{
			return new Color(0.1f, 0.1f, 0.1f, 0.95f);
		}
		switch (steamId.Trim())
		{
		case "76561198050995236":
		case "76561198068597258":
		case "76561198155889632":
		case "76561198980346669":
		case "76561199122116162":
			return new Color(0.8f, 0.4f, 0.7f, 0.95f);
		default:
			return new Color(0.1f, 0.1f, 0.1f, 0.95f);
		}
	}

	private static void SetupPlayerTooltip(Label nameLabel, VisualElement playerContainer, string playerSteamId, Player player, Label referenceLabel, float defaultFontSize = 13f)
	{
		if (_playerTooltips.TryGetValue(playerSteamId, out var value))
		{
			DisposeTooltipCallbacks(value);
			_tooltipMoveTimes.Remove(value);
			value.style.display = DisplayStyle.None;
			value.parent?.Remove(value);
			_playerTooltips.Remove(playerSteamId);
		}
		_playerTooltipNameLabels.Remove(playerSteamId);
		_playerTooltipContainers.Remove(playerSteamId);
		_playerTooltipIsGoalie.Remove(playerSteamId);
		_playerTooltipLabels.Remove(playerSteamId);
		VisualElement tooltip = new VisualElement
		{
			name = "PlayerTooltip_" + playerSteamId
		};
		tooltip.style.position = Position.Absolute;
		tooltip.style.backgroundColor = GetColorFromSteamId(playerSteamId);
		tooltip.style.borderTopWidth = 2f;
		tooltip.style.borderBottomWidth = 2f;
		tooltip.style.borderLeftWidth = 2f;
		tooltip.style.borderRightWidth = 2f;
		tooltip.style.borderTopColor = Color.white;
		tooltip.style.borderBottomColor = Color.white;
		tooltip.style.borderLeftColor = Color.white;
		tooltip.style.borderRightColor = Color.white;
		tooltip.style.paddingTop = 10f;
		tooltip.style.paddingBottom = 10f;
		tooltip.style.paddingLeft = 15f;
		tooltip.style.paddingRight = 15f;
		tooltip.style.minWidth = 280f;
		tooltip.style.width = 280f;
		tooltip.style.maxWidth = 280f;
		tooltip.style.height = StyleKeyword.Auto;
		tooltip.style.display = DisplayStyle.None;
		tooltip.style.overflow = Overflow.Visible;
		tooltip.style.flexShrink = 0f;
		tooltip.style.flexDirection = FlexDirection.Column;
		tooltip.style.alignItems = Align.FlexStart;
		tooltip.style.flexWrap = Wrap.NoWrap;
		tooltip.style.visibility = Visibility.Visible;
		tooltip.style.opacity = 1f;
		string playerTooltipDisplayName = GetPlayerTooltipDisplayName(playerSteamId, player);
		Label titleLabel = new Label(playerTooltipDisplayName);
		PlayerTooltipLabels playerTooltipLabels = new PlayerTooltipLabels
		{
			Tooltip = tooltip,
			Title = titleLabel
		};
		titleLabel.name = "TooltipTitle_" + playerSteamId;
		titleLabel.text = playerTooltipDisplayName;
		float num = GetPlayerTooltipBodyFontSize(defaultFontSize);
		titleLabel.style.fontSize = GetPlayerTooltipTitleFontSize(defaultFontSize);
		titleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
		titleLabel.style.color = new StyleColor(new Color(1f, 1f, 1f, 1f));
		titleLabel.style.unityTextAlign = TextAnchor.UpperLeft;
		titleLabel.style.whiteSpace = WhiteSpace.Normal;
		titleLabel.style.marginBottom = 8f;
		titleLabel.style.width = Length.Percent(100f);
		titleLabel.style.minHeight = 20f;
		titleLabel.style.height = StyleKeyword.Auto;
		titleLabel.style.display = DisplayStyle.Flex;
		titleLabel.style.visibility = Visibility.Visible;
		titleLabel.style.opacity = 1f;
		titleLabel.pickingMode = PickingMode.Ignore;
		titleLabel.style.textOverflow = TextOverflow.Clip;
		titleLabel.style.overflow = Overflow.Visible;
		titleLabel.schedule.Execute((Action)delegate
		{
			titleLabel.text = GetPlayerTooltipDisplayName(playerSteamId, player);
			titleLabel.MarkDirtyRepaint();
		});
		tooltip.Add(titleLabel);
		bool flag = UseGoalieLayoutForTooltip(playerSteamId, player);
		if (flag)
		{
			Label child = CreateTooltipLabel("SV%: 0/0 (0%)", "TooltipSVPercent", playerSteamId, referenceLabel, defaultFontSize);
			playerTooltipLabels.SavePercent = child;
			tooltip.Add(child);
			Label child2 = CreateTooltipLabel("xGA: 0.00", "TooltipExpectedGoalsAgainst", playerSteamId, referenceLabel, defaultFontSize);
			playerTooltipLabels.ExpectedGoalsAgainst = child2;
			tooltip.Add(child2);
			Label child3 = CreateTooltipLabel("GSAx: 0.00", "TooltipGoalsSavedAboveExpected", playerSteamId, referenceLabel, defaultFontSize);
			playerTooltipLabels.GoalsSavedAboveExpected = child3;
			tooltip.Add(child3);
			Label child4 = CreateTooltipLabel("Body Saves: 0", "TooltipBodySaves", playerSteamId, referenceLabel, defaultFontSize);
			playerTooltipLabels.BodySaves = child4;
			tooltip.Add(child4);
			Label child5 = CreateTooltipLabel("Pad Saves: 0", "TooltipPadSaves", playerSteamId, referenceLabel, defaultFontSize);
			playerTooltipLabels.PadSaves = child5;
			tooltip.Add(child5);
			Label child6 = CreateTooltipLabel("Stick Saves: 0", "TooltipStickSaves", playerSteamId, referenceLabel, defaultFontSize);
			playerTooltipLabels.StickSaves = child6;
			tooltip.Add(child6);
			Label child7 = CreateTooltipLabel("Poss Time: 0:00", "TooltipPossessionTime", playerSteamId, referenceLabel, defaultFontSize);
			playerTooltipLabels.PossessionTime = child7;
			tooltip.Add(child7);
			Label child8 = CreateTooltipLabel("Passes: 0", "TooltipPasses", playerSteamId, referenceLabel, defaultFontSize);
			playerTooltipLabels.Passes = child8;
			tooltip.Add(child8);
		}
		else
		{
			Label child9 = CreateTooltipLabel("xG: 0.00", "TooltipExpectedGoals", playerSteamId, referenceLabel, defaultFontSize);
			playerTooltipLabels.ExpectedGoals = child9;
			tooltip.Add(child9);
			Label child8b = CreateTooltipLabel("iCF: 0", "TooltipICF", playerSteamId, referenceLabel, defaultFontSize);
			playerTooltipLabels.IndividualCorsiFor = child8b;
			tooltip.Add(child8b);
			Label child8c = CreateTooltipLabel("iCF%: 0.0% (0/0)", "TooltipICFPercent", playerSteamId, referenceLabel, defaultFontSize);
			playerTooltipLabels.IndividualCorsiForPercent = child8c;
			tooltip.Add(child8c);
			Label child11 = CreateTooltipLabel("Passes: 0", "TooltipPasses", playerSteamId, referenceLabel, defaultFontSize);
			playerTooltipLabels.Passes = child11;
			tooltip.Add(child11);
			Label child12 = CreateTooltipLabel("Exits & Entries: 0 | 0", "TooltipExitsEntries", playerSteamId, referenceLabel, defaultFontSize);
			playerTooltipLabels.ExitsEntries = child12;
			tooltip.Add(child12);
			Label child13 = CreateTooltipLabel("Possession: 0:00", "TooltipPossessionTime", playerSteamId, referenceLabel, defaultFontSize);
			playerTooltipLabels.PossessionTime = child13;
			tooltip.Add(child13);
			Label child14 = CreateTooltipLabel("Takeaways:", "TooltipTakeaways", playerSteamId, referenceLabel, defaultFontSize);
			playerTooltipLabels.Takeaways = child14;
			tooltip.Add(child14);
			Label child15 = CreateTooltipLabel("Turnovers:", "TooltipTurnovers", playerSteamId, referenceLabel, defaultFontSize);
			playerTooltipLabels.Turnovers = child15;
			tooltip.Add(child15);
			Label child16 = CreateTooltipLabel("Blocks: 0", "TooltipBlocks", playerSteamId, referenceLabel, defaultFontSize);
			playerTooltipLabels.Blocks = child16;
			tooltip.Add(child16);
			Label child17 = CreateTooltipLabel("Hits: 0", "TooltipHits", playerSteamId, referenceLabel, defaultFontSize);
			playerTooltipLabels.Hits = child17;
			tooltip.Add(child17);
		}
		Label label = CreateTooltipLabel("Star Points: 0", "TooltipStarPoints", playerSteamId, referenceLabel, defaultFontSize);
		playerTooltipLabels.StarPoints = label;
		label.style.marginTop = 8f;
		label.style.unityFontStyleAndWeight = FontStyle.Bold;
		tooltip.Add(label);
		ApplyPlayerTooltipFontSizes(tooltip, playerSteamId, defaultFontSize);
		try
		{
			VisualElement rootVisualElement = MonoBehaviourSingleton<UIManager>.Instance?.RootVisualElement;
			if (rootVisualElement != null)
			{
				rootVisualElement.Add(tooltip);
				rootVisualElement.style.overflow = Overflow.Visible;
			}
			else
			{
				Logging.LogError("Could not find root for tooltip", _clientConfig);
			}
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error adding tooltip: {arg}", _clientConfig);
		}
		_playerTooltips.Add(playerSteamId, tooltip);
		_playerTooltipNameLabels[playerSteamId] = nameLabel;
		_playerTooltipContainers[playerSteamId] = playerContainer;
		_playerTooltipIsGoalie[playerSteamId] = flag;
		_playerTooltipLabels[playerSteamId] = playerTooltipLabels;
		tooltip.BringToFront();
		tooltip.pickingMode = PickingMode.Ignore;
		VisualElement scoreboardPlayerUsernameContainer = GetScoreboardPlayerUsernameContainer(playerContainer);
		Button scoreboardPlayerButton = GetScoreboardPlayerButton(playerContainer);
		VisualElement visualElement2 = (VisualElement)(object)scoreboardPlayerButton ?? playerContainer.parent ?? playerContainer;
		HashSet<VisualElement> hashSet = new HashSet<VisualElement>();
		Action showTooltip = delegate
		{
			if (IsTrackedPlayerTooltip(playerSteamId, tooltip) && tooltip.style.display == DisplayStyle.Flex &&
                _playerTooltipIsGoalie.TryGetValue(playerSteamId, out bool existingGoalieLayout) && existingGoalieLayout == UseGoalieLayoutForTooltip(playerSteamId, player)) return;
			_tooltipsAlreadyHidden = false;
			try
			{
				if (IsClientStatsUiAuthorized() || _playerTooltips.ContainsKey(playerSteamId))
				{
					if (player != null && (bool)player)
					{
						bool flag2 = UseGoalieLayoutForTooltip(playerSteamId, player);
						if (_playerTooltipIsGoalie.TryGetValue(playerSteamId, out var value2) && value2 != flag2 && _playerTooltipNameLabels.TryGetValue(playerSteamId, out var value3) && _playerTooltipContainers.TryGetValue(playerSteamId, out var value4) && value3 != null && value4 != null)
						{
							float defaultFontSize2 = 20f;
							SetupPlayerTooltip(value3, value4, playerSteamId, player, value3, defaultFontSize2);
							if (_playerTooltips.TryGetValue(playerSteamId, out var value5))
							{
								tooltip = value5;
							}
						}
					}
					RefreshScoreboardSogCellForPlayer(player, scheduleFollowUp: true);
					VisualElement scoreboardHeaderLabelContainer = GetScoreboardHeaderLabelContainer(GetScoreboardContainer());
					VisualElement visualElement4 = QueryNamedElement<VisualElement>(scoreboardHeaderLabelContainer, SOG_HEADER_CELL_NAME);
					Label label16 = QueryNamedElement<Label>(scoreboardHeaderLabelContainer, SOG_HEADER_LABEL_NAME);
					if (scoreboardHeaderLabelContainer != null && visualElement4 != null && label16 != null)
					{
						label16.text = "S/SV%";
						ApplyScoreboardSogHeaderLayout(scoreboardHeaderLabelContainer, visualElement4, label16);
					}
					UpdateTooltipStats(tooltip, playerSteamId, player);
					ApplyPlayerTooltipFontSizes(tooltip, playerSteamId, 20f);
					tooltip.style.backgroundColor = GetColorFromSteamId(playerSteamId);
					tooltip.style.display = DisplayStyle.Flex;
					tooltip.BringToFront();
					ScheduleVisiblePlayerTooltipRefresh(playerSteamId, tooltip);
					tooltip.schedule.Execute((Action)delegate
					{
						if (!IsTrackedPlayerTooltip(playerSteamId, tooltip) || tooltip.style.display != DisplayStyle.Flex)
						{
							return;
						}
						ScheduleVisiblePlayerTooltipPositionRefresh(playerSteamId, tooltip, nameLabel);
						ApplyPlayerTooltipFontSizes(tooltip, playerSteamId, 20f);
						foreach (Label item in tooltip.Children().OfType<Label>())
						{
							string text = item.text;
							item.text = "";
							item.text = text;
							item.MarkDirtyRepaint();
						}
					});
				}
			}
			catch (Exception arg2)
			{
				Logging.LogError($"Error showing tooltip: {arg2}", _clientConfig);
			}
		};
		Action hideTooltip = delegate
		{
			StopTooltipRefresh(tooltip);
			tooltip.style.display = DisplayStyle.None;
		};
		RegisterTooltipHoverCallbacks(nameLabel);
		RegisterTooltipHoverCallbacks(scoreboardPlayerUsernameContainer);
		RegisterTooltipHoverCallbacks(playerContainer);
		RegisterTooltipHoverCallbacks(visualElement2);
		void RegisterTooltipHoverCallbacks(VisualElement target)
		{
			if (target == null || !hashSet.Add(target))
			{
				return;
			}
			target.pickingMode = PickingMode.Position;
			RegisterTooltipCallback<MouseEnterEvent>(tooltip, target, delegate
			{
				showTooltip();
			});
			RegisterTooltipCallback<PointerEnterEvent>(tooltip, target, delegate
			{
				showTooltip();
			});
			RegisterTooltipCallback<MouseLeaveEvent>(tooltip, target, delegate
			{
				hideTooltip();
			});
			RegisterTooltipCallback<PointerLeaveEvent>(tooltip, target, delegate
			{
				hideTooltip();
			});
			RegisterTooltipCallback<MouseMoveEvent>(tooltip, target, delegate
			{
				if (IsTrackedPlayerTooltip(playerSteamId, tooltip) && tooltip.style.display == DisplayStyle.Flex)
				{
					float time = Time.time;
					if (!_tooltipMoveTimes.ContainsKey(tooltip) || time - _tooltipMoveTimes[tooltip] > 0.1f)
					{
						_tooltipMoveTimes[tooltip] = time;
						UpdateTooltipPosition(tooltip, nameLabel);
					}
				}
			});
		}
	}

	private static void UpdateFaceoffStatsFromPbp()
	{
		if (!ServerFunc.IsDedicatedServer())
		{
			return;
		}
		try
		{
			List<PlayByPlayEvent> source = (from e in _playByPlayEvents
				where e.EventType == PlayByPlayEventType.FaceoffOutcome
				orderby e.GameTime
				select e).ToList();
			_teamFaceoffWins.Clear();
			_teamFaceoffTotal.Clear();
			foreach (IGrouping<float, PlayByPlayEvent> item in (from e in source
				group e by e.GameTime).ToList())
			{
				List<PlayByPlayEvent> list = item.ToList();
				int value;
				foreach (PlayByPlayEvent item2 in list)
				{
					if (!(item2.Outcome == "successful"))
					{
						continue;
					}
					PlayerTeam playerTeam = (PlayerTeam)item2.PlayerTeam;
					if (playerTeam != PlayerTeam.None)
					{
						if (!_teamFaceoffWins.TryGetValue(playerTeam, out value))
						{
							_teamFaceoffWins.Add(playerTeam, 0);
						}
						value = _teamFaceoffWins[playerTeam]++;
					}
				}
				foreach (PlayByPlayEvent item3 in list)
				{
					PlayerTeam playerTeam2 = (PlayerTeam)item3.PlayerTeam;
					if (playerTeam2 != PlayerTeam.None)
					{
						if (!_teamFaceoffTotal.TryGetValue(playerTeam2, out value))
						{
							_teamFaceoffTotal.Add(playerTeam2, 0);
						}
						value = _teamFaceoffTotal[playerTeam2]++;
					}
				}
			}
			foreach (KeyValuePair<PlayerTeam, int> teamFaceoffWin in _teamFaceoffWins)
			{
				QueueStatUpdate("oomtm450_statsTEAM_FACEOFF_WINS" + teamFaceoffWin.Key, teamFaceoffWin.Value.ToString());
			}
			foreach (KeyValuePair<PlayerTeam, int> item4 in _teamFaceoffTotal)
			{
				QueueStatUpdate("oomtm450_statsTEAM_FACEOFF_TOTAL" + item4.Key, item4.Value.ToString());
			}
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error updating faceoff stats from pbp: {arg}", ServerConfig);
		}
	}

	private static void UpdateBodyStickSavesFromPbp()
	{
		if (!ServerFunc.IsDedicatedServer())
		{
			return;
		}
		try
		{
			new HashSet<string>();
			List<PlayByPlayEvent> list = _playByPlayEvents.Where((PlayByPlayEvent e) => !string.IsNullOrEmpty(e.PlayerSteamId) && e.EventType == PlayByPlayEventType.Save && e.Outcome == "successful").ToList();
			_stickSaves.Clear();
			_bodySaves.Clear();
			_padSaves.Clear();
			foreach (PlayByPlayEvent item in list)
			{
				string playerSteamId = item.PlayerSteamId;
				int value;
				if (!string.IsNullOrEmpty(item.Flags) && item.Flags.Contains("Stick"))
				{
					if (!_stickSaves.TryGetValue(playerSteamId, out value))
					{
						_stickSaves.Add(playerSteamId, 0);
					}
					value = _stickSaves[playerSteamId]++;
				}
				else if (!string.IsNullOrEmpty(item.Flags) && item.Flags.Contains("Pad"))
				{
					if (!_padSaves.TryGetValue(playerSteamId, out value))
					{
						_padSaves.Add(playerSteamId, 0);
					}
					value = _padSaves[playerSteamId]++;
				}
				else
				{
					if (!_bodySaves.TryGetValue(playerSteamId, out value))
					{
						_bodySaves.Add(playerSteamId, 0);
					}
					value = _bodySaves[playerSteamId]++;
				}
			}
			foreach (KeyValuePair<string, int> stickSafe in _stickSaves)
			{
				QueueStatUpdate("oomtm450_statsSTICK_SAVES" + stickSafe.Key, stickSafe.Value.ToString());
			}
			foreach (KeyValuePair<string, int> padSafe in _padSaves)
			{
				QueueStatUpdate("oomtm450_statsPAD_SAVES" + padSafe.Key, padSafe.Value.ToString());
			}
			foreach (KeyValuePair<string, int> bodySafe in _bodySaves)
			{
				QueueStatUpdate("oomtm450_statsBODY_SAVES" + bodySafe.Key, bodySafe.Value.ToString());
			}
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error updating body/stick saves from pbp: {arg}", ServerConfig);
		}
	}

	private static void UpdateTooltipStats(VisualElement tooltip, string playerSteamId, Player player)
	{
		if (tooltip == null)
		{
			return;
		}
		if (ShouldUseClientPreviousGameSnapshot())
		{
			if (TryUpdatePlayerTooltipFromPreviousGameSnapshot(tooltip, playerSteamId))
			{
				return;
			}
			if (!IsClientPreviousGameParticipant(playerSteamId))
			{
				return;
			}
		}
		if (player == null || !player)
		{
			return;
		}
		PlayerTooltipLabels orCreatePlayerTooltipLabels = GetOrCreatePlayerTooltipLabels(tooltip, playerSteamId);
		if (GetPlayerPosition(player) == "G")
		{
			Label label = orCreatePlayerTooltipLabels?.SavePercent;
			if (label != null)
			{
				int num = 0;
				int num2 = 0;
				if (_savePerc.TryGetValue(playerSteamId, out (int, int) value))
				{
					(num, num2) = value;
				}
				double num3 = ((num2 > 0) ? ((double)num / (double)num2 * 100.0) : 0.0);
				label.text = $"SV%: {num}/{num2} ({num3:F1}%)";
			}
			Label label2 = orCreatePlayerTooltipLabels?.ExpectedGoalsAgainst;
			double value2 = (_expectedGoalsAgainst.TryGetValue(playerSteamId, out var value3) ? value3 : 0.0);
			if (label2 != null)
			{
				label2.text = $"xGA: {value2:F2}";
			}
			Label label3 = orCreatePlayerTooltipLabels?.GoalsSavedAboveExpected;
			if (label3 != null)
			{
				int num4 = 0;
				if (_savePerc.TryGetValue(playerSteamId, out (int Saves, int Shots) value4))
				{
					num4 = Math.Max(0, value4.Shots - value4.Saves);
				}
				double num5 = value2 - (double)num4;
				label3.text = $"GSAx: {num5:+0.00;-0.00;0.00}";
			}
			Label label4 = orCreatePlayerTooltipLabels?.StickSaves;
			if (label4 != null)
			{
				int value5;
				int num6 = (_stickSaves.TryGetValue(playerSteamId, out value5) ? value5 : 0);
				label4.text = $"Stick Saves: {num6}";
			}
			Label label5 = orCreatePlayerTooltipLabels?.BodySaves;
			if (label5 != null)
			{
				int value6;
				int num7 = (_bodySaves.TryGetValue(playerSteamId, out value6) ? value6 : 0);
				label5.text = $"Body Saves: {num7}";
			}
			Label label6 = orCreatePlayerTooltipLabels?.PadSaves;
			if (label6 != null)
			{
				int value7;
				int num8 = (_padSaves.TryGetValue(playerSteamId, out value7) ? value7 : 0);
				label6.text = $"Pad Saves: {num8}";
			}
			Label label7 = orCreatePlayerTooltipLabels?.PossessionTime;
			if (label7 != null)
			{
				double value8;
				double timeSeconds = (_possessionTimeSeconds.TryGetValue(playerSteamId, out value8) ? value8 : 0.0);
				label7.text = "Poss Time: " + FormatTimeAsMinutesSeconds(timeSeconds);
			}
			Label label8 = orCreatePlayerTooltipLabels?.Passes;
			if (label8 != null)
			{
				int value9;
				int num9 = (_passes.TryGetValue(playerSteamId, out value9) ? value9 : 0);
				label8.text = $"Passes: {num9}";
			}
		}
		else
		{
			Label label9b = orCreatePlayerTooltipLabels?.IndividualCorsiFor;
			int value10b;
			int num10b = (_shotAttempts.TryGetValue(playerSteamId, out value10b) ? value10b : 0);
			if (label9b != null)
			{
				label9b.text = $"iCF: {num10b}";
			}
			Label label9c = orCreatePlayerTooltipLabels?.IndividualCorsiForPercent;
			if (label9c != null)
			{
				int value10c;
				int num10c = (_onIceTeamShotAttempts.TryGetValue(playerSteamId, out value10c) ? value10c : 0);
				double num10d = CalculateIndividualCorsiForPercentage(num10b, num10c);
				label9c.text = $"iCF%: {FormatIndividualCorsiForPercentage(num10d)} ({num10b}/{num10c})";
			}
			Label label10 = orCreatePlayerTooltipLabels?.ExpectedGoals;
			if (label10 != null)
			{
				double value11 = (_expectedGoalsFor.TryGetValue(playerSteamId, out var value12) ? value12 : 0.0);
				label10.text = $"xG: {value11:F2}";
			}
			Label label13 = orCreatePlayerTooltipLabels?.Passes;
			if (label13 != null)
			{
				int value17;
				int num11 = (_passes.TryGetValue(playerSteamId, out value17) ? value17 : 0);
				label13.text = $"Passes: {num11}";
			}
			Label labelExitsEntries = orCreatePlayerTooltipLabels?.ExitsEntries;
			if (labelExitsEntries != null)
			{
				int value18;
				int num12 = (_exits.TryGetValue(playerSteamId, out value18) ? value18 : 0);
				int value19;
				int num13 = (_entries.TryGetValue(playerSteamId, out value19) ? value19 : 0);
				labelExitsEntries.text = $"Exits & Entries: {num12} | {num13}";
			}
			Label labelTurnovers = orCreatePlayerTooltipLabels?.PossessionTime;
			if (labelTurnovers != null)
			{
				double value19;
				double timeSeconds2 = (_possessionTimeSeconds.TryGetValue(playerSteamId, out value19) ? value19 : 0.0);
				labelTurnovers.text = "Possession: " + FormatTimeAsMinutesSeconds(timeSeconds2);
			}
			Label label15 = orCreatePlayerTooltipLabels?.Takeaways;
			if (label15 != null)
			{
				int value20;
				int num13 = (_takeaways.TryGetValue(playerSteamId, out value20) ? value20 : 0);
				label15.text = $"Takeaways: {num13}";
			}
			Label label16 = orCreatePlayerTooltipLabels?.Turnovers;
			if (label16 != null)
			{
				int value21;
				int num14 = (_turnovers.TryGetValue(playerSteamId, out value21) ? value21 : 0);
				label16.text = $"Turnovers: {num14}";
			}
			Label label17 = orCreatePlayerTooltipLabels?.Blocks;
			if (label17 != null)
			{
				int value22;
				int num15 = (_blocks.TryGetValue(playerSteamId, out value22) ? value22 : 0);
				label17.text = $"Blocks: {num15}";
			}
			Label label18 = orCreatePlayerTooltipLabels?.Hits;
			if (label18 != null)
			{
				int value23;
				int num16 = (_hits.TryGetValue(playerSteamId, out value23) ? value23 : 0);
				label18.text = $"Hits: {num16}";
			}
		}
		Label label14 = orCreatePlayerTooltipLabels?.StarPoints;
		if (label14 != null)
		{
			double num15 = CalculateCurrentStarPoints(playerSteamId, player);
			label14.text = $"Star Points: {num15:F1}";
		}
	}

	private static double CalculateCurrentStarPoints(string playerSteamId, Player player)
	{
		if (IsClientPreviousGameParticipant(playerSteamId))
		{
			return GetClientPreviousGameStarPoints(playerSteamId);
		}
		if (player == null || !player)
		{
			return 0.0;
		}
		double num = 0.0;
		string text = "";
		PlayerTeam playerTeam = PlayerTeam.None;
		try
		{
			if (NetworkBehaviourSingleton<GameManager>.Instance != null && NetworkBehaviourSingleton<GameManager>.Instance.GameState != null)
			{
				int blueScore = NetworkBehaviourSingleton<GameManager>.Instance.GameState.Value.BlueScore;
				int redScore = NetworkBehaviourSingleton<GameManager>.Instance.GameState.Value.RedScore;
				if (blueScore > redScore)
				{
					playerTeam = PlayerTeam.Blue;
					int num2 = 0;
					int num3 = 0;
					foreach (GoalInfo item in _goals.OrderBy((GoalInfo g) => g.GameTime))
					{
						if (item.Team == "Blue")
						{
							num2++;
						}
						else
						{
							num3++;
						}
						if (item.Team == "Blue" && num2 > num3 && num2 == redScore + 1)
						{
							text = item.Scorer;
							break;
						}
					}
				}
				else if (redScore > blueScore)
				{
					playerTeam = PlayerTeam.Red;
					int num4 = 0;
					int num5 = 0;
					foreach (GoalInfo item2 in _goals.OrderBy((GoalInfo g) => g.GameTime))
					{
						if (item2.Team == "Blue")
						{
							num4++;
						}
						else
						{
							num5++;
						}
						if (item2.Team == "Red" && num5 > num4 && num5 == blueScore + 1)
						{
							text = item2.Scorer;
							break;
						}
					}
				}
			}
		}
		catch
		{
		}
		double num6 = ((text == playerSteamId) ? 0.5 : 0.0);
		double num7 = ((playerTeam == player.Team) ? 1.1 : 1.0);
		if (PlayerFunc.IsGoalie(player))
		{
			if (_savePerc.TryGetValue(playerSteamId, out (int, int) value))
			{
				int num8 = value.Item2 - value.Item1;
				num += (double)num8 * -10.0;
				num += (double)value.Item2 * 10.0;
				if (num8 == 0 && value.Item2 > 0)
				{
					num += 100.0;
				}
			}
			if (_passes.TryGetValue(playerSteamId, out var value2))
			{
				num += (double)value2 * 2.5;
			}
			num += 175.0 * num6;
			num += (double)player.Goals.Value * 175.0;
			num += (double)player.Assists.Value * 30.0;
		}
		else
		{
			if (_sog.TryGetValue(playerSteamId, out var value3))
			{
				num += (double)value3 * 7.5;
			}
			if (_passes.TryGetValue(playerSteamId, out var value4))
			{
				num += (double)value4 * 2.5;
			}
			if (_blocks.TryGetValue(playerSteamId, out var value5))
			{
				num += (double)value5 * 5.0;
			}
			num += 70.0 * num6;
			num += (double)player.Goals.Value * 70.0;
			num += (double)player.Assists.Value * 30.0;
		}
		if (_hits.TryGetValue(playerSteamId, out var value6))
		{
			num += (double)value6 * 2.5;
		}
		if (_takeaways.TryGetValue(playerSteamId, out var value7))
		{
			num += (double)value7 * 5.0;
		}
		if (_turnovers.TryGetValue(playerSteamId, out var value8))
		{
			num -= (double)value8 * 5.0;
		}
		if (_exits.TryGetValue(playerSteamId, out var value9))
		{
			num += (double)value9 * 1.0;
		}
		if (_entries.TryGetValue(playerSteamId, out var value10))
		{
			num += (double)value10 * 1.0;
		}
		if (NetworkBehaviourSingleton<GameManager>.Instance != null && NetworkBehaviourSingleton<GameManager>.Instance.Phase == GamePhase.Intermission)
		{
			num *= num7;
		}
		return num;
	}

	private static string NormalizeBoxscoreTab(string tab)
	{
		if (string.Equals(tab, BOXSCORE_TAB_GAMESTATS, StringComparison.OrdinalIgnoreCase))
		{
			return BOXSCORE_TAB_GAMESTATS;
		}
		if (string.Equals(tab, BOXSCORE_TAB_PLAYERSTATS, StringComparison.OrdinalIgnoreCase))
		{
			return BOXSCORE_TAB_PLAYERSTATS;
		}
		if (string.Equals(tab, BOXSCORE_TAB_SHOOTOUT, StringComparison.OrdinalIgnoreCase))
		{
			return BOXSCORE_TAB_SHOOTOUT;
		}
		return BOXSCORE_TAB_LEADERS;
	}

	private static string GetTeamDisplayLabel(PlayerTeam team)
	{
		return team switch
		{
			PlayerTeam.Red => "Red",
			PlayerTeam.Blue => "Blue",
			_ => "spectator",
		};
	}

	private static Color GetTeamAccentColor(string teamLabel)
	{
		if (string.Equals(teamLabel, "Red", StringComparison.OrdinalIgnoreCase))
		{
			return new Color(0.79f, 0.26f, 0.24f, 1f);
		}
		if (string.Equals(teamLabel, "Blue", StringComparison.OrdinalIgnoreCase))
		{
			return new Color(0.25f, 0.47f, 0.84f, 1f);
		}
		return new Color(0.7f, 0.7f, 0.7f, 1f);
	}

	private static string FormatSignedDecimal(double value, string format = "0.00")
	{
		if (value > 0.00005)
		{
			return "+" + value.ToString(format, CultureInfo.InvariantCulture);
		}
		if (value < -0.00005)
		{
			return value.ToString(format, CultureInfo.InvariantCulture);
		}
		return 0.0.ToString(format, CultureInfo.InvariantCulture);
	}

	private static string FormatFaceoffPercentage(int wins, int losses)
	{
		int num = wins + losses;
		if (num <= 0)
		{
			return "0% (0/0)";
		}
		double num2 = (double)wins / (double)num * 100.0;
		return $"{num2:0}% ({wins}/{num})";
	}

	private static string BuildBoxscoreSummary()
	{
		if (ShouldUseClientPreviousGameSnapshot() && !string.IsNullOrWhiteSpace(_clientPreviousGameSnapshot?.Summary))
		{
			return _clientPreviousGameSnapshot.Summary;
		}
		if (_logic || !_logic)
		{
			return BuildLiveBoxscoreSummary();
		}
		int currentPeriod = GetCurrentPeriod();
		float currentGameTime = GetCurrentGameTime();
		float num = currentGameTime - (float)Math.Max(0, currentPeriod - 1) * 300f;
		if (num < 0f)
		{
			num = currentGameTime;
		}
		string arg = FormatTimeAsMinutesSeconds(num);
		int num2 = 0;
		int num3 = 0;
		try
		{
			if (NetworkBehaviourSingleton<GameManager>.Instance != null && NetworkBehaviourSingleton<GameManager>.Instance.GameState != null)
			{
				num2 = NetworkBehaviourSingleton<GameManager>.Instance.GameState.Value.RedScore;
				num3 = NetworkBehaviourSingleton<GameManager>.Instance.GameState.Value.BlueScore;
			}
		}
		catch
		{
		}
		return $"P{Math.Max(1, currentPeriod)} â€¢ {arg} â€¢ Red {num2} - Blue {num3}";
	}

	private static bool TryGetUiManagerViews(UIManager instance, out List<UIView> views)
	{
		views = null;
		return instance != null && SystemFunc.TryGetPrivateField(typeof(UIManager), instance, "views", out views) && views != null;
	}

	private static Action<UIView> CreateUiManagerViewCallback(UIManager instance, string methodName)
	{
		if (instance == null || string.IsNullOrEmpty(methodName))
		{
			return null;
		}
		try
		{
			MethodInfo method = typeof(UIManager).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
			if (method == null)
			{
				return null;
			}
			return (Action<UIView>)Delegate.CreateDelegate(typeof(Action<UIView>), instance, method);
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error creating UIManager callback {methodName}: {arg}", _clientConfig);
			return null;
		}
	}

	private static void RefreshUiManagerViewState(UIManager instance)
	{
		if (instance == null)
		{
			return;
		}
		try
		{
			typeof(UIManager).GetMethod("CheckMouseRequirement", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(instance, null);
			typeof(UIManager).GetMethod("CheckInteraction", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(instance, null);
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error refreshing UIManager boxscore state: {arg}", _clientConfig);
		}
	}

	private static void RegisterBoxscoreOverlayView(UIManager instance)
	{
		if (instance == null || _boxscoreOverlayView == null)
		{
			return;
		}
		if (!ReferenceEquals(_boxscoreRegisteredUiManager, instance))
		{
			UnregisterBoxscoreOverlayView();
		}
		if (!TryGetUiManagerViews(instance, out List<UIView> views))
		{
			return;
		}
		if (!views.Contains(_boxscoreOverlayView))
		{
			views.Add(_boxscoreOverlayView);
		}
		_boxscoreRegisteredUiManager = instance;
		if (_boxscoreOverlayVisibilityHandler == null)
		{
			_boxscoreOverlayVisibilityHandler = CreateUiManagerViewCallback(instance, "OnViewVisibilityChanged");
		}
		if (_boxscoreOverlayFocusHandler == null)
		{
			_boxscoreOverlayFocusHandler = CreateUiManagerViewCallback(instance, "OnViewFocusChanged");
		}
		if (_boxscoreOverlayVisibilityHandler != null)
		{
			_boxscoreOverlayView.OnVisibility = (Action<UIView>)Delegate.Remove(_boxscoreOverlayView.OnVisibility, _boxscoreOverlayVisibilityHandler);
			_boxscoreOverlayView.OnVisibility = (Action<UIView>)Delegate.Combine(_boxscoreOverlayView.OnVisibility, _boxscoreOverlayVisibilityHandler);
		}
		if (_boxscoreOverlayFocusHandler != null)
		{
			_boxscoreOverlayView.OnFocus = (Action<UIView>)Delegate.Remove(_boxscoreOverlayView.OnFocus, _boxscoreOverlayFocusHandler);
			_boxscoreOverlayView.OnFocus = (Action<UIView>)Delegate.Combine(_boxscoreOverlayView.OnFocus, _boxscoreOverlayFocusHandler);
		}
	}

	private static void UnregisterBoxscoreOverlayView()
	{
		try
		{
			BoxscoreOverlayView boxscoreOverlayView = _boxscoreOverlayView;
			if (boxscoreOverlayView != null)
			{
				if (_boxscoreOverlayVisibilityHandler != null)
				{
					boxscoreOverlayView.OnVisibility = (Action<UIView>)Delegate.Remove(boxscoreOverlayView.OnVisibility, _boxscoreOverlayVisibilityHandler);
				}
				if (_boxscoreOverlayFocusHandler != null)
				{
					boxscoreOverlayView.OnFocus = (Action<UIView>)Delegate.Remove(boxscoreOverlayView.OnFocus, _boxscoreOverlayFocusHandler);
				}
			}
			if (_boxscoreRegisteredUiManager != null && boxscoreOverlayView != null && TryGetUiManagerViews(_boxscoreRegisteredUiManager, out List<UIView> views))
			{
				views.Remove(boxscoreOverlayView);
			}
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error unregistering boxscore overlay view: {arg}", _clientConfig);
		}
		finally
		{
			_boxscoreRegisteredUiManager = null;
			_boxscoreOverlayVisibilityHandler = null;
			_boxscoreOverlayFocusHandler = null;
		}
	}

	private static void EnsureBoxscoreOverlayView()
	{
		UIManager instance = MonoBehaviourSingleton<UIManager>.Instance;
		if (instance == null)
		{
			return;
		}
		if (_boxscoreOverlayView == null)
		{
			_boxscoreOverlayView = instance.gameObject.GetComponent<BoxscoreOverlayView>();
			if (_boxscoreOverlayView == null)
			{
				_boxscoreOverlayView = instance.gameObject.AddComponent<BoxscoreOverlayView>();
			}
		}
		_boxscoreOverlayView.View = _boxscoreOverlay;
		_boxscoreOverlayView.RootVisualElement = instance.RootVisualElement;
		_boxscoreOverlayView.VisibilityRequiresMouse = true;
		_boxscoreOverlayView.VisibilityIsInteractive = true;
		_boxscoreOverlayView.FocusRequiresMouse = true;
		_boxscoreOverlayView.FocusIsInteractive = true;
		RegisterBoxscoreOverlayView(instance);
		bool flag = _boxscoreVisible && _boxscoreOverlay != null && _boxscoreOverlay.style.display == DisplayStyle.Flex;
		_boxscoreOverlayView.IsVisible = flag;
		if (!flag)
		{
			_boxscoreOverlayView.IsFocused = false;
		}
		RefreshUiManagerViewState(instance);
	}

	private static void GetBoxscoreViewportSize(out float viewportWidth, out float viewportHeight)
	{
		viewportWidth = 0f;
		viewportHeight = 0f;
		VisualElement rootVisualElement = MonoBehaviourSingleton<UIManager>.Instance?.RootVisualElement;
		if (rootVisualElement != null)
		{
			viewportWidth = Mathf.Max(rootVisualElement.resolvedStyle.width, rootVisualElement.layout.width);
			viewportHeight = Mathf.Max(rootVisualElement.resolvedStyle.height, rootVisualElement.layout.height);
		}
		if (viewportWidth <= 0f)
		{
			viewportWidth = Mathf.Max(Screen.width, 1280);
		}
		if (viewportHeight <= 0f)
		{
			viewportHeight = Mathf.Max(Screen.height, 720);
		}
	}

	private static float GetDefaultBoxscorePanelWidth()
	{
		GetBoxscoreViewportSize(out var viewportWidth, out var _);
		return Mathf.Min(BOXSCORE_DEFAULT_WIDTH, viewportWidth - BOXSCORE_VIEWPORT_MARGIN);
	}

	private static float GetDefaultBoxscorePanelHeight()
	{
		GetBoxscoreViewportSize(out var _, out var viewportHeight);
		return Mathf.Min(BOXSCORE_DEFAULT_HEIGHT, viewportHeight - BOXSCORE_VIEWPORT_MARGIN);
	}

	private static float ClampBoxscorePanelWidth(float width)
	{
		GetBoxscoreViewportSize(out var viewportWidth, out var _);
		return Mathf.Clamp(width, BOXSCORE_MIN_WIDTH, Mathf.Max(BOXSCORE_MIN_WIDTH, viewportWidth - BOXSCORE_VIEWPORT_MARGIN));
	}

	private static float ClampBoxscorePanelHeight(float height)
	{
		GetBoxscoreViewportSize(out var _, out var viewportHeight);
		return Mathf.Clamp(height, BOXSCORE_MIN_HEIGHT, Mathf.Max(BOXSCORE_MIN_HEIGHT, viewportHeight - BOXSCORE_VIEWPORT_MARGIN));
	}

	private static void ApplyBoxscorePanelSizeFromConfig()
	{
		if (_boxscorePanel == null)
		{
			return;
		}
		float width = ((_clientConfig.BoxscoreWindowWidth > 0f) ? _clientConfig.BoxscoreWindowWidth : GetDefaultBoxscorePanelWidth());
		float height = ((_clientConfig.BoxscoreWindowHeight > 0f) ? _clientConfig.BoxscoreWindowHeight : GetDefaultBoxscorePanelHeight());
		float num = ClampBoxscorePanelWidth(width);
		float num2 = ClampBoxscorePanelHeight(height);
		_boxscorePanel.style.width = num;
		_boxscorePanel.style.height = num2;
		_boxscorePanel.style.minWidth = BOXSCORE_MIN_WIDTH;
		_boxscorePanel.style.minHeight = BOXSCORE_MIN_HEIGHT;
		_boxscorePanel.style.maxWidth = Mathf.Max(BOXSCORE_MIN_WIDTH, num);
		_boxscorePanel.style.maxHeight = Mathf.Max(BOXSCORE_MIN_HEIGHT, num2);
	}

	private static void SaveBoxscorePanelSize(float width, float height)
	{
		float num = ClampBoxscorePanelWidth(width);
		float num2 = ClampBoxscorePanelHeight(height);
		if (!(Mathf.Abs(_clientConfig.BoxscoreWindowWidth - num) < 0.5f) || !(Mathf.Abs(_clientConfig.BoxscoreWindowHeight - num2) < 0.5f))
		{
			_clientConfig.BoxscoreWindowWidth = num;
			_clientConfig.BoxscoreWindowHeight = num2;
			_clientConfig.Save();
		}
	}

	private static void EndBoxscoreResize(bool saveSize)
	{
		if (_boxscoreResizeHandle != null && _boxscoreResizePointerId >= 0 && _boxscoreResizeHandle.HasPointerCapture(_boxscoreResizePointerId))
		{
			_boxscoreResizeHandle.ReleasePointer(_boxscoreResizePointerId);
		}
		if (saveSize && _boxscorePanel != null)
		{
			SaveBoxscorePanelSize(_boxscorePanel.resolvedStyle.width, _boxscorePanel.resolvedStyle.height);
		}
		_boxscoreResizeActive = false;
		_boxscoreResizePointerId = -1;
	}

	private static void SetupBoxscoreResizeHandleCallbacks(VisualElement resizeHandle)
	{
		if (resizeHandle == null)
		{
			return;
		}
		resizeHandle.RegisterCallback<PointerDownEvent>(delegate(PointerDownEvent evt)
		{
			if (_boxscorePanel != null && evt.button == 0)
			{
				_boxscoreResizeActive = true;
				_boxscoreResizePointerId = evt.pointerId;
				_boxscoreResizeStartPointerPosition = evt.position;
				_boxscoreResizeStartWidth = Mathf.Max(_boxscorePanel.resolvedStyle.width, _boxscorePanel.layout.width);
				_boxscoreResizeStartHeight = Mathf.Max(_boxscorePanel.resolvedStyle.height, _boxscorePanel.layout.height);
				resizeHandle.CapturePointer(evt.pointerId);
				evt.StopPropagation();
			}
		});
		resizeHandle.RegisterCallback<PointerMoveEvent>(delegate(PointerMoveEvent evt)
		{
			if (_boxscoreResizeActive && evt.pointerId == _boxscoreResizePointerId && _boxscorePanel != null)
			{
				Vector2 vector = (Vector2)evt.position - _boxscoreResizeStartPointerPosition;
				float width2 = ClampBoxscorePanelWidth(_boxscoreResizeStartWidth + vector.x);
				float height2 = ClampBoxscorePanelHeight(_boxscoreResizeStartHeight + vector.y);
				_boxscorePanel.style.width = width2;
				_boxscorePanel.style.height = height2;
				_boxscorePanel.style.maxWidth = Mathf.Max(BOXSCORE_MIN_WIDTH, width2);
				_boxscorePanel.style.maxHeight = Mathf.Max(BOXSCORE_MIN_HEIGHT, height2);
				evt.StopPropagation();
			}
		});
		resizeHandle.RegisterCallback<PointerUpEvent>(delegate(PointerUpEvent evt)
		{
			if (_boxscoreResizeActive && evt.pointerId == _boxscoreResizePointerId)
			{
				EndBoxscoreResize(saveSize: true);
				evt.StopPropagation();
			}
		});
		resizeHandle.RegisterCallback<PointerLeaveEvent>(delegate(PointerLeaveEvent evt)
		{
			if (_boxscoreResizeActive && evt.pointerId == _boxscoreResizePointerId && !resizeHandle.HasPointerCapture(evt.pointerId))
			{
				EndBoxscoreResize(saveSize: true);
			}
		});
	}

	private static void EnsureBoxscorePanelUI()
	{
		UIManager instance = MonoBehaviourSingleton<UIManager>.Instance;
		if (instance == null || instance.RootVisualElement == null)
		{
			return;
		}
		if (_boxscoreOverlay != null && _boxscoreOverlay.parent != null)
		{
			return;
		}
		_boxscoreTabButtons.Clear();
		_boxscoreOverlay = new VisualElement
		{
			name = "DomaStats_BoxscoreOverlay"
		};
		_boxscoreOverlay.style.position = Position.Absolute;
		_boxscoreOverlay.style.left = 0f;
		_boxscoreOverlay.style.right = 0f;
		_boxscoreOverlay.style.top = 0f;
		_boxscoreOverlay.style.bottom = 0f;
		_boxscoreOverlay.style.backgroundColor = new Color(0f, 0f, 0f, 0.62f);
		_boxscoreOverlay.style.display = DisplayStyle.None;
		_boxscoreOverlay.style.justifyContent = Justify.Center;
		_boxscoreOverlay.style.alignItems = Align.Center;
		_boxscoreOverlay.style.paddingTop = 24f;
		_boxscoreOverlay.style.paddingBottom = 24f;
		_boxscoreOverlay.style.paddingLeft = 32f;
		_boxscoreOverlay.style.paddingRight = 32f;
		_boxscoreOverlay.pickingMode = PickingMode.Position;
		_boxscorePanel = new VisualElement
		{
			name = "DomaStats_BoxscorePanel"
		};
		_boxscorePanel.style.backgroundColor = new Color(0.06f, 0.08f, 0.11f, 0.98f);
		_boxscorePanel.style.borderTopWidth = 2f;
		_boxscorePanel.style.borderBottomWidth = 2f;
		_boxscorePanel.style.borderLeftWidth = 2f;
		_boxscorePanel.style.borderRightWidth = 2f;
		_boxscorePanel.style.borderTopColor = new Color(0.9f, 0.9f, 0.95f, 0.95f);
		_boxscorePanel.style.borderBottomColor = new Color(0.9f, 0.9f, 0.95f, 0.95f);
		_boxscorePanel.style.borderLeftColor = new Color(0.9f, 0.9f, 0.95f, 0.95f);
		_boxscorePanel.style.borderRightColor = new Color(0.9f, 0.9f, 0.95f, 0.95f);
		_boxscorePanel.style.flexDirection = FlexDirection.Column;
		_boxscorePanel.style.overflow = Overflow.Hidden;
		ApplyBoxscorePanelSizeFromConfig();
		VisualElement visualElement = new VisualElement
		{
			name = "DomaStats_BoxscoreBanner"
		};
		visualElement.style.flexDirection = FlexDirection.Row;
		visualElement.style.justifyContent = Justify.SpaceBetween;
		visualElement.style.alignItems = Align.Center;
		visualElement.style.backgroundColor = new Color(0.15f, 0.18f, 0.22f, 0.98f);
		visualElement.style.paddingLeft = 22f;
		visualElement.style.paddingRight = 22f;
		visualElement.style.paddingTop = 16f;
		visualElement.style.paddingBottom = 14f;
		Label label = new Label("BOXSCORE");
		label.style.fontSize = 26f;
		label.style.unityFontStyleAndWeight = FontStyle.Bold;
		label.style.color = new Color(0.98f, 0.98f, 0.98f, 1f);
		Label label2 = new Label("F7 / Esc to close  |  Drag corner to resize");
		label2.style.fontSize = 13f;
		label2.style.color = new Color(0.82f, 0.84f, 0.9f, 0.95f);
		visualElement.Add(label);
		visualElement.Add(label2);
		VisualElement visualElement2 = new VisualElement
		{
			name = "DomaStats_BoxscoreTabs"
		};
		visualElement2.style.flexDirection = FlexDirection.Row;
		visualElement2.style.alignItems = Align.Center;
		visualElement2.style.paddingLeft = 18f;
		visualElement2.style.paddingRight = 18f;
		visualElement2.style.paddingTop = 12f;
		visualElement2.style.paddingBottom = 10f;
		visualElement2.style.backgroundColor = new Color(0.08f, 0.11f, 0.15f, 1f);
		visualElement2.Add(CreateBoxscoreTabButton(BOXSCORE_TAB_LEADERS));
		visualElement2.Add(CreateBoxscoreTabButton(BOXSCORE_TAB_GAMESTATS));
		visualElement2.Add(CreateBoxscoreTabButton(BOXSCORE_TAB_PLAYERSTATS));
		visualElement2.Add(CreateBoxscoreTabButton(BOXSCORE_TAB_SHOOTOUT));
		_boxscoreSummaryLabel = new Label(BuildBoxscoreSummary())
		{
			name = "DomaStats_BoxscoreSummary"
		};
		_boxscoreSummaryLabel.style.fontSize = 14f;
		_boxscoreSummaryLabel.style.color = new Color(0.86f, 0.89f, 0.94f, 1f);
		_boxscoreSummaryLabel.style.paddingLeft = 20f;
		_boxscoreSummaryLabel.style.paddingRight = 20f;
		_boxscoreSummaryLabel.style.paddingTop = 10f;
		_boxscoreSummaryLabel.style.paddingBottom = 10f;
		_boxscoreSummaryLabel.style.backgroundColor = new Color(0.1f, 0.12f, 0.16f, 0.96f);
		_boxscoreLoadingLabel = new Label("")
		{
			name = "DomaStats_BoxscoreLoadingLabel"
		};
		_boxscoreLoadingLabel.style.fontSize = 15f;
		_boxscoreLoadingLabel.style.color = new Color(0.94f, 0.95f, 0.98f, 1f);
		_boxscoreLoadingLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
		_boxscoreLoadingLabel.style.marginTop = 24f;
		_boxscoreLoadingLabel.style.marginBottom = 24f;
		_boxscoreContentRoot = new VisualElement
		{
			name = "DomaStats_BoxscoreContentRoot"
		};
		_boxscoreContentRoot.style.flexGrow = 1f;
		_boxscoreContentRoot.style.paddingLeft = 18f;
		_boxscoreContentRoot.style.paddingRight = 18f;
		_boxscoreContentRoot.style.paddingBottom = 18f;
		_boxscoreContentRoot.style.paddingTop = 14f;
		_boxscoreContentRoot.style.overflow = Overflow.Hidden;
		_boxscoreResizeHandle = new VisualElement
		{
			name = "DomaStats_BoxscoreResizeHandle"
		};
		_boxscoreResizeHandle.style.position = Position.Absolute;
		_boxscoreResizeHandle.style.right = 8f;
		_boxscoreResizeHandle.style.bottom = 8f;
		_boxscoreResizeHandle.style.width = 22f;
		_boxscoreResizeHandle.style.height = 22f;
		_boxscoreResizeHandle.style.backgroundColor = new Color(0.9f, 0.93f, 0.99f, 0.12f);
		_boxscoreResizeHandle.style.borderTopWidth = 1f;
		_boxscoreResizeHandle.style.borderLeftWidth = 1f;
		_boxscoreResizeHandle.style.borderTopColor = new Color(0.95f, 0.97f, 1f, 0.4f);
		_boxscoreResizeHandle.style.borderLeftColor = new Color(0.95f, 0.97f, 1f, 0.4f);
		_boxscoreResizeHandle.pickingMode = PickingMode.Position;
		SetupBoxscoreResizeHandleCallbacks(_boxscoreResizeHandle);
		_boxscorePanel.Add(visualElement);
		_boxscorePanel.Add(visualElement2);
		_boxscorePanel.Add(_boxscoreSummaryLabel);
		_boxscorePanel.Add(_boxscoreContentRoot);
		_boxscorePanel.Add(_boxscoreResizeHandle);
		_boxscoreOverlay.Add(_boxscorePanel);
		instance.RootVisualElement.Add(_boxscoreOverlay);
		EnsureBoxscoreOverlayView();
		RefreshBoxscoreTabButtonVisibility(refreshVisiblePanel: false);
		UpdateBoxscoreTabButtonStyles();
	}

	private static Button CreateBoxscoreTabButton(string tabName)
	{
		Button button = new Button(delegate
		{
			SwitchBoxscoreTab(tabName);
		})
		{
			text = tabName,
			name = "DomaStats_BoxscoreTab_" + tabName.Replace(" ", "")
		};
		button.style.minHeight = 34f;
		button.style.paddingLeft = 14f;
		button.style.paddingRight = 14f;
		button.style.marginRight = 8f;
		button.style.fontSize = 16.1f;
		button.style.unityTextAlign = TextAnchor.MiddleCenter;
		button.style.borderTopWidth = 1f;
		button.style.borderBottomWidth = 1f;
		button.style.borderLeftWidth = 1f;
		button.style.borderRightWidth = 1f;
		button.style.borderTopColor = new Color(0.8f, 0.83f, 0.89f, 0.35f);
		button.style.borderBottomColor = new Color(0.8f, 0.83f, 0.89f, 0.35f);
		button.style.borderLeftColor = new Color(0.8f, 0.83f, 0.89f, 0.35f);
		button.style.borderRightColor = new Color(0.8f, 0.83f, 0.89f, 0.35f);
		_boxscoreTabButtons.AddOrUpdate(tabName, button);
		return button;
	}

	private static void UpdateBoxscoreTabButtonStyles()
	{
		foreach (string item in new List<string>(_boxscoreTabButtons.Keys))
		{
			if (!_boxscoreTabButtons.TryGetValue(item, out var value) || value == null)
			{
				continue;
			}
			bool flag = string.Equals(item, _boxscoreActiveTab, StringComparison.OrdinalIgnoreCase);
			value.style.backgroundColor = (flag ? new Color(0.28f, 0.35f, 0.47f, 0.98f) : new Color(0.12f, 0.15f, 0.2f, 0.96f));
			value.style.color = (flag ? new Color(0.99f, 0.99f, 1f, 1f) : new Color(0.82f, 0.85f, 0.91f, 0.95f));
			value.style.unityFontStyleAndWeight = (flag ? FontStyle.Bold : FontStyle.Normal);
		}
	}

	private static void SwitchBoxscoreTab(string tabName)
	{
		string text = NormalizeBoxscoreTab(tabName);
		if (!_boxscoreVisible)
		{
			_boxscoreActiveTab = text;
			UpdateBoxscoreTabButtonStyles();
			return;
		}
		if (string.Equals(_boxscoreActiveTab, text, StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		_boxscoreActiveTab = text;
		UpdateBoxscoreTabButtonStyles();
		RequestBoxscoreSnapshot(text);
	}

	private static void SetBoxscoreLoadingState(string message)
	{
		if (_boxscoreContentRoot == null)
		{
			return;
		}
		_boxscoreContentRoot.Clear();
		if (_boxscoreLoadingLabel == null)
		{
			_boxscoreLoadingLabel = new Label();
		}
		_boxscoreLoadingLabel.text = message;
		_boxscoreLoadingLabel.style.display = DisplayStyle.Flex;
		_boxscoreContentRoot.Add(_boxscoreLoadingLabel);
	}

	private static void ShowBoxscorePanel()
	{
		try
		{
			EnsureClientScoreboardState();
			if (_serverStatsCompatibilityDisabled || !IsClientStatsUiAuthorized())
			{
				return;
			}
			EnsureBoxscorePanelUI();
			if (_boxscoreOverlay == null)
			{
				return;
			}
			_boxscoreVisible = true;
			ApplyBoxscorePanelSizeFromConfig();
			_boxscoreOverlay.style.display = DisplayStyle.Flex;
			UpdateBoxscoreHintPromptState();
			EnsureBoxscoreOverlayView();
			if (_boxscoreOverlayView != null)
			{
				_boxscoreOverlayView.IsVisible = true;
				_boxscoreOverlayView.IsFocused = true;
			}
			_boxscoreSummaryLabel.text = BuildBoxscoreSummary();
			RefreshBoxscoreTabButtonVisibility(refreshVisiblePanel: false);
			UpdateBoxscoreTabButtonStyles();
			RefreshUiManagerViewState(MonoBehaviourSingleton<UIManager>.Instance);
			RequestBoxscoreSnapshot(_boxscoreActiveTab);
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error showing boxscore panel: {arg}", _clientConfig);
		}
	}

	private static void HideBoxscorePanel(bool setPauseSuppression = false)
	{
		try
		{
			if (setPauseSuppression)
			{
				_boxscorePauseSuppressionFrame = Time.frameCount;
			}
			_boxscoreRequestSequence++;
			_boxscoreShootoutRefreshToken++;
			_boxscoreVisible = false;
			if (_boxscoreOverlay != null)
			{
				_boxscoreOverlay.style.display = DisplayStyle.None;
			}
			EndBoxscoreResize(saveSize: false);
			if (_boxscoreOverlayView != null)
			{
				_boxscoreOverlayView.IsFocused = false;
				_boxscoreOverlayView.IsVisible = false;
			}
			RefreshUiManagerViewState(MonoBehaviourSingleton<UIManager>.Instance);
			UpdateBoxscoreHintPromptState();
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error hiding boxscore panel: {arg}", _clientConfig);
		}
	}

	private static void DisposeBoxscorePanel()
	{
		try
		{
			UIManager instance = MonoBehaviourSingleton<UIManager>.Instance;
			_boxscoreRequestSequence++;
			_boxscoreVisible = false;
			EndBoxscoreResize(saveSize: false);
			if (_boxscoreOverlay != null)
			{
				_boxscoreOverlay.RemoveFromHierarchy();
			}
			_boxscoreOverlay = null;
			_boxscorePanel = null;
			_boxscoreResizeHandle = null;
			_boxscoreSummaryLabel = null;
			_boxscoreLoadingLabel = null;
			_boxscoreContentRoot = null;
			_renderedBoxscorePayload = null;
			_renderedBoxscoreTab = null;
			_renderedBoxscoreFirstChild = null;
			_boxscoreTabButtons.Clear();
			if (_boxscoreOverlayView != null)
			{
				_boxscoreOverlayView.IsFocused = false;
				_boxscoreOverlayView.IsVisible = false;
			}
			RefreshUiManagerViewState(instance);
			UnregisterBoxscoreOverlayView();
			RefreshUiManagerViewState(instance);
			if (_boxscoreOverlayView != null)
			{
				UnityEngine.Object.Destroy(_boxscoreOverlayView);
				_boxscoreOverlayView = null;
			}
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error disposing boxscore panel: {arg}", _clientConfig);
		}
	}

	private static void RequestBoxscoreSnapshot(string requestedTab)
	{
		string normalizedTab = NormalizeBoxscoreTab(requestedTab);
		if (!_boxscoreVisible)
		{
			return;
		}
		RequestBoxscoreSnapshotInternal(normalizedTab, ++_boxscoreRequestSequence, 0);
	}

	private static void RequestBoxscoreSnapshotInternal(string normalizedTab, int requestSequence, int retryAttempt)
	{
		if (!_boxscoreVisible || requestSequence != _boxscoreRequestSequence || NormalizeBoxscoreTab(_boxscoreActiveTab) != normalizedTab)
		{
			return;
		}
		EnsureBoxscorePanelUI();
		try
		{
			if (NetworkManager.Singleton == null || NetworkManager.Singleton.CustomMessagingManager == null)
			{
				SetBoxscoreLoadingState("Boxscore unavailable: no active connection.");
				return;
			}
			EnsureClientScoreboardState();
			if (_serverStatsCompatibilityDisabled)
			{
				SetBoxscoreLoadingState(string.IsNullOrWhiteSpace(_serverStatsCompatibilityDisabledReason) ? "Boxscore unavailable: server is running an incompatible stats mod." : _serverStatsCompatibilityDisabledReason);
				return;
			}
			if (!_serverHasResponded)
			{
				SetBoxscoreLoadingState("Waiting for server stats...");
				ScheduleBoxscoreSnapshotRetry(normalizedTab, requestSequence, retryAttempt + 1);
				return;
			}
			if (!_serverSignatureConfirmed)
			{
				SetBoxscoreLoadingState("Boxscore unavailable: this server is not running DomaStatsBranch.");
				return;
			}
			SetBoxscoreLoadingState("Loading " + normalizedTab + "...");
			NetworkCommunication.SendData("oomtm450_statsBOXSCORE_REQUEST", normalizedTab, 0uL, "oomtm450_stats_client", _clientConfig);
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error requesting boxscore snapshot: {arg}", _clientConfig);
			SetBoxscoreLoadingState("Boxscore request failed.");
		}
	}

	private static void ScheduleBoxscoreSnapshotRetry(string normalizedTab, int requestSequence, int retryAttempt)
	{
		if (retryAttempt > 40)
		{
			if (_boxscoreVisible && requestSequence == _boxscoreRequestSequence && NormalizeBoxscoreTab(_boxscoreActiveTab) == normalizedTab)
			{
				SetBoxscoreLoadingState("Boxscore unavailable: server stats timed out.");
			}
			return;
		}
		VisualElement visualElement = _boxscoreOverlay ?? _boxscoreContentRoot ?? MonoBehaviourSingleton<UIManager>.Instance?.RootVisualElement;
		if (visualElement == null)
		{
			SetBoxscoreLoadingState("Boxscore unavailable: UI not ready.");
			return;
		}
		visualElement.schedule.Execute((Action)delegate
		{
			RequestBoxscoreSnapshotInternal(normalizedTab, requestSequence, retryAttempt);
		}).ExecuteLater(250L);
	}

	private static void ReceiveBoxscoreLeadersSnapshot(string payload)
	{
		if (!_boxscoreVisible || NormalizeBoxscoreTab(_boxscoreActiveTab) != BOXSCORE_TAB_LEADERS) return;
        if (IsBoxscorePayloadAlreadyRendered(payload, BOXSCORE_TAB_LEADERS)) return;
		BoxscoreLeadersSnapshot boxscoreLeadersSnapshot = null;
		try
		{
			boxscoreLeadersSnapshot = JsonConvert.DeserializeObject<BoxscoreLeadersSnapshot>(payload);
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error parsing leaders snapshot: {arg}", _clientConfig);
		}
		if (boxscoreLeadersSnapshot == null || !_boxscoreVisible || NormalizeBoxscoreTab(_boxscoreActiveTab) != BOXSCORE_TAB_LEADERS)
		{
			return;
		}
		UpdateClientBoxscoreAvailableTabsFromSnapshot(boxscoreLeadersSnapshot);
		RenderBoxscoreLeadersSnapshot(boxscoreLeadersSnapshot);
        RememberRenderedBoxscorePayload(payload, BOXSCORE_TAB_LEADERS);
	}

	private static void ReceiveBoxscoreGameStatsSnapshot(string payload)
	{
		if (!_boxscoreVisible || NormalizeBoxscoreTab(_boxscoreActiveTab) != BOXSCORE_TAB_GAMESTATS) return;
        if (IsBoxscorePayloadAlreadyRendered(payload, BOXSCORE_TAB_GAMESTATS)) return;
		BoxscoreGameStatsSnapshot boxscoreGameStatsSnapshot = null;
		try
		{
			boxscoreGameStatsSnapshot = JsonConvert.DeserializeObject<BoxscoreGameStatsSnapshot>(payload);
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error parsing game stats snapshot: {arg}", _clientConfig);
		}
		if (boxscoreGameStatsSnapshot == null || !_boxscoreVisible || NormalizeBoxscoreTab(_boxscoreActiveTab) != BOXSCORE_TAB_GAMESTATS)
		{
			return;
		}
		UpdateClientBoxscoreAvailableTabsFromSnapshot(boxscoreGameStatsSnapshot);
		RenderBoxscoreGameStatsSnapshot(boxscoreGameStatsSnapshot);
        RememberRenderedBoxscorePayload(payload, BOXSCORE_TAB_GAMESTATS);
	}

	private static void ReceiveBoxscorePlayerStatsSnapshot(string payload)
	{
		if (!_boxscoreVisible || NormalizeBoxscoreTab(_boxscoreActiveTab) != BOXSCORE_TAB_PLAYERSTATS) return;
        if (IsBoxscorePayloadAlreadyRendered(payload, BOXSCORE_TAB_PLAYERSTATS)) return;
		BoxscorePlayerStatsSnapshot boxscorePlayerStatsSnapshot = null;
		try
		{
			boxscorePlayerStatsSnapshot = JsonConvert.DeserializeObject<BoxscorePlayerStatsSnapshot>(payload);
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error parsing player stats snapshot: {arg}", _clientConfig);
		}
		if (boxscorePlayerStatsSnapshot == null || !_boxscoreVisible || NormalizeBoxscoreTab(_boxscoreActiveTab) != BOXSCORE_TAB_PLAYERSTATS)
		{
			return;
		}
		UpdateClientBoxscoreAvailableTabsFromSnapshot(boxscorePlayerStatsSnapshot);
		RenderBoxscorePlayerStatsSnapshot(boxscorePlayerStatsSnapshot);
        RememberRenderedBoxscorePayload(payload, BOXSCORE_TAB_PLAYERSTATS);
	}

	private static Label CreateBoxscoreSectionTitle(string text)
	{
		Label label = new Label(text);
		label.style.fontSize = 18f;
		label.style.unityFontStyleAndWeight = FontStyle.Bold;
		label.style.color = new Color(0.97f, 0.98f, 1f, 1f);
		label.style.marginBottom = 12f;
		label.style.marginTop = 6f;
		return label;
	}

	private static Label CreateBoxscoreSectionTitle(string text, Color color)
	{
		Label boxscoreSectionTitle = CreateBoxscoreSectionTitle(text);
		boxscoreSectionTitle.style.color = color;
		return boxscoreSectionTitle;
	}

	private static Label CreateBoxscorePlayerStatsSectionTitle(string text, Color color)
	{
		Label boxscoreSectionTitle = CreateBoxscoreSectionTitle(text, color);
		boxscoreSectionTitle.style.marginLeft = 10f;
		boxscoreSectionTitle.style.marginRight = 10f;
		boxscoreSectionTitle.style.marginTop = 10f;
		boxscoreSectionTitle.style.marginBottom = 14f;
		return boxscoreSectionTitle;
	}

	private static Label CreateBoxscoreBodyLabel(string text, float fontSize = 14f, Color? colorOverride = null)
	{
		Label label = new Label(text);
		label.style.fontSize = fontSize;
		label.style.color = colorOverride ?? new Color(0.87f, 0.89f, 0.95f, 1f);
		return label;
	}

	private static ScrollView CreateVerticalBoxscoreScrollView()
	{
		ScrollView scrollView = new ScrollView(ScrollViewMode.Vertical);
		scrollView.style.flexGrow = 1f;
		scrollView.style.backgroundColor = new Color(0.04f, 0.05f, 0.08f, 0.35f);
		scrollView.style.borderTopWidth = 1f;
		scrollView.style.borderBottomWidth = 1f;
		scrollView.style.borderLeftWidth = 1f;
		scrollView.style.borderRightWidth = 1f;
		scrollView.style.borderTopColor = new Color(0.8f, 0.82f, 0.9f, 0.18f);
		scrollView.style.borderBottomColor = new Color(0.8f, 0.82f, 0.9f, 0.18f);
		scrollView.style.borderLeftColor = new Color(0.8f, 0.82f, 0.9f, 0.18f);
		scrollView.style.borderRightColor = new Color(0.8f, 0.82f, 0.9f, 0.18f);
		scrollView.style.paddingLeft = 14f;
		scrollView.style.paddingRight = 14f;
		scrollView.style.paddingTop = 12f;
		scrollView.style.paddingBottom = 12f;
		return scrollView;
	}

	private static VisualElement CreateBoxscoreGameStatsTeamHeader(string teamName, string scoreText, Color accentColor, TextAnchor titleAlignment)
	{
		VisualElement visualElement = new VisualElement();
		visualElement.style.flexBasis = BOXSCORE_TEAM_STATS_SIDE_WIDTH;
		visualElement.style.minWidth = BOXSCORE_TEAM_STATS_SIDE_WIDTH;
		visualElement.style.alignItems = Align.Stretch;
		Label label = CreateBoxscoreBodyLabel(teamName, 18.4f, accentColor);
		label.style.unityFontStyleAndWeight = FontStyle.Bold;
		label.style.unityTextAlign = titleAlignment;
		label.style.width = Length.Percent(100f);
		visualElement.Add(label);
		if (!string.IsNullOrWhiteSpace(scoreText))
		{
			Label label2 = CreateBoxscoreBodyLabel(scoreText, 28f, accentColor);
			label2.style.unityFontStyleAndWeight = FontStyle.Bold;
			label2.style.unityTextAlign = TextAnchor.MiddleCenter;
			label2.style.width = Length.Percent(100f);
			visualElement.Add(label2);
		}
		return visualElement;
	}

	private static void RenderBoxscoreLeadersSnapshot(BoxscoreLeadersSnapshot snapshot)
	{
		if (_boxscoreContentRoot == null || snapshot == null)
		{
			return;
		}
		_boxscoreContentRoot.Clear();
		_boxscoreSummaryLabel.text = string.IsNullOrWhiteSpace(snapshot.Summary) ? BuildBoxscoreSummary() : snapshot.Summary;
		if (!string.IsNullOrWhiteSpace(snapshot.Error))
		{
			_boxscoreContentRoot.Add(CreateBoxscoreBodyLabel(snapshot.Error, 14f, new Color(1f, 0.72f, 0.72f, 1f)));
			return;
		}
		ScrollView scrollView = CreateVerticalBoxscoreScrollView();
		_boxscoreContentRoot.Add(scrollView);
		scrollView.Add(CreateBoxscoreSectionTitle("Three Stars"));
		VisualElement visualElement = new VisualElement();
		visualElement.style.flexDirection = FlexDirection.Column;
		foreach (BoxscoreStarSnapshot star in snapshot.Stars ?? new List<BoxscoreStarSnapshot>())
		{
			VisualElement visualElement2 = new VisualElement();
			visualElement2.style.marginBottom = 10f;
			visualElement2.style.backgroundColor = new Color(0.1f, 0.12f, 0.17f, 0.95f);
			visualElement2.style.borderTopWidth = 1f;
			visualElement2.style.borderBottomWidth = 1f;
			visualElement2.style.borderLeftWidth = 1f;
			visualElement2.style.borderRightWidth = 1f;
			Color teamAccentColor = GetTeamAccentColor(star.Team);
			visualElement2.style.borderTopColor = teamAccentColor;
			visualElement2.style.borderBottomColor = new Color(teamAccentColor.r, teamAccentColor.g, teamAccentColor.b, 0.5f);
			visualElement2.style.borderLeftColor = new Color(teamAccentColor.r, teamAccentColor.g, teamAccentColor.b, 0.5f);
			visualElement2.style.borderRightColor = new Color(teamAccentColor.r, teamAccentColor.g, teamAccentColor.b, 0.5f);
			visualElement2.style.paddingTop = 12f;
			visualElement2.style.paddingBottom = 12f;
			visualElement2.style.paddingLeft = 14f;
			visualElement2.style.paddingRight = 14f;
			Label label = new Label($"#{star.Rank} Star");
			label.style.fontSize = 12f;
			label.style.unityFontStyleAndWeight = FontStyle.Bold;
			label.style.color = new Color(0.95f, 0.96f, 0.99f, 0.88f);
			Label label2 = new Label(star.Name);
			label2.style.fontSize = 18f;
			label2.style.unityFontStyleAndWeight = FontStyle.Bold;
			label2.style.color = new Color(1f, 1f, 1f, 1f);
			Label label3 = CreateBoxscoreBodyLabel($"{star.Team} â€¢ {star.Position}", 13f, teamAccentColor);
			Label label4 = CreateBoxscoreBodyLabel(star.Summary, 13f, new Color(0.84f, 0.87f, 0.93f, 1f));
			Label label5 = CreateBoxscoreBodyLabel($"Star Points: {star.Score:0.0}", 13f, new Color(0.95f, 0.95f, 0.82f, 1f));
			visualElement2.Add(label);
			visualElement2.Add(label2);
			visualElement2.Add(label3);
			visualElement2.Add(label4);
			visualElement2.Add(label5);
			visualElement.Add(visualElement2);
		}
		scrollView.Add(visualElement);
		scrollView.Add(CreateBoxscoreSectionTitle("Live Leaders"));
		foreach (BoxscoreLeaderRowSnapshot leader in snapshot.Leaders ?? new List<BoxscoreLeaderRowSnapshot>())
		{
			VisualElement visualElement3 = new VisualElement();
			visualElement3.style.flexDirection = FlexDirection.Row;
			visualElement3.style.alignItems = Align.Center;
			visualElement3.style.justifyContent = Justify.SpaceBetween;
			visualElement3.style.paddingTop = 8f;
			visualElement3.style.paddingBottom = 8f;
			visualElement3.style.paddingLeft = 10f;
			visualElement3.style.paddingRight = 24f;
			visualElement3.style.marginBottom = 6f;
			visualElement3.style.backgroundColor = new Color(0.09f, 0.11f, 0.15f, 0.96f);
			Color teamAccentColor2 = GetTeamAccentColor(leader.Team);
			visualElement3.style.borderLeftWidth = 3f;
			visualElement3.style.borderLeftColor = teamAccentColor2;
			Label label6 = CreateBoxscoreBodyLabel(leader.Category, 14f, new Color(0.93f, 0.95f, 0.98f, 1f));
			label6.style.minWidth = 150f;
			label6.style.unityFontStyleAndWeight = FontStyle.Bold;
			VisualElement visualElement4 = new VisualElement();
			visualElement4.style.flexGrow = 1f;
			visualElement4.style.marginLeft = 12f;
			visualElement4.style.marginRight = 12f;
			Label label7 = new Label(leader.Name);
			label7.style.fontSize = 15f;
			label7.style.unityFontStyleAndWeight = FontStyle.Bold;
			label7.style.color = new Color(1f, 1f, 1f, 1f);
			Label label8 = CreateBoxscoreBodyLabel($"{leader.Team} â€¢ {leader.Position}" + (string.IsNullOrWhiteSpace(leader.Detail) ? "" : " â€¢ " + leader.Detail), 12f, new Color(0.8f, 0.84f, 0.91f, 0.96f));
			Label label9 = CreateBoxscoreBodyLabel(leader.Value, 15f, teamAccentColor2);
			label9.style.minWidth = 120f;
			label9.style.marginRight = 10f;
			label9.style.unityTextAlign = TextAnchor.MiddleRight;
			label9.style.unityFontStyleAndWeight = FontStyle.Bold;
			visualElement4.Add(label7);
			visualElement4.Add(label8);
			visualElement3.Add(label6);
			visualElement3.Add(visualElement4);
			visualElement3.Add(label9);
			scrollView.Add(visualElement3);
		}
	}

	private static void RenderBoxscoreGameStatsSnapshot(BoxscoreGameStatsSnapshot snapshot)
	{
		if (_boxscoreContentRoot == null || snapshot == null)
		{
			return;
		}
		_boxscoreContentRoot.Clear();
		_boxscoreSummaryLabel.text = string.IsNullOrWhiteSpace(snapshot.Summary) ? BuildBoxscoreSummary() : snapshot.Summary;
		if (!string.IsNullOrWhiteSpace(snapshot.Error))
		{
			_boxscoreContentRoot.Add(CreateBoxscoreBodyLabel(snapshot.Error, 14f, new Color(1f, 0.72f, 0.72f, 1f)));
			return;
		}
		ScrollView scrollView = CreateVerticalBoxscoreScrollView();
		_boxscoreContentRoot.Add(scrollView);
		VisualElement visualElement = new VisualElement();
		visualElement.style.flexDirection = FlexDirection.Row;
		visualElement.style.alignItems = Align.FlexEnd;
		visualElement.style.paddingLeft = 10f;
		visualElement.style.paddingRight = 10f;
		visualElement.style.paddingBottom = 14f;
		visualElement.Add(CreateBoxscoreGameStatsTeamHeader("RED", "", GetTeamAccentColor("Red"), TextAnchor.MiddleRight));
		VisualElement visualElement2 = new VisualElement();
		visualElement2.style.flexGrow = 1f;
		visualElement2.style.alignItems = Align.Center;
		Label label = CreateBoxscoreBodyLabel("TEAM STATS", 20.2f, new Color(0.9f, 0.93f, 0.99f, 0.9f));
		label.style.unityFontStyleAndWeight = FontStyle.Bold;
		visualElement2.Add(label);
		visualElement.Add(visualElement2);
		visualElement.Add(CreateBoxscoreGameStatsTeamHeader("BLUE", "", GetTeamAccentColor("Blue"), TextAnchor.MiddleLeft));
		scrollView.Add(visualElement);
		foreach (BoxscoreTeamStatRowSnapshot row in snapshot.Rows ?? new List<BoxscoreTeamStatRowSnapshot>())
		{
			VisualElement visualElement3 = new VisualElement();
			visualElement3.style.flexDirection = FlexDirection.Row;
			visualElement3.style.alignItems = Align.Center;
			visualElement3.style.paddingTop = 8f;
			visualElement3.style.paddingBottom = 8f;
			visualElement3.style.paddingLeft = 10f;
			visualElement3.style.paddingRight = 10f;
			visualElement3.style.marginBottom = 4f;
			visualElement3.style.backgroundColor = new Color(0.09f, 0.11f, 0.15f, 0.96f);
			Label label2 = CreateBoxscoreBodyLabel(row.RedValue, 16f, row.HighlightRed ? GetTeamAccentColor("Red") : new Color(0.97f, 0.98f, 1f, 1f));
			label2.style.flexBasis = BOXSCORE_TEAM_STATS_SIDE_WIDTH;
			label2.style.minWidth = BOXSCORE_TEAM_STATS_SIDE_WIDTH;
			label2.style.unityTextAlign = TextAnchor.MiddleRight;
			label2.style.unityFontStyleAndWeight = FontStyle.Bold;
			Label label3 = CreateBoxscoreBodyLabel(row.Label, 16f, new Color(0.95f, 0.97f, 0.99f, 1f));
			label3.style.flexGrow = 1f;
			label3.style.unityTextAlign = TextAnchor.MiddleCenter;
			label3.style.unityFontStyleAndWeight = FontStyle.Bold;
			Label label4 = CreateBoxscoreBodyLabel(row.BlueValue, 16f, row.HighlightBlue ? GetTeamAccentColor("Blue") : new Color(0.97f, 0.98f, 1f, 1f));
			label4.style.flexBasis = BOXSCORE_TEAM_STATS_SIDE_WIDTH;
			label4.style.minWidth = BOXSCORE_TEAM_STATS_SIDE_WIDTH;
			label4.style.unityTextAlign = TextAnchor.MiddleLeft;
			label4.style.unityFontStyleAndWeight = FontStyle.Bold;
			visualElement3.Add(label2);
			visualElement3.Add(label3);
			visualElement3.Add(label4);
			scrollView.Add(visualElement3);
		}
	}

	private static float GetBoxscoreColumnWidth(string columnName)
	{
		return columnName switch
		{
			"Team" => 76f,
			"Pos" => 64f,
			"Name" => 200f,
			"G" => 50f,
			"A" => 50f,
			"Goals" => 58f,
			"Assists" => 64f,
			"SOG" => 58f,
			"iCF" => 58f,
			"iCF%" => 74f,
			"SP" => 58f,
			"Pass" => 70f,
			"Exit" => 62f,
			"Entry" => 70f,
			"Hit" => 54f,
			"xHit" => 62f,
			"TO" => 54f,
			"TKA" => 62f,
			"Touches" => 90f,
			"Poss" => 88f,
			"Post" => 62f,
			"+/-" => 60f,
			"Passes" => 70f,
			"Exits" => 62f,
			"Entries" => 70f,
			"Hits" => 58f,
			"Turnovers" => 84f,
			"Takeaways" => 84f,
			"PuckTouches" => 90f,
			"Possession" => 88f,
			"TOI" => 72f,
			"DZ Poss" => 78f,
			"NZ Poss" => 78f,
			"OZ Poss" => 78f,
			"DZ TOI" => 74f,
			"NZ TOI" => 74f,
			"OZ TOI" => 74f,
			"PostHits" => 74f,
			"FOW" => 54f,
			"FOL" => 54f,
			"xG" => 62f,
			"PlusMinus" => 78f,
			"SA" => 58f,
			"SV" => 58f,
			"BodySv" => 86f,
			"PadSv" => 78f,
			"StickSv" => 88f,
			"ShotsFaced" => 84f,
			"Saves" => 62f,
			"GA" => 50f,
			"SV%" => 60f,
			"BodySaves" => 86f,
			"StickSaves" => 88f,
			"xGA" => 64f,
			"GSAx" => 68f,
			_ => 84f,
		};
	}

	private static VisualElement CreateBoxscoreTableCell(string text, float width, bool isHeader, TextAnchor alignment, Color color, Color backgroundColor)
	{
		Label label = new Label(text ?? "");
		label.style.width = width;
		label.style.minWidth = width;
		label.style.paddingLeft = 9f;
		label.style.paddingRight = 9f;
		label.style.paddingTop = 7f;
		label.style.paddingBottom = 7f;
		label.style.unityTextAlign = alignment;
		label.style.fontSize = isHeader ? 16.1f : 14.95f;
		label.style.unityFontStyleAndWeight = isHeader ? FontStyle.Bold : FontStyle.Normal;
		label.style.color = color;
		label.style.backgroundColor = backgroundColor;
		return label;
	}

	private static VisualElement CreateBoxscorePlayerStatsTable(List<string> columns, List<BoxscorePlayerStatsRowSnapshot> rows)
	{
		VisualElement visualElement = new VisualElement();
		visualElement.style.flexDirection = FlexDirection.Column;
		visualElement.style.marginLeft = 10f;
		visualElement.style.marginRight = 10f;
		visualElement.style.marginBottom = 22f;
		float num = ((columns != null && columns.Count > 0) ? columns.Sum(GetBoxscoreColumnWidth) : 0f);
		visualElement.style.minWidth = Mathf.Max(num, 720f);
		VisualElement visualElement2 = new VisualElement();
		visualElement2.style.flexDirection = FlexDirection.Row;
		visualElement2.style.backgroundColor = new Color(0.16f, 0.19f, 0.24f, 0.98f);
		visualElement2.style.borderBottomWidth = 1f;
		visualElement2.style.borderBottomColor = new Color(1f, 1f, 1f, 0.08f);
		for (int i = 0; i < (columns?.Count ?? 0); i++)
		{
			string text = columns[i];
			visualElement2.Add(CreateBoxscoreTableCell(text, GetBoxscoreColumnWidth(text), isHeader: true, (i == 2) ? TextAnchor.MiddleLeft : TextAnchor.MiddleCenter, new Color(0.98f, 0.99f, 1f, 1f), new Color(0f, 0f, 0f, 0f)));
		}
		visualElement.Add(visualElement2);
		int num2 = 0;
		if (rows == null || rows.Count == 0)
		{
			VisualElement visualElement3 = new VisualElement();
			visualElement3.style.flexDirection = FlexDirection.Row;
			visualElement3.style.backgroundColor = new Color(0.09f, 0.11f, 0.15f, 0.96f);
			visualElement3.Add(CreateBoxscoreTableCell("No players yet.", Mathf.Max(num, 720f), isHeader: false, TextAnchor.MiddleLeft, new Color(0.88f, 0.9f, 0.95f, 1f), new Color(0f, 0f, 0f, 0f)));
			visualElement.Add(visualElement3);
			return visualElement;
		}
		foreach (BoxscorePlayerStatsRowSnapshot row in rows)
		{
			Color teamAccentColor = GetTeamAccentColor(row.Team);
			Color color = new Color(teamAccentColor.r, teamAccentColor.g, teamAccentColor.b, 0.11f + ((num2 % 2 == 0) ? 0.03f : 0f));
			VisualElement visualElement4 = new VisualElement();
			visualElement4.style.flexDirection = FlexDirection.Row;
			visualElement4.style.backgroundColor = color;
			visualElement4.style.borderBottomWidth = 1f;
			visualElement4.style.borderBottomColor = new Color(1f, 1f, 1f, 0.05f);
			for (int j = 0; j < row.Cells.Count && j < (columns?.Count ?? 0); j++)
			{
				string text2 = columns[j];
				visualElement4.Add(CreateBoxscoreTableCell(row.Cells[j], GetBoxscoreColumnWidth(text2), isHeader: false, (j == 2) ? TextAnchor.MiddleLeft : TextAnchor.MiddleCenter, new Color(0.88f, 0.9f, 0.95f, 1f), new Color(0f, 0f, 0f, 0f)));
			}
			visualElement.Add(visualElement4);
			num2++;
		}
		return visualElement;
	}

	private static void RenderBoxscorePlayerStatsSnapshot(BoxscorePlayerStatsSnapshot snapshot)
	{
		if (_boxscoreContentRoot == null || snapshot == null)
		{
			return;
		}
		_boxscoreContentRoot.Clear();
		_boxscoreSummaryLabel.text = string.IsNullOrWhiteSpace(snapshot.Summary) ? BuildBoxscoreSummary() : snapshot.Summary;
		if (!string.IsNullOrWhiteSpace(snapshot.Error))
		{
			_boxscoreContentRoot.Add(CreateBoxscoreBodyLabel(snapshot.Error, 14f, new Color(1f, 0.72f, 0.72f, 1f)));
			return;
		}
		ScrollView scrollView = new ScrollView(ScrollViewMode.VerticalAndHorizontal);
		scrollView.style.flexGrow = 1f;
		scrollView.style.backgroundColor = new Color(0.04f, 0.05f, 0.08f, 0.35f);
		scrollView.style.borderTopWidth = 1f;
		scrollView.style.borderBottomWidth = 1f;
		scrollView.style.borderLeftWidth = 1f;
		scrollView.style.borderRightWidth = 1f;
		scrollView.style.borderTopColor = new Color(0.8f, 0.82f, 0.9f, 0.18f);
		scrollView.style.borderBottomColor = new Color(0.8f, 0.82f, 0.9f, 0.18f);
		scrollView.style.borderLeftColor = new Color(0.8f, 0.82f, 0.9f, 0.18f);
		scrollView.style.borderRightColor = new Color(0.8f, 0.82f, 0.9f, 0.18f);
		_boxscoreContentRoot.Add(scrollView);
		VisualElement visualElement = new VisualElement();
		visualElement.style.flexDirection = FlexDirection.Column;
		visualElement.style.paddingBottom = 12f;
		scrollView.Add(visualElement);
		List<BoxscorePlayerStatsRowSnapshot> list = snapshot.SkaterRows?.Where((BoxscorePlayerStatsRowSnapshot row) => string.Equals(row.Team, "Red", StringComparison.OrdinalIgnoreCase)).ToList() ?? new List<BoxscorePlayerStatsRowSnapshot>();
		List<BoxscorePlayerStatsRowSnapshot> list2 = snapshot.GoalieRows?.Where((BoxscorePlayerStatsRowSnapshot row) => string.Equals(row.Team, "Red", StringComparison.OrdinalIgnoreCase)).ToList() ?? new List<BoxscorePlayerStatsRowSnapshot>();
		List<BoxscorePlayerStatsRowSnapshot> list3 = snapshot.SkaterRows?.Where((BoxscorePlayerStatsRowSnapshot row) => string.Equals(row.Team, "Blue", StringComparison.OrdinalIgnoreCase)).ToList() ?? new List<BoxscorePlayerStatsRowSnapshot>();
		List<BoxscorePlayerStatsRowSnapshot> list4 = snapshot.GoalieRows?.Where((BoxscorePlayerStatsRowSnapshot row) => string.Equals(row.Team, "Blue", StringComparison.OrdinalIgnoreCase)).ToList() ?? new List<BoxscorePlayerStatsRowSnapshot>();
		visualElement.Add(CreateBoxscorePlayerStatsSectionTitle("Red Skaters", GetTeamAccentColor("Red")));
		visualElement.Add(CreateBoxscorePlayerStatsTable(snapshot.SkaterColumns, list));
		visualElement.Add(CreateBoxscorePlayerStatsSectionTitle("Red Goalies", GetTeamAccentColor("Red")));
		visualElement.Add(CreateBoxscorePlayerStatsTable(snapshot.GoalieColumns, list2));
		visualElement.Add(CreateBoxscorePlayerStatsSectionTitle("Blue Skaters", GetTeamAccentColor("Blue")));
		visualElement.Add(CreateBoxscorePlayerStatsTable(snapshot.SkaterColumns, list3));
		visualElement.Add(CreateBoxscorePlayerStatsSectionTitle("Blue Goalies", GetTeamAccentColor("Blue")));
		visualElement.Add(CreateBoxscorePlayerStatsTable(snapshot.GoalieColumns, list4));
	}

	private static void SendRequestedBoxscoreSnapshot(ulong clientId, string requestedTab)
	{
		string text = NormalizeBoxscoreTab(requestedTab);
		try
		{
			if (TrySendPreviousGameBoxscoreSnapshotToClient(clientId, text))
			{
				return;
			}
			if (TrySendCurrentGameShootoutBoxscoreSnapshot(clientId, text))
			{
				return;
			}
			switch (text)
			{
			case "Leaders":
				NetworkCommunication.SendData("oomtm450_statsBOXSCORE_LEADERS", JsonConvert.SerializeObject(BuildLiveBoxscoreLeadersSnapshot()), clientId, "oomtm450_stats_server", ServerConfig);
				return;
			case "Game Stats":
				NetworkCommunication.SendData("oomtm450_statsBOXSCORE_GAMESTATS", JsonConvert.SerializeObject(BuildLiveBoxscoreGameStatsSnapshot()), clientId, "oomtm450_stats_server", ServerConfig);
				return;
			case "Player Stats":
				NetworkCommunication.SendData("oomtm450_statsBOXSCORE_PLAYERSTATS", JsonConvert.SerializeObject(BuildLiveBoxscorePlayerStatsSnapshot()), clientId, "oomtm450_stats_server", ServerConfig);
				return;
			case "Shootout":
				return;
			}
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error sending boxscore snapshot for {text}: {arg}", ServerConfig);
		}
	}

	private static void SetupTeamTooltips(VisualElement scoreboardContainer)
	{
		try
		{
			scoreboardContainer.schedule.Execute((Action)delegate
			{
				try
				{
					if (!_teamTooltipsSetup)
					{
						List<Label> list = scoreboardContainer.Query<Label>().ToList();
						Label label = null;
						Label label2 = null;
						foreach (Label item in list)
						{
							string text = item.name?.ToLower() ?? "";
							_ = item.text;
							if (text == "bluescore" && label == null)
							{
								label = item;
							}
							else if (text == "redscore" && label2 == null)
							{
								label2 = item;
							}
							else if (text.Contains("blue") && text.Contains("score") && label == null)
							{
								label = item;
							}
							else if (text.Contains("red") && text.Contains("score") && label2 == null)
							{
								label2 = item;
							}
						}
						if (label == null || label2 == null)
						{
							List<Label> list2 = (from l in list.Where(delegate(Label l)
								{
									if (l.text == null || l.text.Length == 0)
									{
										return false;
									}
									string text3 = l.name?.ToLower() ?? "";
									if (text3.Contains("ping") || text3.Contains("sog") || text3.Contains("tooltip") || text3.Contains("players") || text3.Contains("position") || text3.Contains("username") || text3.Contains("goals") || text3.Contains("assists") || text3.Contains("points"))
									{
										return false;
									}
									Rect layout3 = l.layout;
									return l.text.All((char c) => char.IsDigit(c) || c == '-') && layout3.y >= 0f && layout3.y < 50f;
								})
								orderby l.layout.x
								select l).ToList();
							if (list2.Count >= 2)
							{
								if (label == null)
								{
									label = list2[0];
								}
								if (label2 == null)
								{
									label2 = list2[list2.Count - 1];
								}
							}
							else if (list2.Count == 1)
							{
								Rect layout = list2[0].layout;
								Rect rect = list2[0].parent?.layout ?? default(Rect);
								float num = ((rect.width > 0f) ? (rect.width / 2f) : 0f);
								if (label == null && layout.x < num)
								{
									label = list2[0];
								}
								else if (label2 == null && layout.x >= num)
								{
									label2 = list2[0];
								}
							}
						}
						if (label == null || label2 == null)
						{
							try
							{
								UIDocument[] array = UnityEngine.Object.FindObjectsByType<UIDocument>(FindObjectsSortMode.None);
								foreach (UIDocument uIDocument in array)
								{
									if (uIDocument?.rootVisualElement != null)
									{
										List<Label> list3 = uIDocument.rootVisualElement.Query<Label>().ToList();
										foreach (Label item2 in list3)
										{
											string text2 = item2.name?.ToLower() ?? "";
											if (text2 == "bluescore" && label == null)
											{
												label = item2;
											}
											else if (text2 == "redscore" && label2 == null)
											{
												label2 = item2;
											}
										}
										if (label == null || label2 == null)
										{
											List<Label> list4 = (from l in list3.Where(delegate(Label l)
												{
													if (l.text == null || l.text.Length == 0)
													{
														return false;
													}
													string text3 = l.name?.ToLower() ?? "";
													if (text3.Contains("ping") || text3.Contains("sog") || text3.Contains("tooltip") || text3.Contains("players") || text3.Contains("position") || text3.Contains("username") || text3.Contains("goals") || text3.Contains("assists") || text3.Contains("points") || text3.Contains("title") || text3.Contains("name"))
													{
														return false;
													}
													bool num4 = l.text.All((char c) => char.IsDigit(c) || c == '-');
													Rect layout3 = l.layout;
													return num4 && layout3.y >= 0f && layout3.y < 200f && layout3.height > 10f;
												})
												orderby l.layout.x
												select l).ToList();
											if (list4.Count >= 2)
											{
												if (label == null)
												{
													label = list4[0];
												}
												if (label2 == null)
												{
													label2 = list4[list4.Count - 1];
												}
											}
											else if (list4.Count == 1)
											{
												Rect layout2 = list4[0].layout;
												Rect rect2 = list4[0].parent?.layout ?? default(Rect);
												float num3 = ((rect2.width > 0f) ? (rect2.width / 2f) : 0f);
												if (label == null && layout2.x < num3)
												{
													label = list4[0];
												}
												else if (label2 == null && layout2.x >= num3)
												{
													label2 = list4[0];
												}
											}
										}
										if (label != null && label2 != null)
										{
											break;
										}
									}
								}
							}
							catch (Exception arg2)
							{
								Logging.LogError($"Error searching all UI documents: {arg2}", _clientConfig);
							}
						}
						if (label != null)
						{
							label.pickingMode = PickingMode.Position;
							SetupTeamTooltip(label, PlayerTeam.Red, label);
						}
						if (label2 != null)
						{
							label2.pickingMode = PickingMode.Position;
							SetupTeamTooltip(label2, PlayerTeam.Blue, label2);
						}
						_teamTooltipsSetup = true;
					}
				}
				catch (Exception arg3)
				{
					Logging.LogError($"Error in scheduled SetupTeamTooltips: {arg3}", _clientConfig);
				}
			}).ExecuteLater(500L);
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error setting up team tooltips: {arg}", _clientConfig);
		}
	}

	private static void SetupTeamTooltip(Label scoreLabel, PlayerTeam team, Label referenceLabel)
	{
		string text = ((team == PlayerTeam.Blue) ? "Blue Team" : "Red Team");
		string text2 = team.ToString();
		if (_teamTooltips.TryGetValue(team, out var value))
		{
			DisposeTooltipCallbacks(value);
			_tooltipMoveTimes.Remove(value);
			value.style.display = DisplayStyle.None;
			value.parent?.Remove(value);
			_teamTooltips.Remove(team);
		}
		_teamTooltipLabels.Remove(team);
		VisualElement tooltip = new VisualElement
		{
			name = "TeamTooltip_" + text2
		};
		tooltip.style.position = Position.Absolute;
		if (team == PlayerTeam.Blue)
		{
			tooltip.style.backgroundColor = new Color(0.15f, 0.25f, 0.35f, 0.95f);
		}
		else
		{
			tooltip.style.backgroundColor = new Color(0.35f, 0.2f, 0.2f, 0.95f);
		}
		tooltip.style.borderTopWidth = 2f;
		tooltip.style.borderBottomWidth = 2f;
		tooltip.style.borderLeftWidth = 2f;
		tooltip.style.borderRightWidth = 2f;
		tooltip.style.borderTopColor = Color.white;
		tooltip.style.borderBottomColor = Color.white;
		tooltip.style.borderLeftColor = Color.white;
		tooltip.style.borderRightColor = Color.white;
		tooltip.style.paddingTop = 10f;
		tooltip.style.paddingBottom = 10f;
		tooltip.style.paddingLeft = 15f;
		tooltip.style.paddingRight = 15f;
		tooltip.style.minWidth = 280f;
		tooltip.style.width = 280f;
		tooltip.style.maxWidth = 280f;
		tooltip.style.height = StyleKeyword.Auto;
		tooltip.style.display = DisplayStyle.None;
		tooltip.style.overflow = Overflow.Visible;
		tooltip.style.flexShrink = 0f;
		tooltip.style.flexDirection = FlexDirection.Column;
		tooltip.style.alignItems = Align.FlexStart;
		tooltip.style.flexWrap = Wrap.NoWrap;
		tooltip.style.visibility = Visibility.Visible;
		tooltip.style.opacity = 1f;
		float defaultFontSize = 13f;
		float num = 16f;
		Label label = new Label(text);
		label.name = "TooltipTeamTitle_" + text2;
		label.text = text;
		label.style.fontSize = num;
		label.style.unityFontStyleAndWeight = FontStyle.Bold;
		label.style.color = new StyleColor(new Color(1f, 1f, 1f, 1f));
		label.style.unityTextAlign = TextAnchor.UpperLeft;
		label.style.marginBottom = 8f;
		label.style.width = Length.Percent(100f);
		label.style.minHeight = 20f;
		label.style.height = StyleKeyword.Auto;
		label.style.display = DisplayStyle.Flex;
		label.style.visibility = Visibility.Visible;
		label.style.opacity = 1f;
		label.pickingMode = PickingMode.Ignore;
		tooltip.Add(label);
		TeamTooltipLabels teamTooltipLabels = new TeamTooltipLabels
		{
			Tooltip = tooltip,
			Title = label,
			ShotsOnGoal = CreateTeamStatLabel(tooltip, "SOGs", "TooltipTeamSOGs_" + text2, referenceLabel, defaultFontSize),
			FaceoffPercent = CreateTeamStatLabel(tooltip, "Faceoffs", "TooltipTeamFaceoffPct_" + text2, referenceLabel, defaultFontSize),
			Passes = CreateTeamStatLabel(tooltip, "Passes", "TooltipTeamPasses_" + text2, referenceLabel, defaultFontSize),
			Takeaways = CreateTeamStatLabel(tooltip, "Takeaways", "TooltipTeamTakeaways_" + text2, referenceLabel, defaultFontSize),
			DefensiveZoneExits = CreateTeamStatLabel(tooltip, "DZ Exits", "TooltipTeamDZExits_" + text2, referenceLabel, defaultFontSize),
			OffensiveZoneEntries = CreateTeamStatLabel(tooltip, "OZ Entries", "TooltipTeamOZEntries_" + text2, referenceLabel, defaultFontSize),
			Possession = CreateTeamStatLabel(tooltip, "Possession", "TooltipTeamPossession_" + text2, referenceLabel, defaultFontSize)
		};
		VisualElement parent = scoreLabel.parent;
		if (parent != null)
		{
			parent.Add(tooltip);
			parent.style.overflow = Overflow.Visible;
		}
		else
		{
			VisualElement privateField = GetScoreboardContainer();
			if (privateField != null)
			{
				privateField.Add(tooltip);
				privateField.style.overflow = Overflow.Visible;
			}
		}
		_teamTooltips.Add(team, tooltip);
		_teamTooltipLabels[team] = teamTooltipLabels;
		Action showTooltip = delegate
		{
			if (IsTrackedTeamTooltip(team, tooltip) && tooltip.style.display == DisplayStyle.Flex) return;
			_tooltipsAlreadyHidden = false;
			UpdateTeamTooltipStats(tooltip, team);
			tooltip.style.display = DisplayStyle.Flex;
			tooltip.BringToFront();
			UpdateTeamTooltipPosition(tooltip, scoreLabel, team);
			ScheduleVisibleTeamTooltipRefresh(team, tooltip);
		};
		Action hideTooltip = delegate
		{
			StopTooltipRefresh(tooltip);
			tooltip.style.display = DisplayStyle.None;
		};
		RegisterTooltipCallback<MouseEnterEvent>(tooltip, scoreLabel, delegate
		{
			showTooltip();
		});
		RegisterTooltipCallback<PointerEnterEvent>(tooltip, scoreLabel, delegate
		{
			showTooltip();
		});
		RegisterTooltipCallback<MouseLeaveEvent>(tooltip, scoreLabel, delegate
		{
			hideTooltip();
		});
		RegisterTooltipCallback<PointerLeaveEvent>(tooltip, scoreLabel, delegate
		{
			hideTooltip();
		});
		RegisterTooltipCallback<MouseMoveEvent>(tooltip, scoreLabel, delegate
		{
			if (IsTrackedTeamTooltip(team, tooltip) && tooltip.style.display == DisplayStyle.Flex)
			{
				float time = Time.time;
				if (!_tooltipMoveTimes.ContainsKey(tooltip) || time - _tooltipMoveTimes[tooltip] > 0.1f)
				{
					_tooltipMoveTimes[tooltip] = time;
					UpdateTeamTooltipPosition(tooltip, scoreLabel, team);
				}
			}
		});
	}

	private static Label CreateTeamStatLabel(VisualElement tooltip, string labelText, string labelName, Label referenceLabel, float defaultFontSize)
	{
		Label label = new Label(labelText + ": 0")
		{
			name = labelName
		};
		label.text = labelText + ": 0";
		label.style.fontSize = defaultFontSize;
		label.style.color = new StyleColor(new Color(1f, 1f, 1f, 1f));
		label.style.unityTextAlign = TextAnchor.UpperLeft;
		if (referenceLabel != null)
		{
			Font font = referenceLabel.resolvedStyle.unityFont;
			if (font != null)
			{
				label.style.unityFont = font;
			}
		}
		label.style.marginBottom = 2f;
		label.style.width = Length.Percent(100f);
		label.style.minHeight = 18f;
		label.style.height = StyleKeyword.Auto;
		label.style.display = DisplayStyle.Flex;
		label.style.visibility = Visibility.Visible;
		label.style.opacity = 1f;
		label.pickingMode = PickingMode.Ignore;
		label.style.textOverflow = TextOverflow.Clip;
		label.style.overflow = Overflow.Visible;
		tooltip.Add(label);
		return label;
	}

	private static void UpdateTeamTooltipStats(VisualElement tooltip, PlayerTeam team)
	{
		if (TryUpdateTeamTooltipFromPreviousGameSnapshot(tooltip, team))
		{
			return;
		}
		PlayerTeam key = ((team == PlayerTeam.Red) ? PlayerTeam.Blue : PlayerTeam.Red);
		int value;
		int num = (_teamPasses.TryGetValue(team, out value) ? value : 0);
		double value2;
		double num2 = (_teamPossessionTime.TryGetValue(team, out value2) ? value2 : 0.0);
		int value3;
		int num3 = (_teamPasses.TryGetValue(key, out value3) ? value3 : 0);
		double value4;
		double num4 = (_teamPossessionTime.TryGetValue(key, out value4) ? value4 : 0.0);
		int value5;
		int num5 = (_teamShots.TryGetValue(team, out value5) ? value5 : 0);
		int value6;
		int num6 = (_teamShots.TryGetValue(key, out value6) ? value6 : 0);
		int value7;
		int num7 = (_teamFaceoffWins.TryGetValue(team, out value7) ? value7 : 0);
		int value8;
		int num8 = (_teamFaceoffTotal.TryGetValue(team, out value8) ? value8 : 0);
		double num9 = ((num8 > 0) ? ((double)num7 / (double)num8 * 100.0) : 0.0);
		int value9;
		int num10 = (_teamTakeaways.TryGetValue(team, out value9) ? value9 : 0);
		int value10;
		int num11 = (_teamTakeaways.TryGetValue(key, out value10) ? value10 : 0);
		int value11;
		int num12 = (_teamExits.TryGetValue(team, out value11) ? value11 : 0);
		int value12;
		int num13 = (_teamExits.TryGetValue(key, out value12) ? value12 : 0);
		int value13;
		int num14 = (_teamEntries.TryGetValue(team, out value13) ? value13 : 0);
		int value14;
		int num15 = (_teamEntries.TryGetValue(key, out value14) ? value14 : 0);
		TeamTooltipLabels orCreateTeamTooltipLabels = GetOrCreateTeamTooltipLabels(tooltip, team);
		Label label = orCreateTeamTooltipLabels?.ShotsOnGoal;
		if (label != null)
		{
			label.text = $"SOGs: {num5}-{num6}";
		}
		Label label2 = orCreateTeamTooltipLabels?.FaceoffPercent;
		if (label2 != null)
		{
			label2.text = $"Faceoffs: {num9:F0}% ({num7}/{num8})";
		}
		Label label3 = orCreateTeamTooltipLabels?.Passes;
		if (label3 != null)
		{
			label3.text = $"Passes: {num}-{num3}";
		}
		Label label4 = orCreateTeamTooltipLabels?.Takeaways;
		if (label4 != null)
		{
			label4.text = $"Takeaways: {num10}-{num11}";
		}
		Label label5 = orCreateTeamTooltipLabels?.DefensiveZoneExits;
		if (label5 != null)
		{
			label5.text = $"DZ Exits: {num12}-{num13}";
		}
		Label label6 = orCreateTeamTooltipLabels?.OffensiveZoneEntries;
		if (label6 != null)
		{
			label6.text = $"OZ Entries: {num14}-{num15}";
		}
		Label label7 = orCreateTeamTooltipLabels?.Possession;
		if (label7 != null)
		{
			string arg = FormatTimeAsMinutesSeconds(num2);
			string arg2 = FormatTimeAsMinutesSeconds(num4);
			double num16 = num2 + num4;
			double num17 = ((num16 > 0.0) ? (num2 / num16 * 100.0) : 0.0);
			label7.text = $"Possession: {num17:F0}% ({arg} - {arg2})";
		}
	}

	private static void UpdateTeamTooltipPosition(VisualElement tooltip, Label scoreLabel, PlayerTeam team)
	{
		try
		{
			VisualElement parent = tooltip.parent;
			if (parent == null)
			{
				return;
			}
			Rect layout = scoreLabel.layout;
			Rect layout2 = parent.layout;
			float num = ((tooltip.resolvedStyle.width > 0f) ? tooltip.resolvedStyle.width : 280f);
			float num2 = ((tooltip.resolvedStyle.height > 0f) ? tooltip.resolvedStyle.height : 200f);
			float num3 = layout.y;
			float num4;
			if (team == PlayerTeam.Blue)
			{
				num4 = layout.x - num - 10f;
				if (num4 < 0f)
				{
					num4 = layout.x + layout.width + 10f;
				}
			}
			else
			{
				num4 = layout.x + layout.width + 10f;
				if (num4 + num > layout2.width)
				{
					num4 = layout.x - num - 10f;
				}
			}
			if (num4 < 0f)
			{
				num4 = layout.x + layout.width / 2f - num / 2f;
			}
			if (num3 + num2 > layout2.height)
			{
				num3 = layout2.height - num2 - 10f;
			}
			if (num4 < 10f)
			{
				num4 = 10f;
			}
			if (num3 < 10f)
			{
				num3 = 10f;
			}
			tooltip.style.left = num4;
			tooltip.style.top = num3;
			tooltip.BringToFront();
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error updating team tooltip position: {arg}", _clientConfig);
		}
	}

	private static void UpdateTooltipPosition(VisualElement tooltip, Label nameLabel)
	{
		try
		{
			VisualElement parent = tooltip.parent;
			if (parent == null)
			{
				return;
			}
			Rect worldBound = nameLabel.worldBound;
			Rect worldBound2 = parent.worldBound;
			Rect layout = tooltip.layout;
			VisualElement scoreboardContainer = GetScoreboardContainer();
			Rect worldBound3 = scoreboardContainer?.worldBound ?? default(Rect);
			VisualElement rootVisualElement = MonoBehaviourSingleton<UIManager>.Instance?.RootVisualElement;
			Rect worldBound4 = ((rootVisualElement != null && rootVisualElement.worldBound.width > 0f) ? rootVisualElement.worldBound : worldBound2);
			if (worldBound.width > 0f && worldBound.height > 0f)
			{
				float num = ((layout.width > 0f) ? layout.width : 280f);
				float num2 = ((layout.height > 0f) ? layout.height : 150f);
				float num3 = worldBound.center.y - worldBound2.yMin - num2 / 2f;
				float num4;
				if (scoreboardContainer != null && worldBound3.width > 0f)
				{
					num4 = worldBound3.xMin - worldBound2.xMin - num - 12f;
				}
				else
				{
					num4 = worldBound.xMin - worldBound2.xMin - num - 10f;
				}
				float num5 = worldBound4.xMin - worldBound2.xMin + 10f;
				float num6 = worldBound4.xMax - worldBound2.xMin - num - 10f;
				if (num4 < num5)
				{
					float num7 = ((scoreboardContainer != null && worldBound3.width > 0f) ? (worldBound3.xMax - worldBound2.xMin + 12f) : (worldBound.xMax - worldBound2.xMin + 10f));
					num4 = Math.Min(num6, num7);
				}
				if (num3 + num2 > worldBound4.yMax - worldBound2.yMin)
				{
					num3 = worldBound4.yMax - worldBound2.yMin - num2 - 5f;
				}
				if (num3 < 5f)
				{
					num3 = 5f;
				}
				if (num4 < num5)
				{
					num4 = num5;
				}
				if (num4 > num6)
				{
					num4 = num6;
				}
				tooltip.style.left = num4;
				tooltip.style.top = num3;
				Logging.Log($"Tooltip positioned at ({num4}, {num3}) relative to parent. Label world: ({worldBound.x}, {worldBound.y}) size {worldBound.width}x{worldBound.height}, Parent world size: {worldBound2.width}x{worldBound2.height}", _clientConfig, bypassConfig: true);
			}
			else
			{
				Logging.LogError($"Label layout is invalid: {worldBound}", _clientConfig);
			}
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error updating tooltip position: {arg}", _clientConfig);
		}
	}

}
