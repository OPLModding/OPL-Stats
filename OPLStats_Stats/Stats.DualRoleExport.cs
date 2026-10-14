using System;
using System.Collections.Generic;
using System.Linq;

namespace OPLStats_Stats;

public partial class Stats
{
	private static IEnumerable<BoxscorePlayerSnapshot> ExpandDualRoleExportPlayers(FrozenGameExportSnapshot snapshot)
	{
		if (snapshot?.LiveData?.Players == null)
		{
			yield break;
		}
		foreach (BoxscorePlayerSnapshot player in snapshot.LiveData.Players)
		{
			if (player == null)
			{
				continue;
			}
			PlayerRoleParticipationSnapshot playerRoleParticipationSnapshot = ResolvePlayerRoleParticipation(snapshot, player.SteamId);
			if (playerRoleParticipationSnapshot == null || !playerRoleParticipationSnapshot.PlayedSkater || !playerRoleParticipationSnapshot.PlayedGoalie)
			{
				yield return player;
				continue;
			}
			BoxscorePlayerSnapshot boxscorePlayerSnapshot = CloneBoxscorePlayerSnapshot(player);
			BoxscorePlayerSnapshot boxscorePlayerSnapshot2 = CloneBoxscorePlayerSnapshot(player);
			ConfigureSkaterRoleSnapshot(snapshot, player, boxscorePlayerSnapshot, playerRoleParticipationSnapshot);
			ConfigureGoalieRoleSnapshot(snapshot, player, boxscorePlayerSnapshot2, playerRoleParticipationSnapshot);
			yield return boxscorePlayerSnapshot;
			yield return boxscorePlayerSnapshot2;
		}
	}

	private static PlayerRoleParticipationSnapshot ResolvePlayerRoleParticipation(FrozenGameExportSnapshot snapshot, string steamId)
	{
		PlayerRoleParticipationSnapshot playerRoleParticipationSnapshot = null;
		if (snapshot?.PlayerRoleParticipation != null && !string.IsNullOrWhiteSpace(steamId))
		{
			snapshot.PlayerRoleParticipation.TryGetValue(steamId, out playerRoleParticipationSnapshot);
		}
		playerRoleParticipationSnapshot = playerRoleParticipationSnapshot ?? new PlayerRoleParticipationSnapshot();
		List<PlayByPlayEvent> list = GetFrozenPlayerEvents(snapshot, steamId).Where((PlayByPlayEvent e) => !string.IsNullOrWhiteSpace(e.PlayerPosition)).OrderBy((PlayByPlayEvent e) => e.GameTime).ThenBy((PlayByPlayEvent e) => e.EventId).ToList() ?? new List<PlayByPlayEvent>();
		string text = "";
		foreach (PlayByPlayEvent item in list)
		{
			if (IsGoaliePosition(item.PlayerPosition))
			{
				playerRoleParticipationSnapshot.PlayedGoalie = true;
			}
			else
			{
				playerRoleParticipationSnapshot.PlayedSkater = true;
				text = item.PlayerPosition;
			}
		}
		if (string.IsNullOrWhiteSpace(playerRoleParticipationSnapshot.LastSkaterPosition))
		{
			playerRoleParticipationSnapshot.LastSkaterPosition = text;
		}
		return playerRoleParticipationSnapshot;
	}

	private static bool IsGoaliePosition(string position)
	{
		return string.Equals(position, "G", StringComparison.OrdinalIgnoreCase);
	}

	private static void ConfigureSkaterRoleSnapshot(FrozenGameExportSnapshot snapshot, BoxscorePlayerSnapshot source, BoxscorePlayerSnapshot skater, PlayerRoleParticipationSnapshot participation)
	{
		skater.Position = ResolveLastSkaterPosition(snapshot, source.SteamId, participation.LastSkaterPosition);
		skater.IsGoalie = false;
		ApplyRoleTime(skater, participation.SkaterTimeOnIceSeconds, participation.SkaterTimeOnIceByZone);
		skater.ShotsFaced = 0;
		skater.Saves = 0;
		skater.GoalsAllowed = 0;
		skater.SavePercentage = 0.0;
		skater.BodySaves = 0;
		skater.PadSaves = 0;
		skater.StickSaves = 0;
		skater.Xga = 0.0;
		skater.Gsax = 0.0;
		SubtractGoalieActivityFromSkater(snapshot, source, skater);
		skater.IndividualCorsiForPercentage = CalculateIndividualCorsiForPercentage(skater.IndividualCorsiFor, skater.OnIceTeamShotAttempts);
	}

	private static void ConfigureGoalieRoleSnapshot(FrozenGameExportSnapshot snapshot, BoxscorePlayerSnapshot source, BoxscorePlayerSnapshot goalie, PlayerRoleParticipationSnapshot participation)
	{
		goalie.Position = "G";
		goalie.IsGoalie = true;
		ApplyRoleTime(goalie, participation.GoalieTimeOnIceSeconds, participation.GoalieTimeOnIceByZone);
		goalie.Goals = CountGoalieGoals(snapshot, source.SteamId);
		goalie.Assists = CountGoalieAssists(snapshot, source.SteamId);
		goalie.Sog = CountGoalieEvents(snapshot, source.SteamId, (PlayByPlayEvent e) => e.EventType == PlayByPlayEventType.Shot && IsShotOnGoalOutcome(e.Outcome), source.Sog);
		goalie.IndividualCorsiFor = CountGoalieEvents(snapshot, source.SteamId, (PlayByPlayEvent e) => e.EventType == PlayByPlayEventType.Shot, source.IndividualCorsiFor);
		goalie.OnIceTeamShotAttempts = CountGoalieOnIceShotAttempts(snapshot, source.SteamId, source.OnIceTeamShotAttempts);
		goalie.Blocks = CountGoalieEvents(snapshot, source.SteamId, (PlayByPlayEvent e) => e.EventType == PlayByPlayEventType.Block, source.Blocks);
		goalie.Passes = CountGoalieEvents(snapshot, source.SteamId, (PlayByPlayEvent e) => e.EventType == PlayByPlayEventType.Pass, source.Passes);
		goalie.Exits = CountGoalieEvents(snapshot, source.SteamId, (PlayByPlayEvent e) => HasEventFlag(e, "DZExit"), source.Exits);
		goalie.Entries = CountGoalieEvents(snapshot, source.SteamId, (PlayByPlayEvent e) => HasEventFlag(e, "OZEntry"), source.Entries);
		goalie.Hits = CountGoalieEvents(snapshot, source.SteamId, (PlayByPlayEvent e) => e.EventType == PlayByPlayEventType.Hit, source.Hits);
		goalie.HitsTaken = 0;
		goalie.Turnovers = CountGoalieEvents(snapshot, source.SteamId, (PlayByPlayEvent e) => e.EventType == PlayByPlayEventType.Turnover, source.Turnovers);
		goalie.Takeaways = CountGoalieEvents(snapshot, source.SteamId, (PlayByPlayEvent e) => e.EventType == PlayByPlayEventType.Takeaway, source.Takeaways);
		goalie.PuckTouches = CountGoalieEvents(snapshot, source.SteamId, (PlayByPlayEvent e) => e.EventType == PlayByPlayEventType.Touch || e.EventType == PlayByPlayEventType.Pass, source.PuckTouches);
		goalie.PossessionTimeSeconds = 0.0;
		goalie.DefensiveZonePossessionTimeSeconds = 0.0;
		goalie.NeutralZonePossessionTimeSeconds = 0.0;
		goalie.OffensiveZonePossessionTimeSeconds = 0.0;
		goalie.PostHits = CountGoalieEvents(snapshot, source.SteamId, (PlayByPlayEvent e) => e.EventType == PlayByPlayEventType.PostHit, source.PostHits);
		goalie.FaceoffWins = CountGoalieEvents(snapshot, source.SteamId, (PlayByPlayEvent e) => e.EventType == PlayByPlayEventType.FaceoffOutcome && string.Equals(e.Outcome, "successful", StringComparison.OrdinalIgnoreCase), source.FaceoffWins);
		goalie.FaceoffLosses = CountGoalieEvents(snapshot, source.SteamId, (PlayByPlayEvent e) => e.EventType == PlayByPlayEventType.FaceoffOutcome && string.Equals(e.Outcome, "failed", StringComparison.OrdinalIgnoreCase), source.FaceoffLosses);
		goalie.Xg = SumGoalieExpectedGoals(snapshot, source.SteamId, source.Xg);
		goalie.PlusMinus = 0;
		// The player's whole influence share stays on the skater entry so the team pool isn't counted twice.
		goalie.Influence = 0.0;
		goalie.IndividualCorsiForPercentage = CalculateIndividualCorsiForPercentage(goalie.IndividualCorsiFor, goalie.OnIceTeamShotAttempts);
	}

	private static void SubtractGoalieActivityFromSkater(FrozenGameExportSnapshot snapshot, BoxscorePlayerSnapshot source, BoxscorePlayerSnapshot skater)
	{
		skater.Goals = Math.Max(0, source.Goals - CountGoalieGoals(snapshot, source.SteamId));
		skater.Assists = Math.Max(0, source.Assists - CountGoalieAssists(snapshot, source.SteamId));
		skater.Sog = Math.Max(0, source.Sog - CountGoalieEvents(snapshot, source.SteamId, (PlayByPlayEvent e) => e.EventType == PlayByPlayEventType.Shot && IsShotOnGoalOutcome(e.Outcome), source.Sog));
		skater.IndividualCorsiFor = Math.Max(0, source.IndividualCorsiFor - CountGoalieEvents(snapshot, source.SteamId, (PlayByPlayEvent e) => e.EventType == PlayByPlayEventType.Shot, source.IndividualCorsiFor));
		skater.OnIceTeamShotAttempts = Math.Max(0, source.OnIceTeamShotAttempts - CountGoalieOnIceShotAttempts(snapshot, source.SteamId, source.OnIceTeamShotAttempts));
		skater.Blocks = Math.Max(0, source.Blocks - CountGoalieEvents(snapshot, source.SteamId, (PlayByPlayEvent e) => e.EventType == PlayByPlayEventType.Block, source.Blocks));
		skater.Passes = Math.Max(0, source.Passes - CountGoalieEvents(snapshot, source.SteamId, (PlayByPlayEvent e) => e.EventType == PlayByPlayEventType.Pass, source.Passes));
		skater.Exits = Math.Max(0, source.Exits - CountGoalieEvents(snapshot, source.SteamId, (PlayByPlayEvent e) => HasEventFlag(e, "DZExit"), source.Exits));
		skater.Entries = Math.Max(0, source.Entries - CountGoalieEvents(snapshot, source.SteamId, (PlayByPlayEvent e) => HasEventFlag(e, "OZEntry"), source.Entries));
		skater.Hits = Math.Max(0, source.Hits - CountGoalieEvents(snapshot, source.SteamId, (PlayByPlayEvent e) => e.EventType == PlayByPlayEventType.Hit, source.Hits));
		skater.Turnovers = Math.Max(0, source.Turnovers - CountGoalieEvents(snapshot, source.SteamId, (PlayByPlayEvent e) => e.EventType == PlayByPlayEventType.Turnover, source.Turnovers));
		skater.Takeaways = Math.Max(0, source.Takeaways - CountGoalieEvents(snapshot, source.SteamId, (PlayByPlayEvent e) => e.EventType == PlayByPlayEventType.Takeaway, source.Takeaways));
		skater.PuckTouches = Math.Max(0, source.PuckTouches - CountGoalieEvents(snapshot, source.SteamId, (PlayByPlayEvent e) => e.EventType == PlayByPlayEventType.Touch || e.EventType == PlayByPlayEventType.Pass, source.PuckTouches));
		skater.PostHits = Math.Max(0, source.PostHits - CountGoalieEvents(snapshot, source.SteamId, (PlayByPlayEvent e) => e.EventType == PlayByPlayEventType.PostHit, source.PostHits));
		skater.FaceoffWins = Math.Max(0, source.FaceoffWins - CountGoalieEvents(snapshot, source.SteamId, (PlayByPlayEvent e) => e.EventType == PlayByPlayEventType.FaceoffOutcome && string.Equals(e.Outcome, "successful", StringComparison.OrdinalIgnoreCase), source.FaceoffWins));
		skater.FaceoffLosses = Math.Max(0, source.FaceoffLosses - CountGoalieEvents(snapshot, source.SteamId, (PlayByPlayEvent e) => e.EventType == PlayByPlayEventType.FaceoffOutcome && string.Equals(e.Outcome, "failed", StringComparison.OrdinalIgnoreCase), source.FaceoffLosses));
		skater.Xg = Math.Max(0.0, source.Xg - SumGoalieExpectedGoals(snapshot, source.SteamId, source.Xg));
	}

	private static string ResolveLastSkaterPosition(FrozenGameExportSnapshot snapshot, string steamId, string trackedPosition)
	{
		if (!string.IsNullOrWhiteSpace(trackedPosition) && !IsGoaliePosition(trackedPosition))
		{
			return trackedPosition;
		}
		return GetFrozenPlayerEvents(snapshot, steamId).Where((PlayByPlayEvent e) => !string.IsNullOrWhiteSpace(e.PlayerPosition) && !IsGoaliePosition(e.PlayerPosition)).OrderByDescending((PlayByPlayEvent e) => e.GameTime).ThenByDescending((PlayByPlayEvent e) => e.EventId).Select((PlayByPlayEvent e) => e.PlayerPosition).FirstOrDefault() ?? "";
	}

	private static void ApplyRoleTime(BoxscorePlayerSnapshot player, double total, double[] byZone)
	{
		player.TimeOnIceSeconds = Math.Max(0.0, total);
		player.DefensiveZoneTimeOnIceSeconds = byZone != null && byZone.Length > 0 ? Math.Max(0.0, byZone[0]) : 0.0;
		player.NeutralZoneTimeOnIceSeconds = byZone != null && byZone.Length > 1 ? Math.Max(0.0, byZone[1]) : 0.0;
		player.OffensiveZoneTimeOnIceSeconds = byZone != null && byZone.Length > 2 ? Math.Max(0.0, byZone[2]) : 0.0;
	}

	private static int CountGoalieEvents(FrozenGameExportSnapshot snapshot, string steamId, Func<PlayByPlayEvent, bool> predicate, int maximum)
	{
		if (maximum <= 0 || snapshot?.PlayByPlayEvents == null)
		{
			return 0;
		}
		int val = GetFrozenPlayerEvents(snapshot, steamId).Count((PlayByPlayEvent e) => IsGoaliePosition(e.PlayerPosition) && predicate(e));
		return Math.Min(maximum, val);
	}

	private static int CountGoalieGoals(FrozenGameExportSnapshot snapshot, string steamId)
	{
		int val = snapshot?.Goals?.Count((GoalInfo g) => g != null && string.Equals(g.Scorer, steamId, StringComparison.Ordinal) && IsGoaliePosition(g.ScorerPosition)) ?? 0;
		BoxscorePlayerSnapshot boxscorePlayerSnapshot = snapshot?.LiveData?.Players?.FirstOrDefault((BoxscorePlayerSnapshot p) => p != null && string.Equals(p.SteamId, steamId, StringComparison.Ordinal));
		return Math.Min(boxscorePlayerSnapshot?.Goals ?? 0, val);
	}

	private static int CountGoalieAssists(FrozenGameExportSnapshot snapshot, string steamId)
	{
		int num = snapshot?.Goals?.Count((GoalInfo g) => g != null && string.Equals(g.PrimaryAssist, steamId, StringComparison.Ordinal) && IsGoaliePosition(g.PrimaryAssistPosition)) ?? 0;
		num += snapshot?.Goals?.Count((GoalInfo g) => g != null && string.Equals(g.SecondaryAssist, steamId, StringComparison.Ordinal) && IsGoaliePosition(g.SecondaryAssistPosition)) ?? 0;
		BoxscorePlayerSnapshot boxscorePlayerSnapshot = snapshot?.LiveData?.Players?.FirstOrDefault((BoxscorePlayerSnapshot p) => p != null && string.Equals(p.SteamId, steamId, StringComparison.Ordinal));
		return Math.Min(boxscorePlayerSnapshot?.Assists ?? 0, num);
	}

	private static int CountGoalieOnIceShotAttempts(FrozenGameExportSnapshot snapshot, string steamId, int maximum)
	{
		if (maximum <= 0 || snapshot?.PlayByPlayEvents == null)
		{
			return 0;
		}
		int val = snapshot.PlayByPlayEvents.Count((PlayByPlayEvent e) => e != null && e.EventType == PlayByPlayEventType.Shot && string.Equals(e.TeamGoalieSteamID, steamId, StringComparison.Ordinal));
		return Math.Min(maximum, val);
	}

	private static double SumGoalieExpectedGoals(FrozenGameExportSnapshot snapshot, string steamId, double maximum)
	{
		if (maximum <= 0.0 || snapshot?.PlayByPlayEvents == null)
		{
			return 0.0;
		}
		double val = GetFrozenPlayerEvents(snapshot, steamId).Where((PlayByPlayEvent e) => e.EventType == PlayByPlayEventType.Shot && IsGoaliePosition(e.PlayerPosition)).Sum((PlayByPlayEvent e) => (double)e.Xg);
		return Math.Min(maximum, Math.Max(0.0, val));
	}

	private static bool IsShotOnGoalOutcome(string outcome)
	{
		return string.Equals(outcome, "on net", StringComparison.OrdinalIgnoreCase) || string.Equals(outcome, "goal", StringComparison.OrdinalIgnoreCase);
	}

	private static bool HasEventFlag(PlayByPlayEvent playByPlayEvent, string flag)
	{
		return playByPlayEvent?.Flags?.Split(',').Any((string value) => string.Equals(value.Trim(), flag, StringComparison.OrdinalIgnoreCase)) ?? false;
	}
}
