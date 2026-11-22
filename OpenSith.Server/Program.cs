using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OpenSith.Core.AI;
using OpenSith.Core.Networking;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;

namespace OpenSith.Server;

public static class Program
{
    private const string DefaultSteamPath = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight";

    public static async Task<int> Main(string[] args)
    {
        string assetsDir = DefaultSteamPath;
        string? levelArg = null;
        string? episodeArg = null;
        int port = NetCodec.DefaultPort;
        int maxPlayers = 8;
        int tickRateHz = 30;
        int maxJediRank = 8;
        bool defaultAllWeapons = false;
        bool allowClientAllWeapons = true;
        int botCount = 0;
        BotSkill botSkill = BotSkill.Medium;
        bool botAllWeapons = false;
        var extraGobs = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (string.Equals(arg, "--level", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                levelArg = args[++i];
            else if (string.Equals(arg, "--episode", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                episodeArg = args[++i];
            else if (string.Equals(arg, "--assets", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                assetsDir = args[++i];
            else if (string.Equals(arg, "--gob", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                extraGobs.Add(args[++i]);
            else if (string.Equals(arg, "--port", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && int.TryParse(args[++i], out int parsedPort))
                port = Math.Clamp(parsedPort, 0, 65535);
            else if (string.Equals(arg, "--max-players", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && int.TryParse(args[++i], out int parsedMax))
                maxPlayers = Math.Clamp(parsedMax, 1, NetCodec.MaxPlayersPerSnapshot);
            else if (string.Equals(arg, "--tickrate", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && int.TryParse(args[++i], out int parsedHz))
                tickRateHz = Math.Clamp(parsedHz, 10, 120);
            else if (string.Equals(arg, "--max-rank", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && int.TryParse(args[++i], out int parsedRank))
                maxJediRank = Math.Clamp(parsedRank, 0, 8);
            else if (string.Equals(arg, "--all-weapons", StringComparison.OrdinalIgnoreCase))
                defaultAllWeapons = true;
            else if (string.Equals(arg, "--no-client-all-weapons", StringComparison.OrdinalIgnoreCase))
                allowClientAllWeapons = false;
            else if (string.Equals(arg, "--bots", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && int.TryParse(args[++i], out int parsedBots))
                botCount = Math.Max(0, parsedBots);
            else if (string.Equals(arg, "--bot-skill", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                botSkill = ParseBotSkill(args[++i]);
            else if (string.Equals(arg, "--bot-all-weapons", StringComparison.OrdinalIgnoreCase))
                botAllWeapons = true;
        }

        string res1Path = Path.Combine(assetsDir, "Resource", "Res1hi.gob");
        string res2Path = Path.Combine(assetsDir, "Resource", "Res2.gob");
        string jk1Path = Path.Combine(assetsDir, "Episode", "JK1.GOB");
        string jk1MpPath = Path.Combine(assetsDir, "Episode", "JK1MP.GOB");

        if (!File.Exists(res2Path))
        {
            Console.Error.WriteLine($"Error: Required Res2.gob not found at '{res2Path}'. Use --assets <dir> to specify Jedi Knight directory.");
            return 1;
        }

        string levelFile = NormalizeJklFileName(
            !string.IsNullOrWhiteSpace(levelArg)
                ? levelArg
                : (File.Exists(jk1MpPath) ? "m2.jkl" : "01narshadda.jkl"));

        string episodeGob = !string.IsNullOrWhiteSpace(episodeArg)
            ? episodeArg.Trim()
            : InferEpisodeGob(assetsDir, levelFile);

        using var vfs = new VirtualFileSystem();
        if (File.Exists(res1Path))
            vfs.Mount(GobArchive.FromFile(res1Path));
        vfs.Mount(GobArchive.FromFile(res2Path));
        if (File.Exists(jk1Path))
            vfs.Mount(GobArchive.FromFile(jk1Path));

        string episodePath = Path.IsPathRooted(episodeGob)
            ? episodeGob
            : Path.Combine(assetsDir, "Episode", episodeGob);
        if (File.Exists(episodePath) && !string.Equals(Path.GetFullPath(episodePath), Path.GetFullPath(jk1Path), StringComparison.OrdinalIgnoreCase))
            vfs.Mount(GobArchive.FromFile(episodePath));

        foreach (string gob in extraGobs)
        {
            if (File.Exists(gob))
                vfs.Mount(GobArchive.FromFile(gob));
            else
                Console.Error.WriteLine($"Warning: Extra GOB not found: '{gob}'");
        }

        string? jklPath = ResolveVfsPath(vfs, $"jkl/{levelFile}") ?? ResolveVfsPath(vfs, levelFile);
        if (jklPath == null)
        {
            Console.Error.WriteLine($"Error: Level '{levelFile}' not found in mounted archives (episode '{episodeGob}').");
            return 1;
        }

        string levelName = Path.GetFileNameWithoutExtension(levelFile);
        JklLevel level = JklReader.Parse(vfs.ReadAllText(jklPath), levelName);

        using var server = new DedicatedGameServer(
            level,
            vfs,
            Path.GetFileName(episodeGob),
            levelFile,
            port: port,
            maxPlayers: maxPlayers,
            tickRateHz: tickRateHz,
            maxJediRank: maxJediRank,
            defaultAllWeapons: defaultAllWeapons,
            allowClientAllWeapons: allowClientAllWeapons);

        int botsSpawned = 0;
        int clampedBots = Math.Clamp(botCount, 0, maxPlayers);
        for (int b = 0; b < clampedBots; b++)
        {
            if (server.AddBot(skill: botSkill, grantAllWeapons: botAllWeapons || defaultAllWeapons) >= 0)
                botsSpawned++;
        }

        Console.WriteLine("==================================================");
        Console.WriteLine("         OPENSITH - DEDICATED SERVER              ");
        Console.WriteLine("==================================================");
        Console.WriteLine($"Episode   : {Path.GetFileName(episodeGob)}");
        Console.WriteLine($"Level     : {levelFile} ({server.Simulation.GetAllPlayerSpawns().Count} spawn markers)");
        Console.WriteLine($"UDP Port  : {server.BoundPort}");
        Console.WriteLine($"Tick Rate : {server.TickRateHz} Hz | Max Players: {server.MaxPlayers}");
        Console.WriteLine($"Policy    : MaxJediRank={server.MaxJediRank} | DefaultAllWeapons={server.DefaultAllWeapons} | ClientAllWeapons={server.AllowClientAllWeapons}");
        Console.WriteLine($"Bots      : {botsSpawned} (Skill={botSkill}, AllWeapons={botAllWeapons || defaultAllWeapons})");
        Console.WriteLine($"COGs      : {server.Simulation.ActiveCogCount} | Dynamic Framed Things: {server.Simulation.FramedThingCount}");
        Console.WriteLine();
        Console.WriteLine("Connect clients with:");
        Console.WriteLine($"  dotnet run --project OpenSith.Client -- --connect 127.0.0.1:{server.BoundPort} --name Kyle");
        Console.WriteLine($"  dotnet run --project OpenSith.Client -- --connect 127.0.0.1:{server.BoundPort} --name Jan");
        Console.WriteLine();
        Console.WriteLine("Console commands: status | players | addbot [name] | kickbots | quit (or Ctrl+C)");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var commands = new ConcurrentQueue<string>();
        if (!Console.IsInputRedirected)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    while (!cts.IsCancellationRequested)
                    {
                        string? line = Console.ReadLine();
                        if (line == null)
                            break;
                        commands.Enqueue(line.Trim());
                    }
                }
                catch (IOException)
                {
                }
            });
        }

        Task serverLoop = server.RunAsync(cts.Token);
        try
        {
            while (!cts.IsCancellationRequested)
            {
                while (commands.TryDequeue(out string? cmd))
                {
                    if (cmd.Equals("quit", StringComparison.OrdinalIgnoreCase) || cmd.Equals("exit", StringComparison.OrdinalIgnoreCase))
                    {
                        cts.Cancel();
                        break;
                    }
                    if (cmd.Equals("status", StringComparison.OrdinalIgnoreCase) || cmd.Equals("players", StringComparison.OrdinalIgnoreCase))
                    {
                        var clients = server.GetConnectedClientsSnapshot();
                        Console.WriteLine($"[STATUS] Tick={server.ServerTick} Players={clients.Count}/{server.MaxPlayers} (Humans={server.ConnectedClientCount}, Bots={server.BotCount})");
                        foreach (var c in clients)
                            Console.WriteLine($"  #{c.PlayerSlot} {(c.IsBot ? "[BOT] " : "[UDP] ")}{c.Name} (Thing {c.PlayerThingId}) Team={c.Team} K={c.Kills} D={c.Deaths} S={c.Score} Pos={c.Position}");
                    }
                    else if (cmd.StartsWith("addbot", StringComparison.OrdinalIgnoreCase))
                    {
                        string customName = cmd.Length > 6 ? cmd[6..].Trim() : string.Empty;
                        int addedId = server.AddBot(
                            name: string.IsNullOrWhiteSpace(customName) ? null : customName,
                            skill: botSkill,
                            grantAllWeapons: botAllWeapons || defaultAllWeapons);
                        Console.WriteLine(addedId >= 0 ? $"Spawned bot as Thing {addedId}." : "Server is full; cannot add bot.");
                    }
                    else if (cmd.Equals("kickbots", StringComparison.OrdinalIgnoreCase))
                    {
                        int removed = server.RemoveAllBots();
                        Console.WriteLine($"Removed {removed} bot(s).");
                    }
                }

                await Task.Delay(50, cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }

        try
        {
            await serverLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        Console.WriteLine("OpenSith Dedicated Server stopped.");
        return 0;
    }

    private static BotSkill ParseBotSkill(string value) => value.Trim().ToLowerInvariant() switch
    {
        "easy" or "0" => BotSkill.Easy,
        "hard" or "2" => BotSkill.Hard,
        _ => BotSkill.Medium
    };

    private static string InferEpisodeGob(string assetsDir, string levelFile)
    {
        if (levelFile.StartsWith("m", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(Path.Combine(assetsDir, "Episode", "JK1MP.GOB")))
        {
            return "JK1MP.GOB";
        }
        if (levelFile.StartsWith("c", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(Path.Combine(assetsDir, "Episode", "JK1CTF.GOB")))
        {
            return "JK1CTF.GOB";
        }
        return "JK1.GOB";
    }

    private static string NormalizeJklFileName(string name)
    {
        string trimmed = Path.GetFileName(name.Trim());
        return trimmed.EndsWith(".jkl", StringComparison.OrdinalIgnoreCase) ? trimmed : $"{trimmed}.jkl";
    }

    private static string? ResolveVfsPath(IVirtualFileSystem vfs, string path)
    {
        if (vfs.FileExists(path))
            return path;
        string lower = path.ToLowerInvariant();
        return vfs.FileExists(lower) ? lower : null;
    }
}
