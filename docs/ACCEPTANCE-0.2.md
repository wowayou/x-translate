# 0.2 交付验收记录

本记录对应 XTranslate 0.2.0 最终独立包；`docs/ACCEPTANCE.md` 的 0.1 记录保留不变。以下只记录已经实际执行或由最终包检查得到的事实，不把未完成项目标记为通过。

## 交付物与可追溯性

- 独立发布目录：`artifacts/XTranslate-0.2.0-win-x64-standalone/`
- 独立发布包：`artifacts/XTranslate-0.2.0-win-x64-standalone.zip`
- ZIP 大小：72,517,821 bytes（69.16 MiB），465 项；解压后约 160.43 MiB。
- ZIP SHA-256：`27F88E81B90D9678A1533B52115C3647E13E6D926BC091540B7CF79D83597C8A`
- UI smoke 证据：`artifacts/verification/0.2.0-final-package/run-20260920-231130-ed3342ab/ui-smoke.json`
- 应用版本：EXE 文件版本 `0.2.0.0`；包含 .NET Core / WindowsDesktop `8.0.31`。

## 已通过

### 构建、核心与功能验收

- Self-contained 最终构建成功；核心测试 12/12 通过。
- `verify.ps1 -LiveGoogle -NativeChecks` 的 UI smoke 23/23 项通过，报告 `success: true`。
- 证据覆盖：真实受控前台 WPF UI Automation 选区、Win32 快捷键注册/清理、显示器工作区定位、请求取消与迟到结果保护、自动收起边界、置顶与设置边界、HTTP 错误、超长输入拦截、空输入、来源显示、真实设置窗口 save/cancel、DPAPI 往返、固定句 Live Google 翻译，以及主窗/设置 PNG 截图。
- UI 语言切换在真实流程中不发 HTTP，并保留原文、译文、服务和目标语言；动态错误、来源和隐藏临时托盘菜单也通过真实本地化路径验证。
- catalog parity 130/130，无缺失 key。
- 主代理已查看中文主窗、英文 490px 主窗和英文设置截图，布局正常。未将任务栏人工验证宣称为通过。

### 包内容与图标

- ZIP 检查为 465 项；未包含 `data`、settings、诊断日志、smoke 或 benchmark 文件。`System.Diagnostics.*.dll` 是合法 .NET runtime 程序集，不是诊断日志。
- 0.2 新目录与旧 0.1 目录并存，旧交付物未删除。
- 图标源图与 PNG/ICO 资产已核对；ICO 包含 16、20、24、32、40、48、64、128、256px 九个 PNG 帧，每帧签名、尺寸、System.Drawing 加载及透明 alpha 有效。
- 从最终 EXE 使用 `ExtractAssociatedIcon` 加载 32px 图标成功；这不替代任务栏图标的人工验收。

### 性能实测（功能可试用，轻量指标不通过）

- 三轮独立进程 warm benchmark 均 exit 0，原始 summary 字段为 `success: true`；汇总证据：`artifacts/verification/0.2.0-final-performance/benchmark-launch-summary.json`。
- 每轮均完成 1,000/1,000 次离屏 popup、200/200 次本地 mock 翻译；warm marker（含 50ms 轮询）为 957.2/887.1/903.5ms，ContentRendered 为 698.692/714.158/693.341ms。
- popup 布局 p95 为 10.951/10.985/10.657ms；本地 mock（UI+日志）p95 为 95.547/95.068/95.433ms。翻译数据是本地 pipeline/UI 测量，不是网络延迟。
- hidden 阶段 Working Set 为 204.88/204.70/204.71MiB，Private Bytes 为 146.67/144.91/147.41MiB；recovery Working Set 为 235.27/235.23/233.54MiB；hidden CPU 均值为 0.046/0.198/0.194%（全逻辑处理器归一化）。
- 以上是三轮观测值，不代表所有机器的性能保证；`<60 MB` 目标明确未达标，因此本版本可进行功能试用，但不应宣称轻量内存指标通过。完整方法与数据说明见 `docs/PERFORMANCE.md`。

## 仍未完成，不视为通过

- 真实外部应用取词全矩阵、mixed-DPI、多 Windows 版本兼容性、8 小时 soak 尚未完成。
- DeepL/OpenAI 兼容服务尚未使用真实付费凭据验证。
- 真实外部应用前台 hotkey-to-render 延迟尚未完成端到端人工验证。
- 任务栏图标的人工验收尚未单独完成；本记录不据此扩大 EXE/WPF/托盘已验证范围。

在上述项目完成前，本版本可进行功能试用，但不应宣称全部环境、性能和长期稳定性验收完成。
