using System;
using BossPilot.Bosses.Endgame;
using BossPilot.Common;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace BossPilot.Bosses;

/// <summary>
/// Lunatic Cultist (439) with its ritual clones (440). Kernel facts used
/// (NPC.AI_084_LunaticCultist): no time-of-day or biome enrage; it only leaves
/// when the target is dead or over 5600 px away. A 420-tick invulnerable intro,
/// then each attack starts by flying to pilot + (0,-200). In the ritual
/// (ai[0] == 5) the real Cultist hides on the circle point farthest from the
/// pilot; hitting it between ai[1] 120 and 420 cancels the ritual and kills the
/// clones, hitting a clone lets the ritual finish and summon a Phantasm Dragon.
/// Clones and the Cultist are not chaseable then, so homing ammo flies straight.
/// </summary>
public sealed class LunaticCultist : EndgamePlan
{
    public override string Boss => "lunatic_cultist";

    private const int ArenaWidth = 230, ArenaHeight = 90;

    // Loadout (post-Golem / Fishron / Empress, Classic):
    // - stage_default: Chain Gun + Ichor Bullets (-15 defense; homing is useless in the ritual,
    //   where nothing is chaseable), Shroomite armor, Ankh Shield, Sniper Scope, Destroyer +
    //   Ranger Emblem, Celestial Stone, Greater Healing potions.
    // - stage_plus = post-Cultist: Vortex Beater + Vortex armor, Celestial Shell, Super Healing.
    // - min_preboss: Megashark + Crystal Bullets, Hallowed armor.
    // Witch's Broom for infinite hover in all tiers (Pumpkin Moon, post-Plantera).
    public override void GrantLoadout(Player p, ScenarioConfig s)
    {
        Kit.Reset(p);
        Kit.Progress(Entry.ProgressionOrder);
        switch (s.Loadout)
        {
            case "stage_plus":
                Kit.Hotbar(p, 0, ItemID.VortexBeater);
                Kit.Hotbar(p, 54, ItemID.ChlorophyteBullet, 9999);
                Kit.Wear(p, ItemID.VortexHelmet, ItemID.VortexBreastplate, ItemID.VortexLeggings,
                    ItemID.AnkhShield, ItemID.SniperScope, ItemID.DestroyerEmblem, ItemID.CelestialShell, ItemID.AvengerEmblem);
                GiveHealing(p, super: true);
                break;
            case "min_preboss":
                Kit.Hotbar(p, 0, ItemID.Megashark);
                Kit.Hotbar(p, 54, ItemID.CrystalBullet, 9999);
                Kit.Wear(p, ItemID.HallowedHelmet, ItemID.HallowedPlateMail, ItemID.HallowedGreaves,
                    ItemID.CobaltShield, ItemID.RangerEmblem, ItemID.FeralClaws, ItemID.CrossNecklace, ItemID.ObsidianRose);
                GiveHealing(p, super: false);
                break;
            default:
                Kit.Hotbar(p, 0, ItemID.ChainGun);
                Kit.Hotbar(p, 54, ItemID.IchorBullet, 9999);
                Kit.Wear(p, ItemID.ShroomiteHelmet, ItemID.ShroomiteBreastplate, ItemID.ShroomiteLeggings,
                    ItemID.AnkhShield, ItemID.SniperScope, ItemID.DestroyerEmblem, ItemID.CelestialStone, ItemID.RangerEmblem);
                GiveHealing(p, super: false);
                break;
        }
        Kit.Broom(p);
        Kit.Buffs(p, s, BuffID.Ironskin, BuffID.Regeneration, BuffID.Endurance, BuffID.Lifeforce,
            BuffID.Wrath, BuffID.Rage, BuffID.AmmoReservation);
        LoadoutGiver.SetMaxStats(p, life: 500, mana: 200);
        p.selectedItem = 0;
    }

    /// <summary>The fight happens in front of the dungeon entrance, where the devotees chant.</summary>
    public override Point ArenaSite(ScenarioConfig s)
    {
        int x = Main.dungeonX + (Main.dungeonX < Main.maxTilesX / 2 ? 40 : -40);
        return new Point(x, Arenas.SurfaceAt(x));
    }

    public override void DecorateArena(Point site, ScenarioConfig s) => Arenas.Platforms(site, ArenaWidth, ArenaHeight, rows: 2, gap: 8);

    /// <summary>The Cultist rises from the devotees' tablet just ahead of the pilot.</summary>
    public override void SpawnBoss(Player p, ScenarioConfig s) => SpawnAbove(p, Entry.NpcId, 14);

    protected override void CollectHazards(Player p, FightContext ctx)
    {
        Vector2 me = p.Center;
        foreach (NPC n in Main.npc)
        {
            if (!n.active || n.friendly || n.damage <= 0 || Vector2.Distance(n.Center, me) > 1600) continue;
            if (n.type == NPCID.AncientLight) Hazards.Add(new AncientLight(n));
            else if (n.type == NPCID.CultistBoss) Hazards.Add(new CultistBody(n));
            else Hazards.Add(new BodyHazard(n, inflate: 4f, damp: 0.95f));
        }
        foreach (Projectile pr in Main.projectile)
        {
            if (!pr.active || !pr.hostile || Vector2.Distance(pr.Center, me) > 1600) continue;
            switch (pr.type)
            {
                case ProjectileID.CultistBossIceMist: Hazards.Add(new IceMist(pr)); break;
                case ProjectileID.CultistBossLightningOrb: Hazards.Add(new LightningOrb(pr)); break;
                case ProjectileID.CultistBossLightningOrbArc: Hazards.Add(new LightningArc(pr)); break;
                case ProjectileID.CultistRitual: break;
                case ProjectileID.CultistBossFireBall: Hazards.Add(new CultistFireball(pr)); break;
                default:
                    if (pr.damage > 0) Hazards.Add(LinearHazard.Of(pr));
                    break;
            }
        }
    }

    protected override void ChooseGoal(Player p, FightContext ctx)
    {
        Point site = ctx.Site;
        Planner.Box = Arenas.FlightBox(site, ArenaWidth, 12, 20, 80);
        Vector2 home = new(site.X * 16, (site.Y - 40) * 16);
        NPC boss = FindNpc(Entry.NpcId);
        if (boss == null) { Planner.Goal = home; return; }

        Vector2 goal;
        if (boss.ai[0] == 5f && RitualCenter(boss) is Vector2 circle)
        {
            // Get on the real Cultist's side of the circle so the shot never crosses a clone.
            Vector2 outward = (boss.Center - circle).SafeNormalize(Vector2.UnitY);
            goal = boss.Center + outward * 320f;
        }
        else
        {
            // Every other attack ends with the Cultist 200 px above the pilot, and fireballs follow
            // 44 ticks later: slide out sideways at mid height, keeping the current side.
            float side = Math.Sign(p.Center.X - boss.Center.X);
            if (side == 0) side = 1;
            goal = new Vector2(boss.Center.X + side * 460f, home.Y);
            if (goal.X < Planner.Box.Left + 120 || goal.X > Planner.Box.Right - 120) goal.X = boss.Center.X - side * 460f;
        }
        Planner.Goal = Vector2.Clamp(goal, Planner.Box.TopLeft(), Planner.Box.BottomRight());
    }

    private static Vector2? RitualCenter(NPC boss)
    {
        int i = (int)boss.ai[2];
        if (i >= 0 && i < Main.maxProjectiles && Main.projectile[i].active && Main.projectile[i].type == ProjectileID.CultistRitual)
            return Main.projectile[i].Center;
        return null;
    }

    protected override Vector2? AimPoint(Player p, FightContext ctx)
    {
        NPC boss = FindNpc(Entry.NpcId);
        if (boss == null) return null;
        if (boss.ai[0] == 5f)
        {
            // Only shoot the real Cultist once it is visible, and only along a clone-free line.
            if (boss.ai[1] < 118f || boss.ai[1] >= 420f || !ClearShot(p.Center, boss)) return null;
            return boss.Center;
        }
        // Lunatic Ancient Lights in the way are fine targets too; aim at the Cultist.
        return Lead(p, boss.Center, boss.ai[0] == 1f ? boss.velocity * 0.5f : boss.velocity);
    }

    private static bool ClearShot(Vector2 from, NPC target)
    {
        Shape line = Shape.Segment(from, target.Center, 6f);
        foreach (NPC n in Main.npc)
        {
            if (!n.active || n.type != NPCID.CultistBossClone) continue;
            if (line.Clearance(n.Center, new Vector2(n.width, n.height) * 0.5f) < 14f) return false;
        }
        return true;
    }
}
