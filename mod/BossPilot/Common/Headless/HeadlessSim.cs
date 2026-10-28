using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.IO;
using Terraria.ModLoader;

namespace BossPilot.Common.Headless;

/// <summary>
/// Windowless trial host. On a dedicated server started with BOSSPILOT_JOB set,
/// the server loop's per-iteration callback drives Main.Update itself with
/// singleplayer semantics (netMode 0, myPlayer 0; dedServ stays true, so
/// nothing renders or plays sound) and no 60 Hz cap. Each server process is an
/// independent kernel: run one per core with scripts/run_trials.py.
/// </summary>
public sealed class HeadlessSim : ModSystem
{
    public static bool Active { get; private set; }
    public static string OutDir { get; private set; }
    public static long Ticks { get; private set; }

    // Wall-clock slice per server-loop iteration; keeps the hang watchdog fed.
    private const int SliceMs = 50;

    private static Action<GameTime> update;
    private static JobRunner job;
    private static StreamWriter exceptionLog;
    private static readonly HashSet<string> seenExceptions = [];
    private static readonly Dictionary<string, int> exceptionCounts = [];

    private static string JobPath => Main.dedServ ? Environment.GetEnvironmentVariable("BOSSPILOT_JOB") : null;

    public override void Load()
    {
        if (string.IsNullOrEmpty(JobPath)) return;
        // tML shows fatal errors in a native Cocoa dialog even on a dedicated server, which pops
        // over the desktop and blocks the process until clicked. Keep them on the console.
        MethodInfo box = typeof(Main).Assembly.GetType("Terraria.ModLoader.Engine.ErrorReporting")
            .GetMethod("MessageBoxShow", BindingFlags.Static | BindingFlags.NonPublic);
        MonoModHooks.Add(box, (Action<Action<string, bool>, string, bool>)((orig, message, fatal) =>
            Console.Error.WriteLine($"[BossPilot] suppressed {(fatal ? "fatal " : "")}error dialog: {message}")));

        // The server's net thread deactivates every player slot without a connected client, which
        // would remove the pilot (and make bosses despawn). Headless servers never have clients.
        On_Netplay.UpdateConnectedClients += orig => { };

        // Client-only visuals that netMode 0 now reaches but a server never loaded (sky, screen shaders).
        On_Player.ManageSpecialBiomeVisuals += (orig, self, biomeName, inZone, source) => { };
        On_Cloud.UpdateClouds += orig => { };    // reads cloud textures
        On_Cloud.addCloud += orig => { };
        Terraria.GameContent.On_ChromaHotkeyPainter.Update += (orig, self) => { };
        Terraria.On_Gore.Update += (orig, self) => { };
        // Damage numbers measure fonts; a throw here aborts Player.Hurt before life loss and immunity.
        // 100 = "no slot", what a server returns.
        Terraria.On_CombatText.NewText_Rectangle_Color_string_bool_bool += (orig, location, color, text, dramatic, dot) => 100;
        // Singleplayer saves the character on death/autosave from a thread-pool thread; the pilot has no
        // file, so the throw there is unhandled and aborts the process. Headless runs never persist.
        On_Player.SavePlayer += (orig, file, skipMapSave) => { };
        On_WorldGen.saveToonWhilePlaying += orig => { };
        On_WorldGen.saveAndPlay += orig => { };
        // Completion pops a UI toast; the conditions themselves are registered in EnterAsLocalPlayer.
        Terraria.Achievements.On_AchievementManager.AchievementCompleted += (orig, self, achievement) => { };
    }

    public override void PostSetupContent()
    {
        string jobPath = JobPath;
        if (string.IsNullOrEmpty(jobPath)) return;

        OutDir = Environment.GetEnvironmentVariable("BOSSPILOT_OUT") ?? Path.GetDirectoryName(jobPath);
        Directory.CreateDirectory(OutDir);
        job = JobRunner.Load(jobPath, OutDir);
        MethodInfo m = typeof(Main).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(GameTime)]);
        update = m.CreateDelegate<Action<GameTime>>(Main.instance);

        // Main.Update swallows exceptions, which would silently truncate ticks; log each new site once.
        exceptionLog = new StreamWriter(Path.Combine(OutDir, "exceptions.log")) { AutoFlush = true };
        AppDomain.CurrentDomain.FirstChanceException += LogException;
        Main.OnTickForThirdPartySoftwareOnly += Pump;
        Active = true;
    }

    public override void Unload()
    {
        if (!Active) return;
        Main.OnTickForThirdPartySoftwareOnly -= Pump;
        AppDomain.CurrentDomain.FirstChanceException -= LogException;
        exceptionLog?.Dispose();
        Active = false;
        update = null;
        job = null;
    }

    private static void Pump()
    {
        if (job.Done) return;
        try
        {
            if (!job.Started)
            {
                EnterAsLocalPlayer();
                job.Begin();
            }

            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < SliceMs && !job.Done)
            {
                // A client's camera follows its player; aiming and on-screen checks rely on it.
                Main.screenPosition = Main.LocalPlayer.Center - new Vector2(Main.screenWidth, Main.screenHeight) / 2f;
                // Player.Update reads input and calls ModPlayer.SetControls only while focused.
                Main.hasFocus = true;
                // Zone flags (jungle, corruption, snow...) come from SceneMetrics, which only the client's
                // lighting engine rescans (every 4 frames, LightingEngine ExportMetrics). Without it biome
                // bosses enrage or despawn.
                if (Ticks % 4 == 0) Main.LocalPlayer.ForceUpdateBiomes();
                update(new GameTime());
                Ticks++;
                job.AfterTick();
            }
        }
        catch (Exception e)
        {
            // An exception escaping this callback would crash the whole server; record it and exit instead.
            Console.Error.WriteLine($"[BossPilot] harness crash: {e}");
            job.Fail(e.GetType().Name + ": " + e.Message);
        }
        if (job.Done)
        {
            lock (seenExceptions)
                File.WriteAllText(Path.Combine(OutDir, "exception_counts.json"),
                    System.Text.Json.JsonSerializer.Serialize(exceptionCounts, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            Netplay.SaveOnServerExit = false;
            Netplay.Disconnect = true;
        }
    }

    private static void EnterAsLocalPlayer()
    {
        Main.netMode = 0;
        Main.myPlayer = 0;
        // Servers never create a lighting engine, but with netMode 0 every AddLight/Clear reaches it.
        // Legacy mode: AddLight is screen-bounded and capped at 2000, so it cannot grow without draws.
        Lighting.Initialize();
        Lighting.Mode = Terraria.Graphics.Light.LightMode.Retro;
        // The camera reads the view matrices; a viewport override keeps them off the (null) GraphicsDevice.
        var viewport = new Microsoft.Xna.Framework.Graphics.Viewport(0, 0, Main.screenWidth, Main.screenHeight);
        Main.GameViewMatrix = new Terraria.Graphics.SpriteViewMatrix(null);
        Main.GameViewMatrix.SetViewportOverride(viewport);
        Main.BackgroundViewMatrix = new Terraria.Graphics.SpriteViewMatrix(null);
        Main.BackgroundViewMatrix.SetViewportOverride(viewport);
        NullEffects.Register();
        // Last statement of Main.DoUpdate when netMode != 2 (RGB keyboard); its Update is a no-op hook below.
        Main.ChromaPainter = new Terraria.GameContent.ChromaHotkeyPainter();
        // Pickup popups measure fonts; a throw there aborts the rest of Player.Update (incl. ItemCheck).
        Main.showItemText = false;
        // AchievementsHelper dereferences Main.Achievements unguarded (running, mining, kills...);
        // the initializer only registers conditions when netMode != 2, which now holds.
        typeof(Main).GetField("_achievements", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(Main.instance, new Terraria.Achievements.AchievementManager());
        Terraria.Initializers.AchievementInitializer.Load();
        var data = new PlayerFileData(Path.Combine(Main.PlayerPath, "Pilot.plr"), false)
        {
            Name = "Pilot",
            // Normally set when a player file is loaded; Spawn reads it for journey-power state.
            Player = new Player { name = "Pilot", savedPerPlayerFieldsThatArentInThePlayerClass = new() },
        };
        data.SetAsActive();
        Player p = Main.LocalPlayer;
        p.whoAmI = 0;
        p.active = true;
        p.Spawn(PlayerSpawnContext.SpawningIntoWorld);
    }

    [ThreadStatic] private static bool inLogger;

    private static void LogException(object sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
    {
        if (inLogger) return;
        inLogger = true;
        try
        {
            // At first chance the exception's trace holds just the throwing frame; its IL offset pins the
            // statement (ilspycmd --il) even inside huge inlined methods like Main.DoUpdate.
            StackFrame top = new StackTrace(e.Exception, false).GetFrame(0);
            MethodBase m = top?.GetMethod();
            string site = $"{e.Exception.GetType().Name} @ {m?.DeclaringType?.Name}.{m?.Name}+IL_{top?.GetILOffset() ?? -1:X4}";
            lock (seenExceptions)
            {
                exceptionCounts[site] = exceptionCounts.GetValueOrDefault(site) + 1;
                if (seenExceptions.Count > 200 || !seenExceptions.Add(site)) return;
                // No file info: tML hooks source-line lookup, and that lookup itself throws here.
                exceptionLog?.WriteLine($"[tick {Ticks}] {site}: {e.Exception.Message}\n{new StackTrace(1, false)}\n");
            }
        }
        finally
        {
            inLogger = false;
        }
    }
}
