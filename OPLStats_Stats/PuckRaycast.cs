using System.Collections.Generic;
using Codebase;
using UnityEngine;

namespace OPLStats_Stats;

internal class PuckRaycast : MonoBehaviour
{
	private const int CHECK_EVERY_X_FRAMES = 6;

	private static readonly PlayerTeam[] GOAL_TEAMS = new PlayerTeam[2]
	{
		PlayerTeam.Blue,
		PlayerTeam.Red
	};

	private readonly Vector3 ABOVE_GROUND_VECTOR = new Vector3(0f, 0.001f, 0f);

	private readonly float RIGHT_OFFSET = 0.16f;

	private readonly Vector3 BOTTOM_VECTOR = new Vector3(0f, -0.6f, 0f);

	private readonly LayerMask _goalTriggerlayerMask = GetLayerMask("Goal Trigger");

	private readonly Dictionary<Collider, PlayerTeam> _goalTeamByCollider = new Dictionary<Collider, PlayerTeam>();

	private Ray _rayBottomLeft;

	private Ray _rayBottomRight;

	private Ray _rayFarBottomLeft;

	private Ray _rayFarBottomRight;

	private Vector3 _startingPosition;

	private int _increment;

	internal LockDictionary<PlayerTeam, bool> PuckIsGoingToNet { get; set; } = new LockDictionary<PlayerTeam, bool>
	{
		{
			PlayerTeam.Blue,
			false
		},
		{
			PlayerTeam.Red,
			false
		}
	};

	internal void Start()
	{
		ResetStartingPosition();
		_increment = 5;
		Update();
	}

	internal void Update()
	{
		if (++_increment != CHECK_EVERY_X_FRAMES)
		{
			return;
		}
		for (int i = 0; i < GOAL_TEAMS.Length; i++)
		{
			PuckIsGoingToNet[GOAL_TEAMS[i]] = false;
		}
		CheckForColliders();
		ResetStartingPosition();
	}

	private void ResetStartingPosition()
	{
		_startingPosition = base.transform.position;
		_increment = 0;
	}

	private void CheckForColliders()
	{
		float maxDistance = MonoBehaviourSingleton<PuckManager>.Instance?.GetPuck()?.Speed * 3.3f ?? 26f;
		_startingPosition.y = base.transform.position.y;
		Vector3 direction = base.transform.position - _startingPosition;
		if (direction.sqrMagnitude <= 1E-06f)
		{
			return;
		}
		Vector3 normalized = direction.normalized;
		Vector3 vector = Vector3.Cross(Vector3.down, normalized).normalized * RIGHT_OFFSET;
		Vector3 vector2 = base.transform.position + vector;
		Vector3 vector3 = base.transform.position - vector;
		_rayBottomLeft = new Ray(vector2 + ABOVE_GROUND_VECTOR, direction);
		RaycastHit hitInfo;
		bool flag = Physics.Raycast(_rayBottomLeft, out hitInfo, maxDistance, _goalTriggerlayerMask, QueryTriggerInteraction.Collide);
		if (!flag)
		{
			_rayBottomRight = new Ray(vector3 + ABOVE_GROUND_VECTOR, direction);
			flag = Physics.Raycast(_rayBottomRight, out hitInfo, maxDistance, _goalTriggerlayerMask, QueryTriggerInteraction.Collide);
			if (!flag)
			{
				_rayFarBottomLeft = new Ray(vector2 + BOTTOM_VECTOR, direction);
				flag = Physics.Raycast(_rayFarBottomLeft, out hitInfo, maxDistance, _goalTriggerlayerMask, QueryTriggerInteraction.Collide);
				if (!flag)
				{
					_rayFarBottomRight = new Ray(vector3 + BOTTOM_VECTOR, direction);
					flag = Physics.Raycast(_rayFarBottomRight, out hitInfo, maxDistance, _goalTriggerlayerMask, QueryTriggerInteraction.Collide);
					if (!flag)
					{
						return;
					}
				}
			}
		}
		if (!TryGetGoalTeam(hitInfo.collider, out var value))
		{
			return;
		}
		PuckIsGoingToNet[value] = flag;
	}

	private bool TryGetGoalTeam(Collider collider, out PlayerTeam team)
	{
		team = PlayerTeam.None;
		if (collider == null)
		{
			return false;
		}
		if (_goalTeamByCollider.TryGetValue(collider, out team))
		{
			return team != PlayerTeam.None;
		}
		GoalTrigger component = collider.gameObject.GetComponent<GoalTrigger>();
		if (component == null)
		{
			_goalTeamByCollider[collider] = PlayerTeam.None;
			return false;
		}
		Goal value;
		if (!SystemFunc.TryGetPrivateField(typeof(GoalTrigger), component, "goal", out value))
		{
			value = component.GetComponentInParent<Goal>();
		}
		if (value == null)
		{
			_goalTeamByCollider[collider] = PlayerTeam.None;
			return false;
		}
		if (!SystemFunc.TryGetPrivateField(typeof(Goal), value, "Team", out team))
		{
			_goalTeamByCollider[collider] = PlayerTeam.None;
			return false;
		}
		_goalTeamByCollider[collider] = team;
		return team != PlayerTeam.None;
	}

	private static LayerMask GetLayerMask(string layerName)
	{
		LayerMask layerMask = 0;
		for (int i = 0; i < 32; i++)
		{
			string text = LayerMask.LayerToName(i);
			if (!string.IsNullOrEmpty(text) && text == layerName)
			{
				layerMask = (int)layerMask | (1 << i);
				break;
			}
		}
		return layerMask;
	}
}
