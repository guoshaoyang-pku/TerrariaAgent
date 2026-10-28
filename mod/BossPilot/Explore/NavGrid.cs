using System;
using System.Collections.Generic;
using System.Reflection;
using Terraria;
using Terraria.ID;

namespace BossPilot.Explore;

/// <summary>
/// Mining model of the held pickaxe: ticks to break a tile, using the kernel's own
/// Player.GetPickaxeDamage (per-tile pick-power gates and damage scaling) and WorldGen.CanKillTile.
/// </summary>
internal sealed class Miner
{
    private static Func<Player, int, int, int, int, Tile, int> pickDamage;

    public readonly int Pick, UseTime;
    private readonly Player player;
    private readonly Dictionary<int, int> cache = [];

    public Miner(Player p, Item pickaxe)
    {
        player = p;
        Pick = pickaxe?.pick ?? 0;
        UseTime = Math.Max(1, (int)((pickaxe?.useTime ?? 20) * p.pickSpeed));
        pickDamage ??= typeof(Player).GetMethod("GetPickaxeDamage", BindingFlags.Instance | BindingFlags.NonPublic)
            .CreateDelegate<Func<Player, int, int, int, int, Tile, int>>();
    }

    /// <summary>Ticks of continuous swinging to break the tile at (x, y); -1 if this pickaxe cannot.</summary>
    public int Ticks(int x, int y)
    {
        Tile t = Main.tile[x, y];
        if (!t.HasTile) return 0;
        int type = t.TileType;
        if (Pick <= 0 || Main.tileAxe[type] || Main.tileHammer[type] || type is TileID.Boulder or TileID.Explosives
            or TileID.Containers or TileID.Containers2 or TileID.DemonAltar) return -1;
        if (!WorldGen.CanKillTile(x, y)) return -1;
        // Damage depends on the tile type and a few position gates (depth, dungeon side); cache by those.
        int key = type * 8 + (y > Main.worldSurface ? 1 : 0) + (y >= Main.UnderworldLayer ? 2 : 0)
                  + (x < Main.maxTilesX * 0.35 || x > Main.maxTilesX * 0.65 ? 4 : 0);
        if (!cache.TryGetValue(key, out int dmg))
        {
            try { dmg = pickDamage(player, x, y, Pick, 0, t); }
            catch { dmg = 0; }
            cache[key] = dmg;
        }
        if (dmg <= 0) return -1;
        int hits = (100 + dmg - 1) / dmg;
        return hits * UseTime;
    }
}

/// <summary>
/// Planner view of a rectangular window of the world: per-tile body blocking, standing support,
/// liquids, hazards and the ticks needed to dig a tile out. Built once per plan; cells outside the
/// window are solid and undiggable.
/// </summary>
internal sealed class NavGrid
{
    public const byte Solid = 1, Top = 2, Lava = 4, Water = 8, Hazard = 16, NoDig = 32, Falls = 64, Occupied = 128;

    public readonly int X0, Y0, W, H;
    private readonly byte[] flags;
    private readonly ushort[] dig;

    /// <summary>Tiles a pickaxe failed to break in practice this attempt (world coords packed).</summary>
    public static readonly HashSet<long> Blacklist = [];

    public static long Key(int x, int y) => ((long)x << 32) | (uint)y;

    public NavGrid(int x0, int y0, int w, int h, Miner miner)
    {
        X0 = Math.Max(x0, 41);
        Y0 = Math.Max(y0, 41);
        W = Math.Min(x0 + w, Main.maxTilesX - 42) - X0;
        H = Math.Min(y0 + h, Main.maxTilesY - 42) - Y0;
        flags = new byte[W * H];
        dig = new ushort[W * H];
        for (int j = 0; j < H; j++)
        for (int i = 0; i < W; i++)
            Classify(X0 + i, Y0 + j, i + j * W, miner);
    }

    private void Classify(int x, int y, int idx, Miner miner)
    {
        Tile t = Main.tile[x, y];
        byte f = 0;
        if (t.LiquidAmount > 0)
        {
            if (t.LiquidType is LiquidID.Lava or LiquidID.Shimmer) f |= Lava;
            else if (t.LiquidAmount > 64) f |= Water;
        }
        if (t.HasTile && !t.IsActuated)
        {
            int type = t.TileType;
            if (Main.tileSolid[type] && !Main.tileSolidTop[type])
            {
                f |= Solid;
                if (TileID.Sets.Falling[type]) f |= Falls;
                int ticks = Blacklist.Contains(Key(x, y)) ? -1 : miner.Ticks(x, y);
                if (ticks < 0) f |= NoDig;
                else dig[idx] = (ushort)Math.Min(ticks, 60000);
            }
            else if (Main.tileSolidTop[type] && t.TileFrameY == 0) f |= Top;
            // Furniture, pots, torches... block placing a block here (cuttable plants do not).
            if (!Main.tileSolid[type] && !Main.tileCut[type]) f |= Occupied;
            // Touch damage exactly as Player.Update applies it (Collision.CanTileHurt: spikes, thorns, hot
            // blocks, rolling cactus...), plus tiles that trigger traps or hatch antlion larvae when broken.
            if (Collision.CanTileHurt((ushort)type, x, y, null) && !TileID.Sets.Suffocate[type]
                || type is TileID.PressurePlates or TileID.GeyserTrap or TileID.LandMine or TileID.Cobweb or 485) f |= Hazard;
        }
        flags[idx] = f;
    }

    public bool In(int x, int y) => x >= X0 && y >= Y0 && x < X0 + W && y < Y0 + H;
    public int Index(int x, int y) => x - X0 + (y - Y0) * W;

    public byte F(int x, int y) => In(x, y) ? flags[Index(x, y)] : (byte)(Solid | NoDig);

    public bool IsSolid(int x, int y) => (F(x, y) & Solid) != 0;
    public bool Blocked(int x, int y) => (F(x, y) & (Solid | Lava)) != 0;
    public bool Support(int x, int y) => (F(x, y) & (Solid | Top)) != 0;
    public bool IsTop(int x, int y) => (F(x, y) & (Solid | Top)) == Top;
    public bool IsLava(int x, int y) => (F(x, y) & Lava) != 0;

    /// <summary>A block can be placed here: no tile (or a cuttable plant), no lava.</summary>
    public bool Placeable(int x, int y) => (F(x, y) & (Solid | Top | Occupied | Lava)) == 0;

    /// <summary>
    /// Ticks to clear (x, y) for the body: 0 if already free, -1 if impossible (undiggable, lava, near lava).
    /// Falling tiles (sand, silt, slush) above slide into the opening: over the pilot's head that buries it
    /// (suffocation), elsewhere the whole falling column has to be mined out.
    /// </summary>
    public int ClearCost(int x, int y, bool overBody = false)
    {
        byte f = F(x, y);
        if ((f & Lava) != 0) return -1;
        if ((f & Solid) == 0) return 0;
        if ((f & NoDig) != 0) return -1;
        // Opening a tile next to lava floods the tunnel.
        if (IsLava(x - 1, y) || IsLava(x + 1, y) || IsLava(x, y - 1)) return -1;
        int c = dig[Index(x, y)] + 6;
        int k = 0;
        while (k < 8 && (F(x, y - 1 - k) & (Falls | Solid)) == (Falls | Solid)) k++;
        if (k > 0)
        {
            // Over the head it buries the pilot; a tall column keeps refilling the tunnel faster than it is mined.
            if (overBody || k > 6) return -1;
            c = c * (1 + k) + 30;
        }
        return c;
    }

    /// <summary>Body (2 x 3 tiles, feet in row y) fits at node (x, y).</summary>
    public bool BodyFree(int x, int y)
    {
        for (int i = 0; i <= 1; i++)
        for (int j = -2; j <= 0; j++)
            if (Blocked(x + i, y + j)) return false;
        return true;
    }

    public bool Standable(int x, int y) => BodyFree(x, y) && (Support(x, y + 1) || Support(x + 1, y + 1));

    /// <summary>
    /// Extra cost of occupying node (x, y). Hazards: Collision.HurtTiles counts tiles overlapping or
    /// touching the hitbox, i.e. columns x..x+1, rows y-2..y+1 when centred (neighbour columns if off-centre).
    /// Water: submerged head means drowning and slow water physics.
    /// </summary>
    public int BodyPenalty(int x, int y)
    {
        int p = 0;
        for (int i = -1; i <= 2; i++)
        for (int j = -2; j <= 1; j++)
        {
            byte f = F(x + i, y + j);
            bool core = i is 0 or 1;
            if ((f & Hazard) != 0) p += core ? 240 : j <= 0 ? 40 : 0;
            if (core && j <= 0 && (f & Water) != 0) p += j == -2 ? 40 : 5;
        }
        // Under a sand ceiling any nearby dig brings it down.
        if ((F(x, y - 3) & Falls) != 0 || (F(x + 1, y - 3) & Falls) != 0) p += 15;
        return p;
    }

    public bool WetBody(int x, int y) => (F(x, y - 1) & Water) != 0 || (F(x + 1, y - 1) & Water) != 0;
}
