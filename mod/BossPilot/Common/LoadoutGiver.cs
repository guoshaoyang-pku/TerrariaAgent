using Terraria;
using Terraria.ID;

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
        if (boss.NpcId == 134)
        {
            // Destroyer: the shared-pool worm is THE Daedalus target — holy
            // arrows rain onto a 50-segment body and pierce twice each. The
            // pilot's point-at-boss aim drops the star rain on the head; the
            // body mass behind it soaks the volley.
            Give(p, ItemID.DaedalusStormbow, 1);
            Give(p, ItemID.HolyArrow, 999);
            Give(p, ItemID.HolyArrow, 999);
            Give(p, ItemID.HolyArrow, 999);
            Equip(p, 0, ItemID.TitaniumMask);
            Equip(p, 1, ItemID.TitaniumBreastplate);
            Equip(p, 2, ItemID.TitaniumLeggings);
        }
        else if (hardmode)
        {
            Give(p, tier == "stage_plus" ? ItemID.SDMG : ItemID.Megashark, 1);
            // Megashark burns ~7 rounds/s; a 999 stack dries up ~140s in and
            // long fights (destroyer 80k) silently go idle.
            Give(p, ItemID.CrystalBullet, 999);
            Give(p, ItemID.CrystalBullet, 999);
            Give(p, ItemID.CrystalBullet, 999);
            // Ranged titanium: real defense plus the Shadow Dodge bonus.
            Equip(p, 0, ItemID.TitaniumMask);
            Equip(p, 1, ItemID.TitaniumBreastplate);
            Equip(p, 2, ItemID.TitaniumLeggings);
        }
        else if (boss.NpcId == 113)
        {
            // Wall of Flesh: its hungries respawn endlessly (~10 attached
            // at all times), so single-target weapons get eaten by the meat
            // screen forever. Beenades are THE classic counter — the bee
            // swarm ignores shields and chews the whole wall column. Star
            // Cannon stays as slot-1 backup.
            Give(p, ItemID.Beenade, 999);
            Give(p, ItemID.StarCannon, 1);
            Give(p, ItemID.FallenStar, 300);
            Give(p, ItemID.WoodPlatform, 999);
            Give(p, ItemID.Rope, 999);
            Equip(p, 0, ItemID.MeteorHelmet);
            Equip(p, 1, ItemID.MeteorSuit);
            Equip(p, 2, ItemID.MeteorLeggings);
            p.statMana = p.statManaMax2;
        }
        else
        {
            // Star Cannon: the hardest-hitting pre-hardmode ranged weapon.
            // Straight-flying stars, no mana dependency.
            Give(p, ItemID.StarCannon, 1);
            Give(p, ItemID.FallenStar, 300);
            // Platforms let the pilot bridge ravines mid-kite (the classic
            // hellbridge, built at run speed) on natural terrain.
            Give(p, ItemID.WoodPlatform, 999);
            // Rope is the vertical escape: shaft floors lie beyond jump/hook
            // reach, and rope deploys straight up its own column.
            Give(p, ItemID.Rope, 999);
            Equip(p, 0, ItemID.MeteorHelmet);
            Equip(p, 1, ItemID.MeteorSuit);
            Equip(p, 2, ItemID.MeteorLeggings);
            p.statMana = p.statManaMax2;
        }

        // Mobility floor: boots + double jump + hook; wings once hardmode.
        // Obsidian Skull pre-hardmode: natural underworld runs cross
        // hellstone, whose Burning contact damage silently out-damages the
        // boss. The hook (QuickGrapple finds it anywhere in inventory) is the
        // answer to cliffs that outgrow the double jump.
        p.armor[3] = new Item();
        p.armor[3].SetDefaults(ItemID.HermesBoots);
        p.armor[4] = new Item();
        p.armor[4].SetDefaults(ItemID.CloudinaBottle);
        Give(p, ItemID.DiamondHook, 1);
        if (hardmode)
        {
            p.armor[5] = new Item();
            p.armor[5].SetDefaults(ItemID.LeafWings);
            // Cross Necklace doubles post-hit i-frames — the single biggest
            // lever in hardmode attrition fights. Avenger Emblem: +12% dps
            // shortens fights the pilot survives on a margin.
            p.armor[6] = new Item();
            p.armor[6].SetDefaults(ItemID.CrossNecklace);
            p.armor[7] = new Item();
            p.armor[7].SetDefaults(ItemID.AvengerEmblem);
            // Warding on every accessory (+4 def each): hardmode contact
            // hits cluster in the 20-40 band, where flat defense scales hard.
            for (int i = 3; i <= 7; i++)
                p.armor[i].prefix = Terraria.ID.PrefixID.Warding;
        }
        else
        {
            p.armor[5] = new Item();
            p.armor[5].SetDefaults(ItemID.ObsidianSkull);
        }

        // Healing potions for the pilot's quick-heal.
        Give(p, hardmode ? ItemID.GreaterHealingPotion : ItemID.HealingPotion, 30);

        // Health floor so v0 trials are not one-shot checks. tML recomputes
        // statLifeMax from ConsumedLifeCrystals/Fruit every tick
        // (PlayerLoader), so grant health through the consumption counters.
        int targetLife = hardmode ? 500 : 400;
        p.ConsumedLifeCrystals = 15;              // 100 + 15*20 = 400
        p.ConsumedLifeFruit = hardmode ? 20 : 0;  // + 20*5 = 500 in hardmode
        p.statLifeMax = targetLife;
        p.statLife = targetLife;

        p.selectedItem = 0;
    }

    private static void Equip(Player p, int slot, int type)
    {
        p.armor[slot] = new Item();
        p.armor[slot].SetDefaults(type);
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
