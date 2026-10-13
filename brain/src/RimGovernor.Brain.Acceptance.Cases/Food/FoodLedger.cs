using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>food/ledger</c> and the delivery-ledger read the ledger cases share (Go <c>ledger.go</c>).</summary>
internal static class FoodLedger
{
    private const string PrepareOp = "test/ledger_prepare";
    private const string ObserveOp = "test/ledger_observe";

    internal static AcceptanceCase Case() => new()
    {
        Name = "food/ledger",
        Scope = "Native read contract and end-to-end signal (a Go snapshot test cannot cover native hooks): the colony facts delivery ledger's fish and rice counters equal the fixture's independent tally of Notify_Fished catches and rice yields, and a gathered cow yields a milk counter; counters never decrease within an epoch.",
        Start = new FixtureStart
        {
            On = new DebugStart { Size = new DebugStartSize { MapSize = 150, PlanetCoverage = 0.05, Biomes = "TropicalRainforest", Seed = "fishing-426" } },
            Op = PrepareOp,
        },
        NoCheckpoint = true,
        RequiredOps = [ObserveOp],
        Budget = TimeSpan.FromMinutes(10),
        Crew = new Crew { Size = 3 },
        Reason = "an Odyssey coast needs the fishing case's 150-tile start; a few native days of fishing, harvest and milking on a programmatic coast with a mature rice field",
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        Task<JsonObject> Tally(string label) => h.CallAsync(label, ObserveOp, null, cancellationToken);
        _ = await NativeReads.RunUntilAsync(s, "ledger-deliveries", 2500, async ct =>
        {
            var v = await h.CallAsync("ledger-progress", ObserveOp, null, ct).ConfigureAwait(false);
            s.Report["ledger_tally"] = v;
            return (Wait.Signature(v["tick"].ToJson()), v["catches"].AsNumber() > 0 && v["riceYield"].AsNumber() > 0 && v["gathers"].AsNumber() > 0);
        }, quiet: true, cancellationToken).ConfigureAwait(false);
        var before = await Tally("ledger-before").ConfigureAwait(false);
        var ledger = await ReadAsync(s, "ledger-facts", cancellationToken).ConfigureAwait(false);
        var after = await Tally("ledger-after").ConfigureAwait(false);
        s.Report["ledger"] = new JsonObject { ["before"] = before.DeepClone(), ["after"] = after.DeepClone(), ["ledger"] = ledger.Raw.DeepClone() };
        if (ledger.Epoch.Length == 0)
        {
            throw new AcceptanceException($"ledger epoch missing: {ledger.Raw.ToJson()}");
        }

        // The ledger may have counted a delivery between the two tallies; it is never outside them.
        foreach (var (name, kind, def, key) in new[] { ("fish", "FISH", "", "catches"), ("rice", "CROP", "RawRice", "riceYield") })
        {
            var units = ledger.Units(kind, def);
            double low = before[key].AsNumber(), high = after[key].AsNumber();
            if (units < low || units > high || units <= 0)
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"{name} ledger units {units} are outside the independent tally [{low}, {high}]"));
            }
        }

        if (ledger.Units("ANIMAL_PRODUCT", "Milk") <= 0)
        {
            throw new AcceptanceException($"no milk counted for a gathered cow: {ledger.Raw.ToJson()}");
        }

        var second = await ReadAsync(s, "ledger-facts-again", cancellationToken).ConfigureAwait(false);
        if (second.Epoch != ledger.Epoch)
        {
            throw new AcceptanceException($"epoch changed without a load: \"{ledger.Epoch}\" -> \"{second.Epoch}\"");
        }

        foreach (var (kind, rows) in ledger.Rows)
        {
            foreach (var (key, units) in rows)
            {
                var now = second.Rows.TryGetValue(kind, out var kindRows) && kindRows.TryGetValue(key, out var n) ? n : 0;
                if (now < units)
                {
                    throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"counter {kind}/{key} decreased: {units} -> {now}"));
                }
            }
        }
    }

    /// <summary>The colony facts delivery ledger: its epoch, raw body and units by source kind, then "source|def".</summary>
    internal sealed record LedgerRead(string Epoch, JsonObject Raw, Dictionary<string, Dictionary<string, double>> Rows)
    {
        /// <summary>Totals a kind's rows, restricted to one def when <paramref name="def"/> is set.</summary>
        internal double Units(string kind, string def)
        {
            var total = 0.0;
            if (Rows.TryGetValue(kind, out var rows))
            {
                foreach (var (key, units) in rows)
                {
                    if (def.Length == 0 || key.EndsWith("|" + def, StringComparison.Ordinal))
                    {
                        total += units;
                    }
                }
            }

            return total;
        }
    }

    internal static async Task<LedgerRead> ReadAsync(ISession s, string label, CancellationToken cancellationToken)
    {
        var facts = await NativeReads.ColonyFactsAsync(s, label, planning: false, cancellationToken).ConfigureAwait(false);
        var section = facts["deliveryLedger"].AsMap();
        var observed = section?["observed"].AsMap()
            ?? throw new AcceptanceException($"delivery ledger is not observed: {section.ToJson()}");
        var rows = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
        foreach (var row in observed["rows"].Rows())
        {
            var kind = row["sourceKind"].ToText().Replace("DELIVERY_SOURCE_KIND_", string.Empty, StringComparison.Ordinal);
            if (!rows.TryGetValue(kind, out var kindRows))
            {
                kindRows = new Dictionary<string, double>(StringComparer.Ordinal);
                rows[kind] = kindRows;
            }

            var key = row["sourceId"].ToText() + "|" + row["defName"].ToText();
            kindRows[key] = (kindRows.TryGetValue(key, out var have) ? have : 0) + row["units"].AsNumber();
        }

        return new LedgerRead(observed["epoch"].AsString(), observed, rows);
    }
}
