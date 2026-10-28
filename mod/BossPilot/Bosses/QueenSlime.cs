using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using BossPilot.Bosses.SlimesEye;
using BossPilot.Common;

namespace BossPilot.Bosses;

/// <summary>
/// Queen Slime (NPC 657, AI_121_QueenSlime), Classic: 18000 life, 26 defense. Kernel facts used:
/// no biome or time condition (despawns only when the player is dead or 500 tiles away), so the
/// Hallow arena is for authenticity and its own spawns. Phase 1 (life > 50%): after 60 grounded
/// ticks picks at random a 4-hop jump series (vy -8, -8, -6, -13 like King Slime), a ground pound
/// (rises at 20 px/tick towards 384 px above the player, hovers 30 ticks, falls; landing spawns
/// QueenSlimeSmash, a square growing from 5 to 30 tiles over 9 ticks around her bottom) or a
/// 50+10 tick charge then 6 gel balls (speed 9, gravity 0.15 after 5 ticks, one bounce) over the
/// upper half circle. Phase 2: flies to 250 px above the player (speed 12, accel 0.085) and every
/// 120 ticks pounds (only if the player is below her and within 250 px horizontally; 10-tick
/// hover then a fall capped at 10 px/tick by NPC gravity) or bursts 10 gels in a full circle.
/// Losing 2% (1.5% in phase 2) of her life spawns 1-2 Crystal/Bouncy/Heavenly slimes.
/// </summary>
public sealed class QueenSlime : PilotPlan
{
    public override string Boss => "queen_slime";

    private const int Half = 90;
    private static readonly int[] Rows = [12, 24, 36];

    /// <summary>
    /// Kits (Classic, first hardmode boss, right after the Wall of Flesh):
    /// stage_default - Titanium Repeater + Jester's Arrows (Merchant; infinite pierce goes through
    ///   her minions), titanium ranged armor, Angel Wings (souls of flight from Wyverns),
    ///   Lightning Boots, Ranger Emblem (WoF), Cloud in a Balloon, Magic Quiver (hardmode Skeleton
    ///   Archers);
    ///   Archery/Wrath/Rage/Ironskin/Swiftness/Regeneration/Endurance; 400 life, healing potions.
    /// min_preboss - Cobalt Repeater + Jester's Arrows, cobalt armor, Hermes Boots + Cloud; 400 life.
    /// stage_plus - post-mech: Megashark + Crystal Bullets, hallowed armor, same accessories; 500 life.
    /// </summary>
    public override void GrantLoadout(Player p, ScenarioConfig s)
    {
        Main.hardMode = true;
        Kit.Clear(p);
        switch (s.Loadout)
        {
            case "min_preboss":
                Kit.Hotbar(p, (ItemID.CobaltRepeater, 1), (ItemID.JestersArrow, 999), (ItemID.HealingPotion, 10));
                Kit.Armor(p, ItemID.CobaltMask, ItemID.CobaltBreastplate, ItemID.CobaltLeggings);
                Kit.Accessories(p, ItemID.HermesBoots, ItemID.CloudinaBottle);
                LoadoutGiver.SetMaxStats(p, 400, 100);
                break;
            case "stage_plus":
                Kit.Hotbar(p, (ItemID.Megashark, 1), (ItemID.CrystalBullet, 999), (ItemID.GreaterHealingPotion, 10));
                Kit.Armor(p, ItemID.HallowedHelmet, ItemID.HallowedPlateMail, ItemID.HallowedGreaves);
                Kit.Accessories(p, ItemID.AngelWings, ItemID.LightningBoots, ItemID.RangerEmblem, ItemID.CloudinaBalloon, ItemID.CrossNecklace);
                Kit.Potions(p, s.MaxDurationSeconds, BuffID.Ironskin, BuffID.Swiftness, BuffID.Regeneration, BuffID.Endurance, BuffID.Wrath);
                LoadoutGiver.SetMaxStats(p, 500, 100);
                break;
            default:
                Kit.Hotbar(p, (ItemID.TitaniumRepeater, 1), (ItemID.JestersArrow, 999), (ItemID.HealingPotion, 10));
                Kit.Armor(p, ItemID.TitaniumHelmet, ItemID.TitaniumBreastplate, ItemID.TitaniumLeggings);
                Kit.Accessories(p, ItemID.AngelWings, ItemID.LightningBoots, ItemID.RangerEmblem, ItemID.CloudinaBalloon, ItemID.MagicQuiver);
                Kit.Potions(p, s.MaxDurationSeconds, BuffID.Archery, BuffID.Wrath, BuffID.Rage, BuffID.Ironskin, BuffID.Swiftness, BuffID.Regeneration, BuffID.Endurance);
                LoadoutGiver.SetMaxStats(p, 400, 100);
                break;
        }
    }

    public override Point ArenaSite(ScenarioConfig s) => ArenaKit.Site(Main.spawnTileX);

    /// <summary>Hallow floor (pearlstone, 6 deep across the whole arena) under the standard rows.</summary>
    public override void DecorateArena(Point site, ScenarioConfig s)
    {
        ushort floor = s.Biome == "hallow" ? TileID.Pearlstone : TileID.Stone;
        if (s.Arena == "flat_platforms") ArenaKit.Flat(site, Half, clear: 50, Rows, wallHeight: 50, floor);
    }

    private readonly QsSim sim = new();
    private int side;
    private float wallL, wallR;

    protected override void BeginAttempt(Player p)
    {
        Planner.Threats.SkipNpcTypes = [NPCID.QueenSlimeBoss];
        Planner.Margin = 16f;
        side = 0;
        int half = Ctx.Scenario.Arena == "flat_platforms" ? Half : 78;
        wallL = (Ctx.Site.X - half) * 16f;
        wallR = (Ctx.Site.X + half) * 16f;
    }

    protected override IBossSim CaptureBoss(NPC boss)
    {
        sim.Capture(boss);
        int now = P.Center.X < boss.Center.X ? -1 : 1;
        if (side == 0) side = now;
        float room = now < 0 ? P.Center.X - wallL : wallR - P.Center.X;
        if (side == now && room < 30 * 16f) side = -now;
        return sim;
    }

    /// <summary>Phase 1: King Slime-style side keeping. Phase 2: stay off the walls and out from under her.</summary>
    protected override float Prefer(Vector2 c, Box boss, int k)
    {
        float cost = 0f;
        if (boss.Valid)
        {
            float dx = c.X - boss.X;
            if (!sim.Phase2)
            {
                if (dx * side > 0f)
                {
                    float a = Math.Abs(dx);
                    if (a < 16 * 16f) cost += (16 * 16f - a) * 0.03f;
                    else if (a > 36 * 16f) cost += (a - 36 * 16f) * 0.01f;
                }
                else cost += 14f + Math.Abs(dx) * 0.02f;
            }
            else
            {
                float a = Math.Abs(dx);
                if (a < 10 * 16f) cost += (10 * 16f - a) * 0.01f;
            }
        }
        float edge = Math.Max(wallL + 12 * 16f - c.X, c.X - (wallR - 12 * 16f));
        if (edge > 0f) cost += edge * 0.05f;
        return cost;
    }

    /// <summary>Shoot Heavenly Slimes (fast flyers) first, then any minion in shooting range, else the Queen.</summary>
    /// <remarks>
    /// Minions never despawn and every 2% (1.5%) of her life adds 1-2, so ignoring them ends in
    /// dozens of Crystal/Bouncy Slimes lobbing spikes (they shoot within 500 px with line of sight).
    /// Killing every minion within that range first costs ~25% more fight time but keeps the
    /// projectile count low enough to dodge.
    /// </remarks>
    protected override Entity ChooseTarget(Player p, NPC boss) =>
        NearestThreat(p, 25 * 16f, n => n.type == NPCID.QueenSlimeMinionPurple)
        ?? NearestThreat(p, 31 * 16f) ?? (Entity)boss;

    /// <summary>Forward model of AI_121_QueenSlime (see class summary); random choices assume the most dangerous option.</summary>
    private sealed class QsSim : IBossSim
    {
        private const int MaxGels = 10;

        private Vector2 bottom0, vel0, tp;
        private float a0_, a1_, a2_, a3_, lifeFrac, h0;
        private int dir0;
        private bool ground0;
        public bool Phase2 { get; private set; }

        private Vector2 bottom, vel;
        private float a0, a1, a2, a3;
        private int dir;
        private bool ground, gone;
        private float scale;

        private readonly Vector2[] gelPos = new Vector2[MaxGels], gelVel = new Vector2[MaxGels];
        private readonly int[] gelAge = new int[MaxGels], gelBounce = new int[MaxGels];
        private int gels;
        private Vector2 smashAt;
        private int smashAge = -1;

        public void Capture(NPC n)
        {
            bottom0 = n.Bottom; vel0 = n.velocity; h0 = n.height;
            a0_ = n.ai[0]; a1_ = n.ai[1]; a2_ = n.ai[2]; a3_ = n.ai[3];
            tp = new Vector2(n.localAI[1], n.localAI[2]);
            dir0 = n.direction == 0 ? 1 : n.direction;
            ground0 = n.velocity.Y == 0f;
            lifeFrac = (float)n.life / n.lifeMax;
            Phase2 = n.life <= n.lifeMax / 2;
        }

        public Vector2 Now() => bottom0 - new Vector2(0f, h0 * 0.5f);

        public void Reset()
        {
            bottom = bottom0; vel = vel0; a0 = a0_; a1 = a1_; a2 = a2_; a3 = a3_;
            dir = dir0; ground = ground0; gone = false; gels = 0; smashAge = -1;
            scale = 1f;
        }

        private float W => 114f * scale;
        private float Hgt => 100f * scale;
        private Vector2 Center => bottom - new Vector2(0f, Hgt * 0.5f);

        public Box Step(Vector2 pc, float ptop, float pbottom)
        {
            StepGels();
            if (smashAge >= 0 && ++smashAge > 9) smashAge = -1;
            if (gone) return Box.None;

            bool fly = false;      // noGravity + noTileCollide this tick
            float maxFall = 10f;
            scale = 1f;
            switch ((int)a0)
            {
                case 0:
                    if (Phase2)
                    {
                        Fly(pc);
                        fly = true;
                        if (++a1 > 120f)
                        {
                            a1 = 0f;
                            bool slamOk = !(pbottom < bottom.Y) && Math.Abs(pc.X - bottom.X) <= 250f;
                            if (slamOk) { a0 = 4f; a2 = 1f; } else { a0 = 5f; a2 = 0f; }
                        }
                    }
                    else
                    {
                        if (ground) { vel.X *= 0.8f; if (Math.Abs(vel.X) < 0.1f) vel.X = 0f; }
                        if (Math.Abs(bottom.Y - Hgt - pbottom) > 320f) a3 += 1.5f; else a3 = Math.Max(0f, a3 - 1f);
                        if (a3 >= 300f && ground) { gone = true; return Box.None; }
                        if (ground && ++a1 > 60f) { a1 = 0f; a0 = 3f; } // random pick: assume the hop series
                    }
                    break;
                case 1:
                    a1++;
                    scale = 0.5f + 0.5f * MathHelper.Clamp(a1 / 30f, 0f, 1f);
                    if (a1 >= 30f) { a0 = 0f; a1 = 0f; }
                    break;
                case 2:
                    a1++;
                    scale = 0.5f + 0.5f * MathHelper.Clamp((60f - a1) / 60f, 0f, 1f);
                    if (a1 >= 60f) { bottom = tp; vel = Vector2.Zero; a0 = 1f; a1 = 0f; ground = true; return Box.None; }
                    break;
                case 3:
                    if (ground)
                    {
                        vel.X *= 0.8f;
                        if (Math.Abs(vel.X) < 0.1f) vel.X = 0f;
                        a1 += 4f + (lifeFrac < 0.66f ? 4f : 0f) + (lifeFrac < 0.33f ? 4f : 0f);
                        if (a1 >= 0f)
                        {
                            dir = pc.X < bottom.X ? -1 : 1;
                            if (a2 == 3f) { vel.Y = -13f; vel.X += 3.5f * dir; a1 = 0f; a2 = 0f; a0 = 0f; }
                            else if (a2 == 2f) { vel.Y = -6f; vel.X += 4.5f * dir; a1 = -40f; a2 = 3f; }
                            else { vel.Y = -8f; vel.X += 4f * dir; a1 = -40f; a2++; }
                            ground = false;
                        }
                    }
                    else if ((dir == 1 && vel.X < 3f) || (dir == -1 && vel.X > -3f))
                    {
                        if ((dir == -1 && vel.X < 0.1f) || (dir == 1 && vel.X > -0.1f)) vel.X += 0.2f * dir;
                        else vel.X *= 0.93f;
                    }
                    break;
                case 4:
                    if (a2 == 1f)
                    {
                        int hover = Phase2 ? 10 : 30;
                        vel.X *= 0.8f;
                        a1++;
                        if (a1 >= hover)
                        {
                            if (Phase2 && a1 > hover + 120) { a0 = 0f; a1 = 0f; a2 = 0f; vel.Y *= 0.8f; break; }
                            vel.Y = Math.Min(vel.Y + 1f, 14f);
                            if (vel.Y == 0f) vel.Y = 0.01f;
                        }
                        else vel.Y *= 0.8f;
                        ground = false;
                    }
                    else
                    {
                        fly = true;
                        a1++;
                        if (a1 >= 30f)
                        {
                            if (a1 >= 60f) { a1 = 0f; a2 = 1f; vel.Y = -3f; }
                            Vector2 target = pc - new Vector2(0f, 384f);
                            if (vel.Y == 0f)
                            {
                                Vector2 d = target - Center;
                                float len = d.Length();
                                vel = len > 0.01f ? d * (20f / len) : Vector2.Zero;
                            }
                            else vel.Y *= 0.95f;
                        }
                    }
                    break;
                case 5:
                    fly = true;
                    if (a2 == 1f)
                    {
                        if (++a1 >= 10f)
                        {
                            int n = Phase2 ? 10 : 6;
                            for (int j = 0; j < n && gels < MaxGels; j++)
                            {
                                gelPos[gels] = Center;
                                gelVel[gels] = new Vector2(9f, 0f).RotatedBy(-j * MathHelper.TwoPi / 10f);
                                gelAge[gels] = 0;
                                gelBounce[gels] = 1;
                                gels++;
                            }
                            a0 = 0f; a1 = 0f; a2 = 0f;
                        }
                    }
                    else
                    {
                        if (++a1 >= 50f) { a1 = 0f; a2 = 1f; }
                        if (Phase2) Fly(pc);
                    }
                    break;
            }

            if (fly)
            {
                bottom += vel;
                ground = false;
            }
            else
            {
                vel.Y = Math.Min(vel.Y + 0.3f, maxFall);
                if (ground) vel.Y = 0f;
                float nx = bottom.X + vel.X;
                int edge = (int)((nx + Math.Sign(vel.X) * W * 0.5f) / 16f);
                if (vel.X != 0f && Tiles.Solid(edge, (int)((bottom.Y - Hgt * 0.5f) / 16f))) vel.X = 0f;
                else bottom.X = nx;
                float ny = bottom.Y + vel.Y;
                if (vel.Y > 0f)
                {
                    bool plats = !(ptop > bottom.Y);
                    float land = Tiles.Land(bottom.X - W * 0.5f, bottom.X + W * 0.5f, bottom.Y, ny, plats);
                    if (!float.IsNaN(land))
                    {
                        ny = land;
                        vel.Y = 0f;
                        if (!ground && a0 == 4f && a2 == 1f)
                        {
                            a0 = 0f; a1 = 0f; a2 = 0f;
                            smashAt = new Vector2(bottom.X, land);
                            smashAge = 0;
                        }
                        ground = true;
                    }
                    else ground = false;
                }
                bottom.Y = ny;
            }
            return new Box(bottom.X, bottom.Y - Hgt * 0.5f, W * 0.5f, Hgt * 0.5f);
        }

        /// <summary>AI_121_QueenSlime_FlyMovement + SimpleFlyMovement, assuming line of sight.</summary>
        private void Fly(Vector2 pc)
        {
            Vector2 c = Center;
            Vector2 d = pc + new Vector2(0f, -250f) - c;
            float len = d.Length();
            if (Math.Abs(d.X) < 40f) d.X = vel.X;
            float acc = 0.085f;
            if (len > 100f && ((vel.X < -10f && d.X > 0f) || (vel.X > 10f && d.X < 0f))) acc *= 2f;
            if (len < 40f) d = vel;
            else
            {
                float l2 = d.Length();
                if (l2 > 0.01f) d *= (len < 80f ? 12f * 0.65f : 12f) / l2;
            }
            if (vel.X < d.X) { vel.X += acc; if (vel.X < 0f && d.X > 0f) vel.X += acc; }
            else if (vel.X > d.X) { vel.X -= acc; if (vel.X > 0f && d.X < 0f) vel.X -= acc; }
            if (vel.Y < d.Y) { vel.Y += acc; if (vel.Y < 0f && d.Y > 0f) vel.Y += acc; }
            else if (vel.Y > d.Y) { vel.Y -= acc; if (vel.Y > 0f && d.Y < 0f) vel.Y -= acc; }
        }

        private void StepGels()
        {
            for (int i = 0; i < gels; i++)
            {
                if (gelBounce[i] < 0) continue;
                if (++gelAge[i] >= 5) gelVel[i].Y = Math.Min(gelVel[i].Y + 0.15f, 16f);
                Vector2 next = gelPos[i] + gelVel[i];
                if (Tiles.Solid((int)(next.X / 16f), (int)((next.Y + Math.Sign(gelVel[i].Y) * 6f) / 16f)))
                {
                    if (gelBounce[i] > 0 && Math.Abs(gelVel[i].Y) > 1f) { gelVel[i].Y *= -0.95f; gelBounce[i]--; next = gelPos[i] + gelVel[i]; }
                    else { gelBounce[i] = -1; continue; }
                }
                gelPos[i] = next;
            }
        }

        public float ExtraGap(in Box player)
        {
            float best = float.MaxValue;
            for (int i = 0; i < gels; i++)
                if (gelBounce[i] >= 0) best = Math.Min(best, player.Gap(new Box(gelPos[i].X, gelPos[i].Y, 8f, 8f)));
            if (smashAge >= 0)
            {
                float half = 8f * MathHelper.Lerp(5f, 30f, MathHelper.Clamp((smashAge + 1) / 9f, 0f, 1f));
                best = Math.Min(best, player.Gap(new Box(smashAt.X, smashAt.Y, half, half)));
            }
            return best;
        }
    }
}
