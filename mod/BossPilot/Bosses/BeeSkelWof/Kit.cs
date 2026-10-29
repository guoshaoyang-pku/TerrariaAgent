using Terraria;
using Terraria.ID;
using BossPilot.Common;

namespace BossPilot.Bosses.BeeSkelWof;

/// <summary>
/// Loadout helpers for the Queen Bee / Skeletron / Wall of Flesh plans. Every
/// attempt starts from an empty character (inventory, armor, accessories), so
/// a previous attempt's kit never leaks into the next one.
/// </summary>
public static class Kit
{
    public sealed record Tier(
        int Weapon, int Ammo, int AmmoStack,
        int Head, int Body, int Legs,
        int[] Accessories, int[] Buffs, int Life, int Mana = 20, int Potion = ItemID.LesserHealingPotion);

    public static void Apply(Player p, Tier t, double seconds)
    {
        for (int i = 0; i < p.inventory.Length; i++) p.inventory[i].TurnToAir();
        for (int i = 0; i < p.armor.Length; i++) p.armor[i].TurnToAir();
        for (int i = 0; i < p.dye.Length; i++) p.dye[i].TurnToAir();
        for (int i = 0; i < p.miscEquips.Length; i++) p.miscEquips[i].TurnToAir();

        p.inventory[0].SetDefaults(t.Weapon);
        if (t.Ammo > 0)
        {
            // Ammo slots (54-57) are drawn from first; two full stacks cover any fight length.
            p.inventory[54].SetDefaults(t.Ammo);
            p.inventory[54].stack = t.AmmoStack;
            p.inventory[55].SetDefaults(t.Ammo);
            p.inventory[55].stack = t.AmmoStack;
        }
        p.inventory[1].SetDefaults(t.Potion);
        p.inventory[1].stack = 10;

        p.armor[0].SetDefaults(t.Head);
        p.armor[1].SetDefaults(t.Body);
        p.armor[2].SetDefaults(t.Legs);
        // Classic difficulty: five accessory slots (armor[3..7]).
        for (int i = 0; i < t.Accessories.Length && i < 5; i++) p.armor[3 + i].SetDefaults(t.Accessories[i]);

        int duration = (int)(seconds * 60) + 3600;
        foreach (int b in t.Buffs) p.AddBuff(b, duration);

        LoadoutGiver.SetMaxStats(p, t.Life, t.Mana);
        p.selectedItem = 0;
    }
}
