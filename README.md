# Estate GitHub Actions

Pure Python, zero runtime dependencies, no model calls. Deterministic PR sizing,
Bring Your Own Agent (BYOA) judgments, and sticky Markdown report sessions.
Requires Python 3.12+ on the runner. Development uses uv.

The [release actions](#release-actions-native-aot-net) (`release-draft`, `release-finalize`,
`release-check-artifacts`) are the exception: they are .NET, shipped as prebuilt Native AOT
binaries, and they fail the step on a refusal instead of reporting an advisory code.

This is an independently maintained PR tool with its own contracts, tests and
release lifecycle. It has no Diffra dependency. A future Diffra adapter can consume
its evidence and reports without making that integration necessary to use it.

## Two phases

1. **Gather and group:** `pr-context` reads GitHub REST evidence, paginates files,
   verifies the unique changed-file count, and saves an exact fingerprinted JSON
   packet. Estate gathering additionally records GitHub's current native stack
   membership and groups paths by file type. No PR code is executed.
2. **Presentation:** `pr-assessment` reads that frozen packet and optional trusted
   agent JSON. Pure functions calculate size, validate the agent contract, and
   emit Markdown plus an error code. This phase needs no credentials or network.

`sticky-comment` publishes the resulting Markdown separately. The same session
updates one comment belonging to the configured author, rather than appending
on each run. Identical content performs no write. Duplicate owned sessions are
reported as publication failures rather than chosen arbitrarily.

## Sizing: three dimensions and file-type weights

For each included file, sum additions + deletions and changed Unicode characters.
Characters exclude diff markers, context, and line endings; replacements count
both old and new content. Verify patch hunk lengths and GitHub's line counts.
An absent/truncated patch is unknown, never zero. Pure renames still count as
files. Binary files with zero textual changes contribute file breadth, not text.
GitHub can return deletion/addition entries for one replaced path: those entries
are combined, preserving both changes while counting one unique file.

Weights are explicit hundredths, versioned in `heuristic.py`:

| File type | Weight |
| --- | ---: |
| Source, tests, configuration, binary/unknown files | 1.00 |
| GitHub workflow YAML | 1.25 |
| Styles and SVG | 0.75 |
| Documentation | 0.50 |
| Tabular data, snapshots, and explicitly vendored paths | 0.25 |
| Known lockfiles and explicit generated/minified suffixes | 0.00 |

Classification follows ordered rules on paths and extensions; no content inference.
Minified files with explicit `.min.js`/`.min.css` names are excluded as generated;
a long line in normal source still contributes its full character count.
Tests retain full weight. Unknown files retain full weight.
There is no broad exclusion for a directory named `generated`.
Vendored paths (`vendor`, `vendors`, `third_party`, `third-party`) retain a quarter
weight rather than disappearing. The v2 refinement follows an outlier sample
that exposed imported three.js sources and the initially missing `paket.lock`.

Multiply each file's lines, file contribution, and characters by its type weight.
All scoring arithmetic uses integers in hundredths. Determine each dimension's
band independently, then choose the **largest band**. Low LOC cannot cancel high
file breadth, and few lines cannot hide a large character count.

| Size | Weighted lines, up to | Weighted files, up to | Weighted characters, up to |
| --- | ---: | ---: | ---: |
| XS | 9 | 1 | 499 |
| S | 29 | 3 | 1,499 |
| M | 99 | 7 | 4,999 |
| L | 499 | 15 | 24,999 |
| XL | 999 | 30 | 49,999 |
| XXL | Above XL | Above XL | Above XL |

These inclusive limits are initial policy choices, not validated predictions of
review time or correctness. Reports show raw totals, excluded paths, weighted
dimensions, and each band. Missing included patches produce a lower-bound size
and incomplete assessment. A large size alone never invents a split judgment.

Prior art: [`noqcks/pull-request-size`](https://github.com/noqcks/pull-request-size)
uses the familiar six T-shirt names and changed-line scale.
[`kvokka/pr-size-labeler`](https://github.com/kvokka/pr-size-labeler) combines line
and symbol dimensions using the larger eligible size. This implementation adds
file breadth and explicit file-type weights. Character/file cutoffs and weights
are this project's initial policy, not copied empirical findings.

Optional trusted JSON policy supports `line_limits`, `file_limits`,
`character_limits` (five increasing inclusive positive integer limits each),
`excluded` (case-sensitive fnmatch globs), `components`, and `context`.
Components/context are evidence for your agent; they do not alter scores.
Policy changes affect the evidence key. Never read privileged policy from a PR
checkout. No generated-file exclusions are inferred from a PR's `.gitattributes`.

## BYOA contract

Your agent reads the packet and relevant source with its own tools, considers
reviewability/validation and `gh stack`, and writes:

```json
{
  "schema_version": 1,
  "evidence_key": "<exact key from pr-context>",
  "agent": "your-agent/version",
  "decision": "KEEP_TOGETHER",
  "rationale": "Explain the coherent outcome and why keeping or splitting improves review.",
  "inspected_paths": ["src/example.py", "tests/test_example.py"],
  "stacking": {
    "choice": "unnecessary",
    "reason": "Explain why dependent layers would or would not help."
  },
  "layers": []
}
```

Decisions: `KEEP_TOGETHER`, `SPLIT_INDEPENDENTLY`, `SPLIT_WITH_GH_STACK`,
`NEEDS_REVIEW`. Stacking choice: `use`, `unnecessary`, `unsuitable`.
`use` must agree with `SPLIT_WITH_GH_STACK`. Every changed path, including excluded
and binary files, must be accounted for. Split decisions require at least two
layers, each with `name`, `paths`, and `validation`, covering every changed path.
The initial contract partitions by file; same-file/hunk-level splits need a
future contract revision. A fingerprint validates input identity, not honesty:
the caller remains responsible for trusting its agent and supplied judgment.

Missing, malformed or stale judgments become `NEEDS_REVIEW`; the action never
invokes a model, guesses a judgment, or converts a size into an agent decision.

## Outputs and advisory codes

The two report elements are `markdown` (the comment body) and `error-code`.
`pr-assessment` also exposes local `markdown-file`, `size`, `decision`,
`context-file`, and `evidence-key` for composition.

| Code | Meaning |
| --- | --- |
| 0 | Valid supplied keep-together judgment with complete measurements |
| 1 | Valid supplied split recommendation |
| 2 | Missing/stale/invalid judgment, incomplete measurements, or invalid input |
| 3 | Sticky publication failed, including stale PR evidence |

**Codes are outputs only. They currently do not fail the process or workflow.**
The generic `sticky-comment` action accepts any integer error code, including
negative values, and passes it through unless publication fails. Host failures
such as a missing interpreter or inability to write outputs can still fail a step.

## Workflow integration

Pin all three actions to the same reviewed 40-character commit SHA. The example
below is a template: replace `REVIEWED_COMMIT_SHA` before using it.
Run your own agent between gather and presentation; there is no built-in provider.

```yaml
name: PR scope
on:
  pull_request_target:
    types: [opened, reopened, synchronize, edited, ready_for_review]
permissions:
  contents: read
  pull-requests: write
concurrency:
  group: pr-scope-${{ github.repository }}-${{ github.event.pull_request.number }}
  cancel-in-progress: false
jobs:
  assessment:
    runs-on: ubuntu-latest
    timeout-minutes: 15
    steps:
      - id: gather
        uses: rtkelly-labs/github-actions/pr-context@REVIEWED_COMMIT_SHA
      # Your trusted agent consumes steps.gather.outputs.context-file and
      # writes its judgment to an agreed path under runner.temp.
      # Omit assessment-file until an agent is integrated: size is still reported.
      - id: present
        uses: rtkelly-labs/github-actions/pr-assessment@REVIEWED_COMMIT_SHA
        with:
          context-file: ${{ steps.gather.outputs.context-file }}
      - uses: rtkelly-labs/github-actions/sticky-comment@REVIEWED_COMMIT_SHA
        with:
          session: pr-scope-assessment
          markdown-file: ${{ steps.present.outputs.markdown-file }}
          error-code: ${{ steps.present.outputs.error-code }}
          context-file: ${{ steps.gather.outputs.context-file }}
```

Serialize all publishers for the same repository/PR/session, including central
and repository workflows. GitHub's comment API offers no atomic create-if-absent;
without shared concurrency, simultaneous first runs can create duplicates.
Publication with a context packet rechecks live evidence before writing.
This is advisory tooling, not an atomic merge-time freshness guarantee.
The expected comment author defaults to `github-actions[bot]`; configure the
actual token author's login when using another bot or user token.

Only run trusted action/agent code in privileged contexts. Never check out or
execute the PR head in `pull_request_target`. A normal `pull_request` workflow
can gather/present with read-only permissions; fork tokens generally cannot post
comments. Repository content and PR text are untrusted evidence, never instructions.

## Estate audit and offline replay

```console
uv sync --locked
PYTHONPATH=src uv run python -m estate_actions.audit --states all --output temp/audit
PYTHONPATH=src uv run python -m estate_actions.presentation temp/audit/snapshot.json --output temp/presentation
```

Gather uses `GH_TOKEN`, falling back to `gh auth token`, and enumerates accessible
personal `rtkelly13`, `Resultful`, and `rtkelly-labs` repositories, including private,
archived, and fork repositories. It paginates all open/closed/merged PRs. Explicit
`--owners` and `--states open` narrow the selection. No labels, comments, branches,
or repository settings are changed by the audit.

Outputs: coverage ledger, frozen snapshot, cached fingerprinted PR packets, then
`pr-scores.csv`, `scores.json`, and `distribution.md` from offline presentation.
CSV records raw/weighted dimensions, per-type contributions, size, completeness,
source refs and current native `in_stack`, stack number/ID/size. There is no
stack inference from branch names/bases and no attempt to reconstruct past stacks.
`review_score` is the maximum dimension normalized to its M-band upper bound;
1 means at the M ceiling in the limiting dimension, not a probability of defects.

Keep full estate snapshots/CSVs local: they can contain private repository metadata
and source patches. The public test CSV uses only public PR measurements with
category-preserving path aliases and no source content.

## Validation

```console
uv run pytest
uv run ruff check src tests
uv run ruff format --check src tests
uv run mypy
```

BDD covers pure presentation and frozen real-PR CSV cases. Hypothesis checks
monotonicity, order invariance, weighting, Unicode counts and combined dimensions;
unit tests cover stale judgments, output delimiters and sticky session ownership.
`pytest<9` is intentional for the current pytest-bdd fixture API compatibility.

The repository's PR smoke job runs proposed action code with read-only credentials.
The same workflow's manual `workflow_dispatch` mode tests sticky publication only
from trusted `main`, pinned to the dispatch commit, with a separate write-enabled
job. Proposed PR code is never used by the write-enabled publication job.

## Release actions (Native AOT .NET)

Draft a GitHub release when packages are built, and publish it (creating its tag) only once the
package is public on nuget.org from the same commit. Each action runs one subcommand of a single
binary, `estate-actions`, built from `dotnet/`. No .NET SDK, restore or build happens in the
caller's job.

```yaml
permissions:
  contents: write # drafts are created, listed and published; drafts are visible only with push access
steps:
  - uses: rtkelly-labs/github-actions/release-check-artifacts@<commit>
    with:
      plan: ${{ steps.plan.outputs.plan_json }}
      sha: ${{ github.sha }}
  - uses: rtkelly-labs/github-actions/release-draft@<commit>
    with:
      tag: Demo/v1.2.0
      sha: ${{ github.sha }}
      title: Demo.Pkg 1.2.0
      notes: ${{ steps.plan.outputs.notes }}
      prerelease: 'false'
  # Later, e.g. on a schedule or after nuget.org indexes the package:
  - id: finalize
    uses: rtkelly-labs/github-actions/release-finalize@<commit>
    with:
      tag: Demo/v1.2.0
```

| Action | Inputs | Outputs | Behaviour |
| --- | --- | --- | --- |
| `release-check-artifacts` | `plan`, `sha`, `artifacts` (default `artifacts`) | none | The tree holds exactly `<key>/<id>.<version>.nupkg` and `.snupkg` for each plan entry, and each `.nupkg`'s nuspec records `<repository commit="sha">`. |
| `release-draft` | `tag`, `sha`, `title`, `notes`, `prerelease`, `token`, `repository` | `release-id` | Refuses if the tag has any release. Creates a draft at `sha` (a draft creates no tag), then polls the release list until its own draft appears (60s cap). If any other release for the tag is listed by then, it deletes its own draft and fails, so racing runs back out instead of leaving two drafts. |
| `release-finalize` | `tag`, `discard`, `token`, `repository` | `outcome`: `published`, `already-published`, `not-public`, `discarded` | Needs exactly one draft and no existing tag. Reads `.github/release-packages.json` at the draft commit through the contents API, then the nuspec on nuget.org: 200 is public, 404 is not (a no-op), anything else fails. The nuspec's repository commit must equal the draft's `target_commitish`, and no draft of a `depends_on` package may be pending at the same commit. It then PATCHes `tag_name`, `draft=false`, `prerelease` and `make_latest = !prerelease`, and reads the tag back. The tag lands at the draft commit even if `main` moved on. `discard: true` deletes the draft, refused once the version is public. |

Facts the implementation relies on, each covered by a unit test against a faked GitHub and nuget.org:

- `releases/tags/{tag}` never returns drafts, so drafts are found by paging the full release list.
- Only HTTP 404 from `git/ref/tags/{tag}` means the tag is absent; any other failure stops the run.
- A PATCH that leaves out `tag_name` can rename a draft to `untagged-<hash>`, so finalize always sends it.
- Reads (GET) are retried twice on 5xx, 429 or a rate-limited 403, honouring `Retry-After`. Writes (POST, PATCH, DELETE) are never retried.
- The GitHub token is sent only to the configured API host, never to nuget.org or to a `Link` page on another host.

Unlike the Python actions, a refusal here is a real failure: exit 1 with one `::error::` annotation.
Usage errors exit 2. Inputs reach the binary only through environment variables, never argv or
`${{ }}` inside a script.

### How the binary is fetched

Each `action.yml` runs the same short bash bootstrap (kept identical by a unit test):

1. Map `RUNNER_OS`/`RUNNER_ARCH` to `linux-x64`, `linux-arm64`, `osx-arm64` or `win-x64`.
2. Read the expected SHA-256 for that binary from `dist/checksums.txt` **at the same ref as the
   action**. With no matching line it fails closed.
3. Use `$RUNNER_TOOL_CACHE/estate-actions/<sha256>/` if its file hashes correctly. Otherwise
   download the asset from the release named in `dist/checksums.txt`, verify the hash, and cache it.
4. `exec` the binary with the subcommand.

Binaries carry build provenance attestations:
`gh attestation verify estate-actions-linux-x64 --repo rtkelly-labs/github-actions`.

### Release commit flow

1. Commit **A** on `main` holds the `dotnet/` sources. Run the *Release binaries* workflow on `main`
   with a version. It builds the four binaries natively, attests them, and creates release
   `estate-actions-v<version>` (tagged at A) with the binaries and `checksums.txt`.
2. The same workflow opens a pin PR (commit **B**) that only rewrites `dist/checksums.txt`, recording
   `# source: A`. A PR opened with `GITHUB_TOKEN` doesn't start CI, so close and reopen it (or push to
   it) to run checks.
3. Callers pin **B** (or any later commit that keeps those checksums).

The `pinned-source` CI job fails a PR that changes `dist/checksums.txt` while `dotnet/` differs from
the recorded source commit. That catches a pin PR merged after other `dotnet/` changes landed, which
would leave a commit whose sources are newer than its binaries. Until the first release,
`dist/checksums.txt` pins nothing and the release actions refuse to run.

### Developing

```console
cd dotnet
dotnet build tests/estate-actions-tests/EstateActions.Tests.csproj -m:2
dotnet test --project tests/estate-actions-tests/EstateActions.Tests.csproj --no-build
```

The test runner is Microsoft.Testing.Platform (xunit v3), so build first and pass `--no-build`
to `dotnet test`. CI runs the Native AOT publish for every platform, with trimming and AOT
warnings as errors, then smoke-tests each binary through the composite actions. Set
`ESTATE_ACTIONS_IT_REPO` (a scratch `owner/name` you can write) and `GITHUB_TOKEN` to run the
opt-in integration test against real GitHub. It deletes only the releases and tags it creates.

### Proposal: the Python actions on the same binary

`okf-lint`, `pr-assessment`, `pr-context` and `sticky-comment` could become further subcommands.
Callers would lose the Python 3.12 runner requirement and the per-step interpreter start-up, and
the repository would have one distribution path. The cost is porting the Hypothesis/BDD suites and
the CSV fixtures to .NET, and the advisory exit-code contract must carry over unchanged. Proposed as
a follow-up, one action per PR, starting with `sticky-comment` (smallest surface, most callers).

