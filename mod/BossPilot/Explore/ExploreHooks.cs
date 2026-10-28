using System;
using Terraria;
using Terraria.ModLoader;

namespace BossPilot.Explore;

/// <summary>Routes the pilot's per-tick controls and damage events to the running exploration job.</summary>
public sealed class ExplorePilot : ModPlayer
{
    /// <summary>Run/jump stats exactly as this tick's HorizontalMovement uses them.</summary>
    public MoveStats Stats { get; private set; }

    public override void PostUpdateRunSpeeds()
    {
        if (Player.whoAmI == Main.myPlayer && ExploreRunner.Current != null) Stats = MoveStats.Capture(Player);
    }

    // SetControls, not PreUpdate: Player.Update resets controls from input between the two.
    public override void SetControls()
    {
        if (Player.whoAmI == Main.myPlayer) ExploreRunner.Current?.Control(Player);
    }

    public override void OnHurt(Player.HurtInfo info)
    {
        if (Player.whoAmI != Main.myPlayer) return;
        var src = info.DamageSource;
        string what = src.SourceNPCIndex >= 0 ? "npc " + Lang.GetNPCNameValue(Main.npc[src.SourceNPCIndex].type)
            : src.SourceProjectileType > 0 ? "proj " + Terraria.ID.ProjectileID.Search.GetName(src.SourceProjectileType)
            : src.SourceOtherIndex == 3 ? "tile " + Terraria.ID.TileID.Search.GetName(Math.Max(0, Collision.HurtTiles(Player.position, Player.width, Player.height, Player).type))
            : src.SourceOtherIndex == 0 ? "fall"
            : src.SourceOtherIndex >= 0 ? "other " + src.SourceOtherIndex : "?";
        ExploreRunner.Current?.NoteHurt(info.Damage, what);
    }
}

/// <summary>Spawn switch and dig counter for exploration jobs; inert unless one is running.</summary>
public sealed class ExploreHooks : ModSystem
{
    public override void Load()
    {
        On_NPC.SpawnNPC += orig =>
        {
            if (ExploreRunner.Current is { SpawnsEnabled: false }) return;
            orig();
        };
        On_Player.PickTile += (orig, self, x, y, pickPower) =>
        {
            bool had = Main.tile[x, y].HasTile;
            orig(self, x, y, pickPower);
            if (had && !Main.tile[x, y].HasTile && self.whoAmI == Main.myPlayer) ExploreRunner.Current?.NoteDug();
        };
    }

    public override void Unload()
    {
        JumpTemplates.Reset();
        Landmarks.Reset();
    }
}
