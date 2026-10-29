using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;

namespace BossPilot.Common;

/// <summary>
/// Trial state machine: build the arena at the plan's site, grant the
/// loadout, spawn the boss, run until it is killed, the pilot dies, it
/// despawns, or the scenario time limit elapses. Started by the chat command,
/// AutorunSystem (GUI) or JobRunner (headless server); per-tick control comes
/// from the boss plan, AutoPilot or ScriptPlayer.
/// </summary>
public sealed class TrialRunner : ModSystem
{
    public enum Phase { Idle, Preparing, Fighting, Finished }

    public static TrialRunner Instance => ModContent.GetInstance<TrialRunner>();

    // Ticks between arena build and boss spawn, so biome scans and landing settle.
    private const int SettleTicks = 45;

    public Phase Current { get; private set; } = Phase.Idle;
    public ScenarioConfig Scenario { get; private set; }
    public BossPlan Plan { get; private set; }
    public FightContext Context { get; private set; }
    public string Result { get; private set; }

    // Per-attempt metrics; Hits/DamageTaken/Kills are fed by TrialMetrics hooks.
    public int Hits { get; internal set; }
    public int DamageTaken { get; internal set; }
    public int Kills { get; private set; }
    public float BossHpFraction { get; private set; } = 1f;
    public int FightTicks => Context?.Tick ?? 0;

    private HashSet<int> trackedTypes = [];
    private int settleTicks;
    private bool bossSeen, bossDefeated;
    private int bossIndex = -1; // plan-less spawns only, for AutoPilot's WoF logging

    public string Start(ScenarioConfig scenario)
    {
        if (Current is Phase.Preparing or Phase.Fighting)
            return $"busy: phase {Current}";
        if (!BossCatalog.TryGet(scenario.Boss, out BossCatalog.BossEntry entry))
            return $"unknown boss: {scenario.Boss}";

        Scenario = scenario;
        Plan = BossPlan.For(scenario.Boss);
        Result = null;
        Hits = DamageTaken = Kills = 0;
        BossHpFraction = 1f;
        bossSeen = bossDefeated = false;
        trackedTypes = [entry.NpcId, .. entry.ExtraPartIds];

        // World stage follows the boss; one server runs many scenarios, so never inherit the last one's.
        // Plans may still override it in ArenaSite/GrantLoadout.
        Main.hardMode = entry.Hardmode;

        prepTicks = settleTicks = 0;
        prepared = false;
        Current = Phase.Preparing;
        // The headless server's pilot is already in the world. A GUI autorun starts at world load,
        // before the in-world player exists, and prepares from PostUpdateEverything instead.
        if (Main.dedServ) Prepare();
        return $"prepared {scenario.Name} ({entry.Name}{(Plan == null ? ", no plan" : "")})";
    }

    private int prepTicks;
    private bool prepared;

    /// <summary>
    /// Builds the arena, puts the pilot on it, grants the loadout and applies the
    /// scenario's world flags. A plan picks its own site and kit; without one,
    /// AutoPilot's biome sites are used.
    /// </summary>
    private void Prepare()
    {
        BossCatalog.BossEntry entry = BossCatalog.Bosses[Scenario.Boss];
        Player p = Main.LocalPlayer;
        hellStripY = -1;
        stripDumped = false;
        AutoPilot.BridgeLineY = -1;
        Point site;
        if (Plan != null)
        {
            site = Plan.ArenaSite(Scenario);
            ArenaBuilder.Build(Scenario.Arena, Scenario.Biome, site.ToWorldCoordinates());
            Plan.DecorateArena(site, Scenario);
            p.position = new Vector2(site.X * 16 - p.width / 2f, site.Y * 16 - p.height - 2);
            p.velocity = Vector2.Zero;
            p.fallStart = site.Y;
            Plan.GrantLoadout(p, Scenario);
        }
        else
            site = PrepareWithoutPlan(entry, p);
        AutoPilot.Enabled = Scenario.Controller == "auto";
        if (Scenario.Controller == "script" && !ScriptPlayer.Loaded)
            Main.NewText("[BossPilot] controller=script but no script loaded", Color.Yellow);
        ApplyWorldFlags(Scenario);
        ApplyBuffs(Scenario);
        Mod.Logger.Info($"[BossPilot] zones: jungle={p.ZoneJungle} corrupt={p.ZoneCorrupt} crimson={p.ZoneCrimson} hallow={p.ZoneHallow} snow={p.ZoneSnow} beach={p.ZoneBeach} dungeon={p.ZoneDungeon}");

        Context = new FightContext { Scenario = Scenario, Site = site };
        prepared = true;
    }

    /// <summary>AutoPilot's arena setup for a boss without a plan; returns the site tile.</summary>
    private Point PrepareWithoutPlan(BossCatalog.BossEntry entry, Player p)
    {
        Mod.Logger.Info($"[BossPilot] prepare: player#{p.GetHashCode()} active={p.active} hpMax(before)={p.statLifeMax}");
        LoadoutGiver.Grant(p, entry, Scenario.Loadout);
        Mod.Logger.Info($"[BossPilot] prepare: hpMax(after)={p.statLifeMax} weapon={p.inventory[0].type}");
        Vector2 site = BiomeSite(Scenario.Biome) ?? p.Center;
        if (Scenario.Biome == "underworld" && Scenario.Arena != "bridge" && entry.NpcId == 113)
            // Natural hell: fight where nature is flattest. Scans runway
            // windows around spawn and sets the wall's sweep direction.
            site = FindBestHellRunway();
        // Fixed-altitude hellbridge line for the Wall of Flesh: the prepped
        // strip surface (or the safe-band clamp of the start ground for the
        // bridge arena). -1 for other fights.
        AutoPilot.BridgeLineY = entry.NpcId == 113
            ? (hellStripY > 0 ? hellStripY : System.Math.Clamp((int)(site.Y / 16f) + 3, Main.UnderworldLayer + 12, Main.UnderworldLayer + 70))
            : -1;
        // Build the arena at the target site BEFORE moving the player onto it.
        ArenaBuilder.Build(Scenario.Arena, Scenario.Biome, site);
        if (ModLoader.HasMod("BossPilot"))
        {
            Point o = (site / 16f).ToPoint();
            Tile floor = Framing.GetTileSafely(o.X, o.Y + 2);
            Mod.Logger.Info($"[BossPilot] arena probe at ({o.X},{o.Y + 2}): hasTile={floor.HasTile} type={floor.TileType}");
        }
        if (site != p.Center)
        {
            // Stand on the arena surface: teleport into the air pocket above
            // the floor, never into the floor itself (the game ejects
            // embedded players out the bottom of the arena).
            Vector2 stand = site + new Vector2(0, -3 * 16f);
            p.Teleport(stand, 0);
            p.velocity = Vector2.Zero;
            p.fallStart = (int)(p.position.Y / 16f);
            Mod.Logger.Info($"[BossPilot] teleported for biome {Scenario.Biome} -> {stand / 16f}");
        }
        return (site / 16f).ToPoint();
    }

    private static int wofKiteDir = 1;
    private static bool stripDumped;

    private static int BeenadesLeft(Player p)
    {
        int n = 0;
        for (int i = 0; i < 58; i++)
            if (p.inventory[i].type == Terraria.ID.ItemID.Beenade) n += p.inventory[i].stack;
        return n;
    }

    /// <summary>
    /// Evaluates candidate natural runways (direction × start offset within
    /// ±400 tiles of spawn), scores cliffs + surface liquid over the next 320
    /// tiles, and returns the start of the flattest one. Sets wofKiteDir as a
    /// side effect so the wall spawns on the rough side.
    /// </summary>
    private static Vector2 FindBestHellRunway()
    {
        var scored = new System.Collections.Generic.List<(int score, int x, int dir)>();
        for (int dir = -1; dir <= 1; dir += 2)
        for (int off = 0; off <= 1800; off += 8)
        {
            int x0 = Main.spawnTileX + dir * off;
            if (x0 < 120 || x0 > Main.maxTilesX - 120) continue;
            if (x0 + dir * 400 < 150 || x0 + dir * 400 > Main.maxTilesX - 150) continue; // keep off the world edge
            int g = StandableY(x0);
            if (g >= Main.maxTilesY - 60) continue; // start itself must be runnable
            scored.Add((RunwayPenalty(x0, dir, 400), x0, dir));
        }
        if (scored.Count == 0)
        {
            wofKiteDir = 1;
            return new Vector2(Main.spawnTileX * 16f, (Main.UnderworldLayer + 35) * 16f);
        }
        scored.Sort();
        var (bestScore, bestX, bestDir) = scored[0];
        wofKiteDir = bestDir;
        int ground = StandableY(bestX);
        // The deck must sit near the BOTTOM of the wall's band: paved at
        // surface level it becomes a 5-tile armor wall shielding the
        // eyes/mouth below it (every projectile died against our own floor).
        // At UL+68 the pilot runs below the fight, LoS up at the eyes is
        // clear, and the tongue ceiling is 65 tiles away.
        int line = Main.UnderworldLayer + 68;
        PrepRunway(bestX - bestDir * 40, bestDir, line, 440);
        // Read back the strip: one column every 8 tiles, surface Y + type.
        var stripCheck = new System.Text.StringBuilder();
        for (int i = 0; i <= 56; i += 8)
        {
            int sx = bestX + bestDir * i;
            int sy = StandableY(sx);
            Tile t = Framing.GetTileSafely(sx, sy);
            stripCheck.Append($"{sx}:{sy}/{(t.HasTile ? t.TileType : -1)} ");
        }
        TrialRunner.Instance.Mod.Logger.Info($"[BossPilot] strip check: {stripCheck}");
        // Full map at Prepare for diffing against the mid-fight fall dump.
        DumpTileMap(bestX + bestDir * 40, ground - 5, "prep map");
        string top = string.Join("  ", scored.GetRange(0, System.Math.Min(5, scored.Count))
            .ConvertAll(s => $"{s.x}{(s.dir > 0 ? "R" : "L")}={s.score}"));
        // Surface profile of the chosen runway: one standable-Y per 16 tiles.
        var profile = new System.Text.StringBuilder();
        for (int i = 0; i <= 400; i += 16)
            profile.Append(StandableY(bestX + bestDir * i)).Append(' ');
        TrialRunner.Instance.Mod.Logger.Info(
            $"[BossPilot] hell runway: start={bestX} dir={(bestDir > 0 ? "right" : "left")} score={bestScore} ground={ground} | top5: {top}\n[BossPilot] runway profile: {profile}");
        hellStripY = line;
        return new Vector2(bestX * 16f, (line - 3) * 16f);
    }

    /// <summary>Prepped launch-strip surface, used as the WoF bridge line; -1 otherwise.</summary>
    private static int hellStripY = -1;

    /// <summary>ASCII tile map around (cx, cy) with absolute Y ruler.</summary>
    private static void DumpTileMap(int cx, int cy, string tag)
    {
        var log = TrialRunner.Instance.Mod.Logger;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"[BossPilot] {tag} @({cx},{cy}) UL={Main.UnderworldLayer}:");
        for (int dy = -12; dy <= 6; dy++)
        {
            sb.Append($"{cy + dy,4} ");
            for (int dx = -10; dx <= 12; dx++)
            {
                Tile t = Framing.GetTileSafely(cx + dx, cy + dy);
                sb.Append(!t.HasTile ? (t.LiquidAmount > 0 ? '~' : '.') : Main.tileSolid[t.TileType] ? '#'
                    : Terraria.ID.TileID.Sets.Platforms[t.TileType] ? '=' : '+');
            }
            sb.Append('\n');
        }
        log.Info(sb.ToString());
    }

    /// <summary>
    /// Paves len tiles of runway at fixed altitude lineY: cuts everything
    /// above the deck, fills the 5-tile deck itself (lava drained first),
    /// with direct tile writes so nothing refuses silently.
    /// </summary>
    private static void PrepRunway(int x0, int dir, int lineY, int len)
    {
        int placed = 0, cut = 0;
        for (int i = 0; i <= len; i++)
        {
            int x = x0 + dir * i;
            if (!WorldGen.InWorld(x, lineY, 12)) continue;
            // Headroom: 10 tiles above the deck, blunt direct clears — one
            // stray debris block is a step into the tongue zone, and KillTile
            // has too many silent refusal modes to trust here.
            for (int y = lineY - 10; y < lineY; y++)
            {
                Tile above = Main.tile[x, y];
                if (above.HasTile) { above.ClearTile(); cut++; }
                if (above.LiquidAmount > 0) above.LiquidAmount = 0;
            }
            for (int y = lineY; y <= lineY + 4; y++)
            {
                Tile t = Main.tile[x, y];
                if (t.LiquidAmount > 0) t.LiquidAmount = 0;
                if (t.HasTile) continue;
                t.ClearTile();
                t.HasTile = true;
                t.TileType = Terraria.ID.TileID.Ash;
                t.Slope = Terraria.ID.SlopeType.Solid;
                t.IsHalfBlock = false;
                WorldGen.SquareTileFrame(x, y);
                if (Main.tile[x, y].HasTile) placed++;
            }
        }
        TrialRunner.Instance.Mod.Logger.Info($"[BossPilot] runway prep: line={lineY} len={len} placed={placed} cut={cut}");
    }

    /// <summary>
    /// Roughness of a natural hell runway: UP-rises along the travel
    /// direction cost climb time; void columns (shafts, lava lakes, hills
    /// poking into the tongue zone) must be bridged or hooked — costly.
    /// </summary>
    private static int RunwayPenalty(int x, int dir, int len)
    {
        int prev = StandableY(x);
        int penalty = 0;
        for (int i = 8; i <= len; i += 4)
        {
            int sx = x + dir * i;
            int sy = StandableY(sx);
            if (sy >= Main.maxTilesY - 60) { penalty += 25; continue; } // unrunnable column
            int rise = prev - sy;
            if (rise > 3) penalty += rise * 4;
            prev = sy;
        }
        return penalty;
    }

    /// <summary>
    /// The open-air surface of hell: scan down from just below the tongue
    /// ceiling and take the first solid tile. Tunnel floors and shaft bottoms
    /// are never reached (the rim is found first). Columns whose open surface
    /// lies in the tongue zone, or under lava, return the void sentinel.
    /// </summary>
    private static int StandableY(int x)
    {
        for (int yy = Main.UnderworldLayer + 2; yy < Main.maxTilesY - 40; yy++)
        {
            Tile t = Framing.GetTileSafely(x, yy);
            if (!t.HasTile || !Main.tileSolid[t.TileType]) continue;
            if (yy < Main.UnderworldLayer + 12) return Main.maxTilesY - 40; // tongue zone
            if (Framing.GetTileSafely(x, yy - 1).LiquidAmount > 0) return Main.maxTilesY - 40; // lava lake
            return yy;
        }
        return Main.maxTilesY - 40; // shaft to bedrock
    }

    /// <summary>
    /// The world position a biome scenario is fought at. null = stay at spawn.
    /// </summary>
    private static Vector2? BiomeSite(string biome) => biome switch
    {
        // UnderworldLayer = maxTilesY - 200. The fight MUST happen below it:
        // WOFTongue() applies The Tongue (38) to any Horrified player whose
        // position.Y is above the underworld layer and drags them through
        // tiles to the mouth. The wall body itself is clamped to the band
        // [UnderworldLayer+10, UnderworldLayer+80], mouth near the middle.
        "underworld" => new Vector2(Main.spawnTileX * 16f, (Main.UnderworldLayer + 35) * 16f),
        "ocean" => new Vector2(380 * 16f, Main.spawnTileY * 16f - 400),
        "space" => new Vector2(Main.spawnTileX * 16f, 120 * 16f),
        "underground_jungle" => FindUndergroundJungle(),
        _ => null,
    };

    /// <summary>
    /// Scans columns near spawn for real jungle (JungleGrass blocks), then
    /// returns a spot above the canopy. A bare mud pocket is NOT jungle —
    /// plantera enrages there.
    /// </summary>
    private static Vector2? FindUndergroundJungle()
    {
        for (int dx = 0; dx < 400; dx += 16)
        for (int y = (int)(Main.maxTilesY * 0.4); y < Main.maxTilesY - 260; y += 4)
        {
            int x = Main.spawnTileX + (dx % 2 == 0 ? dx / 2 : -(dx + 16) / 2);
            Tile t = Framing.GetTileSafely(x, y);
            if (t.HasTile && t.TileType == Terraria.ID.TileID.JungleGrass)
                return new Vector2(x * 16f, (y - 20) * 16f);
        }
        return new Vector2(Main.spawnTileX * 16f, Main.maxTilesY * 0.55f * 16f);
    }

    private void BeginFight()
    {
        BossCatalog.BossEntry entry = BossCatalog.Bosses[Scenario.Boss];
        Player p = Main.LocalPlayer;
        bossIndex = -1;
        if (Plan != null) Plan.SpawnBoss(p, Scenario);
        else
        {
            Vector2 at = p.Center + new Vector2(0, -300);
            if (entry.NpcId == 113)
                // The Wall of Flesh spawns beside the player and sweeps across the
                // world in the player's kite direction (chosen by runway scoring).
                // 30 tiles: inside the tongue leash, with room to spin Hermes up
                // to full speed before the mouth arrives.
                at = p.Center + new Vector2(-wofKiteDir * 30 * 16f, 0);
            int id = NPC.NewNPC(new Terraria.DataStructures.EntitySource_DebugCommand("BossPilot"),
                (int)at.X, (int)at.Y, entry.NpcId);
            bossIndex = id;
            if (entry.NpcId == 125)
                // The twins do NOT pair up from AI — the Mechanical Eye summons
                // Retinazer and Spazmatism as two separate spawns (Player.cs
                // 544-case), so replicate that here.
                NPC.NewNPC(new Terraria.DataStructures.EntitySource_DebugCommand("BossPilot"),
                    (int)at.X + 400, (int)at.Y - 200, 126);
        }
        // This runs after the tick's NPC pass counted spawn slots, so the next NPC.SpawnNPC would see
        // none taken (e.g. a Dungeon Guardian roll beside Skeletron). Credit the boss's slots now.
        foreach (NPC n in Main.npc)
            if (n.active && trackedTypes.Contains(n.type)) p.nearbyActiveNPCs += n.npcSlots;
        StateExport.Instance.Begin(Scenario.Name);
        Current = Phase.Fighting;
        Mod.Logger.Info($"[BossPilot] fight start: {Scenario.Name}, boss npc id {entry.NpcId}");
    }

    public override void PostUpdateEverything()
    {
        switch (Current)
        {
            case Phase.Preparing:
                // GUI autorun: wait ~1s after world entry for the player to finish spawning.
                if (!prepared)
                {
                    if (++prepTicks >= 60 && Main.LocalPlayer is { active: true, dead: false })
                        Prepare();
                    else if (prepTicks % 300 == 0)
                        Mod.Logger.Info($"[BossPilot] preparing: paused={Main.gamePaused} netMode={Main.netMode} playerActive={Main.LocalPlayer?.active} dead={Main.LocalPlayer?.dead}");
                }
                else if (++settleTicks >= SettleTicks) BeginFight();
                break;
            case Phase.Fighting:
                TickFight();
                break;
        }
    }

    private void TickFight()
    {
        Context.Tick++;
        long life = 0, lifeMax = 0;
        bool any = false;
        foreach (NPC n in Main.npc)
        {
            if (!n.active || !trackedTypes.Contains(n.type)) continue;
            any = true;
            life += Math.Max(n.life, 0);
            lifeMax += n.lifeMax;
        }
        if (any)
        {
            bossSeen = true;
            BossHpFraction = lifeMax > 0 ? (float)life / lifeMax : 0f;
        }

        if (Plan == null) LogAutoPilotState();

        if (Main.LocalPlayer.dead) Finish("death");
        else if (!any && Context.Tick > 2) Finish(!bossSeen ? "spawn_failed" : bossDefeated ? "victory" : "despawn");
        else if (Context.Tick / 60.0 >= Scenario.MaxDurationSeconds) Finish("timeout");
    }

    /// <summary>AutoPilot debugging for plan-less fights: kit, WoF parts, falls below the bridge line.</summary>
    private void LogAutoPilotState()
    {
        // Same locals the block had inside TickFight before it moved here.
        Player p = Main.LocalPlayer;
        BossCatalog.BossEntry entry = BossCatalog.Bosses[Scenario.Boss];
        if ((int)Main.GameUpdateCount % 600 == 0)
        {
            int stars = 0;
            for (int i = 0; i < 58; i++)
                if (p.inventory[i].type == Terraria.ID.ItemID.FallenStar) stars += p.inventory[i].stack;
            Mod.Logger.Info($"[BossPilot] tick: hp={p.statLife} item={p.HeldItem?.type} anim={p.itemAnimation} use={p.controlUseItem} stars={stars} beenades={BeenadesLeft(p)} potionSick={p.potionDelay}");
            if (bossIndex >= 0)
            {
                int eyes = 0, hungries = 0, mouths = 0;
                foreach (NPC n in Main.npc)
                {
                    if (!n.active) continue;
                    if (n.type == 114) eyes++;
                    else if (n.type == 115) hungries++;
                    else if (n.type == 113) mouths++;
                }
                Mod.Logger.Info($"[BossPilot] parts: wall={mouths} eyes={eyes} hungries={hungries} wallLife={Main.npc[bossIndex].life} wofIdx={Main.wofNPCIndex}");
                foreach (NPC n in Main.npc)
                    if (n.active && (n.type == 114 || n.type == 115))
                        Mod.Logger.Info($"[BossPilot]   part t={n.type} @({n.Center.X / 16f:F0},{n.Center.Y / 16f:F0}) real={n.realLife} life={n.life} active={n.active}");
            }
        }
        // One-shot tile dump when the pilot is falling below the bridge line:
        // reveals what the runway ACTUALLY looks like mid-fight.
        if (!stripDumped && p.velocity.Y > 6f && AutoPilot.BridgeLineY > 0 && p.Bottom.Y / 16f > AutoPilot.BridgeLineY + 3)
        {
            stripDumped = true;
            Point c = (p.Center / 16f).ToPoint();
            DumpTileMap(c.X, c.Y, $"fall dump line={AutoPilot.BridgeLineY}");
        }
        if ((int)Main.GameUpdateCount % 120 == 0)
        {
            Point feet = (p.Bottom / 16f).ToPoint();
            Tile t = Framing.GetTileSafely(feet.X, feet.Y);
            Mod.Logger.Info($"[BossPilot] feet probe ({feet.X},{feet.Y}): hasTile={t.HasTile} type={t.TileType} active={t.HasUnactuatedTile} py={p.Center.Y / 16f:F1}");
        }
    }

    internal void NoteKill(NPC npc)
    {
        if (Current != Phase.Fighting || !trackedTypes.Contains(npc.type)) return;
        Kills++;
        // The kernel's own defeat marker: only the last Twin and the last EoW segment die with boss set,
        // and parts (fists, hands, creepers) never have it. Killing a part and then losing the boss to
        // a despawn is not a victory.
        if (npc.boss) bossDefeated = true;
    }

    private void Finish(string result)
    {
        Result = result;
        if (result == "victory") BossHpFraction = 0f;
        StateExport.Instance.End();
        string line = $"[BossPilot] {Scenario.Name}: {result} in {FightTicks / 60.0:F1}s, hits {Hits}";
        Main.NewText(line, Color.Orange);
        Mod.Logger.Info(line);
        Current = Phase.Finished;
    }

    public void Abort()
    {
        StateExport.Instance.End();
        Current = Phase.Idle;
    }

    /// <summary>The live NPC of the boss's main type, if any.</summary>
    public NPC MainBoss()
    {
        if (Scenario == null) return null;
        int type = BossCatalog.Bosses[Scenario.Boss].NpcId;
        foreach (NPC n in Main.npc)
            if (n.active && n.type == type) return n;
        return null;
    }

    public object ActiveBossJson()
    {
        NPC n = MainBoss();
        if (n == null) return null;
        return new
        {
            id = n.type,
            x = n.Center.X / 16f, y = n.Center.Y / 16f,
            hp = n.life, hp_max = n.lifeMax,
            ai = n.ai.Clone(),
        };
    }

    public override void Unload() => BossPlan.Unload();

    private static void ApplyWorldFlags(ScenarioConfig scenario)
    {
        Main.dayTime = scenario.Time == "day";
        Main.time = scenario.Time == "day" ? 27000 : 0;
        switch (scenario.Weather)
        {
            case "rain":
                Main.raining = true; Main.StartRain();
                break;
            case "clear":
                Main.raining = false; Main.StopRain();
                break;
            case "sandstorm":
                // Only meaningful in the desert; scenarios must place the arena there.
                Terraria.GameContent.Events.Sandstorm.StartSandstorm();
                break;
            case "blizzard":
                // Blizzard = rain while in the snow biome; scenario must use biome "snow".
                Main.raining = true; Main.StartRain();
                break;
            case "wind":
                Main.windSpeedTarget = 0.8f;
                break;
        }
    }

    private static void ApplyBuffs(ScenarioConfig scenario)
    {
        if (scenario.Buffs != "standard") return;
        Player p = Main.LocalPlayer;
        int duration = (int)(scenario.MaxDurationSeconds * 60) + 3600;
        p.AddBuff(Terraria.ID.BuffID.Campfire, duration);
        p.AddBuff(Terraria.ID.BuffID.HeartLamp, duration);
        p.AddBuff(Terraria.ID.BuffID.Ironskin, duration);
        p.AddBuff(Terraria.ID.BuffID.Regeneration, duration);
        p.AddBuff(Terraria.ID.BuffID.Swiftness, duration);
        p.AddBuff(Terraria.ID.BuffID.Endurance, duration);
        p.AddBuff(Terraria.ID.BuffID.WellFed, duration);
        p.AddBuff(Terraria.ID.BuffID.Lifeforce, duration);
    }
}

/// <summary>Feeds hit and kill counts of the running attempt into TrialRunner.</summary>
public sealed class TrialMetricsPlayer : ModPlayer
{
    public override void OnHurt(Player.HurtInfo info)
    {
        TrialRunner t = TrialRunner.Instance;
        if (t.Current != TrialRunner.Phase.Fighting || Player.whoAmI != Main.myPlayer) return;
        t.Hits++;
        t.DamageTaken += info.Damage;
    }
}

public sealed class TrialMetricsNPC : GlobalNPC
{
    public override void OnKill(NPC npc) => TrialRunner.Instance.NoteKill(npc);
}
