using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace BossPilot.Common;

/// <summary>
/// Grants the loadout tiers referenced by scenarios. v0 is deliberately
/// simple: one reliable weapon per progression stage plus mobility basics, so
/// every scenario is at least survivable. Real per-stage kits come later.
/// </summary>
public static class LoadoutGiver
{
    public static void Grant(Player p, BossCatalog.BossEntry boss, string tier)
    {
        ClearHotbar(p);

        bool hardmode = boss.Hardmode;
        int weapon = hardmode ? ItemID.Megashark : ItemID.Minishark;
        int ammo = ItemID.MusketBall;
        if (tier == "stage_plus") weapon = ItemID.SDMG;

        Give(p, weapon, 1);
        Give(p, ammo, 999);

        // Mobility floor: boots + double jump; wings once hardmode.
        p.armor[3] = new Item();
        p.armor[3].SetDefaults(ItemID.HermesBoots);
        p.armor[4] = new Item();
        p.armor[4].SetDefaults(ItemID.CloudinaBottle);
        if (hardmode)
        {
            p.armor[5] = new Item();
            p.armor[5].SetDefaults(ItemID.LeafWings);
        }

        // Health floor so v0 trials are not one-shot checks.
        SetMaxStats(p, life: hardmode ? 500 : 400);

        p.selectedItem = 0;
    }

    /// <summary>
    /// Grants max life/mana as consumed crystals and fruit. tModLoader recomputes
    /// statLifeMax from these every tick (PlayerLoader.ResetMaxStatsToVanilla), so
    /// writing statLifeMax directly is undone on the next update.
    /// </summary>
    public static void SetMaxStats(Player p, int life, int mana = 20)
    {
        p.ConsumedLifeCrystals = (life - 100) / 20;
        p.ConsumedLifeFruit = (life - 400) / 5;
        p.ConsumedManaCrystals = (mana - 20) / 20;
        PlayerLoader.ResetMaxStatsToVanilla(p);
        p.statLife = p.statLifeMax2 = p.statLifeMax;
        p.statMana = p.statManaMax2 = p.statManaMax;
    }

    private static void Give(Player p, int type, int stack)
    {
        for (int i = 0; i < 10; i++)
        {
            if (p.inventory[i].IsAir)
            {
                p.inventory[i].SetDefaults(type);
                p.inventory[i].stack = stack;
                return;
            }
        }
    }

    private static void ClearHotbar(Player p)
    {
        for (int i = 0; i < 10; i++) p.inventory[i].TurnToAir();
    }
}
