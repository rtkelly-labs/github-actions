from estate_actions.assessment import Policy, make_packet


def file(path="src/main.py", content="hello"):
    return {
        "filename": path,
        "status": "added",
        "additions": 1,
        "deletions": 0,
        "sha": "c" * 40,
        "patch": f"@@ -0,0 +1 @@\n+{content}",
    }


def packet(files=None, policy=None):
    files = files if files is not None else [file()]
    return make_packet(
        "example/repo",
        42,
        {
            "base": {"sha": "a" * 40, "ref": "main"},
            "head": {"sha": "b" * 40, "ref": "feature"},
            "title": "A coherent change",
            "body": "Example evidence",
            "changed_files": len(files),
        },
        files,
        policy or Policy(),
    )


def judgment(context, decision="KEEP_TOGETHER"):
    return {
        "schema_version": 1,
        "evidence_key": context["evidence_key"],
        "agent": "bring-your-own-agent/example-v1",
        "decision": decision,
        "rationale": "Implementation and tests establish one behavior.",
        "inspected_paths": [item["path"] for item in context["files"]],
        "stacking": {"choice": "unnecessary", "reason": "No independently useful prerequisite layer."},
        "layers": [],
    }
