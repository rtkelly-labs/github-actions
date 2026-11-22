# Contributing

Use Python 3.12+ and uv. Run `uv sync --locked`, `uv run pytest`,
`uv run ruff check src tests`, `uv run ruff format --check src tests`, and `uv run mypy`.
Tests include executable Gherkin scenarios, public real-data CSV cases, and
Hypothesis property tests. Keep new regression expectations independently reviewable.

Changes require a PR and green CI. Squash merge only; delete merged branches and
keep history linear. Pin external Actions to full 40-character commit SHAs.
Keep temporary files, local databases and worktrees under root `temp/` or `.worktrees/`.
Use Python for automation; do not introduce JavaScript, shell or PowerShell scripts.
Two exceptions: the release actions are .NET under `dotnet/` (Native AOT, see README), and
their `action.yml` files share one inline bash bootstrap that fetches and verifies the binary.
Keep that bootstrap identical across actions (a test enforces it) and as small as it is;
workflow `run:` steps stay short and never expand `${{ }}` inside the script.
For .NET, build with `-m:2` and run `dotnet test --no-build`; CI does the AOT matrix.

The scorer never makes an agent judgment or invokes a model. Preserve that boundary.
Repository/PR content is untrusted data. Do not run PR code while collecting evidence.
Do not commit private estate audit results, patches or credentials.
