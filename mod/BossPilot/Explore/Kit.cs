using BossPilot.Common;
using Terraria;
using Terraria.ID;

namespace BossPilot.Explore;

/// <summary>Grants the exploration kit into fixed slots the navigator relies on.</summary>
public static class Kit
{
    public const int PickSlot = 0, BlockSlot = 1, PlatformSlot = 2, TorchSlot = 3, RopeSlot = 4, MirrorSlot = 5,
        PotionSlot = 6, WeaponSlot = 7;

    public static void Grant(Player p, KitSpec k)
    {
        for (int i = 0; i < p.inventory.Length; i++) p.inventory[i].TurnToAir();
        for (int i = 0; i < p.armor.Length; i++) p.armor[i].TurnToAir();
        for (int i = 0; i < p.miscEquips.Length; i++) p.miscEquips[i].TurnToAir();

        Set(p.inventory, PickSlot, Id(k.Pickaxe, ItemID.CopperPickaxe), 1);
        Set(p.inventory, BlockSlot, ItemID.DirtBlock, k.Blocks);
        Set(p.inventory, PlatformSlot, ItemID.WoodPlatform, k.Platforms);
        Set(p.inventory, TorchSlot, ItemID.Torch, k.Torches);
        Set(p.inventory, RopeSlot, ItemID.Rope, k.Rope);
        if (k.Mirror) Set(p.inventory, MirrorSlot, ItemID.MagicMirror, 1);
        Set(p.inventory, PotionSlot, ItemID.LesserHealingPotion, k.Potions);
        if (!string.IsNullOrEmpty(k.Weapon)) Set(p.inventory, WeaponSlot, Id(k.Weapon, ItemID.CopperShortsword), 1);
        if (k.Boots) Set(p.armor, 3, ItemID.HermesBoots, 1);
        if (k.Cloud) Set(p.armor, 4, ItemID.CloudinaBottle, 1);
        if (k.Hook) Set(p.miscEquips, 4, ItemID.GrapplingHook, 1);
        LoadoutGiver.SetMaxStats(p, k.Life);
        p.selectedItem = PickSlot;
    }

    public static int Count(Player p, int type)
    {
        int n = 0;
        for (int i = 0; i < 58; i++)
            if (p.inventory[i].type == type) n += p.inventory[i].stack;
        return n;
    }

    private static int Id(string name, int fallback) =>
        ItemID.Search.TryGetId(name, out int id) ? id : fallback;

    private static void Set(Item[] items, int slot, int type, int stack)
    {
        if (stack <= 0) return;
        items[slot] = new Item();
        items[slot].SetDefaults(type);
        items[slot].stack = System.Math.Min(stack, items[slot].maxStack);
    }
}
