# Memory diagnostics

`memory-diagnostics.ps1` compares the production diagnostic mode with two small baselines in one isolated session:

- `app`: the published XTranslate executable with `--memory-diagnostics`.
- `baseline-blank`: a minimal WPF `Window` with the same 650×740 dimensions, Segoe UI font and white background. It does not copy the product UI and does not enter the Forms tray helper.
- `baseline-tray`: the same minimal window plus a real visible `NotifyIcon` and a four-item `ContextMenuStrip` (Open, Settings, separator, Exit), using the linked production ICO. The tray helper is isolated behind a `NoInlining` factory and all Forms/native resources are disposed at the end of the run.

The runner defaults to three sequential runs per group, publishes both projects into a new `artifacts/memory-diagnostics/session-*` directory, and never calls `build.ps1` or reuses the 0.2 release directory. Every run has a unique report and `command.json`; a timeout terminates only the PID started by that run. A failed/non-zero run stops the session.

## Run

```powershell
./memory-diagnostics.ps1 -Runs 3 -DiagnosticBatches 5 -DiagnosticIdleSeconds 5 -DiagnosticHoldSeconds 0
```

正式采样窗口可通过 `-DiagnosticSettleSeconds` 和 `-DiagnosticWindowSeconds` 配置，默认分别为 3 秒和 30 秒。每组完成预热和 visible-idle 后隐藏窗口，等待 settle，再按 t=1..N 每秒采样。缺少参数值或超出范围会使诊断启动失败。应用和基线默认使用发布策略的软件渲染；`-HardwareRendering` 仅用于隔离 A/B 诊断，不能与 `-SoftwareRendering` 同时使用。

The runner builds `win-x64` self-contained by default. Use `-FrameworkDependent` only on a machine with the matching .NET 10 Windows Desktop runtime. `DiagnosticIdleSeconds` accepts 1–600 seconds, `DiagnosticHoldSeconds` and `DiagnosticSettleSeconds` accept 0–600 seconds, and `DiagnosticWindowSeconds` accepts 1–600 seconds. Paths are quoted for spaces. `-VMMapPath` is accepted as provenance metadata only; VMMap is not invoked, downloaded, or installed. Current sessions record `vmmap.status = not-collected` and `reason = deferred-by-user`.

## Diagnostic sequence

Each process is positioned off-screen with `ShowActivated=false` and `ShowInTaskbar=false`; the tray baseline additionally keeps its NotifyIcon visible. The baseline enters its measurement through a one-shot WPF `Loaded` handler (not `ContentRendered`), then explicitly waits for a render/update-layout dispatch before sampling. No shortcut is registered, no clipboard is read, no application is activated, and no forced GC or working-set trim is performed. The sequence is startup, 20 warmup Show/Hide transitions, visible idle, the independent `hidden-measurement` phase, compatibility hidden idle, each requested batch of 100 Show/Hide transitions followed by an unconditional hidden idle checkpoint, and hidden recovery. The hidden window waits for settle, then samples the nominal points t=1..N once per second; the 30-point default means t=1..30 after settling and includes normal sampling/scheduling overhead. Show/Hide transitions use the same Show → render/update-layout → Hide shape as the production diagnostic path; the dispatcher settle is applied at phase boundaries, not after every transition. Idle checkpoints are sampled once per second. Phase objects contain `endSnapshot` and `checkpoints` in memory, while the baseline writes one complete JSON report after all phases; its low-frequency `.progress.log` is only a diagnostic sidecar. The production app and both baselines use the shared `XTranslate.Diagnostics.DiagnosticsSampler`, including the same resource/GC/module metadata path; modules are enumerated once at the end rather than on every sample.

Reports record mode, machine/runtime metadata, loaded modules, parameters, phase/batch snapshots, render mode, and whether tray resources were enabled. The independent `hidden-measurement` phase records raw-byte window statistics under `phase.statistics` (the shared C# record uses PascalCase fields such as `ValidSamples`, `MedianBytes`, and `MeasurementComplete`) for WS, PrivateBytes, and PWS, plus PrivateBytes start/end/delta, actual duration, and sample count; it does not duplicate an end snapshot. The runner session summary projects selected per-run fields to lower camel case (`measurementComplete`, `targetStatus`, and `window`) while preserving the nested statistics object. A partial window or missing PWS is not measurement complete. The sampler owns the authoritative snapshot shape. Individual sessions use `targetStatus = not-evaluated` because the Private Bytes guardrail requires a separate reference session; successful execution alone does not mean the memory target passed. `memory-acceptance.ps1` performs the cross-session evaluation.

Reports produced before the hidden measurement window was added retain their original phase shape and historical meaning; they must not be reinterpreted as if they contained the new window statistics.

The three groups are not a complete attribution experiment and must not be treated as directly subtractable totals: the production app includes its fixed diagnostic mock-translation workload (10 warmup operations and 20 per batch), while the baselines perform only the window Show/Hide workload. Sampler calls, retained JSON snapshots, module metadata, and phase-report serialization are included in the observed process footprint.

## Metric dictionary and comparability

`WorkingSetBytes` (WS) is resident process working set; `PrivateBytes` is the process private-memory counter; and `PrivateWorkingSetBytes` (PWS) is the sampler's queried private working-set estimate, with its method and failure reason recorded alongside it. `ManagedAllocatedEstimateBytes` is `GC.GetTotalMemory(false)`, not a complete managed-heap census. `GcIndex` and the related GC fields describe the last GC snapshot; index `0` can mean that no completed GC snapshot exists, and the sampler never forces a collection. Loaded-module `SizeOfImageBytes` is virtual PE image size, not resident or on-disk size, and module values must not be added as a memory total. Small differences between checkpoints can be sampling/OS scheduling noise, so single-sample deltas are not attribution proof.

For directly comparable runs, use `-DiagnosticHoldSeconds 0`. The baseline's non-zero hold is an auxiliary wait after its complete report has been written; the production diagnostic path performs its additional hold/sampling within each batch, so a non-zero hold does not represent the same phase sequence across groups.

## VMMap follow-up

VMMap is an optional manual follow-up and is not part of the built-in run. If a reviewer has an approved Sysinternals installation, capture each target PID during the explicit hold window and retain the VMMap output beside that run's JSON. Do not download tools or accept an EULA as part of the runner. A missing VMMap capture is `Unavailable`/`not-collected`, not evidence that a process has no native allocations.

## Results

### Main session (2026-09-22)

The formal self-contained `win-x64` session completed successfully: 9/9 processes (three runs each for `app`, `baseline-blank`, and `baseline-tray`) exited with code 0 and reported `success: true`. Runtime was .NET 8.0.31 / x64. The reports recorded the expected hidden/visible state transitions; all Private Working Set and GUI-resource samples had no failure reason, and module enumeration completed successfully. The blank baseline had no Forms modules; tray and app each had three Forms modules.

Session summary: `artifacts/memory-diagnostics/acceptance-final/session-20260922-064420-394-b986d71b/memory-diagnostics-summary.json`.

The following ranges are the observed per-run extrema, in MiB (1 MiB = 1,048,576 bytes):

| Group | Hidden-idle WS | Hidden-idle PWS | Hidden-idle Private | Hidden-recovery PWS |
| --- | ---: | ---: | ---: | ---: |
| baseline-blank | 170.61–176.08 | 90.35–90.67 | 135.44–137.14 | 97.78–99.86 |
| baseline-tray | 184.64–184.91 | 92.20–92.40 | 137.81–140.16 | 99.61–99.91 |
| app | 228.62–234.77 | 116.62–117.15 | 165.52–167.08 | 110.79–114.56 |

The app PWS first-to-last deltas for its three runs were −3.79, −5.83, and −2.37 MiB. The blank-baseline PWS increased by approximately 7.43–9.19 MiB across its runs. The app reached `GcIndex = 1` by recovery, with `GcCommittedBytes` approximately 12.48–14.92 MiB. The baseline reports retained `GcIndex = 0`; under the sampler's documented semantics this means no completed GC event was observed, so `GcCommittedBytes` is N/A—not zero—for those observations.

On this machine, the tray baseline's hidden-idle PWS was only about 1.5–2.1 MiB above the blank baseline. This is an observation for these controlled runs, not proof of a universal framework floor or a reason by itself to rewrite tray handling. The measurements do not establish absence of leaks, a general memory budget, an optimization target, or long-term stability; the groups contain different workloads and are not directly subtractable totals.

The session did not collect VMMap (`not-collected`, deferred by user) and was not an 8-hour soak.

### Extended 20-batch short trend (2026-09-22)

The separate self-contained `win-x64` extension also completed 3/3 successfully (app 95.19 s, blank 96.59 s, tray 95.95 s), using the same `IdleSeconds=3` / `HoldSeconds=0` shape. Reports are under `artifacts/memory-diagnostics/acceptance-final/extended-20batch-20260922-065142-36797375`.

The additional observations reinforce the need to read resident and private counters together:

- App: hidden-idle PWS 115.73 MiB, batch-20 endpoint 123.39 MiB (batch 19 was higher at 123.93 MiB), recovery 117.45 MiB; PrivateBytes 166.71 → 172.33 → 166.55 MiB. Natural GC advanced from index 0 to 9; recovery `GcCommittedBytes` was 18.42 MiB and managed estimate 3.47 MiB.
- Blank baseline: hidden-idle PWS/PrivateBytes 90.14/135.48 MiB, batch-20 29.34/154.02 MiB, recovery 27.88/154.05 MiB; recovery GC index 2, committed 17.02 MiB, managed estimate 2.77 MiB.
- Tray baseline: hidden-idle PWS/PrivateBytes 92.02/137.93 MiB, batch-20 40.03/154.09 MiB, recovery 40.03/154.16 MiB; recovery GC index 2, committed 13.93 MiB, managed estimate 2.17 MiB.

Both baselines showed a large resident/PWS drop while PrivateBytes rose. The cause was not established; it must not be attributed to optimization or GC, and the low PWS values do not demonstrate a `<60 MB` target. The app's partial recovery likewise does not prove a long-term stable plateau or absence of leaks. These are short, controlled trend observations, not an 8-hour soak or a universal memory budget.

### Rendering-path optimization (2026-09-29)

The approved stage rule is: all three complete app runs must have a hidden-window PWS median strictly below 60 MiB, and every candidate Private Bytes median must be no more than 105% of the median of the three reference run medians. `memory-acceptance.ps1` evaluates this rule across distinct reference and candidate sessions; it rejects incomplete windows, missing PWS, mismatched configurations, duplicated reports, and raw/statistics disagreement.

The final no-flag product path recorded `SoftwareOnly` and passed. Its three PWS medians were 30.910, 30.635, and 27.707 MiB; Private Bytes medians were 47.922, 47.422, and 46.988 MiB. The hardware-rendering reference Private Bytes median was 151.816 MiB and its inclusive 5% guardrail was 159.407 MiB. Evidence: `artifacts/verification/render-default-final-memory-20260929/session-20260929-135518-448-17a29757/memory-acceptance.json`.

This is a short controlled acceptance result. The separate eight-hour soak below completed later; VMMap remains uncollected. Neither result establishes leak freedom or a universal machine-independent budget.

## Eight-hour soak

`memory-soak.ps1` publishes an isolated product build (or uses an explicitly supplied executable) and runs one application process for the requested duration. The default eight-hour shape uses 48 ten-minute hidden hold phases. Before each hold, the application performs 100 off-screen Show/Hide transitions and 20 fixed in-process translations. It samples hidden state once per second, uses the product-default software renderer, and does not access the network, clipboard, settings, or user text.

```powershell
./memory-soak.ps1 -DurationHours 8 -CheckpointSeconds 600
```

The runner writes `soak-config.json` before launch, refreshes `soak-status.json` while running, and creates an immutable `soak-summary.json` after completion. Compact hold mode writes each raw sample immediately to `memory-soak-samples.jsonl`; the application report retains only each hold's statistics and endpoints, so diagnostic bookkeeping does not accumulate tens of thousands of full snapshots in the measured process. A ten-minute hold is committed to `memory-diagnostics.json` when that hold finishes; interruption can leave the current JSONL phase incomplete but preserves its written seconds and all earlier completed phases. The runner independently recomputes every hold median from the JSONL file and checks it against the report, the 60 MiB PWS limit, and the approved 159.407 MiB displayed Private Bytes guardrail (the script retains the exact 159.4072265625 MiB value). It also reports first/last-third medians and endpoint changes for managed memory, handles, GDI, and USER resources.

A passing soak means the single process completed the bounded workload and every periodic median stayed inside the approved budget. Trend deltas remain observational; the report deliberately uses `leakConclusion = not-proven` because a bounded run cannot prove the absence of every leak.

### OCR workload and supplied packages

```powershell
# Explicit eight-hour OCR workload; select a trusted, local, flat Lite package.
./memory-soak.ps1 -Ocr -FrameworkDependent -ExecutablePath .\artifacts\my-lite-package\XTranslate.exe -DurationHours 8
# For a self-contained Portable package, omit -FrameworkDependent.
```

Without `-Ocr`, the original workload is retained. `-Ocr` adds three off-screen production OCR windows to each batch: three language enumerations, six real installed en-US recognitions of fixed generated BGRA pixels, and six fixed in-process mock translations. It checks exact generated text/translation, explicit-only retranslation after rereading, initialization count, unchanged source pixels, and cleared owned pixels/text/preview after close. It does not capture the screen, open user images, read the clipboard, download language packs, force GC, trim the working set or invoke a real translation service. An unavailable en-US engine is a failure, not a skipped workload or a language fallback. The underlying Windows engine still has its documented native-operation cancellation limits.

`-ExecutablePath` avoids rebuilding the selected artifact. The runner checks the requested deployment before launching a child, retains the process handle, records the executable SHA-256 plus a bounded flat identity of top-level EXE/DLL/runtimeconfig/deps files, and verifies that identity after exit. This includes Lite's code dependencies, not just its apphost. It never traverses `data` or hashes user settings; this is provenance for the supported flat package layouts, not a complete recursive or atomic tamper-proof manifest.

Without a supplied executable, restore uses only `.tools/cli/.nuget/packages` with NuGet audit disabled; a missing cache fails rather than enabling online restore. Restore and publish have independent 120-second limits, isolated build outputs and logs. Application timing starts after building. The application deadline is the scheduled holds plus nominal diagnostic time `(batches + 3) * DiagnosticIdleSeconds + 3 + DiagnosticWindowSeconds`, plus `TimeoutGraceMinutes * 60`. Grace covers rendering/OCR, sample scheduling overhead and shutdown, not extra hold samples or relaxed budgets. A timeout terminates only the owned application PID and remains a failed run. No retries, installation, or background recurring job are added.

Modern .NET SDK first-run initialization can ignore `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`. One fresh-home validation log displayed the SDK development-certificate initialization message; it does not establish whether a new certificate was created. No trust or certificate-cleanup command was run. The runner now explicitly sets `DOTNET_GENERATE_ASPNET_CERTIFICATE=false` and `DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=false` for its child SDK processes. A fresh-home offline publish and short non-OCR run verified that the certificate message no longer appears (`ocr-slice-seal-sdk-init-disabled/` under the Q3 continuation evidence).

Runner boundary tests: `tests/ocr-soak.Tests.ps1` loads the actual functions and uses synthetic reports/files and a fake build process; it does not launch OCR or establish product memory acceptance. Current OCR short-run results and all retained failures are documented in [Q3 acceptance](ACCEPTANCE-OCR-Q3.md) and [performance](PERFORMANCE.md). They do not extend the historical eight-hour result below to 0.4.0.

The separate opt-in [generated-image corpus runner](../tools/ocr/README.md#固定生成图片与滚动界面验收) validates fixed PNG/local-engine cases and bounded scroll layouts, not memory medians or long-term stability. Later PNG/scroll/provenance fixes create new package identities; the prior 60-second OCR hold results must not be treated as acceptance for these newer packages. Their own two-batch 60-second OCR resource recheck completed with 122 samples per edition but **failed** the unchanged 60 MiB PWS gate: Lite 61.199/61.336 MiB, Portable 59.785/64.527 MiB. The Private Bytes gate passed and GDI endpoint deltas were zero, but these facts do not cancel the PWS failure or establish its cause. Evidence is retained under `ocr-complex-q3-20261022/final-ocr-resource-short/`; no threshold increase, forced collection/trim, automatic retry or eight-hour background job occurred.

The subsequent bounded-allocation slice retains at most one exact-length, zeroed BGRA scratch buffer up to 1 MiB and writes directly into a locked native bitmap instead of creating a `DataWriter` intermediate copy. Live backend ownership is unchanged: cancellation/deadline return never recycles pixels until the actual operation settles. Native offset/stride/capacity checks and independent byte-for-byte bitmap readback are covered by adapter tests. This removes known duplicate allocations, not every possible cause of the preceding PWS failure.

The new final Lite/Portable packages each completed one process with two 60-second holds, 122 samples and the unchanged generated-OCR workload: PWS medians **54.566/58.070 MiB** and **55.703/58.949 MiB** respectively; both original budget gates passed, GDI endpoint deltas were zero, and program files were unchanged. The runner and independent Python validation bind raw statistics/digests to these package identities under `ocr-resource-q3-20261022/bitmap-final-resource-short/` and `bitmap-final-verification.json`. The old failures and intermediate-candidate pairing failures remain. These are one-process short checks, not an eight-hour result, three-run acceptance, leak proof or a single-factor attribution experiment; detailed scope is in [Q3 acceptance](ACCEPTANCE-OCR-Q3.md#q33-有界像素复用与直接位图复制2026-10-22).

### Formal result (2026-09-29 to 2026-09-30)

The formal self-contained `win-x64` run completed successfully in 29,351.206 seconds (8 h 9 min 11 s). One process completed all 48 ten-minute hold phases and exited with code 0. The runner independently validated 28,848 JSONL samples; every phase passed both budgets: PWS median strictly below 60 MiB and Private Bytes median at or below 159.4072265625 MiB. The session summary reports `soakStatus = passed` and `allHoldMediansPassed = true`.

The first-third versus last-third medians were 28.533 versus 28.547 MiB for PWS and 62.494 versus 65.240 MiB for Private Bytes. First-to-last sample deltas were -12.496 MiB PWS, +4.137 MiB Private Bytes, -98.570 MiB working set, +98.570 KiB managed allocation estimate, -10 handles, 0 GDI objects, and -3 USER objects. These trend values are observations rather than additional pass/fail limits.

Evidence: `artifacts/verification/memory-soak-8h-20260929/session-20260929-142156-748-7b4922fc/soak-summary.json`. The summary binds the completed process report and raw JSONL file with SHA-256 values. Its explicit conclusion remains `not-proven: an eight-hour bounded run and trend deltas cannot prove the absence of every leak.` VMMap native-memory classification is still unavailable because no approved VMMap installation was present.
