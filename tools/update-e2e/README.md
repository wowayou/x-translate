# 本地更新全链路验收

`Run-UpdateE2E.ps1` 只在本次自有临时目录解压／更新应用，不覆盖已安装副本，不触及用户运行实例。检查两种包的摘要拒绝、部分替换后的锁文件回滚、正常升级、`.old`／staging 清理、降级拒绝，以及 Lite 缺运行时拒绝。前台重启另由 `Run-RestartE2E.ps1` 验证，不能从本脚本结果推断通过。

## 推荐：已构建包模式

四个 ZIP 和各自 `.sha256` 必须存在；文件名使用 `XTranslate-<版本>-win-x64-{portable|lite}.zip`。两种包的起止版本必须一致。PowerShell 示例（替换为本次自有路径，Output 使用新的目录）：

```powershell
.\tools\update-e2e\Run-UpdateE2E.ps1 `
  -PortableFrom D:\test\XTranslate-0.4.0-win-x64-portable.zip `
  -PortableTo D:\test\XTranslate-0.4.1-win-x64-portable.zip `
  -LiteFrom D:\test\XTranslate-0.4.0-win-x64-lite.zip `
  -LiteTo D:\test\XTranslate-0.4.1-win-x64-lite.zip `
  -DataFixtureDirectory D:\test\fixed-data `
  -Output D:\test\new-update-report -ProcessTimeoutSeconds 45 -KeepWorkOnFailure
```

包模式不构建／下载；不提供四包时，脚本会从当前源码 publish 当前版和临时 patch+1 版本，不改产品版本文件，但 restore 可能访问 NuGet，且构建不受单次更新进程超时限制。需要离线／严格时限时，预先有界构建并采用包模式。本轮 Q3 使用本地缓存和独立构建进程上限，无新增收费服务。

每次更新／清理的自有进程受 `ProcessTimeoutSeconds` 限制（15–180s，默认 60s）；超时仅终止自身 PID，失败不重试。ZIP 解压、压缩及同步文件操作不受此进程时限强制中断，自动调用者还需整体执行上限。可选 `StackTool` 超时抓栈另最多等待 15s。检查脚本退出成功，**并**读取本次新报告 `update-e2e.json.success`、`checks`、`attempts`；拒绝案例的子进程 exit 1 是预期，不能把每个 exit 1 当总失败，也不能只凭报告存在宣告成功。

## 固定数据与保护范围

- 可选 `DataFixtureDirectory` 必须是自有固定样本，不使用已安装应用的 `data/`、真实学习本、密钥或用户日志。报告保留相对文件名及 SHA-256，不保存文件正文；但失败时 `-KeepWorkOnFailure` 会保留整个临时目录。
- 根目录和遇到的子项不得为 reparse/link；逐目录惰性枚举，在第 129 项或累计超过 16 MiB 时停止。不是针对恶意并发修改／祖先链接的文件系统安全沙箱；样本应由调用者独占且保持不变。
- 按相对路径复制固定样本到两份临时安装的 `data/`，拒绝覆盖脚本自带的 `settings.json` 标记。每次更新／清理前后，对全部嵌套和隐藏文件计算摘要，新增、删除、改名或内容变化均令验收失败。只证明返回后的内容相同，不证明期间未短暂改写。
- 报告 `dataFixtureFileCount` 是调用者样本数，`attempts[].dataFiles` 还包含脚本自有 settings；`dataUnchanged` 必须为 true。单进程操作超时／抛错仍判失败，不能用缺少摘要比较当作成功。
- JSON 证据显式 UTF-8，保留中文临时路径。失败证据不覆盖：调用者每次选择新 Output。脚本本身不强制 Output 必须为空。

独立反例测试（无真实产品子进程、无网络）：

```powershell
powershell.exe -NoProfile -File tests/update-e2e-data.Tests.ps1
```

该测试加载 `UpdateData.ps1` 的共享固定数据边界及实际脚本的进程编排，覆盖嵌套学习本修改／删除、隐藏文件、夹具限额和 junction 拒绝，并模拟“子进程 exit 0 但损坏学习本”，确认验收入口拒绝；不能替代真实包更新。新增设置重启与通知栏重启的区别：设置保存可改 `settings.json`，其余数据仍须逐文件相同；通知栏重启则全部 data 字节不变，不能误把整个 JSON 数组排除检查。当前 **14/14** 与实际更新结果统一见 [Q3 记录](../../docs/ACCEPTANCE-OCR-Q3.md)。

## 真实前台升级与重启

```powershell
.\tools\update-e2e\Run-RestartE2E.ps1 `
  -FromPackage D:\test\XTranslate-0.3.0-win-x64-portable.zip `
  -ToPackage D:\test\XTranslate-0.4.0-win-x64-portable.zip `
  -Output D:\test\new-restart-report -RestartVia Banner `
  -DataFixtureDirectory D:\test\fixed-data -StepTimeoutSeconds 15
```

- 要求交互桌面，已有普通实例的 mutex 存在即停止，不唤出或关闭用户实例。只解压到自有含空格／中文的临时目录，配置禁用自动更新／自动翻译／回退／模拟复制，使用固定假凭据；不调用真实服务。
- WPF 控件使用 UI Automation（用户界面自动化）；系统文件选择器的标准文件名 Edit／Open HWND（原生窗口句柄）使用 PID 归属检查后的有界消息。只写明确包路径、不读当前值或文件列表，不注入全局键鼠，不把 PowerShell 不支持原生控件模式视为产品失败或静默跳过。
- `Banner` 关闭设置后从主窗通知栏重启；`Settings` 从设置页保存后重启。分别确认旧 PID 正常退出、新 PID／实际运行版本、前后台焦点、更新残留清理、可解密假凭据及数据摘要。不得从其中一条通过推断另一条也通过。
- 单次等待限 `StepTimeoutSeconds`，文件名消息最多等 2s；同步 UI Automation／解压不受该等待时限强制中断，自动调用还需外层总时限。失败清理只针对本次临时路径；若必须终止自有进程则报告失败，不当作正常退出。每轮 Output 必须新建，JSON UTF-8，只记录固定样本相对路径及摘要。

**2026-10-22 结果与阻塞**：发布记录摘要匹配的 0.3.0 两版实包 → Q3 的 0.4.0 隔离包，`Banner` 均通过，5 个额外固定样本＋settings 共 6 文件摘要相同；`Settings` Lite 通过（只保存 settings，语义配置／假凭据保持），Portable 旧进程未正常退出。另 0.3.0 Portable 无界面更新在部分替换后超时；未把它当作成功升级或回滚。本阶段不修写／重新发布旧包，发布签收仍阻塞，证据和后续处理范围见 Q3 记录。
