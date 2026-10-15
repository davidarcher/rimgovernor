using System.Diagnostics;
using System.Text.Json;
using RimGovernor.RealCombat;

namespace RimGovernor.RealCombat.Host;

/// <summary>
/// <c>RimGovernor.RealCombat --game &lt;dir&gt; --request &lt;json&gt; --out &lt;json&gt;</c>: boots the game's data in this
/// process, fights the request's arena once per seed and writes the outcomes. Stdout belongs to the game's log, so the
/// result goes to the file.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var protocol = Console.Out;
        Console.SetOut(Console.Error);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        string? game = null, requestPath = null, outPath = null, worker = null;
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--worker": worker = args[i + 1]; break;
                case "--game":
                    game = args[i + 1];
                    break;
                case "--request":
                    requestPath = args[i + 1];
                    break;
                case "--out":
                    outPath = args[i + 1];
                    break;
                default:
                    return Usage($"unknown argument {args[i]}");
            }
        }

        if (worker == "stdio" && game is not null) return RunWorker(game, protocol);

        if (game is null || requestPath is null || outPath is null)
        {
            return Usage("--game, --request and --out are required");
        }

        try
        {
            var request = RequestJson.Read(File.ReadAllText(requestPath));
            var startup = Stopwatch.StartNew();
            GameHost.Boot(game);
            var arena = new Arena(request);
            var startupMs = startup.Elapsed.TotalMilliseconds;
            var fights = Stopwatch.StartNew();
            var outcomes = request.Seeds.Select(arena.Fight).ToList();
            var result = new RealFightResult
            {
                Name = request.Name,
                Outcomes = outcomes,
                FightMilliseconds = fights.Elapsed.TotalMilliseconds,
                StartupMilliseconds = startupMs,
                Readback = arena.Readback,
            };
            File.WriteAllText(outPath, JsonSerializer.Serialize(result, options));
            return 0;
        }
        catch (RealFightRejected rejected)
        {
            // No result file: a rejected request has no outcomes. Each problem names its request field.
            foreach (var problem in rejected.Problems)
            {
                Console.Error.WriteLine("realcombat: rejected: " + problem);
            }

            return 3;
        }
    }

    private static int RunWorker(string game, TextWriter protocol)
    {
        GameHost.Boot(game);
        var mods = EngineIdentity.Mods();
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        protocol.WriteLine(JsonSerializer.Serialize(new
        {
            kind = "ready", gameBuild = EngineIdentity.GameBuild, mods,
            engine = EngineIdentity.Runtime(game) + ":" + EngineIdentity.FileHash(typeof(Program).Assembly.Location),
        }, options));
        protocol.Flush();
        // Bounded frames and effort protect a local caller too. The adapter can choose smaller budgets.
        var requests = 0;
        while (ReadFrame(Console.In, 1024 * 1024) is { } line)
        {
            string name = "";
            try
            {
                var request = RequestJson.Read(line);
                name = request.Name;
                if (++requests > 8)
                {
                    protocol.WriteLine(JsonSerializer.Serialize(new { kind = "failed", name, reason = "worker_lifetime_arena_budget" }, options));
                    protocol.Flush();
                    return 4;
                }
                if (request.Seeds.Count is < 1 or > 64 || request.MaxTicks is < 1 or > 60000 ||
                    request.Width is < 1 or > 128 || request.Height is < 1 or > 128 || request.Pawns.Count > 32)
                    throw new RealFightRejected(["worker: effort budget exceeded"]);
                var watch = Stopwatch.StartNew();
                var arena = new Arena(request);
                var construction = watch.Elapsed.TotalMilliseconds;
                watch.Restart();
                var outcomes = request.Seeds.Select(arena.Fight).ToArray();
                var result = new RealFightResult
                {
                    Name = name, Outcomes = outcomes, StartupMilliseconds = construction,
                    FightMilliseconds = watch.Elapsed.TotalMilliseconds, Readback = arena.Readback,
                };
                var frame = JsonSerializer.Serialize(new { kind = "result", name, result }, options);
                if (frame.Length > 1024 * 1024) throw new InvalidDataException("worker_result_frame_budget");
                protocol.WriteLine(frame);
            }
            catch (RealFightRejected rejected)
            {
                protocol.WriteLine(JsonSerializer.Serialize(new { kind = "rejected", name, reason = rejected.Message }, options));
            }
            catch (Exception exception)
            {
                // A failed arena is not reused. The owner receives the reason and must explicitly request another process.
                protocol.WriteLine(JsonSerializer.Serialize(new { kind = "failed", name, reason = exception.Message }, options));
                protocol.Flush();
                return 4;
            }
            protocol.Flush();
        }
        return 0;
    }

    private static string? ReadFrame(TextReader input, int maximum)
    {
        var frame = new System.Text.StringBuilder();
        while (input.Read() is var c && c >= 0)
        {
            if (c == '\n') return frame.ToString();
            if (frame.Length == maximum) throw new InvalidDataException("worker frame budget exceeded");
            frame.Append((char)c);
        }
        return null;
    }
    private static int Usage(string problem)
    {
        Console.Error.WriteLine("realcombat: " + problem);
        Console.Error.WriteLine("usage: RimGovernor.RealCombat --game <game dir> --request <request.json> --out <result.json>");
        return 2;
    }
}
