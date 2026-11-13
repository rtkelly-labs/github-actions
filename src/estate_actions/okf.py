"""Pure Python Open Knowledge Format (OKF v0.2) frontmatter validator and linter."""

from __future__ import annotations

import os
import re
import subprocess
import textwrap
from dataclasses import dataclass
from pathlib import Path
from typing import Any

OKF_TYPES: frozenset[str] = frozenset(
    {
        "note",
        "resource",
        "resource-hub",
        "policy",
        "concept",
        "project",
        "area",
        "reference",
        "index",
        "guide",
    }
)

OKF_STATUSES: frozenset[str] = frozenset(
    {
        "active",
        "reference",
        "draft",
        "unread",
        "archived",
        "deprecated",
    }
)

DATE_PATTERN = re.compile(r"^\d{4}-\d{2}-\d{2}$")
ACTOR_PATTERN = re.compile(r"^(?:(?:agent|process|human):[a-zA-Z0-9_.-]+|[a-zA-Z0-9_.-]+/[a-zA-Z0-9_.-]+)$")


@dataclass(frozen=True)
class Violation:
    """An issue identified in a note's frontmatter."""

    file_path: str
    line: int
    rule: str
    message: str


@dataclass(frozen=True)
class ParsedFrontmatter:
    """Parsed representation of YAML frontmatter."""

    raw_text: str
    end_line: int
    fields: dict[str, Any]


def parse_simple_yaml(text: str) -> dict[str, Any]:
    """Pure Python simple YAML parser for OKF frontmatter key-values, lists, and 1-level mappings."""
    result: dict[str, Any] = {}
    current_key: str | None = None
    current_dict: dict[str, Any] | None = None
    current_list: list[str] | None = None

    for line in textwrap.dedent(text).splitlines():
        trimmed = line.strip()
        if not trimmed or trimmed.startswith("#"):
            continue

        # Check for bullet list item under active key
        if trimmed.startswith("- ") and current_key is not None:
            val = trimmed[2:].strip().strip("\"'")
            if current_list is not None:
                current_list.append(val)
            else:
                current_list = [val]
                result[current_key] = current_list
            continue

        # Check for nested mapping line: e.g. "  by: claude"
        if (line.startswith("  ") or line.startswith("\t")) and current_key is not None:
            subparts = trimmed.split(":", 1)
            if len(subparts) == 2:
                subkey = subparts[0].strip()
                subval = subparts[1].strip().strip("\"'")
                if current_dict is not None:
                    current_dict[subkey] = subval
                else:
                    current_dict = {subkey: subval}
                    result[current_key] = current_dict
                continue

        # Top-level key: value
        if ":" in trimmed:
            # Commit any active nested list or dict
            current_dict = None
            current_list = None

            parts = trimmed.split(":", 1)
            key = parts[0].strip()
            val_part = parts[1].strip()

            if not val_part:
                # Key with empty value or starting a nested block
                current_key = key
                result[key] = {}
                current_dict = result[key]
                continue

            current_key = key
            # Check for inline list [a, b, c]
            if val_part.startswith("[") and val_part.endswith("]"):
                items = [item.strip().strip("\"'") for item in val_part[1:-1].split(",") if item.strip()]
                result[key] = items
            else:
                result[key] = val_part.strip("\"'")

    return result


def extract_frontmatter(content: str) -> tuple[ParsedFrontmatter | None, str | None]:
    """Extracts frontmatter lines from content. Returns (parsed, error_message)."""
    if not content.startswith("---"):
        return None, "File does not begin with frontmatter delimiter '---'"

    end_idx = content.find("\n---", 3)
    closing_len = 4
    if end_idx == -1:
        end_idx = content.find("\n...", 3)
        if end_idx == -1:
            return None, "Frontmatter is unterminated (missing closing '---' or '...')"

    fm_text = content[3:end_idx].strip("\r\n")
    end_line = content[: end_idx + closing_len].count("\n") + 1
    fields = parse_simple_yaml(fm_text)
    return ParsedFrontmatter(raw_text=fm_text, end_line=end_line, fields=fields), None


def validate_note(
    file_path: Path,
    content: str,
    is_new: bool = False,
    require_provenance: bool = True,
) -> list[Violation]:
    """Validates OKF v0.2 frontmatter conformance for a single note."""
    violations: list[Violation] = []
    path_str = str(file_path)

    parsed, err = extract_frontmatter(content)
    if err or parsed is None:
        violations.append(Violation(path_str, 1, "OKF001", err or "Unreadable frontmatter"))
        return violations

    fields = parsed.fields

    # R1: type
    note_type = fields.get("type")
    if not note_type or not isinstance(note_type, str):
        violations.append(Violation(path_str, 1, "OKF002", "Missing required 'type' in frontmatter"))
    elif note_type.lower() not in OKF_TYPES:
        allowed = ", ".join(sorted(OKF_TYPES))
        violations.append(
            Violation(path_str, 1, "OKF003", f"Invalid type '{note_type}'. Allowed types: {allowed}")
        )

    # R2: status
    status = fields.get("status")
    if not status or not isinstance(status, str):
        violations.append(Violation(path_str, 1, "OKF004", "Missing required 'status' in frontmatter"))
    elif status.lower() not in OKF_STATUSES:
        allowed = ", ".join(sorted(OKF_STATUSES))
        violations.append(
            Violation(path_str, 1, "OKF005", f"Invalid status '{status}'. Allowed statuses: {allowed}")
        )

    # R3: created date
    created = fields.get("created")
    if not created or not isinstance(created, str):
        violations.append(Violation(path_str, 1, "OKF006", "Missing required 'created' date in frontmatter"))
    else:
        created_str = created.split()[0] if " " in created else created
        if not DATE_PATTERN.match(created_str):
            violations.append(
                Violation(path_str, 1, "OKF007", f"Invalid 'created' date '{created}'. Must be YYYY-MM-DD")
            )

    # R4: updated date (optional, but if present must be valid)
    if "updated" in fields and isinstance(fields["updated"], str):
        updated_str = fields["updated"].split()[0] if " " in fields["updated"] else fields["updated"]
        if not DATE_PATTERN.match(updated_str):
            violations.append(
                Violation(
                    path_str,
                    1,
                    "OKF008",
                    f"Invalid 'updated' date '{fields['updated']}'. Must be YYYY-MM-DD",
                )
            )

    # R5: Provenance (ADR 0007)
    generated = fields.get("generated")
    if require_provenance and is_new and not generated:
        violations.append(
            Violation(
                path_str,
                1,
                "OKF009",
                "Newly created notes authored by agents must include 'generated: by: <actor>'",
            )
        )
    elif generated:
        actor: str | None = None
        if isinstance(generated, dict):
            actor = str(generated.get("by", "")).strip()
        elif isinstance(generated, str):
            actor = generated.strip()

        if not actor:
            violations.append(
                Violation(path_str, 1, "OKF010", "'generated' block must contain non-empty 'by: <actor>'")
            )
        else:
            # Disallow claiming human authorship on agent notes
            if is_new and actor.startswith("human:"):
                violations.append(
                    Violation(
                        path_str,
                        1,
                        "OKF011",
                        f"Forbidden author '{actor}'. Agents cannot claim human authorship (ADR 0007)",
                    )
                )
            elif not ACTOR_PATTERN.match(actor):
                violations.append(
                    Violation(
                        path_str,
                        1,
                        "OKF012",
                        f"Invalid actor '{actor}'. Expected <producer>/<version>, agent:..., or process:...",
                    )
                )

    return violations


def find_repo_root(start_dir: Path) -> Path:
    """Finds the root directory containing .git."""
    current = start_dir.resolve()
    while current != current.parent:
        if (current / ".git").exists():
            return current
        current = current.parent
    return start_dir


def get_git_diff_files(base_ref: str, vault_dir: Path) -> tuple[set[Path], set[Path]]:
    """Returns (all_changed_files, newly_added_files) relative to base_ref."""
    repo_root = find_repo_root(vault_dir)
    vault_str = str(vault_dir)

    # All added/modified markdown files
    cmd_all = ["git", "diff", "--name-only", "--diff-filter=d", base_ref, "--", vault_str]
    res_all = subprocess.run(cmd_all, capture_output=True, text=True, check=False, cwd=repo_root)
    all_files = {
        (repo_root / p.strip()).resolve() for p in res_all.stdout.splitlines() if p.strip().endswith(".md")
    }

    # Only newly added files
    cmd_new = ["git", "diff", "--name-only", "--diff-filter=A", base_ref, "--", vault_str]
    res_new = subprocess.run(cmd_new, capture_output=True, text=True, check=False, cwd=repo_root)
    new_files = {
        (repo_root / p.strip()).resolve() for p in res_new.stdout.splitlines() if p.strip().endswith(".md")
    }

    return all_files, new_files


def lint_vault(
    vault_dir: Path,
    base_ref: str | None = None,
    require_provenance: bool = True,
) -> tuple[list[Violation], int]:
    """Lints vault files. If base_ref is given, only checks changed files in diff."""
    all_violations: list[Violation] = []
    checked_count = 0

    if base_ref:
        changed_paths, new_paths = get_git_diff_files(base_ref, vault_dir)
        target_files = sorted(changed_paths)
    else:
        target_files = sorted(vault_dir.rglob("*.md"))
        new_paths = set()

    for path in target_files:
        if not path.is_file():
            continue
        # Skip attachments, media, git, obsidian internals
        parts = set(path.parts)
        if {".obsidian", ".git", "Attachments", "Images", "media"}.intersection(parts):
            continue

        try:
            content = path.read_text(encoding="utf-8")
        except UnicodeDecodeError:
            continue

        checked_count += 1
        is_new = path in new_paths or base_ref is None
        violations = validate_note(
            path,
            content,
            is_new=is_new,
            require_provenance=require_provenance,
        )
        all_violations.extend(violations)

    return all_violations, checked_count


def emit_github_annotations(violations: list[Violation]) -> None:
    """Prints GitHub Actions workflow commands (::error file=...::) for PR UI integration."""
    for v in violations:
        # Escaping special characters in workflow command
        msg = v.message.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")
        print(f"::error file={v.file_path},line={v.line},title={v.rule}::{msg}")


def format_markdown_summary(violations: list[Violation], checked_count: int) -> str:
    """Renders a readable summary for GITHUB_STEP_SUMMARY and CLI output."""
    if not violations:
        return f"### ✅ OKF Conformance: Passed\n\nVerified **{checked_count}** file(s) with 0 violations."

    lines = [
        f"### ❌ OKF Conformance: Failed ({len(violations)} violation(s) across {checked_count} file(s))\n",
        "| File | Rule | Line | Description |",
        "|---|---|---|---|",
    ]
    for v in violations:
        lines.append(f"| `{v.file_path}` | `{v.rule}` | {v.line} | {v.message} |")

    lines.append(
        "\n> [!TIP]\n"
        "> Ensure notes begin with valid YAML frontmatter specifying `type`, `status`,\n"
        "> `created: YYYY-MM-DD`, and `generated: by: <actor>` (ADR 0007)."
    )
    return "\n".join(lines)


def run_okf_lint() -> int:
    """Action entry point for okf-lint."""
    vault_path_env = os.environ.get("VAULT_PATH", "Vault")
    base_ref_env = os.environ.get("BASE_REF", "origin/main").strip()
    require_prov = os.environ.get("REQUIRE_PROVENANCE", "true").lower() in ("true", "1", "yes")

    vault_dir = Path(vault_path_env).resolve()
    base_ref = base_ref_env if base_ref_env else None

    violations, checked = lint_vault(vault_dir, base_ref=base_ref, require_provenance=require_prov)
    emit_github_annotations(violations)

    summary = format_markdown_summary(violations, checked)
    print(summary)

    step_summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if step_summary:
        with Path(step_summary).open("a", encoding="utf-8") as stream:
            stream.write(f"\n{summary}\n")

    gh_output = os.environ.get("GITHUB_OUTPUT")
    if gh_output:
        with Path(gh_output).open("a", encoding="utf-8") as stream:
            stream.write(f"violations-count={len(violations)}\n")
            stream.write(f"checked-count={checked}\n")
            stream.write(f"passed={'true' if not violations else 'false'}\n")

    return 1 if violations else 0
