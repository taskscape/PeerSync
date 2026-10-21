import importlib.util
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location("campaign_plan", Path(__file__).resolve().parents[1] / "build/campaign_plan.py")
campaign = importlib.util.module_from_spec(spec); spec.loader.exec_module(campaign)
SHA = "a" * 40


class CampaignPlanTests(unittest.TestCase):
    def setUp(self):
        self.env = dict(GITHUB_EVENT_NAME="workflow_dispatch", GITHUB_RUN_NUMBER="11", GITHUB_RUN_ATTEMPT="1",
                        GITHUB_SHA=SHA, SOAK_MODE="campaign", SOAK_LAST_RUN="12", SOAK_PINNED_SOURCE=SHA)

    @staticmethod
    def good(source, required):
        return dict(sha=source, merged=True, successfulChecks=sorted(campaign.REQUIRED_CHECKS))

    def test_campaign_pins_source_and_two_attempt_bound(self):
        result = campaign.plan(self.env, self.good)
        self.assertEqual(200, result["cases"])
        self.assertEqual(SHA, result["source"])
        self.assertFalse(result["gateP1Passed"])

    def test_final_permitted_attempt_preserves_source_and_scope(self):
        result = campaign.plan(dict(self.env, SOAK_LAST_RUN="11"), self.good)
        self.assertEqual("11", result["lastPermittedRun"])
        self.assertEqual(SHA, result["source"])
        self.assertEqual(200, result["cases"])

    def test_missing_expired_excessive_bound_or_rerun_is_rejected(self):
        for changes in (dict(SOAK_LAST_RUN=""), dict(SOAK_LAST_RUN="10"), dict(SOAK_LAST_RUN="13"), dict(GITHUB_RUN_ATTEMPT="2")):
            with self.subTest(changes=changes), self.assertRaises(ValueError): campaign.plan(dict(self.env, **changes), self.good)

    def test_unmerged_or_non_green_source_is_rejected(self):
        for facts in (dict(sha=SHA, merged=False, successfulChecks=campaign.REQUIRED_CHECKS),
                      dict(sha=SHA, merged=True, successfulChecks=["build (win-x64)"])):
            with self.subTest(facts=facts), self.assertRaises(ValueError): campaign.plan(self.env, lambda *_: facts)

    def test_source_override_cannot_change_active_campaign(self):
        with self.assertRaises(ValueError): campaign.plan(dict(self.env, SOAK_REQUESTED_SOURCE="b" * 40), self.good)

    def test_preflight_has_small_non_gating_scope_without_activation(self):
        result = campaign.plan(dict(self.env, SOAK_MODE="preflight", SOAK_LAST_RUN="", SOAK_PINNED_SOURCE=""), self.good)
        self.assertEqual(2, result["cases"])
        self.assertEqual("preflight", result["mode"])

    def test_scheduled_event_cannot_select_preflight_to_bypass_bound(self):
        with self.assertRaises(ValueError): campaign.plan(dict(self.env, GITHUB_EVENT_NAME="schedule", SOAK_MODE="preflight", SOAK_LAST_RUN=""), self.good)


if __name__ == "__main__": unittest.main()
