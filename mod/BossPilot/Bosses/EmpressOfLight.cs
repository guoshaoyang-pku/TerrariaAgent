using System;
using BossPilot.Bosses.Endgame;
using BossPilot.Common;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace BossPilot.Bosses;

/// <summary>
/// Empress of Light (636). Kernel facts used (NPC.AI_120_HallowBoss):
/// enrage = NPC.ShouldEmpressBeEnraged() = Main.dayTime (non-remix) only, which
/// turns every hit into 9999 damage and forces the expert attack patterns; the
/// Hallow only matters for the Prismatic Lacewing summon (despawns outside it).
/// She leaves when the target is over 6400 px away, or (when genuinely enraged)
/// at night / after day time 53400, so the noon start of eol_daytime is safe.
/// She spawns 200 px above the killed lacewing, i.e. right above the pilot.
/// </summary>
public sealed class EmpressOfLight : EndgamePlan
{
    public override string Boss => "empress_of_light";

    private const int ArenaWidth = 260, ArenaHeight = 110;

    // Loadout (Classic world, ranged, Witch's Broom for infinite symmetric flight):
    // - stage_default = post-Golem: Chain Gun + Ichor Bullets (-15 defense on her 50/60; measured
    //   ~7% faster than homing Chlorophyte with the planner's lead aim), Shroomite (bullet) armor,
    //   Sniper Scope / Destroyer + Ranger Emblem / Celestial Stone (post-Golem), Ankh Shield for
    //   knockback immunity, Greater Healing potions.
    // - stage_plus = post-Cultist (lunar fragments): Vortex Beater + Vortex armor, Celestial Shell,
    //   Super Healing potions. Daytime Empress one-shots anyway, so the extra tier buys kill speed.
    // - min_preboss = early hardmode: Megashark + Crystal Bullets, Hallowed armor.
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

    public override Point ArenaSite(ScenarioConfig s) => Arenas.Site(s.Biome);

    public override void DecorateArena(Point site, ScenarioConfig s) => Arenas.Platforms(site, ArenaWidth, ArenaHeight, rows: 2, gap: 9);

    protected override void CollectHazards(Player p, FightContext ctx)
    {
        NPC boss = FindNpc(Entry.NpcId);
        SunDance sun = null;
        if (boss != null)
        {
            Hazards.Add(new EmpressBody(boss));
            sun = new SunDance(boss);
        }
        Vector2 me = p.Center;
        foreach (Projectile pr in Main.projectile)
        {
            if (!pr.active || !pr.hostile) continue;
            switch (pr.type)
            {
                case ProjectileID.HallowBossRainbowStreak:
                    if (Vector2.Distance(pr.Center, me) < 2000) Hazards.Add(new RainbowStreak(pr));
                    break;
                case ProjectileID.HallowBossLastingRainbow:
                    if (Vector2.Distance(pr.Center, me) < 2400) Hazards.Add(new RainbowTrail(pr));
                    break;
                case ProjectileID.FairyQueenLance:
                    Hazards.Add(new Lance(pr));
                    break;
                case ProjectileID.FairyQueenSunDance:
                    sun?.Add(pr);
                    break;
                default:
                    if (pr.damage > 0 && Vector2.Distance(pr.Center, me) < 1200) Hazards.Add(LinearHazard.Of(pr));
                    break;
            }
        }
        if (sun != null && sun.Any) Hazards.Add(sun);
    }

    protected override void ChooseGoal(Player p, FightContext ctx)
    {
        Point site = ctx.Site;
        Planner.Box = Arenas.FlightBox(site, ArenaWidth, 14, 24, 95);
        NPC boss = FindNpc(Entry.NpcId);
        Vector2 home = new(site.X * 16, (site.Y - 45) * 16);
        if (boss == null) { Planner.Goal = home; return; }

        // Ranged spacing per attack: far from her for the bolt/rainbow/sun casts, closer otherwise.
        int attack = (int)boss.ai[0];
        float range = attack switch { 6 => 950f, 2 or 5 or 12 => 650f, _ => 480f };
        Vector2 away = (p.Center - boss.Center).SafeNormalize(Vector2.UnitY);
        // Stay below-ish: she hovers above the pilot in every cast.
        away = Vector2.Normalize(away + new Vector2(0, 0.6f));
        Vector2 goal = boss.Center + away * range;
        goal.Y = MathHelper.Lerp(goal.Y, home.Y, 0.5f);
        Planner.Goal = Vector2.Clamp(goal, Planner.Box.TopLeft(), Planner.Box.BottomRight());
    }

    protected override Vector2? AimPoint(Player p, FightContext ctx)
    {
        NPC boss = FindNpc(Entry.NpcId);
        if (boss == null) return null;
        return Lead(p, boss.Center, boss.velocity);
    }
}
