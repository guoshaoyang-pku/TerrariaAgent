using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using BossPilot.Bosses.SlimesEye;
using BossPilot.Common;

namespace BossPilot.Bosses;

/// <summary>
/// Eye of Cthulhu (NPC 4, aiStyle 4), Classic. Kernel facts used (NPC.cs, aiStyle == 4):
/// it despawns by day (Main.IsItDay), so trials run at night; no biome condition.
/// Phase 1 (ai[0] = 0): hovers towards 200 px above the player (speed 5, accel 0.04 per axis)
/// for 600 ticks, spawning a Servant (speed 5, accel 0.03 homing) every 110 ticks while above
/// the player within 500 px; then three dashes (ai[1] 1 -> 2) aimed at the player's center at
/// 6 px/tick, 40 ticks straight, x0.98 per tick, 150 ticks each. Below 50% life it spins
/// (ai[0] 1, 2; 100 ticks each), then phase 2 (ai[0] = 3): hover 120 px above (speed 6, accel
/// 0.07) for 200 ticks, three 6.8 px/tick dashes, 40 ticks straight then x0.97, 130 ticks each.
/// Dashes never lead the player, so a dash aimed at a moving player misses by its run speed
/// times the flight time.
/// </summary>
public sealed class EyeOfCthulhu : PilotPlan
{
    public override string Boss => "eye_of_cthulhu";

    private const int Half = 90;
    private static readonly int[] Rows = [12, 24, 36];

    /// <summary>
    /// Kits (Classic, second boss):
    /// stage_default - Minishark + Meteor Shot (meteorite from a smashed orb; it pierces and
    ///   bounces through Servants), platinum armor, Hermes Boots, Cloud in a Bottle, Shiny Red
    ///   Balloon, Lucky Horseshoe, Anklet of the Wind; Ironskin/Swiftness/Regeneration; 300 life.
    /// min_preboss - Gold Bow + Flaming Arrows, iron armor, Hermes Boots; 160 life.
    /// stage_plus - post-EoW/BoC: Minishark + Meteor Shot, shadow armor, adds Wrath; 400 life.
    /// </summary>
    public override void GrantLoadout(Player p, ScenarioConfig s)
    {
        Main.hardMode = false;
        Kit.Clear(p);
        switch (s.Loadout)
        {
            case "min_preboss":
                Kit.Hotbar(p, (ItemID.GoldBow, 1), (ItemID.FlamingArrow, 999), (ItemID.LesserHealingPotion, 5));
                Kit.Armor(p, ItemID.IronHelmet, ItemID.IronChainmail, ItemID.IronGreaves);
                Kit.Accessories(p, ItemID.HermesBoots);
                LoadoutGiver.SetMaxStats(p, 160);
                break;
            case "stage_plus":
                Kit.Hotbar(p, (ItemID.Minishark, 1), (ItemID.MeteorShot, 999), (ItemID.HealingPotion, 5));
                Kit.Armor(p, ItemID.ShadowHelmet, ItemID.ShadowScalemail, ItemID.ShadowGreaves);
                Kit.Accessories(p, ItemID.HermesBoots, ItemID.CloudinaBottle, ItemID.ShinyRedBalloon, ItemID.LuckyHorseshoe, ItemID.AnkletoftheWind);
                Kit.Potions(p, s.MaxDurationSeconds, BuffID.Ironskin, BuffID.Swiftness, BuffID.Regeneration, BuffID.Wrath);
                LoadoutGiver.SetMaxStats(p, 400);
                break;
            default:
                Kit.Hotbar(p, (ItemID.Minishark, 1), (ItemID.MeteorShot, 999), (ItemID.LesserHealingPotion, 5));
                Kit.Armor(p, ItemID.PlatinumHelmet, ItemID.PlatinumChainmail, ItemID.PlatinumGreaves);
                Kit.Accessories(p, ItemID.HermesBoots, ItemID.CloudinaBottle, ItemID.ShinyRedBalloon, ItemID.LuckyHorseshoe, ItemID.AnkletoftheWind);
                Kit.Potions(p, s.MaxDurationSeconds, BuffID.Ironskin, BuffID.Swiftness, BuffID.Regeneration);
                LoadoutGiver.SetMaxStats(p, 300);
                break;
        }
    }

    public override Point ArenaSite(ScenarioConfig s) => ArenaKit.Site(Main.spawnTileX);

    public override void DecorateArena(Point site, ScenarioConfig s)
    {
        if (s.Arena == "flat_platforms") ArenaKit.Flat(site, Half, clear: 45, Rows, wallHeight: 45);
    }

    private readonly EyeSim sim = new();
    private float wallL, wallR;

    protected override void BeginAttempt(Player p)
    {
        Planner.Threats.SkipNpcTypes = [NPCID.EyeofCthulhu];
        Planner.Margin = 16f;
        int half = Ctx.Scenario.Arena == "flat_platforms" ? Half : 78;
        wallL = (Ctx.Site.X - half) * 16f;
        wallR = (Ctx.Site.X + half) * 16f;
    }

    protected override IBossSim CaptureBoss(NPC boss)
    {
        sim.Capture(boss);
        return sim;
    }

    /// <summary>Stay low (on the floor or first row) away from the walls; keep some distance from the Eye.</summary>
    protected override float Prefer(Vector2 c, Box boss, int k)
    {
        float cost = 0f;
        if (boss.Valid)
        {
            float d = Vector2.Distance(c, new Vector2(boss.X, boss.Y));
            if (d < 12 * 16f) cost += (12 * 16f - d) * 0.02f;
        }
        float edge = Math.Max(wallL + 12 * 16f - c.X, c.X - (wallR - 12 * 16f));
        if (edge > 0f) cost += edge * 0.05f;
        return cost;
    }

    /// <summary>Forward model of aiStyle 4 in Classic (see class summary).</summary>
    private sealed class EyeSim : IBossSim
    {
        private Vector2 c0, v0;
        private float ai0_, ai1_, ai2_, ai3_, hw, hh;
        private Vector2 c, v;
        private float a0, a1, a2, a3;

        public void Capture(NPC n)
        {
            c0 = n.Center; v0 = n.velocity;
            ai0_ = n.ai[0]; ai1_ = n.ai[1]; ai2_ = n.ai[2]; ai3_ = n.ai[3];
            hw = n.width * 0.5f; hh = n.height * 0.5f;
        }

        public Vector2 Now() => c0;

        public void Reset()
        {
            c = c0; v = v0; a0 = ai0_; a1 = ai1_; a2 = ai2_; a3 = ai3_;
        }

        public Box Step(Vector2 pc, float ptop, float pbottom)
        {
            if (a0 == 0f || a0 == 3f)
            {
                bool p2 = a0 == 3f;
                if (a1 == 0f)
                {
                    ThreatSet.HomingStep(ref v, c, pc - new Vector2(0f, p2 ? 120f : 200f), p2 ? 6f : 5f, p2 ? 0.07f : 0.04f);
                    if (++a2 >= (p2 ? 200f : 600f)) { a1 = 1f; a2 = 0f; a3 = 0f; }
                }
                else if (a1 == 1f)
                {
                    Vector2 d = pc - c;
                    float len = d.Length();
                    if (len > 0.01f) v = d * ((p2 ? 6.8f : 6f) / len);
                    a1 = 2f;
                }
                else if (a1 == 2f)
                {
                    a2++;
                    if (a2 >= 40f)
                    {
                        v *= p2 ? 0.97f : 0.98f;
                        if (Math.Abs(v.X) < 0.1f) v.X = 0f;
                        if (Math.Abs(v.Y) < 0.1f) v.Y = 0f;
                    }
                    if (a2 >= (p2 ? 130f : 150f))
                    {
                        a3++;
                        a2 = 0f;
                        if (a3 >= 3f) { a1 = 0f; a3 = 0f; }
                        else a1 = 1f;
                    }
                }
            }
            else
            {
                // Transformation spin: drifts to a stop.
                v *= 0.98f;
                if (++a1 >= 100f) { a0++; a1 = 0f; if (a0 == 3f) { a2 = 0f; a3 = 0f; } }
            }
            c += v;
            return new Box(c.X, c.Y, hw, hh);
        }
    }
}
