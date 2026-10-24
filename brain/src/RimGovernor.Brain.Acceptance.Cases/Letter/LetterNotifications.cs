using RimGovernor.Brain.Bridge;
using Fx = RimGovernor.Fixtures.Contracts;
using Presentation = RimGovernor.Contracts.Wire.Presentation;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>letter/notifications</c>: presentation_notifications while a letter is up. A PositiveEvent letter delivered through
/// the real LetterStack callback is listed with its def, label and native type in a letter listing that describes every
/// row; messages are observed and the headless alert readout lists a fresh colony's alerts under a fingerprint; and
/// includeLetters:false leaves the letter section out. The lab opens at tick 0 and vanilla
/// <c>AlertsReadout.AlertsReadoutUpdate</c> evaluates no alert before game tick 600, so the case runs the clock until
/// the readout lists one (the bedless lab's "Need colonist beds").
/// </summary>
internal static class LetterNotifications
{
    private const string Tool = "test/deliver_letter";

    // Vanilla AlertsReadoutUpdate returns before tick 600; the margin covers its 24-frame sweep of every alert.
    private const int AlertTicks = 1_200;

    private static readonly FixtureOp<NoArgs, Fx.LetterDelivered> Deliver = new(
        Tool,
        _ => ServedOps.Args(("definition", "PositiveEvent"), ("label", "Letter acceptance"), ("delayTicks", 0)),
        ServedOps.Succeeded<Fx.LetterDelivered>(Tool, r => r.Success && r.LetterId.Length > 0));

    internal static TypedCase Case() => new()
    {
        Name = "letter/notifications",
        Scope = "Native presentation_notifications: a delivered letter listed with its def, label and type in a complete listing, "
            + "observed messages, the headless alert readout's alerts under a fingerprint, and includeLetters:false omitting letters.",
        Ticks = AlertTicks,
        Tier = CaseTier.OnDemand,
        Ops = [Tool],
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        Task<Presentation.NotificationsSnapshot> Notifications(string label, Presentation.NotificationsRequest request, CancellationToken ct) =>
            s.CallAsync(label, NativeOps.ReadNotifications, request, ct);

        var alertsOnly = new Presentation.NotificationsRequest { Identity = s.Identity, IncludeLetters = false, IncludeMessages = false };
        _ = await s.RunUntilAsync("alerts-listed", AlertTicks, async ct =>
        {
            var read = await Notifications("alerts-poll", alertsOnly, ct).ConfigureAwait(false);
            var count = read.Alerts?.Outcome?.Observed?.Listing?.TotalCount ?? 0;
            return ($"tick={read.Context?.Tick} alerts={count}", count > 0);
        }, cancellationToken).ConfigureAwait(false);

        var letterId = (await s.FixtureAsync("deliver", Deliver, NoArgs.Value, cancellationToken).ConfigureAwait(false)).LetterId;
        var snapshot = await Notifications("notifications", new Presentation.NotificationsRequest { Identity = s.Identity }, cancellationToken).ConfigureAwait(false);
        s.RecordWire("notifications", snapshot);
        var letters = snapshot.Letters?.Outcome?.Observed ?? throw new AcceptanceException($"notifications: letters not observed: {snapshot.Letters}");
        if (letters.Listing is not { Complete: true } listing || listing.TotalCount != letters.Items.Count || listing.ReturnedCount != letters.Items.Count)
        {
            throw new AcceptanceException($"notifications: the letter listing does not describe its {letters.Items.Count} rows: {letters.Listing}");
        }

        var letter = letters.Items.FirstOrDefault(l => l.Id == letterId) ?? throw new AcceptanceException($"notifications: letters omit {letterId}: {letters}");
        if (letter.DefName != "PositiveEvent" || string.IsNullOrEmpty(letter.Label) || string.IsNullOrEmpty(letter.NativeType) || letter.ArrivalTick is not >= 0)
        {
            throw new AcceptanceException($"notifications: letter {letterId} lacks its def, label, type or arrival tick: {letter}");
        }

        var messages = snapshot.Messages?.Outcome?.Observed ?? throw new AcceptanceException($"notifications: messages not observed: {snapshot.Messages}");
        if (messages.Listing is not { Complete: not null })
        {
            throw new AcceptanceException($"notifications: the message section has no listing: {messages.Listing}");
        }

        var alerts = snapshot.Alerts?.Outcome?.Observed ?? throw new AcceptanceException($"notifications: alerts not observed: {snapshot.Alerts}");

        // Past tick 600 the bedless lab has at least "Need colonist beds": an empty list means the headless AlertsReadout stopped running.
        if (string.IsNullOrEmpty(alerts.SnapshotFingerprint) || alerts.Listing is not { TotalCount: > 0 })
        {
            throw new AcceptanceException($"notifications: expected a fresh colony's alerts under a fingerprint, got {alerts}");
        }

        var narrowed = await Notifications("notifications-no-letters", new Presentation.NotificationsRequest { Identity = s.Identity, IncludeLetters = false, AlertLimit = 1 }, cancellationToken).ConfigureAwait(false);
        if (narrowed.Letters is not null)
        {
            throw new AcceptanceException($"notifications-no-letters: includeLetters:false still returned a letter section: {narrowed.Letters}");
        }
    }
}
