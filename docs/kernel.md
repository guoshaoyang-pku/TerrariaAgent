# 游戏内核源码（本地）

源码在 `third_party/kernel/`，不进仓库；用 `scripts/setup_kernel.sh [runtime|145|all]` 重建。

## 版本对照（2026-10-28 本机）

| 组件 | 版本 | 位置 |
|---|---|---|
| Steam Terraria（原版） | 1.4.5.8，Mac FNA 构建 | `~/Library/Application Support/Steam/steamapps/common/Terraria/Terraria.app/Contents/Resources` |
| Steam tModLoader（稳定版） | v2026.08.3.0，内含 Terraria **1.4.4.9** | `.../steamapps/common/tModLoader` |
| `third_party/tModLoader` 克隆 | 分支 `1.4.5`（开发版，领先于稳定版） | 仅供参考 |

BossPilot 运行在稳定版 tModLoader 上，所以 **MPC/预测以 runtime 内核为准**。

## 两份内核

1. **runtime（与运行时逐字节一致）**：`third_party/kernel/tModLoader-v2026.08.3.0/`
   ILSpy 9.1 反编译已安装的 `tModLoader.dll`（Terraria 1.4.4.9 + tML 补丁合一），1907 个 .cs。
   无注释；API 文档看 Steam 目录的 `tModLoader.xml`。
2. **1.4.5 源码树**：`third_party/kernel/tModLoader-1.4.5-src/src/`（tML 官方 setup 生成）
   - `decompiled/` 原版 1.4.5.8 Windows 反编译；`Terraria/`、`TerrariaNetCore/` 可编译的原版，均无冲突。
   - `tModLoader/` 是 tML 1.4.5 补丁层，有 13 个文件共 101 个 hunk 未应用（`*.rej`，Player.cs 39、Main.cs 16），开发分支尚未完成迁移，只作参考。

## 关键入口（runtime 内核，`Terraria/` 下，行号对应 v2026.08.3.0）

| 用途 | 位置 |
|---|---|
| 每帧主循环 | `Main.cs` `DoUpdate` 16466、`DoUpdateInWorld` 17353、`UpdateTime` 62810 |
| 玩家物理 | `Player.cs` `Update` 21371、`HorizontalMovement` 17588、`JumpMovement` 18403、`DryCollision` 20616、`WaterCollision` 20551 |
| 玩家受击 / 使用物品 | `Player.cs` `Hurt` 34843、`ItemCheck` 39208 |
| 地形碰撞 | `Collision.cs` `TileCollision` 1913、`SlopeCollision` 1583、`SolidCollision` 2224、`CanHit` 345 |
| NPC 更新与 AI | `NPC.cs` `UpdateNPC` 88595、`VanillaAI_Inner`（按 `aiStyle` 分派）14625 起；独立 Boss AI 如 `AI_037_Destroyer`、`AI_069_DukeFishron`、`AI_120_HallowBoss`、`AI_121_QueenSlime`、`AI_123_Deerclops` |
| 弹幕 | `Projectile.cs` `Update` 14808、`VanillaAI` 19392、`Damage` 11560 |

## 构建环境要点

- PATH 上的 Homebrew `dotnet` 只有 .NET 10；tModLoader 稳定版需要 .NET 8，构建 Mod 时用
  `DOTNET_ROOT=~/.dotnet PATH=~/.dotnet:$PATH`。tML 1.4.5 的 setup 工具则需要 .NET 10（Homebrew）。
- macOS 上 tML 数据目录是 `~/Library/Application Support/Terraria/tModLoader/`，
  `ModSources/tModLoader.targets` 由首次启动 tML 或首次 `-build` 自动生成；新机器上若还没有，
  先用 `dotnet build -p:tMLTargets="<Steam>/tModLoader/tMLMod.targets"` 构建一次。
