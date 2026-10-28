using System;
using System.IO;
using BossPilot.Common.Headless;

namespace BossPilot.Bosses.EvilDeer;

/// <summary>Sparse per-attempt diagnostics for these plans (out/evildeer.log in headless runs).</summary>
public static class Trace
{
    private static StreamWriter log;

    public static void Line(string msg)
    {
        try
        {
            if (log == null)
            {
                string dir = HeadlessSim.OutDir;
                if (dir == null) return;
                log = new StreamWriter(Path.Combine(dir, "evildeer.log"), append: true) { AutoFlush = true };
            }
            log.WriteLine(msg);
        }
        catch (Exception)
        {
            // Diagnostics must never break a trial.
        }
    }
}
