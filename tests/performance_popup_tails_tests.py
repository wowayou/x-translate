"""Synthetic arithmetic/file-boundary tests; no product, SDK, HTTP or real performance measurement."""
import contextlib
from fractions import Fraction
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import random
import tempfile
import unittest

TOOL_PATH = Path(__file__).absolute().parents[1] / "tools/performance/analyze_popup_tails.py"
spec = importlib.util.spec_from_file_location("popup_tail_tool", TOOL_PATH)
tool = importlib.util.module_from_spec(spec)
spec.loader.exec_module(tool)


def cycle(iteration, show=0, render=0, update=0, continuation=0, hide=0):
    values = (show, render, update, continuation, hide)
    return {"Iteration": iteration, **dict(zip(tool.PHASES, values)), "TotalMs": sum(values)}


def linear_cycles():
    return [cycle(index, show=index / 200, hide=5) for index in range(1, 1001)]


class ArithmeticTests(unittest.TestCase):
    def test_linear_rank_interpolation(self):
        rows = [cycle(index, show=index) for index in range(1, 1001)]
        result = tool.summarize_cycles(rows)
        self.assertAlmostEqual(result["p95Ms"], 950.05, places=10)
        self.assertEqual(result["p95Boundary"]["cycleRanks1Based"], [950, 951])
        self.assertEqual(result["p95Boundary"]["iterations"], [950, 951])
        self.assertAlmostEqual(sum(result["p95Boundary"]["weights"]), 1)

    def test_independent_stage_percentiles_cannot_be_added(self):
        rows = [cycle(index, show=9 if index % 2 else 1, hide=1 if index % 2 else 9)
                for index in range(1, 1001)]
        result = tool.summarize_cycles(rows)
        self.assertEqual(result["p95Ms"], 10)
        self.assertEqual(result["p95Boundary"]["iterations"], [950, 951])
        self.assertAlmostEqual(sum(result["p95Boundary"]["componentsMs"].values()), 10)
        # Each independent stage p95 is 9, but the complete cycle is always 10, not 18.
        self.assertNotEqual(9 + 9, result["p95Ms"])

    def test_slowest_50_use_the_same_cycles_for_every_mean(self):
        result = tool.summarize_cycles([cycle(index, show=index / 4, hide=3 * index / 4)
                                        for index in range(1, 1001)])
        tail = result["slowest50"]
        self.assertEqual(tail["iterations"], list(range(951, 1001)))
        self.assertEqual(tail["meanTotalMs"], 975.5)
        self.assertEqual(tail["meanComponentsMs"]["ShowMs"], 975.5 / 4)
        self.assertEqual(tail["meanComponentsMs"]["HideMs"], 3 * 975.5 / 4)

    def test_all_zero_ties_have_deterministic_iteration_order(self):
        result = tool.summarize_cycles([cycle(index) for index in range(1, 1001)])
        self.assertEqual(result["p95Ms"], 0)
        self.assertEqual(result["slowest50"]["iterations"], list(range(951, 1001)))

    def test_integer_tick_oracle_is_independent_of_float_implementation(self):
        randomizer = random.Random(20261026)
        samples = [(index, [randomizer.randrange(0, 50001) for _ in tool.PHASES]) for index in range(1, 1001)]
        rows = [cycle(index, *(value / 10000 for value in values)) for index, values in samples]
        for row, (_, values) in zip(rows, samples):
            row["TotalMs"] = sum(values) / 10000
        result = tool.summarize_cycles(rows)
        ordered = sorted(samples, key=lambda sample: (sum(sample[1]), sample[0]))
        lower, upper = ordered[949], ordered[950]
        expected_total = float(Fraction(sum(lower[1]) * 95 + sum(upper[1]) * 5, 1_000_000))
        self.assertAlmostEqual(result["p95Ms"], expected_total, places=10)
        for index, phase in enumerate(tool.PHASES):
            expected = float(Fraction(lower[1][index] * 95 + upper[1][index] * 5, 1_000_000))
            self.assertAlmostEqual(result["p95Boundary"]["componentsMs"][phase], expected, places=10)
            mean = float(Fraction(sum(sample[1][index] for sample in ordered[-50:]), 500_000))
            self.assertAlmostEqual(result["slowest50"]["meanComponentsMs"][phase], mean, places=10)

    def test_invalid_numeric_types_and_nonfinite_or_unbounded_values(self):
        for label, value in [("boolean", True), ("string", "1"), ("negative", -1), ("nan", float("nan")),
                             ("infinity", float("inf")), ("too_large", 60001), ("huge_integer", 10 ** 1000)]:
            with self.subTest(label=label):
                rows = linear_cycles(); rows[-1]["HideMs"] = value
                with self.assertRaises(tool.AnalysisError): tool.summarize_cycles(rows)

    def test_incomplete_or_expanded_workload_is_rejected(self):
        for count in (0, 999, 1001):
            with self.subTest(count=count), self.assertRaises(tool.AnalysisError):
                tool.summarize_cycles([cycle(index) for index in range(1, count + 1)])

    def test_duplicate_reordered_and_noninteger_iteration_is_rejected(self):
        for value in (999, "1000", 1000.0, True):
            rows = linear_cycles(); rows[-1]["Iteration"] = value
            with self.subTest(value_type=type(value).__name__), self.assertRaises(tool.AnalysisError):
                tool.summarize_cycles(rows)
        with self.assertRaises(tool.AnalysisError): tool.summarize_cycles(list(reversed(linear_cycles())))

    def test_missing_phase_and_mismatched_total_are_rejected(self):
        rows = linear_cycles(); rows[-1].pop("ShowMs")
        with self.assertRaises(tool.AnalysisError): tool.summarize_cycles(rows)
        rows = linear_cycles(); rows[-1]["TotalMs"] += 0.001
        with self.assertRaises(tool.AnalysisError): tool.summarize_cycles(rows)


class VerifiedInputTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="popup-tail-synthetic-")
        self.root = Path(self.temporary.name)
        self.verified = {"evidenceValidated": True, "experimentComplete": True,
                         "sameDiagnosticBytesAcrossModesPerFlavor": True, "declaredProductProcesses": 16,
                         "attemptedProductProcesses": 16, "validatedFullProtocolProcesses": 16,
                         "acceptedWarmups": 4, "acceptedMeasuredProcesses": 12,
                         "performanceNoRegressionAccepted": False, "releaseAccepted": False,
                         "rows": [], "rawEvidenceDigests": {}}
        order = [("Lite", False), ("Lite", True), ("Portable", True), ("Portable", False)]
        for round_number in (0, 1, 2, 3):
            for flavor, disabled in reversed(order) if round_number == 2 else order:
                index = len(self.verified["rows"]) + 1
                record = {"index": index, "flavor": flavor, "disabled": disabled, "round": round_number,
                          "warmup": round_number == 0, "processId": 1000 + index,
                          "fullProtocolAccepted": True, "rawLatencyValidated": True,
                          "preWaitComplete": True, "nativeInterventionConfirmed": True}
                if round_number:
                    relative = f"reports/{index:02d}/factor-probe.json"
                    path = self.root / relative; path.parent.mkdir(parents=True)
                    report = {"success": True, "diagnosticOnly": True, "processId": record["processId"],
                              "parameters": {"popupIterations": 1000, "translationIterations": 0, "ocr": False,
                                             "resourceSampling": False, "forcedCollection": False, "workingSetTrim": False},
                              "rows": linear_cycles()}
                    path.write_text(json.dumps(report), encoding="utf-8")
                    record["reportSha256"] = self.digest(path)
                    record["cycleStatisticsMs"] = {"TotalMs": {"p95": 9.75025}}
                    self.verified["rawEvidenceDigests"][relative] = record["reportSha256"]
                self.verified["rows"].append(record)
        self.flush_verification()

    def tearDown(self):
        self.temporary.cleanup()

    @staticmethod
    def digest(path):
        return hashlib.sha256(path.read_bytes()).hexdigest().upper()

    def flush_verification(self):
        path = self.root / "verified.json"
        path.write_text(json.dumps(self.verified), encoding="utf-8")
        manifest = {"schemaVersion": 1, "verification": {"path": "verified.json", "sha256": self.digest(path)}}
        (self.root / "manifest.json").write_text(json.dumps(manifest), encoding="utf-8")

    def mutate_report(self, mutate):
        record = self.verified["rows"][4]
        relative = "reports/05/factor-probe.json"; path = self.root / relative
        report = json.loads(path.read_text()); mutate(report)
        path.write_text(json.dumps(report), encoding="utf-8")
        record["reportSha256"] = self.digest(path)
        self.verified["rawEvidenceDigests"][relative] = record["reportSha256"]
        self.flush_verification()

    def analyze(self):
        return tool.analyze_manifest(self.root, "manifest.json")

    def refuse(self):
        with self.assertRaises(tool.AnalysisError): self.analyze()

    def cli(self, output="analysis.json"):
        stdout, stderr = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(stdout), contextlib.redirect_stderr(stderr):
            exit_code = tool.main(["--root", str(self.root), "--manifest", "manifest.json", "--output", output])
        return exit_code, stdout.getvalue(), stderr.getvalue()

    def test_complete_matrix_keeps_all_twelve_measured_reports_and_six_pairs(self):
        result = self.analyze()
        self.assertEqual(len(result["measuredReports"]), 12)
        self.assertEqual(len(result["completeMeasuredPairs"]), 6)
        self.assertEqual(result["warmupsExcluded"], 4)
        self.assertFalse(result["performanceNoRegressionAccepted"])
        self.assertFalse(result["releaseAccepted"])
        self.assertEqual([row["index"] for row in result["measuredReports"]], list(range(5, 17)))
        self.assertTrue(all(pair["p95DifferenceMs"] == 0 for pair in result["completeMeasuredPairs"]))

    def test_asymmetric_pair_deltas_use_each_conditions_own_rank_cycles(self):
        def change(report):
            for row in report["rows"]:
                row["ShowMs"] = 5
                row["HideMs"] = (1001 - row["Iteration"]) / 100
                row["TotalMs"] = row["ShowMs"] + row["HideMs"]
        self.mutate_report(change)
        self.verified["rows"][4]["cycleStatisticsMs"]["TotalMs"]["p95"] = 14.5005
        self.flush_verification()
        result = self.analyze(); pair = result["completeMeasuredPairs"][0]
        self.assertEqual(result["measuredReports"][0]["statistics"]["p95Boundary"]["iterations"], [51, 50])
        self.assertEqual(result["measuredReports"][1]["statistics"]["p95Boundary"]["iterations"], [950, 951])
        self.assertAlmostEqual(pair["p95DifferenceMs"], -4.75025, places=10)
        self.assertAlmostEqual(sum(pair["p95BoundaryComponentDifferencesMs"].values()), pair["p95DifferenceMs"], places=10)
        self.assertAlmostEqual(pair["slowest50MeanDifferenceMs"], -4.8775, places=10)
        self.assertAlmostEqual(sum(pair["slowest50ComponentDifferencesMs"].values()), pair["slowest50MeanDifferenceMs"], places=10)

    def test_stopped_matrix_and_incomplete_protocol_cannot_be_consumed(self):
        self.verified["experimentComplete"] = False; self.flush_verification(); self.refuse()

    def test_false_or_coerced_verified_flags_are_rejected(self):
        for value in (False, "true", 1):
            self.verified["evidenceValidated"] = value; self.flush_verification(); self.refuse()

    def test_missing_record_cannot_hide_a_round(self):
        self.verified["rows"].pop(); self.flush_verification(); self.refuse()

    def test_rejected_measurement_cannot_be_aggregated(self):
        self.verified["rows"][-1]["fullProtocolAccepted"] = False; self.flush_verification(); self.refuse()

    def test_duplicate_index_is_rejected(self):
        self.verified["rows"][-1]["index"] = 15; self.flush_verification(); self.refuse()

    def test_duplicate_pair_coordinate_is_rejected(self):
        self.verified["rows"][-1]["disabled"] = True; self.flush_verification(); self.refuse()

    def test_duplicate_pid_is_rejected(self):
        self.verified["rows"][-1]["processId"] = 1005; self.flush_verification(); self.refuse()

    def test_warmup_cannot_replace_measured_round(self):
        self.verified["rows"][4]["warmup"] = True; self.flush_verification(); self.refuse()

    def test_changed_verification_digest_is_rejected(self):
        (self.root / "verified.json").write_text("{}", encoding="utf-8"); self.refuse()

    def test_changed_raw_report_digest_is_rejected(self):
        (self.root / "reports/05/factor-probe.json").write_text("{}", encoding="utf-8"); self.refuse()

    def test_ambiguous_report_path_is_rejected(self):
        self.verified["rawEvidenceDigests"]["reports/other/factor-probe.json"] = self.verified["rows"][4]["reportSha256"]
        self.flush_verification(); self.refuse()

    def test_payload_pid_must_match_verified_identity(self):
        self.mutate_report(lambda report: report.update(processId=999)); self.refuse()

    def test_workload_and_forced_collection_cannot_change(self):
        self.mutate_report(lambda report: report["parameters"].update(forcedCollection=True)); self.refuse()

    def test_late_raw_corruption_is_checked_even_with_valid_seal_reference(self):
        self.mutate_report(lambda report: report["rows"][-1].update(HideMs=-1)); self.refuse()

    def test_nonfinite_json_is_rejected(self):
        self.mutate_report(lambda report: report["rows"][-1].update(HideMs=float("nan"))); self.refuse()

    def test_verified_total_percentile_must_match_raw_recalculation(self):
        self.verified["rows"][4]["cycleStatisticsMs"]["TotalMs"]["p95"] = 10; self.flush_verification(); self.refuse()

    def test_unsupported_manifest_override_is_rejected(self):
        path = self.root / "manifest.json"; manifest = json.loads(path.read_text()); manifest["p95"] = 0.5
        path.write_text(json.dumps(manifest), encoding="utf-8"); self.refuse()

    def test_duplicate_json_property_is_rejected(self):
        (self.root / "manifest.json").write_text('{"schemaVersion":1,"schemaVersion":1,"verification":{}}', encoding="utf-8")
        self.refuse()

    def test_verification_size_cap_is_enforced(self):
        self.verified["padding"] = "x" * tool.MAX_VERIFICATION_BYTES
        self.flush_verification(); self.refuse()

    def test_non_regular_evidence_is_rejected_without_opening_a_pipe(self):
        pipe = self.root / "pipe.json"
        os.mkfifo(pipe)
        with self.assertRaises(tool.AnalysisError): tool.read_json(pipe, 1024)

    def test_manifest_and_report_size_caps_are_enforced(self):
        (self.root / "manifest.json").write_bytes(b" " * (tool.MAX_MANIFEST_BYTES + 1)); self.refuse()
        self.flush_verification()
        self.mutate_report(lambda report: report.update(padding="x" * tool.MAX_REPORT_BYTES)); self.refuse()

    def test_unsafe_relative_paths_are_rejected_before_reading(self):
        for path in ("", "/absolute.json", "../outside.json", "a/../b.json", "\\\\host\\share", "https://invalid/x", "NUL.json", "a.json.", "x\x00.json"):
            with self.subTest(path=path), self.assertRaises(tool.AnalysisError): tool.local_path(self.root, path)

    def test_unc_root_is_rejected(self):
        with self.assertRaises(tool.AnalysisError): tool.root_path("//fixed.invalid/share")

    def test_linked_root_and_descendant_are_rejected(self):
        linked_root = self.root.parent / (self.root.name + "-link")
        linked_root.symlink_to(self.root, target_is_directory=True)
        try:
            with self.assertRaises(tool.AnalysisError): tool.root_path(linked_root)
        finally:
            linked_root.unlink()
        (self.root / "link").symlink_to(self.root / "reports", target_is_directory=True)
        with self.assertRaises(tool.AnalysisError): tool.local_path(self.root, "link/05/factor-probe.json")

    def test_cli_writes_only_a_new_complete_output(self):
        exit_code, stdout, stderr = self.cli()
        self.assertEqual(exit_code, 0); self.assertIn("6 complete pairs", stdout); self.assertEqual(stderr, "")
        result = json.loads((self.root / "analysis.json").read_text())
        self.assertTrue(result["analysisComplete"]); self.assertFalse(result["releaseAccepted"])

    def test_cli_never_overwrites_an_input_or_old_output(self):
        before = (self.root / "manifest.json").read_bytes()
        exit_code, _, _ = self.cli("manifest.json")
        self.assertEqual(exit_code, 1); self.assertEqual(before, (self.root / "manifest.json").read_bytes())
        (self.root / "analysis.json").write_text("old evidence", encoding="utf-8")
        exit_code, _, _ = self.cli()
        self.assertEqual(exit_code, 1); self.assertEqual((self.root / "analysis.json").read_text(), "old evidence")

    def test_cli_failure_has_no_partial_output_or_raw_content(self):
        self.mutate_report(lambda report: report["rows"][-1].update(HideMs="synthetic-secret-not-to-log"))
        exit_code, stdout, stderr = self.cli()
        self.assertEqual(exit_code, 1); self.assertEqual(stdout, "")
        self.assertNotIn("synthetic-secret", stderr); self.assertFalse((self.root / "analysis.json").exists())


if __name__ == "__main__":
    unittest.main()
