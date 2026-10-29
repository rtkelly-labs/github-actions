import copy

import pytest
from hypothesis import given
from hypothesis import strategies as st

from estate_actions.assessment import SIZES, Policy, bucket, judge, measure, patch_characters, verify_packet
from estate_actions.data import Invalid
from estate_actions.report import render, session_body
from tests.helpers import file, judgment, packet

TEXT = st.text(st.characters(exclude_categories=("Cs",), exclude_characters="\n\r"), max_size=1000)


@given(TEXT)
def test_character_count_matches_unicode_content(text):
    assert patch_characters(f"@@ -0,0 +1 @@\n+{text}", 1, 0) == len(text)


@given(st.integers(0, 100_000), st.integers(0, 100_000))
def test_buckets_are_monotone(first, second):
    low, high = sorted((first, second))
    assert SIZES.index(bucket(low, Policy().line_limits)) <= SIZES.index(bucket(high, Policy().line_limits))


@pytest.mark.parametrize("dimension", ["line_limits", "file_limits", "character_limits"])
def test_all_inclusive_boundaries(dimension):
    limits = getattr(Policy(), dimension)
    for index, threshold in enumerate(limits):
        assert bucket(threshold, limits) == SIZES[index]
        assert bucket(threshold + 1, limits) == SIZES[index + 1]


@given(st.integers(1, 40), st.integers(0, 3000))
def test_combining_dimensions_never_hides_the_largest(files, characters):
    context = packet([file(f"src/{index}.py", "x" * characters) for index in range(files)])
    metrics = measure(context)
    assert metrics.size == max(
        (metrics.line_size, metrics.file_size, metrics.character_size), key=SIZES.index
    )


@given(st.integers(0, 5000))
def test_generated_additions_do_not_change_included_size(characters):
    baseline = measure(packet())
    augmented = measure(packet([file(), file("bundle.min.js", "x" * characters)]))
    assert augmented.size == baseline.size
    assert augmented.weighted_characters == baseline.weighted_characters
    assert augmented.total_files == 2


@given(st.lists(st.integers(0, 1000), min_size=1, max_size=10, unique=True))
def test_packet_fingerprint_and_report_ignore_api_file_order(values):
    files = [file(f"src/{value}.py") for value in values]
    first, second = packet(files), packet(list(reversed(files)))
    assert first == second
    assert render(first, measure(first), None) == render(second, measure(second), None)


@given(TEXT)
def test_sticky_session_is_stable(text):
    assert session_body("assessment", text) == session_body("assessment", text)
    assert session_body("assessment", text) != session_body("other", text)


@pytest.mark.parametrize("patch", [None, "@@ -0,0 +1,2 @@\n+x", "@@ -0,0 +1 @@\n+x\n+y"])
def test_missing_and_truncated_patches_are_unknown(patch):
    assert patch_characters(patch, 2, 0) is None


def test_diff_metadata_and_context_do_not_count_as_changed_characters():
    patch = "@@ -1,2 +1,2 @@\n keep\n-old\n+newer\n\\ No newline at end of file"
    assert patch_characters(patch, 1, 1) == 8


def test_mutated_packet_is_rejected():
    context = packet()
    context["head_sha"] = "changed"
    with pytest.raises(Invalid, match="fingerprint"):
        verify_packet(context)


@pytest.mark.parametrize("field", ["evidence_key", "inspected_paths", "stacking"])
def test_missing_judgment_evidence_is_rejected(field):
    context = packet()
    value = judgment(context)
    value.pop(field)
    with pytest.raises(Invalid):
        judge(value, context)


def test_split_must_have_concrete_layers():
    context = packet()
    value = judgment(context, "SPLIT_INDEPENDENTLY")
    with pytest.raises(Invalid, match="two concrete"):
        judge(value, context)


def test_weights_change_volume_without_hiding_tests():
    source, docs, tests = [
        measure(packet([file(path, "x" * 100)]))
        for path in ("src/main.py", "README.md", "tests/test_main.py")
    ]
    assert docs.weighted_characters * 2 == source.weighted_characters == tests.weighted_characters


def test_supplied_judgment_is_not_modified():
    context = packet()
    value = judgment(context)
    original = copy.deepcopy(value)
    render(context, measure(context), judge(value, context))
    assert value == original


def test_replaced_path_counts_once_and_preserves_both_diff_segments():
    added = file("CLAUDE.md", "new")
    removed = {
        **file("CLAUDE.md", "old"),
        "status": "removed",
        "additions": 0,
        "deletions": 1,
        "patch": "@@ -1 +0,0 @@\n-old",
    }
    # GitHub reports one unique changed path but can return two file records.
    from estate_actions.assessment import make_packet

    metadata = {
        "base": {"sha": "a" * 40, "ref": "main"},
        "head": {"sha": "b" * 40, "ref": "feature"},
        "title": "Replace file kind",
        "body": "",
        "changed_files": 1,
    }
    context = make_packet("a/b", 1, metadata, [added, removed], Policy())
    metrics = measure(context)
    assert metrics.total_files == 1
    assert metrics.total_lines == 2
    assert metrics.characters == 6
    assert not metrics.missing_patches


def test_sticky_marker_is_not_duplicated_between_presentation_and_publication():
    body = session_body("assessment", "Markdown")
    assert session_body("assessment", body) == body


def test_vendored_source_is_visible_but_reduced_and_paket_lock_is_excluded():
    source = measure(packet([file("src/library.js", "x" * 100)]))
    vendor = measure(packet([file("src/vendor/library.js", "x" * 100)]))
    assert vendor.weighted_characters * 4 == source.weighted_characters
    assert vendor.files == 1
    lock = measure(packet([file("paket.lock", "x" * 100)]))
    assert lock.files == 0
    assert lock.total_files == 1
    assert lock.excluded_paths == ("paket.lock",)


@pytest.mark.parametrize("value", ['{"a":1,"a":2}', '{"a":NaN}', '{"a":Infinity}'])
def test_ambiguous_or_nonfinite_json_is_rejected(value):
    from estate_actions.data import decode

    with pytest.raises(Invalid):
        decode(value)
