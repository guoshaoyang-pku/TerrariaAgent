using System.IO;
using Terraria;
using Terraria.ModLoader;

namespace BossPilot.Common;

/// <summary>
/// Chat commands:
///   /bosspilot start &lt;scenario-name&gt;   — prepare arena, spawn boss, run trial
///   /bosspilot abort                       — cancel the current trial
///   /bosspilot script &lt;path.json&gt;      — load an input script for the next fight
///   /bosspilot list                        — list known bosses
/// Scenario JSON is read from &lt;ModLoader folder&gt;/BossPilot/scenarios/.
/// </summary>
public sealed class BossPilotCommand : ModCommand
{
    public override string Command => "bosspilot";
    public override CommandType Type => CommandType.Chat;
    public override string Description => "Run boss-challenge scenarios";

    public override void Action(CommandCaller caller, string input, string[] args)
    {
        if (args.Length == 0) { caller.Reply(Description); return; }
        switch (args[0])
        {
            case "start" when args.Length >= 2:
                string path = Path.Combine(ModLoader.ModPath, "BossPilot", "scenarios", args[1] + ".json");
                if (!File.Exists(path)) { caller.Reply($"no scenario file: {path}"); return; }
                ScenarioConfig cfg = ScenarioConfig.Parse(File.ReadAllText(path));
                caller.Reply(TrialRunner.Instance.Start(cfg));
                break;
            case "abort":
                TrialRunner.Instance.Abort();
                caller.Reply("aborted");
                break;
            case "script" when args.Length >= 2:
                ScriptPlayer.Load(args[1]);
                caller.Reply("script loaded");
                break;
            case "list":
                caller.Reply(string.Join(", ", BossCatalog.Bosses.Keys));
                break;
            default:
                caller.Reply(Description);
                break;
        }
    }
}
