"""Exercise the actual allocation expressions, including cached planner outputs."""
import ast
import json
import re
import unittest
from pathlib import Path


def job_expression(workflow, job):
    current = None; expression = None
    for line in workflow.read_text(encoding="utf-8").splitlines():
        match = re.fullmatch(r"  ([a-z_]+):", line)
        if match: current = match[1]
        if current == job and line.startswith("    if: "): expression = line[8:].strip()
    if expression is None: raise ValueError("Missing job allocation guard")
    return expression


def allocation(job, *, attempt="1", mode="campaign", event="workflow_dispatch", active="true", source="a" * 40, last="10", run=10):
    workflow = Path(__file__).resolve().parents[1] / ".github/workflows/p1-campaign.yml"
    expression = job_expression(workflow, job)
    expression = expression.removeprefix("${{").removesuffix("}}").strip().replace("&&", " and ").replace("||", " or ")
    context = dict(github=dict(event_name=event, run_attempt=attempt, run_number=run), inputs=dict(mode=mode if event == "workflow_dispatch" else ""),
                   vars=dict(PEERSYNC_P1_SOAK_SOURCE=source, PEERSYNC_P1_SOAK_LAST_RUN=last), needs=dict(plan=dict(outputs=dict(active=active, mode=mode))))
    def value(node):
        if isinstance(node, ast.Constant): return node.value
        if isinstance(node, ast.Name): return context[node.id]
        if isinstance(node, ast.Attribute) and not node.attr.startswith("_"): return value(node.value)[node.attr]
        if isinstance(node, ast.BoolOp):
            return all(value(n) for n in node.values) if isinstance(node.op, ast.And) else any(value(n) for n in node.values)
        if isinstance(node, ast.Compare) and len(node.ops) == 1:
            left, right = value(node.left), value(node.comparators[0]); op = node.ops[0]
            if isinstance(op, ast.Eq): return left == right
            if isinstance(op, ast.NotEq): return left != right
            if isinstance(op, ast.LtE): return left <= right
        if isinstance(node, ast.Call) and isinstance(node.func, ast.Name) and not node.keywords:
            if node.func.id == "always" and not node.args: return True
            if node.func.id == "fromJSON" and len(node.args) == 1: return json.loads(value(node.args[0]))
        raise ValueError("Allocation control encountered an unsupported expression")
    return value(ast.parse(expression, mode="eval").body)


class CampaignAllocationTests(unittest.TestCase):
    def test_campaign_rerun_cannot_allocate_plan(self):
        self.assertFalse(allocation("plan", attempt="2"))

    def test_cached_plan_cannot_allocate_campaign_shards_or_report(self):
        for job in ("shards", "report"):
            with self.subTest(job=job): self.assertFalse(allocation(job, attempt="2", active="true"))

    def test_first_campaign_attempt_can_allocate_all_jobs(self):
        for job in ("plan", "shards", "report"):
            with self.subTest(job=job): self.assertTrue(allocation(job))

    def test_manual_preflight_rerun_can_allocate_non_gating_jobs(self):
        for job in ("plan", "shards", "report"):
            with self.subTest(job=job): self.assertTrue(allocation(job, attempt="2", mode="preflight"))

    def test_scheduled_rerun_and_inactive_schedule_do_not_allocate(self):
        for job in ("plan", "shards", "report"):
            with self.subTest(job=job):
                self.assertFalse(allocation(job, attempt="2", event="schedule"))
                self.assertFalse(allocation(job, event="schedule", source="", last="", active="false"))

    def test_expired_schedule_cannot_allocate_plan(self):
        self.assertFalse(allocation("plan", event="schedule", run=11))

    def test_hosted_probe_uses_exact_production_allocation_conditions(self):
        directory = Path(__file__).resolve().parents[1] / ".github/workflows"
        for job in ("plan", "shards", "report"):
            with self.subTest(job=job):
                self.assertEqual(job_expression(directory / "p1-campaign.yml", job),
                                 job_expression(directory / "p1-allocation-probe.yml", job))

    def test_hosted_probe_is_manual_linux_only_with_one_minute_jobs(self):
        path = Path(__file__).resolve().parents[1] / ".github/workflows/p1-allocation-probe.yml"
        text = path.read_text(encoding="utf-8")
        self.assertIn("  workflow_dispatch:", text)
        self.assertNotRegex(text, re.compile(r"^  (?:schedule|push|pull_request):", re.M))
        self.assertEqual(re.findall(r"^    runs-on: (.+)$", text, re.M), ["ubuntu-latest"] * 3)
        self.assertEqual(re.findall(r"^    timeout-minutes: (.+)$", text, re.M), ["1"] * 3)
        self.assertNotIn("uses:", text)
        self.assertNotIn("Run-CampaignShard", text)
        self.assertNotIn("gh variable", text)


if __name__ == "__main__": unittest.main()
