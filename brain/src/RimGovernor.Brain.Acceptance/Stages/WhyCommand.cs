using System.Text.Json;

namespace RimGovernor.Brain.Acceptance;

/// <summary>The <c>acceptance why</c> subcommand.</summary>
public static class WhyCommand
{
    public const string Usage = """

          acceptance why <output>/<area>/<case> [-json]
        """;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>
    /// Prints the postmortem digest of a case output directory: the same digest a failed run writes to result.json
    /// ("diagnosis") and diagnosis.txt, recomputed from the evidence on disk so it can be re-read after a fix lands or
    /// against a peer's run.
    /// </summary>
    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        bool asJson = false;
        var dirs = new List<string>();
        bool flagsEnded = false;
        foreach (string a in args)
        {
            if (!flagsEnded && a.StartsWith('-') && a.Length > 1)
            {
                string flag = a.TrimStart('-');
                if (flag is "json" or "json=true")
                {
                    asJson = true;
                }
                else if (flag == "json=false")
                {
                    asJson = false;
                }
                else
                {
                    await stderr.WriteLineAsync($"flag provided but not defined: {a}\nusage: why <output>/<area>/<case> [-json]").ConfigureAwait(false);
                    return 2;
                }
            }
            else
            {
                if (a == "--")
                {
                    flagsEnded = true;
                    continue;
                }

                dirs.Add(a);
            }
        }

        if (dirs.Count == 0)
        {
            await stderr.WriteLineAsync("why needs a case output directory (the one holding result.json)").ConfigureAwait(false);
            return 2;
        }

        int exit = 0;
        foreach (string dir in dirs)
        {
            if (!File.Exists(Path.Combine(dir, "result.json")))
            {
                await stderr.WriteLineAsync($"{dir}: no result.json").ConfigureAwait(false);
                exit = 1;
                continue;
            }

            Digest digest = Postmortems.Collect(dir, null, cancellationToken);
            if (asJson)
            {
                await stdout.WriteLineAsync(digest.ToJson().ToJsonString(Indented)).ConfigureAwait(false);
                continue;
            }

            if (dirs.Count > 1)
            {
                await stdout.WriteLineAsync($"# {dir}").ConfigureAwait(false);
            }

            digest.Write(stdout);
        }

        return exit;
    }
}
