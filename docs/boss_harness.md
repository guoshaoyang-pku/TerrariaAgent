# Boss 试炼 harness（无窗口、可并行）

## 内核：dedicated server 当无头单机

`scripts/run_trials.py` 为每个 worker 起一个 `tModLoader.dll -server -nosteam` 进程（独立
`-tmlsavedirectory`、独立世界副本、空闲端口）。BossPilot 的 `HeadlessSim` 在服务端主循环回调
`Main.OnTickForThirdPartySoftwareOnly` 里接管：

- `netMode = 0`、`myPlayer = 0`，0 号槽放一个 Pilot 玩家 → 物理、出招、受击、Boss AI 全按单机逻辑走；
- `dedServ` 保持为真 → 不渲染、不出声、不开窗口，不抢前台；
- 不受 60 Hz 限制，每次回调跑满 50 ms 的 tick（`speedup` 列给出实际倍速）；
- 致命错误弹窗被拦截，只写控制台；`Main.Update` 吞掉的异常记到 `out/exceptions.log`。

## 运行

```bash
python3 scripts/run_trials.py --boss king_slime --attempts 5 --parallel 2
python3 scripts/run_trials.py --scenario ks_flat_day eoc_flat_night
python3 scripts/run_trials.py --all --parallel 12
```

首次运行会构建 Mod 到 `.runs/_build/`、生成小世界模板到 `.runs/_world/`（约 3 分钟，之后复用）。
结果：`.runs/<run>/w<k>/out/results.jsonl`（每次尝试一行）、`.runs/<run>/summary.json`；
6 Hz 状态快照在同目录 `*.jsonl`，可离线可视化。

每行字段：`result`（victory / death / timeout / despawn / spawn_failed / crash:…）、`seconds`（游戏内秒）、
`hits`、`damage_taken`、`boss_hp_frac`、`plan`、`speedup`。

## 写一个 Boss 方案

在 `mod/BossPilot/Bosses/` 下每个 Boss 一个文件，继承 `Common/BossPlan.cs` 的 `BossPlan`，按 `Boss` 键
（`BossCatalog` 名）自动发现，不需要改注册表：

| 覆盖 | 作用 | 默认 |
|---|---|---|
| `GrantLoadout(p, s)` | 配装：武器、弹药、盔甲、饰品、药水、生命/魔力上限 | `LoadoutGiver`（Minishark/Megashark） |
| `ArenaSite(s)` | 场地中心（地板行，图格坐标） | 按群落：海洋取左边缘、地狱取底部、太空取高空… |
| `DecorateArena(site, s)` | 场地加工（营火、心灯、神庙砖墙、地牢墙…） | 无 |
| `SpawnBoss(p, s)` | 召唤方式 | `NPC.SpawnOnPlayer`（等同召唤物） |
| `Control(p, boss, ctx)` | 每 tick 设置 `p.controlLeft/Right/Jump/Down/UseItem` 等，`FightContext.Aim(world)` 瞄准；返回 false 交给 AutoPilot | false |

控制在 `ModPlayer.SetControls` 阶段调用（`PreUpdate` 设置的控制会被输入层覆盖）。
`ctx.Memory` 可存跨 tick 的状态；`ctx.Tick` 是本次战斗的 tick 数。

生命/魔力上限用 `LoadoutGiver.SetMaxStats(p, life, mana)`：tML 每 tick 按已吃生命水晶/果重算
`statLifeMax`，直接写 `statLifeMax` 下一 tick 就被改回 100。

## 无头模式已处理的坑（`Common/Headless/`）

服务端从不初始化客户端子系统，而 `netMode = 0` 会让原本只在客户端跑的代码执行。已修：
光照引擎（Legacy 模式，有上限）、相机视图矩阵、屏幕滤镜/天空/覆盖层占位（`NullEffects`）、
成就管理器、`Main.hasFocus`（否则 `SetControls` 不被调用）、网络线程会把无客户端的玩家槽设为不活跃、
云/血肉/伤害数字/拾取文字/RGB 键盘、死亡时存档线程崩溃、致命错误弹窗。
新出现的问题看 `out/exception_counts.json`：除 `Throw.OutOfBounds`（tML 日志钩子）和网络类异常外应为空；
任何 `Main.DoUpdate`/`Player.Update` 下的异常都意味着该 tick 被截断，结果不可信。

## 约束

- 只用无头服务器跑试炼，不启动游戏客户端窗口。
- 世界规则、Boss AI 以 `third_party/kernel/tModLoader-v2026.08.3.0/`（运行时内核，Terraria 1.4.4.9）为准。
- 目标：标准平台场地、对应群落/时间，尽量无伤（`hits == 0`）且用时最短；配装要符合该 Boss 的流程阶段。
