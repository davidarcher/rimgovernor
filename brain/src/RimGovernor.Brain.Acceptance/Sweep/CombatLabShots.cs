using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Simulation.CombatMetrics;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The shot tap rows a combat run records (Go <c>shots.go</c>): one JSON object per line, in game order, each tagged by
/// "k". test/lab_stage action shots replies the rows; the dataset under calibration/shots stamps each with its run seed.
/// </summary>
internal static class CombatLabShots
{
    /// <summary>The bundle file next to the reads file holding the shot tap (scripts/fixtures/DebugStartFixture.cs, LabShotLedger; #2917).</summary>
    internal const string ShotsFile = "combat_shots.jsonl";

    // The fields of each row kind, in the order the Go structs encode them: s string, i int, f float, b bool, a int array.
    private static readonly Dictionary<string, (string Name, char Type)[]> Kinds = new(StringComparer.Ordinal)
    {
        ["shot"] = [("seed", 'i'), ("k", 's'), ("tick", 'i'), ("id", 'i'), ("shooter", 's'), ("side", 's'), ("x", 'i'), ("z", 'i'), ("target", 's'), ("tx", 'i'), ("tz", 'i'), ("dx", 'i'), ("dz", 'i'), ("burst", 'i'), ("of", 'i'), ("weapon", 's'), ("proj", 's'), ("tti", 'i')],
        ["impact"] = [("seed", 'i'), ("k", 's'), ("tick", 'i'), ("id", 'i'), ("hit", 's'), ("x", 'i'), ("z", 'i'), ("dealt", 'f')],
        ["job"] = [("seed", 'i'), ("k", 's'), ("tick", 'i'), ("pawn", 's'), ("side", 's'), ("job", 's'), ("forced", 'b'), ("thing", 's'), ("x", 'i'), ("z", 'i')],
        ["stance"] = [("seed", 'i'), ("k", 's'), ("tick", 'i'), ("pawn", 's'), ("side", 's'), ("stance", 's'), ("left", 'i'), ("thing", 's'), ("x", 'i'), ("z", 'i')],
        ["draft"] = [("seed", 'i'), ("k", 's'), ("tick", 'i'), ("pawn", 's'), ("drafted", 'b')],
        ["think"] = [("seed", 'i'), ("k", 's'), ("pawn", 's'), ("ticks", 'a'), ("count", 'i')],
    };

    /// <summary>
    /// Saves the rows of a test/lab_stage shots reply to the bundle, one line each. A reply with dropped rows (the tap is
    /// bounded) is an error: a truncated ledger would read as fewer shots.
    /// </summary>
    internal static async Task WriteShotsAsync(string dir, JsonObject reply, CancellationToken cancellationToken)
    {
        var dropped = (int)reply["shotsDropped"].NumberOrZero();
        if (dropped > 0)
        {
            throw new AcceptanceException($"shot tap dropped {SweepText.Tag(dropped)} rows");
        }

        var text = new StringBuilder();
        foreach (var row in reply["shots"].AsSlice())
        {
            _ = text.Append(row?.ToJsonString()).Append('\n');
        }

        await File.WriteAllTextAsync(Path.Combine(dir, ShotsFile), text.ToString(), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The damage ledger of a run bundle: the last read of its combat_reads.jsonl, which holds every hit since the staging.</summary>
    internal static async Task<IReadOnlyList<MetricsHit>> BundleDamageAsync(string dir, CancellationToken cancellationToken)
    {
        var data = await File.ReadAllTextAsync(Path.Combine(dir, CombatLabBundle.ReadsFile), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        var lines = data.Trim().Split('\n');
        try
        {
            var last = JsonNode.Parse(lines[^1])?.AsObject() ?? throw new AcceptanceException($"{CombatLabBundle.ReadsFile}: last line is not an object");
            return CombatLabBundle.ReadRows(last).Damage;
        }
        catch (System.Text.Json.JsonException e)
        {
            throw new AcceptanceException($"{CombatLabBundle.ReadsFile}: {e.Message}", e);
        }
    }

    /// <summary>
    /// Copies a bundle's shot rows to <paramref name="w"/> in order, each with the run seed added, and each impact with the
    /// damage its projectile dealt: the total of the damage ledger rows at the impact's tick whose victim is the thing hit
    /// and whose instigator is the shooter of the projectile. A row of an unknown kind or with an unknown field is an
    /// error, so a schema change fails the dataset build rather than reading as zeros.
    /// </summary>
    internal static void StampShots(TextWriter w, int seed, string bundle, IReadOnlyList<MetricsHit> damage)
    {
        var rows = new List<(string Kind, JsonObject Row)>();
        var line = 0;
        foreach (var raw in bundle.Split('\n'))
        {
            line++;
            if (raw.Trim().Length == 0)
            {
                continue;
            }

            JsonObject row;
            try
            {
                row = JsonNode.Parse(raw)?.AsObject() ?? throw new AcceptanceException($"line {SweepText.Tag(line)}: not an object");
            }
            catch (System.Text.Json.JsonException e)
            {
                throw new AcceptanceException($"line {SweepText.Tag(line)}: {e.Message}", e);
            }

            var kind = row["k"].AsString();
            if (!Kinds.TryGetValue(kind, out var fields))
            {
                throw new AcceptanceException($"line {SweepText.Tag(line)}: unknown kind \"{kind}\"");
            }

            foreach (var (name, _) in row)
            {
                if (!fields.Any(f => f.Name == name))
                {
                    throw new AcceptanceException($"line {SweepText.Tag(line)} ({kind}): unknown field \"{name}\"");
                }
            }

            rows.Add((kind, row));
        }

        var shooter = new Dictionary<int, string>();
        foreach (var (kind, row) in rows)
        {
            if (kind == "shot")
            {
                shooter[(int)row["id"].NumberOrZero()] = row["shooter"].AsString();
            }
        }

        foreach (var (kind, row) in rows)
        {
            var output = new JsonObject();
            foreach (var (name, type) in Kinds[kind])
            {
                output[name] = type switch
                {
                    's' => row[name].AsString(),
                    'b' => row[name].AsBool() == true,
                    'f' => row[name].NumberOrZero(),
                    'a' => new JsonArray([.. row[name].AsSlice().Select(v => (JsonNode?)JsonValue.Create((int)v.NumberOrZero()))]),
                    _ => (int)row[name].NumberOrZero(),
                };
            }

            output["seed"] = seed;
            if (kind == "impact" && output["hit"].AsString().Length > 0)
            {
                var dealt = output["dealt"].NumberOrZero();
                var id = (int)output["id"].NumberOrZero();
                var tick = (int)output["tick"].NumberOrZero();
                foreach (var h in damage)
                {
                    if (h.Tick == tick && h.Victim == output["hit"].AsString() && h.Instigator == shooter.GetValueOrDefault(id, ""))
                    {
                        dealt += h.Dealt;
                    }
                }

                output["dealt"] = dealt;
            }

            w.Write(output.ToJsonString());
            w.Write('\n');
        }
    }

    /// <summary>
    /// Writes the shot dataset of a sweep: for each seed the shot rows of the bundle directory <paramref name="caseDir"/>
    /// names, stamped by <see cref="StampShots"/>. A seed without a shot file is an error: the dataset covers every seed.
    /// </summary>
    internal static async Task BuildShotDatasetAsync(Stream stream, IReadOnlyList<int> seeds, Func<int, string> caseDir, CancellationToken cancellationToken)
    {
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true);
        writer.NewLine = "\n";
        foreach (var seed in seeds)
        {
            var dir = caseDir(seed);
            string data;
            try
            {
                data = await File.ReadAllTextAsync(Path.Combine(dir, ShotsFile), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException e)
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"seed {seed}: {e.Message}"), e);
            }

            try
            {
                var damage = await BundleDamageAsync(dir, cancellationToken).ConfigureAwait(false);
                StampShots(writer, seed, data, damage);
            }
            catch (IOException e)
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"seed {seed}: {e.Message}"), e);
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"seed {seed}: {e.Message}"), e);
            }
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
