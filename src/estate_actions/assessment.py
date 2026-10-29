"""Deterministic evidence packets, T-shirt sizes, and BYOA contract validation."""

from __future__ import annotations

import fnmatch
import hashlib
import re
from dataclasses import asdict, dataclass

from estate_actions.data import Invalid, canonical, decode, integer, mapping, sequence, string
from estate_actions.github import GitHub
from estate_actions.heuristic import VERSION, classify

POLICY_VERSION = "1"
SIZES = ("XS", "S", "M", "L", "XL", "XXL")
DECISIONS = ("KEEP_TOGETHER", "SPLIT_INDEPENDENTLY", "SPLIT_WITH_GH_STACK", "NEEDS_REVIEW")


@dataclass(frozen=True)
class Policy:
    """Independent dimensions; the largest bucket wins. Limits are inclusive."""

    line_limits: tuple[int, ...] = (9, 29, 99, 499, 999)
    file_limits: tuple[int, ...] = (1, 3, 7, 15, 30)
    character_limits: tuple[int, ...] = (499, 1499, 4999, 24999, 49999)
    excluded: tuple[str, ...] = ()
    components: tuple[str, ...] = ()
    context: str = ""

    def __post_init__(self) -> None:
        for limits in (self.line_limits, self.file_limits, self.character_limits):
            if len(limits) != 5 or any(type(item) is not int or item < 1 for item in limits):
                raise Invalid("Sizing requires five positive integer limits")
            if tuple(sorted(set(limits))) != limits:
                raise Invalid("Sizing limits must be strictly increasing")

    @classmethod
    def parse(cls, raw: object) -> Policy:
        obj = mapping(raw)
        defaults = cls()
        unknown = set(obj) - {
            "line_limits",
            "file_limits",
            "character_limits",
            "excluded",
            "components",
            "context",
        }
        if unknown:
            raise Invalid(f"Unknown policy fields: {', '.join(sorted(unknown))}")
        return cls(
            line_limits=tuple(
                integer(item) for item in sequence(obj.get("line_limits", list(defaults.line_limits)))
            ),
            file_limits=tuple(
                integer(item) for item in sequence(obj.get("file_limits", list(defaults.file_limits)))
            ),
            character_limits=tuple(
                integer(item)
                for item in sequence(obj.get("character_limits", list(defaults.character_limits)))
            ),
            excluded=tuple(string(item) for item in sequence(obj.get("excluded", []))),
            components=tuple(string(item) for item in sequence(obj.get("components", []))),
            context=string(obj.get("context", "")),
        )


def fingerprint(value: object) -> str:
    return hashlib.sha256(canonical(value).encode("utf-8")).hexdigest()


def collect(github: GitHub, number: int, policy: Policy) -> dict[str, object]:
    before = github.pull(number)
    files = github.pages(f"pulls/{number}/files")
    after = github.pull(number)
    # Files REST is capped at 3,000. Compare the count instead of silently assessing a prefix.
    if len({string(item.get("filename")) for item in files}) != integer(after.get("changed_files")):
        raise Invalid("Changed-file enumeration is incomplete")
    for field in ("base", "head"):
        if mapping(before.get(field)).get("sha") != mapping(after.get(field)).get("sha"):
            raise Invalid("PR changed during evidence collection; retry")
    return make_packet(github.repository, number, after, files, policy)


def make_packet(
    repository: str,
    number: int,
    after: dict[str, object],
    files: list[dict[str, object]],
    policy: Policy,
) -> dict[str, object]:
    if len({string(item.get("filename")) for item in files}) != integer(after.get("changed_files")):
        raise Invalid("Changed-file enumeration is incomplete")
    normalized: dict[str, list[dict[str, object]]] = {}
    for item in files:
        normalized.setdefault(string(item["filename"]), []).append(item)
    combined: list[dict[str, object]] = []
    for path, entries in sorted(normalized.items()):
        entries.sort(key=canonical)
        patches = [item.get("patch") for item in entries]
        combined.append(
            {
                "path": path,
                "previous_path": entries[0].get("previous_filename"),
                "status": string(entries[0].get("status")) if len(entries) == 1 else "replaced",
                "additions": sum(integer(item.get("additions")) for item in entries),
                "deletions": sum(integer(item.get("deletions")) for item in entries),
                "patch": "\n".join(string(patch) for patch in patches)
                if all(isinstance(patch, str) for patch in patches)
                else None,
                "sha": string(entries[0].get("sha")),
                "api_entries": len(entries),
            }
        )
    evidence = {
        "schema_version": 1,
        "policy_version": POLICY_VERSION,
        "heuristic_version": VERSION,
        "repository": repository,
        "number": number,
        "base_sha": string(mapping(after.get("base")).get("sha")),
        "head_sha": string(mapping(after.get("head")).get("sha")),
        "base_ref": string(mapping(after.get("base")).get("ref")),
        "head_ref": string(mapping(after.get("head")).get("ref")),
        "title": string(after.get("title")),
        "body": after.get("body") or "",
        "policy": mapping(decode(canonical(asdict(policy)))),
        "files": combined,
        "file_groups": {
            kind: [item["path"] for item in combined if classify(string(item["path"])).name == kind]
            for kind in sorted({classify(string(item["path"])).name for item in combined})
        },
    }
    return {**evidence, "evidence_key": fingerprint(evidence)}


def verify_packet(raw: object) -> dict[str, object]:
    packet = mapping(raw)
    content = {key: value for key, value in packet.items() if key != "evidence_key"}
    if packet.get("evidence_key") != fingerprint(content):
        raise Invalid("Evidence packet fingerprint mismatch")
    if packet.get("policy_version") != POLICY_VERSION:
        raise Invalid("Unsupported evidence policy version")
    return packet


@dataclass(frozen=True)
class Metrics:
    size: str
    lines: int
    files: int
    total_lines: int
    total_files: int
    characters: int
    line_size: str
    file_size: str
    character_size: str
    weighted_lines: int
    weighted_files: int
    weighted_characters: int
    excluded_paths: tuple[str, ...]
    missing_patches: tuple[str, ...]


def patch_characters(patch: object, additions: int, deletions: int) -> int | None:
    """Count Unicode code points in +/- hunk content, excluding markers and line endings.

    Verify hunk lengths AND aggregate counts: omitted/truncated patches are unknown.
    Pure renames and zero-line changes need no textual patch.
    """
    if additions + deletions == 0:
        return 0
    if not isinstance(patch, str):
        return None
    added = removed = characters = 0
    old_left = new_left = 0
    in_hunk = False
    for line in patch.split("\n"):
        header = re.fullmatch(r"@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@.*", line)
        if header:
            if old_left or new_left:
                return None
            old_left = int(header.group(2) or "1")
            new_left = int(header.group(4) or "1")
            in_hunk = True
        elif in_hunk and line.startswith("+"):
            added += 1
            new_left -= 1
            characters += len(line[1:].removesuffix("\r"))
        elif in_hunk and line.startswith("-"):
            removed += 1
            old_left -= 1
            characters += len(line[1:].removesuffix("\r"))
        elif in_hunk and line.startswith(" "):
            old_left -= 1
            new_left -= 1
        elif line and not line.startswith("\\ No newline at end of file"):
            return None
        if old_left < 0 or new_left < 0:
            return None
    if old_left or new_left or added != additions or removed != deletions:
        return None
    return characters


def bucket(value: int, limits: tuple[int, ...]) -> str:
    return next((SIZES[index] for index, limit in enumerate(limits) if value <= limit), "XXL")


@dataclass(frozen=True)
class Change:
    """Observed file measurements, reusable by real-data CSV presentation fixtures."""

    path: str
    additions: int
    deletions: int
    characters: int | None


def measure(packet: dict[str, object]) -> Metrics:
    changes: list[Change] = []
    for raw in sequence(packet["files"]):
        item = mapping(raw)
        additions, deletions = integer(item["additions"]), integer(item["deletions"])
        changes.append(
            Change(
                string(item["path"]),
                additions,
                deletions,
                patch_characters(item.get("patch"), additions, deletions),
            )
        )
    return score_files(changes, Policy.parse(packet["policy"]))


def score_files(files: list[Change], policy: Policy) -> Metrics:
    total_lines = 0
    lines = 0
    count = 0
    characters = 0
    weighted_lines = weighted_files = weighted_characters = 0
    excluded: list[str] = []
    missing: list[str] = []
    if len({item.path for item in files}) != len(files):
        raise Invalid("Duplicate changed paths are invalid")
    for item in sorted(files, key=lambda item: item.path):
        path, additions, deletions = item.path, item.additions, item.deletions
        if min(additions, deletions) < 0 or (item.characters is not None and item.characters < 0):
            raise Invalid("Negative measurement counts are invalid")
        changed = additions + deletions
        total_lines += changed
        file_type = classify(path)
        if file_type.weight == 0 or any(fnmatch.fnmatchcase(path, pattern) for pattern in policy.excluded):
            excluded.append(path)
        else:
            # Pure renames count as files, but contribute zero changed lines.
            lines += changed
            count += 1
            weighted_lines += changed * file_type.weight
            weighted_files += file_type.weight
            chars = item.characters
            if chars is None:
                missing.append(path)
            else:
                characters += chars
                weighted_characters += chars * file_type.weight
    line_size = bucket(weighted_lines, tuple(limit * 100 for limit in policy.line_limits))
    file_size = bucket(weighted_files, tuple(limit * 100 for limit in policy.file_limits))
    character_size = bucket(weighted_characters, tuple(limit * 100 for limit in policy.character_limits))
    size = max((line_size, file_size, character_size), key=SIZES.index)
    return Metrics(
        size,
        lines,
        count,
        total_lines,
        len(files),
        characters,
        line_size,
        file_size,
        character_size,
        weighted_lines,
        weighted_files,
        weighted_characters,
        tuple(excluded),
        tuple(missing),
    )


@dataclass(frozen=True)
class Judgment:
    decision: str
    agent: str
    rationale: str
    stacking: str
    stacking_reason: str
    layers: tuple[dict[str, object], ...]


def nonempty(value: object) -> str:
    text = string(value)
    if not text.strip() or len(text) > 6000:
        raise Invalid("Judgment text must contain 1–6,000 characters")
    return text


def judge(raw: object, packet: dict[str, object]) -> Judgment:
    """Validate externally supplied judgment. Never synthesize or infer a decision."""
    obj = mapping(raw)
    if obj.get("schema_version") != 1 or obj.get("evidence_key") != packet["evidence_key"]:
        raise Invalid("Agent judgment is stale or has an unsupported schema")
    decision = string(obj.get("decision"))
    if decision not in DECISIONS:
        raise Invalid("Unknown agent decision")
    stacking = mapping(obj.get("stacking"))
    choice = string(stacking.get("choice"))
    if choice not in ("use", "unnecessary", "unsuitable"):
        raise Invalid("Agent must explicitly consider gh stack")
    if (choice == "use") != (decision == "SPLIT_WITH_GH_STACK"):
        raise Invalid("Stacking choice contradicts the decision")
    changed = {string(mapping(item)["path"]) for item in sequence(packet["files"])}
    inspected = {string(item) for item in sequence(obj.get("inspected_paths"))}
    if inspected != changed:
        raise Invalid("Agent must account for every changed path, including excluded and binary files")
    layers = tuple(mapping(item) for item in sequence(obj.get("layers")))
    if decision.startswith("SPLIT_"):
        if len(layers) < 2:
            raise Invalid("A split requires at least two concrete layers")
        covered: set[str] = set()
        for layer in layers:
            nonempty(layer.get("name"))
            nonempty(layer.get("validation"))
            paths = [string(item) for item in sequence(layer.get("paths"))]
            if not paths or len(paths) != len(set(paths)) or covered.intersection(paths):
                raise Invalid("Split layers must partition paths without duplicates")
            covered.update(paths)
        if covered != changed:
            raise Invalid("Split layers must account for every changed path")
    elif layers:
        raise Invalid("Only split decisions may specify layers")
    return Judgment(
        decision,
        nonempty(obj.get("agent")),
        nonempty(obj.get("rationale")),
        choice,
        nonempty(stacking.get("reason")),
        layers,
    )
