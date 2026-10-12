using System;
using System.Collections.Generic;
using System.Linq;
using Codebase;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.UIElements;

namespace OPLStats_Stats;

public partial class Stats
{
	private static int _boxscoreShootoutRefreshToken = 0;

	private static void RefreshBoxscoreTabButtonVisibility(bool refreshVisiblePanel)
	{
		List<string> list = NormalizeBoxscoreAvailableTabs(_clientBoxscoreAvailableTabs);
		bool flag = !list.Contains(_boxscoreActiveTab);
		if (flag)
		{
			_boxscoreActiveTab = BOXSCORE_TAB_LEADERS;
		}
		foreach (string item in new List<string>(_boxscoreTabButtons.Keys))
		{
			if (_boxscoreTabButtons.TryGetValue(item, out var value) && value != null)
			{
				value.style.display = (list.Contains(item) ? DisplayStyle.Flex : DisplayStyle.None);
			}
		}
		UpdateBoxscoreTabButtonStyles();
		if (refreshVisiblePanel && _boxscoreVisible && flag)
		{
			RequestBoxscoreSnapshot(_boxscoreActiveTab);
		}
	}

	private static void UpdateClientBoxscoreAvailableTabsFromSnapshot(BoxscoreSnapshotBase snapshot)
	{
		SetClientBoxscoreAvailableTabs(snapshot?.AvailableTabs ?? CreateBaseBoxscoreTabList(), refreshVisiblePanel: false);
	}

	private static Color GetShootoutAttemptStateColor(string state, string teamLabel)
	{
		switch ((state ?? "").Trim().ToLowerInvariant())
		{
		case "goal":
			return new Color(0.17f, 0.72f, 0.32f, 0.98f);
		case "miss":
			return new Color(0.82f, 0.25f, 0.21f, 0.98f);
		case "pending":
			return new Color(0.5f, 0.53f, 0.58f, 0.92f);
		default:
			return new Color(0.19f, 0.21f, 0.25f, 0.85f);
		}
	}

	private static VisualElement CreateShootoutAttemptBox(string state, string teamLabel, bool isCurrent)
	{
		Color teamAccentColor = GetTeamAccentColor(teamLabel);
		Color shootoutAttemptStateColor = GetShootoutAttemptStateColor(state, teamLabel);
		VisualElement visualElement = new VisualElement();
		visualElement.style.width = 22f;
		visualElement.style.minWidth = 22f;
		visualElement.style.height = 22f;
		visualElement.style.minHeight = 22f;
		visualElement.style.marginLeft = 6f;
		visualElement.style.marginRight = 6f;
		visualElement.style.backgroundColor = shootoutAttemptStateColor;
		visualElement.style.borderTopWidth = isCurrent ? 2f : 1f;
		visualElement.style.borderBottomWidth = isCurrent ? 2f : 1f;
		visualElement.style.borderLeftWidth = isCurrent ? 2f : 1f;
		visualElement.style.borderRightWidth = isCurrent ? 2f : 1f;
		visualElement.style.borderTopColor = (isCurrent ? teamAccentColor : new Color(1f, 1f, 1f, 0.16f));
		visualElement.style.borderBottomColor = (isCurrent ? teamAccentColor : new Color(1f, 1f, 1f, 0.16f));
		visualElement.style.borderLeftColor = (isCurrent ? teamAccentColor : new Color(1f, 1f, 1f, 0.16f));
		visualElement.style.borderRightColor = (isCurrent ? teamAccentColor : new Color(1f, 1f, 1f, 0.16f));
		return visualElement;
	}

	private static VisualElement CreateShootoutGoalieStrip(BoxscoreShootoutSnapshot snapshot)
	{
		VisualElement visualElement = new VisualElement();
		visualElement.style.flexDirection = FlexDirection.Row;
		visualElement.style.alignItems = Align.Center;
		visualElement.style.paddingLeft = 10f;
		visualElement.style.paddingRight = 10f;
		visualElement.style.paddingTop = 8f;
		visualElement.style.paddingBottom = 14f;
		visualElement.style.marginBottom = 12f;
		visualElement.style.backgroundColor = new Color(0.08f, 0.1f, 0.14f, 0.92f);
		Label label = CreateBoxscoreBodyLabel("Goalie: " + (string.IsNullOrWhiteSpace(snapshot.RedGoalieName) ? "None" : snapshot.RedGoalieName), 14f, GetTeamAccentColor("Red"));
		label.style.flexBasis = BOXSCORE_TEAM_STATS_SIDE_WIDTH * 1.5f;
		label.style.minWidth = BOXSCORE_TEAM_STATS_SIDE_WIDTH * 1.5f;
		label.style.unityTextAlign = TextAnchor.MiddleRight;
		label.style.unityFontStyleAndWeight = FontStyle.Bold;
		Label label2 = CreateBoxscoreBodyLabel("SHOOTERS", 13f, new Color(0.84f, 0.87f, 0.93f, 1f));
		label2.style.flexGrow = 1f;
		label2.style.unityTextAlign = TextAnchor.MiddleCenter;
		label2.style.unityFontStyleAndWeight = FontStyle.Bold;
		Label label3 = CreateBoxscoreBodyLabel("Goalie: " + (string.IsNullOrWhiteSpace(snapshot.BlueGoalieName) ? "None" : snapshot.BlueGoalieName), 14f, GetTeamAccentColor("Blue"));
		label3.style.flexBasis = BOXSCORE_TEAM_STATS_SIDE_WIDTH * 1.5f;
		label3.style.minWidth = BOXSCORE_TEAM_STATS_SIDE_WIDTH * 1.5f;
		label3.style.unityTextAlign = TextAnchor.MiddleLeft;
		label3.style.unityFontStyleAndWeight = FontStyle.Bold;
		visualElement.Add(label);
		visualElement.Add(label2);
		visualElement.Add(label3);
		return visualElement;
	}

	private static void ScheduleLiveShootoutBoxscoreRefresh(BoxscoreShootoutSnapshot snapshot)
	{
		_boxscoreShootoutRefreshToken++;
		if (snapshot == null || !snapshot.IsActive || !_boxscoreVisible || NormalizeBoxscoreTab(_boxscoreActiveTab) != BOXSCORE_TAB_SHOOTOUT || ShouldUseClientPreviousGameSnapshot())
		{
			return;
		}
		VisualElement visualElement = _boxscoreOverlay ?? _boxscoreContentRoot ?? MonoBehaviourSingleton<UIManager>.Instance?.RootVisualElement;
		if (visualElement == null)
		{
			return;
		}
		int num = _boxscoreShootoutRefreshToken;
		visualElement.schedule.Execute((Action)delegate
		{
			if (_boxscoreVisible && num == _boxscoreShootoutRefreshToken && NormalizeBoxscoreTab(_boxscoreActiveTab) == BOXSCORE_TAB_SHOOTOUT)
			{
				RequestBoxscoreSnapshot(BOXSCORE_TAB_SHOOTOUT);
			}
		}).ExecuteLater(700L);
	}

	private static void ReceiveBoxscoreShootoutSnapshot(string payload)
	{
		BoxscoreShootoutSnapshot boxscoreShootoutSnapshot = null;
		try
		{
			boxscoreShootoutSnapshot = JsonConvert.DeserializeObject<BoxscoreShootoutSnapshot>(payload);
		}
		catch (Exception arg)
		{
			Logging.LogError($"Error parsing shootout snapshot: {arg}", _clientConfig);
		}
		if (boxscoreShootoutSnapshot == null || !_boxscoreVisible || NormalizeBoxscoreTab(_boxscoreActiveTab) != BOXSCORE_TAB_SHOOTOUT)
		{
			return;
		}
		RenderBoxscoreShootoutSnapshot(boxscoreShootoutSnapshot);
	}

	private static void RenderBoxscoreShootoutSnapshot(BoxscoreShootoutSnapshot snapshot)
	{
		if (_boxscoreContentRoot == null || snapshot == null)
		{
			return;
		}
		UpdateClientBoxscoreAvailableTabsFromSnapshot(snapshot);
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
		visualElement.style.paddingBottom = 8f;
		visualElement.Add(CreateBoxscoreGameStatsTeamHeader(snapshot.RedTeamName, snapshot.RedScore.ToString(), GetTeamAccentColor("Red"), TextAnchor.MiddleRight));
		VisualElement visualElement2 = new VisualElement();
		visualElement2.style.flexGrow = 1f;
		visualElement2.style.alignItems = Align.Center;
		Label label = CreateBoxscoreBodyLabel("SHOOTOUT", 20.2f, new Color(0.9f, 0.93f, 0.99f, 0.9f));
		label.style.unityFontStyleAndWeight = FontStyle.Bold;
		visualElement2.Add(label);
		visualElement.Add(visualElement2);
		visualElement.Add(CreateBoxscoreGameStatsTeamHeader(snapshot.BlueTeamName, snapshot.BlueScore.ToString(), GetTeamAccentColor("Blue"), TextAnchor.MiddleLeft));
		scrollView.Add(visualElement);
		scrollView.Add(CreateShootoutGoalieStrip(snapshot));
		if (snapshot.Rows == null || snapshot.Rows.Count == 0)
		{
			scrollView.Add(CreateBoxscoreBodyLabel("Shootout order will appear once the attempts are initialized.", 14f, new Color(0.88f, 0.9f, 0.95f, 1f)));
			ScheduleLiveShootoutBoxscoreRefresh(snapshot);
			return;
		}
		foreach (BoxscoreShootoutRowSnapshot row in snapshot.Rows)
		{
			bool flag = row.HighlightRedCurrent || row.HighlightBlueCurrent;
			VisualElement visualElement3 = new VisualElement();
			visualElement3.style.flexDirection = FlexDirection.Row;
			visualElement3.style.alignItems = Align.Center;
			visualElement3.style.justifyContent = Justify.SpaceBetween;
			visualElement3.style.paddingTop = 8f;
			visualElement3.style.paddingBottom = 8f;
			visualElement3.style.paddingLeft = 12f;
			visualElement3.style.paddingRight = 12f;
			visualElement3.style.marginBottom = 5f;
			visualElement3.style.backgroundColor = flag ? new Color(0.13f, 0.16f, 0.22f, 0.98f) : new Color(0.09f, 0.11f, 0.15f, 0.96f);
			visualElement3.style.borderLeftWidth = flag ? 3f : 1f;
			visualElement3.style.borderLeftColor = flag ? new Color(0.94f, 0.96f, 1f, 0.9f) : new Color(1f, 1f, 1f, 0.05f);
			Label label2 = CreateBoxscoreBodyLabel(string.IsNullOrWhiteSpace(row.RedShooterName) ? "-" : row.RedShooterName, 15f, row.HighlightRedCurrent ? GetTeamAccentColor("Red") : new Color(0.94f, 0.95f, 0.98f, 1f));
			label2.style.flexBasis = 260f;
			label2.style.minWidth = 260f;
			label2.style.unityTextAlign = TextAnchor.MiddleRight;
			label2.style.unityFontStyleAndWeight = row.HighlightRedCurrent ? FontStyle.Bold : FontStyle.Normal;
			VisualElement visualElement4 = new VisualElement();
			visualElement4.style.flexDirection = FlexDirection.Row;
			visualElement4.style.alignItems = Align.Center;
			visualElement4.style.justifyContent = Justify.Center;
			visualElement4.style.flexBasis = 126f;
			visualElement4.style.minWidth = 126f;
			visualElement4.Add(CreateShootoutAttemptBox(row.RedState, "Red", row.HighlightRedCurrent));
			Label label3 = CreateBoxscoreBodyLabel("#" + row.AttemptNumber, 12f, new Color(0.82f, 0.85f, 0.91f, 0.95f));
			label3.style.minWidth = 40f;
			label3.style.unityTextAlign = TextAnchor.MiddleCenter;
			visualElement4.Add(label3);
			visualElement4.Add(CreateShootoutAttemptBox(row.BlueState, "Blue", row.HighlightBlueCurrent));
			Label label4 = CreateBoxscoreBodyLabel(string.IsNullOrWhiteSpace(row.BlueShooterName) ? "-" : row.BlueShooterName, 15f, row.HighlightBlueCurrent ? GetTeamAccentColor("Blue") : new Color(0.94f, 0.95f, 0.98f, 1f));
			label4.style.flexBasis = 260f;
			label4.style.minWidth = 260f;
			label4.style.unityTextAlign = TextAnchor.MiddleLeft;
			label4.style.unityFontStyleAndWeight = row.HighlightBlueCurrent ? FontStyle.Bold : FontStyle.Normal;
			visualElement3.Add(label2);
			visualElement3.Add(visualElement4);
			visualElement3.Add(label4);
			scrollView.Add(visualElement3);
		}
		ScheduleLiveShootoutBoxscoreRefresh(snapshot);
	}
}
