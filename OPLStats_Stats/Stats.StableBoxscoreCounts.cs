using System.Collections.Generic;
using System.Linq;

namespace OPLStats_Stats;

public partial class Stats
{
    private sealed class StableBoxscoreCounts
    {
        internal long Revision;
        internal Dictionary<string, int> FaceoffWins, FaceoffLosses, Goals, Assists;
    }
    private static StableBoxscoreCounts _stableBoxscoreCounts;
    private static StableBoxscoreCounts GetStableBoxscoreCounts()
    {
        if (_stableBoxscoreCounts != null && _stableBoxscoreCounts.Revision == _analyticsRevision) return _stableBoxscoreCounts;
		Dictionary<string, int> dictionary = new Dictionary<string, int>();
		Dictionary<string, int> dictionary2 = new Dictionary<string, int>();
		foreach (PlayByPlayEvent item in _playByPlayEvents.Where((PlayByPlayEvent e) => e.EventType == PlayByPlayEventType.FaceoffOutcome && !string.IsNullOrEmpty(e.PlayerSteamId)).ToList())
		{
			string playerSteamId = item.PlayerSteamId;
			if (item.Outcome == "successful")
			{
				if (!dictionary.ContainsKey(playerSteamId))
				{
					dictionary[playerSteamId] = 0;
				}
				dictionary[playerSteamId]++;
			}
			else if (item.Outcome == "failed")
			{
				if (!dictionary2.ContainsKey(playerSteamId))
				{
					dictionary2[playerSteamId] = 0;
				}
				dictionary2[playerSteamId]++;
			}
		}
		Dictionary<string, int> dictionary3 = new Dictionary<string, int>();
		Dictionary<string, int> dictionary4 = new Dictionary<string, int>();
		foreach (GoalInfo goal in _goals)
		{
			if (!string.IsNullOrEmpty(goal.Scorer))
			{
				if (!dictionary3.ContainsKey(goal.Scorer))
				{
					dictionary3[goal.Scorer] = 0;
				}
				dictionary3[goal.Scorer]++;
			}
			if (!string.IsNullOrEmpty(goal.PrimaryAssist))
			{
				if (!dictionary4.ContainsKey(goal.PrimaryAssist))
				{
					dictionary4[goal.PrimaryAssist] = 0;
				}
				dictionary4[goal.PrimaryAssist]++;
			}
			if (!string.IsNullOrEmpty(goal.SecondaryAssist))
			{
				if (!dictionary4.ContainsKey(goal.SecondaryAssist))
				{
					dictionary4[goal.SecondaryAssist] = 0;
				}
				dictionary4[goal.SecondaryAssist]++;
			}
		}
        return _stableBoxscoreCounts = new StableBoxscoreCounts {
            Revision = _analyticsRevision, FaceoffWins = dictionary, FaceoffLosses = dictionary2, Goals = dictionary3, Assists = dictionary4
        };
    }
}
