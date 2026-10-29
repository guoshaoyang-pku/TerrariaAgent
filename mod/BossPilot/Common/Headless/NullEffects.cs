using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria.Graphics.Effects;
using Terraria.Graphics.Shaders;

namespace BossPilot.Common.Headless;

/// <summary>
/// Server-side stand-ins for screen filters, skies and overlays. Vanilla update
/// code (biomes, weather, boss AI) looks these up and toggles them by name with
/// no dedServ guard; a server never registers them, so under netMode 0 every
/// lookup would throw and Main.Update would silently drop the rest of the tick.
/// Names are every literal the runtime kernel uses.
/// </summary>
internal static class NullEffects
{
    private static readonly string[] FilterNames =
    [
        "Blizzard", "BloodMoon", "CRT", "CrystalDestructionColor", "CrystalDestructionVortex", "CrystalWin",
        "Graveyard", "HeatDistortion", "MonolithMoonLord", "MonolithNebula", "MonolithSolar", "MonolithStardust",
        "MonolithVortex", "MoonLord", "MoonLordShake", "Nebula", "Sandstorm", "Sepia", "Solar", "Stardust",
        "Test", "Test2", "Test3", "Vortex", "WaterDistortion", "testInvert",
    ];

    private static readonly string[] SkyNames =
    [
        "Ambience", "Blizzard", "CreditsRoll", "Lantern", "Martian", "MonolithMoonLord", "MonolithNebula",
        "MonolithSolar", "MonolithStardust", "MonolithVortex", "MoonLord", "Nebula", "Party", "Sandstorm",
        "Slime", "Solar", "Stardust", "Vortex",
    ];

    private static readonly string[] OverlayNames = ["Blizzard", "Sandstorm"];

    public static void Register()
    {
        // Filter + ScreenShaderData only hold a Ref<Effect>; nothing touches it until a draw.
        foreach (string name in FilterNames)
            if (Filters.Scene[name] == null)
                Filters.Scene[name] = new Filter(new ScreenShaderData("FilterTower"));
        foreach (string name in SkyNames)
            if (SkyManager.Instance[name] == null)
                SkyManager.Instance[name] = new NullSky();
        foreach (string name in OverlayNames)
            if (Overlays.Scene[name] == null)
                Overlays.Scene[name] = new NullOverlay();
    }

    private sealed class NullSky : CustomSky
    {
        private bool active;
        public override void Update(GameTime gameTime) { }
        public override void Draw(SpriteBatch spriteBatch, float minDepth, float maxDepth) { }
        public override bool IsActive() => active;
        public override void Reset() => active = false;
        public override void Activate(Vector2 position, params object[] args) => active = true;
        public override void Deactivate(params object[] args) => active = false;
    }

    private sealed class NullOverlay : Overlay
    {
        public NullOverlay() : base(EffectPriority.VeryLow, RenderLayers.All) { }
        public override void Draw(SpriteBatch spriteBatch) { }
        public override void Update(GameTime gameTime) { }
        public override bool IsVisible() => false;
        public override void Activate(Vector2 position, params object[] args) => Mode = OverlayMode.Active;
        public override void Deactivate(params object[] args) => Mode = OverlayMode.Inactive;
    }
}
