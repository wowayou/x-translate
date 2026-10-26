# 隔离性能诊断工具

`DiagnosticPreWait.cs` 只供隔离源码探针与 `tests/XTranslate.PerformanceTools.Tests` 链接使用，**不编入生产应用**，也不修复 `MainWindow.Performance.cs` 的 idle 末次采样覆盖问题。

## 预等待契约

- 每次 `WaitAsync()` 独立保证至少 5000ms，以 `Stopwatch` 的原始整数计数达到 `5 × Frequency` 为准。`Stopwatch` 是单调高精度计时器，在这里用来判断实际时间是否达到下限，而非信任计时器请求的名义时长。
- `Task.Delay` 若提前返回，仅将剩余时间向上取整到毫秒后重新异步等待；不忙等、不把结果补成 5000、不引入验收容差。返回 `Measurement` 中的实际 `ElapsedTicks`、`ElapsedMs` 和 `DelayCalls`，超出的时长也原样报告。
- 每阶段最多 16 次 delay 调用；计时未推进到下限、时钟倒退／溢出或 delay 失败均明确失败。取消／异常直接传播，不重试实验进程。
- 调用次数上限**不是墙钟期限**：异步操作本身仍可能未完成。外层 runner 必须保留自有子进程期限和一次有界清理；当前诊断进程为 60s，测试控制台为 45s。可见／隐藏阶段分别调用，不共享测量。

## 回归测试

项目：`tests/XTranslate.PerformanceTools.Tests/XTranslate.PerformanceTools.Tests.csproj`，目标 .NET 10，无第三方测试框架。只接受两个参数：**已有目录中尚不存在的报告路径**和显式模式：

- `synthetic-only`：23 项确定性测试，含真实失败值 4999.7219ms、恰好 5000ms、少一个计数、连续早返回、调用次数上限、异步等待、倒退／溢出和取消／异常。
- `with-system-waits`：上述检查再加两次本机 `Stopwatch`／`Task.Delay` 等待，共 25 项。它不启动 WPF／产品进程，不代表产品性能验收。

已完成隔离构建后的手动命令模板（替换尖括号路径；模板本身不内置进程期限）：

```powershell
& '<隔离构建目录>\XTranslate.PerformanceTools.Tests.exe' '<已有新目录>\checks.json' synthetic-only
# 只有显式需要两次本机五秒等待时才使用：
& '<隔离构建目录>\XTranslate.PerformanceTools.Tests.exe' '<另一个已有新目录>\checks.json' with-system-waits
```

构建应使用已有可信 SDK／本地缓存：restore 显式 `--source .tools/cli/.nuget/packages -p:NuGetAudit=false`，build 使用 `--no-restore`；禁用 SDK 遥测、开发证书初始化和全局工具 PATH 初始化。每个 SDK 操作限 120s，控制台限 45s，串行、失败即停、无重试；缓存或 SDK 缺失时失败，不自动下载或联网回退。不要用默认 `dotnet run` 隐式恢复依赖。

本机修复／新矩阵与旧失败的身份、实际测试和限制统一见 [性能记录](../../docs/PERFORMANCE.md#2026-10-26-诊断预等待修复与独立三轮材质对照)。测试通过只支持诊断等待契约，不是材质优化、资源预算、前台体验或发布签收；不得改写失败报告或合并不同探针身份的样本。
