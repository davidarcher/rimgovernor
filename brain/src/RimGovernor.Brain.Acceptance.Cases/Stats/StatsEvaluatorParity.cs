using System.Globalization;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>stats/evaluator-parity</c> (Go <c>evaluator_parity.go</c>): the Go stat evaluator (#2636) agreed with the game's own
/// EvaluateStat on a sample of definition requests.
/// <para>
/// Dropped from Go: the Go evaluator itself (<c>stateval</c>) and the catalog's stat-def list. The Go evaluator is replaced,
/// not ported, by the def stat table in the C# brain, so there is no second evaluator to compare with and the "not mirrored"
/// skip count is gone. What stays is the native side: every (def, stuff, stat) request over the evaluate-stat sample's
/// subjects and stats is answered twice, and both reads must agree on the shown flag and the exact float32 value, so the
/// oracle the def stat table is computed from is deterministic on a paused world. A pair the game does not know (NOT_FOUND) is
/// skipped, never defaulted. Equality against the def stat table is re-established at the #3085 cutover.
/// </para>
/// </summary>
internal static class StatsEvaluatorParity
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "stats/evaluator-parity",
        Scope = "The Go stat evaluator (#2636) agrees with the game's own EvaluateStat on a sample of definition requests: the same shown flag "
            + "and the same float32 value for every (def, stuff, stat) it can evaluate; a stat with a class not ported yet is skipped, never defaulted.",
        Start = new LabStart(),
        // Boot and two small reads per (subject, stat) pair on a kept process.
        Budget = TimeSpan.FromMinutes(5),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var subjects = StatsEvaluateStat.Sample.Select(r => (r.Def, r.Stuff)).Distinct().ToArray();
        var stats = StatsEvaluateStat.Sample.Select(r => r.Stat).Distinct().Order(StringComparer.Ordinal).ToArray();
        var compared = 0;
        var skipped = 0;
        foreach (var (def, stuff) in subjects)
        {
            foreach (var stat in stats)
            {
                var label = $"{def}-{stuff}-{stat}";
                var first = await StatsEvaluateStat.ReadAsync(s, "first-" + label, def, stuff, stat, cancellationToken).ConfigureAwait(false);
                if (first is null)
                {
                    skipped++;
                    continue;
                }

                var second = await StatsEvaluateStat.ReadAsync(s, "second-" + label, def, stuff, stat, cancellationToken).ConfigureAwait(false);
                if (second is null || first.Value.Shown != second.Value.Shown
                    || BitConverter.SingleToInt32Bits((float)first.Value.Value) != BitConverter.SingleToInt32Bits((float)second.Value.Value))
                {
                    throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"{def}/{stuff}/{stat}: first read {first}, second read {second}"));
                }

                compared++;
            }
        }

        if (compared == 0)
        {
            throw new AcceptanceException($"the game answered no sampled request ({skipped} NOT_FOUND)");
        }

        s.Report["pairs_compared"] = compared;
        s.Report["pairs_not_found"] = skipped;
    }
}
