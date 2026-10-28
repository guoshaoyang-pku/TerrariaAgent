using System;
using BossPilot.Bosses.Endgame;
using BossPilot.Common;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace BossPilot.Bosses;

/// <summary>
/// Moon Lord: core 398 (invulnerable until both hands 397 and the head 396 have
/// lost their eyes), parts that become 80-damage husks spawning a True Eye (400)
/// when "killed" (NPC.checkDead), and a 600-tick core death animation before the
/// kill counts. Kernel facts used: the Celestial Sigil / pillar countdown ends in
/// NPC.SpawnOnPlayer(398), which places the core 150 px above the pilot (no
/// screen-ring search), so spawning it directly is equivalent; the core steers
/// towards pilot + (0,130) at 8 px/tick and teleports when farther than 2400 px;
/// natural spawns stop near it (NPC.SpawnNPC skips players within 4500 px).
/// The attack schedule is the fixed NPC.MoonLordAttacksArray (core ai[2] = 0).
/// </summary>
public sealed class MoonLord : EndgamePlan
{
    public override string Boss => "moon_lord";

    private const int ArenaWidth = 280, ArenaHeight = 130;
    private MoonFrame frame;
    private readonly System.Collections.Generic.HashSet<int> volleyOwners = [];

    // Loadout (post-Cultist and lunar pillars, Classic):
    // - stage_default: Vortex Beater + Chlorophyte Bullets (Luminite bullets need Moon Lord),
    //   Vortex armor, Ankh Shield, Sniper Scope, Destroyer + Avenger Emblem, Celestial Shell,
    //   Super Healing potions (fragment-crafted), Witch's Broom.
    // - stage_plus = post-Moon Lord: S.D.M.G. + Luminite Bullets, same armor and accessories.
    // - min_preboss = post-Golem: Chain Gun + Chlorophyte Bullets, Shroomite armor.
    public override void GrantLoadout(Player p, ScenarioConfig s)
    {
        Kit.Reset(p);
        Kit.Progress(Entry.ProgressionOrder);
        switch (s.Loadout)
        {
            case "stage_plus":
                Kit.Hotbar(p, 0, ItemID.SDMG);
                Kit.Hotbar(p, 54, ItemID.MoonlordBullet, 9999);
                Kit.Wear(p, ItemID.VortexHelmet, ItemID.VortexBreastplate, ItemID.VortexLeggings,
                    ItemID.AnkhShield, ItemID.SniperScope, ItemID.DestroyerEmblem, ItemID.CelestialShell, ItemID.AvengerEmblem);
                break;
            case "min_preboss":
                Kit.Hotbar(p, 0, ItemID.ChainGun);
                Kit.Hotbar(p, 54, ItemID.ChlorophyteBullet, 9999);
                Kit.Wear(p, ItemID.ShroomiteHelmet, ItemID.ShroomiteBreastplate, ItemID.ShroomiteLeggings,
                    ItemID.AnkhShield, ItemID.SniperScope, ItemID.DestroyerEmblem, ItemID.CelestialStone, ItemID.RangerEmblem);
                break;
            default:
                // Vortex Beater measured ~30% faster and with fewer hits than Phantasm + Holy Arrows.
                Kit.Hotbar(p, 0, ItemID.VortexBeater);
                Kit.Hotbar(p, 54, ItemID.ChlorophyteBullet, 9999);
                Kit.Wear(p, ItemID.VortexHelmet, ItemID.VortexBreastplate, ItemID.VortexLeggings,
                    ItemID.AnkhShield, ItemID.SniperScope, ItemID.DestroyerEmblem, ItemID.CelestialShell, ItemID.AvengerEmblem);
                break;
        }
        GiveHealing(p, super: true);
        Kit.Broom(p);
        Kit.Buffs(p, s, BuffID.Ironskin, BuffID.Regeneration, BuffID.Endurance, BuffID.Lifeforce,
            BuffID.Wrath, BuffID.Rage, BuffID.AmmoReservation);
        LoadoutGiver.SetMaxStats(p, life: 500, mana: 200);
        p.selectedItem = 0;
    }

    public override Point ArenaSite(ScenarioConfig s) => Arenas.Site(s.Biome);

    public override void DecorateArena(Point site, ScenarioConfig s)
    {
        // Open-sky scenarios keep just the floor; the forest one gets the standard platform field.
        if (s.Arena == "open_sky") Arenas.Platforms(site, ArenaWidth, ArenaHeight, rows: 0, gap: 0);
        else Arenas.Platforms(site, ArenaWidth, ArenaHeight, rows: 2, gap: 9);
        // In space the sky layer is only ~20 tiles under the world-top clamp: drop the floor
        // (keep a launch pad) so the fight can use the open air just below it.
        if (s.Biome == "space") Arenas.ClearFloor(site, ArenaWidth, keep: 8);
    }

    public override void SpawnBoss(Player p, ScenarioConfig s)
    {
        int i = NPC.NewNPC(new Terraria.DataStructures.EntitySource_BossSpawn(p), (int)p.Center.X, (int)p.Center.Y - 150, Entry.NpcId);
        if (i < Main.maxNPCs) Main.npc[i].target = p.whoAmI;
    }

    protected override void CollectHazards(Player p, FightContext ctx)
    {
        Vector2 me = p.Center;
        NPC core = FindNpc(NPCID.MoonLordCore);
        frame = core != null ? new MoonFrame(core) : null;
        volleyOwners.Clear();
        if (frame != null) Hazards.Add(frame);
        foreach (NPC n in Main.npc)
        {
            if (!n.active) continue;
            switch (n.type)
            {
                case NPCID.MoonLordFreeEye:
                    Hazards.Add(new TrueEyeBody(n));
                    AddEyeAttacks(n, me);
                    break;
                case NPCID.MoonLordHand:
                case NPCID.MoonLordHead:
                    if (frame == null) break;
                    if (n.ai[0] == -2f) Hazards.Add(new FramedBody(n, frame));
                    else AddPartAttacks(n, me);
                    break;
            }
        }
        foreach (Projectile pr in Main.projectile)
        {
            if (!pr.active || !pr.hostile) continue;
            switch (pr.type)
            {
                case ProjectileID.PhantasmalEye: Hazards.Add(new PhantasmalEye(pr)); break;
                case ProjectileID.PhantasmalDeathray: Hazards.Add(new Deathray(pr, frame)); break;
                case ProjectileID.PhantasmalSphere:
                    // Parked spheres ride in their owner's volley; orphans (owner dead or done) just drift.
                    if (pr.ai[0] == -1f) Hazards.Add(new LinearHazard(pr.Center, pr.velocity * 2f, new Vector2(20), pr.timeLeft, blastHalf: new Vector2(104)));
                    else if (!volleyOwners.Contains((int)pr.ai[1])) Hazards.Add(new LinearHazard(pr.Center, pr.velocity, new Vector2(20), pr.timeLeft, blastHalf: new Vector2(104)));
                    break;
                case ProjectileID.MoonLeech: break;
                default:
                    if (pr.damage > 0 && Vector2.Distance(pr.Center, me) < 1800) Hazards.Add(LinearHazard.Of(pr));
                    break;
            }
        }
    }

    private void AddPartAttacks(NPC part, Vector2 me)
    {
        int idx = MoonClock.PartIndex(part);
        MoonClock.Slot now = MoonClock.Part(idx, part.ai[1]);
        int horizon = Planner.Horizon + 2;
        // Bolts leave at elapsed duration-14 and -7 of sub-attack 3 (elapsed never reaches duration).
        var fire = new System.Collections.Generic.List<int>();
        for (int ahead = 1; ahead <= horizon; ahead++)
        {
            MoonClock.Slot s = MoonClock.PartAt(idx, part.ai[1], ahead);
            if (s.Attack == 3 && (s.Elapsed == s.Duration - 14 || s.Elapsed == s.Duration - 7)) fire.Add(ahead);
        }
        if (fire.Count > 0) Hazards.Add(new BoltVolley(part.Center, frame, me, fire.ToArray()));

        if (idx < 2)
        {
            // Spheres launch at elapsed 292 of sub-attack 2.
            // Spheres launch at elapsed 292 of sub-attack 2, aimed from 350 px above the hand. From 30 to
            // 210 the hand glides 400 px outwards (clamped 700 px from the core) and parks there.
            int launchIn = now.Attack == 2 && now.Elapsed < 292 ? 292 - now.Elapsed : int.MaxValue;
            float side = part.ai[2] == 0f ? -1f : 1f;
            NPC core = Main.npc[(int)part.ai[3]];
            Vector2 handAtLaunch = now.Elapsed >= 210 ? part.Center : core.Center + new Vector2(side * 700f, -170f);
            var volley = new SphereVolley(handAtLaunch - Vector2.UnitY * 350f, frame, launchIn);
            foreach (Projectile pr in Main.projectile)
                if (pr.active && pr.type == ProjectileID.PhantasmalSphere && pr.ai[0] != -1f && (int)pr.ai[1] == part.whoAmI) volley.Add(pr);
            if (volley.Any) { Hazards.Add(volley); volleyOwners.Add(part.whoAmI); }
        }
        else
        {
            // Head deathray: cast at elapsed 180 of sub-attack 1.
            for (int ahead = 1; ahead <= horizon; ahead++)
            {
                MoonClock.Slot s = MoonClock.PartAt(idx, part.ai[1], ahead);
                if (s.Attack == 1 && s.Elapsed == 180) { Hazards.Add(new CastDeathray(part, frame, ahead)); break; }
            }
        }
    }

    private void AddEyeAttacks(NPC eye, Vector2 me)
    {
        MoonClock.Slot now = MoonClock.Eye(eye.ai[1]);
        var fire = new System.Collections.Generic.List<int>();
        for (int ahead = 1; ahead <= Planner.Horizon + 2; ahead++)
        {
            MoonClock.Slot s = MoonClock.Eye((eye.ai[1] + ahead) % MoonClock.EyeCycle);
            if (s.Attack == 1 && (s.Elapsed == s.Duration - 14 || s.Elapsed == s.Duration - 7)) fire.Add(ahead);
        }
        if (fire.Count > 0) Hazards.Add(new BoltVolley(eye.Center, null, me, fire.ToArray()));
        if (now.Attack == 2 && now.Elapsed < 105)
        {
            // The eye brakes (x0.8 until 15), is kicked to (0,-7) at 75 and decays (x0.96); it aims
            // from where it is at 104, the spheres get the same kick at 75.
            Vector2 rise = Vector2.Zero;
            Vector2 v = now.Elapsed < 15 ? Vector2.Zero : eye.velocity;
            for (int e = now.Elapsed + 1; e < 105; e++)
            {
                if (e == 75) v = new Vector2(0f, -7f);
                if (e > 75) v.Y *= 0.96f;
                rise += v;
            }
            var volley = new SphereVolley(eye.Center + rise, null, 105 - now.Elapsed, now.Elapsed < 75 ? 75 - now.Elapsed : 0);
            foreach (Projectile pr in Main.projectile)
                if (pr.active && pr.type == ProjectileID.PhantasmalSphere && pr.ai[0] != -1f && (int)pr.ai[1] == eye.whoAmI) volley.Add(pr);
            if (volley.Any) { Hazards.Add(volley); volleyOwners.Add(eye.whoAmI); }
        }
        // Deathray: cast at elapsed 180 of sub-attack 4.
        for (int ahead = 1; ahead <= Planner.Horizon + 2; ahead++)
        {
            MoonClock.Slot s = MoonClock.Eye((eye.ai[1] + ahead) % MoonClock.EyeCycle);
            if (s.Attack == 4 && s.Elapsed == 180) { Hazards.Add(new CastDeathray(eye, null, ahead)); break; }
        }
    }

    protected override void ChooseGoal(Player p, FightContext ctx)
    {
        Point site = ctx.Site;
        // Space leaves only the band between the world-top clamp and a little below the sky-layer edge.
        Planner.Box = ctx.Scenario.Biome == "space"
            ? Arenas.FlightBox(site, ArenaWidth, 14, -24, 120)
            : Arenas.FlightBox(site, ArenaWidth, 14, 18, 90);
        NPC core = FindNpc(NPCID.MoonLordCore);
        Vector2 mid = Planner.Box.Center.ToVector2();
        // The space band is thin: sit in its lower part so a diagonal climb away from a ray has room.
        if (ctx.Scenario.Biome == "space") mid.Y = MathHelper.Lerp(Planner.Box.Top, Planner.Box.Bottom, 0.7f);
        if (core == null) { Planner.Goal = mid; return; }

        // The core steers to sit 130 px under the pilot, so altitude is ours to pick: hold the
        // middle of the box (room to dodge diving eyes both ways). Horizontally keep 220 px to
        // one side of the core: inside the hands' clamp boxes (330-700 px out, where they spawn
        // eyes, spheres and bolts point-blank) yet far enough that the core keeps chasing, so
        // the pilot never sits still for aimed shots. Swap sides near a wall.
        float side = Math.Sign(p.Center.X - core.Center.X);
        if (side == 0) side = 1;
        Vector2 goal = new(core.Center.X + side * 220f, mid.Y);
        if (goal.X < Planner.Box.Left + 200 || goal.X > Planner.Box.Right - 200) goal.X = core.Center.X - side * 220f;
        Planner.GoalWeight = 0.01f;
        if (DeathrayEscape(p, Planner.Box) is Vector2 flee)
        {
            // Rays are only escapable from the right place, which the horizon cannot see in time.
            goal = flee;
            Planner.GoalWeight = 0.04f;
        }
        Planner.Goal = Vector2.Clamp(goal, Planner.Box.TopLeft(), Planner.Box.BottomRight());
    }

    /// <summary>
    /// A Phantasmal Deathray turns at a fixed rate through a 120 degree sector, faster at the
    /// tip than the broom flies beyond ~770 px, and the planner horizon sees it only ~40
    /// degrees out. So flee early: aim the goal ahead of the pilot along the sweep, at a
    /// radius (250-450 px) where the pilot out-turns it. Covers coming casts too.
    /// </summary>
    private static Vector2? DeathrayEscape(Player p, Rectangle box)
    {
        Vector2? best = null;
        float soonest = float.MaxValue;
        foreach (Projectile pr in Main.projectile)
        {
            if (!pr.active || pr.type != ProjectileID.PhantasmalDeathray) continue;
            NPC owner = Main.npc[(int)pr.ai[1]];
            Vector2 o = owner.Center;
            float spin = pr.ai[0];
            float delta = MathHelper.WrapAngle((p.Center - o).ToRotation() - pr.velocity.ToRotation());
            float remaining = Math.Max(0, 180f - pr.localAI[0]) * Math.Abs(spin);
            if (Math.Sign(delta) != Math.Sign(spin) || Math.Abs(delta) > remaining + 0.2f) continue;
            float eta = Math.Abs(delta) / Math.Max(Math.Abs(spin), 1e-4f);
            if (eta < soonest) { soonest = eta; best = owner.type == NPCID.MoonLordHead ? Diagonal(p, spin, box) : Ahead(p, o, spin); }
        }
        if (best.HasValue) return best;

        // Not cast yet. The head follows the pilot anyway (core chase), so only its last 60 ticks
        // matter; a True Eye has stopped, so close in on it for its whole 180-tick charge-up.
        foreach (NPC n in Main.npc)
        {
            if (!n.active) continue;
            MoonClock.Slot s;
            if (n.type == NPCID.MoonLordHead && n.ai[0] != -2f) { s = MoonClock.Part(2, n.ai[1]); if (s.Attack != 1) continue; }
            else if (n.type == NPCID.MoonLordFreeEye) { s = MoonClock.Eye(n.ai[1]); if (s.Attack != 4) continue; }
            else continue;
            if (s.Elapsed >= 180 || (n.type == NPCID.MoonLordHead && s.Elapsed < 120)) continue;
            float eta = 180 - s.Elapsed + 90;
            if (eta >= soonest) continue;
            soonest = eta;
            float side = p.Center.X - n.Center.X < 0f ? 1f : -1f;
            float spin = side * MathHelper.TwoPi / 540f;
            best = n.type == NPCID.MoonLordHead ? Diagonal(p, spin, box) : Ahead(p, n.Center, spin);
        }
        return best;
    }

    /// <summary>
    /// The head rides on the core, which chases at Normalize(offset) * 8 px/tick, while the broom
    /// does 9 and 8 px/tick per axis (~12 on a diagonal): only a diagonal run gains on it. With
    /// the head above, the sweep is outrun by going outwards (away from its side) and up, or
    /// down when the keep-in box leaves no room above.
    /// </summary>
    private static Vector2 Diagonal(Player p, float spin, Rectangle box) =>
        p.Center + new Vector2(-Math.Sign(spin) * 900f, p.Center.Y - box.Top > 360f ? -900f : 900f);

    private static Vector2 Ahead(Player p, Vector2 origin, float spin)
    {
        Vector2 rel = p.Center - origin;
        float r = MathHelper.Clamp(rel.Length(), 250f, 450f);
        return origin + (rel.ToRotation() + Math.Sign(spin) * 0.9f).ToRotationVector2() * r;
    }

    protected override Vector2? AimPoint(Player p, FightContext ctx)
    {
        // Focus the head (removes the deathray), then the weaker hand, then the core.
        NPC best = null;
        float bestScore = float.MaxValue;
        foreach (NPC n in Main.npc)
        {
            if (!n.active || n.dontTakeDamage) continue;
            float score = n.type switch
            {
                NPCID.MoonLordHead => 0f,
                NPCID.MoonLordHand => 1f + n.life / (float)n.lifeMax,
                NPCID.MoonLordCore => 3f,
                _ => float.MaxValue,
            };
            if (score < bestScore) { bestScore = score; best = n; }
        }
        if (best == null)
        {
            // Nothing vulnerable: pre-aim the head's eye so the first window is not wasted.
            NPC head = FindNpc(NPCID.MoonLordHead);
            return head != null && head.ai[0] != -2f ? head.Center : FindNpc(NPCID.MoonLordCore)?.Center;
        }
        return Lead(p, best.Center, best.velocity);
    }
}
