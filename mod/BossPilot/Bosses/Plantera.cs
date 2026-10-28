using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using BossPilot.Common;
using BossPilot.Bosses.JungleTempleOcean;

namespace BossPilot.Bosses;

/// <summary>
/// Plantera (Classic, 30000 HP): fought in a 190x60 platform hall dug into the real underground
/// jungle (enclosed = stone shell like ArenaBuilder's box, natural = carved into the mud with
/// natural walls), below the surface line so ZoneJungle + depth keep it from enraging
/// (NPC.cs aiStyle 51). The hall is long enough to step past the 500 px hook leash.
/// Control: sampling MPC (Pilot.cs) against a kernel model of Plantera's chase (hook
/// centroid + 500 px leash, speed 2.5/5/7, accel 0.025/0.05), rigid tentacle ring, spore
/// homing, bouncing thorn balls and straight seeds. A route layer (Macro.cs) picks waypoint
/// routes along the floor, platforms and ceiling lane by simulating the same chase model for
/// 150 ticks, so the pilot circles past Plantera instead of being pinned at a wall.
/// </summary>
public sealed class Plantera : PilotPlan
{
    public override string Boss => "plantera";

    private static readonly HashSet<int> Npcs = [NPCID.Plantera, NPCID.PlanterasHook, NPCID.PlanterasTentacle, NPCID.Spore];
    protected override ISet<int> ModelledNpcs => Npcs;
    private static readonly HashSet<int> Projs = [ProjectileID.ThornBall];
    protected override ISet<int> ModelledProjectiles => Projs;

    // Loadouts. Plantera needs all three mechs: Hallowed gear, Megashark and Chlorophyte
    // (minable after the mechs) are the realistic speedrun kit; stage_plus borrows the
    // post-Plantera ranger kit (Venus Magnum, Shroomite), min_preboss is the bare minimum.
    private static Kit KitFor(string tier) => tier switch
    {
        "min_preboss" => new Kit(ItemID.Megashark, ItemID.MusketBall,
            [ItemID.AdamantiteMask, ItemID.AdamantiteBreastplate, ItemID.AdamantiteLeggings],
            [ItemID.HermesBoots, ItemID.AngelWings, ItemID.RangerEmblem],
            [BuffID.Ironskin, BuffID.Swiftness, BuffID.Regeneration], 400),
        "stage_plus" => new Kit(ItemID.VenusMagnum, ItemID.ChlorophyteBullet,
            [ItemID.ShroomiteMask, ItemID.ShroomiteBreastplate, ItemID.ShroomiteLeggings],
            [ItemID.FrostsparkBoots, ItemID.BoneWings, ItemID.SniperScope, ItemID.AvengerEmblem, ItemID.RangerEmblem],
            Kits.StandardBuffs, 540),
        _ => new Kit(ItemID.Megashark, ItemID.ChlorophyteBullet,
            [ItemID.HallowedHelmet, ItemID.HallowedPlateMail, ItemID.HallowedGreaves],
            [ItemID.FrostsparkBoots, ItemID.FlameWings, ItemID.AvengerEmblem, ItemID.RangerEmblem, ItemID.CharmofMyths],
            Kits.StandardBuffs, 500),
    };

    public override void GrantLoadout(Player p, ScenarioConfig s)
    {
        Kits.Hardmode(plantDown: false, golemDown: false);
        Kits.Apply(p, KitFor(s.Loadout), s);
    }

    public override Point ArenaSite(ScenarioConfig s)
    {
        Recon.DumpOnce();
        List<Point> sites = Arenas.JungleSites();
        return s.Arena == "natural" ? sites[1] : sites[0];
    }

    /// <summary>Arena half width / height in tiles: a long jungle hall, so the pilot can open the
    /// distance past the 500 px hook leash and Plantera stalls until its hooks re-anchor.</summary>
    private const int HalfWidth = 95, Height = 60;
    private static readonly int[] PlatformRows = [12, 24, 36, 48];

    public override void DecorateArena(Point site, ScenarioConfig s)
    {
        int x0 = site.X - HalfWidth, x1 = site.X + HalfWidth;
        // Widen ArenaBuilder's room (enclosed) or dig the hall into the mud (natural).
        Arenas.Carve(site, 2 * HalfWidth + 1, Height, TileID.Mud, TileID.JungleGrass, 4);
        if (s.Arena != "natural")
        {
            // Enclosed: a full stone shell around the hall, like ArenaBuilder.Box.
            for (int x = x0 - 1; x <= x1 + 1; x++)
            for (int y = site.Y - Height - 1; y <= site.Y + 10; y++)
            {
                bool edge = x == x0 - 1 || x == x1 + 1 || y == site.Y - Height - 1 || y == site.Y + 10;
                if (edge && WorldGen.InWorld(x, y, 2)) Main.tile[x, y].ResetToType(TileID.Stone);
            }
        }
        var rows = new List<int>();
        foreach (int r in PlatformRows) rows.Add(site.Y - r);
        Arenas.PlatformRows(x0 + 2, x1 - 2, rows);
        Arena = ArenaInfo.FromTiles(x0, x1, site.Y - Height, site.Y);
        foreach (int r in rows) Arena.Platforms.Add(r * 16f);
    }

    public override void SpawnBoss(Player p, ScenarioConfig s)
    {
        Biomes.Refresh(p);
        int i = NPC.NewNPC(new Terraria.DataStructures.EntitySource_BossSpawn(p), (int)p.Center.X + 30 * 16, (int)p.Center.Y - 16 * 16, NPCID.Plantera);
        if (i < Main.maxNPCs) Main.npc[i].target = p.whoAmI;
    }

    private NPC plant;
    private readonly RoutePlanner routes = new();
    private readonly List<Vector2> tentacles = [];
    private Vector2 carrot;

    protected override void OnAttemptStart(Player p) => routes.Best = null;

    /// <summary>Body radius + pilot, or the tentacle orbit (200 px, +100 below 25%, +100 below 10%) + tentacle + pilot.</summary>
    private static float DangerRadius(float lifeFrac) =>
        lifeFrac > 0.5f ? 90f : lifeFrac > 0.25f ? 265f : lifeFrac > 0.1f ? 365f : 465f;

    protected override void BuildThreats(Player p)
    {
        plant = FindNpc(NPCID.Plantera);
        if (plant == null) return;
        bool enraged = !p.ZoneJungle || p.position.Y < Main.worldSurface * 16.0 || p.position.Y > Main.UnderworldLayer * 16;
        PlanteraModel model = new(plant, enraged);
        FuncThreat body = Func("plantera", (pc, bx) => model.Predict(pc, bx), 6f, 0.25f);
        Planner.Boss = body;
        float lifeFrac = plant.life / (float)plant.lifeMax;
        float danger = DangerRadius(lifeFrac);

        // Route clearance uses the real tentacle ring (rigid offsets) rather than a full disk.
        tentacles.Clear();
        foreach (NPC n in Main.npc)
            if (n.active && n.type == NPCID.PlanterasTentacle) tentacles.Add(n.Center - plant.Center);
        float bodyR = 80f;
        if (Ctx.Tick % 12 == 1 || routes.Best == null)
        {
            // Waypoints: every 12 tiles along the floor, each platform row and the ceiling lane.
            var wps = new List<Vector2>();
            for (float x = Arena.Left + 160; x <= Arena.Right - 160; x += 192)
            {
                wps.Add(new Vector2(x, Arena.Floor - 22));
                foreach (float py in Arena.Platforms) wps.Add(new Vector2(x, py - 22));
            }
            routes.BuildRoutes(p.Center, wps, Arena.Top + 140, Arena.Floor - 22);
            routes.Choose(p.Center, model,
                (pc, bc) =>
                {
                    float c = Vector2.Distance(pc, bc) - bodyR;
                    foreach (Vector2 off in tentacles) c = Math.Min(c, Vector2.Distance(pc, bc + off) - 70f);
                    return c;
                },
                (pc, bc) => 0.6f * Math.Min(Vector2.Distance(pc, bc), 1000f) - 3f * BoundsCost(pc, 160f, 60f));
        }
        carrot = routes.Carrot(p.Center, 45);

        foreach (Projectile pr in Main.projectile)
            if (pr.active && pr.type == ProjectileID.ThornBall) Ball(pr, 4f, 0.2f);

        foreach (NPC n in Main.npc)
        {
            if (!n.active) continue;
            if (n.type == NPCID.PlanterasHook)
            {
                // Hooks fly straight to their tile target (ai[0], ai[1]) and stay there.
                Vector2 c0 = n.Center;
                Vector2 target = n.ai[0] > 0 ? new(n.ai[0] * 16f - 8f, n.ai[1] * 16f - 8f) : c0;
                float speed = model.HookSpeed;
                float hw = n.width / 2f, hh = n.height / 2f;
                Func("hook", (pc, bx) =>
                {
                    Vector2 c = c0;
                    for (int k = 0; k < Planner.H; k++)
                    {
                        Vector2 d = target - c;
                        float len = d.Length();
                        c = len < 12f + speed ? target : c + d * (speed / len);
                        bx[k] = Box.Around(c, hw, hh);
                    }
                }, 8f, 0.3f, reactive: false);
            }
            else if (n.type == NPCID.PlanterasTentacle)
            {
                // Tentacles orbit Plantera at 200-400 px; treat the ring offset as rigid.
                Vector2 off = n.Center - plant.Center;
                float hw = n.width / 2f, hh = n.height / 2f;
                Func("tentacle", (pc, bx) =>
                {
                    Box[] pb = body.Boxes;
                    for (int k = 0; k < Planner.H; k++)
                        bx[k] = Box.Around(pb[k].Center + off, hw, hh);
                }, 12f, 0.6f);
            }
            else if (n.type == NPCID.Spore)
            {
                NPC sp = n;
                Func("spore", (pc, bx) => PredictSpore(sp, pc, bx), 6f, 0.25f);
            }
        }
        Planner.Objective = (k, st, boss) => Objective(st, boss, danger);
    }

    private float Objective(PilotState s, Box boss, float danger)
    {
        Vector2 c = s.Center;
        float cost = BoundsCost(c, 48f, 112f) + Vector2.Distance(c, carrot) / 24f + WingReserveCost(s);
        if (!boss.Valid) return cost;
        // Tentacles are explicit threats; the ring here only keeps a margin around the body.
        float d = Vector2.Distance(c, boss.Center) - Math.Min(danger, 260f);
        if (d < 60f) cost += (60f - d) / 20f;
        return cost;
    }

    private static void PredictSpore(NPC n, Vector2[] pc, Box[] bx)
    {
        // NPC.cs aiStyle 50 (Classic): drift down 0.02/tick (max 1), steer 0.1/tick toward the target's x.
        Vector2 pos = n.position, v = n.velocity;
        for (int k = 0; k < Planner.H; k++)
        {
            Vector2 tp = pc[k] - new Vector2(10, 21);
            v.Y += 0.02f;
            if (v.Y < 0f && tp.Y > pos.Y + 100f) v.Y *= 0.95f;
            if (v.Y > 1f) v.Y = 1f;
            if (pos.X + n.width < tp.X) { if (v.X < 0f) v.X *= 0.98f; v.X += 0.1f; }
            else if (pos.X > tp.X + 20) { if (v.X > 0f) v.X *= 0.98f; v.X -= 0.1f; }
            if (v.X > 5f || v.X < -5f) v.X *= 0.97f;
            pos += v;
            bx[k] = new Box(pos.X, pos.Y, pos.X + n.width, pos.Y + n.height);
        }
    }

    protected override Vector2? AimTarget(Player p, out Vector2 v)
    {
        v = Vector2.Zero;
        if (plant == null) return null;
        // Spores (1 HP, 70 contact damage) drifting close get shot first.
        NPC spore = null;
        float best = 220f;
        foreach (NPC n in Main.npc)
            if (n.active && n.type == NPCID.Spore && Vector2.Distance(n.Center, p.Center) < best)
            {
                best = Vector2.Distance(n.Center, p.Center);
                spore = n;
            }
        NPC t = spore ?? plant;
        v = t.velocity;
        return t.Center;
    }

    /// <summary>Kernel model of Plantera's body motion (NPC.cs aiStyle 51, Classic).</summary>
    private sealed class PlanteraModel : IBossSim
    {
        private readonly NPC n;
        private readonly Vector2[] hookStart = new Vector2[3], hookTarget = new Vector2[3], hp = new Vector2[3];
        private readonly int hooks;
        private readonly float speed, accel, leash;
        public readonly float HookSpeed;
        private Vector2 c, v;

        public PlanteraModel(NPC plant, bool enraged)
        {
            n = plant;
            speed = 2.5f; accel = 0.025f;
            if (n.life < n.lifeMax / 2) { speed = 5f; accel = 0.05f; }
            if (n.life < n.lifeMax / 4) speed = 7f;
            if (enraged) { speed += 8f; accel = 0.15f; }
            leash = enraged ? 850f : 500f;
            HookSpeed = n.life < n.lifeMax / 4 ? 10f : n.life < n.lifeMax / 2 ? 8f : 6f;
            if (enraged) HookSpeed *= 2f;
            foreach (NPC h in Main.npc)
            {
                if (!h.active || h.aiStyle != 52 || hooks >= 3) continue;
                hookStart[hooks] = h.Center;
                hookTarget[hooks] = h.ai[0] > 0 ? new Vector2(h.ai[0] * 16f - 8f, h.ai[1] * 16f - 8f) : h.Center;
                hooks++;
            }
            Reset();
        }

        public void Reset()
        {
            c = n.Center;
            v = n.velocity;
            for (int i = 0; i < hooks; i++) hp[i] = hookStart[i];
        }

        public Vector2 Step(Vector2 pilot)
        {
            Vector2 centroid = Vector2.Zero;
            for (int i = 0; i < hooks; i++)
            {
                Vector2 d = hookTarget[i] - hp[i];
                float len = d.Length();
                hp[i] = len < 12f + HookSpeed ? hookTarget[i] : hp[i] + d * (HookSpeed / len);
                centroid += hp[i];
            }
            centroid = hooks > 0 ? centroid / hooks : c;
            Vector2 toP = pilot - centroid;
            float l = toP.Length();
            if (l >= leash) toP *= leash / l;
            Vector2 want = centroid + toP - c;
            float dist = want.Length();
            if (dist < speed) want = v;
            else want *= speed / dist;
            v.X = Approach(v.X, want.X, accel);
            v.Y = Approach(v.Y, want.Y, accel);
            c += v;
            return c;
        }

        public void Predict(Vector2[] pc, Box[] bx)
        {
            Reset();
            float hw = n.width / 2f, hh = n.height / 2f;
            for (int k = 0; k < Planner.H; k++) bx[k] = Box.Around(Step(pc[k]), hw, hh);
        }

        private static float Approach(float v, float want, float a)
        {
            if (v < want) { v += a; if (v < 0f && want > 0f) v += a * 2f; }
            else if (v > want) { v -= a; if (v > 0f && want < 0f) v -= a * 2f; }
            return v;
        }
    }
}
