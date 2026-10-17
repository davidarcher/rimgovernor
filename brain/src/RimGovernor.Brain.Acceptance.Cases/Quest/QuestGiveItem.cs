using Common = RimGovernor.Contracts.Wire.Common;
using Operations = RimGovernor.Contracts.Wire.Operations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>quest/give-item</c>: the GiveItemIntent arm of operations_apply against a vanilla beggar lord asking twenty silver.
/// A stale expected count is refused without starting delivery; the whole request starts the hauler's vanilla
/// GiveToPawn job on the exact beggar, and ordinary hauling moves all twenty into the beggar's inventory.
/// </summary>
internal static class QuestGiveItem
{
    private const int DeliveryTicks = 3000;

    internal static TypedCase Case() => new()
    {
        Name = "quest/give-item",
        Scope = "Native whole-request count fencing and ordinary GiveToPawn delivery of twenty silver into an Ideology beggar's inventory.",
        Ticks = DeliveryTicks,
        Tier = CaseTier.OnDemand,
        NoKeep = true,
        Expansions = ["ludeon.rimworld.ideology"],
        Ops = [QuestKit.GivePrepareTool, QuestKit.GiveReadTool],
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var prepared = await s.FixtureAsync("prepare", QuestKit.GivePrepare, NoArgs.Value, cancellationToken).ConfigureAwait(false);
        if (prepared.Remaining != 20)
        {
            throw new AcceptanceException($"prepare: expected the beggar to ask twenty, got {ServedOps.Json(prepared)}");
        }

        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);

        Task<Operations.ActionResult> Apply(string key, int remaining) => TradeKit.ApplyAsync(s, key, new Operations.GiveItemIntent
        {
            Hauler = new Common.Ref { Id = prepared.HaulerId },
            Recipient = new Common.Ref { Id = prepared.RecipientId },
            Definition = "Silver",
            ExpectedRemaining = remaining,
        }, cancellationToken);

        var stale = await Apply("giving-stale-count", 19).ConfigureAwait(false);
        if (TradeKit.RefusedCode(stale) != Common.FailureCode.InvalidRequest)
        {
            throw new AcceptanceException($"giving-stale-count: expected an INVALID_REQUEST refusal, got {stale}");
        }

        var before = await s.FixtureAsync("giving-before", QuestKit.GiveRead, prepared, cancellationToken).ConfigureAwait(false);
        if (before.Received != 0 || before.Remaining != 20 || before.Job == "GiveToPawn")
        {
            throw new AcceptanceException($"giving-before: the refusal changed native delivery state: {ServedOps.Json(before)}");
        }

        var given = TradeKit.Applied("giving-whole-request", await Apply("giving-whole-request", 20).ConfigureAwait(false)).GiveItem;
        if (given is not { ExpectedRemaining: 20 } || given.RecipientId != prepared.RecipientId)
        {
            throw new AcceptanceException($"giving-whole-request: expected a give-item effect owing twenty to {prepared.RecipientId}, got {given}");
        }

        var started = await s.FixtureAsync("giving-started", QuestKit.GiveRead, prepared, cancellationToken).ConfigureAwait(false);
        if (started.Job != "GiveToPawn" || started.TargetId != prepared.RecipientId)
        {
            throw new AcceptanceException($"giving-started: expected the hauler's GiveToPawn job on the beggar, got {ServedOps.Json(started)}");
        }

        _ = await s.RunUntilAsync("giving-delivered", DeliveryTicks, async ct =>
        {
            var read = await s.FixtureAsync("giving-read", QuestKit.GiveRead, prepared, ct).ConfigureAwait(false);
            return ($"{read.Received}/{read.Remaining} {read.Job}", read.Remaining == 0 && read.Received == 20);
        }, cancellationToken).ConfigureAwait(false);
    }
}
