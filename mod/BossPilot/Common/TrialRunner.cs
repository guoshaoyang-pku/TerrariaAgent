using System.Linq;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace BossPilot.Common;

/// <summary>
/// Trial state machine: prepare the arena, spawn the scenario boss, run until
/// the boss dies, the player dies, or the scenario time limit elapses.
/// Driving it happens through the /bosspilot chat command; per-tick control
/// comes from ScriptPlayer once a script is loaded.
/// </summary>
public sealed class TrialRunner : ModSystem
{
    public enum Phase { Idle, Preparing, Fighting, Finished }

    public static TrialRunner Instance => ModContent.GetInstance<TrialRunner>();

    public Phase Current { get; private set; } = Phase.Idle;
    public ScenarioConfig Scenario { get; private set; }
    public string Result { get; private set; }

    private int fightStartTick;
    private int bossIndex = -1;

    public string Start(ScenarioConfig scenario)
    {
        if (Current != Phase.Idle && Current != Phase.Finished)
            return $"busy: phase {Current}";
        if (!BossCatalog.TryGet(scenario.Boss, out BossCatalog.BossEntry entry))
            return $"unknown boss: {scenario.Boss}";

        Scenario = scenario;
        Result = null;
        ArenaBuilder.Build(scenario.Arena, scenario.Biome, Main.LocalPlayer.Center);
        ApplyWorldFlags(scenario);
        ApplyBuffs(scenario);
        Current = Phase.Preparing;
        return $"prepared {scenario.Name} ({entry.Name})";
    }

    private void BeginFight()
    {
        BossCatalog.BossEntry entry = BossCatalog.Bosses[Scenario.Boss];
        Player p = Main.LocalPlayer;
        AutoPilot.Enabled = Scenario.Controller == "auto";
        if (Scenario.Controller == "script" && !ScriptPlayer.Loaded)
            Main.NewText("[BossPilot] controller=script but no script loaded", Color.Yellow);
        Vector2 at = p.Center + new Vector2(0, -300);
        int id = NPC.NewNPC(new Terraria.DataStructures.EntitySource_DebugCommand("BossPilot"),
            (int)at.X, (int)at.Y, entry.NpcId);
        bossIndex = id;
        fightStartTick = (int)Main.GameUpdateCount;
        StateExport.Instance.Begin(Scenario.Name);
        Current = Phase.Fighting;
    }

    public override void PostUpdateEverything()
    {
        switch (Current)
        {
            case Phase.Preparing:
                BeginFight();
                break;
            case Phase.Fighting:
                TickFight();
                break;
        }
    }

    private void TickFight()
    {
        Player p = Main.LocalPlayer;
        bool bossGone = bossIndex < 0 || !Main.npc[bossIndex].active;
        double elapsed = (Main.GameUpdateCount - fightStartTick) / 60.0;

        if (bossGone) Finish("victory");
        else if (p.dead) Finish("death");
        else if (elapsed >= Scenario.MaxDurationSeconds) Finish("timeout");
    }

    private void Finish(string result)
    {
        Result = result;
        StateExport.Instance.End();
        Main.NewText($"[BossPilot] {Scenario.Name}: {result}", Color.Orange);
        Current = Phase.Finished;
    }

    public void Abort()
    {
        StateExport.Instance.End();
        Current = Phase.Idle;
    }

    public object ActiveBossJson()
    {
        if (bossIndex < 0 || !Main.npc[bossIndex].active) return null;
        NPC n = Main.npc[bossIndex];
        return new
        {
            id = n.type,
            x = n.Center.X / 16f, y = n.Center.Y / 16f,
            hp = n.life, hp_max = n.lifeMax,
            ai = n.ai.ToArray(),
        };
    }

    private static void ApplyWorldFlags(ScenarioConfig scenario)
    {
        Main.dayTime = scenario.Time == "day";
        Main.time = scenario.Time == "day" ? 27000 : 0;
        switch (scenario.Weather)
        {
            case "rain":
                Main.raining = true; Main.StartRain();
                break;
            case "clear":
                Main.raining = false; Main.StopRain();
                break;
            case "sandstorm":
                // Only meaningful in the desert; scenarios must place the arena there.
                Terraria.GameContent.Events.Sandstorm.StartSandstorm();
                break;
            case "blizzard":
                // Blizzard = rain while in the snow biome; scenario must use biome "snow".
                Main.raining = true; Main.StartRain();
                break;
            case "wind":
                Main.windSpeedTarget = 0.8f;
                break;
        }
    }

    private static void ApplyBuffs(ScenarioConfig scenario)
    {
        if (scenario.Buffs != "standard") return;
        Player p = Main.LocalPlayer;
        int duration = (int)(scenario.MaxDurationSeconds * 60) + 3600;
        p.AddBuff(Terraria.ID.BuffID.Campfire, duration);
        p.AddBuff(Terraria.ID.BuffID.HeartLamp, duration);
    }
}
