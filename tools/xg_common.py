from __future__ import annotations

import csv
import json
import math
import random
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable


FEATURE_IDS = (
    "distance_to_net",
    "distance_squared",
    "lateral_distance",
    "shot_angle_deg",
    "shot_height",
    "home_plate",
    "outside",
    "offensive_zone",
    "neutral_zone",
    "defensive_zone",
    "force_magnitude",
    "puck_velocity",
    "player_speed_kph",
    "current_play_in_possession",
    "time_since_last_same_team_event",
    "distance_from_last_same_team_event",
    "lateral_change_from_last_same_team_event",
    "rebound_within_2s",
    "previous_same_team_shot_within_4s",
    "entry_within_6s",
    "same_team_event_within_2s",
    "midrange_entry",
    "sharp_close",
    "distance_spline_10",
    "distance_spline_20",
    "distance_spline_30",
    "angle_spline_15",
    "angle_spline_35",
    "angle_spline_55",
    "close_range",
    "slot_range",
    "point_range",
    "home_plate_rebound",
    "close_rebound",
    "home_plate_entry",
    "entry_rebound",
    "lateral_rebound",
)

INCLUDED_SHOT_OUTCOMES = ("goal", "on net", "missed", "blocked")
DEFAULT_BIN_EDGES = (0.0, 0.02, 0.05, 0.08, 0.12, 0.17, 0.25, 0.40, 1.000001)


@dataclass(frozen=True)
class GameSource:
    prefix: str
    playbyplay_path: Path
    stats_path: Path


@dataclass
class GoalInfo:
    game_time: float
    period: int
    team: str
    scorer: str
    primary_assist: str
    secondary_assist: str
    gwg: bool


@dataclass
class Event:
    event_id: int
    event_type: str
    game_time: float
    period: int
    team: str
    position_x: float
    position_y: float
    position_z: float
    player_position: str
    zone: str
    outcome: str
    flags: str
    force_magnitude: float
    puck_velocity: float
    player_speed_kph: float
    score_state: str
    current_play_in_possession: str
    team_forwards: str
    team_defense: str
    team_goalie: str
    opposing_forwards: str
    opposing_defense: str
    opposing_goalie: str
    player_steam_id: str


@dataclass
class ShotSample:
    game_id: str
    team: str
    event_id: int
    feature_map: dict[str, float]
    feature_row: list[float]
    raw_outcome: str
    raw_label: int
    resolved_label: int
    zone: str
    player_position: str
    distance_to_net: float
    shot_angle_deg: float
    home_plate: bool
    rebound_within_2s: bool
    entry_within_6s: bool


@dataclass
class LabelResolutionSummary:
    official_goals: int = 0
    raw_goal_shots: int = 0
    resolved_goal_shots: int = 0
    matched_official_goals_to_shots: int = 0
    unmatched_official_goals: int = 0
    team_mismatch_goal_matches: int = 0
    own_goal_events: int = 0
    matched_own_goals_to_shots: int = 0
    unmatched_own_goals: int = 0
    raw_goal_downgraded_to_onnet: int = 0
    nongoal_upgraded_to_goal: int = 0
    probable_shootout_goals_skipped: int = 0
    duplicate_goal_records_skipped: int = 0
    event_team_label_flips: int = 0

    def merge(self, other: "LabelResolutionSummary") -> None:
        for field_name in self.__dataclass_fields__:
            setattr(self, field_name, getattr(self, field_name) + getattr(other, field_name))

    def to_dict(self) -> dict[str, int]:
        return {field_name: getattr(self, field_name) for field_name in self.__dataclass_fields__}


def clean_string(value: str | None) -> str:
    return (value or "").strip()


def parse_int(value: str | None) -> int:
    try:
        return int(clean_string(value))
    except ValueError:
        return 0


def parse_float(value: str | None) -> float:
    try:
        return float(clean_string(value))
    except ValueError:
        return 0.0


def clamp(value: float, minimum: float, maximum: float) -> float:
    if value < minimum:
        return minimum
    if value > maximum:
        return maximum
    return value


def positive_part(value: float) -> float:
    return value if value > 0.0 else 0.0


def contains_ignore_case(value: str, needle: str) -> bool:
    return needle.lower() in clean_string(value).lower()


def clean_steam_id(value: str | None) -> str:
    text = clean_string(value)
    if text.startswith('="') and text.endswith('"') and len(text) >= 4:
        return text[2:-1]
    return text.strip('"')


def normalize_shot_outcome(outcome: str) -> str:
    normalized = clean_string(outcome).lower()
    return "missed" if normalized == "attempt" else normalized


def parse_score_diff_for_team(score_state: str, shooting_team: str) -> int:
    score_state = clean_string(score_state)
    if not score_state:
        return 0
    parts = score_state.split("-")
    if len(parts) != 2:
        return 0
    try:
        red_score = int(parts[0])
        blue_score = int(parts[1])
    except ValueError:
        return 0
    if clean_string(shooting_team) == "Blue":
        return blue_score - red_score
    return red_score - blue_score


def parse_current_play_in_possession(current_play_in_possession: str) -> int:
    try:
        return int(clean_string(current_play_in_possession))
    except ValueError:
        return 0


def extract_steam_ids_from_roster_field(roster_field: str) -> set[str]:
    values: set[str] = set()
    if not roster_field:
        return values
    for part in roster_field.split(";"):
        steam_id = clean_steam_id(part)
        if steam_id:
            values.add(steam_id)
    return values


def determine_shot_flag(position_x: float, position_z: float) -> str:
    absolute_z = abs(position_z)
    if absolute_z < 18.0 or absolute_z > 40.0:
        return "Outside"
    if 18.0 <= absolute_z <= 30.0 and -11.01 <= position_x <= 11.01:
        return "HomePlate"
    if 30.0 < absolute_z <= 40.0:
        normalized = (absolute_z - 30.0) / 10.0
        left_edge = -11.0 + normalized * 9.0
        right_edge = 11.0 - normalized * 9.0
        if left_edge - 0.01 <= position_x <= right_edge + 0.01:
            return "HomePlate"
    return "Outside"


def build_event(row: dict[str, str]) -> Event:
    return Event(
        event_id=parse_int(row.get("id")),
        event_type=clean_string(row.get("name")).lower(),
        game_time=parse_float(row.get("gameTime")),
        period=parse_int(row.get("period")),
        team=clean_string(row.get("team")),
        position_x=parse_float(row.get("xCoord")),
        position_y=parse_float(row.get("yCoord")),
        position_z=parse_float(row.get("zCoord")),
        player_position=clean_string(row.get("playerPosition")),
        zone=clean_string(row.get("zone")).lower(),
        outcome=clean_string(row.get("outcome")),
        flags=clean_string(row.get("flags")),
        force_magnitude=parse_float(row.get("forcemagnitude")),
        puck_velocity=parse_float(row.get("PuckVelocity")),
        player_speed_kph=parse_float(row.get("PlayerSpeed")),
        score_state=clean_string(row.get("scoreState")),
        current_play_in_possession=clean_string(row.get("currentPlayInPossession")),
        team_forwards=clean_string(row.get("teamForwardsSteamID")),
        team_defense=clean_string(row.get("teamDefencemenSteamID")),
        team_goalie=clean_string(row.get("teamGoalieSteamID")),
        opposing_forwards=clean_string(row.get("opposingTeamForwardsSteamID")),
        opposing_defense=clean_string(row.get("opposingTeamDefencemenSteamID")),
        opposing_goalie=clean_string(row.get("opposingTeamGoalieSteamID")),
        player_steam_id=clean_steam_id(row.get("playerReferenceSteamID")),
    )


def load_events(csv_path: Path) -> list[Event]:
    with csv_path.open("r", encoding="utf-8-sig", newline="") as handle:
        first_line = handle.readline()
        if not first_line:
            return []
        if not first_line.startswith("sep="):
            handle.seek(0)
        reader = csv.DictReader(handle)
        events = [build_event(row) for row in reader if row]
    events.sort(key=lambda event: (event.game_time, event.event_id))
    return events


def is_probable_shootout_goal(goal_info: GoalInfo) -> bool:
    return goal_info.period >= 5 and abs(goal_info.game_time - 1495.0) <= 0.01


def score_goal_info_richness(goal_info: GoalInfo) -> tuple[int, int, int]:
    return (
        1 if goal_info.gwg else 0,
        1 if goal_info.primary_assist else 0,
        1 if goal_info.secondary_assist else 0,
    )


def load_goal_infos_with_summary(stats_path: Path) -> tuple[list[GoalInfo], int, int]:
    payload = json.loads(stats_path.read_text(encoding="utf-8"))
    goals_payload = ((payload.get("teamStats") or {}).get("goals")) or []
    raw_goals = [
        GoalInfo(
            game_time=parse_float(str(item.get("gameTime", ""))),
            period=parse_int(str(item.get("period", ""))),
            team=clean_string(item.get("team")),
            scorer=clean_string(item.get("scorer")),
            primary_assist=clean_string(item.get("primaryAssist")),
            secondary_assist=clean_string(item.get("secondaryAssist")),
            gwg=bool(item.get("gwg", False)),
        )
        for item in goals_payload
    ]
    raw_goals.sort(key=lambda goal: (goal.period, goal.game_time))
    probable_shootout_goals_skipped = 0
    duplicate_goal_records_skipped = 0
    deduped_goals: dict[tuple[int, int, str, str], GoalInfo] = {}
    for goal in raw_goals:
        if is_probable_shootout_goal(goal):
            probable_shootout_goals_skipped += 1
            continue
        key = (goal.period, int(round(goal.game_time * 1000.0)), goal.team, goal.scorer)
        existing = deduped_goals.get(key)
        if existing is None:
            deduped_goals[key] = goal
            continue
        duplicate_goal_records_skipped += 1
        if score_goal_info_richness(goal) > score_goal_info_richness(existing):
            deduped_goals[key] = goal
    goals = sorted(deduped_goals.values(), key=lambda goal: (goal.period, goal.game_time))
    return goals, probable_shootout_goals_skipped, duplicate_goal_records_skipped


def load_goal_infos(stats_path: Path) -> list[GoalInfo]:
    goals, _, _ = load_goal_infos_with_summary(stats_path)
    goals.sort(key=lambda goal: (goal.period, goal.game_time))
    return goals


def list_game_sources(archive_dir: Path) -> list[GameSource]:
    playbyplay_files = {path.name[: -len("_playbyplay.csv")]: path for path in sorted(archive_dir.glob("*_playbyplay.csv"))}
    stats_files = {path.name[: -len("_stats.json")]: path for path in sorted(archive_dir.glob("*_stats.json"))}
    prefixes = sorted(set(playbyplay_files) & set(stats_files))
    return [GameSource(prefix=prefix, playbyplay_path=playbyplay_files[prefix], stats_path=stats_files[prefix]) for prefix in prefixes]


def split_game_sources(
    game_sources: list[GameSource], validation_fraction: float, test_fraction: float
) -> tuple[list[GameSource], list[GameSource], list[GameSource]]:
    total = len(game_sources)
    if total == 0:
        return [], [], []
    if total < 3 or (validation_fraction <= 0.0 and test_fraction <= 0.0):
        return list(game_sources), [], []

    validation_count = max(1, int(round(total * max(0.0, validation_fraction))))
    test_count = max(1, int(round(total * max(0.0, test_fraction))))

    while total - validation_count - test_count < 1:
        if validation_count >= test_count and validation_count > 1:
            validation_count -= 1
        elif test_count > 1:
            test_count -= 1
        else:
            break

    train_count = max(1, total - validation_count - test_count)
    validation_start = train_count
    test_start = train_count + validation_count
    return game_sources[:train_count], game_sources[validation_start:test_start], game_sources[test_start:]


def is_eligible_shot(event: Event, normalized_outcome: str) -> bool:
    if event.event_type != "shot":
        return False
    if event.team not in {"Red", "Blue"}:
        return False
    return normalized_outcome in INCLUDED_SHOT_OUTCOMES


def infer_attacking_net_z_by_team(events: list[Event]) -> dict[str, float]:
    z_sums = {"Red": 0.0, "Blue": 0.0}
    counts = {"Red": 0, "Blue": 0}
    for event in events:
        normalized_outcome = normalize_shot_outcome(event.outcome)
        if event.zone != "oz" or not is_eligible_shot(event, normalized_outcome):
            continue
        z_sums[event.team] += event.position_z
        counts[event.team] += 1

    result: dict[str, float] = {}
    for team in ("Red", "Blue"):
        if counts[team] > 0:
            result[team] = 40.0 if (z_sums[team] / counts[team]) >= 0.0 else -40.0
        else:
            result[team] = 40.0 if team == "Red" else -40.0
    return result


def find_previous_same_team_event(events: list[Event], shot_index: int) -> Event | None:
    shot = events[shot_index]
    for index in range(shot_index - 1, -1, -1):
        candidate = events[index]
        if candidate.team == shot.team and candidate.event_type not in {"hit", "posthit", "gameend"}:
            return candidate
    return None


def find_previous_same_team_shot(events: list[Event], shot_index: int) -> Event | None:
    shot = events[shot_index]
    for index in range(shot_index - 1, -1, -1):
        candidate = events[index]
        if candidate.team == shot.team and candidate.event_type == "shot":
            return candidate
    return None


def build_expected_goal_features(
    events: list[Event],
    shot_index: int,
    attacking_net_z_by_team: dict[str, float] | None = None,
) -> dict[str, float]:
    shot = events[shot_index]
    default_net_z = -40.0 if shot.team == "Blue" else 40.0
    net_z = (attacking_net_z_by_team or {}).get(shot.team, default_net_z)
    delta_z = net_z - shot.position_z
    lateral_distance = abs(shot.position_x)
    distance_to_net = math.sqrt(lateral_distance * lateral_distance + delta_z * delta_z)
    shot_angle_deg = math.degrees(math.atan2(lateral_distance, max(0.1, abs(delta_z))))
    derived_flag = determine_shot_flag(shot.position_x, shot.position_z)
    is_home_plate = contains_ignore_case(shot.flags, "HomePlate") or derived_flag == "HomePlate"

    previous_same_team_event = find_previous_same_team_event(events, shot_index)
    previous_same_team_shot = find_previous_same_team_shot(events, shot_index)

    time_since_last_same_team_event = 10.0
    distance_from_last_same_team_event = 0.0
    lateral_change_from_last_same_team_event = 0.0
    same_team_event_within_2s = False
    if previous_same_team_event is not None:
        time_since_last_same_team_event = clamp(shot.game_time - previous_same_team_event.game_time, 0.0, 10.0)
        delta_x = shot.position_x - previous_same_team_event.position_x
        delta_z_prev = shot.position_z - previous_same_team_event.position_z
        distance_from_last_same_team_event = math.sqrt(delta_x * delta_x + delta_z_prev * delta_z_prev)
        lateral_change_from_last_same_team_event = abs(delta_x)
        same_team_event_within_2s = time_since_last_same_team_event <= 2.0

    previous_same_team_shot_within_4s = False
    rebound_within_2s = False
    if previous_same_team_shot is not None:
        delta_shot_time = shot.game_time - previous_same_team_shot.game_time
        previous_same_team_shot_within_4s = 0.0 <= delta_shot_time <= 4.0
        rebound_within_2s = 0.0 <= delta_shot_time <= 2.0

    entry_within_6s = False
    for previous_index in range(shot_index - 1, -1, -1):
        previous_event = events[previous_index]
        delta_time = shot.game_time - previous_event.game_time
        if delta_time < 0.0:
            continue
        if delta_time > 6.0:
            break
        if previous_event.team == shot.team and contains_ignore_case(previous_event.flags, "OZEntry"):
            entry_within_6s = True
            break

    # Convert the scoreboard string into the shooting team's perspective.
    score_diff = parse_score_diff_for_team(shot.score_state, shot.team)
    current_play_in_possession = parse_current_play_in_possession(shot.current_play_in_possession)
    own_skaters_on_ice = len(extract_steam_ids_from_roster_field(shot.team_forwards)) + len(
        extract_steam_ids_from_roster_field(shot.team_defense)
    )
    opposing_skaters_on_ice = len(extract_steam_ids_from_roster_field(shot.opposing_forwards)) + len(
        extract_steam_ids_from_roster_field(shot.opposing_defense)
    )
    opposing_goalie_present = 1.0 if extract_steam_ids_from_roster_field(shot.opposing_goalie) else 0.0
    is_midrange_entry = 15.0 < distance_to_net <= 25.0 and entry_within_6s
    is_sharp_close = distance_to_net <= 15.0 and shot_angle_deg >= 45.0
    distance_spline_10 = positive_part(distance_to_net - 10.0)
    distance_spline_20 = positive_part(distance_to_net - 20.0)
    distance_spline_30 = positive_part(distance_to_net - 30.0)
    angle_spline_15 = positive_part(shot_angle_deg - 15.0)
    angle_spline_35 = positive_part(shot_angle_deg - 35.0)
    angle_spline_55 = positive_part(shot_angle_deg - 55.0)
    close_range = distance_to_net <= 12.0
    slot_range = distance_to_net <= 20.0 and is_home_plate
    point_range = distance_to_net >= 30.0
    home_plate_rebound = is_home_plate and rebound_within_2s
    close_rebound = distance_to_net <= 15.0 and rebound_within_2s
    home_plate_entry = is_home_plate and entry_within_6s
    entry_rebound = entry_within_6s and rebound_within_2s
    lateral_rebound = lateral_change_from_last_same_team_event if rebound_within_2s else 0.0

    return {
        "distance_to_net": distance_to_net,
        "distance_squared": distance_to_net * distance_to_net,
        "lateral_distance": lateral_distance,
        "shot_angle_deg": shot_angle_deg,
        "shot_height": shot.position_y,
        "home_plate": 1.0 if is_home_plate else 0.0,
        "outside": 0.0 if is_home_plate else 1.0,
        "offensive_zone": 1.0 if shot.zone == "oz" else 0.0,
        "neutral_zone": 1.0 if shot.zone == "nz" else 0.0,
        "defensive_zone": 1.0 if shot.zone == "dz" else 0.0,
        "force_magnitude": shot.force_magnitude,
        "puck_velocity": shot.puck_velocity,
        "player_speed_kph": shot.player_speed_kph,
        "score_diff": float(score_diff),
        "leading": 1.0 if score_diff > 0 else 0.0,
        "trailing": 1.0 if score_diff < 0 else 0.0,
        "tied": 1.0 if score_diff == 0 else 0.0,
        "own_skaters_on_ice": float(own_skaters_on_ice),
        "opposing_skaters_on_ice": float(opposing_skaters_on_ice),
        "opposing_goalie_present": opposing_goalie_present,
        "current_play_in_possession": float(current_play_in_possession),
        "period": float(shot.period),
        "time_since_last_same_team_event": time_since_last_same_team_event,
        "distance_from_last_same_team_event": distance_from_last_same_team_event,
        "lateral_change_from_last_same_team_event": lateral_change_from_last_same_team_event,
        "rebound_within_2s": 1.0 if rebound_within_2s else 0.0,
        "previous_same_team_shot_within_4s": 1.0 if previous_same_team_shot_within_4s else 0.0,
        "entry_within_6s": 1.0 if entry_within_6s else 0.0,
        "same_team_event_within_2s": 1.0 if same_team_event_within_2s else 0.0,
        "midrange_entry": 1.0 if is_midrange_entry else 0.0,
        "sharp_close": 1.0 if is_sharp_close else 0.0,
        "distance_spline_10": distance_spline_10,
        "distance_spline_20": distance_spline_20,
        "distance_spline_30": distance_spline_30,
        "angle_spline_15": angle_spline_15,
        "angle_spline_35": angle_spline_35,
        "angle_spline_55": angle_spline_55,
        "close_range": 1.0 if close_range else 0.0,
        "slot_range": 1.0 if slot_range else 0.0,
        "point_range": 1.0 if point_range else 0.0,
        "home_plate_rebound": 1.0 if home_plate_rebound else 0.0,
        "close_rebound": 1.0 if close_rebound else 0.0,
        "home_plate_entry": 1.0 if home_plate_entry else 0.0,
        "entry_rebound": 1.0 if entry_rebound else 0.0,
        "lateral_rebound": lateral_rebound,
        "is_center": 1.0 if shot.player_position.upper() == "C" else 0.0,
        "is_wing": 1.0 if shot.player_position.upper() in {"LW", "RW"} else 0.0,
        "is_defense": 1.0 if shot.player_position.upper() in {"LD", "RD"} else 0.0,
        "is_goalie": 1.0 if shot.player_position.upper() == "G" else 0.0,
    }


def get_official_own_goal_events(events: list[Event]) -> list[int]:
    result: list[int] = []
    last_time = -999.0
    last_period = -1
    last_team = ""
    for index, event in enumerate(events):
        if event.event_type != "owngoal":
            continue
        if event.period == last_period and event.team == last_team and abs(event.game_time - last_time) <= 1.5:
            continue
        result.append(index)
        last_time = event.game_time
        last_period = event.period
        last_team = event.team
    return result


def find_candidate_shot_index_for_official_goal(
    events: list[Event], goal_info: GoalInfo, reserved_shot_indices: set[int], require_team_match: bool
) -> int:
    fallback_index: int | None = None
    for index in range(len(events) - 1, -1, -1):
        event = events[index]
        if event.period != goal_info.period:
            if event.period < goal_info.period:
                break
            continue
        if event.game_time > goal_info.game_time + 1.0:
            continue
        if goal_info.game_time - event.game_time > 15.0:
            break
        if (
            index in reserved_shot_indices
            or event.event_type != "shot"
            or event.player_steam_id != goal_info.scorer
            or (require_team_match and goal_info.team and event.team != goal_info.team)
        ):
            continue
        outcome = normalize_shot_outcome(event.outcome)
        if outcome in {"goal", "on net"}:
            return index
        if fallback_index is None:
            fallback_index = index
    return fallback_index if fallback_index is not None else -1


def find_best_shot_index_for_official_goal(
    events: list[Event], goal_info: GoalInfo, reserved_shot_indices: set[int]
) -> tuple[int, bool]:
    require_team_match = bool(goal_info.team)
    shot_index = find_candidate_shot_index_for_official_goal(
        events,
        goal_info,
        reserved_shot_indices,
        require_team_match=require_team_match,
    )
    if shot_index >= 0 or not require_team_match:
        return shot_index, False

    relaxed_shot_index = find_candidate_shot_index_for_official_goal(
        events,
        goal_info,
        reserved_shot_indices,
        require_team_match=False,
    )
    if relaxed_shot_index >= 0:
        return relaxed_shot_index, True
    return -1, False


def flip_team_label(team: str) -> str:
    if team == "Red":
        return "Blue"
    if team == "Blue":
        return "Red"
    return team


def infer_event_team_labels_flipped(events: list[Event], goals: list[GoalInfo]) -> bool:
    direct_matches = 0
    flipped_matches = 0
    for goal in goals:
        shot_index = find_candidate_shot_index_for_official_goal(events, goal, set(), require_team_match=False)
        if shot_index < 0:
            continue
        event_team = events[shot_index].team
        if event_team == goal.team:
            direct_matches += 1
        elif flip_team_label(event_team) == goal.team:
            flipped_matches += 1
    return flipped_matches >= 3 and flipped_matches > direct_matches


def normalize_event_team_labels(events: list[Event], goals: list[GoalInfo]) -> bool:
    if not infer_event_team_labels_flipped(events, goals):
        return False
    for event in events:
        event.team = flip_team_label(event.team)
    return True


def opposite_team(team: str) -> str:
    if team == "Red":
        return "Blue"
    if team == "Blue":
        return "Red"
    return ""


def find_best_shot_index_for_own_goal_event(
    events: list[Event], own_goal_event_index: int, reserved_shot_indices: set[int]
) -> int:
    if own_goal_event_index <= 0 or own_goal_event_index >= len(events):
        return -1
    own_goal_event = events[own_goal_event_index]
    shot_team = opposite_team(own_goal_event.team)
    if not shot_team:
        return -1
    fallback_index: int | None = None
    for index in range(own_goal_event_index - 1, -1, -1):
        event = events[index]
        if event.period != own_goal_event.period:
            break
        if own_goal_event.game_time - event.game_time > 15.0:
            break
        if index in reserved_shot_indices or event.event_type != "shot" or event.team != shot_team:
            continue
        outcome = normalize_shot_outcome(event.outcome)
        if outcome in {"goal", "on net"}:
            return index
        if fallback_index is None:
            fallback_index = index
    return fallback_index if fallback_index is not None else -1


def resolve_shot_outcome(event: Event, official_goal_shot_event_ids: set[int], own_goal_shot_event_ids: set[int]) -> str:
    if event.event_type != "shot":
        return normalize_shot_outcome(event.outcome)
    if event.event_id in official_goal_shot_event_ids:
        return "goal"
    raw_outcome = normalize_shot_outcome(event.outcome)
    if event.event_id in own_goal_shot_event_ids or raw_outcome == "goal":
        return "on net"
    return raw_outcome


def build_shot_samples_for_game(game_source: GameSource) -> tuple[list[ShotSample], LabelResolutionSummary]:
    events = load_events(game_source.playbyplay_path)
    goals, probable_shootout_goals_skipped, duplicate_goal_records_skipped = load_goal_infos_with_summary(game_source.stats_path)
    summary = LabelResolutionSummary(
        official_goals=len(goals),
        probable_shootout_goals_skipped=probable_shootout_goals_skipped,
        duplicate_goal_records_skipped=duplicate_goal_records_skipped,
    )
    if normalize_event_team_labels(events, goals):
        summary.event_team_label_flips += 1
    attacking_net_z_by_team = infer_attacking_net_z_by_team(events)

    reserved_shot_indices: set[int] = set()
    official_goal_shot_event_ids: set[int] = set()
    for goal in goals:
        shot_index, used_team_mismatch_fallback = find_best_shot_index_for_official_goal(events, goal, reserved_shot_indices)
        if shot_index >= 0:
            reserved_shot_indices.add(shot_index)
            official_goal_shot_event_ids.add(events[shot_index].event_id)
            summary.matched_official_goals_to_shots += 1
            if used_team_mismatch_fallback:
                summary.team_mismatch_goal_matches += 1
        else:
            summary.unmatched_official_goals += 1

    own_goal_shot_event_ids: set[int] = set()
    own_goal_event_indices = get_official_own_goal_events(events)
    summary.own_goal_events = len(own_goal_event_indices)
    for own_goal_event_index in own_goal_event_indices:
        shot_index = find_best_shot_index_for_own_goal_event(events, own_goal_event_index, reserved_shot_indices)
        if shot_index >= 0:
            reserved_shot_indices.add(shot_index)
            own_goal_shot_event_ids.add(events[shot_index].event_id)
            summary.matched_own_goals_to_shots += 1
        else:
            summary.unmatched_own_goals += 1

    samples: list[ShotSample] = []
    for index, event in enumerate(events):
        raw_outcome = normalize_shot_outcome(event.outcome)
        if not is_eligible_shot(event, raw_outcome):
            continue
        feature_map = build_expected_goal_features(events, index, attacking_net_z_by_team)
        resolved_outcome = resolve_shot_outcome(event, official_goal_shot_event_ids, own_goal_shot_event_ids)
        raw_label = 1 if raw_outcome == "goal" else 0
        resolved_label = 1 if resolved_outcome == "goal" else 0

        summary.raw_goal_shots += raw_label
        summary.resolved_goal_shots += resolved_label
        if raw_label == 1 and resolved_outcome == "on net":
            summary.raw_goal_downgraded_to_onnet += 1
        if raw_label == 0 and resolved_label == 1:
            summary.nongoal_upgraded_to_goal += 1

        samples.append(
            ShotSample(
                game_id=game_source.prefix,
                team=event.team,
                event_id=event.event_id,
                feature_map=feature_map,
                feature_row=[feature_map[feature_id] for feature_id in FEATURE_IDS],
                raw_outcome=raw_outcome,
                raw_label=raw_label,
                resolved_label=resolved_label,
                zone=event.zone,
                player_position=event.player_position,
                distance_to_net=feature_map["distance_to_net"],
                shot_angle_deg=feature_map["shot_angle_deg"],
                home_plate=feature_map["home_plate"] > 0.5,
                rebound_within_2s=feature_map["rebound_within_2s"] > 0.5,
                entry_within_6s=feature_map["entry_within_6s"] > 0.5,
            )
        )
    return samples, summary


def build_shot_samples_for_games(game_sources: Iterable[GameSource]) -> tuple[list[ShotSample], LabelResolutionSummary]:
    samples: list[ShotSample] = []
    summary = LabelResolutionSummary()
    for game_source in game_sources:
        game_samples, game_summary = build_shot_samples_for_game(game_source)
        samples.extend(game_samples)
        summary.merge(game_summary)
    return samples, summary


def standardize_rows(rows: list[list[float]]) -> tuple[list[list[float]], list[float], list[float]]:
    if not rows:
        return [], [0.0] * len(FEATURE_IDS), [1.0] * len(FEATURE_IDS)

    feature_count = len(FEATURE_IDS)
    row_count = len(rows)
    means = [0.0] * feature_count
    for row in rows:
        for index in range(feature_count):
            means[index] += row[index]
    means = [value / row_count for value in means]

    variances = [0.0] * feature_count
    for row in rows:
        for index in range(feature_count):
            delta = row[index] - means[index]
            variances[index] += delta * delta
    std_devs = [math.sqrt(value / row_count) for value in variances]

    return apply_standardization(rows, means, std_devs), means, std_devs


def apply_standardization(rows: list[list[float]], means: list[float], std_devs: list[float]) -> list[list[float]]:
    standardized_rows: list[list[float]] = []
    for row in rows:
        standardized_rows.append(
            [
                0.0 if std_devs[index] < 1e-9 else (row[index] - means[index]) / std_devs[index]
                for index in range(len(row))
            ]
        )
    return standardized_rows


def predict_probability(intercept: float, weights: list[float], row: Iterable[float]) -> float:
    linear_value = intercept
    for weight, feature_value in zip(weights, row):
        linear_value += weight * feature_value
    linear_value = clamp(linear_value, -60.0, 60.0)
    return 1.0 / (1.0 + math.exp(-linear_value))


def compute_log_loss(probabilities: list[float], labels: list[int]) -> float:
    epsilon = 1e-9
    total = 0.0
    for probability, label in zip(probabilities, labels):
        clipped = clamp(probability, epsilon, 1.0 - epsilon)
        total += -(label * math.log(clipped) + (1 - label) * math.log(1.0 - clipped))
    return total / max(1, len(labels))


def compute_brier_score(probabilities: list[float], labels: list[int]) -> float:
    total = 0.0
    for probability, label in zip(probabilities, labels):
        delta = probability - label
        total += delta * delta
    return total / max(1, len(labels))


def compute_group_residual_metrics(
    probabilities: list[float], labels: list[int], group_ids: list[str] | None
) -> dict[str, float]:
    if not probabilities or not group_ids or len(probabilities) != len(group_ids):
        return {"mae": 0.0, "rmse": 0.0, "mse": 0.0}

    predicted_by_group: dict[str, float] = {}
    actual_by_group: dict[str, float] = {}
    for probability, label, group_id in zip(probabilities, labels, group_ids):
        predicted_by_group[group_id] = predicted_by_group.get(group_id, 0.0) + probability
        actual_by_group[group_id] = actual_by_group.get(group_id, 0.0) + float(label)

    residuals = [predicted_by_group[group_id] - actual_by_group[group_id] for group_id in predicted_by_group]
    if not residuals:
        return {"mae": 0.0, "rmse": 0.0, "mse": 0.0}

    mse = sum(residual * residual for residual in residuals) / len(residuals)
    mae = sum(abs(residual) for residual in residuals) / len(residuals)
    return {"mae": mae, "rmse": math.sqrt(mse), "mse": mse}


def train_logistic_regression(
    rows: list[list[float]],
    labels: list[int],
    epochs: int,
    learning_rate: float,
    l2: float,
    seed: int,
    validation_rows: list[list[float]] | None = None,
    validation_labels: list[int] | None = None,
    group_ids: list[str] | None = None,
    validation_group_ids: list[str] | None = None,
    team_game_penalty: float = 0.0,
) -> tuple[float, list[float], dict[str, object]]:
    feature_count = len(FEATURE_IDS)
    positive_rate = clamp(sum(labels) / max(1, len(labels)), 1e-6, 1.0 - 1e-6)
    intercept = math.log(positive_rate / (1.0 - positive_rate))
    weights = [0.0] * feature_count

    beta1 = 0.9
    beta2 = 0.999
    epsilon = 1e-8
    m_weights = [0.0] * feature_count
    v_weights = [0.0] * feature_count
    m_intercept = 0.0
    v_intercept = 0.0
    step = 0

    indices = list(range(len(rows)))
    rng = random.Random(seed)

    best_intercept = intercept
    best_weights = list(weights)
    best_validation_logloss = float("inf")
    history: list[dict[str, float]] = []
    has_validation = bool(validation_rows) and bool(validation_labels)
    use_team_game_penalty = (
        team_game_penalty > 0.0
        and bool(group_ids)
        and len(group_ids or []) == len(rows)
    )

    if use_team_game_penalty:
        best_validation_objective = float("inf")
        for epoch in range(epochs):
            probabilities = [predict_probability(intercept, weights, row) for row in rows]
            shot_gradients = [probability - label for probability, label in zip(probabilities, labels)]
            if use_team_game_penalty:
                predicted_by_group: dict[str, float] = {}
                actual_by_group: dict[str, float] = {}
                for probability, label, group_id in zip(probabilities, labels, group_ids or []):
                    predicted_by_group[group_id] = predicted_by_group.get(group_id, 0.0) + probability
                    actual_by_group[group_id] = actual_by_group.get(group_id, 0.0) + float(label)
                residual_by_group = {
                    group_id: predicted_by_group[group_id] - actual_by_group.get(group_id, 0.0)
                    for group_id in predicted_by_group
                }
                for index, (probability, group_id) in enumerate(zip(probabilities, group_ids or [])):
                    shot_gradients[index] += (
                        2.0 * team_game_penalty * residual_by_group[group_id] * probability * (1.0 - probability)
                    )

            scale = 1.0 / max(1, len(rows))
            grad_intercept = sum(shot_gradients) * scale
            m_intercept = beta1 * m_intercept + (1.0 - beta1) * grad_intercept
            v_intercept = beta2 * v_intercept + (1.0 - beta2) * grad_intercept * grad_intercept
            step += 1
            m_hat_intercept = m_intercept / (1.0 - beta1**step)
            v_hat_intercept = v_intercept / (1.0 - beta2**step)
            intercept -= learning_rate * m_hat_intercept / (math.sqrt(v_hat_intercept) + epsilon)

            for feature_index in range(feature_count):
                gradient = l2 * weights[feature_index]
                for row, shot_gradient in zip(rows, shot_gradients):
                    gradient += shot_gradient * row[feature_index] * scale
                m_weights[feature_index] = beta1 * m_weights[feature_index] + (1.0 - beta1) * gradient
                v_weights[feature_index] = beta2 * v_weights[feature_index] + (1.0 - beta2) * gradient * gradient
                m_hat = m_weights[feature_index] / (1.0 - beta1**step)
                v_hat = v_weights[feature_index] / (1.0 - beta2**step)
                weights[feature_index] -= learning_rate * m_hat / (math.sqrt(v_hat) + epsilon)

            train_probabilities = [predict_probability(intercept, weights, row) for row in rows]
            train_logloss = compute_log_loss(train_probabilities, labels)
            train_group_metrics = compute_group_residual_metrics(train_probabilities, labels, group_ids)
            metrics_row = {
                "epoch": float(epoch + 1),
                "train_logloss": train_logloss,
                "train_brier": compute_brier_score(train_probabilities, labels),
                "train_team_game_mae": train_group_metrics["mae"],
                "train_team_game_rmse": train_group_metrics["rmse"],
            }
            train_objective = train_logloss + team_game_penalty * train_group_metrics["mse"]

            if has_validation:
                validation_probabilities = [predict_probability(intercept, weights, row) for row in validation_rows or []]
                validation_logloss = compute_log_loss(validation_probabilities, validation_labels or [])
                validation_group_metrics = compute_group_residual_metrics(
                    validation_probabilities, validation_labels or [], validation_group_ids
                )
                metrics_row["validation_logloss"] = validation_logloss
                metrics_row["validation_brier"] = compute_brier_score(validation_probabilities, validation_labels or [])
                metrics_row["validation_team_game_mae"] = validation_group_metrics["mae"]
                metrics_row["validation_team_game_rmse"] = validation_group_metrics["rmse"]
                validation_objective = validation_logloss + team_game_penalty * validation_group_metrics["mse"]
                if validation_logloss < best_validation_logloss:
                    best_validation_logloss = validation_logloss
                if validation_objective < best_validation_objective:
                    best_validation_objective = validation_objective
                    best_intercept = intercept
                    best_weights = list(weights)
            else:
                if train_logloss < best_validation_logloss:
                    best_validation_logloss = train_logloss
                if train_objective < best_validation_objective:
                    best_validation_objective = train_objective
                    best_intercept = intercept
                    best_weights = list(weights)

            history.append(metrics_row)
            if epoch in {0, epochs - 1} or (epoch + 1) % 6 == 0:
                if has_validation:
                    print(
                        f"epoch {epoch + 1}/{epochs} "
                        f"train_logloss={metrics_row['train_logloss']:.5f} "
                        f"validation_logloss={metrics_row['validation_logloss']:.5f} "
                        f"train_team_mae={metrics_row['train_team_game_mae']:.5f} "
                        f"validation_team_mae={metrics_row['validation_team_game_mae']:.5f}"
                    )
                else:
                    print(
                        f"epoch {epoch + 1}/{epochs} "
                        f"train_logloss={metrics_row['train_logloss']:.5f} "
                        f"train_team_mae={metrics_row['train_team_game_mae']:.5f}"
                    )

        metadata = {
            "best_validation_logloss": best_validation_logloss,
            "used_validation_early_stopping": has_validation,
            "used_team_game_penalty": True,
            "team_game_penalty": team_game_penalty,
            "history": history,
        }
        return best_intercept, best_weights, metadata

    for epoch in range(epochs):
        rng.shuffle(indices)
        for row_index in indices:
            step += 1
            row = rows[row_index]
            label = labels[row_index]
            probability = predict_probability(intercept, weights, row)
            error = probability - label

            grad_intercept = error
            m_intercept = beta1 * m_intercept + (1.0 - beta1) * grad_intercept
            v_intercept = beta2 * v_intercept + (1.0 - beta2) * grad_intercept * grad_intercept
            m_hat_intercept = m_intercept / (1.0 - beta1**step)
            v_hat_intercept = v_intercept / (1.0 - beta2**step)
            intercept -= learning_rate * m_hat_intercept / (math.sqrt(v_hat_intercept) + epsilon)

            for feature_index in range(feature_count):
                gradient = error * row[feature_index] + l2 * weights[feature_index]
                m_weights[feature_index] = beta1 * m_weights[feature_index] + (1.0 - beta1) * gradient
                v_weights[feature_index] = beta2 * v_weights[feature_index] + (1.0 - beta2) * gradient * gradient
                m_hat = m_weights[feature_index] / (1.0 - beta1**step)
                v_hat = v_weights[feature_index] / (1.0 - beta2**step)
                weights[feature_index] -= learning_rate * m_hat / (math.sqrt(v_hat) + epsilon)

        train_probabilities = [predict_probability(intercept, weights, row) for row in rows]
        train_logloss = compute_log_loss(train_probabilities, labels)
        metrics_row = {
            "epoch": float(epoch + 1),
            "train_logloss": train_logloss,
            "train_brier": compute_brier_score(train_probabilities, labels),
        }

        if has_validation:
            validation_probabilities = [predict_probability(intercept, weights, row) for row in validation_rows or []]
            validation_logloss = compute_log_loss(validation_probabilities, validation_labels or [])
            metrics_row["validation_logloss"] = validation_logloss
            metrics_row["validation_brier"] = compute_brier_score(validation_probabilities, validation_labels or [])
            if validation_logloss < best_validation_logloss:
                best_validation_logloss = validation_logloss
                best_intercept = intercept
                best_weights = list(weights)
        else:
            if train_logloss < best_validation_logloss:
                best_validation_logloss = train_logloss
                best_intercept = intercept
                best_weights = list(weights)

        history.append(metrics_row)
        if epoch in {0, epochs - 1} or (epoch + 1) % 6 == 0:
            if has_validation:
                print(
                    f"epoch {epoch + 1}/{epochs} "
                    f"train_logloss={metrics_row['train_logloss']:.5f} "
                    f"validation_logloss={metrics_row['validation_logloss']:.5f} "
                    f"train_brier={metrics_row['train_brier']:.5f} "
                    f"validation_brier={metrics_row['validation_brier']:.5f}"
                )
            else:
                print(
                    f"epoch {epoch + 1}/{epochs} "
                    f"train_logloss={metrics_row['train_logloss']:.5f} "
                    f"train_brier={metrics_row['train_brier']:.5f}"
                )

    metadata: dict[str, object] = {
        "best_validation_logloss": best_validation_logloss,
        "used_validation_early_stopping": has_validation,
        "used_team_game_penalty": False,
        "team_game_penalty": team_game_penalty,
        "history": history,
    }
    return best_intercept, best_weights, metadata


def convert_standardized_weights_to_raw(
    intercept_std: float, weights_std: list[float], means: list[float], std_devs: list[float]
) -> tuple[float, list[float]]:
    intercept_raw = intercept_std
    weights_raw = [0.0] * len(weights_std)

    for index, weight in enumerate(weights_std):
        std_dev = std_devs[index]
        if std_dev < 1e-9:
            weights_raw[index] = 0.0
            continue
        weights_raw[index] = weight / std_dev
        intercept_raw -= weight * means[index] / std_dev

    return intercept_raw, weights_raw


def fit_intercept_only_calibration(
    probabilities: list[float], labels: list[int], sample_weights: list[float] | None = None, max_iterations: int = 50
) -> float:
    if not probabilities:
        return 0.0
    if sample_weights is None:
        sample_weights = [1.0] * len(probabilities)

    logits = [math.log(clamp(probability, 1e-9, 1.0 - 1e-9) / clamp(1.0 - probability, 1e-9, 1.0)) for probability in probabilities]
    intercept = 0.0

    for _ in range(max_iterations):
        gradient = 0.0
        hessian = 0.0
        for logit_value, label, sample_weight in zip(logits, labels, sample_weights):
            probability = predict_probability(intercept + logit_value, [], [])
            logistic_weight = probability * (1.0 - probability)
            error = probability - label
            gradient += sample_weight * error
            hessian += sample_weight * logistic_weight
        if hessian <= 1e-12:
            break

        delta_intercept = gradient / hessian
        intercept -= delta_intercept
        if abs(delta_intercept) < 1e-8:
            break

    return intercept


def fit_platt_scaler(
    probabilities: list[float], labels: list[int], sample_weights: list[float] | None = None, max_iterations: int = 50
) -> tuple[float, float]:
    if not probabilities:
        return 0.0, 1.0
    if sample_weights is None:
        sample_weights = [1.0] * len(probabilities)

    logits = [math.log(clamp(probability, 1e-9, 1.0 - 1e-9) / clamp(1.0 - probability, 1e-9, 1.0)) for probability in probabilities]
    intercept = 0.0
    slope = 1.0

    for _ in range(max_iterations):
        grad_intercept = 0.0
        grad_slope = 0.0
        h11 = 0.0
        h12 = 0.0
        h22 = 0.0

        for logit_value, label, sample_weight in zip(logits, labels, sample_weights):
            linear_value = intercept + slope * logit_value
            probability = predict_probability(linear_value, [], [])
            logistic_weight = probability * (1.0 - probability)
            error = probability - label

            grad_intercept += sample_weight * error
            grad_slope += sample_weight * error * logit_value
            h11 += sample_weight * logistic_weight
            h12 += sample_weight * logistic_weight * logit_value
            h22 += sample_weight * logistic_weight * logit_value * logit_value

        determinant = h11 * h22 - h12 * h12
        if abs(determinant) < 1e-12:
            break

        delta_intercept = (grad_intercept * h22 - grad_slope * h12) / determinant
        delta_slope = (-grad_intercept * h12 + grad_slope * h11) / determinant
        intercept -= delta_intercept
        slope -= delta_slope

        if abs(delta_intercept) < 1e-8 and abs(delta_slope) < 1e-8:
            break

    return intercept, slope


def apply_intercept_only_calibration_to_raw_coefficients(
    intercept_raw: float, weights_raw: list[float], calibration_intercept: float
) -> tuple[float, list[float]]:
    return intercept_raw + calibration_intercept, list(weights_raw)


def apply_platt_to_raw_coefficients(
    intercept_raw: float, weights_raw: list[float], calibration_intercept: float, calibration_slope: float
) -> tuple[float, list[float]]:
    return calibration_intercept + calibration_slope * intercept_raw, [calibration_slope * weight for weight in weights_raw]


def get_model_weights_in_feature_order(model_payload: dict[str, object]) -> list[float]:
    weights_map = model_payload.get("Weights") or {}
    return [float(weights_map.get(feature_id, 0.0)) for feature_id in FEATURE_IDS]


def predict_probabilities_from_model(model_payload: dict[str, object], rows: list[list[float]]) -> list[float]:
    intercept = float(model_payload.get("Intercept", 0.0))
    weights = get_model_weights_in_feature_order(model_payload)
    minimum_probability = float(model_payload.get("MinimumProbability", 0.0001))
    maximum_probability = float(model_payload.get("MaximumProbability", 0.9999))

    probabilities = []
    for row in rows:
        probability = predict_probability(intercept, weights, row)
        probabilities.append(clamp(probability, minimum_probability, maximum_probability))
    return probabilities


def average_ranks(values: list[float]) -> list[float]:
    indexed = sorted(enumerate(values), key=lambda item: item[1])
    ranks = [0.0] * len(values)
    start = 0
    while start < len(indexed):
        end = start
        while end + 1 < len(indexed) and indexed[end + 1][1] == indexed[start][1]:
            end += 1
        average_rank = (start + end + 2) / 2.0
        for rank_index in range(start, end + 1):
            ranks[indexed[rank_index][0]] = average_rank
        start = end + 1
    return ranks


def compute_pearson(xs: list[float], ys: list[float]) -> float:
    if len(xs) < 2 or len(xs) != len(ys):
        return float("nan")
    mean_x = sum(xs) / len(xs)
    mean_y = sum(ys) / len(ys)
    numerator = sum((x - mean_x) * (y - mean_y) for x, y in zip(xs, ys))
    denominator_x = math.sqrt(sum((x - mean_x) * (x - mean_x) for x in xs))
    denominator_y = math.sqrt(sum((y - mean_y) * (y - mean_y) for y in ys))
    if denominator_x == 0.0 or denominator_y == 0.0:
        return float("nan")
    return numerator / (denominator_x * denominator_y)


def compute_spearman(xs: list[float], ys: list[float]) -> float:
    return compute_pearson(average_ranks(xs), average_ranks(ys))


def compute_auc(probabilities: list[float], labels: list[int]) -> float:
    positive_count = sum(labels)
    negative_count = len(labels) - positive_count
    if positive_count == 0 or negative_count == 0:
        return float("nan")

    pairs = sorted(zip(probabilities, labels), key=lambda pair: pair[0])
    positive_rank_sum = 0.0
    index = 0
    while index < len(pairs):
        end_index = index
        while end_index + 1 < len(pairs) and pairs[end_index + 1][0] == pairs[index][0]:
            end_index += 1
        average_rank = (index + end_index + 2) / 2.0
        positive_rank_sum += average_rank * sum(label for _, label in pairs[index : end_index + 1])
        index = end_index + 1

    return (positive_rank_sum - positive_count * (positive_count + 1) / 2.0) / (positive_count * negative_count)


def format_probability_bin(bin_edges: tuple[float, ...], probability: float) -> str:
    for lower, upper in zip(bin_edges[:-1], bin_edges[1:]):
        if lower <= probability < upper:
            return f"{lower:.2f}+" if upper >= 1.0 else f"{lower:.2f}-{upper:.2f}"
    return "other"


def evaluate_probability_predictions(
    probabilities: list[float], labels: list[int], bin_edges: tuple[float, ...] = DEFAULT_BIN_EDGES
) -> dict[str, object]:
    if len(probabilities) != len(labels):
        raise ValueError("Probability and label counts do not match.")
    if not probabilities:
        return {
            "shots": 0,
            "pred_goals": 0.0,
            "actual_goals": 0,
            "mean_xg": 0.0,
            "goal_rate": 0.0,
            "brier": 0.0,
            "logloss": 0.0,
            "ece": 0.0,
            "auc": float("nan"),
            "calibration_intercept": 0.0,
            "calibration_slope": 1.0,
            "reliability": [],
        }

    shots = len(probabilities)
    predicted_goals = sum(probabilities)
    actual_goals = sum(labels)
    brier = compute_brier_score(probabilities, labels)
    logloss = compute_log_loss(probabilities, labels)
    calibration_intercept, calibration_slope = fit_platt_scaler(probabilities, labels)
    auc = compute_auc(probabilities, labels)

    bins: dict[str, dict[str, float]] = {}
    for probability, label in zip(probabilities, labels):
        bin_label = format_probability_bin(bin_edges, probability)
        if bin_label not in bins:
            bins[bin_label] = {"shots": 0.0, "pred_goals": 0.0, "actual_goals": 0.0}
        bins[bin_label]["shots"] += 1.0
        bins[bin_label]["pred_goals"] += probability
        bins[bin_label]["actual_goals"] += float(label)

    def sort_key(value: str) -> float:
        return float(value.split("-")[0].replace("+", ""))

    reliability = []
    expected_calibration_error = 0.0
    for bin_label in sorted(bins.keys(), key=sort_key):
        bin_metrics = bins[bin_label]
        bin_shots = int(bin_metrics["shots"])
        predicted_rate = bin_metrics["pred_goals"] / max(1, bin_shots)
        actual_rate = bin_metrics["actual_goals"] / max(1, bin_shots)
        expected_calibration_error += abs(predicted_rate - actual_rate) * bin_shots / shots
        reliability.append(
            {
                "bin": bin_label,
                "shots": bin_shots,
                "pred_goals": round(bin_metrics["pred_goals"], 2),
                "actual_goals": int(bin_metrics["actual_goals"]),
                "pred_rate": round(predicted_rate, 5),
                "actual_rate": round(actual_rate, 5),
                "delta_rate": round(actual_rate - predicted_rate, 5),
            }
        )

    return {
        "shots": shots,
        "pred_goals": round(predicted_goals, 2),
        "actual_goals": actual_goals,
        "mean_xg": round(predicted_goals / shots, 5),
        "goal_rate": round(actual_goals / shots, 5),
        "brier": round(brier, 5),
        "logloss": round(logloss, 5),
        "ece": round(expected_calibration_error, 5),
        "auc": round(auc, 5) if not math.isnan(auc) else auc,
        "calibration_intercept": round(calibration_intercept, 5),
        "calibration_slope": round(calibration_slope, 5),
        "reliability": reliability,
    }
