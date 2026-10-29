import pytest
from pytest_bdd import given, parsers, scenarios, then, when

from estate_actions.assessment import judge, measure
from estate_actions.report import render
from tests.helpers import file, judgment, packet

scenarios("features/presentation.feature")


@pytest.fixture
def state():
    return {}


@given("a frozen PR with one small source file")
def small(state):
    state["packet"] = packet()


@given("a frozen PR with eight one-line source files")
def broad(state):
    state["packet"] = packet([file(f"src/{index}.py", "x") for index in range(8)])


@given("a frozen PR with one 50000-character source line")
def minified(state):
    state["packet"] = packet([file("bundle.js", "x" * 50000)])


@given("a frozen PR with an omitted textual patch")
def missing(state):
    item = file()
    item.pop("patch")
    state["packet"] = packet([item])


@given("an agent recommends keeping the PR together")
def keep(state):
    state["judgment"] = judgment(state["packet"])


@when("the assessment is presented")
def presented(state):
    context = state["packet"]
    agent = judge(state["judgment"], context) if "judgment" in state else None
    state["report"] = render(context, measure(context), agent)


@then(parsers.parse("the report has error code {code:d}"))
def code(state, code):
    assert state["report"].error_code == code


@then(parsers.parse("the report shows size {size}"))
def size(state, size):
    assert state["report"].size == size
    assert f"Size: {size}" in state["report"].markdown


@then("the report explains the gh stack decision")
def stacking(state):
    assert "**gh stack:** unnecessary" in state["report"].markdown


@then("the report requires review without inferring a split")
def no_inference(state):
    assert state["report"].decision == "NEEDS_REVIEW"
    assert "No split decision has been inferred" in state["report"].markdown


@then("the report describes its size as a lower bound")
def lower_bound(state):
    assert "lower bound" in state["report"].markdown
