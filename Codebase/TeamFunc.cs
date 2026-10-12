namespace Codebase;

public static class TeamFunc
{
	public const PlayerTeam DEFAULT_TEAM = PlayerTeam.Blue;

	public static PlayerTeam GetOtherTeam(PlayerTeam team)
	{
		return team switch
		{
			PlayerTeam.Blue => PlayerTeam.Red,
			PlayerTeam.Red => PlayerTeam.Blue,
			_ => PlayerTeam.None,
		};
	}
}
