"""Action entry point. Assessment codes are data, never the process exit status."""

from __future__ import annotations

import argparse
import hashlib
import os
import sys
from pathlib import Path

from estate_actions.assessment import Policy, collect, judge, measure, verify_packet
from estate_actions.data import Invalid, canonical, decode
from estate_actions.github import GitHub
from estate_actions.report import Report, publish, render, safe, session_body


def emit(outputs: dict[str, str]) -> None:
    target = os.environ.get("GITHUB_OUTPUT")
    if target:
        with Path(target).open("a", encoding="utf-8", newline="\n") as stream:
            for key, value in outputs.items():
                delimiter = "ESTATE_OUTPUT"
                while delimiter in value.splitlines():
                    delimiter += "_"
                stream.write(f"{key}<<{delimiter}\n{value}\n{delimiter}\n")


def policy_from_environment() -> Policy:
    path = os.environ.get("POLICY_FILE", "")
    return Policy.parse(decode(Path(path).read_text(encoding="utf-8"))) if path else Policy()


def run(mode: str) -> int:
    directory = Path(os.environ.get("REPORT_DIRECTORY") or os.environ.get("RUNNER_TEMP") or "temp")
    directory = directory / "estate-actions"
    directory.mkdir(parents=True, exist_ok=True)
    report = Report("## PR scope assessment\n\nNEEDS_REVIEW: assessment did not complete.\n", 2)
    client: GitHub | None = None
    number = 0
    extras: dict[str, str] = {}
    session = os.environ.get("SESSION", "pr-scope-assessment")
    slot = "unbound"
    try:
        if mode in ("prepare", "sticky"):
            number = int(os.environ.get("PR_NUMBER", "0"))
            if number < 1:
                raise Invalid("A positive PR number is required")
            client = GitHub(
                os.environ.get("GH_TOKEN", ""),
                os.environ.get("REPOSITORY") or os.environ.get("GITHUB_REPOSITORY", ""),
                os.environ.get("GITHUB_API_URL", "https://api.github.com"),
            )
            slot = hashlib.sha256(f"{client.repository}:{number}:{session}".encode()).hexdigest()[:16]
        if mode == "sticky":
            code = int(os.environ.get("REPORT_ERROR_CODE", "0"))
            report = Report(Path(os.environ["MARKDOWN_FILE"]).read_text(encoding="utf-8"), code)
        elif mode == "prepare" and client:
            packet = collect(client, number, policy_from_environment())
            packet_file = directory / f"context-{number}-{slot}.json"
            packet_file.write_text(canonical(packet) + "\n", encoding="utf-8")
            extras = {"context-file": str(packet_file.resolve()), "evidence-key": str(packet["evidence_key"])}
            emit({**extras, "error-code": "0"})
            return 0
        else:
            context_file = Path(os.environ["CONTEXT_FILE"])
            packet = verify_packet(decode(context_file.read_text(encoding="utf-8")))
            number = int(str(packet["number"]))
            slot = hashlib.sha256(f"{packet['repository']}:{number}:{session}".encode()).hexdigest()[:16]
            extras = {
                "context-file": str(context_file.resolve()),
                "evidence-key": str(packet["evidence_key"]),
            }
            judgment = None
            issue = ""
            judgment_file = os.environ.get("ASSESSMENT_FILE", "")
            if mode == "assess" and judgment_file:
                try:
                    judgment = judge(decode(Path(judgment_file).read_text(encoding="utf-8")), packet)
                except (Invalid, OSError) as error:
                    issue = (
                        str(error)
                        if isinstance(error, Invalid)
                        else "Cannot read the supplied assessment file"
                    )
            report = render(packet, measure(packet), judgment, issue)
    except (Invalid, ValueError, OSError, KeyError) as error:
        message = str(error) if isinstance(error, Invalid) else "Invalid or inaccessible action input"
        report = Report(f"## PR scope assessment\n\n**NEEDS_REVIEW**: {safe(message)}\n", 2)

    try:
        body = session_body(session, report.markdown)
        if mode == "sticky" and client and number > 0:
            # Recheck before publishing: do not replace newer evidence with an obsolete run.
            context_path = os.environ.get("CONTEXT_FILE", "")
            if context_path:
                expected = verify_packet(decode(Path(context_path).read_text(encoding="utf-8")))
                current = collect(client, number, Policy.parse(expected["policy"]))
                if current["evidence_key"] != expected["evidence_key"]:
                    raise Invalid("PR changed before publication; rerun the assessment")
            extras["comment-id"] = str(
                publish(client, number, body, os.environ.get("COMMENT_AUTHOR", "github-actions[bot]"))
            )
    except (Invalid, OSError) as error:
        message = str(error) if isinstance(error, Invalid) else "Sticky publication failed"
        report = Report(
            report.markdown + f"\n**Publication failed:** {message}\n", 3, report.size, report.decision
        )
        body = report.markdown
    markdown_file = directory / f"report-{number}-{slot}.md"
    markdown_file.write_text(body, encoding="utf-8")
    (directory / f"error-code-{number}-{slot}.txt").write_text(
        str(report.error_code) + "\n", encoding="utf-8"
    )
    emit(
        {
            **extras,
            "markdown": body,
            "markdown-file": str(markdown_file.resolve()),
            "error-code": str(report.error_code),
            "size": report.size,
            "decision": report.decision,
        }
    )
    print(f"Assessment: {report.decision}; size: {report.size}; advisory error code: {report.error_code}")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=("prepare", "assess", "sticky"))
    return run(parser.parse_args().mode)


if __name__ == "__main__":
    sys.exit(main())
