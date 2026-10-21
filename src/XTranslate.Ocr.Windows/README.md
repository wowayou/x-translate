# 本地 Windows OCR 适配器

独立于 WPF 的按需 OCR（Optical Character Recognition，光学字符识别）适配。不初始化网络／数据库，不打开屏幕／剪贴板，不下载语言包。`WindowsOcrEngine` 使用系统已安装语言；一个进程只允许一个后台操作，取消／超时返回后若原生调用未结束仍占用 gate（操作门控），不启动额外工作者。

`XTranslate.Ocr.Windows.csproj` 通过官方 `Microsoft.Windows.CsWinRT 2.3.1` 生成 OCR、位图、语言和异步类型所需的有限投影；元数据输入固定为官方 `Microsoft.Windows.SDK.NET.Ref 10.0.19041.57` 包的 `winmd/`，不依赖本机构建工具自动选 SDK。生成文件仅在 `obj/`，不手改或提交。运行时发布 `WinRT.Runtime.dll`，不发布完整 `Microsoft.Windows.SDK.NET.dll`；MIT 许可随输出的 `ThirdParty/CsWinRT-LICENSE.txt` 保留。

构建需 Windows、.NET 10 SDK 和首次 NuGet 还原。正常项目 TFM 保持 `net10.0-windows`，Windows 10 API 调用有运行时版本检查；这不是 Win7 或普通 Win10 22H2 的 .NET 10 支持声明。详细限制和实际证据见 [Q3 阶段记录](../../docs/ACCEPTANCE-OCR-Q3.md)。

测试入口：

```powershell
dotnet run --project tests/XTranslate.Ocr.Tests -c Release             # 固定模拟 backend
dotnet run --project tests/XTranslate.Ocr.Tests -c Release -- --native # 额外生成英文/中文图片、本地真实识别
```

真实引擎测试明确使用本机已安装语言；未安装中文时报告 skip、不下载或换语言。两次简单生成图片不能证明真实屏幕识别准确率。截图／文件预览及用户确认仍由 App 独立实现；适配器不会自动翻译。
