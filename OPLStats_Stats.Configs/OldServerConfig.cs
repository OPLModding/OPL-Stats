using System;
using Codebase.Configs;

namespace OPLStats_Stats.Configs;

public class OldServerConfig : ISubConfig
{
	public bool LogInfo { get; } = true;

	public float GoalieRadius { get; } = 0.7f;

	public float GoalieSaveCreaseSystemZDelta { get; } = 0.0125f;

	public int MaxTippedMilliseconds { get; } = 91;

	public int MinPossessionMilliseconds { get; } = 300;

	public int MaxPossessionMilliseconds { get; } = 700;

	public string FileHeaderName { get; } = "";

	public void UpdateDefaultValues(ISubConfig oldConfig)
	{
		throw new NotImplementedException();
	}
}
