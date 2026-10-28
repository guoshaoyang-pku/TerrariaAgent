using System;
using Microsoft.Xna.Framework;
using Terraria;

namespace BossPilot.Bosses.EvilDeer;

/// <summary>
/// Forward model of an Eater of Worlds head (NPC.AI_006_Worms, type 13,
/// Classic: speed 10, acceleration 0.07). Inside tiles - or farther than
/// 1000 px from the player box - the head steers toward the player center
/// with per-axis acceleration; otherwise it is ballistic (gravity 0.11,
/// terminal 10) with small x corrections. Body segments trail the head's
/// path at 38 px spacing, so their future positions lie on the current chain.
/// </summary>
public static class WormPredictor
{
    public const float Speed = 10f, Accel = 0.07f;

    public static void Predict(NPC head, Player target, ArenaGrid g, Track track, int horizon)
    {
        Vector2 pos = head.position, vel = head.velocity;
        int w = head.width, h = head.height;
        Vector2 tgt = target.Center;
        // AI_006 snaps both endpoints to the 16 px grid before steering.
        float tx = (int)(tgt.X / 16f) * 16, ty = (int)(tgt.Y / 16f) * 16;
        Rectangle far = new((int)target.position.X - 1000, (int)target.position.Y - 1000, 2000, 2000);
        for (int t = 1; t <= horizon; t++)
        {
            bool inTiles = g.WormInTiles(pos.X, pos.Y, w, h);
            if (!inTiles && !new Rectangle((int)pos.X, (int)pos.Y, w, h).Intersects(far)) inTiles = true;
            float cx = (int)((pos.X + w * 0.5f) / 16f) * 16, cy = (int)((pos.Y + h * 0.5f) / 16f) * 16;
            float dx = tx - cx, dy = ty - cy;
            if (!inTiles)
            {
                vel.Y += 0.11f;
                if (vel.Y > Speed) vel.Y = Speed;
                if (Math.Abs(vel.X) + Math.Abs(vel.Y) < Speed * 0.4f)
                    vel.X += (vel.X < 0f ? -1f : 1f) * Accel * 1.1f;
                else if (vel.Y == Speed)
                {
                    if (vel.X < dx) vel.X += Accel;
                    else if (vel.X > dx) vel.X -= Accel;
                }
                else if (vel.Y > 4f)
                    vel.X += vel.X < 0f ? Accel * 0.9f : -Accel * 0.9f;
            }
            else
            {
                float dist = (float)Math.Sqrt(dx * dx + dy * dy);
                float ax = Math.Abs(dx), ay = Math.Abs(dy);
                if (dist < 1f) dist = 1f;
                float sx = dx * Speed / dist, sy = dy * Speed / dist;
                if ((vel.X > 0f && sx > 0f) || (vel.X < 0f && sx < 0f) || (vel.Y > 0f && sy > 0f) || (vel.Y < 0f && sy < 0f))
                {
                    if (vel.X < sx) vel.X += Accel; else if (vel.X > sx) vel.X -= Accel;
                    if (vel.Y < sy) vel.Y += Accel; else if (vel.Y > sy) vel.Y -= Accel;
                    if (Math.Abs(sy) < Speed * 0.2f && ((vel.X > 0f && sx < 0f) || (vel.X < 0f && sx > 0f)))
                        vel.Y += vel.Y > 0f ? Accel * 2f : -Accel * 2f;
                    if (Math.Abs(sx) < Speed * 0.2f && ((vel.Y > 0f && sy < 0f) || (vel.Y < 0f && sy > 0f)))
                        vel.X += vel.X > 0f ? Accel * 2f : -Accel * 2f;
                }
                else if (ax > ay)
                {
                    if (vel.X < sx) vel.X += Accel * 1.1f; else if (vel.X > sx) vel.X -= Accel * 1.1f;
                    if (Math.Abs(vel.X) + Math.Abs(vel.Y) < Speed * 0.5f) vel.Y += vel.Y > 0f ? Accel : -Accel;
                }
                else
                {
                    if (vel.Y < sy) vel.Y += Accel * 1.1f; else if (vel.Y > sy) vel.Y -= Accel * 1.1f;
                    if (Math.Abs(vel.X) + Math.Abs(vel.Y) < Speed * 0.5f) vel.X += vel.X > 0f ? Accel : -Accel;
                }
            }
            pos += vel;
            track.Set(t, new Box(pos.X, pos.Y, w, h));
        }
    }
}
