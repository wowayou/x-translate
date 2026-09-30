# XTranslate 0.2.1 发布验收

本记录是 0.2.1 的发布入口；历史 0.2 验收、429 行为与完整内存数据继续保留在既有文档中，不在此重复。

## 发布内容

- 产品默认使用 WPF 软件渲染；`--hardware-rendering` 仅保留给受控诊断。
- Google 非官方网页接口、DeepL 官方 API Free / Pro 和 OpenAI 兼容 AI 翻译接口可用。
- AI 接口支持自定义 HTTPS Chat Completions 地址、API key 和模型；结果显示实际 endpoint 主机与模型。
- Microsoft Translator 与 Papago 已获得稳定 provider ID，并在主界面保留禁用入口。二者尚未接入鉴权、请求与响应解析，不会被保存为当前服务，也不会发出 HTTP。
- 固定窗口内存验收、紧凑 JSONL 长测采样和正式 8 小时 runner 已纳入代码库。

## 验证结果

- Core：17/17，通过 provider catalog、AI 元数据以及 Microsoft/Papago 禁止发出 HTTP 的边界检查。
- Diagnostics：10/10。
- 内存验收合成夹具：10/10。
- Release 构建：0 warnings / 0 errors。
- 最终发布目录 UI + Native smoke：24/24。报告：`artifacts/verification/release-0.2.1-final-20260930/run-20260930-132001-8ad32095/ui-smoke.json`。
- 正式内存长测：8 小时 9 分 11 秒、48/48 阶段、28,848 样本，所有阶段满足 PWS 与 Private Bytes 预算。完整记录见 `docs/ACCEPTANCE-DIAGNOSTICS-429.md`。

## 发布产物

- 目录：`artifacts/XTranslate-0.2.1-win-x64-standalone/`
- ZIP：`artifacts/XTranslate-0.2.1-win-x64-standalone.zip`
- ZIP 大小：72,549,727 bytes
- ZIP SHA-256：`CEF6ADB73E8069F01FB87BFE3745E1FF99928CDC10FF0E815EC18E17B2F37A68`
- 目录与 ZIP 各含 466 个文件，条目逐项一致；不包含 `data`、`smoke-data`、`benchmark-data`、`verification`、日志、临时文件或 PDB。
- `XTranslate.exe` FileVersion 为 0.2.1.0，ProductVersion 为 0.2.1；包内含 `使用说明.md`。

## 已知边界

- Google 网页接口可能限流或失效；真实付费服务需要用户自己的凭据，自动验收不会调用它们。
- Microsoft Translator 与 Papago 只是已验证的不可用预留项，不得表述为已集成。
- OCR、并行比较、质量检查、翻译历史、自动更新和开机启动未实现。
- 完整前台快捷键链路、更多真实第三方应用、Windows 版本、GPU、DPI 与远程桌面组合仍需扩大兼容性测试。
- 正式长测证明本机有界负载满足预算，不证明不存在任何内存泄漏；VMMap 原生内存分类仍未采集。
