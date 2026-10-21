"""Validate generated P1 campaign artifacts; never execute artifact contents."""
import argparse
import datetime as dt
import json
import re
import xml.etree.ElementTree as ET
from pathlib import Path

SCOPE = json.loads(Path(__file__).with_name("simulation-scope.json").read_text(encoding="utf-8"))
if SCOPE.get("schema") != "peersync-simulation-scope-v1":
    raise ValueError("Unknown simulation scope registry")
RETAINED = set(SCOPE["retainedSeeds"])
SHA = re.compile(r"[0-9a-fA-F]{40}\Z")
PREFIX = SCOPE["prefix"]
METHODS = set(SCOPE["baselineMethods"])


class EvidenceError(ValueError):
    pass


def require(value, message):
    if not value:
        raise EvidenceError(message)


def integer(value, label, minimum=0):
    require(type(value) is int and value >= minimum, f"Invalid integer: {label}")
    return value


def timestamp(value):
    require(isinstance(value, str), "Missing UTC completion time")
    parsed = dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
    require(parsed.tzinfo is not None and parsed.utcoffset() == dt.timedelta(), "Completion time must be UTC")
    return parsed


def bounded_file(root, relative):
    path = (root / relative).resolve()
    require(path.is_relative_to(root.resolve()), "Artifact path leaves its evidence directory")
    require(path.is_file() and path.stat().st_size <= 10 * 1024 * 1024, "Missing/oversized artifact file")
    return path


def read_json(path):
    require(path.stat().st_size <= 10 * 1024 * 1024, "Oversized JSON artifact")
    value = json.loads(path.read_text(encoding="utf-8-sig"))
    require(isinstance(value, dict), "JSON artifact must be an object")
    return value


def read_cases(root, name, count):
    rows = [json.loads(line) for line in bounded_file(root, name).read_text(encoding="utf-8-sig").splitlines() if line.strip()]
    require(len(rows) == count, f"Wrong completed case count: {name}")
    for row in rows:
        require(isinstance(row, dict), "Case row must be an object")
        seed = integer(row.get("Seed"), "Seed")
        require(seed <= 2147483647, "Seed exceeds the replay range")
        require(3 <= integer(row.get("Nodes"), "Nodes") <= 7, "Case node count outside 3..7")
        require(1 <= integer(row.get("Rounds"), "Rounds") <= 24, "Case did not complete bounded healing")
        integer(row.get("Versions"), "Versions", 1)
        for field in ("Operations", "Writes", "Renames", "Deletes"):
            integer(row.get(field), field)
    require(len({row["Seed"] for row in rows}) == count, "Duplicate seed within a shard")
    return rows


def validate_day(root, source, run_id, mode):
    require(SHA.fullmatch(source) is not None, "Expected source must be a full Git SHA")
    require(str(run_id).isdigit(), "Expected workflow run ID must be numeric")
    require(mode in ("campaign", "preflight"), "Unknown campaign mode")
    manifests = sorted(Path(root).rglob("campaign-shard.json"))
    require(len(manifests) == 5, "Exactly five shard manifests are required")
    flat, hierarchy, retained, shards, completions = [], [], set(), set(), []
    per_shard = 200 if mode == "campaign" else 2
    attempts = set(); maintenance_retired = 0; maintenance_deleted = 0
    for manifest in manifests:
        directory = manifest.parent
        meta = read_json(manifest)
        require(meta.get("schema") == "peersync-p1-campaign-shard-v1", "Unknown shard schema")
        require(meta.get("status") == "passed" and integer(meta.get("testExitCode"), "testExitCode") == 0, "Shard is incomplete or failed")
        require(meta.get("sourceRevision") == source and str(meta.get("runId")) == str(run_id), "Mixed source/run evidence")
        require(meta.get("mode") == mode, "Mixed preflight/campaign evidence")
        if mode == "campaign":
            require(meta.get("workingTreeDirty") is False, "Campaign source checkout is dirty or unproven")
        require(re.fullmatch(r"[0-9a-fA-F]{64}", meta.get("binarySha256", "")) is not None, "Missing executable hash provenance")
        shard = integer(meta.get("shard"), "shard")
        require(shard in range(5) and shard not in shards, "Duplicate/invalid shard")
        shards.add(shard); attempt = integer(meta.get("runAttempt"), "runAttempt", 1)
        if mode == "campaign": require(attempt == 1, "Campaign evidence requires first workflow attempt")
        attempts.add(attempt)
        completions.append(timestamp(meta.get("completedUtc")))
        require(integer(meta.get("flatCases"), "flatCases") == per_shard and integer(meta.get("hierarchicalCases"), "hierarchicalCases") == 1, "Wrong requested shard scope")
        with_retained = mode == "campaign" and shard == 0
        require(meta.get("retainedRegressions") is with_retained, "Wrong retained-regression ownership")
        for filename, expected, scenario in (("summary.json", per_shard, "real-engine-flat-path-v1"),
                                             ("hierarchical-summary.json", 1, "real-engine-hierarchical-v1")):
            summary = read_json(bounded_file(directory, filename))
            require(summary.get("status") == "passed" and integer(summary.get("completed"), "completed") == expected
                    and summary.get("scenario") == scenario, "Missing successful generated summary")
        flat.extend(read_cases(directory, "cases.jsonl", per_shard))
        hierarchy.extend(read_cases(directory, "hierarchical-cases.jsonl", 1))
        maintenance = read_json(bounded_file(directory, "connected-maintenance.json"))
        require(maintenance.get("status") == "passed" and maintenance.get("stage") == "passed"
                and maintenance.get("scenario") == "connected-maintenance-v1", "Missing successful connected maintenance")
        require(integer(maintenance.get("nodes"), "maintenance nodes") == 3
                and integer(maintenance.get("daysAdvanced"), "maintenance days") == 64
                and integer(maintenance.get("publishedVersions"), "maintenance versions") == 2, "Incomplete maintenance scope")
        retired = integer(maintenance.get("retiredArchives"), "retired archives", 2)
        deleted = integer(maintenance.get("deletedBlocks"), "deleted blocks", 2)
        binary = maintenance.get("testBinarySha256")
        require(isinstance(binary, str) and binary.lower() == meta["binarySha256"].lower(), "Maintenance binary differs from its shard")
        if mode == "campaign":
            version = maintenance.get("sourceInformationalVersion")
            require(isinstance(version, str) and version.lower().endswith("+" + source.lower()), "Maintenance executable source differs from campaign pin")
        maintenance_retired += retired; maintenance_deleted += deleted
        trx = ET.parse(bounded_file(directory, meta.get("trx", "results.trx")))
        counters = trx.find(".//{*}Counters")
        expected_tests = len(METHODS) + (len(RETAINED) if with_retained else 0)
        require(counters is not None, "Missing test counters")
        require(all(counters.get(field) == str(expected_tests) for field in ("total", "executed", "passed")), "Incomplete executed test scope")
        require(all(counters.get(field, "0") == "0" for field in ("failed", "error", "timeout", "aborted", "notExecuted", "inconclusive")), "Failed/skipped test scope")
        names = {PREFIX + method for method in METHODS}
        if with_retained:
            names.update(PREFIX + f"RetainedHierarchicalRegressionsConvergeAndPreservePublishedPayloads(seed: {seed})" for seed in RETAINED)
        executed = trx.findall(".//{*}UnitTestResult")
        require(len(executed) == expected_tests and {row.get("testName") for row in executed} == names
                and all(row.get("outcome") == "Passed" for row in executed), "Wrong executed test methods")
        if with_retained:
            for seed in RETAINED:
                result = read_json(bounded_file(directory, f"regressions/{seed}/result.json"))
                require(result.get("Seed") == seed and 3 <= result.get("Nodes", 0) <= 7
                        and 1 <= result.get("Rounds", 0) <= 24 and result.get("Versions", 0) > 0, "Missing retained regression")
                retained.add(seed)
    require(len(attempts) == 1, "Mixed workflow rerun attempts")
    require(len({row["Seed"] for row in flat}) == per_shard * 5, "Duplicate flat seed across shards")
    require(len({row["Seed"] for row in hierarchy}) == 5, "Duplicate hierarchy sample across shards")
    require(retained == (RETAINED if mode == "campaign" else set()), "Incomplete retained regressions")
    node_counts = sorted({row["Nodes"] for row in flat})
    totals = {field: sum(row[field] for row in flat) for field in ("Operations", "Writes", "Renames", "Deletes", "Versions")}
    if mode == "campaign":
        require(node_counts == [3, 4, 5, 6, 7], "Campaign omitted node-count coverage")
        require(all(totals[field] > 0 for field in ("Writes", "Renames", "Deletes")), "Campaign omitted mutation coverage")
    completed = max(completions)
    return dict(schema="peersync-p1-campaign-day-v1", status="passed", mode=mode,
                sourceRevision=source, runId=str(run_id), runAttempt=attempts.pop(),
                completedUtc=completed.isoformat(), date=completed.date().isoformat(),
                flatSeeds=len(flat), uniqueFlatSeeds=len({row["Seed"] for row in flat}),
                hierarchyCases=len(hierarchy), retainedSeeds=sorted(retained), shards=5,
                maintenanceCases=5, maintenanceRetiredArchives=maintenance_retired, maintenanceDeletedBlocks=maintenance_deleted,
                nodeCounts=node_counts, totals=totals, qualifiedCampaignDay=mode == "campaign", gateP1Passed=False)


def validate_history(paths, source):
    require(len(paths) == 2 and SHA.fullmatch(source) is not None, "History requires two reports and one pinned source")
    reports = [read_json(Path(path)) for path in paths]
    for report in reports:
        require(integer(report.get("runAttempt"), "history runAttempt", 1) == 1, "Campaign history requires first workflow attempt")
        require(report.get("schema") == "peersync-p1-campaign-day-v1" and report.get("status") == "passed"
                and report.get("mode") == "campaign" and report.get("qualifiedCampaignDay") is True, "Non-campaign/failed day")
        require(report.get("sourceRevision") == source and report.get("flatSeeds") == 1000
                and report.get("uniqueFlatSeeds") == 1000 and report.get("hierarchyCases") == 5
                and report.get("shards") == 5 and set(report.get("retainedSeeds", [])) == RETAINED, "Incomplete/mixed-source campaign day")
        require(report.get("nodeCounts") == [3, 4, 5, 6, 7], "Incomplete node coverage")
        require(report.get("maintenanceCases") == 5 and integer(report.get("maintenanceRetiredArchives"), "history retired archives", 10) >= 10
                and integer(report.get("maintenanceDeletedBlocks"), "history deleted blocks", 10) >= 10, "Incomplete maintenance history")
        require(timestamp(report.get("completedUtc")).date().isoformat() == report.get("date"), "Completion date mismatch")
    require(len({report.get("runId") for report in reports}) == 2, "A rerun is not a new campaign day")
    dates = sorted(dt.date.fromisoformat(report["date"]) for report in reports)
    require(len(set(dates)) == 2 and all((right - left).days == 1 for left, right in zip(dates, dates[1:])), "Campaign dates are not consecutive")
    return dict(status="passed", sourceRevision=source, dates=[day.isoformat() for day in dates],
                campaignDays=2, flatSeedsPerDay=1000, gateP1Passed=False,
                note="Artifact audit only; verify authoritative GitHub run/attempt conclusions before ticking the gate.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", required=True)
    parser.add_argument("--output", required=True, type=Path)
    choice = parser.add_mutually_exclusive_group(required=True)
    choice.add_argument("--day", type=Path)
    choice.add_argument("--history", nargs="+", type=Path)
    parser.add_argument("--run-id")
    parser.add_argument("--mode", choices=("preflight", "campaign"), default="preflight")
    args = parser.parse_args()
    try:
        report = validate_day(args.day, args.source, args.run_id, args.mode) if args.day else validate_history(args.history, args.source)
    except (ValueError, OSError, ET.ParseError, TypeError, KeyError) as error:
        report = dict(status="failed", error=str(error), gateP1Passed=False)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report))
    return 0 if report["status"] == "passed" else 1


if __name__ == "__main__":
    raise SystemExit(main())
