using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using BossPilot.Bosses.SlimesEye;
using BossPilot.Common;

namespace BossPilot.Bosses;

/// <summary>
/// King Slime (NPC 50, aiStyle 15). Kernel facts used (NPC.cs, aiStyle == 15):
/// contact damage only; on the ground ai[0] climbs by 2 (+1/+1/+2/+3/+4 below 80/60/40/20/10% life)
/// per tick and at 0 he jumps toward the player with (vx += 4, vy = -8), (4, -8), (4.5, -6),
/// (3.5, -13) cycling on ai[1]; in the air vx eases toward 3 px/tick; gravity 0.3, max fall 10.
/// He lands on platforms unless the player's top is below his bottom. Out of sight or more than
/// 160 px between his top and the player's feet for 300 ticks he teleports (ai[1] 5 then 6) to
/// the bottom point stored in localAI[1..2], known 60 ticks in advance. Every 5% of life lost
/// spawns 1-3 Blue Slimes (25 HP, hop toward the player).
/// </summary>
public sealed class KingSlime : PilotPlan
{
    public override string Boss => "king_slime";

    // Arena: 180 tiles of floor (walls at both ends), standard platform rows every 12 tiles.
    private const int Half = 90;
    private static readonly int[] Rows = [12, 24, 36];

    /// <summary>
    /// Kits (Classic, first boss):
    /// stage_default - Minishark (Arms Dealer) + Silver Bullets, platinum armor, Hermes Boots,
    ///   Cloud in a Bottle, Shiny Red Balloon, Lucky Horseshoe (natural terrain has drops), Band of
    ///   Regeneration (balloon and horseshoe come from the same sky island); Ironskin/Swiftness/
    ///   Regeneration; 200 life (a few crystals), lesser healing potions.
    /// min_preboss - Gold Bow + Flaming Arrows, iron armor, Hermes Boots only; 140 life.
    /// stage_plus - EoC-stage kit: Minishark + Meteor Shot, adds Anklet of the Wind and Wrath; 300 life.
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
                LoadoutGiver.SetMaxStats(p, 140);
                break;
            case "stage_plus":
                Kit.Hotbar(p, (ItemID.Minishark, 1), (ItemID.MeteorShot, 999), (ItemID.LesserHealingPotion, 10));
                Kit.Armor(p, ItemID.PlatinumHelmet, ItemID.PlatinumChainmail, ItemID.PlatinumGreaves);
                Kit.Accessories(p, ItemID.HermesBoots, ItemID.CloudinaBottle, ItemID.ShinyRedBalloon, ItemID.AnkletoftheWind, ItemID.BandofRegeneration);
                Kit.Potions(p, s.MaxDurationSeconds, BuffID.Ironskin, BuffID.Swiftness, BuffID.Regeneration, BuffID.Wrath);
                LoadoutGiver.SetMaxStats(p, 300);
                break;
            default:
                Kit.Hotbar(p, (ItemID.Minishark, 1), (ItemID.SilverBullet, 999), (ItemID.LesserHealingPotion, 5));
                Kit.Armor(p, ItemID.PlatinumHelmet, ItemID.PlatinumChainmail, ItemID.PlatinumGreaves);
                Kit.Accessories(p, ItemID.HermesBoots, ItemID.CloudinaBottle, ItemID.ShinyRedBalloon, ItemID.LuckyHorseshoe, ItemID.BandofRegeneration);
                Kit.Potions(p, s.MaxDurationSeconds, BuffID.Ironskin, BuffID.Swiftness, BuffID.Regeneration);
                LoadoutGiver.SetMaxStats(p, 200);
                break;
        }
    }

    public override Point ArenaSite(ScenarioConfig s) => ArenaKit.Site(Main.spawnTileX);

    public override void DecorateArena(Point site, ScenarioConfig s)
    {
        if (s.Arena == "flat_platforms") ArenaKit.Flat(site, Half, clear: 45, Rows, wallHeight: 45);
    }

    private readonly KsSim sim = new();
    private int side;      // side of King Slime we want to be on (-1 left, +1 right)
    private float wallL, wallR;

    protected override IBossSim CaptureBoss(NPC boss)
    {
        sim.Capture(boss, P);
        // Retreating in front of him ends at a wall. The safest crossing is under his -13 jump
        // (ai[1] == 3: 87 airborne ticks, bottom 4+ tiles up for ~75 of them). One hop cycle plus
        // a teleport closes ~85 tiles, so when that jump is next and less than 90 tiles are left
        // behind us, prefer the other side; with under 15 tiles left switch regardless.
        int now = P.Center.X < boss.Center.X ? -1 : 1;
        int half = Ctx.Scenario.Arena == "flat_platforms" ? Half : 120;
        wallL = (Ctx.Site.X - half) * 16f;
        wallR = (Ctx.Site.X + half) * 16f;
        if (side == 0) side = now;
        float room = now < 0 ? P.Center.X - wallL : wallR - P.Center.X;
        bool highJumpNext = boss.ai[1] == 3f && boss.velocity.Y == 0f;
        if (side == now && (room < 15 * 16f || (highJumpNext && room < 90 * 16f))) side = -now;
        return sim;
    }

    protected override void BeginAttempt(Player p)
    {
        Planner.Threats.SkipNpcTypes = [NPCID.KingSlime];
        Planner.Margin = 28f; // slimes get knocked around by our shots: keep a wider berth
        side = 0;
    }

    /// <summary>Slimes within this range are shot first (the Minishark kills one in two bullets).</summary>
    protected override Entity ChooseTarget(Player p, NPC boss) =>
        NearestThreat(p, (p.HeldItem.autoReuse ? 20 : 10) * 16f) ?? (Entity)boss;

    /// <summary>
    /// On the wanted side keep 14-34 tiles of room to King Slime; on the other side a constant
    /// cost plus a pull towards him (the contact cost decides when crossing is safe). Stay off the walls.
    /// </summary>
    protected override float Prefer(Vector2 c, Box boss, int k)
    {
        float cost = 0f;
        if (boss.Valid)
        {
            float dx = c.X - boss.X;
            if (dx * side > 0f)
            {
                float a = Math.Abs(dx);
                if (a < 14 * 16f) cost += (14 * 16f - a) * 0.03f;
                else if (a > 34 * 16f) cost += (a - 34 * 16f) * 0.01f;
            }
            else cost += 14f + Math.Abs(dx) * 0.02f;
        }
        float edge = Math.Max(wallL + 10 * 16f - c.X, c.X - (wallR - 10 * 16f));
        if (edge > 0f) cost += edge * 0.05f;
        return cost;
    }

    /// <summary>Forward model of aiStyle 15 (see class summary).</summary>
    private sealed class KsSim : IBossSim
    {
        private Vector2 bottom0, vel0, tp;
        private float w0, h0, ai0, ai1, ai2, lifeFrac;
        private int dir0;
        private bool ground0;

        private Vector2 bottom, vel;
        private float a0, a1, a2, w, h;
        private int dir;
        private bool ground, gone;
        private float inc;

        public void Capture(NPC n, Player p)
        {
            bottom0 = n.Bottom; vel0 = n.velocity;
            w0 = n.width; h0 = n.height;
            ai0 = n.ai[0]; ai1 = n.ai[1]; ai2 = n.ai[2];
            tp = new Vector2(n.localAI[1], n.localAI[2]);
            dir0 = n.direction == 0 ? 1 : n.direction;
            ground0 = n.velocity.Y == 0f;
            lifeFrac = (float)n.life / n.lifeMax;
            inc = 2f;
            if (lifeFrac < 0.8f) inc += 1f;
            if (lifeFrac < 0.6f) inc += 1f;
            if (lifeFrac < 0.4f) inc += 2f;
            if (lifeFrac < 0.2f) inc += 3f;
            if (lifeFrac < 0.1f) inc += 4f;
        }

        public Vector2 Now() => bottom0 - new Vector2(0f, h0 * 0.5f);

        public void Reset()
        {
            bottom = bottom0; vel = vel0; w = w0; h = h0;
            a0 = ai0; a1 = ai1; a2 = ai2; dir = dir0; ground = ground0; gone = false;
        }

        public Box Step(Vector2 pc, float ptop, float pbottom)
        {
            if (gone) return Box.None;
            float full = (lifeFrac * 0.5f + 0.75f);
            if (a1 == 5f)
            {
                a0++;
                float s = 0.5f + 0.5f * MathHelper.Clamp((60f - a0) / 60f, 0f, 1f);
                if (a0 >= 60f)
                {
                    bottom = tp; vel = Vector2.Zero; a1 = 6f; a0 = 0f; ground = true;
                    return Box.None;
                }
                return BoxAt(98f * full * s, 92f * full * s);
            }
            if (a1 == 6f)
            {
                a0++;
                float s = 0.5f + 0.5f * MathHelper.Clamp(a0 / 30f, 0f, 1f);
                if (a0 >= 30f) { a1 = 0f; a0 = 0f; }
                return BoxAt(98f * full * s, 92f * full * s);
            }

            // Teleport timer: the destination is random until it starts, so stop predicting then.
            if (Math.Abs(bottom.Y - h - pbottom) > 160f) a2++;
            if (a2 >= 300f && ground) { gone = true; return Box.None; }

            if (ground)
            {
                vel.X *= 0.8f;
                if (Math.Abs(vel.X) < 0.1f) vel.X = 0f;
                a0 += inc;
                if (a0 >= 0f)
                {
                    dir = pc.X < bottom.X ? -1 : 1;
                    if (a1 == 3f) { vel.Y = -13f; vel.X += 3.5f * dir; a0 = -200f; a1 = 0f; }
                    else if (a1 == 2f) { vel.Y = -6f; vel.X += 4.5f * dir; a0 = -120f; a1 = 3f; }
                    else { vel.Y = -8f; vel.X += 4f * dir; a0 = -120f; a1 += 1f; }
                    ground = false;
                }
            }
            else if ((dir == 1 && vel.X < 3f) || (dir == -1 && vel.X > -3f))
            {
                if ((dir == -1 && vel.X < 0.1f) || (dir == 1 && vel.X > -0.1f)) vel.X += 0.2f * dir;
                else vel.X *= 0.93f;
            }

            // He slides on the ground too (vx decays x0.8 per tick), not only in the air.
            float nx = bottom.X + vel.X;
            int edge = (int)((nx + Math.Sign(vel.X) * w * 0.5f) / 16f);
            if (vel.X != 0f && Tiles.Solid(edge, (int)((bottom.Y - h * 0.5f) / 16f))) vel.X = 0f;
            else bottom.X = nx;
            if (!ground)
            {
                vel.Y = Math.Min(vel.Y + 0.3f, 10f);
                float ny = bottom.Y + vel.Y;
                if (vel.Y > 0f)
                {
                    // Falls through platforms only while the player's top is below his bottom.
                    bool plats = !(ptop > bottom.Y);
                    float land = Tiles.Land(bottom.X - w * 0.5f, bottom.X + w * 0.5f, bottom.Y, ny, plats);
                    if (!float.IsNaN(land)) { ny = land; vel.Y = 0f; ground = true; }
                }
                bottom.Y = ny;
            }
            return BoxAt(w, h);
        }

        private Box BoxAt(float bw, float bh) => new(bottom.X, bottom.Y - bh * 0.5f, bw * 0.5f, bh * 0.5f);
    }
}
