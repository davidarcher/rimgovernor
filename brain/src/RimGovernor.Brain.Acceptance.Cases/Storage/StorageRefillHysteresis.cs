using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>storage/refill-hysteresis</c> (Go <c>refill_hysteresis.go</c>): a full Steel stockpile (a stockpile zone is also a
/// bench input store) with a loose stack waiting: under supervision taking one item out starts no refill haul, taking a
/// stack's worth out starts it (#2519). Native: the HaulToStorageJob postfix and the idle hauler's work scanner, which no
/// Go snapshot reaches.
/// </summary>
internal static class StorageRefillHysteresis
{
    private const string PrepareOp = "test/refill_hysteresis_prepare";
    private const string TakeOp = "test/refill_hysteresis_take";
    private const string ReadOp = "test/refill_hysteresis_read";

    // Long enough for an idle hauler to walk to the loose stack and carry it, so an unchanged store means the haul was
    // withheld.
    private const ulong IdleTicks = 1500;

    // Bounds the refill once a stack's worth of room is open.
    private const int RefillWindow = 3000;

    internal static AcceptanceCase Case() => new()
    {
        Name = "storage/refill-hysteresis",
        Scope = "A full Steel stockpile (a stockpile zone is also a bench input store) with a loose stack waiting: under supervision "
            + "taking one item out starts no refill haul, taking a stack's worth out starts it (#2519). Native: the HaulToStorageJob "
            + "postfix and the idle hauler's work scanner, which no Go snapshot reaches.",
        Start = new FixtureStart { Op = PrepareOp, On = new LabStart() },
        RequiredOps = [PrepareOp, TakeOp, ReadOp],
        QuietWorld = true,
        Budget = TimeSpan.FromMinutes(6),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private readonly record struct State(int Store, int Loose);

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var report = s.Report;
        var prepared = s.Prepared ?? [];
        var stack = (int)prepared["stackLimit"].AsNumber();
        var store = (int)prepared["store"].AsNumber();
        if (stack <= 1 || store != 2 * stack)
        {
            throw new AcceptanceException($"prepare: unexpected fixture reply {prepared.ToJson()}");
        }

        async Task<State> Read(string label)
        {
            var reply = await h.CallAsync(label, ReadOp, [], cancellationToken).ConfigureAwait(false);
            return reply["success"].AsBool() != true
                ? throw new AcceptanceException($"{ReadOp} refused: {reply.ToJson()}")
                : new State((int)reply["store"].AsNumber(), (int)reply["loose"].AsNumber());
        }

        async Task Take(string label, int n)
        {
            var reply = await h.CallAsync(label, TakeOp, new JsonObject { ["count"] = n }, cancellationToken).ConfigureAwait(false);
            if (reply["success"].AsBool() != true)
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"{TakeOp} {n} refused: {reply.ToJson()}"));
            }
        }

        string Describe(State state) => string.Create(CultureInfo.InvariantCulture, $"{{store {state.Store}, loose {state.Loose}}}");

        var start = await Read("start").ConfigureAwait(false);
        if (start.Store != store)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"the store starts at {start.Store}, want {store}: {Describe(start)}"));
        }

        // One item out: room below a stack, so the loose stack stays put.
        await Take("take-one", 1).ConfigureAwait(false);
        _ = await s.AdvanceAsync(IdleTicks, null, cancellationToken).ConfigureAwait(false);
        var idle = await Read("after-one").ConfigureAwait(false);
        report["after_one"] = new JsonObject { ["store"] = idle.Store, ["loose"] = idle.Loose };
        if (idle.Store != store - 1 || idle.Loose != start.Loose)
        {
            throw new AcceptanceException(string.Create(
                CultureInfo.InvariantCulture,
                $"one item out of a full store started a refill: {Describe(idle)}, want store {store - 1} loose {start.Loose}"));
        }

        // A stack's worth out: the hauler refills.
        await Take("take-stack", stack - 1).ConfigureAwait(false);
        for (var advanced = 0; ; advanced += 300)
        {
            var now = await Read(string.Create(CultureInfo.InvariantCulture, $"refill-{advanced}")).ConfigureAwait(false);
            if (now.Loose < start.Loose)
            {
                report["refill_ticks"] = advanced;
                return;
            }

            if (advanced >= RefillWindow)
            {
                throw new AcceptanceException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"a stack's worth of room opened and no refill started in {advanced} ticks: {Describe(now)}"));
            }

            _ = await s.AdvanceAsync(300, null, cancellationToken).ConfigureAwait(false);
        }
    }
}
