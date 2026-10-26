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

本机修复／新矩阵与旧失败的身份、实际测试和限制统一见 [性能记录](../../docs/PERFORMANCE.md#2026-10-26-诊断预等待修复与独立三轮材质对照)。上述预等待测试通过只支持诊断等待契约，不是材质优化、资源预算、前台体验或发布签收；不得改写失败报告或合并不同探针身份的样本。

## 完整开合慢迭代只读分析

`analyze_popup_tails.py` 使用 Python 3.11+ 标准库，**不是产品运行时依赖**。只消费已有独立复核封存的完整材质诊断矩阵：16个进程中4个预热排除，12份测量报告／6个同形式同轮配对全部保留。它核对封存摘要、PID／轮次／负载、原始循环与已复算的完整p95，不代替原生状态、进程时窗、整包身份及完整协议验收器。

方法：按每份报告的完整 `TotalMs` 升序排列，同值按 `Iteration` 升序。完整p95位于第950／951次排名，以95%／5%权重插值；五个分段使用**这两个相同完整迭代、相同权重**，不各自取分段p95再相加。最慢5%取排名末50个完整迭代，所有分段均值都使用这同一组50个。每个条件各自选择边界／尾部，因此跨条件的分段差值也包含样本组成变化，不能直接归因为CPU／等待或材质的因果效应。

输入manifest只有以下字段（路径／摘要均须替换为已核对值；摘要核对内容，不认证来源）：

```json
{
  "schemaVersion": 1,
  "verification": {
    "path": "artifacts/verification/<已独立复核目录>/independent-verification.json",
    "sha256": "<经核对的完整64位SHA-256>"
  }
}
```

从仓库根目录手动调用的模板，替换路径；输出父目录须已存在、文件须不存在：

```bash
python3 -B tools/performance/analyze_popup_tails.py \
  --manifest 'artifacts/verification/<已核对会话>/input-manifest.json' \
  --output 'artifacts/verification/<已有新目录>/analysis.json'
python3 -B -m unittest discover -s tests -p performance_popup_tails_tests.py -v
```

根目录默认仓库，可用 `--root` 显式指定可信本地目录；文件路径必须为根目录相对POSIX格式，拒绝绝对文件路径、URL／UNC形式的网络路径、链接／重解析路径及覆盖输出。不遍历目录、不自动安装依赖或启动产品。manifest限64KiB、独立复核JSON限256KiB、每份报告限2MiB、输出限1MiB；输入不完整／损坏／摘要不符时明确失败、不创建分析输出；创建／写入失败仍须按失败保留，不把部分文件当成果。命令模板本身不内置期限，自动调用方必须保留每个Python工具进程60s、串行／失败即停／无重试的控制。

本次在WSL本地盘验收 **38项合成测试**，含独立分段p95相加的反例、精确整数计数参照、并列／乱序／无效数值、缺轮／重复身份／损坏封存、普通文件／管道文件拒绝（防止阻塞读取）／链接边界和旧输出保护；未实测原生Windows Python路径或其权限差异。可信本地目录是调用前提，检查不是原子防篡改／通用安全沙箱，也不保证识别所有网络映射或挂载。实际12份报告、精确分数独立复核和未发布结论见[慢迭代记录](../../docs/PERFORMANCE.md#2026-10-26-慢迭代同样本分段分析只读复算)；本机输入证据由Git忽略，不随工具提交分发。
