using Terraria;
using Terraria.ID;

namespace BossPilot.Bosses.SlimesEye;

/// <summary>Loadout helpers: hotbar, armor, the five Classic accessory slots, pre-fight potions.</summary>
public static class Kit
{
    public static void Clear(Player p)
    {
        for (int i = 0; i < 50; i++) p.inventory[i].TurnToAir();
        for (int i = 0; i < p.armor.Length; i++) p.armor[i] = new Item();
        for (int i = 0; i < p.miscEquips.Length; i++) p.miscEquips[i] = new Item();
        p.selectedItem = 0;
    }

    /// <summary>Puts items into the hotbar in order; the first one is held.</summary>
    public static void Hotbar(Player p, params (int type, int stack)[] items)
    {
        for (int i = 0; i < items.Length && i < 10; i++)
        {
            p.inventory[i].SetDefaults(items[i].type);
            p.inventory[i].stack = items[i].stack;
        }
        p.selectedItem = 0;
    }

    public static void Armor(Player p, int head, int body, int legs)
    {
        Set(p, 0, head);
        Set(p, 1, body);
        Set(p, 2, legs);
    }

    /// <summary>Classic worlds have five accessory slots (armor[3..7]).</summary>
    public static void Accessories(Player p, params int[] items)
    {
        for (int i = 0; i < items.Length && i < 5; i++) Set(p, 3 + i, items[i]);
    }

    /// <summary>Potions drunk right before the fight; they last the whole attempt.</summary>
    public static void Potions(Player p, double seconds, params int[] buffs)
    {
        int ticks = (int)(seconds * 60) + 600;
        foreach (int b in buffs) p.AddBuff(b, ticks);
    }

    private static void Set(Player p, int slot, int type)
    {
        p.armor[slot] = new Item();
        if (type > 0) p.armor[slot].SetDefaults(type);
    }

    /// <summary>Drinks a healing potion (quick heal) below the given life fraction.</summary>
    public static void HealIfLow(Player p, float frac = 0.45f)
    {
        if (p.statLife < p.statLifeMax2 * frac && !p.HasBuff(BuffID.PotionSickness)) p.QuickHeal();
    }
}
