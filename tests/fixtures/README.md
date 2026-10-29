# Real PR measurement fixtures

`real_pr_files.csv` contains frozen file-level measurements from public PRs,
gathered on 2026-10-29. Source PR URLs and head SHAs identify provenance.
Paths are replaced with category-preserving aliases; no patch/source contents,
titles, private repository identifiers or credentials are included.

Each case has independently inspectable raw additions, deletions and Unicode
changed-character counts. Empty character cells mean incomplete patch evidence,
not zero. Expected bands are frozen regression expectations for the documented
documented thresholds. Review those values against README.md before deliberately updating
the heuristic; do not regenerate expectations automatically during tests.

BDD loads these measurements directly into the same pure scoring and rendering
functions used by the action. Synthetic boundary/property tests complement these
real cases. No fixture claims that its source PR should or should not be split.
