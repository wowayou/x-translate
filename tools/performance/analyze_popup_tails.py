"""Bounded, read-only arithmetic analysis of an independently accepted popup factor matrix.

This consumes the existing verification seal; it is not a replacement for the full diagnostic verifier.
No product/SDK/network operation is performed. Never add independent stage percentiles as a total.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
from pathlib import Path
import stat
import sys

PHASES = ("ShowMs", "RenderQueueMs", "UpdateLayoutMs", "ContinuationMs", "HideMs")
CYCLES = 1000
TAIL_COUNT = 50
SUM_EPSILON_MS = 1e-6  # Floating-point sum consistency only; never a pre-wait acceptance tolerance.
MAX_MANIFEST_BYTES = 64 * 1024
MAX_VERIFICATION_BYTES = 256 * 1024
MAX_REPORT_BYTES = 2 * 1024 * 1024


class AnalysisError(ValueError):
    pass


def require(condition: bool, message: str) -> None:
    if not condition:
        raise AnalysisError(message)


def number(value: object) -> float:
    require(type(value) in (int, float) and 0 <= value <= 60000 and math.isfinite(value),
            "InvalidLatencyNumber")
    return float(value)


def consistent(left: float, right: float) -> None:
    require(math.isclose(left, right, rel_tol=0, abs_tol=SUM_EPSILON_MS), "InconsistentLatencySum")


def summarize_cycles(rows: list[dict]) -> dict:
    require(type(rows) is list and len(rows) == CYCLES, "IncompleteCycleSeries")
    for iteration, row in enumerate(rows, 1):
        require(type(row) is dict and type(row.get("Iteration")) is int and row["Iteration"] == iteration,
                "InvalidIterationOrder")
        try:
            total = number(row["TotalMs"])
            consistent(math.fsum(number(row[phase]) for phase in PHASES), total)
        except KeyError as error:
            raise AnalysisError("MissingLatencyPhase") from error
    ordered = sorted(rows, key=lambda row: (row["TotalMs"], row["Iteration"]))
    position = (CYCLES - 1) * 0.95
    lower_index, upper_index = math.floor(position), math.ceil(position)
    fraction = position - lower_index
    lower, upper = ordered[lower_index], ordered[upper_index]

    def interpolate(field: str) -> float:
        return lower[field] + (upper[field] - lower[field]) * fraction

    p95 = interpolate("TotalMs")
    components = {phase: interpolate(phase) for phase in PHASES}
    consistent(math.fsum(components.values()), p95)
    tail = ordered[-TAIL_COUNT:]
    tail_total = math.fsum(row["TotalMs"] for row in tail) / TAIL_COUNT
    tail_components = {phase: math.fsum(row[phase] for row in tail) / TAIL_COUNT for phase in PHASES}
    consistent(math.fsum(tail_components.values()), tail_total)
    return {"count": CYCLES, "p95Ms": p95,
            "p95Boundary": {"cycleRanks1Based": [lower_index + 1, upper_index + 1],
                            "iterations": [lower["Iteration"], upper["Iteration"]],
                            "weights": [1 - fraction, fraction], "componentsMs": components},
            "slowest50": {"count": TAIL_COUNT, "iterations": [row["Iteration"] for row in tail],
                          "minimumTotalMs": tail[0]["TotalMs"], "meanTotalMs": tail_total,
                          "meanComponentsMs": tail_components}}


def ordinary_path(path: Path) -> None:
    # Check lexical ancestors before opening; do not resolve links first.
    for current in reversed((path, *path.parents)):
        try:
            info = current.lstat()
        except FileNotFoundError:
            continue
        require(not stat.S_ISLNK(info.st_mode) and not (getattr(info, "st_file_attributes", 0) & 0x400),
                "LinkPathNotAllowed")


def root_path(value: str | Path) -> Path:
    text = str(value)
    require(not text.startswith(("//", "\\\\")) and ".." not in Path(text).parts, "UnsafeRootPath")
    root = Path(value).absolute()
    ordinary_path(root)
    require(root.is_dir(), "LocalRootDirectoryRequired")
    return root


def local_path(root: Path, relative: str) -> Path:
    require(type(relative) is str and 0 < len(relative) <= 4096 and
            not any(char in relative for char in '\\:<>|?*"') and all(ord(char) >= 32 for char in relative),
            "UnsafeRelativePath")
    parts = relative.split("/")
    reserved = {"CON", "PRN", "AUX", "NUL"} | {f"{prefix}{index}" for prefix in ("COM", "LPT") for index in range(1, 10)}
    require(len(parts) <= 64 and all(part not in ("", ".", "..") and not part.endswith((" ", ".")) and
            part.upper().split(".")[0] not in reserved for part in parts), "UnsafeRelativePath")
    path = root.joinpath(*parts)
    ordinary_path(path)
    return path


def digest_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest().upper()


def digest_value(value: object) -> str:
    require(type(value) is str and len(value) == 64 and all(char in "0123456789abcdefABCDEF" for char in value),
            "InvalidEvidenceDigest")
    return value.upper()


def unique_object(pairs: list[tuple[str, object]]) -> dict:
    result = {}
    for key, value in pairs:
        require(key not in result, "DuplicateJsonProperty")
        result[key] = value
    return result


def reject_constant(value: str) -> None:
    raise AnalysisError("NonFiniteJsonValue")


def read_json(path: Path, maximum: int, expected: str | None = None) -> tuple[dict, str]:
    require(path.is_file() and stat.S_ISREG(path.stat().st_mode), "OrdinaryEvidenceFileRequired")
    with path.open("rb") as stream:
        data = stream.read(maximum + 1)
    require(0 < len(data) <= maximum, "EvidenceSizeLimit")
    digest = digest_bytes(data)
    if expected is not None:
        require(digest == digest_value(expected), "EvidenceDigestMismatch")
    try:
        result = json.loads(data.decode("utf-8-sig"), object_pairs_hook=unique_object, parse_constant=reject_constant)
    except (UnicodeError, json.JSONDecodeError, RecursionError) as error:
        raise AnalysisError("InvalidEvidenceJson") from error
    require(type(result) is dict, "JsonObjectRequired")
    return result, digest


def exact_fields(value: dict, fields: dict) -> None:
    require(type(value) is dict and all(type(value.get(key)) is type(expected) and value[key] == expected
                                     for key, expected in fields.items()), "IncompatibleVerifiedProtocol")


def analyze_manifest(root: Path, manifest_relative: str) -> dict:
    manifest, manifest_digest = read_json(local_path(root, manifest_relative), MAX_MANIFEST_BYTES)
    exact_fields(manifest, {"schemaVersion": 1})
    require(set(manifest) == {"schemaVersion", "verification"}, "UnsupportedManifestFields")
    seal = manifest["verification"]
    require(type(seal) is dict and set(seal) == {"path", "sha256"}, "InvalidVerificationReference")
    verified, verification_digest = read_json(local_path(root, seal["path"]), MAX_VERIFICATION_BYTES, seal["sha256"])
    exact_fields(verified, {"evidenceValidated": True, "experimentComplete": True,
                           "sameDiagnosticBytesAcrossModesPerFlavor": True, "declaredProductProcesses": 16,
                           "attemptedProductProcesses": 16, "validatedFullProtocolProcesses": 16,
                           "acceptedWarmups": 4, "acceptedMeasuredProcesses": 12,
                           "performanceNoRegressionAccepted": False, "releaseAccepted": False})
    records = verified.get("rows")
    digests = verified.get("rawEvidenceDigests")
    require(type(records) is list and len(records) == 16 and type(digests) is dict, "IncompleteVerifiedMatrix")
    selected = []; keys = set(); pids = set(); indices = set(); warmups = 0
    for record in records:
        exact_fields(record, {"fullProtocolAccepted": True, "rawLatencyValidated": True,
                              "preWaitComplete": True, "nativeInterventionConfirmed": True})
        require(type(record.get("index")) is int and 1 <= record["index"] <= 16 and record["index"] not in indices and
                type(record.get("warmup")) is bool and type(record.get("disabled")) is bool and
                record.get("flavor") in ("Lite", "Portable") and type(record.get("round")) is int,
                "InvalidVerifiedRow")
        indices.add(record["index"])
        if record["warmup"]:
            require(record["round"] == 0, "WarmupMisclassified")
            warmups += 1
            continue
        require(record["round"] in (1, 2, 3), "InvalidMeasuredRound")
        key = (record["flavor"], record["round"], record["disabled"])
        require(key not in keys and type(record.get("processId")) is int and record["processId"] > 0 and
                record["processId"] not in pids, "DuplicateMeasuredIdentity")
        keys.add(key); pids.add(record["processId"])
        expected_digest = digest_value(record.get("reportSha256"))
        paths = [path for path, digest in digests.items() if digest == expected_digest and path.endswith("/factor-probe.json")]
        require(len(paths) == 1, "AmbiguousReportIdentity")
        report, report_digest = read_json(local_path(root, paths[0]), MAX_REPORT_BYTES, expected_digest)
        exact_fields(report, {"success": True, "diagnosticOnly": True, "processId": record["processId"]})
        exact_fields(report.get("parameters"), {"popupIterations": 1000, "translationIterations": 0,
                     "ocr": False, "resourceSampling": False, "forcedCollection": False, "workingSetTrim": False})
        summary = summarize_cycles(report.get("rows"))
        try:
            consistent(summary["p95Ms"], number(record["cycleStatisticsMs"]["TotalMs"]["p95"]))
        except (KeyError, TypeError) as error:
            raise AnalysisError("MissingVerifiedTotalPercentile") from error
        selected.append({key: record[key] for key in ("index", "flavor", "round", "disabled", "processId")} |
                        {"reportPath": paths[0], "reportSha256": report_digest, "statistics": summary})
    expected_keys = {(flavor, round_number, disabled) for flavor in ("Lite", "Portable")
                     for round_number in (1, 2, 3) for disabled in (False, True)}
    require(warmups == 4 and keys == expected_keys and indices == set(range(1, 17)), "IncompleteMeasuredPairs")
    pairs = []
    for flavor in ("Lite", "Portable"):
        for round_number in (1, 2, 3):
            before, after = [next(row for row in selected if (row["flavor"], row["round"], row["disabled"]) ==
                                 (flavor, round_number, disabled)) for disabled in (False, True)]
            left, right = before["statistics"], after["statistics"]
            p95_delta = right["p95Ms"] - left["p95Ms"]
            p95_components = {phase: right["p95Boundary"]["componentsMs"][phase] -
                              left["p95Boundary"]["componentsMs"][phase] for phase in PHASES}
            tail_delta = right["slowest50"]["meanTotalMs"] - left["slowest50"]["meanTotalMs"]
            tail_components = {phase: right["slowest50"]["meanComponentsMs"][phase] -
                               left["slowest50"]["meanComponentsMs"][phase] for phase in PHASES}
            consistent(math.fsum(p95_components.values()), p95_delta)
            consistent(math.fsum(tail_components.values()), tail_delta)
            pairs.append({"flavor": flavor, "round": round_number, "defaultIndex": before["index"],
                          "disabledIndex": after["index"], "p95DifferenceMs": p95_delta,
                          "p95BoundaryComponentDifferencesMs": p95_components,
                          "slowest50MeanDifferenceMs": tail_delta, "slowest50ComponentDifferencesMs": tail_components})
    return {"analysisComplete": True, "diagnosticOnly": True, "inputManifestSha256": manifest_digest,
            "sourceVerification": {"path": seal["path"], "sha256": verification_digest},
            "method": {"cyclesPerReport": CYCLES, "p95": 0.95, "slowestCyclesPerReport": TAIL_COUNT,
                       "tieBreak": "TotalMs ascending, then Iteration ascending",
                       "componentRule": "Use the SAME total-rank cycles and interpolation weights within each report; never add independent stage percentiles."},
            "measuredReports": sorted(selected, key=lambda row: row["index"]), "completeMeasuredPairs": pairs,
            "warmupsExcluded": 4, "productProcessesStarted": 0, "sdkOperations": 0,
            "performanceNoRegressionAccepted": False, "releaseAccepted": False,
            "limitations": ["Each condition selects its own total-rank cycles, not matched iterations across conditions.",
                            "Rank-boundary and slowest-50 arithmetic are descriptive; they do not establish CPU/wait time, a population stage percentile or causality.",
                            "The verification seal must already be independently trusted. This consumer does not replace full native/process/package/idle acceptance.",
                            "No new measurements, resource budget, foreground/compositor, visual or long-run acceptance."]}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", default=str(Path(__file__).absolute().parents[2]), help="Trusted existing local root directory")
    parser.add_argument("--manifest", required=True, help="Root-relative POSIX path to a verification reference")
    parser.add_argument("--output", required=True, help="Fresh root-relative JSON path in an existing directory")
    args = parser.parse_args(argv)
    try:
        root = root_path(args.root)
        output = local_path(root, args.output)
        require(not output.exists() and output.parent.is_dir(), "FreshOutputRequired")
        result = analyze_manifest(root, args.manifest)
        payload = json.dumps(result, indent=2, allow_nan=False) + "\n"
        require(len(payload.encode("utf-8")) <= 1024 * 1024, "OutputSizeLimit")
        with output.open("x", encoding="utf-8") as stream:
            stream.write(payload)
    except (AnalysisError, OSError, KeyError, TypeError) as error:
        # No raw text/report content in errors; failure is explicit and never falls back to partial analysis.
        message = str(error) if isinstance(error, AnalysisError) else type(error).__name__
        print("ERROR " + message, file=sys.stderr)
        return 1
    print("PASS arithmetic analysis: 12 reports, 6 complete pairs, 4 warmups excluded; no performance/release acceptance")
    return 0


if __name__ == "__main__":
    sys.exit(main())
