using System.IO;
using Terraria.ModLoader;

namespace BossPilot.Common;

/// <summary>
/// Hands-free trial start: when a world loads, if
/// &lt;ModLoader&gt;/BossPilot/autorun.json exists, parse it as a scenario and
/// start it. Lets trials run unattended — the only manual step is entering a
/// world once. The file is renamed to autorun.done.json after firing so it
/// does not loop on re-entry.
/// </summary>
public sealed class AutorunSystem : ModSystem
{
    public static string Dir => Path.Combine(ModLoader.ModPath, "BossPilot");

    public override void OnWorldLoad()
    {
        string path = Path.Combine(Dir, "autorun.json");
        if (!File.Exists(path)) return;
        try
        {
            ScenarioConfig cfg = ScenarioConfig.Parse(File.ReadAllText(path));
            File.Move(path, Path.Combine(Dir, "autorun.done.json"), overwrite: true);
            string msg = TrialRunner.Instance.Start(cfg);
            Mod.Logger.Info($"autorun: {msg}");
        }
        catch (System.Exception e)
        {
            Mod.Logger.Error($"autorun failed: {e.Message}");
        }
    }
}
