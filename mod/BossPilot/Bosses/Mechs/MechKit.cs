using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using BossPilot.Common;

namespace BossPilot.Bosses.Mechs;

/// <summary>
/// Loadout and world prep shared by the three mechanical bosses (one progression
/// stage: post-Wall of Flesh, first mechanical boss).
///
/// stage_default is the classic ranged speedrun kit for the first mech: Daedalus
/// Stormbow (Hallowed Mimic) - arrows rain from above the cursor and need no line of
/// sight - with the world evil's arrows (Ichor: -15 defense, or Cursed: Cursed Inferno;
/// both beat Holy Arrows on 24-48 defense mech parts) and Holy Arrows as spares; the
/// Destroyer plan puts piercing Jester's Arrows first. Adamantite ranged armor
/// from the first altars, Fairy Wings (130 flight ticks, 6.75 px/tick), Frostspark
/// Boots (6.75 run, rocket time adds flight), Lucky Horseshoe (no fall-damage hits),
/// Magic Quiver + Ranger Emblem, 400 HP (15 crystals; life fruit needs a mech kill),
/// the usual pre-fight potions (Calming against wyverns/harpies over sky arenas) and
/// Greater Healing Potions.
/// min_preboss drops to Marrow + Jester's Arrows, Mythril armor, Angel Wings, Hermes Boots.
/// stage_plus is one mech later: Hallowed armor, Avenger Emblem, Leaf Wings, 500 HP.
/// No bottles/balloons: extra jumps make the pilot's flight model ambiguous.
/// </summary>
public static class MechKit
{
    public static void Grant(Player p, ScenarioConfig s)
    {
        Main.hardMode = true;
        for (int i = 0; i < 58; i++) p.inventory[i].TurnToAir();
        for (int i = 0; i < p.armor.Length; i++) p.armor[i].TurnToAir();
        for (int i = 0; i < p.miscEquips.Length; i++) p.miscEquips[i].TurnToAir();

        switch (s.Loadout)
        {
            case "min_preboss":
                Put(p, 0, ItemID.Marrow);
                Put(p, 54, ItemID.JestersArrow, 999);
                Armor(p, ItemID.MythrilHat, ItemID.MythrilChainmail, ItemID.MythrilGreaves);
                Accessories(p, ItemID.AngelWings, ItemID.HermesBoots, ItemID.LuckyHorseshoe, ItemID.RangerEmblem);
                Put(p, 10, ItemID.GreaterHealingPotion, 15);
                LoadoutGiver.SetMaxStats(p, 400);
                Buffs(p, BuffID.Ironskin, BuffID.Regeneration);
                break;
            case "stage_plus":
                Put(p, 0, ItemID.DaedalusStormbow);
                Put(p, 54, WorldGen.crimson ? ItemID.IchorArrow : ItemID.CursedArrow, 999);
                Put(p, 55, ItemID.HolyArrow, 999);
                Armor(p, ItemID.HallowedHelmet, ItemID.HallowedPlateMail, ItemID.HallowedGreaves);
                Accessories(p, ItemID.LeafWings, ItemID.FrostsparkBoots, ItemID.LuckyHorseshoe, ItemID.MagicQuiver, ItemID.AvengerEmblem);
                Put(p, 10, ItemID.GreaterHealingPotion, 15);
                LoadoutGiver.SetMaxStats(p, 500);
                Buffs(p, BuffID.Ironskin, BuffID.Regeneration, BuffID.Swiftness, BuffID.Archery, BuffID.Wrath, BuffID.Endurance, BuffID.WellFed2, BuffID.Calm);
                break;
            default: // stage_default
                Put(p, 0, ItemID.DaedalusStormbow);
                Put(p, 54, WorldGen.crimson ? ItemID.IchorArrow : ItemID.CursedArrow, 999);
                Put(p, 55, ItemID.HolyArrow, 999);
                Armor(p, ItemID.AdamantiteMask, ItemID.AdamantiteBreastplate, ItemID.AdamantiteLeggings);
                Accessories(p, ItemID.FairyWings, ItemID.FrostsparkBoots, ItemID.LuckyHorseshoe, ItemID.MagicQuiver, ItemID.RangerEmblem);
                Put(p, 10, ItemID.GreaterHealingPotion, 15);
                LoadoutGiver.SetMaxStats(p, 400);
                Buffs(p, BuffID.Ironskin, BuffID.Regeneration, BuffID.Swiftness, BuffID.Archery, BuffID.Wrath, BuffID.Endurance, BuffID.WellFed2, BuffID.Calm);
                break;
        }
        p.selectedItem = 0;
    }

    public static void Put(Player p, int slot, int type, int stack = 1)
    {
        p.inventory[slot].SetDefaults(type);
        p.inventory[slot].stack = stack;
    }

    private static void Armor(Player p, int head, int body, int legs)
    {
        p.armor[0].SetDefaults(head);
        p.armor[1].SetDefaults(body);
        p.armor[2].SetDefaults(legs);
    }

    /// <summary>Classic worlds have five accessory slots (armor[3..7]).</summary>
    private static void Accessories(Player p, params int[] types)
    {
        for (int i = 0; i < types.Length && i < 5; i++) p.armor[3 + i].SetDefaults(types[i]);
    }

    /// <summary>Potions drunk right before the fight; 8 minutes outlasts every scenario.</summary>
    private static void Buffs(Player p, params int[] buffs)
    {
        foreach (int b in buffs) p.AddBuff(b, 8 * 60 * 60);
    }

    /// <summary>Quick-heals below the threshold when potion sickness allows (the H key).</summary>
    public static void MaybeHeal(Player p, float fraction = 0.55f)
    {
        p.controlQuickHeal = p.statLife < p.statLifeMax2 * fraction && p.potionDelay <= 0;
    }

    /// <summary>
    /// Lit Peace Candles standing on the given floor row (spawn rate x1.3 slower, max spawns
    /// x0.7, on top of Calming): arena furniture against wyverns and harpies at sky height.
    /// </summary>
    public static void PeaceCandles(int centerX, int floorRow)
    {
        foreach (int dx in new[] { -30, 0, 30 })
        {
            int x = centerX + dx + 2, y = floorRow - 1;
            if (!WorldGen.InWorld(x, y, 2)) continue;
            WorldGen.KillTile(x, y, fail: false, effectOnly: false, noItem: true);
            WorldGen.PlaceTile(x, y, TileID.PeaceCandle, mute: true, forced: true);
        }
    }

    /// <summary>World X range of the built arena, in pixels.</summary>
    public static (float left, float right) ArenaSpan(FightContext ctx)
    {
        int half = ctx.Scenario.Arena switch { "open_sky" => 80, "enclosed" => 50, "bridge" => 200, _ => 60 };
        return ((ctx.Site.X - half) * 16f, (ctx.Site.X + half) * 16f);
    }

    public static float FloorY(FightContext ctx) => ctx.Site.Y * 16f;

    public static Vector2 SpawnOffset(Player p, int dxTiles, int dyTiles) => p.Center + new Vector2(dxTiles * 16, dyTiles * 16);
}
