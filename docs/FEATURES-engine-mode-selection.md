# 引擎默认选择：自动推断 / 固定本机 PC / 记住上次切换

**状态：已记录，未实施。** 目标版本 **0.4.0**。

## 背景

当前 `EngineIndex` 完全由来源路径推断，没有用户可配置的默认值：

```csharp
// src/ImgZip.Core/MainViewModel.cs:179
EngineIndex = NasEligible ? 1 : 0;
```

`NasEligible`（[MainViewModel.cs:85](../src/ImgZip.Core/MainViewModel.cs)）要求已配置 Server，且全部来源是 UNC 路径或网络映射盘。用户手动点击引擎开关只修改当前草稿，不落盘，换一批来源就重新推断。

对"NAS 共享上的图想用本机 32 核压完"这类用法，用户每次都要手动切一次；对长期只用一侧的用户，首次选择也依赖路径形态猜。

## 三种模式

`AppConfig.EngineMode` 取三个值：

| 值 | 名称 | 行为 |
| --- | --- | --- |
| `auto` | 自动推断（默认） | 保持现状：来源在 NAS 共享上→NAS，否则→PC |
| `fixedPc` | 固定本机 PC | 不管来源在哪，一律 PC |
| `last` | 记住上次切换 | 新草稿沿用上次手动选择的引擎 |

**不做"固定 NAS"。** 因为 NAS 引擎不接受本地路径——`worker/imgzip-worker.ps1` 的 `Resolve-NasSources` 会直接抛出 `NAS engine requires a UNC share or mapped NAS drive.`。本地来源选了固定 NAS 也必然被夹取回 PC，行为与 `auto` 完全一致，没有独立价值。

`fixedPc` 反而有实际用途：NAS 任务并发硬编码为 1（[MainViewModel.Tasks.cs:131](../src/ImgZip.Core/MainViewModel.Tasks.cs)），而 PC 侧按 `PcConcurrency` 并发并可组批调用；把 NAS 共享上的图拉到本机压完，通常比串行跑 NAS 快。

## 关键设计：资格与偏好必须分离

`NasEligible` 目前同时承担两种职责，实施时必须拆开：

| 位置 | 作用 | 性质 |
| --- | --- | --- |
| `SetSourcesAsync:179` | 决定默认选中谁 | **偏好**，可被模式覆盖 |
| `CanStartTask:74` | NAS 模式下能否开始 | **资格**，不可覆盖 |
| `SyncEngineButtons` / `EngineButton_Click` | 禁用 NAS 按钮 | **资格**，不可覆盖 |

资格判定不变：来源不是 UNC/映射盘，NAS 永远不可选，无论模式选了什么。

### 夹取规则

模式算出的候选引擎若通过不了资格检查，本次回落 PC，**但不改写记忆值**：

```
选择引擎(来源, 模式):
    候选 = 模式 switch {
        fixedPc → PC
        last    → LastEngine
        auto    → 来源在 NAS 共享上 ? NAS : PC
    }
    return 候选 == NAS && !NasEligible ? PC : 候选      // 只影响本次
```

"不覆盖记忆"是核心细节。若夹取时顺手把 `LastEngine` 改成 PC，功能语义会从"记住上次手动选择"退化为"记住最后一次实际运行的引擎"，两者不同。例：记住 NAS → 拖入本地文件夹 → 回落 PC（记忆仍为 NAS）→ 再拖入 NAS 共享 → 自动回到 NAS。

### 记录时机

采用**点击即记**，与既有 `SetThemeAsync` / `SetCopySkippedFilesAsync` 的"改属性→立即保存→失败降级提示"模式一致：

```
EngineButton_Click → EngineIndex = index → SetLastEngineAsync(...)
```

不采用"任务提交时才记"或"任务成功后记"，因为与"记住上次切换"字面不符，行为难以预期。

## 数据结构

`AppConfig` 新增两个字段，旧配置缺省时保持 `auto`，升级零行为变化：

```csharp
/// <summary>引擎默认选择：auto（自动推断）/ fixedPc（固定本机）/ last（记住上次）。</summary>
public string EngineMode { get; init; } = "auto";
/// <summary>last 模式记住的引擎：pc / nas。</summary>
public string LastEngine { get; init; } = "pc";
```

`Normalize()` 中兜底，非法值归位：

```csharp
EngineMode = EngineMode is "auto" or "fixedPc" or "last" ? EngineMode : "auto",
LastEngine = LastEngine is "pc" or "nas" ? LastEngine : "pc"
```

`fixedPc` 模式下 `LastEngine` 仍可继续记录（切换引擎时照常落盘），这样切回 `last` 模式能立刻生效，不会丢失历史偏好。

## UI

设置对话框"性能"分组新增三选 ComboBox（`SettingsDialog.xaml` 第 18–21 行附近）：

- 自动推断（按来源位置选择）
- 固定本机 PC
- 记住上次切换

`固定本机 PC` 选中后，主界面引擎分段控件应仍可临时切到 NAS（只要来源合法）——模式只是默认值，不锁死当次选择。

主界面**不新增**任何引擎模式入口。标题栏下的"本机 PC / NAS"分段控件已承担当次切换职责，模式属于低频配置项，放设置里即可。

### 回落提示

`last` / `auto` 模式下若发生夹取回落，用引擎状态行（`EngineStatus`）显示一行说明，例如：

> 来源在本机，已临时切到 PC（记住的偏好是 NAS）。

不提示会让这个功能显得"时灵时不灵"，是智能默认最常见的抱怨点。

选状态行文本而非 InfoBar：`EngineStatus` 本就是引擎状态的常驻展示位，回落信息属于同一语义；InfoBar 需要额外的开关与关闭态管理，且会挤占布局，对一个"每次添加来源就可能出现"的提示来说过重。

## 改动清单

| 文件 | 改动 |
| --- | --- |
| `src/ImgZip.Core/Models.cs` | `AppConfig` 加 `EngineMode` / `LastEngine` + `Normalize` 兜底 |
| `src/ImgZip.Core/MainViewModel.cs` | 抽出纯函数 `PickEngine(模式, 来源, 资格)`；`SetSourcesAsync` 改用；新增 `SetEngineModeAsync` / `SetLastEngineAsync` |
| `src/ImgZip.App/SettingsDialog.xaml(.cs)` | 三选 ComboBox，填充与读取 |
| `src/ImgZip.App/MainWindow.xaml.cs` | `EngineButton_Click` 记录 `LastEngine`；回落提示渲染 |
| `tests/ImgZip.Tests/*` | 见下 |

协议不变（`WorkerRequest.version` 仍为 1），`AppConfig` 是随请求传递的快照对象，加字段只影响本机配置与请求正文，旧 worker 忽略未知字段即可。

## 测试用例

- 旧配置缺少两个字段 → 加载后为 `auto` / `pc`，行为等同现状
- `Normalize` 对非法 `EngineMode` / `LastEngine` 归位
- `auto`：NAS 来源→NAS；本地来源→PC
- `fixedPc`：NAS 来源→PC；本地来源→PC
- `last` + 记忆 nas：NAS 来源→NAS
- `last` + 记忆 nas：本地来源→PC，且 `LastEngine` **仍为 nas**
- `last` + 记忆 nas → 再切回 NAS 来源 → 回到 NAS
- 切换引擎触发保存；保存失败给出提示但不阻塞操作
- `fixedPc` 下 `LastEngine` 仍随切换更新

## 已定

1. 主界面不新增引擎模式入口，模式只在设置对话框配置。
2. 回落提示用引擎状态行文本（`EngineStatus`），不用 InfoBar。
3. 归属版本 **0.4.0**。
