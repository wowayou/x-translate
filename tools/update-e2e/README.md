# 本地更新全链路验收

`Run-UpdateE2E.ps1` 只在本次自有临时目录解压／更新应用，不覆盖已安装副本，不触及用户运行实例。检查两种包的摘要拒绝、部分替换后的锁文件回滚、正常升级、`.old`／staging 清理、降级拒绝，以及 Lite 缺运行时拒绝。前台重启另由 `Run-RestartE2E.ps1` 验证，不能从本脚本结果推断通过。工作区当前采用**准备 → 旧进程正常退出 → 暂存进程替换**；旧包可仍采用此前时序，两者结果不得混写。

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

每次更新／清理的自有进程链受 `ProcessTimeoutSeconds` 限制（15–180s，默认 60s）；父进程＋暂存 worker 共用这次上限。超时只终止已验证 PID／路径归属的自有进程，失败不重试。ZIP 解压、压缩及同步文件操作不受此进程时限强制中断，自动调用者还需整体执行上限。可选 `StackTool` 超时抓栈另最多等待 15s。检查脚本退出成功，**并**读取本次新报告 `update-e2e.json.success`、`checks`、`attempts`；拒绝案例 exit 1 是预期，不能只凭报告存在宣告成功。

新包的 headless `--apply-update` 父进程 exit 0 **只表示验证／交接完成**，不是安装成功。报告先 `phase=waiting-parent`，最后由该报告中自有 `updaterProcessId` 写 `phase=complete`；验收器核对 worker 路径、最终报告／退出码后才检查程序和 data。读取 JSON 使用 share-delete 以免锁住原子替换。缺报告、中间状态、父进程单独 success 均拒绝。

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

该测试加载 `UpdateData.ps1` 的共享固定数据边界及实际脚本的进程编排，覆盖嵌套学习本修改／删除、隐藏文件、夹具限额和 junction 拒绝，并模拟“子进程 exit 0 但损坏学习本”，确认验收入口拒绝；不能替代真实包更新。新增设置重启与通知栏重启的区别：设置保存可改 `settings.json`，其余数据仍须逐文件相同；通知栏重启则全部 data 字节不变，不能误把整个 JSON 数组排除检查。当前 **16/16** 与实际更新结果统一见 [Q3 记录](../../docs/ACCEPTANCE-OCR-Q3.md)。共享报告读取器不锁住原子替换，新增反例拒绝缺失／中间结果及没有 worker 的成功声明。

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
- `Banner` 关闭设置后从主窗通知栏重启；`Settings` 从设置页保存后重启。`Exit` 关闭设置后正常退出，确认安装完成但**未自动重启**，再由验收器单独启动新应用。分别核对旧 PID 正常退出、新 PID／实际运行版本、前后台焦点、更新残留清理、可解密假凭据及数据摘要。新时序还断言准备阶段全部程序字节不变、无 `.old`；不能从一条通过推断另一条。
- `-Fault PayloadChanged|WorkerChanged|WorkerMissing` 只改本次临时目录的固定文件：检查暂存 payload 或 worker 被改／缺失时，旧程序不退出、不替换、不发后续请求；交接失败撤销计划，之后普通 Exit 正常完成，不偷偷重试安装。
- 单次等待限 `StepTimeoutSeconds`，文件名消息最多等 2s；同步 UI Automation／解压不受该等待时限强制中断，自动调用还需外层总时限。失败清理只针对本次临时路径；若必须终止自有进程则报告失败，不当作正常退出。每轮 Output 必须新建，JSON UTF-8，只记录固定样本相对路径及摘要。

**2026-10-22 阶段结果**：修复包两版 `Settings`／`Banner`／`Exit` 六条生产 UI 链路、六条准备篡改／缺 worker 反例、11 次无界面更新／回滚操作均通过，原有功能两版各 119/119。已发布 0.3.0 两版实包 → 修复包的 `Banner` 链路另通过；旧版 Portable 无界面及 Settings 故障仍不能追溯修复，不从新的通过结果推断旧包其它路径安全。

## 失败恢复与旧版迁移

- **准备或交接失败，尚未替换**：仍运行旧版本，显示更新已撤销；正常退出即可，不会再次安装。若提示的是上次更新恢复错误，按下一项处理；仅未交接的暂存文件可由下次普通启动清理，`data/` 不移动、不删除。
- **旧进程非正常退出／worker 中断／回滚失败**：程序目录的 `.update-result.json` 明确未成功时，普通启动保留 `.old`／`.update-*`，暂停再次准备，显示恢复说明；不把重启或缺少报告视为成功，不自动反复应用。需要手工恢复时先关闭全部本目录进程并备份整个目录，向**另一新目录**解压可信完整同版旧包或新包，仅把原 `data/` 原样复制过去，核对新目录可用后再自行处理旧目录。不要仅删除 `.update-result.json` 或拿 `.old` 随意覆盖部分文件；回滚不完整可能是混合版本。应用未包含一键恢复界面。
- **0.3.0 旧版 Portable**：运行中原地替换可能影响单文件运行时的延迟加载；无界面调用及 Settings 保存重启已出现真实失败。推荐退出后按上面的**新目录解压＋原样迁移 data**方式更新，不以本轮单机 `Banner` 成功作为通用安全保证；Lite 同样可用这个保守方法。新 0.4.0 时序只保护从修复版开始的后续升级，不能改变旧 EXE 已执行的逻辑。

## 非正常父进程与互斥冲突

`tests/XTranslate.UpdateHost.Tests` 是独立 .NET 固定父进程，不是正常产品程序、不会注册热键／读配置。先在自有输出离线构建，再提供给：

```powershell
.\tools\update-e2e\Run-WorkerFailures.ps1 `
  -CandidatePackage D:\test\XTranslate-0.4.1-win-x64-lite.zip `
  -ParentExecutable D:\test\owned-parent\XTranslate.exe `
  -Output D:\test\new-worker-failure-report -ProcessTimeoutSeconds 25
```

三种有限实验在交接后强制终止这个固定父 PID、明确返回非零、或由验收器持有正常 mutex，确认实际 production worker 在 `wait-parent`／`mutex` 边界拒绝且安装／data 摘要不变。用固定父进程准确控制边界，避免给产品加入测试延时或靠竞态猜测。报告标注该父进程非产品 UI，不能替代真实前台重启；所有进程／文件只属于本轮自有目录，结果保留不自动清理。
