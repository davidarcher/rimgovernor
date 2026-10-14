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
        Console.SetOut(Console.Error);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        string? game = null, requestPath = null, outPath = null;
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            switch (args[i])
            {
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

        if (game is null || requestPath is null || outPath is null)
        {
            return Usage("--game, --request and --out are required");
        }

        var request = JsonSerializer.Deserialize<RealFightRequest>(File.ReadAllText(requestPath), options)
            ?? throw new InvalidOperationException("realcombat: empty request");
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
        };
        File.WriteAllText(outPath, JsonSerializer.Serialize(result, options));
        return 0;
    }

    private static int Usage(string problem)
    {
        Console.Error.WriteLine("realcombat: " + problem);
        Console.Error.WriteLine("usage: RimGovernor.RealCombat --game <game dir> --request <request.json> --out <result.json>");
        return 2;
    }
}
