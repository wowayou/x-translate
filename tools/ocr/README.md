# 本地 OCR 探针与固定样本验收

工具只用于手动、有界的本地验收，不是产品 OCR 子进程兜底。当前 .NET 10 引擎／截图对照的能力与限制统一见 [Q3 记录](../../docs/ACCEPTANCE-OCR-Q3.md)。

## 早期可行性探针

`Probe-WindowsOcr.ps1` 只生成一张固定英文 PNG，用明确的 `en-US` Windows OCR 引擎识别两次，并检查不支持的语言不会回退。它不截屏、不读剪贴板／用户图片、不调用云接口、不下载语言包、不修改系统或应用配置；已有证据目录会拒绝使用。

使用 **Windows PowerShell 5.1**（其 .NET Framework 环境提供 WinRT 投影）：

```powershell
powershell.exe -NoProfile -File tools/ocr/Probe-WindowsOcr.ps1 -OutputDirectory D:\temp\xtranslate-ocr-new-run
```

输出目录必须不存在。检查退出码为 0 **且**本次新写出的 `ocr-probe.json` 中 `success` 为 `true`；不能只凭父进程退出或报告文件存在判断通过。报告保存生成图片的 SHA-256、明确语言、两次识别文本／耗时、检查和失败原因。

约束：最多 2 次本地识别；生成图片最多 1 MiB；异步操作每次最多等 5s，累计时钟在 30s 边界检查，超时不重试。同步系统调用和清理不由该异步等待上限强制终止；自动执行者还必须为**本次自有子进程**设置外层硬超时。本轮验收为 45s，超时结束该探针，不操作其他进程。

本探针通过本身不证明 .NET 10/WPF 接入、真实屏幕识别、中文／日文准确率、复杂图像、取消及时性或跨 DPI 框选可用。它是早期可行性实验，方法与未知项见 [交互参考](../../docs/INTERACTION-REFERENCES.md)。

## 固定生成图片与滚动界面验收

`Run-OcrCorpus.ps1` 显式运行指定的可信、本地构建或发布包，不构建／安装／更新软件。`corpus-plan.json` 固定十张生成图片的语言、像素尺寸、预期文字与分级，并作为资源嵌入 App；runner 核对计划 SHA-256 与逐项字段，不能拿其它源码／计划的结果验收当前版本。

```powershell
powershell.exe -NoProfile -File tools/ocr/Run-OcrCorpus.ps1 `
  -ExecutablePath D:\temp\xtranslate-lite\XTranslate.exe -Flavor Lite `
  -OutputDirectory D:\temp\ocr-corpus-new
# Portable 为不依赖外部运行时的单文件包；Dev 为依赖运行时的开发发布目录。
```

- 输出目录必须不存在，输入／输出为本地普通路径；拒绝 UNC、网络驱动和路径中的链接。仅启动一个自有进程，默认硬超时 180s（可选 30–240s），超时仅结束本次 PID、清理等候最多 5s；不重试、不定时运行。构建缓存或语言包缺失不能以联网下载补齐。
- 四项必验：多行英文、透明背景、深色背景、192-DPI 图片元数据。六项观察：双栏、表格、倾斜、低对比度、固定噪点、中英混排。英文必须明确选已安装 en-US；中英混排仅选已安装 zh-Hans-CN，缺失记 `language-unavailable`／零识别，不改选语言或冒充准确率通过。
- 每张实际执行的图片经过生产 PNG 解码、预览与输入像素逐字节比对、一次本地 Windows OCR；成功识别后明确手动翻译，向固定的进程内 handler 恰好发送一次 **raw 文字**。最多十次真实本地识别／十次模拟 HTTP；不读屏、用户文件／配置或剪贴板，不发送真实 Google／DeepL／AI 请求。截图／文字证据只来自本工具生成的固定图。
- 评分仅删除空白后作 ordinal（逐字符、区分大小写）比较，不修复拼写、标点或顺序。四项必验不匹配使验收失败；观察项的误字、漏字或 EmptyText 保留独立非通过等级，不能靠总体 `success: true` 宣称复杂图片准确。该有限生成集不是泛化准确率或版面保持保证。
- 另用两次固定模拟识别／两次模拟翻译检查八种默认／最小窗口、短／长文、中／英文深浅布局；原文／译文独立滚动，页面只在空间不足时滚动，滚轮边界传递、末尾可达、滚动条不越界、底部操作固定。保存十二张 100/150/200% 渲染图（不是实际跨 DPI 拖选）；这两次 mock 计数与十张真实 OCR 样本分开。
- 静态 PNG 回归还验证透明展平、灰度／索引色以及缺 IEND、坏 CRC、孤立动画控制、未知关键块及末尾多余内容的拒绝。CRC（Cyclic Redundancy Check，循环冗余校验）用于检测块损坏，不是安全签名；预检不验证全部压缩像素／辅助元数据，仍需 Windows 解码器。

runner 同时检查退出码、`corpus-only.json`、`ocr-corpus.json` 和 `ocr-scroll.json` 的本次 PID／完成标志，重算文字等级、工作次数、输入及对照 PNG 摘要，开始／结束绑定顶层 EXE／DLL／runtimeconfig／deps 身份（不遍历 data）。汇总 `corpus-summary.json` 保存三份报告摘要。成功执行不等于八小时稳定性、真实屏幕／鼠标或所有语言已验收。

`tests/ocr-corpus.Tests.ps1` 加载实际 runner 函数，用合成报告／文件和假进程检查拒绝边界，**不**启动 WPF 或系统 OCR。最新实测与失败证据以 [Q3 阶段记录](../../docs/ACCEPTANCE-OCR-Q3.md) 为准。
