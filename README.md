# XTranslate 0.2.1

Windows 划词翻译工具。使用 C# / WPF：选中文字、快捷键取词、查看翻译，然后继续工作。

## 开始使用

1. 解压整个发布包到可写目录，运行 `XTranslate.exe`。不要只移动 EXE。
2. 默认选择 **Google · 非官方网页接口**，无需填写 API 密钥；需要能访问 Google 的网络。
3. 在输入框粘贴文字，点击「翻译」或按 **Ctrl+Enter**。原文将发送给当前选择的服务。
4. 在其他应用选中文字，按 **Ctrl+Q**。首次默认只取词，按 Enter 翻译；可在设置中开启「取词后自动翻译」。
5. **Esc** 或关闭窗口会收起到托盘。双击托盘打开，右键托盘可彻底退出。

界面语言可在标题栏右侧或设置里切换：跟随系统 / 简体中文 / English。切换只改变界面文字，不影响原文、译文、目标语言或翻译服务，也不会触发新的请求。原文区提供「粘贴」「清空」按钮；「置顶」可让窗口保持在前并阻止取词弹窗因失焦自动收起。键盘：Ctrl+Enter 翻译、Ctrl+L 聚焦原文、Ctrl+Shift+C 复制译文、Esc 收起；粘贴/清空也可通过原文区按钮完成。

Ctrl+Q 被占用时，本次运行尝试 Ctrl+Alt+Q，并在界面显示实际快捷键。可在设置中固定使用 Ctrl+Alt+Q。再次运行 EXE 会唤出已有窗口。

最终发布目录使用版本化名称 `artifacts/XTranslate-0.2.1-win-x64-standalone/`，对应压缩包为 `artifacts/XTranslate-0.2.1-win-x64-standalone.zip`；该独立包内置 .NET 8 Desktop Runtime x64，解压即可运行。依赖运行时的便携包使用同一版本前缀但需要目标机器安装 .NET 8 Desktop Runtime x64。正式面向其他机器发布前需补充 Windows 10/11 兼容性测试。

## 已实现

- 全局快捷键、UI Automation 选区读取、保守的模拟复制取词。
- 简体中文 / English 界面切换（可跟随系统），独立于翻译目标语言，切换立即生效并持久化。
- 取词弹窗失焦自动收起（可在设置关闭），窗口置顶时保持可见；下拉选择与进入设置不会误收起。
- 可编辑原文、自动识别源语言、六种目标语言、复制译文。
- Google 非官方网页接口；DeepL 官方 API Free / Pro；可配置 HTTPS 地址、API key 和模型的 OpenAI 兼容 AI 翻译接口。
- Microsoft Translator 与 Papago 已保留稳定 provider ID 和禁用界面入口，尚未接入真实 API，不会被误选或发出请求。
- 明确来源、模型（AI）、耗时；20 秒请求超时；取消与重试。
- 编辑原文、切换服务、关闭弹窗会取消旧请求，迟到结果不会覆盖新结果。
- 请求失败不跨服务自动切换。用户可以手动更换服务。
- 密钥由 Windows DPAPI 按当前用户加密保存。诊断日志不包含原文、译文或密钥。
- 按当前显示器工作区定位弹窗，声明 Per-Monitor V2 DPI awareness。

每次最多 3000 字符。首版没有 OCR、并行比较、质量检查、历史记录、自动更新和开机启动；这些不影响基本取词与翻译。

## 特色功能状态

| 功能 | 状态 | 当前行为 |
| --- | --- | --- |
| 划词与全局快捷键 | 已实现 | UI Automation 优先，必要时可启用保守的模拟复制；快捷键冲突时回退 Ctrl+Alt+Q。 |
| 自动翻译与请求治理 | 已实现 | 可选择取词后自动翻译；编辑、切换服务或隐藏窗口会取消旧请求，迟到结果不会覆盖新结果。 |
| AI 翻译接口 | 已实现 | 支持 OpenAI 兼容 Chat Completions，可配置 HTTPS endpoint、API key 和模型，并显示实际主机与模型。 |
| DeepL | 已实现 | 支持官方 API Free / Pro，限制为官方 `/v2/translate` 地址。 |
| Microsoft Translator / Papago | 已预留 | provider 和界面位置已存在但保持禁用，尚未实现鉴权、请求格式与响应解析。 |
| 中英文界面与来源标识 | 已实现 | 界面语言独立切换；保留原文、译文、目标语言和服务状态，不额外发请求。 |
| 长时内存预算 | 已验收 | 本机 8 小时 9 分钟运行的 48 个阶段全部满足 PWS 与 Private Bytes 预算。 |
| OCR、并行比较、质量检查、历史、自动更新、开机启动 | 未实现 | 不在 0.2.1 发布范围内。 |

## 服务配置

Google 的接口非官方，可能限流、失效或被所在网络阻断；界面会明确显示错误，不保证长期可用。

DeepL：填写自己的 API key，根据账户类型选择 Free 或 Pro。首版仅允许官方接口。

AI 翻译接口：填写完整的 OpenAI 兼容 HTTPS Chat Completions 地址、API key、服务支持的模型名。默认地址为 `https://api.openai.com/v1/chat/completions`；默认模型名只是可编辑初始值，需要与你的账户权限匹配。第三方兼容服务会显示其主机名，不能误认为由 OpenAI 提供。真实付费服务需要你自己的凭据，自动测试不调用它们。

## 取词边界

优先使用 UI Automation，是否可用取决于目标应用。兼容取词会模拟 Ctrl+C，暂时改变剪贴板；只在可备份的文本、HTML、RTF 等格式上尝试，读取后恢复。如果有其他应用更新剪贴板，会放弃恢复以避免覆盖新内容。

图片、文件、特殊格式或大剪贴板会阻止兼容取词。已知终端和带内嵌终端的开发工具不会使用模拟 Ctrl+C，以免中断命令；仍可尝试 UI Automation 或手动粘贴。普通权限不能保证读取管理员运行的软件。无选区、慢响应、远程桌面、剪贴板管理器和自定义控件仍需实测；兼容取词无法保证不被系统剪贴板历史或第三方剪贴板管理器观察到。

## 数据与隐私

配置与日志位于 EXE 旁的 `data/`。`settings.json` 中 API key 字段为空，凭据以 DPAPI 密文保存；复制到另一台电脑或换 Windows 用户后需重新填写。不要将 `data/` 随发布包分享。

日志只包含请求 ID、服务类型、耗时和状态，最多保留当前与上一份约 1 MB 日志。没有默认翻译历史。选区本地读取；点击翻译，或主动开启自动翻译后触发快捷键，才会发送原文到所选服务。

## 构建与验证

要求 Windows 与 .NET 8 SDK。项目没有额外 NuGet 库依赖。仓库 `.tools/dotnet/` 中存在 SDK 时脚本优先使用，否则使用 PATH 中的 `dotnet`。

```powershell
./build.ps1                 # 运行核心测试并生成依赖运行环境的便携包
./build.ps1 -SelfContained  # 生成内置运行环境的便携包，首次需要下载运行时包
./verify.ps1               # 核心测试 + 真实 WPF 生命周期验收（模拟 HTTP）
./verify.ps1 -LiveGoogle -NativeChecks
./benchmark.ps1            # 本地 UI 性能基准（1000 次弹窗 + 200 次 mock 翻译 + idle 三阶段）
./memory-soak.ps1           # 默认 8 小时本地稳定性测试（无网络/剪贴板/用户数据）
```

`-LiveGoogle` 只向 Google 发送固定测试句；`-NativeChecks` 会创建固定文本测试窗口，验证真实 UI Automation 选区读取、快捷键注册和当前显示器内的定位，不读取用户剪贴板。Windows 不允许后台测试窗口获取前台焦点时，测试会改为读取明确指定的本程序测试控件，并在报告中注明：完整前台快捷键链路仍需手动验证。

`build.ps1` 先发布到一次性隔离 staging，再从该目录生成压缩包，避免把旧日志、截图或用户数据带入包内；若目标版本目录或 ZIP 已存在，脚本会停止并要求手动移走旧产物，不会删除或覆盖它们。`verify.ps1` 每次使用唯一 run 目录并确认本次进程实际写出了报告。

验收 JSON 和界面 PNG 写入 `artifacts/verification/`；当前发布状态与结果以 `docs/ACCEPTANCE-0.2.1.md` 为准。截图阶段的进程内存包含渲染与测试开销，不是空闲内存基准。`benchmark.ps1` 的性能模式包含 1000 次离屏弹窗 Show/Hide、200 次本地固定 mock 翻译，以及启动/可见 idle/隐藏 idle/恢复 idle 资源阶段；它排除真实网络耗时，网络延迟不得混为 UI 性能。性能方法与结果说明见 `docs/PERFORMANCE.md`，本 README 不对未记录的性能指标作达标结论。

应用图标为原创生成的位图（见 `docs/ICON.md`）。`tools/icon-builder/Build-Icon.ps1`（Windows/System.Drawing）只做技术性缩放与打包，生成 `src/XTranslate.App/Assets/xtranslate.png` 与 `src/XTranslate.App/Assets/xtranslate.ico`；验证时请把输出指向独立目录，避免覆盖 shipping assets。

应用默认使用 WPF 软件渲染。当前界面以文本和静态控件为主；本机受控验收中，该策略显著降低隐藏状态的私有驻留内存，并未降低离屏 Show/Hide 或本地翻译基准。该结果不等同于所有显卡、DPI 和远程桌面环境的通用性能保证。

## 代码结构

`src/XTranslate.Core`：服务适配、错误处理、结果来源；不依赖 WPF。

`src/XTranslate.App`：窗口、托盘、快捷键、取词、设置与诊断。

`tests/XTranslate.Core.Tests`：无第三方测试框架的可执行测试集。

本版本独立实现，没有复制报告中其他翻译器的源码或品牌素材。
