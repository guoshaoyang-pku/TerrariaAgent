using System;
using System.IO;
using Terraria;
using Terraria.IO;
using Terraria.ModLoader;

namespace BossPilot.Common;

/// <summary>
/// Zero-click trial bootstrap. When the game is sitting at the main menu and
/// &lt;ModLoader&gt;/BossPilot/bootstrap.flag exists, this creates a pilot
/// character and a fresh world entirely in code, then enters the world — where
/// AutorunSystem picks up the scenario and starts the trial. The flag file
/// holds an optional seed on its first line; the file is renamed to
/// bootstrap.done.flag so a session bootstraps at most once.
/// </summary>
public sealed class MenuBootstrap : ModSystem
{
    private bool attempted;

    public override void Load()
    {
        // ModSystem.PostUpdateEverything only ticks while a world is loaded;
        // hook DoUpdate directly so the bootstrap can fire at the main menu.
        On_Main.DoUpdate += HookDoUpdate;
    }

    private void HookDoUpdate(On_Main.orig_DoUpdate orig, Main self, ref Microsoft.Xna.Framework.GameTime gameTime)
    {
        orig(self, ref gameTime);
        TryBootstrap();
    }

    private int debugTicks;

    private void TryBootstrap()
    {
        if (!attempted && ++debugTicks % 300 == 0)
            Mod.Logger.Info($"[BossPilot] menu probe: dir={AutorunSystem.Dir} gameMenu={Main.gameMenu} menuMode={Main.menuMode} flag={File.Exists(Path.Combine(AutorunSystem.Dir, "bootstrap.flag"))}");
        if (attempted || !Main.gameMenu || Main.menuMode != 0) return;
        string flag = Path.Combine(AutorunSystem.Dir, "bootstrap.flag");
        if (!File.Exists(flag)) return;
        attempted = true;
        try
        {
            string seed = File.ReadAllText(flag).Trim();
            File.Move(flag, Path.Combine(AutorunSystem.Dir, "bootstrap.done.flag"), overwrite: true);
            Bootstrap(seed.Length > 0 ? seed : "1.1.1.12345");
        }
        catch (Exception e)
        {
            Mod.Logger.Error($"[BossPilot] bootstrap failed: {e}");
        }
    }

    private void Bootstrap(string seed)
    {
        Mod.Logger.Info($"[BossPilot] bootstrap: creating player+world, seed {seed}");

        string worldFile = Path.Combine(Main.WorldPath, "PilotLab.wld");
        if (!File.Exists(worldFile))
        {
            // In-process generation deadlocks at the menu on this tML build.
            // Generate once headlessly instead:
            //   ./start-tModLoaderServer.sh -autocreate 1 -seed <seed> -autoshutdown -noupnp
            // then rename Worlds/World.wld(.twld) to PilotLab.wld(.twld).
            Mod.Logger.Error("[BossPilot] PilotLab.wld missing; create it with start-tModLoaderServer.sh -autocreate 1 and retry");
            return;
        }

        Player player = new Player { name = "Pilot", difficulty = 0 };
        // Mirror Player.LoadPlayer: the creative-powers per-player store must
        // exist and be reset, or Spawn crashes in GodmodePower with an NRE.
        player.savedPerPlayerFieldsThatArentInThePlayerClass = new Player.SavedPlayerDataWithAnnoyingRules();
        Terraria.GameContent.Creative.CreativePowerManager.Instance.ResetDataForNewPlayer(player);
        PlayerFileData playerData = PlayerFileData.CreateAndSave(player);
        Main.ActivePlayerFileData = playerData;
        Main.player[Main.myPlayer] = playerData.Player;

        Main.ActiveWorldFileData = WorldFile.GetAllMetadata(worldFile, false);
        if (Main.ActiveWorldFileData == null)
        {
            Mod.Logger.Error("[BossPilot] failed to read PilotLab.wld metadata");
            return;
        }

        Mod.Logger.Info("[BossPilot] bootstrap: entering PilotLab");
        WorldGen.playWorld();
    }
}
