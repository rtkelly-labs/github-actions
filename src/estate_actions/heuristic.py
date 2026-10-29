"""Versioned, integer-only file-type review-volume heuristic. Never predicts correctness."""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import PurePosixPath

VERSION = "file-types-v2"


@dataclass(frozen=True)
class FileType:
    name: str
    weight: int  # hundredths; all arithmetic is integer until display


def classify(path: str) -> FileType:
    name = PurePosixPath(path).name.lower()
    suffix = PurePosixPath(path).suffix.lower()
    parts = {part.lower() for part in PurePosixPath(path).parts}
    if name in {
        "uv.lock",
        "poetry.lock",
        "pnpm-lock.yaml",
        "package-lock.json",
        "yarn.lock",
        "cargo.lock",
        "paket.lock",
        "packages.lock.json",
        "composer.lock",
        "gemfile.lock",
        "pipfile.lock",
        "pdm.lock",
        "bun.lock",
        "bun.lockb",
        ".terraform.lock.hcl",
    }:
        return FileType("lockfile", 0)
    if parts.intersection({"vendor", "vendors", "third_party", "third-party"}):
        return FileType("vendored", 25)
    if suffix in {".snap", ".snapshot"} or "__snapshots__" in parts:
        return FileType("snapshot", 25)
    if name.endswith((".min.js", ".min.css", ".map", ".g.cs", ".generated.cs", ".generated.ts")):
        return FileType("generated", 0)
    if suffix in {".md", ".mdx", ".rst", ".txt", ".adoc"}:
        return FileType("documentation", 50)
    if ".github" in parts and "workflows" in parts:
        return FileType("workflow", 125)
    if suffix in {".json", ".yaml", ".yml", ".toml", ".ini", ".cfg", ".xml", ".props", ".targets"}:
        return FileType("configuration", 100)
    if suffix in {".csv", ".tsv", ".ndjson", ".jsonl"}:
        return FileType("data", 25)
    if suffix in {".css", ".scss", ".sass", ".less", ".svg"}:
        return FileType("style-or-vector", 75)
    if suffix in {".png", ".jpg", ".jpeg", ".gif", ".webp", ".ico", ".pdf", ".woff", ".woff2"}:
        return FileType("binary-asset", 100)
    # Tests receive full weight: they establish behavior and must still be reviewed.
    if parts.intersection({"test", "tests", "__tests__"}) or name.startswith("test_"):
        return FileType("test", 100)
    return FileType("source-or-other", 100)
