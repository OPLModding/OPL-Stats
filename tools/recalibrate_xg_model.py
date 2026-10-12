from __future__ import annotations

import argparse
import json
import math
from pathlib import Path

from xg_common import (
    DEFAULT_BIN_EDGES,
    FEATURE_IDS,
    build_shot_samples_for_games,
    clamp,
    compute_auc,
    compute_pearson,
    compute_spearman,
    evaluate_probability_predictions,
    get_model_weights_in_feature_order,
    list_game_sources,
    predict_probability,
    predict_probabilities_from_model,
)


DEFAULT_CALIBRATION_FEATURES = (
    "home_plate",
    "close_range",
    "point_range",
    "rebound_within_2s",
    "home_plate_rebound",
    "entry_rebound",
)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Recalibrate an existing xG model on a recent game window using a base-logit slope plus targeted feature adjustments."
    )
    parser.add_argument("--archive", required=True, help="Directory containing paired *_playbyplay.csv and *_stats.json files.")
    parser.add_argument("--model", required=True, help="Path to an existing xg_model.json.")
    parser.add_argument("--output", required=True, help="Path to write the recalibrated xg_model.json.")
    parser.add_argument("--report-output", help="Optional JSON path for the evaluation report.")
    parser.add_argument("--games", type=int, default=100, help="Number of most recent paired games to recalibrate against.")
    parser.add_argument(
        "--features",
        default=",".join(DEFAULT_CALIBRATION_FEATURES),
        help="Comma-separated existing xG feature ids to adjust during calibration.",
    )
    parser.add_argument("--epochs", type=int, default=400, help="Adam epochs.")
    parser.add_argument("--learning-rate", type=float, default=0.03, help="Adam learning rate.")
    parser.add_argument(
        "--l2",
        type=float,
        default=0.1,
        help="L2 regularization strength applied to the slope delta and feature adjustments.",
    )
    return parser.parse_args()


def parse_feature_ids(value: str) -> list[str]:
    feature_ids = [item.strip() for item in value.split(",") if item.strip()]
    invalid = [feature_id for feature_id in feature_ids if feature_id not in FEATURE_IDS]
    if invalid:
        raise SystemExit(f"Unknown feature ids: {', '.join(invalid)}")
    if not feature_ids:
        raise SystemExit("At least one calibration feature must be provided.")
    return feature_ids


def build_base_logits(model_payload: dict[str, object], rows: list[list[float]]) -> list[float]:
    intercept = float(model_payload.get("Intercept", 0.0))
    weights = get_model_weights_in_feature_order(model_payload)
    logits: list[float] = []
    for row in rows:
        linear_value = intercept
        for weight, feature_value in zip(weights, row):
            linear_value += weight * feature_value
        logits.append(clamp(linear_value, -60.0, 60.0))
    return logits


def compute_calibrated_probabilities(
    calibration_intercept: float,
    calibration_slope: float,
    feature_adjustments: list[float],
    base_logits: list[float],
    calibration_rows: list[list[float]],
) -> list[float]:
    probabilities: list[float] = []
    for base_logit, feature_row in zip(base_logits, calibration_rows):
        linear_value = calibration_intercept + calibration_slope * base_logit
        for adjustment, feature_value in zip(feature_adjustments, feature_row):
            linear_value += adjustment * feature_value
        probabilities.append(predict_probability(linear_value, [], []))
    return probabilities


def fit_structured_calibration(
    base_logits: list[float],
    calibration_rows: list[list[float]],
    labels: list[int],
    epochs: int,
    learning_rate: float,
    l2: float,
) -> tuple[float, float, list[float], list[dict[str, float]]]:
    calibration_intercept = 0.0
    calibration_slope = 1.0
    feature_adjustments = [0.0] * len(calibration_rows[0]) if calibration_rows else []

    m_intercept = 0.0
    v_intercept = 0.0
    m_slope = 0.0
    v_slope = 0.0
    m_adjustments = [0.0] * len(feature_adjustments)
    v_adjustments = [0.0] * len(feature_adjustments)
    beta1 = 0.9
    beta2 = 0.999
    epsilon = 1e-8

    history: list[dict[str, float]] = []
    sample_count = max(1, len(labels))

    for epoch in range(1, epochs + 1):
        probabilities = compute_calibrated_probabilities(
            calibration_intercept,
            calibration_slope,
            feature_adjustments,
            base_logits,
            calibration_rows,
        )

        grad_intercept = 0.0
        grad_slope = l2 * (calibration_slope - 1.0)
        grad_adjustments = [l2 * value for value in feature_adjustments]
        logloss = 0.0

        for probability, label, base_logit, feature_row in zip(probabilities, labels, base_logits, calibration_rows):
            clipped = clamp(probability, 1e-9, 1.0 - 1e-9)
            error = probability - label
            logloss += -(label * math.log(clipped) + (1 - label) * math.log(1.0 - clipped))
            grad_intercept += error
            grad_slope += error * base_logit
            for index, feature_value in enumerate(feature_row):
                grad_adjustments[index] += error * feature_value

        grad_intercept /= sample_count
        grad_slope /= sample_count
        grad_adjustments = [value / sample_count for value in grad_adjustments]
        logloss /= sample_count

        m_intercept = beta1 * m_intercept + (1.0 - beta1) * grad_intercept
        v_intercept = beta2 * v_intercept + (1.0 - beta2) * grad_intercept * grad_intercept
        m_intercept_hat = m_intercept / (1.0 - beta1**epoch)
        v_intercept_hat = v_intercept / (1.0 - beta2**epoch)
        calibration_intercept -= learning_rate * m_intercept_hat / (math.sqrt(v_intercept_hat) + epsilon)

        m_slope = beta1 * m_slope + (1.0 - beta1) * grad_slope
        v_slope = beta2 * v_slope + (1.0 - beta2) * grad_slope * grad_slope
        m_slope_hat = m_slope / (1.0 - beta1**epoch)
        v_slope_hat = v_slope / (1.0 - beta2**epoch)
        calibration_slope -= learning_rate * m_slope_hat / (math.sqrt(v_slope_hat) + epsilon)

        for index in range(len(feature_adjustments)):
            gradient = grad_adjustments[index]
            m_adjustments[index] = beta1 * m_adjustments[index] + (1.0 - beta1) * gradient
            v_adjustments[index] = beta2 * v_adjustments[index] + (1.0 - beta2) * gradient * gradient
            m_hat = m_adjustments[index] / (1.0 - beta1**epoch)
            v_hat = v_adjustments[index] / (1.0 - beta2**epoch)
            feature_adjustments[index] -= learning_rate * m_hat / (math.sqrt(v_hat) + epsilon)

        if epoch == 1 or epoch % 25 == 0 or epoch == epochs:
            history.append(
                {
                    "epoch": epoch,
                    "logloss": round(logloss, 6),
                    "slope": round(calibration_slope, 6),
                }
            )

    return calibration_intercept, calibration_slope, feature_adjustments, history


def build_recalibrated_model(
    base_model_payload: dict[str, object],
    selected_games,
    feature_ids: list[str],
    calibration_intercept: float,
    calibration_slope: float,
    feature_adjustments: list[float],
) -> dict[str, object]:
    base_intercept = float(base_model_payload.get("Intercept", 0.0))
    base_weights = get_model_weights_in_feature_order(base_model_payload)
    updated_weights = [calibration_slope * weight for weight in base_weights]

    for feature_id, adjustment in zip(feature_ids, feature_adjustments):
        feature_index = FEATURE_IDS.index(feature_id)
        updated_weights[feature_index] += adjustment

    model_payload = json.loads(json.dumps(base_model_payload))
    model_payload["ModelVersion"] = f"{base_model_payload.get('ModelVersion', 'xg_model')}_structured_cal_recent_window"
    model_payload["TrainedThrough"] = selected_games[-1].prefix if selected_games else base_model_payload.get("TrainedThrough", "")
    model_payload["Intercept"] = round(calibration_intercept + calibration_slope * base_intercept, 12)
    model_payload["Weights"] = {
        feature_id: round(weight, 12) for feature_id, weight in zip(FEATURE_IDS, updated_weights)
    }
    model_payload["CalibrationParameters"] = {
        "Mode": "structured_logit_feature_adjustment",
        "Intercept": round(calibration_intercept, 8),
        "Slope": round(calibration_slope, 8),
        "AdjustedFeatures": list(feature_ids),
    }
    model_payload["StructuredCalibration"] = {
        "BaseModelVersion": base_model_payload.get("ModelVersion", ""),
        "BaseTrainedThrough": base_model_payload.get("TrainedThrough", ""),
        "WindowGames": len(selected_games),
        "WindowRange": [selected_games[0].prefix, selected_games[-1].prefix] if selected_games else [],
        "FeatureAdjustments": {
            feature_id: round(adjustment, 8) for feature_id, adjustment in zip(feature_ids, feature_adjustments)
        },
    }
    return model_payload


def summarize_games(game_rows: list[dict[str, object]]) -> dict[str, object]:
    if not game_rows:
        return {"games": 0}

    game_count = len(game_rows)
    predicted_goals = sum(float(row["xg"]) for row in game_rows)
    actual_goals = sum(int(row["goals"]) for row in game_rows)
    residuals = [int(row["goals"]) - float(row["xg"]) for row in game_rows]
    xs = [float(row["xg"]) for row in game_rows]
    ys = [float(row["goals"]) for row in game_rows]
    denominator = sum(value * value for value in xs)
    slope0 = sum(x * y for x, y in zip(xs, ys)) / denominator if denominator > 0.0 else float("nan")

    return {
        "games": game_count,
        "pred": round(predicted_goals, 2),
        "actual": actual_goals,
        "ratio_actual_over_pred": round(actual_goals / predicted_goals, 4) if predicted_goals > 0.0 else None,
        "mae": round(sum(abs(value) for value in residuals) / game_count, 3),
        "rmse": round(math.sqrt(sum(value * value for value in residuals) / game_count), 3),
        "corr": round(compute_pearson(xs, ys), 4),
        "slope0_actual_on_xg": round(slope0, 4),
    }


def build_boolean_group_summary(
    samples,
    probabilities: list[float],
    labels: list[int],
    feature_id: str,
) -> dict[str, object]:
    shots = 0
    predicted_goals = 0.0
    actual_goals = 0
    for sample, probability, label in zip(samples, probabilities, labels):
        if sample.feature_map.get(feature_id, 0.0) <= 0.5:
            continue
        shots += 1
        predicted_goals += probability
        actual_goals += label

    return {
        "feature": feature_id,
        "shots": shots,
        "pred_goals": round(predicted_goals, 2),
        "actual_goals": actual_goals,
        "pred_rate": round(predicted_goals / max(1, shots), 5),
        "actual_rate": round(actual_goals / max(1, shots), 5),
        "actual_minus_pred": round(actual_goals - predicted_goals, 2),
    }


def evaluate_model_on_games(model_payload: dict[str, object], selected_games) -> dict[str, object]:
    samples, label_summary = build_shot_samples_for_games(selected_games)
    rows = [sample.feature_row for sample in samples]
    labels = [sample.resolved_label for sample in samples]
    probabilities = predict_probabilities_from_model(model_payload, rows)

    game_totals: dict[str, dict[str, object]] = {
        game.prefix: {"game": game.prefix, "xg": 0.0, "goals": 0, "shots": 0} for game in selected_games
    }
    for sample, probability, label in zip(samples, probabilities, labels):
        game_totals[sample.game_id]["xg"] = float(game_totals[sample.game_id]["xg"]) + probability
        game_totals[sample.game_id]["goals"] = int(game_totals[sample.game_id]["goals"]) + label
        game_totals[sample.game_id]["shots"] = int(game_totals[sample.game_id]["shots"]) + 1

    per_game = []
    for game in selected_games:
        game_entry = game_totals[game.prefix]
        per_game.append(
            {
                "game": game_entry["game"],
                "xg": round(float(game_entry["xg"]), 2),
                "goals": int(game_entry["goals"]),
                "shots": int(game_entry["shots"]),
                "delta_goals_minus_xg": round(int(game_entry["goals"]) - float(game_entry["xg"]), 2),
            }
        )

    return {
        "label_resolution_summary": label_summary.to_dict(),
        "shot_level": {
            "metrics": evaluate_probability_predictions(probabilities, labels, DEFAULT_BIN_EDGES),
            "correlations": {
                "pearson": round(compute_pearson(probabilities, [float(label) for label in labels]), 5),
                "spearman": round(compute_spearman(probabilities, [float(label) for label in labels]), 5),
                "auc": round(compute_auc(probabilities, labels), 5),
            },
        },
        "game_level": {
            "last_window": summarize_games(per_game),
        },
        "focus_groups": {
            "home_plate": build_boolean_group_summary(samples, probabilities, labels, "home_plate"),
            "close_range": build_boolean_group_summary(samples, probabilities, labels, "close_range"),
            "point_range": build_boolean_group_summary(samples, probabilities, labels, "point_range"),
            "rebound_within_2s": build_boolean_group_summary(samples, probabilities, labels, "rebound_within_2s"),
            "home_plate_rebound": build_boolean_group_summary(samples, probabilities, labels, "home_plate_rebound"),
            "entry_rebound": build_boolean_group_summary(samples, probabilities, labels, "entry_rebound"),
        },
        "largest_overperformers": sorted(per_game, key=lambda row: row["delta_goals_minus_xg"], reverse=True)[:8],
        "largest_underperformers": sorted(per_game, key=lambda row: row["delta_goals_minus_xg"])[:8],
        "latest_10_games": per_game[-10:],
    }


def main() -> None:
    args = parse_args()
    archive_dir = Path(args.archive)
    model_path = Path(args.model)
    output_path = Path(args.output)
    report_path = Path(args.report_output) if args.report_output else None
    feature_ids = parse_feature_ids(args.features)

    if not archive_dir.is_dir():
        raise SystemExit(f"Archive directory not found: {archive_dir}")
    if not model_path.is_file():
        raise SystemExit(f"Model file not found: {model_path}")

    base_model_payload = json.loads(model_path.read_text(encoding="utf-8"))
    all_games = list_game_sources(archive_dir)
    if not all_games:
        raise SystemExit(f"No paired *_playbyplay.csv / *_stats.json files found in: {archive_dir}")

    selected_games = all_games[-max(1, args.games) :]
    samples, _ = build_shot_samples_for_games(selected_games)
    rows = [sample.feature_row for sample in samples]
    labels = [sample.resolved_label for sample in samples]
    base_logits = build_base_logits(base_model_payload, rows)
    calibration_rows = [[sample.feature_map[feature_id] for feature_id in feature_ids] for sample in samples]

    calibration_intercept, calibration_slope, feature_adjustments, history = fit_structured_calibration(
        base_logits=base_logits,
        calibration_rows=calibration_rows,
        labels=labels,
        epochs=args.epochs,
        learning_rate=args.learning_rate,
        l2=args.l2,
    )

    recalibrated_model = build_recalibrated_model(
        base_model_payload=base_model_payload,
        selected_games=selected_games,
        feature_ids=feature_ids,
        calibration_intercept=calibration_intercept,
        calibration_slope=calibration_slope,
        feature_adjustments=feature_adjustments,
    )
    recalibrated_model["StructuredCalibration"]["History"] = history
    recalibrated_model["StructuredCalibration"]["Optimizer"] = {
        "Epochs": args.epochs,
        "LearningRate": args.learning_rate,
        "L2": args.l2,
    }

    report = {
        "archive": str(archive_dir),
        "base_model": str(model_path),
        "output_model": str(output_path),
        "selected_window": {
            "games": len(selected_games),
            "from": selected_games[0].prefix,
            "to": selected_games[-1].prefix,
        },
        "calibration_features": feature_ids,
        "base_evaluation": evaluate_model_on_games(base_model_payload, selected_games),
        "recalibrated_evaluation": evaluate_model_on_games(recalibrated_model, selected_games),
    }

    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(json.dumps(recalibrated_model, indent=2), encoding="utf-8")

    if report_path is not None:
        report_path.parent.mkdir(parents=True, exist_ok=True)
        report_path.write_text(json.dumps(report, indent=2), encoding="utf-8")

    base_game_summary = report["base_evaluation"]["game_level"]["last_window"]
    recalibrated_game_summary = report["recalibrated_evaluation"]["game_level"]["last_window"]
    print(
        f"Recalibrated {base_model_payload.get('ModelVersion', 'xg_model')} on {len(selected_games)} games "
        f"({selected_games[0].prefix} -> {selected_games[-1].prefix}). "
        f"Game totals: {base_game_summary['pred']} xG vs {base_game_summary['actual']} goals -> "
        f"{recalibrated_game_summary['pred']} xG vs {recalibrated_game_summary['actual']} goals. "
        f"Wrote {output_path}"
    )


if __name__ == "__main__":
    main()
