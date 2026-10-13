# XTranslate 性能基准

性能模式只使用固定的本地测试数据，不读取用户原文、剪贴板、设置密钥或其它应用，也不会发起网络请求：

```powershell
XTranslate.exe --benchmark --benchmark-output .\benchmark-result.json
```

`--benchmark` 使用独立的 `benchmark-data` 目录和独立 mutex。窗口设置为 `ShowActivated=false`、不显示在任务栏并移到屏幕外；不会调用 `Activate`、`SetForegroundWindow` 或 `ShowForUser`。因此弹窗数据只代表离屏 WPF `Show/Hide + DispatcherPriority.Render/UpdateLayout` 的布局完成时间，不是屏幕合成时间，也不是快捷键到前台弹窗的延迟。

一次运行包含：

- 启动到首次 `ContentRendered`；启动计时从 `App` 类型加载时的 Stopwatch 开始，CLR 宿主在此之前的进程启动工作不包含在内。
- 可见 5 秒 idle、隐藏 5 秒 idle，以及 200 次本地固定 handler 翻译后的可见 5 秒恢复 idle。idle 以 200ms 间隔记录 Working Set、Private Bytes、managed heap、线程、句柄、GDI/USER 对象和进程 CPU 间隔；不执行强制 GC 或工作集清空。
- 1000 次离屏 `Show/Hide`（每次等待 Dispatcher Render 优先级和 `UpdateLayout`），以及 200 次通过 UI `TranslateNowAsync` 的固定本地 pipeline。延迟输出 p50/p90/p95/p99/max，并给出各循环前后资源差。

报告中的翻译延迟是本地 pipeline/UI 性能标签，不是网络性能。报告不会声称 OCR、8 小时 soak 或真实前台 hotkey 延迟，也不应把 `<60 MB` 之类的目标当作已达标结论。

## 独立进程 warm launch

先构建要测的发布目录，然后运行：

```powershell
.\benchmark.ps1 -ExecutablePath .\artifacts\XTranslate-0.2.0-win-x64-standalone\XTranslate.exe -Runs 5
```

脚本在每个进程创建前启动计时，并轮询该进程写入的 `ContentRendered` marker，另外保存完整 benchmark JSON。轮询间隔为 50ms，测量含轮询误差；缓存不清理，所以结果标为 warm launch 而非 cold boot。每个进程默认上限为 180 秒、允许配置 45–240 秒；失败或超时会在汇总中标记，不能当作成功样本。

## 0.2.0 发布版实测（3 次 warm launch）

数据目录：`artifacts/verification/0.2.0-final-performance/session-20260920-231150-520-a2dc616c`。三次均完成且退出码为 0。外部进程创建到 `ContentRendered` marker 为 **887.081–957.177 ms**；marker 以 50 ms 轮询，外部值包含轮询间隔以及调度和文件检查开销。进程内首次 `ContentRendered` 为 **693.341–714.158 ms**，仍不包含 CLR 宿主在 `App` 类型加载前的工作。

延迟结果范围如下：

| 场景 | p95 | p99 | max |
| --- | ---: | ---: | ---: |
| 离屏 1000 次 Show/Hide + layout completion | 10.657–10.985 ms | 12.127–12.443 ms | 16.358–21.646 ms |
| 200 次本地 `TranslateNowAsync` pipeline | 95.068–95.547 ms | 95.783–96.977 ms | 95.943–109.299 ms |

三次 idle 资源范围（MiB；CPU 为该 5 秒阶段样本的平均/最大进程 CPU 百分比，已按 `Environment.ProcessorCount` 的全部逻辑处理器归一化）如下。句柄/GDI/USER 是阶段起止差值：

| 阶段 | WS 起→止；差值 | Private 起→止；差值 | managed heap 起→止；差值 | CPU 平均/最大 | handles / GDI / USER 差值 |
| --- | --- | --- | --- | --- | --- |
| visible idle | 196.73–200.00 → 218.87–219.17；+19.00–+22.43 | 145.23–148.12 → 159.64–161.20；+12.99–+15.97 | 4.32–4.45 → 4.68；+0.23–+0.36 | 0.493–0.619% / 3.214–3.563% | +20–+28 / −1–0 / +4–+5 |
| hidden idle | 218.90–219.20 → 204.70–204.88；−14.33–−14.19 | 159.64–161.20 → 144.91–147.41；−14.73–−13.71 | 4.69–4.70 → 4.87–4.88；+0.18–+0.19 | 0.046–0.198% / 0.714–0.722% | 0–+1 / 0 / 0 |
| translation 后 recovery visible | 227.86–228.30 → 233.54–235.27；+5.68–+7.02 | 162.04–163.19 → 167.14–168.59；+5.11–+5.44 | 4.50–4.58 → 5.57–6.22；+0.98–+1.70 | 1.546–1.743% / 3.439–4.309% | +6–+13 / +1 / +3–+4 |

循环资源差值范围：popup 循环 WS **+8.71–+9.12 MiB**、Private **+8.64–+10.50 MiB**、managed heap **+7.22–+7.28 MiB**；翻译循环 WS **+13.95–+14.47 MiB**、Private **+6.48–+6.97 MiB**、managed heap **−7.63–−7.61 MiB**。翻译 p95 包含实际 UI 更新、日志磁盘写入和本地 mock handler；它不是网络延迟，不能据此推断瓶颈。

发布版隐藏 idle 的 WS 约 **204.70–204.88 MiB**，因此 `<60 MB` 目标在本次测量中未达到。三次短时 warm-launch 不能证明 8 小时无泄漏，也不能将资源变化归因到特定 WPF 组件；需要更长时间序列和基线对照。所有 popup 数字仍是离屏本地布局完成，不代表屏幕合成或前台热键延迟。

## 2026-09-29 软件渲染验收

本机的硬件渲染进程加载了集成/独立显卡驱动模块；模块虚拟映像大小不是驻留内存归因。为验证渲染路径影响，同一 self-contained win-x64 构建分别显式运行默认硬件渲染和 `SoftwareOnly`。同期三轮结果如下：

| 模式 | warm launch | Show/Hide p95 | Show/Hide p99 | 本地翻译 p95 |
| --- | ---: | ---: | ---: | ---: |
| 硬件渲染对照 | 1135.9–1257.0 ms | 119.55–128.29 ms | 126.91–136.70 ms | 0.80–1.78 ms |
| 软件渲染候选 | 895.2–1056.7 ms | 5.22–5.44 ms | 5.96–6.97 ms | 0.67–1.77 ms |

最终代码将软件渲染设为默认。使用最终默认构建、不传渲染开关的三轮复验为：warm launch 998.259–1846.339 ms，Show/Hide p95 7.047–8.267 ms、p99 7.891–9.989 ms，本地翻译 p95 0.949–1.155 ms。数据位于 `artifacts/verification/render-default-final-benchmark-20260929`。

硬件渲染对照首次使用旧的 90 秒上限时被基准脚本终止，保留为失败记录，未计入结果。单轮延长观察确认工作负载能完成后，脚本默认上限改为 180 秒，并以新的三轮完整 session 作为对照。上述结论只覆盖本机离屏 WPF 布局完成路径；软件渲染不意味着所有图形密集界面或所有机器都会更快。

## 2026-10-13 便携/轻量打包对比

同一份 A 阶段源码，同机 warm launch 各 3 轮，`benchmark.ps1` 默认负载（1000 次离屏 Show/Hide、200 次 mock 翻译、idle 阶段），取中位数。idle 内存取可见 idle 阶段最后一个样本。

| 发布形式 | 文件数 / 解压大小 | 启动 | 可见 idle WS | 可见 idle Private |
| --- | ---: | ---: | ---: | ---: |
| 基线：A 阶段前 Release 构建（依赖运行时） | — | 656 ms | 125.9 MB | 55.8 MB |
| 旧 self-contained 目录（0.2.1 发布形式） | 465 / 162 MB | 1489 ms¹ | 125.2 MB | 55.1 MB |
| self-contained + 仅 en/zh 卫星资源 | 244 / 146 MB | 694 ms | 125.5 MB | 54.8 MB |
| **portable**：单文件、不压缩 | 7 / 141 MB | 704 ms | 127.6 MB | 56.0 MB |
| 单文件 + 压缩（未采用） | 6 / 68 MB | 953 ms | 247.1 MB | 129.1 MB |
| **lite**：依赖 .NET 8 Desktop Runtime | 7 / 1 MB | 641 ms | 107.0 MB | 41.1 MB |

¹ 该组最先运行，首轮包含磁盘缓存预热，不应解读为目录形式本身慢 2 倍。

结论：单文件压缩在启动时把程序集解压到内存，私有内存约翻倍，因此不采用；不压缩单文件与目录形式资源表现一致，同时把 465 个文件收敛为 7 个。lite 内存较低的原因未归因（推测与共享运行时映像的页面共享有关），三轮样本不足以作为稳定差异。所有数据为本机 warm launch，不是冷启动，也不替代 8 小时长测。原始数据：`artifacts/verification/portable-variants/`、`artifacts/verification/package-portable/performance/`、`artifacts/verification/package-lite/performance/`。
