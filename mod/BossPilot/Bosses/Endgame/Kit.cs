using BossPilot.Common;
using Terraria;
using Terraria.ID;

namespace BossPilot.Bosses.Endgame;

/// <summary>
/// Loadout building blocks for the endgame plans: equipment slots, the Witch's
/// Broom mount, buff potions and world progression flags. Plans compose these
/// per tier; everything is vanilla and obtainable in a Classic world.
/// </summary>
public static class Kit
{
    /// <summary>Clears hotbar, armor/accessory and misc slots so attempts never inherit gear.</summary>
    public static void Reset(Player p)
    {
        for (int i = 0; i < 58; i++) p.inventory[i].TurnToAir();
        for (int i = 0; i < p.armor.Length; i++) p.armor[i] = new Item();
        for (int i = 0; i < p.miscEquips.Length; i++) p.miscEquips[i] = new Item();
        if (p.mount.Active) p.mount.Dismount(p);
    }

    public static void Hotbar(Player p, int slot, int type, int stack = 1)
    {
        p.inventory[slot].SetDefaults(type);
        p.inventory[slot].stack = stack;
    }

    /// <summary>Armor in slots 0-2, accessories from slot 3 (Classic: five accessory slots).</summary>
    public static void Wear(Player p, int head, int body, int legs, params int[] accessories)
    {
        Equip(p, 0, head);
        Equip(p, 1, body);
        Equip(p, 2, legs);
        for (int i = 0; i < accessories.Length && i < 5; i++) Equip(p, 3 + i, accessories[i]);
    }

    private static void Equip(Player p, int slot, int type)
    {
        p.armor[slot] = new Item();
        p.armor[slot].SetDefaults(type);
    }

    /// <summary>
    /// Mounts the Witch's Broom (Mourning Wood drop, post-Plantera): infinite hover
    /// (Mount.DoesHoverIgnoresFatigue), no hitbox growth (heightBoost 0), 9 px/tick.
    /// Its symmetric point-mass dynamics are what BroomModel predicts.
    /// </summary>
    public static void Broom(Player p)
    {
        p.miscEquips[3] = new Item();
        p.miscEquips[3].SetDefaults(ItemID.WitchBroom);
        if (p.mount.Active) p.mount.Dismount(p);
        p.mount.SetMount(MountID.WitchBroom, p);
    }

    /// <summary>Pre-fight potions, applied as if drunk; lasts the whole attempt.</summary>
    public static void Buffs(Player p, ScenarioConfig s, params int[] buffs)
    {
        int duration = (int)(s.MaxDurationSeconds * 60) + 3600;
        foreach (int b in buffs) p.AddBuff(b, duration);
    }

    /// <summary>Hardmode plus every boss up to <paramref name="order"/> (BossCatalog progression order) defeated.</summary>
    public static void Progress(int order)
    {
        Main.hardMode = true;
        NPC.downedBoss1 = NPC.downedBoss2 = NPC.downedBoss3 = NPC.downedSlimeKing = NPC.downedQueenBee = true;
        NPC.downedMechBoss1 = NPC.downedMechBoss2 = NPC.downedMechBoss3 = NPC.downedMechBossAny = true;
        NPC.downedQueenSlime = true;
        NPC.downedPlantBoss = order > 9;
        NPC.downedGolemBoss = order > 10;
        NPC.downedFishron = NPC.downedEmpressOfLight = order > 11;
        NPC.downedAncientCultist = order > 12;
        NPC.downedTowerSolar = NPC.downedTowerVortex = NPC.downedTowerNebula = NPC.downedTowerStardust = order > 12;
        // A summoned Moon Lord is not the end of a Lunar Event; keep event state out of the way.
        NPC.LunarApocalypseIsUp = false;
        NPC.TowerActiveSolar = NPC.TowerActiveVortex = NPC.TowerActiveNebula = NPC.TowerActiveStardust = false;
        NPC.MoonLordCountdown = 0;
    }
}
