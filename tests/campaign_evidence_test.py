import importlib.util
import json
import tempfile
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path

spec = importlib.util.spec_from_file_location("campaign_evidence", Path(__file__).resolve().parents[1] / "build/campaign_evidence.py")
evidence = importlib.util.module_from_spec(spec)
spec.loader.exec_module(evidence)
SOURCE = "a" * 40


class CampaignEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="PeerSync-Campaign-Control-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        for shard in range(5):
            directory = self.root / str(shard); directory.mkdir()
            self.save(directory / "campaign-shard.json", dict(schema="peersync-p1-campaign-shard-v1", status="passed",
                testExitCode=0, sourceRevision=SOURCE, runId="123", runAttempt=1, shard=shard, mode="campaign",
                completedUtc=f"2026-10-20T06:00:0{shard}Z", flatCases=200, hierarchicalCases=1,
                retainedRegressions=shard == 0, trx="results.trx"))
            for filename, count, scenario in (("summary.json", 200, "real-engine-flat-path-v1"),
                                             ("hierarchical-summary.json", 1, "real-engine-hierarchical-v1")):
                self.save(directory / filename, dict(status="passed", completed=count, scenario=scenario))
            cases = [self.case(shard * 200 + number) for number in range(200)]
            (directory / "cases.jsonl").write_text("\n".join(json.dumps(row) for row in cases), encoding="utf-8")
            (directory / "hierarchical-cases.jsonl").write_text(json.dumps(self.case(10000 + shard)), encoding="utf-8")
            methods = {evidence.PREFIX + method for method in evidence.METHODS}
            if shard == 0:
                for seed in evidence.RETAINED:
                    path = directory / "regressions" / str(seed) / "result.json"
                    path.parent.mkdir(parents=True); self.save(path, self.case(seed))
                    methods.add(evidence.PREFIX + f"RetainedHierarchicalRegressionsConvergeAndPreservePublishedPayloads(seed: {seed})")
            root = ET.Element("TestRun"); result = ET.SubElement(root, "Results")
            for name in sorted(methods): ET.SubElement(result, "UnitTestResult", testName=name, outcome="Passed")
            summary = ET.SubElement(root, "ResultSummary")
            ET.SubElement(summary, "Counters", total=str(len(methods)), executed=str(len(methods)), passed=str(len(methods)), failed="0", notExecuted="0")
            ET.ElementTree(root).write(directory / "results.trx")

    @staticmethod
    def case(seed):
        return dict(Seed=seed, Nodes=3 + seed % 5, Rounds=3, Versions=9, Operations=10, Writes=7, Renames=1, Deletes=2)

    @staticmethod
    def save(path, value):
        path.write_text(json.dumps(value), encoding="utf-8")

    def change_meta(self, slot, **changes):
        path = self.root / str(slot) / "campaign-shard.json"
        value = evidence.read_json(path); value.update(changes); self.save(path, value)

    def check(self):
        return evidence.validate_day(self.root, SOURCE, "123", "campaign")

    def test_complete_day_preserves_explicit_counts(self):
        report = self.check()
        self.assertEqual(1000, report["uniqueFlatSeeds"])
        self.assertEqual(5, report["hierarchyCases"])
        self.assertEqual(3, len(report["retainedSeeds"]))
        self.assertFalse(report["gateP1Passed"])

    def test_duplicate_shard(self):
        self.change_meta(4, shard=0)
        with self.assertRaises(evidence.EvidenceError): self.check()

    def test_source_and_attempt_mixing(self):
        for change in (dict(sourceRevision="b" * 40), dict(runAttempt=2), dict(runId="124")):
            with self.subTest(change=change):
                self.change_meta(4, **change)
                with self.assertRaises(evidence.EvidenceError): self.check()
                self.change_meta(4, sourceRevision=SOURCE, runAttempt=1, runId="123")

    def test_partial_cancelled_preflight_or_bool_counts(self):
        for change in (dict(status="cancelled"), dict(mode="preflight"), dict(testExitCode=False)):
            with self.subTest(change=change):
                self.change_meta(4, **change)
                with self.assertRaises(evidence.EvidenceError): self.check()
                self.change_meta(4, status="passed", mode="campaign", testExitCode=0)

    def test_duplicate_seeds_across_shards(self):
        path = self.root / "4/cases.jsonl"; rows = path.read_text().splitlines()
        rows[0] = json.dumps(self.case(0)); path.write_text("\n".join(rows))
        with self.assertRaises(evidence.EvidenceError): self.check()

    def test_missing_retained_result(self):
        (self.root / "0/regressions/1044729930/result.json").unlink()
        with self.assertRaises(evidence.EvidenceError): self.check()

    def test_partial_trx_or_wrong_method_is_not_coverage(self):
        path = self.root / "4/results.trx"; doc = ET.parse(path)
        doc.find(".//UnitTestResult").set("testName", "UnrelatedPassingTest")
        doc.write(path)
        with self.assertRaises(evidence.EvidenceError): self.check()

    def test_path_escape_and_missing_shard(self):
        self.change_meta(4, trx="../0/results.trx")
        with self.assertRaises(evidence.EvidenceError): self.check()
        (self.root / "4/campaign-shard.json").unlink()
        with self.assertRaises(evidence.EvidenceError): self.check()

    def test_history_requires_five_distinct_consecutive_qualified_days(self):
        report = self.check(); paths = []
        for number in range(5):
            row = dict(report, runId=str(123 + number), completedUtc=f"2026-10-{20+number}T06:00:04+00:00", date=f"2026-10-{20+number}")
            path = self.root / f"day-{number}.json"; self.save(path, row); paths.append(path)
        self.assertEqual(5, evidence.validate_history(paths, SOURCE)["campaignDays"])
        for change in (dict(runId="123"), dict(date="2026-10-26", completedUtc="2026-10-26T06:00:04Z"),
                       dict(qualifiedCampaignDay=False), dict(sourceRevision="b" * 40), dict(uniqueFlatSeeds=999)):
            with self.subTest(change=change):
                row = evidence.read_json(paths[4]); original = dict(row); row.update(change); self.save(paths[4], row)
                with self.assertRaises(evidence.EvidenceError): evidence.validate_history(paths, SOURCE)
                self.save(paths[4], original)


if __name__ == "__main__":
    unittest.main()
