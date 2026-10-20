"""Plan an explicitly bounded P1 campaign before allocating Windows runners."""
import json
import os
import re
import subprocess
from urllib.parse import quote

REQUIRED_CHECKS = {"build (win-x64)", "build (win-arm64)", "fuzz-smoke",
                   "service-security (windows-2022)", "service-security (windows-2025)"}


def plan(env, inspect_source):
    mode = "campaign" if env.get("GITHUB_EVENT_NAME") == "schedule" else env.get("SOAK_MODE", "preflight")
    if mode not in ("campaign", "preflight"):
        raise ValueError("Unknown campaign mode")
    run = int(env["GITHUB_RUN_NUMBER"])
    attempt = int(env.get("GITHUB_RUN_ATTEMPT", "1"))
    if mode == "campaign":
        bound = env.get("SOAK_LAST_RUN", "")
        if not bound.isdecimal() or not run <= int(bound) <= run + 4 or attempt != 1:
            raise ValueError("Campaign needs a five-attempt run bound; reruns cannot extend it")
        source = env.get("SOAK_PINNED_SOURCE", "")
        if env.get("SOAK_REQUESTED_SOURCE") and env["SOAK_REQUESTED_SOURCE"].lower() != source.lower():
            raise ValueError("Manual campaign source differs from its activation pin")
    else:
        source = env.get("SOAK_REQUESTED_SOURCE") or env.get("SOAK_PINNED_SOURCE") or env.get("GITHUB_SHA", "")
    if not re.fullmatch(r"[0-9a-fA-F]{40}", source):
        raise ValueError("An immutable full source SHA is required")
    source = source.lower()
    facts = inspect_source(source, mode == "campaign")
    if facts.get("sha") != source:
        raise ValueError("Source commit does not exist in this repository")
    if mode == "campaign" and (not facts.get("merged") or not REQUIRED_CHECKS.issubset(set(facts.get("successfulChecks", [])))):
        raise ValueError("Campaign activation requires a merged source with all five CI checks green")
    return dict(active="true", mode=mode, source=source, cases=200 if mode == "campaign" else 2,
                lastPermittedRun=env.get("SOAK_LAST_RUN", ""), gateP1Passed=False)


def github_source(env):
    repository = env["GITHUB_REPOSITORY"]
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository):
        raise ValueError("Invalid repository context")

    def api(path):
        result = subprocess.run(["gh", "api", f"repos/{repository}/{path}"], check=True, capture_output=True, text=True)
        return json.loads(result.stdout)

    def inspect(source, campaign):
        commit = api(f"commits/{source}")
        facts = dict(sha=commit["sha"])
        if campaign:
            branch = quote(env.get("SOAK_DEFAULT_BRANCH", "main"), safe="")
            comparison = api(f"compare/{source}...{branch}")
            checks = api(f"commits/{source}/check-runs?per_page=100&filter=latest")
            if checks["total_count"] > 100:
                raise ValueError("Too many source check runs to establish bounded latest evidence")
            facts.update(merged=comparison["status"] in ("ahead", "identical"),
                         successfulChecks=[check["name"] for check in checks["check_runs"]
                                           if check["status"] == "completed" and check["conclusion"] == "success"
                                           and check.get("app", {}).get("slug") == "github-actions"])
        return facts
    return inspect


if __name__ == "__main__":
    result = plan(os.environ, github_source(os.environ))
    print(json.dumps(result))
    with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
        for name in ("active", "mode", "source", "cases"):
            output.write(f"{name}={result[name]}\n")
