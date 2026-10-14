using System;
using System.Collections.Generic;
using System.Linq;

namespace OPLStats_Stats;

// "Influence": every team has a fixed pool of points that is split between its players in proportion to how much
// meaningful, win-driving work each one did. Unlike star points the total never grows, so a player's influence is
// their share of what their team accomplished.
public partial class Stats
{
	internal const double INFLUENCE_TEAM_POOL = 250.0;

	// Everyone who played gets at least this much raw weight so a quiet player still holds a small share.
	private const double INFLUENCE_PARTICIPATION_FLOOR = 1.0;

	private const double INFLUENCE_PER_MINUTE_ON_ICE = 0.5;

	// Raw weights for the actions that drive winning. Tune here.
	private const double INFLUENCE_GOAL = 12.0;

	private const double INFLUENCE_GAME_WINNING_GOAL = 6.0;

	private const double INFLUENCE_ASSIST = 6.0;

	// Added on top of INFLUENCE_ASSIST for a primary assist (so a primary is worth 10 and a secondary 6).
	private const double INFLUENCE_PRIMARY_ASSIST_BONUS = 4.0;

	private const double INFLUENCE_EXPECTED_GOAL = 6.0;

	private const double INFLUENCE_SHOT_ON_GOAL = 1.5;

	private const double INFLUENCE_BLOCK = 2.5;

	private const double INFLUENCE_TAKEAWAY = 2.0;

	private const double INFLUENCE_TURNOVER = -1.5;

	private const double INFLUENCE_HIT = 0.5;

	private const double INFLUENCE_ZONE_ENTRY = 1.0;

	private const double INFLUENCE_ZONE_EXIT = 1.0;

	private const double INFLUENCE_PASS = 0.2;

	private const double INFLUENCE_FACEOFF_WIN = 0.6;

	private const double INFLUENCE_PLUS_MINUS = 2.0;

	private const double INFLUENCE_SAVE = 0.8;

	private const double INFLUENCE_GOAL_ALLOWED = -4.0;

	private const double INFLUENCE_GSAX = 5.0;

	private const double INFLUENCE_SHUTOUT = 10.0;

	private const int INFLUENCE_SHUTOUT_MIN_SHOTS = 5;

	private static double CalculateInfluenceRawScore(BoxscorePlayerSnapshot player, bool expectedGoalsEnabled, string winningGoalScorerSteamId, int primaryAssists)
	{
		double impact = player.Goals * INFLUENCE_GOAL
			+ player.Assists * INFLUENCE_ASSIST
			+ primaryAssists * INFLUENCE_PRIMARY_ASSIST_BONUS
			+ player.Takeaways * INFLUENCE_TAKEAWAY
			+ player.Turnovers * INFLUENCE_TURNOVER
			+ player.Hits * INFLUENCE_HIT
			+ player.Entries * INFLUENCE_ZONE_ENTRY
			+ player.Exits * INFLUENCE_ZONE_EXIT
			+ player.Passes * INFLUENCE_PASS
			+ player.FaceoffWins * INFLUENCE_FACEOFF_WIN;
		if (!string.IsNullOrEmpty(winningGoalScorerSteamId) && string.Equals(winningGoalScorerSteamId, player.SteamId, StringComparison.Ordinal))
		{
			impact += INFLUENCE_GAME_WINNING_GOAL;
		}
		if (player.IsGoalie)
		{
			impact += player.Saves * INFLUENCE_SAVE + player.GoalsAllowed * INFLUENCE_GOAL_ALLOWED;
			if (expectedGoalsEnabled)
			{
				impact += player.Gsax * INFLUENCE_GSAX;
			}
			if (player.GoalsAllowed == 0 && player.ShotsFaced >= INFLUENCE_SHUTOUT_MIN_SHOTS)
			{
				impact += INFLUENCE_SHUTOUT;
			}
		}
		else
		{
			impact += player.Sog * INFLUENCE_SHOT_ON_GOAL
				+ player.Blocks * INFLUENCE_BLOCK
				+ player.PlusMinus * INFLUENCE_PLUS_MINUS;
			if (expectedGoalsEnabled)
			{
				impact += player.Xg * INFLUENCE_EXPECTED_GOAL;
			}
		}
		return INFLUENCE_PARTICIPATION_FLOOR + player.TimeOnIceSeconds / 60.0 * INFLUENCE_PER_MINUTE_ON_ICE + Math.Max(0.0, impact);
	}

	// Splits INFLUENCE_TEAM_POOL between the players of each team. Players who didn't take part (no time on ice and
	// no recorded action) get 0 and don't dilute the others' share.
	private static void ApplyInfluence(LiveBoxscoreData liveBoxscoreData)
	{
		if (liveBoxscoreData?.Players == null)
		{
			return;
		}
		bool expectedGoalsEnabled = liveBoxscoreData.ExpectedGoals != null && liveBoxscoreData.ExpectedGoals.Enabled;
		string winningGoalScorerSteamId = GetCurrentWinningGoalScorer().WinningGoalScorerSteamId;
		Dictionary<string, int> primaryAssistsBySteamId = new Dictionary<string, int>(StringComparer.Ordinal);
		foreach (GoalInfo goal in _goals)
		{
			if (!string.IsNullOrEmpty(goal.PrimaryAssist))
			{
				primaryAssistsBySteamId[goal.PrimaryAssist] = (primaryAssistsBySteamId.TryGetValue(goal.PrimaryAssist, out int count) ? count : 0) + 1;
			}
		}
		foreach (BoxscorePlayerSnapshot player in liveBoxscoreData.Players)
		{
			player.Influence = 0.0;
		}
		foreach (PlayerTeam team in new[] { PlayerTeam.Red, PlayerTeam.Blue })
		{
			List<BoxscorePlayerSnapshot> roster = liveBoxscoreData.Players.Where((BoxscorePlayerSnapshot p) => p.Team == team && HasInfluenceParticipation(p)).ToList();
			if (roster.Count == 0)
			{
				continue;
			}
			Dictionary<BoxscorePlayerSnapshot, double> raw = roster.ToDictionary((BoxscorePlayerSnapshot p) => p, (BoxscorePlayerSnapshot p) => CalculateInfluenceRawScore(p, expectedGoalsEnabled, winningGoalScorerSteamId, primaryAssistsBySteamId.TryGetValue(p.SteamId, out int primaryAssists) ? primaryAssists : 0));
			double total = raw.Values.Sum();
			foreach (BoxscorePlayerSnapshot player in roster)
			{
				player.Influence = INFLUENCE_TEAM_POOL * raw[player] / total;
			}
		}
	}

	private static bool HasInfluenceParticipation(BoxscorePlayerSnapshot player)
	{
		return player.TimeOnIceSeconds > 0.0 || player.PuckTouches > 0 || player.Goals > 0 || player.Assists > 0 || player.Sog > 0 || player.ShotsFaced > 0 || player.Passes > 0;
	}
}
