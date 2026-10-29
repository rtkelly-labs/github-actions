import pytest

from estate_actions.cli import emit, run
from estate_actions.data import Invalid, canonical
from estate_actions.github import GitHub
from estate_actions.presentation import present
from estate_actions.report import publish, session_body
from tests.helpers import judgment, packet


def test_author_scoped_sticky_comment_updates_instead_of_appending(monkeypatch):
    old = session_body("one", "old")
    body = session_body("one", "new")
    comments = [
        {"id": 10, "body": old, "user": {"login": "someone-else"}},
        {"id": 11, "body": old, "user": {"login": "github-actions[bot]"}},
    ]
    calls = []
    monkeypatch.setattr(GitHub, "pages", lambda *args: comments)
    monkeypatch.setattr(GitHub, "request", lambda *args: calls.append(args[1:]))
    assert publish(GitHub("secret", "a/b"), 1, body, "github-actions[bot]") == 11
    assert calls == [("PATCH", "issues/comments/11", {"body": body})]


def test_unchanged_sticky_report_does_not_write(monkeypatch):
    body = session_body("one", "same")
    monkeypatch.setattr(
        GitHub, "pages", lambda *args: [{"id": 11, "body": body, "user": {"login": "github-actions[bot]"}}]
    )
    monkeypatch.setattr(GitHub, "request", lambda *args: pytest.fail("Unexpected mutation"))
    assert publish(GitHub("secret", "a/b"), 1, body, "github-actions[bot]") == 11


def test_multiline_output_resists_delimiter_collision(monkeypatch, tmp_path):
    location = tmp_path / "outputs"
    monkeypatch.setenv("GITHUB_OUTPUT", str(location))
    emit({"markdown": "Hello\nESTATE_OUTPUT\nworld", "error-code": "-1"})
    assert "markdown<<ESTATE_OUTPUT_\nHello\nESTATE_OUTPUT\nworld\nESTATE_OUTPUT_\n" in location.read_text()
    assert "error-code<<ESTATE_OUTPUT\n-1\nESTATE_OUTPUT\n" in location.read_text()


def test_presentation_needs_neither_token_nor_network(monkeypatch, tmp_path):
    context = packet()
    context_file, agent_file = tmp_path / "context.json", tmp_path / "agent.json"
    context_file.write_text(canonical(context), encoding="utf-8")
    agent_file.write_text(canonical(judgment(context)), encoding="utf-8")
    monkeypatch.setenv("CONTEXT_FILE", str(context_file))
    monkeypatch.setenv("ASSESSMENT_FILE", str(agent_file))
    monkeypatch.setenv("REPORT_DIRECTORY", str(tmp_path))
    monkeypatch.setenv("GITHUB_OUTPUT", str(tmp_path / "outputs"))
    monkeypatch.delenv("GH_TOKEN", raising=False)
    monkeypatch.setattr(GitHub, "request", lambda *args: pytest.fail("Presentation used the network"))
    assert run("assess") == 0
    assert "error-code<<ESTATE_OUTPUT\n0\n" in (tmp_path / "outputs").read_text()


def test_stale_judgment_emits_nonzero_code_but_process_succeeds(monkeypatch, tmp_path):
    context = packet()
    agent = judgment(context)
    agent["evidence_key"] = "stale"
    for name, value in (("context", context), ("agent", agent)):
        (tmp_path / f"{name}.json").write_text(canonical(value), encoding="utf-8")
    monkeypatch.setenv("CONTEXT_FILE", str(tmp_path / "context.json"))
    monkeypatch.setenv("ASSESSMENT_FILE", str(tmp_path / "agent.json"))
    monkeypatch.setenv("REPORT_DIRECTORY", str(tmp_path))
    monkeypatch.setenv("GITHUB_OUTPUT", str(tmp_path / "outputs"))
    assert run("assess") == 0
    assert "error-code<<ESTATE_OUTPUT\n2\n" in (tmp_path / "outputs").read_text()


def test_csv_presentation_is_reproducible(monkeypatch, tmp_path):
    context = packet()
    (tmp_path / "packet.json").write_text(canonical(context), encoding="utf-8")
    snapshot = {
        "schema_version": 1,
        "coverage": [],
        "pull_requests": [
            {
                "repository": "example/repo",
                "number": 42,
                "in_stack": True,
                "status": "GATHERED",
                "packet_file": "packet.json",
                "packet_key": context["evidence_key"],
            }
        ],
    }
    (tmp_path / "snapshot.json").write_text(canonical(snapshot), encoding="utf-8")
    monkeypatch.setattr(GitHub, "request", lambda *args: pytest.fail("CSV presentation used network"))
    first, second = tmp_path / "first", tmp_path / "second"
    present(tmp_path / "snapshot.json", first)
    present(tmp_path / "snapshot.json", second)
    assert (first / "pr-scores.csv").read_bytes() == (second / "pr-scores.csv").read_bytes()
    assert ",True," in (first / "pr-scores.csv").read_text(encoding="utf-8")


def test_duplicate_sessions_are_reported_not_silently_chosen(monkeypatch):
    body = session_body("one", "same")
    monkeypatch.setattr(
        GitHub,
        "pages",
        lambda *args: [
            {"id": number, "body": body, "user": {"login": "github-actions[bot]"}} for number in (1, 2)
        ],
    )
    with pytest.raises(Invalid, match="Duplicate"):
        publish(GitHub("secret", "a/b"), 1, body, "github-actions[bot]")
