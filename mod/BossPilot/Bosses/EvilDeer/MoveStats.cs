using Terraria;
using Terraria.ModLoader;

namespace BossPilot.Bosses.EvilDeer;

/// <summary>
/// Caches the pilot's effective movement stats. Player.Update resets maxRunSpeed, accRunSpeed,
/// runAcceleration, runSlowdown, gravity, maxFallSpeed and the static jumpSpeed/jumpHeight to
/// base values before SetControls runs, and equipment re-applies them later in the same update;
/// PostUpdateRunSpeeds sees the final values, right before HorizontalMovement uses them.
/// </summary>
public sealed class MoveStats : ModPlayer
{
    public static bool Valid;
    public static float MaxRun, AccRun, RunAccel, RunSlow, JumpSpeed, Gravity, MaxFall;
    public static int JumpHeight;

    public override void PostUpdateRunSpeeds()
    {
        if (Player.whoAmI != Main.myPlayer) return;
        MaxRun = Player.maxRunSpeed;
        AccRun = Player.accRunSpeed;
        RunAccel = Player.runAcceleration;
        RunSlow = Player.runSlowdown;
        JumpSpeed = Player.jumpSpeed;
        JumpHeight = Player.jumpHeight;
        Gravity = Player.gravity;
        MaxFall = Player.maxFallSpeed;
        Valid = true;
    }
}
