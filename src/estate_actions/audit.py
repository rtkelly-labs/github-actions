"""Read-only estate audit. Run with uv: PYTHONPATH=src uv run python -m estate_actions.audit."""

from __future__ import annotations

import argparse
import os
import subprocess
import time
from concurrent.futures import ThreadPoolExecutor
from datetime import UTC, datetime
from pathlib import Path

from estate_actions.assessment import Policy, collect, make_packet, verify_packet
from estate_actions.data import Invalid, canonical, decode, integer, mapping, sequence, string
from estate_actions.github import GitHub
from estate_actions.heuristic import classify

QUERY = """
query($owner:String!,$name:String!,$cursor:String,$states:[PullRequestState!]) {
  repository(owner:$owner,name:$name) {
    defaultBranchRef { name }
    pullRequests(first:100,after:$cursor,states:$states,orderBy:{field:CREATED_AT,direction:ASC}) {
      pageInfo { hasNextPage endCursor }
      nodes {
        number title body url state createdAt mergedAt closedAt isDraft changedFiles
        baseRefName headRefName baseRefOid headRefOid
        author { login }
        stack { id number size }
      }
    }
  }
}
"""


def repositories(client: GitHub, owners: list[str]) -> list[str]:
    repos: set[str] = set()
    # Authenticated discovery includes owned private repositories, unlike GET /users/{owner}/repos.
    for row in client.pages("/user/repos?affiliation=owner,collaborator,organization_member"):
        if string(mapping(row["owner"])["login"]).lower() in {owner.lower() for owner in owners}:
            repos.add(string(row["full_name"]))
    for owner in owners:
        # Organization enumeration also includes accessible public repositories not in /user/repos.
        account = mapping(client.request("GET", f"/users/{owner}"))
        kind = string(account.get("type"))
        if kind not in {"User", "Organization"}:
            raise Invalid(f"Unsupported owner account type for {owner}")
        route = f"/orgs/{owner}/repos" if kind == "Organization" else f"/users/{owner}/repos"
        for row in client.pages(route):
            repos.add(string(row["full_name"]))
    return sorted(repos, key=str.lower)


def pulls(client: GitHub, repo: str, states: str) -> list[dict[str, object]]:
    owner, name = repo.split("/", 1)
    cursor: object = None
    result: list[dict[str, object]] = []
    while True:
        response: dict[str, object] = {}
        for attempt in range(3):
            response = mapping(
                client.request(
                    "POST",
                    "/graphql",
                    {
                        "query": QUERY,
                        "variables": {
                            "owner": owner,
                            "name": name,
                            "cursor": cursor,
                            "states": ["OPEN"] if states == "open" else ["OPEN", "CLOSED", "MERGED"],
                        },
                    },
                )
            )
            if not response.get("errors"):
                break
            if attempt < 2:
                time.sleep(attempt + 1)
        if response.get("errors"):
            raise Invalid(f"GraphQL failed for {repo}; stack state or PR discovery is incomplete")
        repository = mapping(mapping(response["data"])["repository"])
        connection = mapping(repository["pullRequests"])
        for raw in sequence(connection["nodes"]):
            row = mapping(raw)
            row["observed_at_utc"] = datetime.now(UTC).isoformat()
            row["repository"] = repo
            row["default_branch"] = mapping(repository.get("defaultBranchRef") or {}).get("name", "")
            result.append(row)
        page = mapping(connection["pageInfo"])
        if not page["hasNextPage"]:
            return result
        cursor = page["endCursor"]


def gather_one(client: GitHub, row: dict[str, object], cache: Path) -> dict[str, object]:
    repo = string(row["repository"])
    number = integer(row["number"])
    stack = mapping(row.get("stack") or {})
    result: dict[str, object] = {
        "repository": repo,
        "number": number,
        "url": row["url"],
        "state": row["state"],
        "is_draft": row["isDraft"],
        "author": mapping(row.get("author") or {}).get("login", ""),
        "created_at": row["createdAt"],
        "merged_at": row["mergedAt"] or "",
        "closed_at": row["closedAt"] or "",
        "base_ref": row["baseRefName"],
        "head_ref": row["headRefName"],
        "in_stack": bool(stack),
        "stack_number": stack.get("number", ""),
        "stack_id": stack.get("id", ""),
        "stack_size": stack.get("size", ""),
        "stack_evidence": "native_membership" if stack else "no_native_membership_observed",
        "stack_observed_at_utc": row["observed_at_utc"],
    }
    try:
        location = cache / repo / f"{number}.json"
        client = GitHub(client.token, repo, client.api_url)
        # Cache only immutable evidence with exact refs/title; refresh open PRs for current changes.
        packet: dict[str, object] | None = None
        if location.exists() and row["state"] != "OPEN":
            saved = verify_packet(decode(location.read_text(encoding="utf-8")))
            if (saved.get("base_sha"), saved.get("head_sha"), saved.get("title")) == (
                row["baseRefOid"],
                row["headRefOid"],
                row["title"],
            ):
                packet = saved
        if packet is None:
            if row["state"] == "OPEN":
                packet = collect(client, number, Policy())
            else:
                metadata = {
                    "base": {"sha": row["baseRefOid"], "ref": row["baseRefName"]},
                    "head": {"sha": row["headRefOid"], "ref": row["headRefName"]},
                    "title": row["title"],
                    "body": row["body"],
                    "changed_files": row["changedFiles"],
                }
                packet = make_packet(repo, number, metadata, client.pages(f"pulls/{number}/files"), Policy())
            location.parent.mkdir(parents=True, exist_ok=True)
            location.write_text(canonical(packet) + "\n", encoding="utf-8")
        result["packet_file"] = location.relative_to(cache.parent).as_posix()
        result["packet_key"] = packet["evidence_key"]
        result["status"] = "GATHERED"
        groups: dict[str, list[str]] = {}
        for item in sequence(packet["files"]):
            path = string(mapping(item)["path"])
            groups.setdefault(classify(path).name, []).append(path)
        result["file_groups"] = groups
    except (Invalid, OSError, ValueError, KeyError) as error:
        result["status"] = "UNASSESSED"
        result["error"] = str(error) if isinstance(error, Invalid) else type(error).__name__
    return result


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--owners", nargs="+", default=["rtkelly13", "Resultful", "rtkelly-labs"])
    parser.add_argument("--states", choices=("open", "all"), default="all")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    token = os.environ.get("GH_TOKEN") or subprocess.check_output(["gh", "auth", "token"], text=True).strip()
    client = GitHub(token, "rtkelly-labs/github-actions")
    output: Path = args.output
    output.mkdir(parents=True, exist_ok=True)
    repos = repositories(client, args.owners)
    rows: list[dict[str, object]] = []
    coverage: list[dict[str, object]] = []
    for repo in repos:
        try:
            values = pulls(client, repo, args.states)
            rows.extend(values)
            coverage.append({"repository": repo, "status": "DISCOVERED", "prs": len(values)})
            print(f"Discovered {repo}: {len(values)} PRs", flush=True)
        except Invalid as error:
            coverage.append({"repository": repo, "status": "UNASSESSED", "error": str(error)})
    (output / "coverage.json").write_text(canonical(coverage) + "\n", encoding="utf-8")
    results: list[dict[str, object]] = []
    with ThreadPoolExecutor(max_workers=3) as executor:
        gathered = executor.map(lambda row: gather_one(client, row, output / "cache"), rows)
        for index, result in enumerate(gathered, 1):
            results.append(result)
            if index % 25 == 0 or index == len(rows):
                print(f"Gathered {index}/{len(rows)}", flush=True)
    snapshot = {
        "schema_version": 1,
        "completed_at_utc": datetime.now(UTC).isoformat(),
        "coverage": coverage,
        "pull_requests": results,
    }
    (output / "snapshot.json").write_text(canonical(snapshot) + "\n", encoding="utf-8")
    print(f"Wrote frozen snapshot with {len(results)} PRs to {output / 'snapshot.json'}")


if __name__ == "__main__":
    main()
