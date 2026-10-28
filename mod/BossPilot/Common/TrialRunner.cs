using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;

namespace BossPilot.Common;

/// <summary>
/// Trial state machine: build the arena at the plan's site, grant the
/// loadout, spawn the boss, run until it is killed, the pilot dies, it
/// despawns, or the scenario time limit elapses. Started by the chat command,
/// AutorunSystem (GUI) or JobRunner (headless server); per-tick control comes
/// from the boss plan, AutoPilot or ScriptPlayer.
/// </summary>
public sealed class TrialRunner : ModSystem
{
    public enum Phase { Idle, Preparing, Fighting, Finished }

    public static TrialRunner Instance => ModContent.GetInstance<TrialRunner>();

    // Ticks between arena build and boss spawn, so biome scans and landing settle.
    private const int SettleTicks = 45;

    public Phase Current { get; private set; } = Phase.Idle;
    public ScenarioConfig Scenario { get; private set; }
    public BossPlan Plan { get; private set; }
    public FightContext Context { get; private set; }
    public string Result { get; private set; }

    // Per-attempt metrics; Hits/DamageTaken/Kills are fed by TrialMetrics hooks.
    public int Hits { get; internal set; }
    public int DamageTaken { get; internal set; }
    public int Kills { get; private set; }
    public float BossHpFraction { get; private set; } = 1f;
    public int FightTicks => Context?.Tick ?? 0;

    private HashSet<int> trackedTypes = [];
    private int settleTicks;
    private bool bossSeen;

    public string Start(ScenarioConfig scenario)
    {
        if (Current is Phase.Preparing or Phase.Fighting)
            return $"busy: phase {Current}";
        if (!BossCatalog.TryGet(scenario.Boss, out BossCatalog.BossEntry entry))
            return $"unknown boss: {scenario.Boss}";

        Scenario = scenario;
        Plan = BossPlan.For(scenario.Boss);
        Result = null;
        Hits = DamageTaken = Kills = 0;
        BossHpFraction = 1f;
        bossSeen = false;
        trackedTypes = [entry.NpcId, .. entry.ExtraPartIds];

        Player p = Main.LocalPlayer;
        Point site = Plan?.ArenaSite(scenario) ?? ArenaSites.Default(scenario.Biome);
        ArenaBuilder.Build(scenario.Arena, scenario.Biome, site.ToWorldCoordinates());
        Plan?.DecorateArena(site, scenario);
        p.position = new Vector2(site.X * 16 - p.width / 2f, site.Y * 16 - p.height - 2);
        p.velocity = Vector2.Zero;
        p.fallStart = site.Y;

        if (Plan != null) Plan.GrantLoadout(p, scenario);
        else LoadoutGiver.Grant(p, entry, scenario.Loadout);
        AutoPilot.Enabled = scenario.Controller == "auto";
        if (scenario.Controller == "script" && !ScriptPlayer.Loaded)
            Main.NewText("[BossPilot] controller=script but no script loaded", Color.Yellow);
        ApplyWorldFlags(scenario);
        ApplyBuffs(scenario);

        Context = new FightContext { Scenario = scenario, Site = site };
        settleTicks = 0;
        Current = Phase.Preparing;
        return $"prepared {scenario.Name} ({entry.Name}{(Plan == null ? ", no plan" : "")})";
    }

    private void BeginFight()
    {
        Player p = Main.LocalPlayer;
        if (Plan != null) Plan.SpawnBoss(p, Scenario);
        else BossPlan.SpawnAbove(p, BossCatalog.Bosses[Scenario.Boss].NpcId, 25);
        StateExport.Instance.Begin(Scenario.Name);
        Current = Phase.Fighting;
    }

    public override void PostUpdateEverything()
    {
        switch (Current)
        {
            case Phase.Preparing:
                if (++settleTicks >= SettleTicks) BeginFight();
                break;
            case Phase.Fighting:
                TickFight();
                break;
        }
    }

    private void TickFight()
    {
        Context.Tick++;
        long life = 0, lifeMax = 0;
        bool any = false;
        foreach (NPC n in Main.npc)
        {
            if (!n.active || !trackedTypes.Contains(n.type)) continue;
            any = true;
            life += Math.Max(n.life, 0);
            lifeMax += n.lifeMax;
        }
        if (any)
        {
            bossSeen = true;
            BossHpFraction = lifeMax > 0 ? (float)life / lifeMax : 0f;
        }

        if (Main.LocalPlayer.dead) Finish("death");
        else if (!any && Context.Tick > 2) Finish(!bossSeen ? "spawn_failed" : Kills > 0 ? "victory" : "despawn");
        else if (Context.Tick / 60.0 >= Scenario.MaxDurationSeconds) Finish("timeout");
    }

    internal void NoteKill(int npcType)
    {
        if (Current == Phase.Fighting && trackedTypes.Contains(npcType)) Kills++;
    }

    private void Finish(string result)
    {
        Result = result;
        if (result == "victory") BossHpFraction = 0f;
        StateExport.Instance.End();
        Main.NewText($"[BossPilot] {Scenario.Name}: {result} in {FightTicks / 60.0:F1}s, hits {Hits}", Color.Orange);
        Current = Phase.Finished;
    }

    public void Abort()
    {
        StateExport.Instance.End();
        Current = Phase.Idle;
    }

    /// <summary>The live NPC of the boss's main type, if any.</summary>
    public NPC MainBoss()
    {
        if (Scenario == null) return null;
        int type = BossCatalog.Bosses[Scenario.Boss].NpcId;
        foreach (NPC n in Main.npc)
            if (n.active && n.type == type) return n;
        return null;
    }

    public object ActiveBossJson()
    {
        NPC n = MainBoss();
        if (n == null) return null;
        return new
        {
            id = n.type,
            x = n.Center.X / 16f, y = n.Center.Y / 16f,
            hp = n.life, hp_max = n.lifeMax,
            ai = n.ai.Clone(),
        };
    }

    public override void Unload() => BossPlan.Unload();

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

/// <summary>Feeds hit and kill counts of the running attempt into TrialRunner.</summary>
public sealed class TrialMetricsPlayer : ModPlayer
{
    public override void OnHurt(Player.HurtInfo info)
    {
        TrialRunner t = TrialRunner.Instance;
        if (t.Current != TrialRunner.Phase.Fighting || Player.whoAmI != Main.myPlayer) return;
        t.Hits++;
        t.DamageTaken += info.Damage;
    }
}

public sealed class TrialMetricsNPC : GlobalNPC
{
    public override void OnKill(NPC npc) => TrialRunner.Instance.NoteKill(npc.type);
}
