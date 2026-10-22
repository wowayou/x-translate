# Q3 本地 OCR：实施与阶段验收

状态：**Q3.1 引擎、Q3.2 截图／翻译对照已验收；Q3.3 本地发布形式／更新、有限复杂生成图及滚动界面有阶段证据，但当前包 OCR 资源短测未过 60 MiB 预算，真实跨 DPI 与八小时仍未完成，复杂版面准确性仍有失败，未发布**。Q2 已提交 `bab88b9`，Q3.1 `01edb4a`，Q3.2 `faa0e2c`。不运行 QTranslate、不反编译、不调用云 OCR／真实翻译，不安装用户运行环境或语言包。

## 边界与交付切片

1. **Q3.1 引擎适配（已验收）**：.NET 10 使用 Windows 已安装的 OCR 语言，明确选用语言，图像／像素／输出有界；一次本地识别、取消／超时／忙状态明确，不重试／换语言，阶段结果见下。
2. **Q3.2 现截图／现翻译与原文对照（已验收）**：按用户追加要求，主流程改为选择本地识别语言与翻译接收方 → 点击「框选并翻译」→ 框选当前显示器 → 内存截图／本地识别 → 最多一次所选服务请求。无需保存或打开图片。原图、可编辑识别文字、译文在同一窗口对照；取消不改主窗，不保存截图。也可主动关闭本次会话的立即翻译，仅识别后手动翻译；静态 PNG 为辅助入口。
3. **Q3.3 发布形式与环境覆盖（部分验收）**：两种包、基准和数据保护有独立证据；2026-10-22 补两版已发布0.3.0实包通知栏前台升级，并修复当前版本退出后再替换的更新时序（两版119/119、6条重启／退出及失败边界）。后续两版121/121、重复识别修复与有界OCR负载短测见文末；旧Portable迁移限制、长测、真人跨 DPI和复杂图片仍保留；不推送或发布。

## 依赖选择与本地实验

证据目录 `artifacts/verification/ocr-q3/feasibility/`。官方 Windows SDK 投影包 `Microsoft.Windows.SDK.NET.Ref 10.0.19041.57`：下载约 12.1 MiB，通用 Windows API DLL 24,877,600 bytes，另有 WinRT runtime。只读清单见 `sdk-package-review.json`。没有将完整投影直接加入 WPF，也不提高整个 App 的目标 TFM 或使用 PowerShell 子进程作兜底。

使用官方 C#/WinRT（C# Windows Runtime projection，自动生成 Windows 运行时 API 到 C# 的接口适配）`2.3.1`，按 OCR／像素缓冲／Language／Async 所需类型生成独立的小投影并保留官方 `WinRT.Runtime`。生成器 MIT 许可随发行保留；Windows SDK 元数据仅用于构建，具体 package／winmd 版本将固定，不能依赖构建机自动挑选最新 SDK。相关官方使用、互操作及嵌入说明仅存 `artifacts/references/ocr-projection-q3/`。

有限实验实际通过：在 .NET 10 进程（不是 PowerShell）生成 `960×160` 英文像素图，明确 en-US，识别结果 `HELLO WORLD 123`；独立投影程序集实验为 250,368 bytes，构建零警告／错误（`scoped-image-runtime.log`）。这是候选适配可行性，不是产品验收或真实屏幕准确率。宽泛类型过滤产生缺失依赖／重复类型的失败日志已保留；未把失败的 embedded runtime 配置放入产品。

## Q3.1 引擎适配阶段结果（2026-10-21）

实现 `src/XTranslate.Ocr.Windows/`，Core 合约 `LocalOcr.cs`；App 仅新增依赖，尚无入口也不在启动时初始化引擎。正常 TFM 不变，元数据包固定 `10.0.19041.57`，构建不依赖本机 SDK 路径；官方 runtime 的传递依赖必须进入 App／测试输出，首轮错误配置 `PrivateAssets=all` 导致真实进程无法加载 runtime，失败日志 `adapter-tests-first.log` 保留，修正后从真实输出运行验证。

- 有界 BGRA（蓝／绿／红／透明度四通道）输入：维度最多 8192、像素积最多 8,000,000，严格 4 bytes／像素，原图不修改；请求进入 gate 后同步复制，调用方立即改图也不污染已接收快照，工作完成清空内部像素。
- 输出最多 20,000 UTF-16 字符，空白／超长／无效 Unicode 明确失败，无静默截断／修正。语言必须明确，只接受引擎列出的已安装项，不自动检测／回退。原始中文引擎有字间空格，保持原样；测试仅为有限中文文本对照另去空格，不能宣称空格／排版准确。
- 一个进程仅一项后台操作，默认 10s deadline；取消／超时不再次识别、不提前释放仍运行的原生操作和 bitmap。原生取消失败不据此释放所有权，等真实 completed 才清理。若系统引擎永久阻塞则本进程后续 OCR 明确 Busy，需退出应用；不能强制终止系统调用。错误不含用户内容、路径或原始异常。语言枚举也受同一 gate／deadline 限制。

实际验收 `adapter-final.log`：独立项目／App Release `-warnaserror` **零警告／错误**，Core **143/143**（新增 2 组大小／格式合约），OCR **7/7**（6 组固定 backend＋1 组真实系统样本），真实 WPF 旧功能 **94/94**（`adapter-final-ui/run-20261021-032803-cfcecc82/ui-smoke.json`）。本轮更早带 Native 全组 **100/100** 记录于 `adapter-ui/`；最终这份适配改动未重新做 hook 全链路性能，存储 **44/44**、Diagnostics **10/10**（`adapter-storage-diagnostics.log`）。

固定 backend 覆盖构造零调用、预取消、同步快照隔离、语言不支持零识别、I/O／输出错误、没有重试、超时／显式取消返回与后端忽略取消、跨实例 Busy 及真实结束后 gate 释放。真实系统 en-US／zh-Hans-CN 各识别同一生成图两次，并检查原像素 SHA-256 不变、`zxx` 拒绝、空白图片明确 EmptyText；无用户图片／屏幕／剪贴板。测试 native lane 的两种语言和次数固定，有外层 20s 等候；它不评估全语言准确率，也未抓包证明 Windows 自身没有遥测。

实际 App 输出：OCR 投影＋适配器 DLL **253,952 bytes**、WinRT.Runtime **598,840 bytes**（合计约 833 KiB），完整 `Microsoft.Windows.SDK.NET.dll` 不存在，输出含 MIT 许可。仅此构建形态；正式便携／轻量包、更新及长时空闲内存尚未验收。这个阶段不新增全局截图热键、屏幕捕获、文件打开或后台任务，不宣布完整 OCR 交付。

## Q3.2 实际使用与已实现边界

入口为主窗标题区的截图 OCR 按钮。首次需明确选择本机已安装的识别语言；翻译服务和目标语言从主窗快照预填，可在 OCR 窗口独立修改而不写设置。具备翻译接线时显示「框选并翻译」，默认勾选本次会话的立即翻译；**打开窗口、选语言／服务、切换主题或勾选开关本身都不读取屏幕／发翻译请求**。一次明确框选动作才允许读取像素、识别及发送，接收主机和目标语言始终显示在按钮附近。

- 一次框选只捕获当前鼠标所在显示器，坐标均为物理像素；4×4 像素以下、跨屏、尺寸超过 8192 或像素积超过 800 万明确拒绝，不缩放／裁剪成“成功”。框选期间 Escape／失焦取消，释放鼠标捕获；未选择区域不读取像素或识别。
- 框选前暂时隐藏本次 OCR／主窗的原生 HWND（窗口句柄），不调用会结束 `ShowDialog` 的 WPF Hide；框选窗口关闭后等待合成器完成，再捕获。取消／失败均恢复原窗口，不重新初始化本地语言，不截入自己的标题栏、背景材质或对照内容。
- Q1 排除策略在读屏前应用于**全部与选区相交的可见、非最小化、非 cloaked 窗口矩形**；包括被其它窗口遮挡的已排除应用，故可能保守误拒绝。枚举最多 4096 个顶层句柄、选区内最多 512 个窗口；无法读取窗口边界、非空名单下进程名未知、超限或规则无效明确拒绝，不只凭初始前台应用背书。捕获前后核对窗口句柄、进程、边界和名称；变化即清空丢弃截图，不识别或发送。
- 这仍是便利过滤而非系统隐私沙箱：枚举和读屏不是原子操作，无法证明没有瞬时窗口或同一窗口内容变化；窗口透明／圆角／阴影可能包含背后像素。用户必须避开敏感屏幕内容。捕获后再次核对只能阻止后续识别／发送，不能撤销已经发生的本地像素读取。
- GDI（Graphics Device Interface，Windows 图形设备接口）使用有界顶向下 BGRA DIBSection（设备无关位图）；`BitBlt` 后先 `GdiFlush` 再读像素指针，不能拿 `DwmFlush` 的合成器同步替代。检查原生 bitmap／DC 释放结果，失败丢弃结果并报告 CleanupPending，不静默忽略。
- 截图原图保持原样；识别输出保留空格／换行，不自动纠错。左侧截图＋可编辑识别文字，右侧完整译文，是**并排对照**，不是把译文覆盖回图片、逐行自动对齐或替换源应用内容。修改识别文字／切换服务或目标会使旧译文不可复制；超长输入可保留编辑，但不发送／导入超过 3000 字符的原文。
- 翻译复用现有 `TranslationService`，只向当前选择发送识别文字，**不上传截图、不走普通自动回退、不自动重试**，保留现有 20s 总请求超时和响应大小边界。本步非流式；失败保留截图／原文但不显示完整译文，取消／关闭／变更代次阻止迟到结果。AI 费用仍可能发生，一次请求不是金额硬上限；取消也不保证远端立即停止计费。
- 本地重新识别与 PNG 打开不会隐式翻译；仅明确框选并翻译／手动翻译按钮可发送。静态 PNG 辅助入口上限 16 MiB；解码前按固定尺寸／像素预算校验块 CRC（Cyclic Redundancy Check，循环冗余校验，用于发现块损坏）及关键顺序，最多 4096 个块。缺 IEND、尾随内容、坏 CRC、未知关键块、非连续 IDAT、非法颜色／调色板及 acTL/fcTL/fdAT 动画控制明确拒绝，不让原生解码器静默修复为“成功”。不是完整 PNG 元数据或压缩像素验证，仍需真实单帧解码器。拒绝其它格式、UNC／网络驱动及链接路径；预览和识别都使用同一白底展平后的像素。用户手动选择网络路径的文件选择对话框不属于程序提供的零网络保证。
- 截图、PNG 和识别文字不写入应用设置／诊断／历史；关闭后解除预览引用、清空自有像素／文字。不承诺 CLR／系统图形缓存已即时安全擦除。默认空闲时不会注册截图后台任务或初始化 OCR；本步未新增全局截图快捷键。

## Q3.2 失败证据、实际修复与隐私边界

本轮未用“模拟通过”掩盖真实失败，证据根目录为 `artifacts/verification/ocr-q3/`：

- 最小 OCR 窗口被顶部自动高度挤压，`screen-acceptance-second.log` 原布局断言失败；改为中间内容可滚动、560 DIP 最小内容／280 DIP 对照区、底部操作固定，保持原 60 高译文断言。长内容不必同时装进最小窗口。
- 原生框选关闭时失焦又调用 Close，造成关闭重入异常，不能被进程内普通 catch 捕获；原 Windows 事件记录 `screen-close-native-error.json` 保留。现有单一取消入口和 Closing 状态保护，新增重入取消用例；并去掉初始化中强制 SHOWWINDOW，避免在 ShowDialog 建立前提前激活。
- 先前把 512 选区窗口上限用在全部句柄枚举，本机 788 个句柄／52 可见窗口时合法截图提前失败（`window-count-probe.json`、`screen-close-fixed.log`）；改为两个有界计数，不提高请求超时或取消故障检查。
- 早期 WPF／Fluent 测试窗口的“归属”并不能保证已完成不透明绘制，首次真实捕获混入背景内容。**该轮仅请求本地模拟翻译，没有调用真实翻译／OCR 接口**；含原始截图／正文错误报告的自有证据已可逆移到忽略的 `artifacts/private-verification/ocr-mixed-background-20261021/`，不用于共享证据或发布包。隔离后没有继续读取其中正文作分析，也没有删除或覆盖原证据。
- 后续真实验收改为明确不透明 GDI 自有窗口、固定黑白生成文字，读屏前核对可见／非 layered 及选区内窗口归属，捕获后必须与预期裁切图**逐字节完全相等**才进入 OCR／mock 翻译／截图报告。差异失败时只记录计数／布尔／错误类型，立即清空丢弃像素，不保存或输出内容。48 字节差异实际定位到最后四行两侧边角，避开 Windows 11 圆角后 `728×148` 像素全部相等；不是放宽容差。`GdiFlush` 属必要内存同步修复，但单独补它并未消除圆角差异。
- 资源检查原断言要求句柄数完全相同，实测 45→43 时失败（`screen-gdi-resources.log`），并非增长；现检查无正向增长，且原生每次释放返回值独立检查。测试不会因为系统同时释放其它句柄而误报“增加”。

临时逐阶段 Trace 和像素差异诊断代码已移除。失败报告不展示真实屏幕 OCR 正文，只有确定通过完整像素比对的固定内容才渲染为正常截图证据。

## Q3.2 最终验收（2026-10-21，未发布）

- 隔离 Release 应用和 OCR 测试构建 `-warnaserror` **0 warnings / 0 errors**。
- Core **146/146**（在 Q3.1 的 143 基础新增 3 组物理区域／排除／窗口快照规则），OCR **7/7**（固定 backend 6 组＋真实本地系统样本），存储 **44/44**、Diagnostics **10/10**：`screen-sealed-native.log`。
- 默认完整真实 WPF **111/111**：`screen-sealed-default/run-20261021-101721-3024f77e/ui-smoke.json`、`screen-sealed-default.log`。
- WPF＋Native **117/117**：`screen-sealed-native/run-20261021-101549-75910899/ui-smoke.json`。其中保留全部学习／原文检查／取词／配置／协议旧回归，新增图片入口 8 组、截图对照 9 组记录；不把整组数称为真实模型质量验收。
- 真实屏幕样本为自有不透明窗口内的生成 `HELLO WORLD 123`，框选 UI 完成指定裁切区域、生产窗口隐藏／恢复、GDI 读屏、逐字节比对、本地 Windows OCR、一次 mock HTTP 和对照渲染均经过同一生产接线。没有真实 AI／Google／DeepL 或云 OCR 请求；鼠标事件／选择完成采用受控 routed event／固定区域，不冒充真人跨屏操作。
- 20 次自有区域准确裁切短测，GDI 句柄 delta **-8**（无增长），174.4–175.4ms，总量不含 OCR／HTTP。原生资源释放均检查，性能不是任意屏幕／长时预算。
- 实际查看中文浅色、英文深色完整对照及最小尺寸、真实截图样本图；原文、译文、主操作／接收方及底部按钮可达。最小窗口通过外层滚动查看对照，不宣称所有内容同时可见。

覆盖：打开／改服务／语言／勾选零请求；明确框选默认一识别＋最多一请求；本地-only 模式零请求；取消／排除零读屏后续动作、未知归属／区域超限在读屏前停止；窗口变化清空结果；图像和 raw／手动修正原文不变；目标独立修改；失败／取消／服务切换和编辑中迟到结果不显示、不复活、不触发下一次发送；PNG 上限和失败保留草稿、关闭中迟到图清空；初始化一次；日志不写图片、识别原文或密钥。

**Q3.2 当时未覆盖**：发布形式已由下节 Q3.3 补充有限检查；真实收费服务费用／准确性、复杂图片／文字排列、多语言样本准确率、Win10／RDP、真人系统高对比度／读屏完整流程、原生跨 DPI 多屏框选与长期资源负载仍未验收。本任务没有推送、发布、更新本机副本或提高任何付费预算。

## Q3.3 本地发布形式与更新短测（2026-10-21，部分完成）

以下隔离发布来自 `faa0e2c`，不修改产品版本或已安装副本。restore 只使用本地 NuGet 缓存，每次自有构建进程限 120s，Release `-warnaserror`。未作在线依赖漏洞审计。证据仍位于 `artifacts/verification/ocr-q3/`，不覆盖旧报告；ZIP 在执行测试前生成，无测试／用户数据。

| 发布形式 | 文件数 | 解压 bytes | ZIP bytes | Native WPF |
| --- | ---: | ---: | ---: | ---: |
| Portable | 9 | 160,575,469 | 66,423,474 | 117/117 |
| Lite | 15 | 4,756,248 | 2,368,855 | 117/117 |

- 清单与摘要：`packages/package-inspection.json`、`packages/*.zip.sha256`。两版均平铺，包含 CsWinRT 许可和 SQLite 原生依赖；Lite 含小 OCR 投影及 WinRT runtime，Portable 的托管依赖合入未压缩 EXE，无完整 `Microsoft.Windows.SDK.NET.dll`。本地测试产物不是可下载的正式发行。
- Lite：`package-lite-ui/run-20261021-104143-a0344028/ui-smoke.json`。Portable 首轮自有测试窗口被遮挡，在读屏前安全拒绝，保留 `package-portable-ui/` 失败；一次有界复查 `package-portable-recheck/run-20261021-104427-f5a9337c/ui-smoke.json` 通过，未放宽窗口／像素断言、超时或增加服务重试。
- 两版各三轮基准均成功、子进程 exit 0：`package-lite-benchmark/`、`package-portable-benchmark/`。Lite 外层包装最初误读遗留 `$LASTEXITCODE` 报失败，依据三个新报告及自有子进程结果确认成功，没有重跑／丢弃成功样本。同工具链前后交替对照另有 12 个成功进程，详见 [性能记录](PERFORMANCE.md#2026-10-21-q3-发布形式短测与-ocr-接入前后对照)。它不测 OCR 启动／识别性能或长期资源负载。
- 更新：`update-e2e-final/update-e2e.json`、`update-e2e-complete.log`，真实两版 0.4.0→同源码临时 0.4.1 ZIP；**9 个检查项、11 次自有子进程操作**，每个限 45s、均未超时，预期拒绝的 exit 1 不当成功升级。涵盖摘要篡改、部分文件已替换后锁文件回滚、升级、清理、拒绝降级及 Lite 缺运行时拒绝。两份 rollback 证据实际观察 `.old` 重命名并确认全部程序字节恢复，不是只在写入前拒绝。
- 固定数据为 `update-data-fixture/` 四文件：含词组 `account for`、语境／备注／复习时间／间隔／评分的 v1 学习本、空词库 catalog、嵌套二进制和固定 diagnostics；加脚本自有 settings 标记共 **5 文件**。学习本经存储测试读取器验证 `LEARNING_READ_OK`；11 次操作前后所有 data 文件（含嵌套／隐藏文件）SHA-256 均相同，不仅检查 settings。样本上限 128 项／16 MiB，逐目录有界枚举并拒绝根／子 reparse 项；不从用户数据复制。
- 验收器反例 **10/10**：`tests/update-e2e-data.Tests.ps1`，证据 `update-data-verifier-sealed/update-data-tests.json`。加载实际脚本函数，仅替换本进程的子进程启动；模拟 exit 0 但损坏学习本会令 `Run` 失败，另查嵌套文件修改／删除、隐藏文件、尺寸／条目上限及目录 junction 拒绝。它不冒充真实更新；真实更新另见上述报告。新增隐藏文件反例首轮因 Windows 拒绝以 Create 模式重写隐藏文件而失败，保留 `update-e2e-final.log`，改为 Open 模式写入；未放宽保护断言。
- Windows PowerShell 报告明确 UTF-8，中文临时路径可正常解码；子进程保留 Handle，避免快速退出丢失 exit code。执行方式与数据隐私约束见 [更新测试说明](../tools/update-e2e/README.md)。

**本轮当时仍阻塞完整 Q3.3／发布结论**：没有从已发布 0.3.0 实包升级、前台重启完整链路、8 小时稳定性或 `<60 MiB` 私有驻留预算验收，也没有真人跨 DPI 多屏框选、Win10／RDP 和复杂图片覆盖。后续升级／重启的有限进展与失败见下；已有 125%／150% 主窗跨屏测试仍不是 OCR 框选跨屏实测。

## Q3.3 真实旧包前台链路与验收器修复（2026-10-22）

证据新目录 `artifacts/verification/q3-continuation-20261022/`，不覆盖 Q3 旧报告。当前产品包仍为 `faa0e2c`；本切片只改本地验收脚本，不改用户副本、产品版本或发布资产。

- **旧包身份**：本地 `artifacts/release-0.3.0/` 两 ZIP 与已保存 Releases API 附件清单及 release notes 的 SHA-256 一致：Portable `cbd53101c3c0c4368f89ad0fd4c72e7dcb07c9517c5d90cad0d6fab0ea5b80ff`，Lite `df3f30abc92bc38082b6aa9ed7d14a189ebf70fd6fe1f4ffc8a1880746c20f74`；没有以重构建旧源码冒充发布实包，本轮不联网下载。
- **通知栏前台重启两版通过**：`restart-lite-banner-sealed/restart-e2e.json`、`restart-portable-banner-sealed/restart-e2e.json`。实际 From file 对话框接受同版候选 ZIP、原地替换、关闭设置、通知栏重启；旧 PID exit 0，新 PID 为前台，关于页显示实际 0.4.0，`.old`／staging 清理，最后按正常退出路径 exit 0。5 份额外样本（学习本、词库 catalog、nested binary、diagnostics、隐藏二进制）＋固定 settings 共 **6 文件**，三个阶段摘要逐文件相同；假 DPAPI key 可解密且不明文保存，自动功能仍关闭。单步限 15s、无真实翻译／OCR 请求。
- **设置保存重启区别**：`restart-lite-settings-sealed/restart-e2e.json` 成功，允许 settings 保存导致 JSON 格式／密文字节变化，语义偏好和假凭据保留，其余 5 文件不变。该成功样本的数组证据被 Windows PowerShell 包装为 `value/Count`，原始字节保留、不能称作标准平铺 schema；修正 Snapshot 的显式数组枚举后，两版 Banner 最终报告 schema 正常。外层最初误读遗留 `$LASTEXITCODE` 报错，依据本次 report.success 确认成功，验收脚本现显式返回 exit 0；不丢弃或重复成功样本。
- **真实 Portable 故障不关闭**：`restart-portable-settings-sealed/restart-e2e.json` 已达到 update-ready 和前台，但设置页保存重启后旧进程未正常退出，结果失败；没有增加超时或改从 Banner 来冒充 Settings 成功。`headless-legacy/update-e2e.json` 在第二次部分替换／锁文件回滚操作超时（15s，仅终止自有 PID，临时目录保留），不能确认回滚成功。这与之前 `release-0.4.0-rc2/legacy-probe-apply.json.failure.txt` 的延迟加载 `System.Text.Encodings.Web` 失败线索相关，但新超时未取得调用栈，**不据此认定所有新故障同一根因**。需要后续修复当前版本时序，并明确旧包迁移限制；本轮没有追溯改写已发布包。
- **验收器根因修复**：系统文件选择器的原生 Edit／Open 在 PowerShell managed UIA 中不支持预期模式，改为只对该测试 PID 标准子 HWND 的有界消息（不是全局键鼠／剪贴板）；旧 0.3.0 Cancel 仅有 x:Name，增加明确旧 ID 兼容。失败清理若强制终止本次副本则计失败。初轮失败证据保留在 `restart-lite-original/`、`restart-lite-picker-fixed/`。
- **数据边界复用与反例 14/14**：新增 `tools/update-e2e/UpdateData.ps1`，两脚本复用有界样本扫描／摘要／复制；设置保存仅豁免 settings 字节。Windows PowerShell 直接把 ConvertFrom-Json 数组送入 Where-Object 会保留整个数组，先保存到变量再枚举；最初假阳性失败 `restart-lite-settings/` 和 helper 的失败测试保留。新增真实 helper 反例查 settings-only 允许／Banner 拒绝、嵌套学习本损坏和新增文件仍拒绝，`update-data-shared-tests-fixed/update-data-tests.json` **14/14**，没有放宽学习本断言。
- **共享函数接线复验**：当前 0.4.0→临时 0.4.1 两版 `update-data-shared-e2e/update-e2e.json`，**9 checks／11 attempts** 均在 45s 内按预期完成，dataUnchanged 全 true，每次 6 文件摘要；部分替换后回滚仍逐程序文件字节相同。结果只代表当前包的这些调用，不能替代上述旧版 Portable 失败。

**该切片当时仍部分完成**：已补两版旧包 From file／Banner 真实链路，但旧 Portable 无界面／Settings 路径、安全更新时序、新版 8 小时／私有驻留预算、真人跨 DPI 多屏及复杂图片未闭环。后续当前版本时序修复见下，旧包限制不追溯取消。

## Q3.3 更新时序根因修复与独立验收（2026-10-22）

产品更新改为 `PreparedUpdate`（准备好的更新事务）：先验证平铺包／版本／运行时并生成程序摘要清单及 worker 独立副本，**运行中不替换 EXE／DLL**。重启或正常退出才启动经过再次哈希核对的 `.update-*/.update-worker/XTranslate.exe`；worker 先持有父进程 Handle、核对 PID／启动时间／程序路径，握手后等待 exit 0，持有正常单实例 mutex 至自身退出，复制独立 `.payload` 再原地替换。原始 worker 所在目录始终不动，避免单文件或 Lite 依赖延迟加载读到另一版。正常退出也安装已准备的包但不启动 UI；只有明确重启才启动新窗。

- **必要失败边界**：准备仅一项、关闭设置可取消且迟到计划不生效；暂存 payload／worker 修改或 worker 缺失时交接失败，撤销当前计划，保留旧应用并允许随后正常 Exit，**不因退出再隐式重试**。强制终止／非零父进程、启动时间／路径不符或单实例冲突不安装。最多一次 worker 启动，握手等 5s、父退出等 15s；报告原子替换只有最多 3 次 × 25ms 本地锁重试，不重试安装／HTTP。
- **回滚事实**：`RollbackIncomplete` 新错误明确区分“已回滚”与“回滚也失败”，保留 `.old`／payload，不宣称原版完整或自动运行混合版。成功结果在正常 mutex 下清理；失败／中断的 `.update-result.json` 令下次启动保留恢复证据、暂停再次准备并提示。恢复与旧版保守迁移统一在 [更新说明](../tools/update-e2e/README.md#失败恢复与旧版迁移)。无一键自动恢复，没有签名／同用户恶意篡改隔离保证；哈希检查不是文件系统原子安全沙箱。
- **构建／回归**：同 SDK 10.0.401 / runtime 10.0.12、离线 cache restore，四包每个构建进程限 120s，Release `-warnaserror` 零警告／错误。`regression-release-final/` Core **153/153**（7 组准备／完整回滚新检查）、Storage **44/44**、Diagnostics **10/10**、OCR **7/7**。数据／worker 报告验收器反例 **16/16**，`worker-data-sealed-tests/update-data-tests.json`；缺报告／中间状态／父单独成功拒绝。
- **最终两版 UI**：`worker-packages-done/` 实际 ZIP及哈希；`worker-done-ui-recheck/{lite,portable}/ui-smoke.json` 各 **119/119**，在原117回归上新增并发准备／取消和交接失败撤销检查。更早通过的 118 记录未替代最后版本；失败 `worker-done-ui/` 的生成像素不一致在 OCR 前安全拒绝，一次有界复查通过，未放宽像素断言。
- **最终生产重启／退出 6 条**：`worker-done-{lite,portable}-{Settings,Banner,Exit}/restart-e2e.json` 全 success。准备阶段程序摘要完全不变，无 `.old`；Settings 仅允许保存 settings 字节、语义假凭据／偏好保持，Banner／Exit 共 6 份 data 字节不变。前台重启旧／新 PID exit 0、关于页实际新版本、全部残留清理；Exit 单独证明安装完成但未重启，再由测试启动新窗，不能把它称为应用自动前台重启。
- **最终 6 条 UI 反例**：`worker-done-{lite,portable}-{PayloadChanged,WorkerChanged,WorkerMissing}/`。本次固定 payload／worker 校验失败或缺 EXE，显示安全错误、旧进程仍活、程序／非settings数据未变；随后正常 Exit 不再安装，零隐藏重试。不修改第三方应用、用户文件或真实凭据。
- **最终无界面更新／回滚**：`worker-done-headless/update-e2e.json` **9 checks／11 attempts** 全预期、每操作链45s，4 次准备成功的操作还实测并核对 worker exit 0／1，不把 parent exit 0 当安装成功。两版部分替换后锁文件回滚每一原程序字节恢复，全部 6 data 摘要不变；失败 stage 由产品保留，验收器只有在核对全部原字节后才清除**自有临时安装**的恢复证据继续下一项，不冒充自动产品清理。
- **非正常父进程及竞争 3/3**：`worker-parent-done-failures/worker-failures.json`，独立 `tests/XTranslate.UpdateHost.Tests` 持有自有 PID 控制非零／被强制终止／mutex 被持有三种边界；production worker 分别在 wait-parent／mutex 拒绝，原 EXE／嵌套固定 data 不变。固定父进程不是产品 UI，不把这组叫真人链路。
- **旧包到修复候选**：`legacy-done-{lite,portable}-banner/restart-e2e.json`，与发布摘要一致的 0.3.0 实包 → 最终修复 0.4.0 两版 **Banner** 前台及6 data字节通过。旧版 Portable 无界面／Settings 故障仍存在；推荐先退出旧版、在新目录解压新包并原样复制data，不能仅凭单机 Banner 结果宣称旧包可无风险原地更新。
- **真实失败与修正**：首轮 worker 只查 PID 未保留 Handle，父快速退出时失败；持有实际父 Handle 后成功。`worker-fault-lite-missing-4/` 保留 waiting结果与未提交 `.tmp`，safe fallback只记录 UnauthorizedAccessException；证据确认验收器 Get-Content 无 share-delete 竞争原子结果替换，读端共享修正＋写端3次有界锁重试后两版全矩阵通过。`worker-final-portable-Settings/` 未获前台明确失败，允许一次请求后等待原生激活交付而不注入键鼠；最终未靠放宽前台断言通过。所有失败原证据保留。

**本切片完成，但 Q3.3／发布仍部分完成**：当前版本退出后安全更新／回滚已独立验收；不追溯修复旧0.3 Portable，不把这次更新修复代替 OCR 真人跨 DPI、复杂图片、8小时或私有驻留预算。未更新用户副本、未推送／发布、无收费请求。

## Q3.3 重复识别、显示器探针与 OCR 负载短测（2026-10-22）

本切片接续 `3d892e6`，未修改版本号、用户副本或发布资产。证据仍位于 `artifacts/verification/q3-continuation-20261022/`。

- **实际产品修复**：重新识别与上次文字完全相同时，WPF 不产生 `TextChanged`，原实现因此保留旧译文／复制状态。现在成功应用每次识别结果前显式撤销旧译文；拒绝重读确认则不识别、不请求并保留有效译文。真实窗口回归还检查主动再次翻译恰好一次，不把本地重读变成隐式发送。
- **显式诊断负载**：`--memory-diagnostics --diagnostic-ocr`／`memory-soak.ps1 -Ocr` 才开启。每批3个离屏生产OCR窗口，各枚举一次语言、对固定 `960×160` 生成像素进行两次真实已安装 en-US 识别及两次进程内模拟翻译；检查原像素不变、完全相同的文字／译文、重开不重复初始化和关闭后自有像素／文字／预览清空。不读屏、用户文件或剪贴板，不发真实翻译请求／下载语言包，不强制GC或清空工作集。
- **显示器探针不是双屏验收**：`--native-checks` 枚举最多16个实际显示器，逐个检查框选 HWND 物理边界、WPF／原生 DPI、正反向选区和边框到物理像素的对应，以及越界拒绝／鼠标捕获释放；枚举失败不跳过作成功。鼠标位置和事件受控，不注入全局输入。本次只连有 **1920×1080、120 DPI（125%）单屏**，回访同一屏不证明跨 DPI 或真人拖选。
- **runner 有界及证据身份**：新增 `-ExecutablePath` 直接测指定包，先检查 Portable／Lite 部署类型，并记录顶层 EXE／DLL／runtimeconfig／deps 的摘要；结束再核对，Lite 不再只凭相同 apphost 识别代码。默认构建改为离线缓存 restore／publish、独立输出、各120s上限；执行时钟从应用启动计。超时计入保持时长、全部名义 idle／settle／测量阶段，再加有界 grace；不提高内存预算，也不把 grace 计为完成保持时长。
- **回归**：`ocr-slice-seal-regression/` Core **153/153**、Storage **44/44**、Diagnostics **10/10**、OCR **7/7**，更新data／结果验收器 **16/16**；新增runner合成反例／边界 **33/33**（`ocr-slice-seal-tests-final/ocr-soak-tests.json`），含缺批／重复／乱序、错误请求／清理／隐私标志、部署识别、同长度DLL变化及模拟子进程超时／非零退出。合成进程不冒充真实构建或OCR。
- **两版构建与最终 UI**：`ocr-slice-seal-packages-2/` 离线 Release `-warnaserror` 构建通过；Lite `Lite-ui/ui-smoke.json`、Portable `ocr-slice-seal-portable-recheck/ui-smoke.json` 各 **121/121**。首轮 Portable 新几何断言失败但未记录具体矩形，原因未确认；补安全的坐标诊断后第二轮在旧托盘焦点断言失败；同一最终 Portable 一次有界复查通过。两个失败报告保留在 `ocr-slice-seal-packages/`、`ocr-slice-seal-packages-2/Portable-ui/`，没有放宽几何／焦点／像素断言，不宣称环境偶发失败的根因已修复。
- **两版资源短测**：`ocr-slice-seal-soak/{Lite,Portable}/soak-summary.json`，串行各2批60秒保持、各122个逐秒样本，实际各约166.6–166.8s，218s外层上限内 exit 0；每版6个OCR窗口／12次真实识别／12次mock翻译。逐批 PWS 中位数 Lite **50.078／54.004 MiB**、Portable **57.371／59.777 MiB**，均严格低于60；Private Bytes 均低于既有159.4072265625 MiB上限。程序摘要不变，另用Python复算两个报告／JSONL哈希、样本顺序和中位数一致。Portable余量很小，不能据此保证长期预算。默认离线构建／无OCR路径及部署不符零子进程拒绝也实际通过（同目录 `matrix.json`）。

**失败与未闭环项仍保留**：最初 `ocr-soak-preflight/` 揭示相同文字重读的真实故障；修复后的5秒保持 `ocr-soak-preflight-fixed/` 有两批PWS超限，并未以更长保持的通过结果注销。`ocr-soak-recovery-20m/` 已写出第二批598/601个样本但被旧1260s上限终止；采样实际有调度开销，旧计算又遗漏38s固定诊断阶段。新20分钟名义配置上限为1298s，计算已测试，但本切片未重跑完整20分钟。先前单批十分钟通过证据 `ocr-soak-ten-minute-sealed/` 仍仅是短测。

构建首次CLI home的SDK日志曾出现开发证书初始化提示，不能从该提示判断是否新增证书；未执行信任或清理。后续显式禁用SDK自动证书初始化与全局工具PATH变更，新的完整离线构建／短测 `ocr-slice-seal-sdk-init-disabled/` 通过且日志不再出现该提示，详见[诊断说明](MEMORY-DIAGNOSTICS.md#ocr-workload-and-supplied-packages)。

**本切片阶段完成，Q3.3／发布仍部分完成**：八小时、最终发布基线／两种最终包性能、原生跨 DPI 双屏／真人框选、复杂图片和旧Portable迁移限制不因此消失。没有启动无人值守长测、安装用户副本、推送／发布或收费请求。

## Q3.3 有限复杂生成图、PNG 预检与滚动界面（2026-10-22）

本切片接续 `6ab55be`，接手时已有未提交 PNG／corpus 草稿；保留后补齐生产边界、独立 runner、滚动回归和实际证据。证据新根为 `artifacts/verification/ocr-complex-q3-20261022/`，不改版本号、用户副本或发布资产。

- **PNG 实际反例与根因**：`png-preflight/ui/ui-smoke.json` 明确失败，原生产解码器实际接受了六种畸形图：缺 IEND、坏 IHDR／IEND CRC、孤立 fcTL、未知关键块及 IEND 后追加内容。现在 Core `LocalOcrPng` 在原生解码前强制有限静态 PNG 包络与 CRC／关键顺序／颜色／调色板检查，拒绝动画控制和 4097th 块；声明预算仍优先、无重采样／自动修复。Core 六组覆盖每字节截断、CRC 损坏、极端长度、连续数据及合法边界（8192 维度、800 万像素、16 MiB／4096 块）。CRC 校验不是签名，不宣称所有辅助元数据／压缩像素安全。真实解码另查灰度／索引色、透明和部分 alpha 展平、300-DPI 元数据不改变物理尺寸、预览／提交像素相同、关闭清空。
- **固定规格先于识别**：`tools/ocr/corpus-plan.json` 十项固定规格与 SHA-256 嵌入 App；四项必验（多行／透明／深色／192-DPI 图片元数据），六项观察（双栏／表格／-6°倾斜／低对比度／固定噪点／中英混排）。明确 en-US／zh-Hans-CN，无语言下载或换语言。评分只删除空白后区分大小写与顺序比较，不做拼写、标点或排版修复，观察项不得提升为成功准确率。
- **生产链路与独立证据**：两版各一个新进程，在生产 OCR 窗口加载生成 PNG，预览／BGRA 输入与生成 pixels 全字节相等，每图一次真实系统识别，成功后一次明确 raw 原文的进程内模拟 Google handler 翻译。每版实际 **10 次真实识别／10 次 mock HTTP**、**11 次显式语言枚举**，无用户图像、屏幕／剪贴板或真实 HTTP。全部文件未改写、像素关闭清空、主窗原文不变。runner 复核本次 PID／版本／部署、计划及逐例等级／次数、PNG 和对照图摘要、顶层程序开始／结束摘要；Python 另复算等级及报告／PNG 摘要，两版生成图／像素／实际文字一致。

| 固定样本 | 两版结果（仅有限生成图） |
| --- | --- |
| 多行、透明、深色、192-DPI 元数据 | **4/4 必验符合**；多行被引擎串为空格，不能称换行保真 |
| 倾斜、低对比度、固定噪点、中英混排 | **4/6 观察项符合 whitespace-only 比较**；噪点原输出 `NOISYTEXT 654`、中文有字间空格，不保证空格准确 |
| 双栏 | **text-mismatch**：`RIGHT TWO 444` 识别为 `RIGHT T\NO 444`，不修正后冒充通过 |
| 表格 | **text-mismatch**：输出 `ITEM ALPHA BETA VALUE 123`，`456` 缺失且阅读顺序变化 |

- **滚动界面真实问题与修复**：用户追加滚动条要求后，`scroll-observation/ocr-scroll.json`／截图记录默认短文有 0.8–19.2 DIP 左栏溢出，长文既有文本内滚动又有左栏滚动，最小窗再叠加页面滚动。去掉左栏 ScrollViewer，原文与译文只各有一层文本滚动；对照高度按实际图像／帮助／来源及窗口可用区计算，预览可紧凑显示但不改输入像素，原文至少100／译文至少60，底部按钮固定。预留页面滚动条空间，不覆盖服务／正文；默认短文无多余滚动条，长文滚到末尾，文本到边界的滚轮才交给页面。
- **滚动验收方法不冒充真人**：两版 `ocr-scroll.json` 各 **8 个默认／最小、短／长、zh/light／en/dark 布局**，**12 张 100/150/200% 渲染图**；仅2次固定模拟识别／2次mock翻译，与真实corpus计数分开。检查无左栏额外祖先滚动、纵／横条按需、滚动条在宿主内、末尾及来源可达、底部按钮完整可见、内容／请求不变。使用 WPF Preview＋bubble 成对 routed wheel（受控路由滚轮），不是全局输入或真人鼠标；早期测试只发 Preview 导致内部未滚动，失败 `scroll-layout-4/`、`scroll-layout-5/` 保留，修正测试事件对而非放宽断言。`scroll-layout-3/` 还记录最小英文窗口右卡片末尾可达性失败，紧凑预览及实际可用高度修复后最终通过。
- **来源本地化根因**：首轮 corpus 对照图显示切语言后未检查提示仍留旧语言；新增断言在 `corpus-provenance-failure/` 实际失败。现在保存完成结果，语言切换仅重绘来源／“原文未检查”，编辑／重读／关闭清除该结果，不改变 raw 文字／译文、复制状态或次数。当前普通翻译及迟到代次边界不变。
- **最终回归与构建**：离线缓存、SDK 10.0.401／runtime10.0.12、每构建进程120s、禁首次证书／全局工具PATH初始化，Release `-warnaserror` 零警告／错误；`sealed-regression/` Core **159/159**、Storage **44/44**、Diagnostics **10/10**、OCR **7/7**，旧soak runner **33/33**、更新data／结果 **16/16**、内存阈值合成 **13/13**。新增 corpus／滚动验收器 **95/95** 合成反例，含错误等级、必验降级、缺批／重复／乱序、缺可选语言／空文本、真实请求边界、同长度文件／代码损坏、滚动末尾／栏层／截图、timeout／非零退出；它不冒充真实 OCR。
- **两版独立最终程序包验收**：`final-3-{lite,portable}/receipt.json` 为本切片程序清单，ZIP在测试前生成；Lite／Portable `final-{lite,portable}-ui/ui-smoke.json` 各 **126/126 Native WPF**，保留旧功能及新PNG／来源／滚动检查。`final-{lite,portable}-corpus/corpus-summary.json` 各 **4/4必验＋4/6观察符合**，每次180s硬上限，实际6.504／7.010s、exit0、无自动复跑；各包含8种滚动布局／12张图。Lite15文件／4,918,980解压bytes／2,423,530 ZIP bytes；Portable9文件／160,740,761解压bytes／66,478,356 ZIP bytes。包为本地隔离产物，含SQLite与CsWinRT许可，无data／截图／完整Windows投影；随包README为构建时快照，后续验收结果在仓库文档补录，不改已测包。Python复核ZIP清单、每文件与sidecar摘要和实际program身份，临时包并非正式发行。
- **新模式的失败关闭**：`--ocr-corpus --smoke-output <本地绝对新文件>` 是排他的显式模式，在 Exit／更新／Live 入口前拒绝混用或未知参数，使用smoke独立存储／mutex，不广播唤起现有用户窗。`final-cli-boundaries/cli-boundaries.json` **16/16 实际子进程边界**，覆盖混用收费／读屏／更新／退出、参数缺失／错序、UNC／相对／缺父目录／junction输出、mutex冲突及大小写参数下已有报告不覆盖；本组零识别／真实请求。运行方式与次数／隐私见 [OCR 工具说明](../tools/ocr/README.md)。
- **最终资源复查失败，不能签收预算**：相同最终程序包串行各2批60s显式OCR负载，`final-ocr-resource-short/{Lite,Portable}/soak-summary.json` 各122样本、12次真实识别／12次OCR mock翻译、约166.64s且exit0；但Lite两批PWS中位数 **61.199／61.336 MiB**、Portable **59.785／64.527 MiB**，两版预算结果均false。Private Bytes中位数77.148／80.559及76.941／81.520 MiB仍在原上限内，GDI首末差0；文件身份、原图及关闭清理、样本顺序／统计／摘要独立复算一致。观察到GC提交量比上一份短测较高、GDI未增长，**不能据此归因GC、认定无泄漏或解释本轮回退根因**。没有强制GC、清空工作集、提高60MiB阈值或重跑换取通过；失败保留并成为资源／发布阻塞项。独立复核汇总 `final-verification.json` 明确记录 `functionalEvidenceValidated=true`、`resourceBudgetPassed=false` 和当前源码／包摘要，不把它的“校验成功”当预算通过。

**功能切片及本地阶段提交完成；资源验收失败，Q3.3／发布仍部分完成**：有限复杂生成图有独立证据，不等于用户复杂图片泛化、日文或版面保持已通过；双栏／表格识别失败继续保留。本轮60MiB预算未通过，根因待查；八小时及最终发布基线／两包性能、原生跨 DPI／真人框选、Win10／RDP和旧Portable其它迁移路径仍未闭环。未安装用户副本、未启动后台长测、未推送／发布、无收费请求。

## 验收方法

- 纯边界：输入长度／像素积／编码与输出上限、语言无效／不支持、预取消、并发、超时后不能重复占用后台引擎、原图快照不变、失败不能冒充结果；固定模拟后台不调用系统 OCR。
- 真实引擎：明确语言的生成式英文、中文样本（只有已安装语言才执行），空白图；缺语言包的拒绝只证明明确请求不会换语言，不冒充无语言包机器实测。
- 验收仅使用生成图和明确自有的不透明屏幕区域；不把测试窗口句柄归属当作像素隐私保证，原生输入必须通过完整生成像素比对才能识别／记录。本步资料／NuGet 获取都是有界普通公开下载，没有计费工具、订阅、云端请求或委派子 Agent。
- 通过后检查实际 diff、依赖清单、正常翻译／学习／取词回归，更新本记录与相关现有文档，并做本地阶段提交。
