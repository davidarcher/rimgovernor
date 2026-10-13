using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The authority boundary and native receipt audit for <c>takeover/draft</c> beside the draft subsystem's native
/// assertions (Go package <c>cases/takeover</c>). The Go package registers no case; it only holds these helpers, which
/// other takeover cases use, so there is nothing to register.
/// </summary>
public static class TakeoverAudit
{
    // A fence: every reason listed is a retired "the player owns this, hands off" hold, and none is produced any more
    // (the last, clearance's foreign_designation, went with the wall removal ledger's ownership flags). A journal naming
    // one means the concept crept back.
    private static readonly string[] RetiredHolds =
    [
        "foreign_designation",
        "player_building",
        "player_override",
        "player_owned",
        "foreign_bill",
        "unmanaged_bill",
        "player_schedule",
        "player_excluded",
    ];

    /// <summary>Establishes and reads back Manual before any simulated player edit (Go <c>takeover.Manual</c>).</summary>
    public static async Task ManualAsync(ISession s, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(s);
        var h = s.Harness;
        var grant = await Authority.GrantAutoAsync(h.WireFunc(), "takeover-initial-auto", s.Identity, cancellationToken).ConfigureAwait(false);
        _ = await Authority.RevokeManualAsync(h.WireFunc(), "takeover-manual", s.Identity, grant, cancellationToken).ConfigureAwait(false);
        var (status, _) = await Authority.AuthorityStatusAsync(h.WireFunc(), "takeover-manual-readback", s.Identity, cancellationToken).ConfigureAwait(false);
        _ = Authority.RequireInactive(status, "REVOCATION_REASON_MANUAL");
        s.Report["manual_before_edit"] = status.DeepClone();
    }

    /// <summary>
    /// Audits the native attempt journal for bridge-driven takeover cases (Go <c>takeover.Receipt</c>). A successful
    /// receipt must be applied and recoverable, never a provenance refusal.
    /// </summary>
    public static async Task ReceiptAsync(Harness h, JsonObject request, JsonObject receipt, Report report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(h);
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(report);
        if (receipt["applied"].AsMap() is null)
        {
            throw new AcceptanceException($"takeover was not applied: {receipt.ToJson()}");
        }

        var reply = await h.WireAsync("takeover-journal-lookup", "receipts_lookup", request, cancellationToken).ConfigureAwait(false);
        var (_, stored) = Harness.Outcome(reply, "receipt");
        if (!Dyn.DeepEqual(receipt, stored))
        {
            throw new AcceptanceException("native takeover journal differs from applied receipt");
        }

        report["takeover_native_journal"] = stored.DeepClone();
        NoProvenanceHold(stored);
    }

    private static void NoProvenanceHold(JsonNode value)
    {
        var text = value.ToJsonString();
        foreach (var reason in RetiredHolds)
        {
            if (text.Contains("\"" + reason + "\"", StringComparison.Ordinal))
            {
                throw new AcceptanceException($"auto retained provenance hold {reason}");
            }
        }
    }
}
