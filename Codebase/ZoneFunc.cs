using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using UnityEngine;

namespace Codebase;

internal static class ZoneFunc
{
	internal const Zone DEFAULT_ZONE = Zone.BlueTeam_Center;

	internal static ReadOnlyDictionary<IceElement, (double Start, double End)> ICE_Z_POSITIONS { get; } = new ReadOnlyDictionary<IceElement, (double, double)>(new Dictionary<IceElement, (double, double)>
	{
		{
			IceElement.BlueTeam_BlueLine,
			(13.0, 13.5)
		},
		{
			IceElement.RedTeam_BlueLine,
			(-13.5, -13.0)
		},
		{
			IceElement.CenterLine,
			(-0.25, 0.25)
		},
		{
			IceElement.BlueTeam_GoalLine,
			(39.75, 40.0)
		},
		{
			IceElement.RedTeam_GoalLine,
			(-40.0, -39.75)
		},
		{
			IceElement.BlueTeam_BluePaint,
			(37.25, 40.0)
		},
		{
			IceElement.RedTeam_BluePaint,
			(-40.0, -37.25)
		},
		{
			IceElement.BlueTeam_HashMarks,
			(29.1, 30.4)
		},
		{
			IceElement.RedTeam_HashMarks,
			(-30.4, -29.1)
		}
	});

	internal static ReadOnlyDictionary<IceElement, (double Start, double End)> ICE_X_POSITIONS { get; } = new ReadOnlyDictionary<IceElement, (double, double)>(new Dictionary<IceElement, (double, double)>
	{
		{
			IceElement.BlueTeam_BluePaint,
			(-2.5, 2.5)
		},
		{
			IceElement.RedTeam_BluePaint,
			(-2.5, 2.5)
		}
	});

	private static ReadOnlyCollection<Zone> BLUE_TEAM_DEFENSE_ZONES { get; } = new ReadOnlyCollection<Zone>(new List<Zone>
	{
		Zone.BlueTeam_Zone,
		Zone.BlueTeam_BehindGoalLine
	});

	private static ReadOnlyCollection<Zone> BLUE_TEAM_ZONES { get; } = new ReadOnlyCollection<Zone>(new List<Zone>
	{
		Zone.BlueTeam_Zone,
		Zone.BlueTeam_BehindGoalLine,
		Zone.BlueTeam_Center
	});

	private static ReadOnlyCollection<Zone> RED_TEAM_DEFENSE_ZONES { get; } = new ReadOnlyCollection<Zone>(new List<Zone>
	{
		Zone.RedTeam_Zone,
		Zone.RedTeam_BehindGoalLine
	});

	private static ReadOnlyCollection<Zone> RED_TEAM_ZONES { get; } = new ReadOnlyCollection<Zone>(new List<Zone>
	{
		Zone.RedTeam_Zone,
		Zone.RedTeam_BehindGoalLine,
		Zone.RedTeam_Center
	});

	internal static Zone GetZone(Vector3 position, Zone oldZone, float radius)
	{
		float num = position.z + radius;
		if ((double)num < ICE_Z_POSITIONS[IceElement.RedTeam_GoalLine].Start)
		{
			return Zone.RedTeam_BehindGoalLine;
		}
		if ((double)num < ICE_Z_POSITIONS[IceElement.RedTeam_GoalLine].End && oldZone == Zone.RedTeam_BehindGoalLine)
		{
			if (oldZone == Zone.RedTeam_BehindGoalLine)
			{
				return Zone.RedTeam_BehindGoalLine;
			}
			return Zone.RedTeam_Zone;
		}
		if ((double)num < ICE_Z_POSITIONS[IceElement.RedTeam_BlueLine].Start)
		{
			return Zone.RedTeam_Zone;
		}
		if ((double)num < ICE_Z_POSITIONS[IceElement.RedTeam_BlueLine].End)
		{
			if (oldZone == Zone.RedTeam_Zone)
			{
				return Zone.RedTeam_Zone;
			}
			return Zone.RedTeam_Center;
		}
		if ((double)num < ICE_Z_POSITIONS[IceElement.CenterLine].Start)
		{
			return Zone.RedTeam_Center;
		}
		if ((double)num < ICE_Z_POSITIONS[IceElement.CenterLine].End && oldZone == Zone.RedTeam_Center)
		{
			return Zone.RedTeam_Center;
		}
		if ((double)num < ICE_Z_POSITIONS[IceElement.RedTeam_BlueLine].End)
		{
			if (oldZone == Zone.RedTeam_Center)
			{
				return Zone.RedTeam_Center;
			}
			return Zone.BlueTeam_Center;
		}
		if ((double)num < ICE_Z_POSITIONS[IceElement.BlueTeam_BlueLine].Start)
		{
			return Zone.BlueTeam_Center;
		}
		if ((double)num < ICE_Z_POSITIONS[IceElement.BlueTeam_BlueLine].End)
		{
			if (oldZone == Zone.BlueTeam_Center)
			{
				return Zone.BlueTeam_Center;
			}
			return Zone.BlueTeam_Zone;
		}
		if ((double)num < ICE_Z_POSITIONS[IceElement.BlueTeam_GoalLine].Start)
		{
			return Zone.BlueTeam_Zone;
		}
		if ((double)num < ICE_Z_POSITIONS[IceElement.BlueTeam_GoalLine].End)
		{
			if (oldZone == Zone.BlueTeam_Zone)
			{
				return Zone.BlueTeam_Zone;
			}
			return Zone.BlueTeam_BehindGoalLine;
		}
		return Zone.BlueTeam_BehindGoalLine;
	}

	internal static bool IsBehindHashmarks(PlayerTeam team, Vector3 position, float radius = 0f)
	{
		float num = position.z + radius;
		switch (team)
		{
		case PlayerTeam.Red:
		{
			double num3 = ICE_Z_POSITIONS[IceElement.RedTeam_HashMarks].Start + (ICE_Z_POSITIONS[IceElement.RedTeam_HashMarks].End - ICE_Z_POSITIONS[IceElement.RedTeam_HashMarks].Start);
			if ((double)num < num3)
			{
				return true;
			}
			break;
		}
		case PlayerTeam.Blue:
		{
			double num2 = ICE_Z_POSITIONS[IceElement.BlueTeam_HashMarks].End - (ICE_Z_POSITIONS[IceElement.BlueTeam_HashMarks].End - ICE_Z_POSITIONS[IceElement.BlueTeam_HashMarks].Start);
			if ((double)num > num2)
			{
				return true;
			}
			break;
		}
		}
		return false;
	}

	internal static Zone GetZone(FaceoffSpot faceoffSpot)
	{
		switch (faceoffSpot)
		{
		case FaceoffSpot.BlueteamDZoneLeft:
		case FaceoffSpot.BlueteamDZoneRight:
			return Zone.BlueTeam_Zone;
		case FaceoffSpot.BlueteamBLLeft:
		case FaceoffSpot.BlueteamBLRight:
			return Zone.BlueTeam_Center;
		case FaceoffSpot.RedteamDZoneLeft:
		case FaceoffSpot.RedteamDZoneRight:
			return Zone.RedTeam_Zone;
		case FaceoffSpot.RedteamBLLeft:
		case FaceoffSpot.RedteamBLRight:
			return Zone.RedTeam_Center;
		default:
			return Zone.BlueTeam_Center;
		}
	}

	internal static List<Zone> GetTeamZones(PlayerTeam team, bool includeCenter = false)
	{
		switch (team)
		{
		case PlayerTeam.Blue:
			if (includeCenter)
			{
				return BLUE_TEAM_ZONES.ToList();
			}
			return BLUE_TEAM_DEFENSE_ZONES.ToList();
		case PlayerTeam.Red:
			if (includeCenter)
			{
				return RED_TEAM_ZONES.ToList();
			}
			return RED_TEAM_DEFENSE_ZONES.ToList();
		default:
			return new List<Zone> { Zone.None };
		}
	}
}
