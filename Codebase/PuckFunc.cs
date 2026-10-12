using System.Diagnostics;

namespace Codebase;

public class PuckFunc
{
	public static bool PuckIsTipped(string playerSteamId, int maxTippedMilliseconds, LockDictionary<string, Stopwatch> playersCurrentPuckTouch, LockDictionary<string, Stopwatch> lastTimeOnCollisionStayOrExitWasCalled)
	{
		if (!playersCurrentPuckTouch.TryGetValue(playerSteamId, out var value))
		{
			return false;
		}
		if (!lastTimeOnCollisionStayOrExitWasCalled.TryGetValue(playerSteamId, out var value2))
		{
			return false;
		}
		if (value.ElapsedMilliseconds - value2.ElapsedMilliseconds < maxTippedMilliseconds)
		{
			return true;
		}
		return false;
	}
}
