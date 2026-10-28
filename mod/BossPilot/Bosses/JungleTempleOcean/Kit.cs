using Terraria;
using Terraria.ID;
using BossPilot.Common;

namespace BossPilot.Bosses.JungleTempleOcean;

/// <summary>A complete pilot kit: gun + ammo, armor, accessories, buffs, max life.</summary>
public sealed record Kit(int Gun, int Ammo, int[] Armor, int[] Accessories, int[] Buffs, int Life);

/// <summary>Applies kits and the hardmode world flags these bosses assume.</summary>
public static class Kits
{
    /// <summary>Standard speedrun potions for a hardmode boss (all brewable before Plantera).</summary>
    public static readonly int[] StandardBuffs =
    [
        BuffID.Ironskin, BuffID.Regeneration, BuffID.Swiftness, BuffID.Endurance,
        BuffID.Wrath, BuffID.Rage, BuffID.Lifeforce, BuffID.AmmoReservation, BuffID.WellFed,
    ];

    public static void Apply(Player p, Kit kit, ScenarioConfig s)
    {
        for (int i = 0; i < 58; i++) p.inventory[i].TurnToAir();
        for (int i = 0; i < p.armor.Length; i++) p.armor[i] = new Item();
        for (int i = 0; i < p.dye.Length; i++) p.dye[i] = new Item();
        for (int i = 0; i < p.miscEquips.Length; i++) p.miscEquips[i] = new Item();

        p.inventory[0].SetDefaults(kit.Gun);
        p.inventory[54].SetDefaults(kit.Ammo);
        p.inventory[54].stack = p.inventory[54].maxStack;
        p.inventory[55].SetDefaults(kit.Ammo);
        p.inventory[55].stack = p.inventory[55].maxStack;
        for (int i = 0; i < kit.Armor.Length && i < 3; i++) p.armor[i].SetDefaults(kit.Armor[i]);
        for (int i = 0; i < kit.Accessories.Length && i < 5; i++) p.armor[3 + i].SetDefaults(kit.Accessories[i]);
        p.selectedItem = 0;

        LoadoutGiver.SetMaxStats(p, kit.Life, 20);
        int duration = (int)(s.MaxDurationSeconds * 60) + 3600;
        foreach (int b in kit.Buffs) p.AddBuff(b, duration);
        p.statLife = p.statLifeMax2;
    }

    /// <summary>World state of the boss's place in progression (Classic difficulty is left untouched).</summary>
    public static void Hardmode(bool plantDown, bool golemDown)
    {
        Main.hardMode = true;
        NPC.downedMechBoss1 = NPC.downedMechBoss2 = NPC.downedMechBoss3 = NPC.downedMechBossAny = true;
        NPC.downedPlantBoss = plantDown;
        NPC.downedGolemBoss = golemDown;
        // An invasion started during an earlier attempt (e.g. a Martian probe) must not carry over.
        Main.invasionType = 0;
        Main.invasionSize = 0;
        Main.invasionDelay = 0;
    }
}
