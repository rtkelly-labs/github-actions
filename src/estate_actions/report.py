"""Deterministic Markdown and reusable, author-scoped sticky comment sessions."""

from __future__ import annotations

import hashlib
import html
import re
from dataclasses import dataclass

from estate_actions.assessment import Judgment, Metrics
from estate_actions.data import Invalid, integer, mapping, sequence, string
from estate_actions.github import GitHub
from estate_actions.heuristic import VERSION


def safe(value: str) -> str:
    return html.escape(value).replace("@", "@\u200b").replace("`", "&#96;")


@dataclass(frozen=True)
class Report:
    markdown: str
    error_code: int
    size: str = "UNKNOWN"
    decision: str = "NEEDS_REVIEW"


def render(packet: dict[str, object], metrics: Metrics, judgment: Judgment | None, issue: str = "") -> Report:
    decision = judgment.decision if judgment else "NEEDS_REVIEW"
    code = 0 if decision == "KEEP_TOGETHER" else 2 if decision == "NEEDS_REVIEW" else 1
    if metrics.missing_patches:
        code = 2
        decision = "NEEDS_REVIEW"
        issue = "Character coverage is incomplete; the displayed size is a lower bound. " + issue
    lower = "at least " if metrics.missing_patches else ""
    rows = [
        "## PR scope assessment",
        "",
        f"**Size: {lower}{metrics.size}** · **Decision: {decision}** · **Error code: {code}**",
        "",
        "| Dimension | Raw included count | Weighted count | Size band |",
        "| --- | ---: | ---: | --- |",
        f"| Changed lines | {metrics.lines} | {metrics.weighted_lines / 100:g} | {metrics.line_size} |",
        f"| Changed files | {metrics.files} | {metrics.weighted_files / 100:g} | {metrics.file_size} |",
        f"| Changed characters | {metrics.characters} | {metrics.weighted_characters / 100:g} "
        f"| {metrics.character_size} |",
        "",
        f"Total before exclusions: {metrics.total_lines} changed lines across {metrics.total_files} files.",
        f"Sizing rule: largest dimension band wins; heuristic `{VERSION}`. "
        "Size is review volume, not correctness.",
        "",
    ]
    if issue:
        rows.extend([f"**Assessment limitation:** {safe(issue)}", ""])
    if judgment:
        rows.extend(
            [
                f"**BYOA agent:** {safe(judgment.agent)}",
                "",
                safe(judgment.rationale),
                "",
                f"**gh stack:** {judgment.stacking} — {safe(judgment.stacking_reason)}",
                "",
            ]
        )
        for index, layer in enumerate(judgment.layers, 1):
            paths = ", ".join(safe(string(path)) for path in sequence(layer["paths"]))
            rows.extend(
                [
                    f"{index}. **{safe(string(layer['name']))}**: {paths}",
                    f"   Validation: {safe(string(layer['validation']))}",
                ]
            )
    else:
        rows.extend(["No valid agent judgment supplied. No split decision has been inferred.", ""])
    if metrics.excluded_paths:
        rows.extend(
            ["**Excluded from sizing:** " + ", ".join(safe(path) for path in metrics.excluded_paths), ""]
        )
    if metrics.missing_patches:
        rows.extend(
            ["**Incomplete patches:** " + ", ".join(safe(path) for path in metrics.missing_patches), ""]
        )
    rows.extend(
        [
            f"Base: `{safe(string(packet['base_sha']))}` · Head: `{safe(string(packet['head_sha']))}`",
            f"Evidence key: `{safe(string(packet['evidence_key']))}`",
            "",
            "Advisory mode: error code is an output; assessment outcomes do not fail the workflow.",
        ]
    )
    return Report("\n".join(rows) + "\n", code, metrics.size, decision)


def session_body(session: str, markdown: str) -> str:
    if not session.strip():
        raise Invalid("Sticky session must not be empty")
    marker = hashlib.sha256(session.encode("utf-8")).hexdigest()
    prefix = f"<!-- estate-actions:session:{marker} -->\n"
    body = markdown if markdown.startswith(prefix) else prefix + markdown
    if len(body.encode("utf-8")) > 60_000:
        raise Invalid("Sticky report exceeds 60,000 UTF-8 bytes")
    return body


def publish(github: GitHub, number: int, body: str, author: str) -> int:
    if not re.match(r"\A<!-- estate-actions:session:[0-9a-f]{64} -->\n", body):
        raise Invalid("A valid sticky session marker is required before publication")
    marker = body.split("\n", 1)[0] + "\n"
    matches = [
        comment
        for comment in github.pages(f"issues/{number}/comments")
        if mapping(comment.get("user")).get("login") == author
        and string(comment.get("body") or "").startswith(marker)
    ]
    if len(matches) > 1:
        raise Invalid("Duplicate sticky session comments found; resolve duplicates before retrying")
    if matches:
        comment = matches[0]
        number_id = integer(comment["id"])
        if comment.get("body") != body:
            github.request("PATCH", f"issues/comments/{number_id}", {"body": body})
        return number_id
    response = mapping(github.request("POST", f"issues/{number}/comments", {"body": body}))
    return integer(response["id"])
