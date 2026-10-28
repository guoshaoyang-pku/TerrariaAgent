using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;

namespace BossPilot.Common;

/// <summary>
/// General-purpose fight controller (v0): per-tick reactive pilot that keeps a
/// range band from the boss, sidesteps incoming hostile projectiles by
/// crossing their velocity line, jumps when horizontally blocked, and holds
/// attack on the boss. Runs on native player controls, 60 Hz, no rendering
/// dependency. Per-boss tuned controllers replace this later; this is the
/// floor that should already survive simple fights.
/// </summary>
public sealed class AutoPilot : ModPlayer
{
    public static bool Enabled = true;

    // Tuning knobs (world units: 1 tile = 16 px).
    private const float PreferredRange = 18f * 16;   // keep ~18 tiles from boss
    private const float RangeSlack = 6f * 16;
    private const float ThreatHorizon = 90f;          // ticks ahead a projectile matters
    private const float ThreatRadius = 3.5f * 16;     // personal space
    private const float MaxSpeed = 11f;

    private int stuckTicks;
    private int jumpCooldown;
    private int placeCooldown;
    private int hookCooldown;
    private bool bridgedOnce;
    private bool hookFiredOnce;
    private float lastX;
    private int lastBossHp;
    private long lastDamageTick = -10000;

    /// <summary>
    /// WoF hellbridge altitude (tile Y) maintained by the bridging code;
    /// -1 = free altitude. Set by TrialRunner at fight start.
    /// </summary>
    public static int BridgeLineY = -1;

    // SetControls runs after vanilla input processing, so our writes survive
    // the frame. (PreUpdate writes get overwritten by PlayerInput.)
    public override void SetControls()
    {
        if (!Enabled || TrialRunner.Instance.Current != TrialRunner.Phase.Fighting) return;
        if (Player.dead) return;

        NPC boss = FindBoss();
        Vector2 pos = Player.Center;
        Vector2 vel = Player.velocity;

        // --- Threat scan: nearest hostile projectile predicted to pass close.
        Vector2 dodge = Vector2.Zero;
        float worst = float.MaxValue;
        foreach (Projectile pr in Main.projectile)
        {
            if (!pr.active || !pr.hostile || pr.damage <= 0) continue;
            TryThreat(pr.Center, pr.velocity, Math.Max(pr.width, pr.height) * 0.5f, ref dodge, ref worst, pos);
        }
        // The boss body is itself the main threat: predict its landing point
        // (jumping bosses like King Slime) with a generous personal radius.
        // Exception: the Wall of Flesh is handled by its sweep logic below.
        bool isWoF = boss != null && boss.type == 113;
        int wofDir = boss != null && boss.velocity.X >= 0 ? 1 : -1;
        if (boss != null && boss.life < lastBossHp)
        {
            lastBossHp = boss.life;
            lastDamageTick = Main.GameUpdateCount;
        }
        else if (boss != null) lastBossHp = boss.life;
        if (boss != null && !isWoF)
            TryThreat(boss.Center, boss.velocity, Math.Max(boss.width, boss.height) * 0.5f + 4f * 16,
                ref dodge, ref worst, pos);

        // Hostile minions (Queen Bee's bees, etc.) — NPCs, not projectiles.
        foreach (NPC n in Main.npc)
        {
            if (!n.active || n.friendly || n.boss || n.damage <= 0 || n.lifeMax > 500) continue;
            TryThreat(n.Center, n.velocity, Math.Max(n.width, n.height) * 0.5f + 16,
                ref dodge, ref worst, pos);
        }

        // --- Range keeping vs boss.
        float moveX = 0f, moveY = 0f;
        if (boss != null)
        {
            Player.selectedItem = 0; // weapon slot; platform placement overrides it for one tick
            Vector2 toBoss = boss.Center - pos;
            float dist = toBoss.Length();
            Vector2 dir = dist > 0.01f ? toBoss / dist : Vector2.Zero;

            if (isWoF)
            {
                // Kite along the wall's sweep direction. The wall NPC's
                // position IS the mouth (the visual wall is cosmetic span) —
                // measure lead from the mouth itself. Bands: sprint when it
                // closes in, cruise to hold, stand-and-shoot when far,
                // approach when starving (hill blocking line-of-sight).
                float sweep = boss.velocity.X >= 0 ? 1f : -1f;
                float lead = (pos.X - boss.Center.X) * sweep; // tiles ahead of the mouth
                long starved = Main.GameUpdateCount - lastDamageTick;
                if (lead < 14f * 16) moveX = sweep * 2f;                    // open the gap
                else if (starved > 120 && lead > 10f * 16) moveX = -sweep * 0.8f; // LoS blocked: close in
                else if (lead > 20f * 16) moveX = -sweep * 0.5f;            // too far: walk back into star range
                else moveX = sweep * 0.6f;                                  // cruise

                NPC eye = NearestOfType(114, pos);
                // Aim at the nearest eye; it sits in the wall's face between
                // the hungries, which is also where beenades should land.
                Vector2 aimAt = eye != null ? eye.Center : boss.Center;
                Vector2 aimVel = eye != null ? eye.velocity : boss.velocity;
                Vector2 aim = aimAt + aimVel * (Vector2.Distance(pos, aimAt) / 14f);
                Main.mouseX = (int)(aim.X - Main.screenPosition.X);
                Main.mouseY = (int)(aim.Y - Main.screenPosition.Y);
                PressUse();
            }
            else
            {
                if (dist > PreferredRange + RangeSlack) { moveX += dir.X; moveY += dir.Y * 0.4f; }
                else if (dist < PreferredRange - RangeSlack) { moveX -= dir.X; moveY -= dir.Y * 0.4f; }

                // Attack: aim at the predicted intercept point and hold use.
                Vector2 aim = boss.Center + boss.velocity * (dist / 14f);
                Main.mouseX = (int)(aim.X - Main.screenPosition.X);
                Main.mouseY = (int)(aim.Y - Main.screenPosition.Y);
                PressUse();
            }
        }

        if (dodge != Vector2.Zero)
        {
            if (isWoF)
                // Never break the kite horizontally (backward = into the wall
                // = tongue/death). Lasers and Hungries are dodged vertically.
                moveY = dodge.Y * 1.5f;
            else
            {
                moveX = dodge.X * 1.8f;
                moveY = dodge.Y * 1.2f;
            }
        }

        // Quick-heal under half health.
        if (Player.statLife < Player.statLifeMax2 / 2 && Player.potionDelay <= 0)
            Player.QuickHeal();

        // --- Translate intent into native controls.
        Player.controlLeft = moveX < -0.2f;
        Player.controlRight = moveX > 0.2f;

        bool wantUp = moveY < -0.5f;
        bool wantDown = moveY > 0.6f;
        Player.controlDown = wantDown;

        bool blocked = (Player.controlLeft && vel.X > -1f) || (Player.controlRight && vel.X < 1f);
        stuckTicks = blocked && (Player.controlLeft || Player.controlRight) ? stuckTicks + 1 : 0;
        if (stuckTicks == 60 || stuckTicks == 180)
        {
            // ASCII cross-section around the player: # solid, = platform, . air.
            Point c = (Player.Center / 16f).ToPoint();
            var sb = new System.Text.StringBuilder();
            for (int dy = -9; dy <= 2; dy++)
            {
                for (int dx = -4; dx <= 5; dx++)
                {
                    Tile t = Framing.GetTileSafely(c.X + dx, c.Y + dy);
                    sb.Append(!t.HasTile ? '.' : Main.tileSolid[t.TileType] ? '#'
                        : Terraria.ID.TileID.Sets.Platforms[t.TileType] ? '=' : '+');
                }
                sb.Append('\n');
            }
            Mod.Logger.Info($"[BossPilot] stuck map @({c.X},{c.Y}) stuck={stuckTicks}:\n{sb}");
        }
        if (jumpCooldown > 0) jumpCooldown--;
        bool onGround = Player.velocity.Y == 0f;

        // Pit sense: running on the ground toward a gap → jump before the
        // edge.
        if (onGround && jumpCooldown == 0 && (Player.controlLeft || Player.controlRight))
        {
            // WoF kites one direction: probe far enough that Hermes-speed
            // approaches to pits register before the edge. Other fights keep
            // the short 44px probe.
            int dirX = Player.controlRight ? 1 : -1;
            int probePx = isWoF ? dirX * 72 : dirX * 44;
            Point ahead = ((Player.Bottom + new Vector2(probePx, 8f)) / 16f).ToPoint();
            bool groundAhead = false;
            for (int dy = 0; dy < 7 && !groundAhead; dy++)
            {
                Tile t = Framing.GetTileSafely(ahead.X, ahead.Y + dy);
                if (GroundLike(t)) groundAhead = true;
            }
            if (!groundAhead)
            {
                Player.controlJump = true;
                jumpCooldown = 25;
            }
        }
        if (placeCooldown > 0) placeCooldown--;

        // Vs the Wall of Flesh the pilot builds a FIXED-ALTITUDE hellbridge
        // as it runs (the human strategy, at Hermes speed). The bridge line
        // never ratchets downward, so the pilot stays on the open ash surface
        // instead of descending into hell's 3-tall ash tunnels where jumps,
        // stairs and hooks all fail.
        if (isWoF && BridgeLineY > 0 && (Player.controlLeft || Player.controlRight || Player.pulley || Player.wet))
        {
            int feetTile = (int)(Player.Bottom.Y / 16f);
            int dirX = Player.controlRight ? 1 : -1;
            int ownX = (int)(Player.Center.X / 16f);
            // Anywhere below the line with an open column: keep a platform
            // waiting at the line (it coexists with rope).
            if (feetTile > BridgeLineY + 2 && !Framing.GetTileSafely(ownX, BridgeLineY).HasTile
                && placeCooldown == 0 && TryPlacePlatform(ownX, BridgeLineY))
                placeCooldown = 6;

            bool deep = feetTile > BridgeLineY + 8;
            if (stuckTicks > 10 && !deep)
            {
                // Cliff face: step platforms at head height in our own column,
                // never above the tongue-safe band.
                Point step = ((Player.Bottom + new Vector2(0, -52f)) / 16f).ToPoint();
                step.Y = System.Math.Max(step.Y, Main.UnderworldLayer + 11);
                if (placeCooldown == 0 && !Framing.GetTileSafely(step.X, step.Y).HasTile
                    && TryPlacePlatform(step.X, step.Y))
                    placeCooldown = 4;
            }
            else
            {
                // Bridge ahead at the line (or just underfoot when terrain
                // rides higher than the line). NEVER build above UL+11:
                // jumping from a higher platform lifts the pilot into the
                // tongue zone (position.Y < UnderworldLayer*16) and gets it
                // dragged through tiles into the wall.
                int aheadX = (int)((Player.Bottom.X + dirX * 32f) / 16f);
                int by = System.Math.Clamp(System.Math.Min(feetTile + 2, BridgeLineY),
                    Main.UnderworldLayer + 11, BridgeLineY);
                if (placeCooldown == 0 && !Framing.GetTileSafely(aheadX, by).HasTile
                    && TryPlacePlatform(aheadX, by))
                {
                    placeCooldown = 2;
                    if (!bridgedOnce)
                    {
                        bridgedOnce = true;
                        Mod.Logger.Info($"[BossPilot] hellbridge started @({aheadX},{by}) line={BridgeLineY}");
                    }
                }
                if (onGround && jumpCooldown == 0)
                {
                    if (feetTile > BridgeLineY + 2 && feetTile - BridgeLineY <= 7)
                    {
                        // Shallow dip: hop onto the line platform.
                        Player.controlJump = true;
                        jumpCooldown = 25;
                    }
                    else if (deep)
                    {
                        // Deep in a valley: bunny-hop toward the kite side —
                        // covers ground and climbs small rises until the far
                        // wall triggers stairs.
                        Player.controlJump = true;
                        jumpCooldown = 18;
                    }
                }
            }
        }

        // WoF wall-crossing toolkit: when stairs and the double jump are not
        // enough (blocked hard for >25 ticks), fire the grappling hook at the
        // cliff face above-forward and let the pull carry the pilot over.
        // QuickGrapple finds the hook in inventory by itself — no slot switch.
        // The aim is capped below the tongue ceiling; release with a jump near
        // the latch point to keep the momentum.
        if (isWoF)
        {
            if (hookCooldown > 0) hookCooldown--;
            if (stuckTicks > 25 && hookCooldown == 0 && Player.grapCount == 0 && !Player.pulley)
            {
                float ty = System.Math.Max(Player.Center.Y - 170f, (Main.UnderworldLayer + 8) * 16f);
                Vector2 hookAt = new(Player.Center.X + wofDir * 60f, ty);
                Main.mouseX = (int)(hookAt.X - Main.screenPosition.X);
                Main.mouseY = (int)(hookAt.Y - Main.screenPosition.Y);
                Player.controlHook = true;
                hookCooldown = 45;
                Player.controlUseItem = false; // hook tick: don't waste a beenade into the hook aim
                if (!hookFiredOnce)
                {
                    hookFiredOnce = true;
                    Mod.Logger.Info($"[BossPilot] hook fire @({(int)(hookAt.X / 16f)},{(int)(hookAt.Y / 16f)}) stuck={stuckTicks}");
                }
            }
            else if (Player.grapCount > 0 && hookCooldown is > 0 and < 20)
                Player.controlJump = true; // release near the latch, keep momentum
        }

        // Vertical dodge beats everything: drop through platforms (down+jump)
        // or jump/double-jump (cloud bottle allows mid-air jumps).
        // Vs the Wall of Flesh the mid-air (cloud) jump is allowed only when
        // deep enough below the tongue ceiling: rising above
        // Main.UnderworldLayer while Horrified drags the player into the wall.
        bool emergencyJump = dodge != Vector2.Zero && wantUp;
        bool canJump = onGround || !isWoF || Player.position.Y > (Main.UnderworldLayer + 12) * 16f;
        if ((emergencyJump || (moveY < -0.5f && onGround) || stuckTicks > 15) && jumpCooldown == 0 && canJump)
        {
            Player.controlJump = true;
            jumpCooldown = emergencyJump ? 8 : 20;
        }
        if (wantDown && dodge != Vector2.Zero)
            Player.controlJump = true; // platform drop-through uses down+jump

        // Hook/mount dashes, accessories etc. are untouched on purpose; the
        // loadout layer decides what the pilot is allowed to use.
        lastX = pos.X;
    }

    private NPC FindBoss()
    {
        NPC best = null;
        float bestDist = float.MaxValue;
        foreach (NPC n in Main.npc)
        {
            if (!n.active || !n.boss) continue;
            float d = Vector2.Distance(n.Center, Player.Center);
            if (d < bestDist) { bestDist = d; best = n; }
        }
        return best;
    }

    private static bool GroundLike(Tile t) =>
        t.HasTile && (Main.tileSolid[t.TileType] || Terraria.ID.TileID.Sets.Platforms[t.TileType]);

    /// <summary>Solid tile within `tiles` above the player's head.</summary>
    private bool RockAbove(int tiles)
    {
        int x = (int)(Player.Center.X / 16f);
        int y = (int)(Player.position.Y / 16f);
        for (int dy = 0; dy <= tiles; dy++)
        {
            Tile t = Framing.GetTileSafely(x, y - dy);
            if (t.HasTile && Main.tileSolid[t.TileType]) return true;
        }
        return false;
    }

    /// <summary>
    /// One-tick rope deployment: rope uses instantly (no swing) and extends
    /// up its own column. Switches back to the weapon next tick.
    /// </summary>
    private bool TryUseRope()
    {
        int slot = -1;
        for (int i = 0; i < 10; i++)
            if (Player.inventory[i].type == Terraria.ID.ItemID.Rope && Player.inventory[i].stack > 0) { slot = i; break; }
        if (slot < 0) return false;
        Player.selectedItem = slot;
        Main.mouseX = (int)(Player.Center.X + 8f - Main.screenPosition.X);
        Main.mouseY = (int)(Player.Center.Y - 32f - Main.screenPosition.Y);
        Player.controlUseItem = true;
        return true;
    }

    /// <summary>
    /// Places one Wood Platform at the tile via WorldGen (deterministic —
    /// native item-use placement races weapon fire in the same frame) and
    /// consumes it from the hotbar.
    /// </summary>
    private bool TryPlacePlatform(int tx, int ty)
    {
        int slot = -1;
        for (int i = 0; i < 10; i++)
            if (Player.inventory[i].type == Terraria.ID.ItemID.WoodPlatform && Player.inventory[i].stack > 0) { slot = i; break; }
        if (slot < 0) return false;
        if (!WorldGen.InWorld(tx, ty, 1)) return false;
        if (!WorldGen.PlaceTile(tx, ty, Terraria.ID.TileID.Platforms, mute: true, forced: true, style: 0))
            return false;
        Player.inventory[slot].stack--;
        return true;
    }

    /// <summary>
    /// Holds fire for autoReuse weapons and pulses a click edge for manual
    /// ones (beenades, thrown items): without the edge, ItemCheck fires them
    /// exactly once.
    /// </summary>
    private void PressUse()
    {
        Player.controlUseItem = Player.itemAnimation > 0 || Main.GameUpdateCount % 2 == 0;
    }

    private NPC NearestOfType(int type, Vector2 pos)
    {
        NPC best = null;
        float bestDist = float.MaxValue;
        foreach (NPC n in Main.npc)
        {
            if (!n.active || n.type != type) continue;
            float d = Vector2.Distance(n.Center, pos);
            if (d < bestDist) { bestDist = d; best = n; }
        }
        return best;
    }

    /// <summary>
    /// If the threat's trajectory passes within (radius + ThreatRadius) of the
    /// player within ThreatHorizon ticks, propose a perpendicular dodge.
    /// </summary>
    private void TryThreat(Vector2 at, Vector2 vel, float radius, ref Vector2 dodge, ref float worst, Vector2 pos)
    {
        float speed = vel.Length();
        if (speed < 0.01f) return;
        Vector2 toMe = pos - at;
        float tHit = Vector2.Dot(toMe, vel) / (speed * speed);
        if (tHit < 0 || tHit > ThreatHorizon) return;
        Vector2 closest = at + vel * tHit;
        float miss = Vector2.Distance(closest, pos);
        float need = ThreatRadius + radius;
        if (miss < need && miss < worst)
        {
            worst = miss;
            Vector2 perp = new(-vel.Y, vel.X);
            perp.Normalize();
            if (Vector2.Dot(perp, pos - closest) < 0) perp = -perp;
            dodge = perp;
        }
    }
}
