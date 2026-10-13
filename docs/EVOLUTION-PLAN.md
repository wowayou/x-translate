# 下一阶段实施与验收（工作文档，尚未整体定稿）

## 需求与进度

本文件跟踪 2026-10-13 新一轮需求。不能把单个阶段的完成或模拟接口通过称为整个产品定稿。

| 阶段 | 对应需求 | 完成条件 | 状态 |
| --- | --- | --- | --- |
| A：可用性与安全基础 | AI 请求失败、更多语言、自定义取词快捷键、可定位错误、密钥保护、资源约束 | 地址规则/预览、语言目录与服务映射、快捷键冲突保护、脱敏诊断、响应大小限制；Core + WPF 回归；同条件前后基准 | 已验收（未发布） |
| A2：便携与轻量 | 便携版不写注册表；轻量化（二选一时优先便携） | 程序不写 HKCU/HKLM；不写 AppData/%TEMP%；文件数与体积下降且内存/启动不回退；两种包 smoke 通过 | 已验收（未发布） |
| B：多协议与服务配置 | OpenAI Chat/Responses、Anthropic Messages、Google Gemini；多个自定义服务和默认模板 | 独立服务配置与凭据；真实协议适配；不借兼容地址冒充原生协议；安全传输与错误/取消/流式合约测试；SDK 依赖和资源验收 | 已验收（未发布；OpenAI Responses 不纳入，见下） |
| C：交互与系统定稿 | 进一步优化 UI/交互、架构与可维护性，建立设计/交互/性能系统；版本号与更新机制 | 设置分区导航；可执行的错误下一步；设计令牌收敛与对比度；版本单一来源与应用内更新；发布验收记录 | 实施中（见 C 验收） |

不在用户不知情时：调用付费接口、改变密钥接收主机、导入用户正文到测试、上传诊断包、停止用户正在运行的实例、覆盖已发布产物。真实服务测试需要单独确认固定测试句和调用上限。

## 已确认的故障线索

- 当前实例使用开发目录程序，配置选择 AI、开启流式，填写的是自定义 HTTPS 根地址。
- 现有实现直接向该根地址 POST，设置校验只验证 HTTPS，不区分基础地址与 Chat Completions 路由。
- 最近三次失败日志只有 `TranslationException`，没有错误分类/HTTP 状态。2026-10-13 经用户授权的真实自检已确认新路由可用（见下方验收结果）。
- 配置文件的明文 AI key 字段为空，已存在加密凭据。没有输出、解密或发送用户密钥。

## 多方调研（2026-10-13 实际查阅）

| 来源 | 已核实的信息 | 采用/限制 |
| --- | --- | --- |
| [OpenAI 官方 .NET SDK](https://github.com/openai/openai-dotnet) | 支持自定义基础地址、Chat/Responses、异步流式；当前 README 说明部分 HTTP 错误默认最多额外重试 3 次，包含运行时遥测头 | B 阶段优先候选；必须关闭自动重试和可选遥测，适配现有错误脱敏/流式完成边界后才能替换现有 Chat 路径 |
| [Anthropic 官方 C# SDK](https://github.com/anthropics/anthropic-sdk-csharp)、[官方文档](https://platform.claude.com/docs/en/api/sdks/csharp) | 官方包从 v10 起为 Anthropic SDK；支持 BaseUrl、Messages 流式；默认重试 2 次、默认超时 10 分钟 | 不能直接采用默认行为；需固定版本、MaxRetries=0、应用级有界超时、禁止环境变量意外替换配置 |
| [Google Gen AI .NET SDK](https://github.com/googleapis/dotnet-genai)、[Gemini API](https://ai.google.dev/api/generate-content) | 支持 .NET 8；Gemini 使用 generateContent/streamGenerateContent，不是 Chat Completions；客户端可从环境读取基础地址和云端配置 | 优先候选；只在用户选择 Gemini Developer API 时使用，不隐式切换 Vertex/云账户；需验证 HTTP transport 可注入、密钥不进 URL、重试与大小限制 |
| [DeepSeek 官方接口说明](https://api-docs.deepseek.com/) | 示例把根地址交给 OpenAI SDK，由 SDK 拼接 `/chat/completions`；明确区分 OpenAI 与 Anthropic 基础地址 | 确认“基础地址≠最终请求路径”。A 阶段对已知根地址/`v1` 做确定性解析并预览，不扫描多条路由重试 |
| [Apple iOS Feature Availability](https://www.apple.com/ios/feature-availability/) | Translate App 与 System-Wide Translation 的语言清单不同，包含西班牙语、意大利语、荷兰语、印地语、泰语等 | 作为语言覆盖参考，不把 Apple 引擎或功能可用性移植为本项目保证；软件界面语言与翻译目标语言分开 |
| [DeepL 语言文档](https://developers.deepl.com/docs/getting-started/supported-languages) | 支持按资源查询语言，能力可能更新，不能由“样式规则语言”反推所有翻译能力 | 静态目录保守映射；未验证能力不自动降级成别的语言；B 阶段再考虑显式能力刷新，不能启动即发送鉴权请求 |
| [Win32 RegisterHotKey](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey) | 支持修饰键与 MOD_NOREPEAT；F12 始终保留给调试器 | 复用 Win32 API；快捷键解析/注册生命周期单独测试，先注册新组合再释放旧组合 |

这些是特定日期的文档观察，不保证最新 SDK 默认值永远不变。官方 SDK 是优先候选，不等于已经安装或集成；依赖大小、默认网络行为、许可证和实测结果必须进入 B 阶段准入记录。不会为了“避免造轮子”一次性引入全套未验证 SDK，也不会另造可执行脚本/任意 HTTP 模板平台。

## 当前方案选择

- 保留 .NET 8 / WPF 与已验证的软件渲染、托盘及选区读取，不重写技术栈。
- 先拆出纯核心策略（地址解析、语言目录、快捷键声明/生命周期），保持 WPF 只负责绑定与原生边界。后续协议适配器返回统一结果/增量/错误，不让 SDK 类型进入窗口层。
- B 的“自定义服务”限定为**已支持协议的命名配置**：独立 ID、协议、基础地址/明确完整路由、模型、流式选项、凭据引用。内置模板不附带任何公共密钥；切换主机不沿用旧密钥；不执行用户脚本。
- 继续一次显式操作一次请求，默认不跨服务回退（2026-10-13 用户要求改为可选的有序回退，默认关闭，见「回退决策」）、不自动探测模型/路由、不自动付费测试；禁止跟随重定向携带正文或凭据。
- 不再使用序列化含密钥配置的方式制作运行时快照。磁盘仍由 DPAPI（Windows Data Protection API，按当前用户保护数据）加密；同一 Windows 用户下的恶意进程/管理员/内存转储不在其保护范围内。
- 设计与交互规范当前以 `INTERACTION-REFERENCES.md` 为入口，性能方法与历史测量用 `PERFORMANCE.md`；`DESIGN-SYSTEM.md`、`ARCHITECTURE.md` 将在 C 阶段按定稿接口创建，当前尚不存在。不复制旧结论为新版本达标证明。

## A 阶段验收与基线

- 构建与测试必须使用隔离输出，保持当前运行的开发 EXE 和 `data/` 不变。
- 基线：Core 31/31、WPF smoke 28/28、Release 0 warnings / 0 errors。
- 基线 UI 报告：`artifacts/verification/foundation-baseline/run-20261013-120943-1323c4ec/ui-smoke.json`。
- 基线三轮短测：`artifacts/verification/foundation-baseline/performance/benchmark-launch-summary.json`；最终代码使用同机同负载重复三轮，报告实际差异而非先宣称优化成功。
- 必测：根地址、`v1`、完整地址、错误协议路由、查询串与凭据 URL；西语持久化、服务语言不支持不发 HTTP；快捷键非法/保留键、冲突/恢复/释放；响应上限、错误码/状态脱敏、重定向禁止；设置保存/取消、主机变化密钥隔离；流式旧请求保护回归。

验收进度：A、A2、B、回退已验收；C 交互/版本/更新已验收，架构文档、DPI 截图与新负载长测待完成。整体仍为部分完成。

## A / A2 验收结果（2026-10-13）

- Core **43/43**；Release 构建 0 warnings / 0 errors。
- WPF + Native smoke **34/34**（基线 28）：新增语言目录与不支持语言不发 HTTP、地址预览/原生协议提示/换主机清密钥、自定义快捷键保存与取消、F12 拒绝、HTTP 404 脱敏诊断且日志不含地址/原文/密钥。报告：`artifacts/verification/foundation-a/run-20261013-130249-4994e62e/`，已查看 `.diagnostics.png`。
- **现场故障闭环**（用户授权，1 次调用，固定句 `Hello, world.`）：配置地址 `https://api.deepseek.com/` 解析为 `/chat/completions`，流式成功（4 个增量，1615 ms）。确认旧版失败根因是直接 POST 根地址。报告：`artifacts/verification/foundation-a/live-ai/live-ai.json`（不含密钥）。用于自检的 `settings.json` 副本已删除。
- **便携**：portable 包跑 smoke 前后导出 HKCU / HKLM\SOFTWARE 对比，HKLM 无变化；HKCU 仅 `MuiCache` 多出该 EXE 的 FriendlyAppName/ApplicationCompany，由 Windows Shell 在启动任何新 EXE 时写入，不是程序代码。源码无 Registry API 调用；未发现 `%TEMP%\.net` 或 AppData 下的 XTranslate 目录。真实托盘图标的 Explorer 记录（TrayNotify）未测：普通模式启动会唤出用户正在运行的实例。
- **轻量**：`build.ps1 -Flavor Portable|Lite`。portable 7 个文件 / ZIP 61,287,962 bytes（0.2.1 为 466 个 / 72,549,727 bytes）；lite ZIP 553,003 bytes。两包 smoke 均 34/34；启动与内存不回退，对比表见 `PERFORMANCE.md`。单文件压缩因私有内存约翻倍而否决。
- 未验证：只读目录下的保存失败提示（仅按代码路径 `StatusSaveFailed` 确认）；跨机器 DPAPI 失效路径沿用既有 `StorageWarning` 逻辑，本轮未在第二台机器实测；新负载的 8 小时长测。

## B 验收结果（2026-10-13）

决策：

- **不引入官方 SDK**。实测在空项目加入 OpenAI 2.14.0、Anthropic 12.55.0、Google.GenAI 1.26.0 后发布目录多出 26 个 DLL、约 22 MB（含 Newtonsoft.Json、Google.Apis.Auth、System.Management、Extensions.Hosting 抽象），会使 lite 包从约 0.5 MB 增至 20 MB 以上、portable 文件数从 7 增至 30+，且三者默认自动重试需逐一关闭。三种协议均为一次 POST + JSON/SSE，复用既有有界 SSE 读取器（`SseReader`）直接实现；协议适配集中在 `AiProtocolAdapters.cs`，日后需要时可替换为 SDK 而不改窗口层。
- 协议：OpenAI 兼容 Chat Completions、Anthropic Messages（`x-api-key`、`anthropic-version: 2023-06-01`、`max_tokens` 4096）、Gemini generateContent / streamGenerateContent（`x-goog-api-key`，`alt=sse`）。OpenAI Responses 未纳入：OpenAI 官方仍提供 Chat Completions，所有兼容服务也只支持后者。
- 完成判定：Anthropic 需 `stop_reason` 为 `end_turn`/`stop_sequence`，流式另需 `message_stop`；Gemini 需 `finishReason: STOP`，流式在 STOP 后正常结束；thinking / thought 内容、工具调用、拒绝、截断、安全拦截都不当作译文。Gemini 模型名进入 URL 路径，限制为 `[A-Za-z0-9._-]`。
- 服务配置：`UserSettings.AiServices` 命名列表 + `ActiveAiServiceId`；密钥按服务 ID 存于 `AiSecrets`（DPAPI），模板不带密钥；`TranslationSettings.OpenAi*` 字段保留为当前服务的运行时镜像以兼容旧 `settings.json`。旧单服务配置首次加载自动迁移。
- 无法解密的密钥（便携文件夹换机/换用户）改为**保留密文**并提示，不再阻止无关设置的保存；换主机或填写新密钥时才替换。此前行为是拒绝保存。

验证：

- Core **54/54**（新增 11 组：两种协议的路由/鉴权头/请求体、完成边界、thinking 过滤、流式事件、截断/拒绝/错误事件、HTTP 401/400/404/429/529 分类、流式类型、模板与配置复制）。向复制源码注入两处缺陷（接受 `max_tokens`、不过滤 Gemini thought）后 5 项测试按预期失败。
- WPF + Native smoke **39/39**（A 阶段 34）：旧配置迁移与不可解密密钥保留；通过真实设置窗口从模板添加 Anthropic、Gemini 并分别加密保存；选择服务后分别以原生协议请求且只带本服务密钥；换主机清密钥、切换服务不串密钥、取消不改配置；不完整服务拒绝保存；删除当前服务同时删除密钥并重选剩余服务。报告：`artifacts/verification/services-b/run-20261013-151902-8d0eae72/`，已查看 `.services.zh.png`。
- 真实服务（沿用授权，1 次调用，固定句）：用户现有 `settings.json` 的临时副本迁移为「DeepSeek」服务后流式成功，1702 ms；原文件未改动，副本已删除。报告：`artifacts/verification/services-b/live-ai.json`。Anthropic 与 Gemini 无用户凭据，**未做真实调用**，仅为按官方文档的模拟合约测试。
- 短基准 3 轮：启动 703 ms、可见 idle WS 106.8 MB / Private 41.3 MB、popup p95 13.5 ms，相对 A 基线无回退（`artifacts/verification/services-b/performance/`）。

## 回退决策（2026-10-13，用户要求）

原则由「从不跨服务回退」改为「可选的有序回退，默认关闭」。实现：`TranslationFallback`（Core）按调用方给定顺序尝试；所选服务总在第一位；请求本身的问题（空/超长/缺目标语言）与取消不换服务；配置类问题（缺密钥/模型/地址、协议不符、不支持的目标语言、本地限流期）跳过且不发 HTTP；最多实际发送 3 次；单候选时与原单服务行为完全一致（仍抛原异常）。顺序与勾选保存在 `UserSettings.FallbackOrder`，随服务增删自动规范化。流式尝试失败时先销毁其预览再试下一个。

验证：Core 60/60（新增 6 组：单候选不变、严格顺序与逐次报告、跳过不计数、3 次上限与首错保留、请求问题/取消不切换、每次独立进度与去重）；WPF + Native smoke 44/44（新增 5 项：默认关闭/排序/持久化/取消、提示列出全部接收方、AI 失败改用 Google 并说明原因且日志不含内容、流式中断先清半成品、全部失败逐一列出、关闭时不切换）。验收中发现并修复：重建 AI 服务列表后主界面下拉框失去选中项；「已依次尝试」把跳过的服务算作已请求。报告：`artifacts/verification/fallback/run-20261013-161825-efadf60d/`。

## C 验收结果（2026-10-13，部分）

版本与更新决策：

- 版本 0.3.0（新增功能，非破坏性）。`<Version>` 为唯一来源；发布脚本把版本类型（Portable/Lite）写入程序集元数据 `UpdateFlavor`，开发构建为 Dev 且不自我更新。
- 发布仓库 `wowayou/x-translate` 经核实为**私有**，匿名访问 Releases API 返回 404；不在应用中内置令牌。因此提供「从文件更新」（始终可用）与在线检查（仓库公开后自动生效；当前明确提示不可访问）。在线检查默认关闭（访问 GitHub 会暴露 IP 与版本号）。
- 完整性：在线更新必须有 GitHub 附件 `digest`（SHA-256）并逐字节校验；只允许 GitHub HTTPS 主机，手动跟随最多 5 次重定向；大小上限 256 MB。未做代码签名：SHA-256 防传输损坏/篡改附件，不防发布账户被盗。
- 原地替换：旧文件改名 `*.old` → 移入新文件 → 失败全部还原 → 新进程等旧进程退出后清理。只处理扁平 ZIP；拒绝路径、`data`、`*.old`、超大与超量条目；版本类型须一致，版本须更新（拒绝降级）。

交互：

- 设置改为左侧分区导航（通用 / 翻译服务 / 失败时换服务 / 隐私与数据 / 关于与更新）；校验错误切换到问题所在分区并聚焦字段。
- 状态栏「下一步」按钮：配置类错误「打开设置」直达出错服务；临时错误「重试」。未设置密钥的服务在下拉中标注，选中时先说明。
- 非模态通知条（有新版本 / 更新已就绪需重启）。
- 设计令牌收敛到 `App.xaml`，XAML 中不再有颜色值；对比度计算见 `docs/DESIGN-SYSTEM.md`。验收中修复：选中分区的粗体被整页继承。

验证：

- Core **67/67**（新增 7 组更新测试：版本比较、资产与摘要选择、私有源不重试、仅 GitHub 重定向与 SHA-256、暂存安全、占用文件改名替换与清理、失败全部还原）。
- WPF + Native smoke **48/48**（新增 4 项交互验收）。报告：`artifacts/verification/c-ux/run-20261013-164709-2cdff9e4/`，已查看 `.settings.zh.png`、`.about.png`、`.interaction.png`。
- 端到端更新（真实便携版二进制，临时目录）：错误摘要拒绝且不改动；运行中的 0.3.0 原地替换为 0.3.1，`data/` 不变；下次启动清理 `*.old`；同版本/旧版本拒绝。报告：`artifacts/verification/update-e2e/update-e2e.json`。
- 未验证：在线检查与下载的真实 GitHub 往返（仓库私有）；「立即重启」的完整前台链路（端到端测试用无界面模式替换，未经单实例互斥交接）。
