using RimGovernor.Brain.Bridge;
using Common = RimGovernor.Contracts.Wire.Common;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;
using Operations = RimGovernor.Contracts.Wire.Operations;
using Receipts = RimGovernor.Contracts.Wire.Receipts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>trade/settlement-session</c> and <c>trade/orbital-session</c>, one row per channel: a pinned Core settlement our
/// caravan visits, or a passing bulk ship over a powered console, traded through the shared open/read/stage/accept/end
/// contract. Opening and staging move nothing and a cancel buys nothing; an accepted purchase moves the seller's
/// component and the negotiator's silver natively (the orbital one by drop pod) and closes the session, and a settlement
/// purchase stays away from home.
/// </summary>
internal static class TradeWorldSession
{
    /// <summary>The orbital open walks a UseCommsConsole job and its goods land by drop pod; each wait is at most this.</summary>
    private const int ContactTicks = 2_500;

    private static readonly string[] Channels = ["settlement", "orbital"];

    internal static IEnumerable<TypedCase> Cases() => Channels.Select(channel => new TypedCase
    {
        Name = "trade/" + channel + "-session",
        Scope = "Core " + channel + " trade: shared open/read/stage/accept/end, cancel without buying, native inventory and silver "
            + "transfer and a closed session; opening and staging supply no goods, and a settlement purchase stays away from home.",
        Ticks = 2 * ContactTicks + 500,
        Tier = CaseTier.OnDemand,
        Ops = [TradeKit.Tool],
        Run = (s, ct) => RunAsync(s, channel, ct),
    });

    private static async Task RunAsync(ITypedSession s, string channel, CancellationToken cancellationToken)
    {
        var prepared = await s.FixtureAsync("session-setup", TradeKit.Session, channel, cancellationToken).ConfigureAwait(false);
        s.Record("prepared", ServedOps.Json(prepared));
        var target = channel == "settlement"
            ? new Common.TradeTarget { Kind = new Common.SettlementTradeTarget { SettlementId = prepared.Trader, CaravanId = prepared.Caravan } }
            : new Common.TradeTarget { Kind = new Common.OrbitalTradeTarget { ShipId = prepared.Trader } };
        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);

        async Task<Receipts.TradeEffect> Apply(string label, Operations.TradeIntentStep step) => TradeKit.AppliedTrade(
            label,
            await TradeKit.ApplyAsync(s, "world-session-" + label, new Operations.TradeIntent { Target = target, NegotiatorId = prepared.Negotiator, Step = step }, cancellationToken).ConfigureAwait(false));

        Task<Fx.TradeInventory> Inventory(string label, CancellationToken ct) => s.FixtureAsync(label, TradeKit.Inventory, (channel, prepared), ct);

        async Task Unchanged(string label, Fx.TradeInventory before)
        {
            var after = await Inventory(label, cancellationToken).ConfigureAwait(false);
            if (after != before)
            {
                throw new AcceptanceException($"{label} moved stock before acceptance: before={before} after={after}");
            }
        }

        async Task Closed(string label)
        {
            var state = await TradeKit.SessionAsync(s, label, cancellationToken).ConfigureAwait(false);
            if (state.Open == true)
            {
                throw new AcceptanceException($"{label}: session still open: {state}");
            }
        }

        async Task<string> Open(string label)
        {
            _ = await Apply(label, new Operations.OpenTrade { GiftMode = false }).ConfigureAwait(false);
            var sessionId = "";
            async Task<(string Signature, bool Done)> Observe(CancellationToken ct)
            {
                var sheet = await TradeKit.SessionAsync(s, label + "-session", ct).ConfigureAwait(false);
                var open = sheet.Open == true;
                var read = open ? await SheetOrNull(ct).ConfigureAwait(false) : null;
                sessionId = read?.SessionId ?? "";
                return ($"open={open} session={sessionId}", sessionId.Length > 0);
            }

            async Task<Observations.TradeSheet?> SheetOrNull(CancellationToken ct)
            {
                var reply = await s.CallAsync(label + "-sheet", NativeOps.ReadTradeSheet, new Observations.TradeSheetRequest { Scope = s.Scope }, ct).ConfigureAwait(false);
                return reply.Outcome?.Observed;
            }

            // The orbital open orders a real UseCommsConsole job; its receipt is not an open session.
            if (!(await Observe(cancellationToken).ConfigureAwait(false)).Done)
            {
                _ = await s.RunUntilAsync(label + "-contact", ContactTicks, Observe, cancellationToken).ConfigureAwait(false);
            }

            return sessionId;
        }

        static Observations.TradeLine? Find(Observations.TradeSheet sheet, string name) => sheet.Lines.FirstOrDefault(row => row.Definition?.DefName == name);

        async Task Cancel(string label)
        {
            _ = await Apply(label, new Operations.EndTrade { Kind = Operations.EndTradeKind.Cancel }).ConfigureAwait(false);
            await Closed(label + "-closed").ConfigureAwait(false);
        }

        var before = await Inventory("before", cancellationToken).ConfigureAwait(false);
        if (before is not { Silver: 2000, Medicine: 6, Components: 0, SellerComponents: 20 })
        {
            throw new AcceptanceException($"invalid pinned inventory: {before}");
        }

        var sessionId = await Open("browse-open").ConfigureAwait(false);
        var offers = await TradeKit.SheetAsync(s, "browse-sheet", sessionId, cancellationToken).ConfigureAwait(false);
        var component = Find(offers, "ComponentIndustrial");
        var medicine = Find(offers, "MedicineIndustrial");
        if (component is null || !(component.BuyPrice > 0) || !(component.MaximumCount >= 1) || medicine is null || !(medicine.MinimumCount <= -1))
        {
            throw new AcceptanceException($"missing live priced stock: component {component}, medicine {medicine}");
        }

        await Unchanged("opened-no-delivery", before).ConfigureAwait(false);
        _ = await Apply("browse-stage", new Operations.SetTradeLines { Lines = [new() { LineId = component.LineId, AbsoluteCount = 1 }] }).ConfigureAwait(false);
        await Unchanged("staged-no-delivery", before).ConfigureAwait(false);
        await Cancel("browse-cancel").ConfigureAwait(false);
        await Unchanged("cancel-no-purchase", before).ConfigureAwait(false);

        sessionId = await Open("purchase-open").ConfigureAwait(false);
        offers = await TradeKit.SheetAsync(s, "purchase-sheet", sessionId, cancellationToken).ConfigureAwait(false);
        var componentId = Find(offers, "ComponentIndustrial")?.LineId ?? throw new AcceptanceException("purchase-sheet: no component row");
        _ = await Apply("purchase-stage", new Operations.SetTradeLines { Lines = [new() { LineId = componentId, AbsoluteCount = 1 }] }).ConfigureAwait(false);
        var staged = await TradeKit.SheetAsync(s, "purchase-accept-sheet", sessionId, cancellationToken).ConfigureAwait(false);
        var effect = await Apply("purchase-accept", new Operations.AcceptTrade { ExpectedDealSignature = staged.DealSignature }).ConfigureAwait(false);
        s.RecordWire("purchase", effect);
        await Closed("purchase-closed").ConfigureAwait(false);

        // Orbital goods are incoming drop pods until vanilla releases their contents.
        var after = before;
        async Task<(string Signature, bool Done)> ObserveDelivery(CancellationToken ct)
        {
            after = await Inventory("delivery-inventory", ct).ConfigureAwait(false);
            return (after.ToString(), after.Components == 1);
        }

        if (!(await ObserveDelivery(cancellationToken).ConfigureAwait(false)).Done)
        {
            _ = await s.RunUntilAsync("native-delivery", ContactTicks, ObserveDelivery, cancellationToken).ConfigureAwait(false);
        }

        s.Record("before", ServedOps.Json(before));
        s.Record("after", ServedOps.Json(after));
        if (after.SellerComponents != 19 || after.Medicine != 6 || after.Silver >= 2000 || after.Silver < 1000 || after.Silver != effect.AfterSilver)
        {
            throw new AcceptanceException($"native purchase inventory/silver mismatch: {after} effect={effect}");
        }

        if (channel == "settlement" && after.HomeComponents != 0)
        {
            throw new AcceptanceException($"away purchase counted as home delivery: {after}");
        }
    }
}
