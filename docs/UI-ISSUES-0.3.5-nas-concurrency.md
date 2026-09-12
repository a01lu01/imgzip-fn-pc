# ImgZip 0.3.5：NAS 并发事故与加固

记录日期：2026-09-12
状态：代码修复与静态/替代执行层测试完成；NAS 重启后的 OOM 证据与真机稳定性复测待执行。

## 现象

0.3.4 在一次 NAS 压缩任务启动后，SSH 报连接超时或横幅交换超时，NAS 随后无法访问。任务来源约 20 张图片，NAS 为 N5105 4 核、约 7.7 GB 内存。

## 根因

1. 应用执行层用 `Environment.ProcessorCount` 计算任务线程。当前 Windows 主机为 32 逻辑核，因此单个 NAS 请求携带 `threads=32`。
2. NAS worker 的并行池只校验 `THREADS` 下限，没有核数或绝对值上限。
3. worker 随即尝试同时启动 32 个 `caesiumclt` 进程。4 核 NAS 同时承担图片解码和编码，内存需求叠加，造成内存耗尽并失去 SSH/SMB 响应。

这是应用侧线程预算错误与 NAS worker 缺少防御性上限共同造成的。客户端参数即使来自新版本，也不能被 worker 无条件信任。

## 修复

### NAS worker 防御

- 实际并发固定夹取为 `min(请求值, nproc, 4)`。
- 请求值非数字或小于 1 时回退为 2。
- 每次新增子任务前读取 `MemAvailable`：
  - 小于 1.5 GB：最多新增 1 个；
  - 小于 3 GB：最多新增 2 个；
  - 否则：最多新增 4 个。
- 并发被夹取或内存守卫生效时发送 `phase` 提示，并写入任务 `diagnostic.log`。
- 每次派发记录当前运行数、实际 limit 和内存快照。
- 引擎通过 `nice -n 10` 启动，压缩满载时仍为 SSH/SMB 留出响应机会。

### 应用侧独立配置

- 新增 `nasConcurrency`，默认 2，可在设置中调整为 1–4。
- NAS probe 返回 `nasCores` 与 `nasMemoryMb`。
- NAS 任务线程数为 `min(nasConcurrency, nasCores, 4)`，完全不使用 PC 核数。
- NAS 任务仍保持单任务串行，可与 PC 任务并行；PC 任务线程分配不受 NAS 设置影响。
- 任务卡片显示实际并发、硬上限和探测到的 NAS 核数。

### 错误提示

- SSH 认证失败显示“NAS 认证失败：请检查 SSH 用户与私钥”。
- 超时、连接拒绝、无路由或横幅交换失败显示“NAS 不可达（设备可能离线或资源耗尽）”。
- 其他 SSH 错误保留退出码和原始诊断。

## 立即处置

1. 物理重启 NAS。
2. 检查 OOM 证据：

```sh
dmesg -T | grep -iE 'oom|killed process'
journalctl -k -n 200
```

3. 保留 `~/.cache/imgzip/jobs/*/diagnostic.log`，其中应包含：

```text
concurrency: requested=32 nas_cores=4 effective=4 cap=4
dispatch: next=... running=... limit=... mem_available_kb=...
```

修复发布前，如果必须使用旧版 NAS 引擎，应手动把 `max_jobs` 改为 2，或改用 PC 引擎。不要继续让 0.3.4 以 32 并发运行。

## 验证

- C# / PowerShell 替代执行层检查：34 组通过。
- 新增检查覆盖 NAS 设置按探测核数夹取、NAS 不再继承 PC 线程数、PC/NAS 并发互不影响、认证失败与不可达提示区分。
- Git Bash 平台条件测试：4 组通过。
  - `THREADS=32` 时最大实际并发不超过 `min(nproc, 4)`；
  - `THREADS=0` 与 `THREADS=abc` 回退为 2（仍受硬件上限夹取）；
  - 模拟 1 GB 可用内存时最大实际并发为 1。
- 真机尚需重启 NAS 后验证：任务期间同时最多 2–4 个引擎进程、内存不被打满、SSH/SMB 持续可用、20 张 JPEG 性能仍优于旧的单核逐文件实现。

## 结论边界

当前最可能原因是 OOM，但在取得 `dmesg`、`journalctl` 或任务诊断前，不能把事故原因表述为已由内核日志确认。0.3.5 的加固不依赖该结论：即使其他原因导致失联，限制并发、读取内存和保留诊断仍能显著降低风险并改善定位。
