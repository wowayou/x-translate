# 本地 OCR 可行性探针（不是应用功能）

`Probe-WindowsOcr.ps1` 只生成一张固定英文 PNG，用明确的 `en-US` Windows OCR 引擎识别两次，并检查不支持的语言不会回退。它不截屏、不读剪贴板／用户图片、不调用云接口、不下载语言包、不修改系统或应用配置；已有证据目录会拒绝使用。

使用 **Windows PowerShell 5.1**（其 .NET Framework 环境提供 WinRT 投影）：

```powershell
powershell.exe -NoProfile -File tools/ocr/Probe-WindowsOcr.ps1 -OutputDirectory D:\temp\xtranslate-ocr-new-run
```

输出目录必须不存在。检查退出码为 0 **且**本次新写出的 `ocr-probe.json` 中 `success` 为 `true`；不能只凭父进程退出或报告文件存在判断通过。报告保存生成图片的 SHA-256、明确语言、两次识别文本／耗时、检查和失败原因。

约束：最多 2 次本地识别；生成图片最多 1 MiB；异步操作每次最多等 5s，累计时钟在 30s 边界检查，超时不重试。同步系统调用和清理不由该异步等待上限强制终止；自动执行者还必须为**本次自有子进程**设置外层硬超时。本轮验收为 45s，超时结束该探针，不操作其他进程。

本探针通过不证明 .NET 10/WPF 接入、真实屏幕识别、中文／日文准确率、复杂图像、取消及时性或跨 DPI 框选可用。不得把启动 PowerShell 子进程作为产品中隐蔽的 OCR 兜底；应用适配器、框选预览和相关回归仍需独立实施。证据与未知项见 [交互参考](../../docs/INTERACTION-REFERENCES.md)。
