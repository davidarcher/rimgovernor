using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using RimGovernor.Brain.Simulation.CombatMetrics;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The shot tap rows a combat run records: one JSON object per line, in game order, each tagged by
/// "k". test/lab_stage action shots replies the rows; the dataset under calibration/shots stamps each with its run seed.
/// </summary>
internal static class CombatLabShots
{
    /// <summary>The bundle file next to the reads file holding the shot tap (scripts/fixtures/DebugStartFixture.cs, LabShotLedger; #2917).</summary>
    internal const string ShotsFile = "combat_shots.jsonl";

    /// <summary>
    /// Saves the rows of a test/lab_stage shots reply to the bundle, one line each as the tap wrote it. A reply with
    /// dropped rows (the tap is bounded) is an error: a truncated ledger would read as fewer shots.
    /// </summary>
    internal static async Task WriteShotsAsync(string dir, Fx.LabShotsRead reply, CancellationToken cancellationToken)
    {
        if (reply.ShotsDropped > 0)
        {
            throw new AcceptanceException($"shot tap dropped {SweepText.Tag(reply.ShotsDropped)} rows");
        }

        var text = new StringBuilder();
        foreach (var row in reply.Shots)
        {
            _ = text.Append(row.GetRawText()).Append('\n');
        }

        await File.WriteAllTextAsync(Path.Combine(dir, ShotsFile), text.ToString(), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The damage ledger of a run bundle: the last read of its combat_reads.jsonl, which holds every hit since the staging.</summary>
    internal static async Task<IReadOnlyList<MetricsHit>> BundleDamageAsync(string dir, CancellationToken cancellationToken)
    {
        var data = await File.ReadAllTextAsync(Path.Combine(dir, CombatLabBundle.ReadsFile), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        var lines = data.Trim().Split('\n');
        return [.. CombatLabBundle.ParseRead(lines[^1]).Damage.Select(CombatLabBundle.Hit)];
    }

    /// <summary>
    /// Copies a bundle's shot rows to <paramref name="w"/> in order, each with the run seed added, and each impact with the
    /// damage its projectile dealt: the total of the damage ledger rows at the impact's tick whose victim is the thing hit
    /// and whose instigator is the shooter of the projectile. A row of an unknown kind or with an unknown field is an
    /// error, so a schema change fails the dataset build rather than reading as zeros.
    /// </summary>
    internal static void StampShots(TextWriter w, int seed, string bundle, IReadOnlyList<MetricsHit> damage)
    {
        var rows = new List<object>();
        var line = 0;
        foreach (var raw in bundle.Split('\n'))
        {
            line++;
            if (raw.Trim().Length == 0)
            {
                continue;
            }

            var kind = Row(raw, line, "", ShotTapJsonContext.Default.ShotTapKind).K;
            rows.Add(kind switch
            {
                "shot" => Row(raw, line, kind, ShotTapJsonContext.Default.ShotTapShot),
                "impact" => Row(raw, line, kind, ShotTapJsonContext.Default.ShotTapImpact),
                "job" => Row(raw, line, kind, ShotTapJsonContext.Default.ShotTapJob),
                "stance" => Row(raw, line, kind, ShotTapJsonContext.Default.ShotTapStance),
                "draft" => Row(raw, line, kind, ShotTapJsonContext.Default.ShotTapDraft),
                "think" => Row(raw, line, kind, ShotTapJsonContext.Default.ShotTapThink),
                _ => throw new AcceptanceException($"line {SweepText.Tag(line)}: unknown kind \"{kind}\""),
            });
        }

        var shooter = new Dictionary<int, string>();
        foreach (var shot in rows.OfType<ShotTapShot>())
        {
            shooter[shot.Id] = shot.Shooter;
        }

        foreach (var row in rows)
        {
            w.Write(row switch
            {
                ShotTapShot shot => JsonSerializer.Serialize(shot with { Seed = seed }, ShotTapJsonContext.Default.ShotTapShot),
                ShotTapImpact impact => JsonSerializer.Serialize(Dealt(impact with { Seed = seed }, shooter, damage), ShotTapJsonContext.Default.ShotTapImpact),
                ShotTapJob job => JsonSerializer.Serialize(job with { Seed = seed }, ShotTapJsonContext.Default.ShotTapJob),
                ShotTapStance stance => JsonSerializer.Serialize(stance with { Seed = seed }, ShotTapJsonContext.Default.ShotTapStance),
                ShotTapDraft draft => JsonSerializer.Serialize(draft with { Seed = seed }, ShotTapJsonContext.Default.ShotTapDraft),
                ShotTapThink think => JsonSerializer.Serialize(think with { Seed = seed }, ShotTapJsonContext.Default.ShotTapThink),
                _ => throw new InvalidOperationException($"unhandled shot tap row {row.GetType().Name}"),
            });
            w.Write('\n');
        }
    }

    // One tap line as its kind's record; a malformed line or a field the kind does not declare names the line.
    private static T Row<T>(string raw, int line, string kind, JsonTypeInfo<T> info)
    {
        try
        {
            return JsonSerializer.Deserialize(raw, info) ?? throw new AcceptanceException($"line {SweepText.Tag(line)}: not an object");
        }
        catch (JsonException e)
        {
            var at = kind.Length > 0 ? $"line {SweepText.Tag(line)} ({kind})" : $"line {SweepText.Tag(line)}";
            throw new AcceptanceException($"{at}: {e.Message}", e);
        }
    }

    // An impact on a thing with the damage its projectile dealt added: the ledger rows at its tick whose victim is the
    // thing hit and whose instigator shot the projectile.
    private static ShotTapImpact Dealt(ShotTapImpact impact, Dictionary<int, string> shooter, IReadOnlyList<MetricsHit> damage)
    {
        if (impact.Hit.Length == 0)
        {
            return impact;
        }

        var dealt = impact.Dealt;
        var by = shooter.GetValueOrDefault(impact.Id, "");
        foreach (var h in damage)
        {
            if (h.Tick == impact.Tick && h.Victim == impact.Hit && h.Instigator == by)
            {
                dealt += h.Dealt;
            }
        }

        return impact with { Dealt = dealt };
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
