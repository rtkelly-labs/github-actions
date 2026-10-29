# Estate GitHub Actions: deterministic core

Pure Python, zero runtime dependencies, and no model calls. Independently maintained;
Diffra is an optional future integration, not a runtime or development dependency.

The core collects fingerprinted PR evidence, classifies files, measures changed
lines/files/Unicode characters, validates Bring Your Own Agent judgments, and
renders deterministic Markdown with an advisory numeric error code.

Sizing uses the largest weighted dimension band. Known lockfiles/generated outputs
are excluded, vendored code and snapshots retain quarter weight, and missing
patches remain incomplete rather than becoming zero. Size measures review volume;
it does not infer whether a change should be split. The next stack layer adds
GitHub Actions wrappers and the two-phase estate audit/presentation commands.

Use Python 3.12+ and uv:

```console
uv sync --locked
uv run pytest
uv run ruff check src tests
uv run ruff format --check src tests
uv run mypy
```

Tests include Gherkin BDD, twelve sanitized real-PR CSV cases, and Hypothesis
properties covering thresholds, weighting, ordering and Unicode boundaries.
See tests/fixtures/README.md for provenance and CONTRIBUTING.md for development.
