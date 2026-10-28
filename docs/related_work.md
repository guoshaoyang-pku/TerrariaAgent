# 相关项目调研（2026-10-28）

核查口径来自 Spec 第 18 节：输入是什么、接口封装到哪一层、代码由谁写、运行时是否有人干预、
Agent 数量、种子来源、实际成绩。结论：**没有发现已实现"随机种子自主通关全流程 + 全 Boss"
的项目**；最接近的几个项目分别只覆盖 Boss 战、局部任务或概念验证。

## 直接可借鉴

| 项目 | 覆盖 | 可借鉴点 |
| --- | --- | --- |
| [MutantPilot](https://github.com/snsididjdjsjsj/MutantPilot)（third_party 已克隆） | 单个 Mod Boss（突变体）零受击走位 | 模组内读状态 + 原生输入做预测控制的完整工程结构；configs/results/check.py 的组织方式 |
| [Tairaria](https://github.com/Reisenbug/Tairaria) + [TerraBlind](https://github.com/Reisenbug/TerraBlind)（third_party 已克隆） | LLM 四层架构 Bot（大脑 0.1Hz / 反射 5Hz / 状态 HTTP / 执行 60Hz） | 与本 Spec 三层职责同构；TerraBlind 用 HTTP 暴露游戏状态，可作状态接口参考 |
| [onisps/terraria_remote_control](https://github.com/onisps/terraria_remote_control) | MCP 让本地 LLM 挖矿/建房/合成 | MCP 接口形态参考；概念验证级 |
| [terrar.ai](https://github.com/SlothfulDreams/terrar.ai) | Grok 驱动 NPC 助手 | 人机协作方向，非自主通关 |

## 难度证据

- [TerrariaBench](https://terrariabench.com/)：前沿模型（Gemini 3 Flash/Pro、Claude Opus 4.7）
  在真实 tModLoader 环境开放式推进，8 小时预算最好成绩停在"100 木头"（L2）。
  说明纯 LLM + 窗口操控路线远不能通关，Spec 的上/下层分工有公开数据支撑。

## 其他方向

- RL：[dkoleber/TerrarAI](https://github.com/dkoleber/TerrarAI)（状态上报平台）、
  [samueleallen/Terraria-RL](https://github.com/samueleallen/Terraria-RL)（PPO+视觉）、
  [BenSisk/Terraria-Reinforcement-Learning](https://github.com/BenSisk/Terraria-Reinforcement-Learning)
  （仅前两个 Boss）——都停在战斗子问题。
- TAS 社区：减速 + 分段回放 + 已知种子全图，人工制作，非自主。
- [iMuffin 用 C++ 重写 Terraria 做 RL 环境](https://x.com/iMuffined/status/2091993742832284071)（2026-08），
  若发布对 MPC 内核有参考价值，值得跟踪。
- Minecraft 侧对照：arXiv 2610.22319（4×Gemini 4 Argon + Mineflayer，27h14m 屠龙，605 死）、
  Superbloom Steve（100 种子 90 成功，均 46h）——见 Spec 附录 B。
