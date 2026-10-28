using Terraria;
using Terraria.ID;

namespace BossPilot.Bosses.EvilDeer;

/// <summary>Loadout helpers: the pilot persists across attempts, so every slot a kit uses is rewritten.</summary>
public static class Kit
{
    /// <summary>Clears hotbar, ammo slots, armor and accessories.</summary>
    public static void Reset(Player p)
    {
        for (int i = 0; i < 10; i++) p.inventory[i].TurnToAir();
        for (int i = 54; i < 58; i++) p.inventory[i].TurnToAir();
        for (int i = 0; i < 10; i++) p.armor[i] = new Item();
        p.selectedItem = 0;
    }

    public static void Hotbar(Player p, int slot, int type, int stack = 1)
    {
        p.inventory[slot].SetDefaults(type);
        p.inventory[slot].stack = stack;
    }

    public static void Ammo(Player p, int type, int stack = 999)
    {
        for (int i = 54; i < 58; i++)
            if (p.inventory[i].IsAir)
            {
                p.inventory[i].SetDefaults(type);
                p.inventory[i].stack = stack;
                return;
            }
    }

    /// <summary>Armor set into slots 0-2.</summary>
    public static void Armor(Player p, int head, int body, int legs)
    {
        Equip(p, 0, head);
        Equip(p, 1, body);
        Equip(p, 2, legs);
    }

    /// <summary>Accessories into slots 3.. (five in Classic pre-hardmode).</summary>
    public static void Accessories(Player p, params int[] types)
    {
        for (int i = 0; i < types.Length && i < 5; i++) Equip(p, 3 + i, types[i]);
    }

    /// <summary>Potion buffs, as if drunk right before summoning.</summary>
    public static void Buffs(Player p, double seconds, params int[] buffs)
    {
        foreach (int b in buffs) p.AddBuff(b, (int)(seconds * 60) + 600);
    }

    private static void Equip(Player p, int slot, int type)
    {
        p.armor[slot] = new Item();
        p.armor[slot].SetDefaults(type);
    }
}
