using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using BossPilot.Bosses.EvilDeer;
using BossPilot.Common;

namespace BossPilot.Bosses;

/// <summary>
/// Deerclops (NPC.AI_123_Deerclops, 7000 HP Classic).
///
/// Kernel facts used:
/// - Ice spikes (states 1/4) need the target's closest point within 120 px horizontally and
///   between 200 px above and ~100 px below Deerclops' feet-32; a pilot 15+ tiles above its
///   feet never triggers them, and Deerclops does not climb to platforms it is not touching.
/// - It halts when the target is within 80 px horizontally; standing still for 90 ticks it
///   casts six shadow hands (state 5, projectile 965) whose four AI_187 variations are
///   deterministic after spawn. Walking 240 ticks gives rubble (state 2), 120 ticks while
///   100+ px away gives the Slow roar (state 3).
/// - It becomes dontTakeDamage once the target stays 450+ px away; running away needs the
///   target 2400 px away (snow only matters while already fleeing).
/// Arena: the standard snow platforms with the pilot on the 24-tile row (natural snow:
/// a platform bridge ~20 tiles above the ground). The pilot hovers over Deerclops so it
/// stays halted and only casts hands, which the planner simulates and dodges while the bow fires.
/// </summary>
public sealed class Deerclops : BossPlan
{
    public override string Boss => "deerclops";

    private const int FlatRow = 24;
    private const int NaturalRise = 20;
    private const int NaturalHalf = 60;

    private readonly Planner planner = new();
    private readonly Gunner gunner = new();
    private FightContext ctxSeen;
    private float bridgeFeet, homeX, halfSpan;
    private Vector2 deerC;

    // stage_default (around Skeletron/Queen Bee, hellstone available): Molten Fury + Hellfire
    // Arrows, fossil armor (ranged), boots/bottle/balloon/horseshoe/anklet, 400 HP and the
    // usual Swiftness/Ironskin/Archery/Regeneration. min_preboss: the evil-boss kit (ore bow +
    // Jester's). stage_plus: Hellwing Bow (shadow chest) with necro armor.
    public override void GrantLoadout(Player p, ScenarioConfig s)
    {
        Kit.Reset(p);
        switch (s.Loadout)
        {
            case "min_preboss":
                Kit.Hotbar(p, 0, ItemID.PlatinumBow);
                Kit.Ammo(p, ItemID.JestersArrow);
                Kit.Armor(p, ItemID.PlatinumHelmet, ItemID.PlatinumChainmail, ItemID.PlatinumGreaves);
                Kit.Accessories(p, ItemID.HermesBoots, ItemID.CloudinaBottle, ItemID.LuckyHorseshoe);
                LoadoutGiver.SetMaxStats(p, 300);
                break;
            case "stage_plus":
                Kit.Hotbar(p, 0, ItemID.HellwingBow);
                Kit.Ammo(p, ItemID.HellfireArrow);
                Kit.Armor(p, ItemID.NecroHelmet, ItemID.NecroBreastplate, ItemID.NecroGreaves);
                Kit.Accessories(p, ItemID.HermesBoots, ItemID.CloudinaBottle, ItemID.ShinyRedBalloon, ItemID.LuckyHorseshoe, ItemID.AnkletoftheWind);
                Kit.Buffs(p, s.MaxDurationSeconds, BuffID.Swiftness, BuffID.Ironskin, BuffID.Archery, BuffID.Regeneration);
                LoadoutGiver.SetMaxStats(p, 400);
                break;
            default:
                Kit.Hotbar(p, 0, ItemID.MoltenFury);
                Kit.Ammo(p, ItemID.HellfireArrow);
                Kit.Armor(p, ItemID.FossilHelm, ItemID.FossilShirt, ItemID.FossilPants);
                Kit.Accessories(p, ItemID.HermesBoots, ItemID.CloudinaBottle, ItemID.ShinyRedBalloon, ItemID.LuckyHorseshoe, ItemID.AnkletoftheWind);
                Kit.Buffs(p, s.MaxDurationSeconds, BuffID.Swiftness, BuffID.Ironskin, BuffID.Archery, BuffID.Regeneration);
                LoadoutGiver.SetMaxStats(p, 400);
                break;
        }
        // TrialRunner put the pilot on the floor; start on the standing row instead.
        Point site = new((int)(p.Center.X / 16f), (int)Math.Round(p.Bottom.Y / 16f));
        p.position.Y = BridgeRow(site, s) * 16 - p.height;
        p.velocity = Vector2.Zero;
        p.fallStart = (int)(p.position.Y / 16f);
        p.ForceUpdateBiomes();
    }

    public override Point ArenaSite(ScenarioConfig s) => s.Arena == "natural" ? Arena.Cached("snow-natural", SnowSite) : Arena.SpawnSite();

    public override void DecorateArena(Point site, ScenarioConfig s)
    {
        if (s.Arena != "natural") return;
        int row = BridgeRow(site, s);
        Arena.ClearBox(site.X - NaturalHalf, site.X + NaturalHalf, row - 6, row);
        Arena.PlatformRow(site.X - NaturalHalf, site.X + NaturalHalf, row);
        // A lower row where the ground dips (13+ tiles under it, taller than Deerclops), so a
        // pilot that had to drop can climb back without Deerclops ever touching the row.
        for (int x = site.X - NaturalHalf; x <= site.X + NaturalHalf; x++)
        {
            int ground = Arena.SolidBelow(x, row + 1);
            if (ground > row + 21) Arena.PlatformRow(x, x, row + 8);
        }
    }

    private static int naturalBridge;

    private static int BridgeRow(Point site, ScenarioConfig s) => s.Arena == "natural" ? naturalBridge : site.Y - FlatRow;

    public override void SpawnBoss(Player p, ScenarioConfig s)
    {
        // The Deer Thing brings it in from off-screen: put it on the ground 28 tiles to one side.
        int x = (int)(p.Center.X / 16f) + 28;
        int floor = Arena.SolidBelow(x, (int)(p.Bottom.Y / 16f) + 1);
        if (floor < 0) floor = (int)(p.Bottom.Y / 16f) + FlatRow;
        int i = NPC.NewNPC(new Terraria.DataStructures.EntitySource_BossSpawn(p), x * 16 + 8, floor * 16, NPCID.Deerclops);
        if (i < Main.maxNPCs) Main.npc[i].target = p.whoAmI;
    }

    public override bool Control(Player p, NPC boss, FightContext ctx)
    {
        if (ctx != ctxSeen) Begin(p, ctx);
        if (ctx.Tick % 30 == 0) p.ForceUpdateBiomes();

        HazardField f = planner.Field;
        f.Clear();
        NPC d = boss is { active: true, type: NPCID.Deerclops } ? boss : TrialRunner.Instance.MainBoss();
        if (d != null)
        {
            deerC = d.Center;
            DeerclopsModel.AddBody(f, d, p);
            DeerclopsModel.AddPendingSpikes(f, d, p, planner.Grid);
            DeerclopsModel.AddPendingRubble(f, d, planner.Grid);
        }
        foreach (Projectile pr in Main.projectile)
        {
            if (!pr.active || !pr.hostile) continue;
            if (pr.type == DeerclopsModel.IceSpike) DeerclopsModel.AddSpike(f, pr);
            else if (pr.type == DeerclopsModel.Rubble) DeerclopsModel.AddRubble(f, pr);
            else if (pr.type == DeerclopsModel.ShadowHand)
            {
                DeerclopsModel.AddShadowHand(f, pr);
                CheckHand(pr, f.Tracks[^1], ctx.Tick);
            }
        }
        Threats.AddProjectiles(f, DeerclopsModel.IceSpike, DeerclopsModel.Rubble, DeerclopsModel.ShadowHand);
        Threats.AddStrays(f, n => n.type == NPCID.Deerclops);

        planner.Plan(p, Objective);
        if (d != null) gunner.Fire(p, gunner.Lead(p.Center, d.Center, d.velocity), true);
        else gunner.Fire(p, p.Center, false);
        if (ctx.Tick % 120 == 0) TraceState(p, ctx, d);
        if (planner.PredictedHit <= 16) TraceDanger(p, ctx, d);
        history[ctx.Tick % history.Length] = $"{ctx.Tick}:({planner.Chosen.A},{planner.Chosen.B},{planner.Chosen.Switch},{planner.Chosen.JumpAt},{planner.Chosen.Down})h{planner.PredictedHit}y{p.position.Y / 16f:F1}";
        bool below = p.position.Y + p.height > bridgeFeet + 24f;
        if (below && !wasBelow)
        {
            string h = "";
            for (int k = 30; k >= 0; k--) h += " " + history[(ctx.Tick - k + history.Length * 4) % history.Length];
            Trace.Line($"[drop] t={ctx.Tick} p=({p.Center.X / 16f:F1},{p.Center.Y / 16f:F1}) v={p.velocity} history:{h}");
        }
        wasBelow = below;
        return true;
    }

    private void Begin(Player p, FightContext ctx)
    {
        ctxSeen = ctx;
        planner.Grid = ArenaGrid.Capture(ctx.Site, 110, 70, 40);
        bridgeFeet = BridgeRow(ctx.Site, ctx.Scenario) * 16;
        homeX = ctx.Site.X * 16 + 8;
        halfSpan = (ctx.Scenario.Arena == "natural" ? NaturalHalf : 60) * 16 - 48;
        gunner.Configure(p);
        gunner.Shots = 0;
        HitStats.Hits = 0;
        planner.ResetDiagnostics();
        handChecks.Clear();
        handLogs = 0;
        int row = BridgeRow(ctx.Site, ctx.Scenario), half = ctx.Scenario.Arena == "natural" ? NaturalHalf : 60;
        string gaps = "";
        for (int x = ctx.Site.X - half; x <= ctx.Site.X + half; x++)
            if (!planner.Grid.Platform(x, row)) gaps += $" {x}";
        if (gaps.Length > 0) Trace.Line($"[dc] bridge row {row} gaps:{gaps}");
        Trace.Line($"[dc] {ctx.Scenario.Name} start snow={p.ZoneSnow} snowTiles={Main.SceneMetrics.SnowTileCount} site={ctx.Site} bridge={bridgeFeet / 16f} speed={gunner.Speed:F1}");
    }

    private void TraceState(Player p, FightContext ctx, NPC d)
    {
        int hands = 0;
        foreach (Projectile pr in Main.projectile) if (pr.active && pr.type == DeerclopsModel.ShadowHand) hands++;
        Trace.Line($"[dc] t={ctx.Tick} life={(d == null ? -1 : d.life)} ai0={(d == null ? 0 : d.ai[0])} hands={hands} shots={gunner.Shots} hits={HitStats.Hits} " +
                   $"p=({p.Center.X / 16f:F0},{p.Center.Y / 16f:F0}) d=({(d == null ? 0 : d.Center.X / 16f):F0},{(d == null ? 0 : d.Center.Y / 16f):F0}) hit={planner.PredictedHit} snow={p.ZoneSnow} slow={p.slow} modelErr={planner.MaxErr}");
    }

    private readonly string[] history = new string[40];
    private bool wasBelow;
    private readonly System.Collections.Generic.Dictionary<int, (int due, Vector2 at, float ai0)> handChecks = [];
    private int handLogs;

    /// <summary>Diagnostics: compares each hand's 10-tick prediction with where it really goes.</summary>
    private void CheckHand(Projectile pr, Track tr, int tick)
    {
        if (handChecks.TryGetValue(pr.identity, out var c) && c.due == tick)
        {
            float err = Vector2.Distance(c.at, pr.Center);
            if (err > 3f && handLogs++ < 20) Trace.Line($"[handerr] ai0 {c.ai0}->{pr.ai[0]} err={err:F1} predicted={c.at} actual={pr.Center} v={pr.velocity} r={pr.rotation:F2}");
            handChecks.Remove(pr.identity);
        }
        if (!handChecks.ContainsKey(pr.identity) && tr.On[10]) handChecks[pr.identity] = (tick + 10, tr.At[10].Center, pr.ai[0]);
    }

    private void TraceDanger(Player p, FightContext ctx, NPC d)
    {
        string hs = "";
        foreach (Projectile pr in Main.projectile)
            if (pr.active && pr.hostile && Vector2.Distance(pr.Center, p.Center) < 260f)
                hs += $" [{pr.type} ai0={pr.ai[0]:F0} ai1={pr.ai[1]:F2} c=({pr.Center.X - p.Center.X:F0},{pr.Center.Y - p.Center.Y:F0}) v=({pr.velocity.X:F1},{pr.velocity.Y:F1}) r={pr.rotation:F2}]";
        Trace.Line($"[danger] t={ctx.Tick} hit@{planner.PredictedHit} m=({planner.Chosen.A},{planner.Chosen.B},{planner.Chosen.JumpAt},{planner.Chosen.CloudAt},{planner.Chosen.Down}) " +
                   $"p=({p.Center.X / 16f:F1},{p.Center.Y / 16f:F1}) v=({p.velocity.X:F1},{p.velocity.Y:F1}) dc={(d == null ? -1 : d.ai[0])}/{(d == null ? 0 : d.ai[1]):F0}{hs}");
    }

    private float Objective(in PilotState s, int t)
    {
        float x = s.Pos.X + 10f;
        float c = 0f;
        // Below the standing row Deerclops can spike: strongly prefer climbing back.
        float below = s.Pos.Y + 42f - bridgeFeet;
        if (below > 8f) c += 6000f + 10f * below;
        float dx = Math.Abs(x - deerC.X);
        if (dx > 40f) c += 0.004f * (dx - 40f) * (dx - 40f);
        // Beyond 450 px for 30 ticks it turns dontTakeDamage; jumping high wastes shots.
        float dist = Vector2.Distance(s.Pos + new Vector2(10f, 21f), deerC);
        if (dist > 400f) c += 0.05f * (dist - 400f) * (dist - 400f);
        float edge = Math.Abs(x - homeX) - halfSpan;
        if (edge > 0f) c += 400f + 2f * edge * edge;
        return c;
    }

    /// <summary>Flattest surface spot in the widest surface snow region (cached per server).</summary>
    private static Point SnowSite()
    {
        int n = Main.maxTilesX;
        int[] surf = new int[n];
        bool[] snow = new bool[n];
        for (int x = 50; x < n - 50; x++)
        {
            surf[x] = Arena.SolidBelow(x, 50, (int)Main.worldSurface + 40);
            if (surf[x] > 0)
            {
                ushort t = Main.tile[x, surf[x]].TileType;
                snow[x] = t == TileID.SnowBlock || t == TileID.IceBlock;
            }
        }
        int bestStart = -1, bestLen = 0;
        for (int x = 50, run = 0; x < n - 50; x++)
        {
            // Tolerate short gaps (trees, a stray dirt tile) inside the biome.
            bool on = snow[x] || (x + 3 < n && (snow[x + 1] || snow[x + 2] || snow[x + 3]) && run > 0);
            run = on ? run + 1 : 0;
            if (run > bestLen) { bestLen = run; bestStart = x - run + 1; }
        }
        if (bestLen < 60)
        {
            Trace.Line($"[site] no surface snow region (best {bestLen}); using spawn");
            Point spawn = Arena.SpawnSite();
            naturalBridge = spawn.Y - NaturalRise;
            return spawn;
        }
        int bestX = bestStart + bestLen / 2, bestRough = int.MaxValue;
        for (int c = bestStart + 30; c < bestStart + bestLen - 30; c++)
        {
            int lo = int.MaxValue, hi = int.MinValue;
            for (int x = c - 30; x <= c + 30; x++)
            {
                if (surf[x] <= 0) continue;
                lo = Math.Min(lo, surf[x]);
                hi = Math.Max(hi, surf[x]);
            }
            int rough = hi - lo + Math.Abs(c - (bestStart + bestLen / 2)) / 20;
            if (rough < bestRough) { bestRough = rough; bestX = c; }
        }
        // Bridge 17 tiles over the highest ground near the site: Deerclops (9.6 tiles tall) never
        // reaches it and its spike check (target within 200 px above its feet - 32) never passes.
        int top = surf[bestX];
        for (int x = bestX - 30; x <= bestX + 30; x++) if (surf[x] > 0) top = Math.Min(top, surf[x]);
        naturalBridge = Math.Min(top - 17, surf[bestX] - NaturalRise);
        Trace.Line($"[site] snow region x={bestStart}..{bestStart + bestLen} pick {bestX} floor {surf[bestX]} roughness {bestRough} bridge {naturalBridge}");
        return new Point(bestX, surf[bestX]);
    }
}
