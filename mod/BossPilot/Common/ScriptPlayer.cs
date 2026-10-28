using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Terraria;
using Terraria.ModLoader;

namespace BossPilot.Common;

/// <summary>
/// Replays a per-tick input script onto the local player. Script JSON is an
/// array of frames; each frame may set movement keys, item use, and the mouse
/// position in tile units relative to spawn. Frames hold until replaced.
/// This is the script-execution half of the Spec's lower layer; the planner
/// that writes these scripts lives outside the mod.
/// </summary>
public sealed class ScriptPlayer : ModPlayer
{
    public sealed class Frame
    {
        [JsonPropertyName("t")] public int Tick { get; init; }
        [JsonPropertyName("left")] public bool Left { get; init; }
        [JsonPropertyName("right")] public bool Right { get; init; }
        [JsonPropertyName("up")] public bool Up { get; init; }
        [JsonPropertyName("down")] public bool Down { get; init; }
        [JsonPropertyName("jump")] public bool Jump { get; init; }
        [JsonPropertyName("use_item")] public bool UseItem { get; init; }
        [JsonPropertyName("mouse_x")] public float? MouseX { get; init; }
        [JsonPropertyName("mouse_y")] public float? MouseY { get; init; }
        [JsonPropertyName("slot")] public int? Slot { get; init; }
    }

    public static bool Loaded { get; private set; }
    private static List<Frame> frames = [];
    private static int cursor;
    private static Frame current;

    public static void Load(string path)
    {
        frames = JsonSerializer.Deserialize<List<Frame>>(File.ReadAllText(path)) ?? [];
        frames.Sort((a, b) => a.Tick.CompareTo(b.Tick));
        cursor = 0;
        current = null;
        Loaded = true;
    }

    public static void Reset()
    {
        Loaded = false;
        frames = [];
        current = null;
    }

    public override void Unload() => Reset();

    public override void PreUpdate()
    {
        if (!Loaded || AutoPilot.Enabled || !TrialRunner.Instance.Current.Equals(TrialRunner.Phase.Fighting)) return;

        int tick = StateExport.Instance.TicksSinceOpen;
        while (cursor < frames.Count && frames[cursor].Tick <= tick)
            current = frames[cursor++];
        if (current == null) return;

        Player.controlLeft = current.Left;
        Player.controlRight = current.Right;
        Player.controlUp = current.Up;
        Player.controlDown = current.Down;
        Player.controlJump = current.Jump;
        Player.controlUseItem = current.UseItem;
        if (current.Slot is int slot && slot >= 0 && slot < 10)
            Player.selectedItem = slot;
        if (current.MouseX is float mx && current.MouseY is float my)
        {
            Main.mouseX = (int)(mx * 16 - Main.screenPosition.X);
            Main.mouseY = (int)(my * 16 - Main.screenPosition.Y);
        }
    }
}
