using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine.UIElements;

namespace OPLStats_Stats;

public partial class Stats
{
    private sealed class ScoreboardLayoutCallback { internal Action Reapply; }
    private static readonly ConditionalWeakTable<VisualElement, ScoreboardLayoutCallback> _scoreboardLayoutCallbacks = new ConditionalWeakTable<VisualElement, ScoreboardLayoutCallback>();
    private static readonly Dictionary<VisualElement, IVisualElementScheduledItem> _tooltipRefreshTasks = new Dictionary<VisualElement, IVisualElementScheduledItem>();
    private static readonly Dictionary<VisualElement, List<Action>> _tooltipUnregisterActions = new Dictionary<VisualElement, List<Action>>();
    private static readonly HashSet<string> _dirtyPlayerTooltips = new HashSet<string>(StringComparer.Ordinal);
    private static bool _dirtyTeamTooltips;
    private static IVisualElementScheduledItem _pendingTooltipRender;
    private static VisualElement _tooltipRenderRoot;
    private static bool _tooltipsAlreadyHidden;
    private static string _renderedBoxscorePayload;
    private static string _renderedBoxscoreTab;
    private static VisualElement _renderedBoxscoreFirstChild;

    private static bool IsBoxscorePayloadAlreadyRendered(string payload, string tab)
    {
        return _boxscoreContentRoot != null && _boxscoreContentRoot.childCount > 0 &&
            ReferenceEquals(_boxscoreContentRoot[0], _renderedBoxscoreFirstChild) &&
            _renderedBoxscoreTab == tab && _renderedBoxscorePayload == payload;
    }

    private static void RememberRenderedBoxscorePayload(string payload, string tab)
    {
        _renderedBoxscorePayload = payload;
        _renderedBoxscoreTab = tab;
        _renderedBoxscoreFirstChild = _boxscoreContentRoot != null && _boxscoreContentRoot.childCount > 0 ? _boxscoreContentRoot[0] : null;
    }

    private static void RegisterTooltipCallback<T>(VisualElement owner, VisualElement target, EventCallback<T> callback) where T : EventBase<T>, new()
    {
        target.RegisterCallback(callback);
        if (!_tooltipUnregisterActions.TryGetValue(owner, out var actions))
            _tooltipUnregisterActions[owner] = actions = new List<Action>();
        actions.Add(() => target.UnregisterCallback(callback));
    }

    private static void StopTooltipRefresh(VisualElement tooltip)
    {
        if (tooltip != null && _tooltipRefreshTasks.TryGetValue(tooltip, out var task))
        {
            task.Pause();
            _tooltipRefreshTasks.Remove(tooltip);
        }
    }

    private static void DisposeTooltipCallbacks(VisualElement tooltip)
    {
        if (tooltip == null) return;
        StopTooltipRefresh(tooltip);
        if (_tooltipUnregisterActions.TryGetValue(tooltip, out var actions))
        {
            foreach (Action action in actions) action();
            _tooltipUnregisterActions.Remove(tooltip);
        }
    }

    private static void StartTooltipRefresh(VisualElement tooltip, Func<bool> isVisible, Action update)
    {
        _tooltipsAlreadyHidden = false;
        if (_tooltipRefreshTasks.ContainsKey(tooltip)) return;
        update();
        _tooltipRefreshTasks[tooltip] = tooltip.schedule.Execute(() => {
            if (!isVisible() || !IsScoreboardCurrentlyVisible()) { StopTooltipRefresh(tooltip); return; }
            update();
        }).Every(500);
    }

    private static void RefreshVisiblePlayerTooltip(string playerSteamId)
    {
        if (string.IsNullOrWhiteSpace(playerSteamId)) return;
        _dirtyPlayerTooltips.Add(playerSteamId);
        ScheduleTooltipRender();
    }

    private static void RefreshVisibleTeamTooltips()
    {
        _dirtyTeamTooltips = true;
        ScheduleTooltipRender();
    }

    private static void ScheduleTooltipRender()
    {
        VisualElement root = MonoBehaviourSingleton<UIManager>.Instance?.RootVisualElement;
        if (_pendingTooltipRender != null && ReferenceEquals(_tooltipRenderRoot, root)) return;
        _pendingTooltipRender?.Pause();
        _pendingTooltipRender = null;
        _tooltipRenderRoot = root;
        if (root == null) { _dirtyPlayerTooltips.Clear(); _dirtyTeamTooltips = false; return; }
        _pendingTooltipRender = root.schedule.Execute(() => {
            _pendingTooltipRender = null;
            foreach (string id in _dirtyPlayerTooltips) RefreshVisiblePlayerTooltipNow(id);
            _dirtyPlayerTooltips.Clear();
            if (_dirtyTeamTooltips) RefreshVisibleTeamTooltipsNow();
            _dirtyTeamTooltips = false;
        });
    }

    private static void ResetTooltipPerformanceState()
    {
        _renderedBoxscorePayload = null;
        _renderedBoxscoreTab = null;
        _renderedBoxscoreFirstChild = null;
        _pendingTooltipRender?.Pause();
        _pendingTooltipRender = null;
        _tooltipRenderRoot = null;
        _dirtyPlayerTooltips.Clear();
        _dirtyTeamTooltips = false;
        foreach (var task in _tooltipRefreshTasks.Values) task.Pause();
        _tooltipRefreshTasks.Clear();
        foreach (var actions in _tooltipUnregisterActions.Values)
            foreach (Action action in actions) action();
        _tooltipUnregisterActions.Clear();
        _tooltipsAlreadyHidden = false;
    }
}
