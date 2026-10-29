"""Phase two: deterministic local snapshot -> scores, CSV and distribution Markdown.

No GitHub client, credentials, network calls or model invocation in this phase.
"""

from __future__ import annotations

import argparse
import csv
from collections import Counter
from dataclasses import asdict
from pathlib import Path

from estate_actions.assessment import SIZES, Policy, measure, verify_packet
from estate_actions.data import Invalid, canonical, decode, integer, mapping, sequence, string
from estate_actions.heuristic import VERSION, classify


def present_row(raw: object, directory: Path) -> dict[str, object]:
    row = mapping(raw)
    result = {
        key: value for key, value in row.items() if key not in {"packet_file", "file_groups", "packet_key"}
    }
    if row.get("status") != "GATHERED":
        return result
    try:
        location = (directory / string(row["packet_file"])).resolve()
        if not location.is_relative_to(directory.resolve()):
            raise Invalid("Evidence packet must be contained in the snapshot directory")
        packet = verify_packet(decode(location.read_text(encoding="utf-8")))
        if row["packet_key"] != packet["evidence_key"]:
            raise Invalid("Snapshot and packet fingerprints differ")
        metrics = measure(packet)
        result.update(asdict(metrics))
        result["character_coverage"] = "partial" if metrics.missing_patches else "complete"
        result["status"] = "LOWER_BOUND" if metrics.missing_patches else "SCORED"
        result["base_sha"] = packet["base_sha"]
        result["head_sha"] = packet["head_sha"]
        result["evidence_key"] = packet["evidence_key"]
        result["heuristic_version"] = VERSION
        contributions: dict[str, dict[str, int]] = {}
        for raw_file in sequence(packet["files"]):
            item = mapping(raw_file)
            kind = classify(string(item["path"]))
            counts = contributions.setdefault(kind.name, {"files": 0, "lines": 0, "weight": kind.weight})
            counts["files"] += 1
            counts["lines"] += integer(item["additions"]) + integer(item["deletions"])
        result["file_types"] = canonical(contributions)
        policy = Policy.parse(packet["policy"])
        result["review_score"] = round(
            max(
                metrics.weighted_lines / (100 * policy.line_limits[2]),
                metrics.weighted_files / (100 * policy.file_limits[2]),
                metrics.weighted_characters / (100 * policy.character_limits[2]),
            ),
            6,
        )
        for field in ("weighted_lines", "weighted_files", "weighted_characters"):
            result[field] = integer(result[field]) / 100
    except (Invalid, OSError, ValueError, KeyError) as error:
        result["status"] = "UNASSESSED"
        result["error"] = str(error) if isinstance(error, Invalid) else type(error).__name__
    return result


def distribution(rows: list[dict[str, object]], coverage: list[object] | None = None) -> str:
    counts = Counter(str(row.get("size", "UNKNOWN")) for row in rows)
    statuses = Counter(str(row.get("status", "UNKNOWN")) for row in rows)
    lines = [
        "# Estate PR size distribution",
        "",
        f"PRs: {len(rows)}. Fully scored: {statuses['SCORED']}; "
        f"lower bounds: {statuses['LOWER_BOUND']}; unassessed: {statuses['UNASSESSED']}.",
        "",
        "| Size | PRs | Share |",
        "| --- | ---: | ---: |",
    ]
    failures = [mapping(row) for row in coverage or [] if mapping(row).get("status") != "DISCOVERED"]
    if failures:
        lines[2:2] = [
            f"**Incomplete repository discovery: {len(failures)} repositories are unassessed.**",
            "",
        ]
    for size in (*SIZES, "UNKNOWN"):
        share = counts[size] * 100 / len(rows) if rows else 0
        lines.append(f"| {size} | {counts[size]} | {share:.1f}% |")
    lines.extend(
        [
            "",
            "Sizes with incomplete character coverage are lower bounds.",
            "Stack membership is GitHub's current native membership at collection time; "
            "no branch-based inference or historical reconstruction is used.",
            "The heuristic measures review volume; it does not determine whether a PR should be split.",
        ]
    )
    return "\n".join(lines) + "\n"


def present(snapshot_file: Path, output: Path) -> list[dict[str, object]]:
    snapshot = mapping(decode(snapshot_file.read_text(encoding="utf-8")))
    if snapshot.get("schema_version") != 1:
        raise Invalid("Unsupported snapshot schema")
    rows = [present_row(raw, snapshot_file.parent) for raw in sequence(snapshot["pull_requests"])]
    rows.sort(key=lambda row: (string(row["repository"]).lower(), integer(row["number"])))
    output.mkdir(parents=True, exist_ok=True)
    fields = sorted({key for row in rows for key in row})
    with (output / "pr-scores.csv").open("w", encoding="utf-8", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=fields, lineterminator="\n")
        writer.writeheader()
        for row in rows:
            writer.writerow(
                {
                    key: canonical(value) if isinstance(value, (tuple, list)) else value
                    for key, value in row.items()
                }
            )
    (output / "distribution.md").write_text(
        distribution(rows, sequence(snapshot.get("coverage", []))), encoding="utf-8"
    )
    (output / "scores.json").write_text(canonical(rows) + "\n", encoding="utf-8")
    return rows


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("snapshot", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    rows = present(args.snapshot, args.output)
    print(f"Presented {len(rows)} PRs without network access")


if __name__ == "__main__":
    main()
