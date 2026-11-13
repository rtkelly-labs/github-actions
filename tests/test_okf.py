"""Unit tests for OKF v0.2 frontmatter validation."""

from pathlib import Path

from estate_actions.okf import (
    lint_vault,
    parse_simple_yaml,
    validate_note,
)


def test_parse_simple_yaml_handles_mappings_and_lists() -> None:
    text = """
    type: note
    status: active
    created: 2026-11-13
    tags: [architecture, dotnet]
    generated:
      by: claude-opus-5/1m
      at: 2026-11-13T09:00:00Z
    """
    fields = parse_simple_yaml(text)
    assert fields["type"] == "note"
    assert fields["status"] == "active"
    assert fields["created"] == "2026-11-13"
    assert fields["tags"] == ["architecture", "dotnet"]
    assert isinstance(fields["generated"], dict)
    assert fields["generated"]["by"] == "claude-opus-5/1m"


def test_validate_note_valid_conformance() -> None:
    content = """---
type: note
status: active
created: 2026-11-13
updated: 2026-11-13
generated:
  by: claude-opus-5/1m
---

# Title
Body text.
"""
    violations = validate_note(Path("Vault/test.md"), content, is_new=True, require_provenance=True)
    assert violations == []


def test_validate_note_missing_frontmatter() -> None:
    content = "# Just markdown\nNo YAML."
    violations = validate_note(Path("Vault/test.md"), content)
    assert len(violations) == 1
    assert violations[0].rule == "OKF001"


def test_validate_note_unterminated_frontmatter() -> None:
    content = "---\ntype: note\nstatus: active"
    violations = validate_note(Path("Vault/test.md"), content)
    assert len(violations) == 1
    assert violations[0].rule == "OKF001"


def test_validate_note_missing_and_invalid_type() -> None:
    content_missing = "---\nstatus: active\ncreated: 2026-11-13\n---\n"
    v_missing = validate_note(Path("Vault/test.md"), content_missing)
    assert any(v.rule == "OKF002" for v in v_missing)

    content_invalid = "---\ntype: non_existent_type\nstatus: active\ncreated: 2026-11-13\n---\n"
    v_invalid = validate_note(Path("Vault/test.md"), content_invalid)
    assert any(v.rule == "OKF003" for v in v_invalid)


def test_validate_note_missing_and_invalid_status() -> None:
    content_missing = "---\ntype: note\ncreated: 2026-11-13\n---\n"
    v_missing = validate_note(Path("Vault/test.md"), content_missing)
    assert any(v.rule == "OKF004" for v in v_missing)

    content_invalid = "---\ntype: note\nstatus: not_real\ncreated: 2026-11-13\n---\n"
    v_invalid = validate_note(Path("Vault/test.md"), content_invalid)
    assert any(v.rule == "OKF005" for v in v_invalid)


def test_validate_note_date_formats() -> None:
    content_bad_created = "---\ntype: note\nstatus: active\ncreated: 13-11-2026\n---\n"
    v_created = validate_note(Path("Vault/test.md"), content_bad_created)
    assert any(v.rule == "OKF007" for v in v_created)

    content_bad_updated = "---\ntype: note\nstatus: active\ncreated: 2026-11-13\nupdated: bad_date\n---\n"
    v_updated = validate_note(Path("Vault/test.md"), content_bad_updated)
    assert any(v.rule == "OKF008" for v in v_updated)


def test_validate_note_provenance_enforcement() -> None:
    # New note missing provenance
    content_no_prov = "---\ntype: note\nstatus: active\ncreated: 2026-11-13\n---\n"
    v_no_prov = validate_note(Path("Vault/test.md"), content_no_prov, is_new=True, require_provenance=True)
    assert any(v.rule == "OKF009" for v in v_no_prov)

    # Note with human authorship claim without evidence
    content_fake_human = """---
type: note
status: active
created: 2026-11-13
generated:
  by: human:rtkelly13
---
"""
    v_fake_human = validate_note(
        Path("Vault/test.md"), content_fake_human, is_new=True, require_provenance=True
    )
    assert any(v.rule == "OKF011" for v in v_fake_human)

    # Note with malformed actor syntax
    content_bad_actor = """---
type: note
status: active
created: 2026-11-13
generated:
  by: "invalid actor syntax with spaces"
---
"""
    v_bad_actor = validate_note(
        Path("Vault/test.md"), content_bad_actor, is_new=True, require_provenance=True
    )
    assert any(v.rule == "OKF012" for v in v_bad_actor)


def test_lint_vault_directory(tmp_path: Path) -> None:
    vault = tmp_path / "Vault"
    vault.mkdir()

    # Valid note
    (vault / "good.md").write_text(
        "---\ntype: note\nstatus: active\ncreated: 2026-11-13\ngenerated:\n  by: agent:codex\n---\n# Good\n",
        encoding="utf-8",
    )

    # Bad note
    (vault / "bad.md").write_text("# No frontmatter\n", encoding="utf-8")

    violations, checked = lint_vault(vault, base_ref=None, require_provenance=True)
    assert checked == 2
    assert len(violations) == 1
    assert violations[0].file_path.endswith("bad.md")
    assert violations[0].rule == "OKF001"
