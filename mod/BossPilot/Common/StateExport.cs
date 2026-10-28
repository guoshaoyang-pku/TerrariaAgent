using System;
using System.IO;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;

namespace BossPilot.Common;

/// <summary>
/// Periodic JSONL snapshot of the trial-relevant state: player, the tracked
/// boss and its parts, and hostile projectiles. Written to
/// &lt;ModLoader folder&gt;/BossPilot/runs/&lt;scenario&gt;-&lt;tick0&gt;.jsonl.
/// </summary>
public sealed class StateExport : ModSystem
{
    public static StateExport Instance => ModContent.GetInstance<StateExport>();

    private StreamWriter writer;
    private string runPath;

    public bool Active => writer != null;
    public int TicksSinceOpen { get; private set; }

    public void Begin(string scenarioName)
    {
        End();
        string dir = Path.Combine(ModLoader.ModPath, "BossPilot", "runs");
        Directory.CreateDirectory(dir);
        runPath = Path.Combine(dir, $"{scenarioName}-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");
        writer = new StreamWriter(runPath);
        TicksSinceOpen = 0;
    }

    public void End()
    {
        writer?.Dispose();
        writer = null;
    }

    public override void Unload() => End();

    public override void PostUpdateEverything()
    {
        if (writer == null) return;
        if (++TicksSinceOpen % 10 != 0) return; // 6 Hz snapshots

        Player p = Main.LocalPlayer;
        var payload = new
        {
            tick = (int)Main.GameUpdateCount,
            player = new
            {
                x = p.Center.X / 16f, y = p.Center.Y / 16f,
                vx = p.velocity.X, vy = p.velocity.Y,
                hp = p.statLife, hp_max = p.statLifeMax2,
                dead = p.dead,
            },
            boss = TrialRunner.Instance.ActiveBossJson(),
            projectiles = CountHostileProjectiles(),
        };
        writer.WriteLine(JsonSerializer.Serialize(payload));
    }

    private static int CountHostileProjectiles()
    {
        int n = 0;
        foreach (Projectile proj in Main.projectile)
            if (proj.active && proj.hostile) n++;
        return n;
    }

    public override void OnWorldUnload() => End();
}
