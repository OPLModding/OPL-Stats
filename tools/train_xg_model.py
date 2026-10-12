from __future__ import annotations

import argparse
import json
from pathlib import Path

from xg_common import (
    FEATURE_IDS,
    INCLUDED_SHOT_OUTCOMES,
    apply_intercept_only_calibration_to_raw_coefficients,
    apply_platt_to_raw_coefficients,
    apply_standardization,
    build_shot_samples_for_games,
    convert_standardized_weights_to_raw,
    evaluate_probability_predictions,
    fit_intercept_only_calibration,
    fit_platt_scaler,
    list_game_sources,
    predict_probabilities_from_model,
    split_game_sources,
    standardize_rows,
    train_logistic_regression,
)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Train a resolved-label expected-goals model JSON.")
    parser.add_argument("--archive", required=True, help="Directory containing paired *_playbyplay.csv and *_stats.json files.")
    parser.add_argument("--output", required=True, help="Path to write xg_model.json.")
    parser.add_argument("--training-mode", choices=("recent_window", "chronological_split"), default="recent_window")
    parser.add_argument(
        "--recent-fit-games",
        type=int,
        default=90,
        help="For recent_window mode, number of most recent games used to fit the base logistic model.",
    )
    parser.add_argument(
        "--recent-calibration-games",
        type=int,
        default=20,
        help="For recent_window mode, number of most recent games reserved for calibration and early stopping. Set to 0 to fit across the full selected recent window.",
    )
    parser.add_argument("--epochs", type=int, default=72, help="Training epochs for Adam SGD.")
    parser.add_argument("--learning-rate", type=float, default=0.01, help="Adam learning rate.")
    parser.add_argument("--l2", type=float, default=0.001, help="L2 regularization strength.")
    parser.add_argument("--seed", type=int, default=42, help="Random seed.")
    parser.add_argument("--validation-fraction", type=float, default=0.15, help="Chronological validation fraction by game.")
    parser.add_argument("--test-fraction", type=float, default=0.15, help="Chronological test fraction by game.")
    parser.add_argument(
        "--team-game-penalty",
        type=float,
        default=0.0,
        help="Additional training penalty applied when summed shot xG for a team-game drifts away from its real goal total.",
    )
    parser.add_argument(
        "--calibration-mode",
        choices=("none", "intercept_only", "platt"),
        default="intercept_only",
        help="How to recalibrate the fitted base model on the most recent calibration games.",
    )
    parser.add_argument(
        "--recent-calibration-half-life-games",
        type=float,
        default=8.0,
        help="Exponential half-life, in games, used to weight newer calibration games more heavily. Set to 0 to disable recency weighting.",
    )
    parser.add_argument("--minimum-probability", type=float, default=0.0001, help="Minimum clamped runtime probability.")
    parser.add_argument("--maximum-probability", type=float, default=0.9999, help="Maximum clamped runtime probability.")
    return parser.parse_args()


def select_recent_window_games(game_sources, recent_fit_games: int, recent_calibration_games: int):
    total_games = len(game_sources)
    if total_games == 0:
        return [], [], []
    if total_games == 1:
        return list(game_sources), [], []

    if recent_calibration_games <= 0:
        fit_count = max(1, recent_fit_games)
        fit_count = min(fit_count, total_games)
        return list(game_sources[-fit_count:]), [], []

    calibration_count = max(1, recent_calibration_games)
    calibration_count = min(calibration_count, total_games - 1)

    available_fit_games = total_games - calibration_count
    fit_count = max(1, recent_fit_games)
    fit_count = min(fit_count, available_fit_games)

    selected_window = list(game_sources[-(fit_count + calibration_count) :])
    train_games = selected_window[:fit_count]
    calibration_games = selected_window[fit_count:]
    return train_games, calibration_games, []


def select_game_splits(args: argparse.Namespace, game_sources):
    if args.training_mode == "recent_window":
        return select_recent_window_games(game_sources, args.recent_fit_games, args.recent_calibration_games)
    return split_game_sources(
        game_sources,
        validation_fraction=args.validation_fraction,
        test_fraction=args.test_fraction,
    )


def build_recent_game_sample_weights(samples, games, half_life_games: float) -> list[float]:
    if not samples:
        return []
    if half_life_games <= 0.0 or not games:
        return [1.0] * len(samples)

    game_index_by_prefix = {game.prefix: index for index, game in enumerate(games)}
    newest_index = len(games) - 1
    weights: list[float] = []
    for sample in samples:
        game_index = game_index_by_prefix.get(sample.game_id, newest_index)
        game_age = max(0.0, float(newest_index - game_index))
        weights.append(0.5 ** (game_age / half_life_games))
    return weights


def build_team_game_group_ids(samples) -> list[str]:
    return [f"{sample.game_id}:{sample.team}" for sample in samples]


def build_model_metadata(
    args: argparse.Namespace,
    game_sources,
    train_games,
    validation_games,
    test_games,
    train_label_summary,
    validation_label_summary,
    test_label_summary,
    training_metadata,
    calibration_intercept: float,
    calibration_slope: float,
    calibrated_intercept_raw: float,
    calibrated_weights_raw,
):
    is_recent_window = args.training_mode == "recent_window"
    model_version = (
        "xg_logistic_v8_recent_window_shot_danger_composite"
        if is_recent_window
        else "xg_logistic_v8_resolved_shot_danger_composite"
    )
    training_context: dict[str, object] = {
        "TrainingMode": args.training_mode,
        "TrainGames": len(train_games),
        "ValidationGames": len(validation_games),
        "TrainGameRange": [train_games[0].prefix, train_games[-1].prefix] if train_games else [],
        "ValidationGameRange": [validation_games[0].prefix, validation_games[-1].prefix] if validation_games else [],
        "RecentFitGames": args.recent_fit_games,
        "RecentCalibrationGames": args.recent_calibration_games,
        "CalibrationMode": args.calibration_mode,
        "CalibrationHalfLifeGames": args.recent_calibration_half_life_games,
        "TeamGamePenalty": args.team_game_penalty,
        "TrainingObjective": (
            "shot_logloss_plus_team_game_penalty" if args.team_game_penalty > 0.0 else "shot_logloss"
        ),
    }
    if is_recent_window and (train_games or validation_games):
        selected_games = train_games + validation_games
        training_context["SelectedWindowGameRange"] = [selected_games[0].prefix, selected_games[-1].prefix]

    return {
        "LogInfo": True,
        "Enabled": True,
        "ModelVersion": model_version,
        "TrainedThrough": game_sources[-1].prefix if game_sources else "",
        "ModelType": "logistic",
        "Intercept": round(calibrated_intercept_raw, 12),
        "MinimumProbability": args.minimum_probability,
        "MaximumProbability": args.maximum_probability,
        "IncludedShotOutcomes": list(INCLUDED_SHOT_OUTCOMES),
        "SupportedFeatures": list(FEATURE_IDS),
        "Weights": {feature_id: round(weight, 12) for feature_id, weight in zip(FEATURE_IDS, calibrated_weights_raw)},
        "CalibrationParameters": {
            "Mode": args.calibration_mode,
            "Intercept": round(calibration_intercept, 8),
            "Slope": round(calibration_slope, 8),
            "HalfLifeGames": round(args.recent_calibration_half_life_games, 4),
        },
        "TrainingContext": training_context,
    }


def main() -> None:
    args = parse_args()
    archive_dir = Path(args.archive)
    output_path = Path(args.output)

    if not archive_dir.is_dir():
        raise SystemExit(f"Archive directory not found: {archive_dir}")

    game_sources = list_game_sources(archive_dir)
    if not game_sources:
        raise SystemExit(f"No paired *_playbyplay.csv / *_stats.json files found in: {archive_dir}")

    train_games, validation_games, test_games = select_game_splits(args, game_sources)

    train_samples, train_label_summary = build_shot_samples_for_games(train_games)
    validation_samples, validation_label_summary = build_shot_samples_for_games(validation_games)
    test_samples, test_label_summary = build_shot_samples_for_games(test_games)

    if not train_samples:
        raise SystemExit("No resolved-label training shots were found in the archive.")

    train_rows = [sample.feature_row for sample in train_samples]
    train_labels = [sample.resolved_label for sample in train_samples]
    validation_rows = [sample.feature_row for sample in validation_samples]
    validation_labels = [sample.resolved_label for sample in validation_samples]
    test_rows = [sample.feature_row for sample in test_samples]
    test_labels = [sample.resolved_label for sample in test_samples]

    standardized_train_rows, means, std_devs = standardize_rows(train_rows)
    standardized_validation_rows = apply_standardization(validation_rows, means, std_devs) if validation_rows else []

    intercept_std, weights_std, training_metadata = train_logistic_regression(
        standardized_train_rows,
        train_labels,
        epochs=args.epochs,
        learning_rate=args.learning_rate,
        l2=args.l2,
        seed=args.seed,
        validation_rows=standardized_validation_rows,
        validation_labels=validation_labels,
        group_ids=build_team_game_group_ids(train_samples),
        validation_group_ids=build_team_game_group_ids(validation_samples),
        team_game_penalty=args.team_game_penalty,
    )

    base_intercept_raw, base_weights_raw = convert_standardized_weights_to_raw(intercept_std, weights_std, means, std_devs)

    base_validation_model = {
        "Intercept": base_intercept_raw,
        "Weights": {feature_id: weight for feature_id, weight in zip(FEATURE_IDS, base_weights_raw)},
        "MinimumProbability": args.minimum_probability,
        "MaximumProbability": args.maximum_probability,
    }
    calibration_rows = validation_rows if validation_rows else train_rows
    calibration_labels = validation_labels if validation_labels else train_labels
    calibration_samples = validation_samples if validation_samples else train_samples
    calibration_games = validation_games if validation_games else train_games
    calibration_sample_weights = build_recent_game_sample_weights(
        calibration_samples,
        calibration_games,
        args.recent_calibration_half_life_games,
    )
    calibration_probabilities = predict_probabilities_from_model(base_validation_model, calibration_rows)

    if args.calibration_mode == "none":
        calibration_intercept = 0.0
        calibration_slope = 1.0
        calibrated_intercept_raw = base_intercept_raw
        calibrated_weights_raw = list(base_weights_raw)
    elif args.calibration_mode == "platt":
        calibration_intercept, calibration_slope = fit_platt_scaler(
            calibration_probabilities,
            calibration_labels,
            sample_weights=calibration_sample_weights,
        )
        calibrated_intercept_raw, calibrated_weights_raw = apply_platt_to_raw_coefficients(
            base_intercept_raw,
            base_weights_raw,
            calibration_intercept,
            calibration_slope,
        )
    else:
        calibration_intercept = fit_intercept_only_calibration(
            calibration_probabilities,
            calibration_labels,
            sample_weights=calibration_sample_weights,
        )
        calibration_slope = 1.0
        calibrated_intercept_raw, calibrated_weights_raw = apply_intercept_only_calibration_to_raw_coefficients(
            base_intercept_raw,
            base_weights_raw,
            calibration_intercept,
        )

    model_payload = build_model_metadata(
        args=args,
        game_sources=game_sources,
        train_games=train_games,
        validation_games=validation_games,
        test_games=test_games,
        train_label_summary=train_label_summary,
        validation_label_summary=validation_label_summary,
        test_label_summary=test_label_summary,
        training_metadata=training_metadata,
        calibration_intercept=calibration_intercept,
        calibration_slope=calibration_slope,
        calibrated_intercept_raw=calibrated_intercept_raw,
        calibrated_weights_raw=calibrated_weights_raw,
    )

    final_model = {
        "Intercept": model_payload["Intercept"],
        "Weights": model_payload["Weights"],
        "MinimumProbability": model_payload["MinimumProbability"],
        "MaximumProbability": model_payload["MaximumProbability"],
    }

    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(json.dumps(model_payload, indent=2), encoding="utf-8")

    print(
        f"Trained resolved-label xG model from {len(game_sources)} games "
        f"using mode={args.training_mode}. "
        f"Train shots={len(train_samples)}, validation shots={len(validation_samples)}, test shots={len(test_samples)}. "
        f"Wrote {output_path}"
    )


if __name__ == "__main__":
    main()
