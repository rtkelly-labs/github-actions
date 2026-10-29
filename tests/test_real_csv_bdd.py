import csv
from pathlib import Path

import pytest
from pytest_bdd import given, parsers, scenarios, then, when

from estate_actions.assessment import Change, Policy, score_files
from estate_actions.report import render
from tests.helpers import packet

scenarios("features/real_csv.feature")


@pytest.fixture
def state():
    return {}


@given(parsers.parse("real PR measurements from CSV case {case_id}"))
def real_measurements(state, case_id):
    location = Path(__file__).parent / "fixtures/real_pr_files.csv"
    with location.open(encoding="utf-8", newline="") as stream:
        rows = [row for row in csv.DictReader(stream) if row["case_id"] == case_id]
    assert rows, f"Missing real-data CSV case {case_id}"
    state["expected"] = rows[0]
    state["changes"] = [
        Change(
            row["path"],
            int(row["additions"]),
            int(row["deletions"]),
            int(row["characters"]) if row["characters"] else None,
        )
        for row in rows
    ]


@when("the real measurements are scored and presented")
def score_real(state):
    state["metrics"] = score_files(state["changes"], Policy())
    state["report"] = render(packet(), state["metrics"], None)


@then("all expected dimension bands match the CSV")
def bands(state):
    for name in ("size", "line_size", "file_size", "character_size"):
        assert getattr(state["metrics"], name) == state["expected"][f"expected_{name}"]


@then("character coverage matches the CSV")
def coverage(state):
    actual = "partial" if state["metrics"].missing_patches else "complete"
    assert actual == state["expected"]["expected_coverage"]
    assert state["report"].error_code == 2  # Sizing fixtures do not claim agent assessment.


@then("presenting the real measurements again gives identical Markdown")
def repeat(state):
    repeated = score_files(list(reversed(state["changes"])), Policy())
    assert render(packet(), repeated, None).markdown == state["report"].markdown
