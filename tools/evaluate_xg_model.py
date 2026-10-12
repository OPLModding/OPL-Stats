from __future__ import annotations

import argparse
import json
from pathlib import Path

from xg_common import (
    DEFAULT_BIN_EDGES,
    build_shot_samples_for_games,
    compute_auc,
    compute_pearson,
    compute_spearman,
    evaluate_probability_predictions,
    list_game_sources,
    predict_probabilities_from_model,
    split_game_sources,
)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Evaluate an xG model against resolved or raw archive shot labels.")
    parser.add_argument("--archive", required=True, help="Directory containing paired *_playbyplay.csv and *_stats.json files.")
    parser.add_argument("--model", required=True, help="Path to xg_model.json.")
    parser.add_argument("--split", choices=("all", "train", "validation", "test"), default="all", help="Chronological game split to evaluate.")
    parser.add_argument("--label-mode", choices=("resolved", "raw"), default="resolved", help="Which shot label set to score against.")
    parser.add_argument("--validation-fraction", type=float, default=0.15, help="Chronological validation fraction by game.")
    parser.add_argument("--test-fraction", type=float, default=0.15, help="Chronological test fraction by game.")
    parser.add_argument("--output", help="Optional JSON output path.")
    return parser.parse_args()


def summarize_groups(samples, probabilities, labels):
    group_specs = {
        "zone": lambda sample: sample.zone or "unknown",
        "home_plate": lambda sample: "HomePlate" if sample.home_plate else "Outside",
        "position": lambda sample: sample.player_position.upper() or "unknown",
        "rebound_within_2s": lambda sample: "Yes" if sample.rebound_within_2s else "No",
        "entry_within_6s": lambda sample: "Yes" if sample.entry_within_6s else "No",
    }
    summaries: dict[str, list[dict[str, object]]] = {}
    for group_name, key_fn in group_specs.items():
        buckets: dict[str, dict[str, float]] = {}
        for sample, probability, label in zip(samples, probabilities, labels):
            key = key_fn(sample)
            if key not in buckets:
                buckets[key] = {"shots": 0.0, "pred_goals": 0.0, "actual_goals": 0.0}
            buckets[key]["shots"] += 1.0
            buckets[key]["pred_goals"] += probability
            buckets[key]["actual_goals"] += float(label)
        group_rows = []
        for key, metrics in buckets.items():
            shots = int(metrics["shots"])
            pred_goals = metrics["pred_goals"]
            actual_goals = int(metrics["actual_goals"])
            group_rows.append(
                {
                    "group": key,
                    "shots": shots,
                    "pred_goals": round(pred_goals, 2),
                    "actual_goals": actual_goals,
                    "pred_rate": round(pred_goals / max(1, shots), 5),
                    "actual_rate": round(actual_goals / max(1, shots), 5),
                    "actual_minus_pred": round(actual_goals - pred_goals, 2),
                }
            )
        group_rows.sort(key=lambda row: row["shots"], reverse=True)
        summaries[group_name] = group_rows
    return summaries


def main() -> None:
    args = parse_args()
    archive_dir = Path(args.archive)
    model_path = Path(args.model)

    if not archive_dir.is_dir():
        raise SystemExit(f"Archive directory not found: {archive_dir}")
    if not model_path.is_file():
        raise SystemExit(f"Model file not found: {model_path}")

    model_payload = json.loads(model_path.read_text(encoding="utf-8"))
    game_sources = list_game_sources(archive_dir)
    train_games, validation_games, test_games = split_game_sources(
        game_sources,
        validation_fraction=args.validation_fraction,
        test_fraction=args.test_fraction,
    )

    selected_games = {
        "all": game_sources,
        "train": train_games,
        "validation": validation_games,
        "test": test_games,
    }[args.split]

    samples, label_summary = build_shot_samples_for_games(selected_games)
    rows = [sample.feature_row for sample in samples]
    probabilities = predict_probabilities_from_model(model_payload, rows)
    labels = [sample.resolved_label if args.label_mode == "resolved" else sample.raw_label for sample in samples]

    metrics = evaluate_probability_predictions(probabilities, labels, DEFAULT_BIN_EDGES)
    correlations = {
        "pearson": round(compute_pearson(probabilities, [float(label) for label in labels]), 5),
        "spearman": round(compute_spearman(probabilities, [float(label) for label in labels]), 5),
        "auc": round(compute_auc(probabilities, labels), 5),
    }

    result = {
        "archive": str(archive_dir),
        "model": str(model_path),
        "split": args.split,
        "label_mode": args.label_mode,
        "game_count": len(selected_games),
        "shot_count": len(samples),
        "label_resolution_summary": label_summary.to_dict(),
        "metrics": metrics,
        "correlations": correlations,
        "group_summaries": summarize_groups(samples, probabilities, labels),
    }

    output_text = json.dumps(result, indent=2)
    if args.output:
        Path(args.output).write_text(output_text, encoding="utf-8")
    print(output_text)


if __name__ == "__main__":
    main()
