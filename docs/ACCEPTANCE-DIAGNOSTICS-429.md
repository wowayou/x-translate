# Google 429 与诊断验收记录

本记录针对 Google 非官方网页接口返回 HTTP 429 的行为，补充 0.2 验收材料；不修改既有 `ACCEPTANCE-0.2.md` 或 `PERFORMANCE.md`。

## 已验证行为

- Google 的 HTTP 429 表示该网页接口当前限制了请求，不等同于用户账户额度不足。Google 专属提示不会把该错误描述为“额度不足”；其他 provider 的原有错误文案保持不变。
- `Retry-After` 支持秒数和 HTTP 日期。缺失、非法或已过去的值使用 10 秒 fallback；长值不会被人为截断。
- 429 首次失败会建立进程内的 Google 本地冷却。冷却期间再次请求在发出 HTTP 之前失败，不自动重试，也不自动切换到其他服务。
- 取消请求不会被本地冷却错误替代；冷却到期后可再次请求。冷却只作用于当前 `TranslationService` 实例，重启应用或重建 service 不会持久化冷却状态。
- 本地冷却不能解除服务端限流；冷却结束仅表示应用允许用户再次手动尝试，不保证服务已恢复。
- 错误映射不读取或显示 429 响应正文，因而不会把敏感正文带入 UI。

## 已完成的自动化验收

Root 验收记录（2026-09-22）如下：

- Release 构建：0 warnings / 0 errors。
- Core 测试：16/16。
- UI + Native smoke：23/23，包含 Google 429 首次错误、重复请求无 HTTP，以及切换语言无 HTTP 的检查。
- 报告：`artifacts/verification/diagnostics-final-429/run-20260922-064343-7dfc5fbf/ui-smoke.json`。
- Diagnostics 既有检查：5/5（此前已通过）。
- 冻结发布复验：Core 16/16、UI + Native smoke 23/23、Diagnostics 5/5；报告：`artifacts/verification/diagnostics-frozen-publish/run-20260922-065918-bf01afc3/ui-smoke.json`。

上述结果证明的是受控 mock/本地验收路径，不是对 Google 服务可用性或持续限流状态的保证；本验收禁止以真实 Google 请求替代受控测试。

## 内存诊断边界

正式内存诊断 session 已完成，结果只描述本机受控诊断，不作为通用内存承诺：

- Session：`artifacts/memory-diagnostics/acceptance-final/session-20260922-064420-394-b986d71b`
- 参数：Runs 3 / DiagnosticBatches 5 / DiagnosticIdleSeconds 3 / DiagnosticHoldSeconds 0 / self-contained x64；三组共 9/9 成功退出。
- VMMap：用户 deferred，尚未采集。
- 20 批短趋势扩展：三组 3/3 成功，目录为 `artifacts/memory-diagnostics/acceptance-final/extended-20batch-20260922-065142-36797375`。两组 baseline 都出现驻留 PWS 下降而 PrivateBytes 上升，原因未归因；app 末批波动并回落，也不足以证明长期平台或无泄漏。该结果不证明 `<60 MB` 达标。
- 8 小时 soak：本节对应的 2026-09-22 session 尚未执行；正式运行结果见文末“8 小时稳定性正式验收”。

VMMap 未采集（用户 deferred）。因此不得据此声称内存上限、长期稳定性或 native allocation 已验证。

## 独立验收构建

另有独立验收构建目录及 ZIP：

- 目录：`artifacts/XTranslate-0.2.0-acceptance-20260922-win-x64`，466 个文件、13 个卫星/运行时目录。
- ZIP：`artifacts/XTranslate-0.2.0-acceptance-20260922-win-x64.zip`，466 个条目，SHA-256：`13CEB74B668A42BACFAFE3CF7F9C5568991A23700A8C0E2DE5E73042BDDF8FF7`。
- 来源：主诊断 session 的 `app` 发布目录；已排除 `run-01`、`run-02`、`run-03`、`smoke-data`，保留所有运行时卫星语言目录。

它们是包含 429 修复的验收构建，不替换旧的 0.2 正式包，版本号未修改。

## 未覆盖项

本记录不覆盖真实 Google 长期可用性、服务端限流解除时间、重启后的服务端状态或 VMMap 原生内存分类。8 小时持续运行稳定性已在文末单独验收；其有界结果不改变这些未覆盖项。

## 固定窗口测量改进（2026-09-29）

执行分工：Root 制定范围并独立验收，Luna 实现测量与统计。本轮验收对象是诊断能力，尚未批准新的产品内存预算。

计划与完成条件：

- 三组在预热后使用独立隐藏测量阶段：默认先等待 3 秒，再按每秒一次采样 30 秒；记录实际时间、隐藏状态和原始样本，明确首末点约定。
- 共享统计代码计算 PWS、Private Bytes、WS 的有效/缺失样本数及最小值、中位数、最大值，并记录 Private Bytes 首末变化。缺失值不得当作零，端点不得重复参与窗口统计。
- 运行成功与内存目标通过分别表达。目标口径及 Private Bytes 护栏上限待定；不凭单次低值宣称达标，不将旧数据改写为新窗口的结果。
- 覆盖统计边界与参数失败路径，独立运行三组诊断并从原始样本复算统计，复验既有诊断及模拟翻译测试。
- 保留原发布包、429 行为、GC/R2R 配置及 UI/托盘实现。VMMap 继续延期，本轮不启动 8 小时测试或真实 Google 请求。

工作区没有 Git 元数据；本轮更改前源码与文档快照位于 `artifacts/verification/memory-window-before-20260929`，用于最终逐文件差异审查。

执行与验收结果：诊断能力验收通过，内存产品目标未验收通过。

Root 独立执行：

- Diagnostics 测试 10/10；覆盖中位数精度、单个低值、空窗口、缺失 PWS、样本不足/超出及非法配置。
- App 和 baseline 新参数非法/缺值启动检查 16/16，均以退出码 2 拒绝；App 同时写出 `argument-validation` 失败报告。
- 本次隔离发布与三组运行成功，9/9 进程退出码为 0。Root 从 270 个原始窗口样本独立复算三项指标及 Private Bytes 首末差值，与单次报告及 session 汇总完全一致；检查了隐藏状态、阶段位置、实际等待时间、样本顺序与完整性。窗口实际耗时约 30.45–30.63 秒。
- 使用本次发布的 App 复验 Core 16/16、UI + Native smoke 23/23，包括首次 429 与冷却期间不再次发出 HTTP。Native 选区检查使用明确指定的后台测试控件，未验证完整前台热键链路。未请求真实 Google 服务。
- 已对照修改前快照审查全部差异；仅修改诊断实现、参数、测试与本文档及测量文档。既有翻译、托盘实现与项目 GC/R2R 配置未改动。

复现命令（本机使用已有运行时缓存，不需要下载新工具）：

```powershell
$env:NUGET_PACKAGES = Join-Path $PWD '.tools/cli/.nuget/packages'
./memory-diagnostics.ps1 -Runs 3 -DiagnosticBatches 1 -DiagnosticIdleSeconds 1 -DiagnosticHoldSeconds 0 -OutputDirectory './artifacts/verification/memory-window-20260929'
```

其余参数使用默认值：self-contained win-x64，隐藏 settle 3 秒、窗口 30 点，.NET 8.0.31。`DiagnosticIdleSeconds=1` 是本次预热/趋势阶段的配置，后续比较必须同时保留；它不改变 30 点窗口的长度。

下表为三次运行各自窗口中位数的范围，单位 MiB，**不是窗口最小/最大读数**：

| 组别 | PWS 中位数范围 | Private Bytes 中位数范围 | WS 中位数范围 |
| --- | ---: | ---: | ---: |
| app | 100.11–103.37 | 150.97–154.29 | 222.27–225.85 |
| baseline-blank | 86.10–86.74 | 130.01–132.11 | 169.04–169.36 |
| baseline-tray | 87.99–91.02 | 135.83–136.60 | 180.10–183.95 |

若使用 PWS 中位数 `<60 MiB` 作为候选目标，本次应用三次均未达到；正式目标和 Private Bytes 护栏尚未由用户确认，报告保留 `pending-definition`。本次未做内存优化，新旧采样条件不同，不能把与 2026-09-22 数据的差异解释为优化收益，也不能据此进行原生内存分类或宣称无泄漏。

证据位置：

- Session：`artifacts/verification/memory-window-20260929/session-20260929-112801-332-63f12c89/memory-diagnostics-summary.json`
- Root 原始样本复算：同目录 `root-window-verification.json`；校验脚本 `artifacts/verification/verify-memory-window.ps1`。
- 参数失败检查：`artifacts/verification/memory-window-invalid-95aab0cf/argument-verification.json`；脚本 `artifacts/verification/verify-memory-arguments.ps1`。
- 功能回归：`artifacts/verification/memory-window-functional-20260929/run-20260929-113658-e0edbdf6/ui-smoke.json`。

首次发布尝试 `session-20260929-112723-868-03914514` 因隔离 CLI home 未找到运行时缓存、NuGet 网络不可达而失败；失败摘要已保留。指定上述已有缓存后，新 session 成功，不将失败尝试计入 9 次测量。

剩余边界：VMMap 仍未采集，8 小时稳定性测试未执行；本轮数据不改变这两项的待验证状态，也不批准部署方式或 UI 架构改造。

## 渲染路径内存优化（2026-09-29）

用户已确认本阶段口径：相同条件运行 3 次，每次隐藏窗口 PWS 中位数严格小于 60 MiB，Private Bytes 中位数相对本轮优化前对照增加不超过 5%。对照值取本轮 3 次默认渲染运行的 Private Bytes 窗口中位数之中位数；候选的每次运行均须满足护栏。缺样、失败或采样条件不一致不能判为通过。

本轮计划与完成条件：保留相同 self-contained win-x64、1 批、idle 1 秒、settle 3 秒、30 点窗口、hold 0；比较默认与显式软件渲染，复算原始样本，运行本地弹窗/翻译性能对照及 UI/Core/Diagnostics 回归。软件渲染先作为进程级显式选项实现，默认策略的选择依据实验结果另行确认。无强制 GC、工作集清空、真实翻译请求或注册表修改。VMMap 与 8 小时测试继续延期。

本轮修改前快照：`artifacts/verification/memory-optimization-before-20260929`（含 SHA-256 清单）。工作区仍没有 Git 元数据，最终逐文件对照此快照审查。

执行结果：本阶段正式目标通过，并将软件渲染作为产品默认策略。

- 优化前同条件硬件渲染对照：`artifacts/verification/render-default-20260929/session-20260929-114224-512-de2e2493`。应用三次 PWS 中位数为 99.77–104.02 MiB，Private Bytes 中位数为 151.04–155.25 MiB。护栏以三次 Private Bytes 中位数的中位数 151.816 MiB 为对照，允许上限为 159.407 MiB（含）。
- 最终默认路径：`artifacts/verification/render-default-final-memory-20260929/session-20260929-135518-448-17a29757`。不传渲染开关，报告三次均记录 `SoftwareOnly`；PWS 中位数分别为 30.910、30.635、27.707 MiB，Private Bytes 中位数分别为 47.922、47.422、46.988 MiB。
- 正式判定：同目录 `memory-acceptance.json` 为 `targetStatus: passed`。三次 PWS 均严格小于 60 MiB，三次 Private Bytes 均未超过 159.407 MiB；脚本从 180 个应用原始样本（对照 90 点、候选 90 点）复算统计并校验报告哈希、运行环境、采样顺序、完整性和配置一致性。
- 验收脚本边界：`tests/memory-acceptance.Tests.ps1` 的合成夹具 10/10，通过与失败路径包含 60 MiB 严格边界、5% 含边界、超限、缺失 PWS、原始样本/统计不一致、采样条件不一致、重复报告及不完整运行。合成夹具不计入产品实测结果。
- 最终默认构建性能三次：`artifacts/verification/render-default-final-benchmark-20260929`。报告均记录 `SoftwareOnly`；离屏 1000 次 Show/Hide 的 p95 为 7.047–8.267 ms，p99 为 7.891–9.989 ms；200 次本地翻译 p95 为 0.949–1.155 ms。同期显式硬件渲染对照的弹窗 p95 为 119.55–128.29 ms；这只描述本机离屏布局完成路径，不是屏幕合成或真实前台热键延迟。
- 回归：Diagnostics 10/10、Core 16/16、Release 构建 0 warnings / 0 errors、UI + Native smoke 23/23。UI 报告：`artifacts/verification/render-default-final-functional-20260929/run-20260929-135506-7a237280/ui-smoke.json`。

实现边界：软件渲染在创建任何窗口前按进程设置；`--hardware-rendering` 仅保留给受控 A/B 诊断，脚本拒绝同时指定软件与硬件选项。单次诊断自身不能计算依赖对照 session 的 5% 护栏，因此报告使用 `not-evaluated`；只有 `memory-acceptance.ps1` 的跨 session 结果表示正式通过。

本节短时验收仍未覆盖 VMMap、真实前台快捷键链路以及其他 GPU/DPI/远程桌面环境；8 小时持续运行结果另见下一节。因此通过结果不能表述为无泄漏或所有机器上的通用性能保证。

## 8 小时稳定性正式验收（2026-09-29 至 2026-09-30）

新增 `memory-soak.ps1`：默认以最终产品软件渲染策略启动一个隔离发布进程，执行 48 个十分钟隐藏检查阶段；每阶段前包含 100 次离屏 Show/Hide 和 20 次固定进程内翻译。紧凑模式把逐秒原始样本立即写入 JSONL，应用进程只保留当前阶段及各阶段统计/端点，避免诊断对象因累计约 2.9 万个完整快照而产生人为增长。runner 在进程退出后从 JSONL 独立复算各阶段 PWS、Private Bytes 和 WS 中位数，并记录句柄、GDI、USER、托管估计的端点变化。

40.986 秒短时端到端验证通过：3 个 5 秒阶段、18 个原始样本，进程报告和 JSONL 复算一致，所有预算检查通过；证据位于 `artifacts/verification/memory-soak-short-20260929/session-20260929-141735-149-7a42b5c1`。普通非紧凑 hold 回归仍保留 6/6 内存检查点且不创建旁路文件。Diagnostics 10/10、Core 16/16、Release 构建 0 warnings / 0 errors、UI + Native smoke 23/23；UI 报告为 `artifacts/verification/memory-soak-functional-20260929/run-20260929-141844-cbe698bd/ui-smoke.json`。

正式运行位于 `artifacts/verification/memory-soak-8h-20260929/session-20260929-142156-748-7b4922fc`，已完成并通过：

- 单进程实际运行 29,351.206 秒（8 小时 9 分 11 秒），完成 48/48 个十分钟保持阶段，应用退出码为 0。
- runner 从 JSONL 独立复算并验证 28,848 个逐秒样本；48 个阶段的 PWS 中位数均严格小于 60 MiB，Private Bytes 中位数均不超过 159.4072265625 MiB。`soak-summary.json` 记录 `soakStatus: passed`、`allHoldMediansPassed: true`。
- 首三分之一与末三分之一阶段中位数：PWS 28.533 → 28.547 MiB，Private Bytes 62.494 → 65.240 MiB。首末样本变化：PWS -12.496 MiB、Private Bytes +4.137 MiB、WS -98.570 MiB、托管分配估计 +98.570 KiB、句柄 -10、GDI 0、USER -3。
- `soak-summary.json` 保存了最终报告与原始 JSONL 的 SHA-256；原始文件、配置、状态和最终报告均保留在同一 session。正式结果不使用短测样本替代长测样本。

收尾复验（2026-09-30）：重新计算 `soak-summary.json` 绑定的报告与 JSONL 哈希，并逐阶段检查 0..600 秒序列、601 个样本和三个内存中位数，48 阶段/28,848 样本全部一致；复算所得阶段 PWS 中位数范围 8.094–32.402 MiB，Private Bytes 中位数范围 53.336–66.863 MiB。随后 Core 16/16、Diagnostics 10/10、内存验收合成夹具 10/10、Release 构建 0 warnings / 0 errors、UI + Native smoke 23/23 全部通过。最新 UI 报告：`artifacts/verification/memory-optimization-final-20260930/run-20260930-004310-1de65e9f/ui-smoke.json`。

正式通过仅表示该单进程在本机完成有界负载，且所有阶段满足已批准预算。趋势值不是额外阈值；结论保持 `leakConclusion: not-proven: an eight-hour bounded run and trend deltas cannot prove the absence of every leak.` VMMap 仍未采集，因为本机没有已批准的 VMMap 安装；因此原生内存分类仍是明确边界，而不是失败被掩盖。
