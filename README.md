# Terraria Agent

长程自主玩 Terraria 的 Agent：上层大模型管阶段目标、配装与资源，下层脚本与搜索控制管跑图、
走位、采矿、合成、建房和即时避险，利用游戏内核做 MPC 式预测。

## 设计 Spec（先读这个）

**[docs/Terraria_Agent_Design_Spec.md](docs/Terraria_Agent_Design_Spec.md)** 是本项目的需求基准。
实现时以其中的"用户明确要求"为准；标注为"待定""助手建议"的内容不能当成已定需求。

## 当前阶段：Boss 挑战矩阵

目标：先让所有 Boss 的挑战在各种场景下做成功（见 Spec 第 1、8、21 节）。

- `configs/boss_matrix.json` — 18 个原版 Boss × 场景轴（场地 / 群落 / 昼夜 / 天气 / 配装档 / 增益），共 47 个场景。
- `mod/BossPilot/` — tModLoader 模组：按场景建场地、调天气、生成 Boss、导出 6 Hz 状态快照（JSONL）、回放输入脚本。`/bosspilot start <name>` 启动一场试炼。
- `scripts/check.py` — 不启动游戏的静态检查（矩阵合法性 + 模组文件结构）。

走位/战斗控制脚本目前参考 [MutantPilot](https://github.com/snsididjdjsjsj/MutantPilot)
（已在 `third_party/` 克隆）：模组内读游戏状态、用原生输入做预测走位，已有突变体零受击成绩。

## 目录

- `docs/` — Spec、调研记录
- `mod/BossPilot/` — Boss 挑战 harness（tModLoader 模组）
- `configs/` — Boss 场景矩阵
- `scripts/` — 无游戏检查
- `third_party/` — 参考项目克隆（tModLoader 内核源码、MutantPilot、Tairaria/TerraBlind），不进仓库

## 基建状态

- [x] tModLoader 内核源码（third_party/tModLoader）
- [x] .NET SDK（~/.dotnet，免 sudo 安装）
- [x] Boss 场景矩阵与 harness 骨架
- [ ] 安装 Terraria + tModLoader（需用户操作）
- [ ] tModLoader setup 生成完整反编译内核
- [ ] BossPilot 进游戏编译、跑通首个场景
- [ ] 逐 Boss 接入控制脚本，跑通全部场景

## 许可

代码 MIT。Terraria 版权归 Re-Logic；本仓库不含游戏程序或素材。
