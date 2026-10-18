using RimGovernor.Brain.Bridge;
using Fx = RimGovernor.Fixtures.Contracts;
using Presentation = RimGovernor.Contracts.Wire.Presentation;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>letter/notifications</c>: presentation_notifications while a letter is up. A PositiveEvent letter delivered through
/// the real LetterStack callback is listed with its def, label and native type in a letter listing that describes every
/// row; messages are observed and the headless alert readout lists a fresh colony's alerts under a fingerprint; and
/// includeLetters:false leaves the letter section out.
/// </summary>
internal static class LetterNotifications
{
    private const string Tool = "test/deliver_letter";

    private static readonly FixtureOp<NoArgs, Fx.LetterDelivered> Deliver = new(
        Tool,
        _ => ServedOps.Args(("definition", "PositiveEvent"), ("label", "Letter acceptance"), ("delayTicks", 0)),
        ServedOps.Succeeded<Fx.LetterDelivered>(Tool, r => r.Success && r.LetterId.Length > 0));

    internal static TypedCase Case() => new()
    {
        Name = "letter/notifications",
        Scope = "Native presentation_notifications: a delivered letter listed with its def, label and type in a complete listing, "
            + "observed messages, the headless alert readout's alerts under a fingerprint, and includeLetters:false omitting letters.",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        Ops = [Tool],
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var letterId = (await s.FixtureAsync("deliver", Deliver, NoArgs.Value, cancellationToken).ConfigureAwait(false)).LetterId;

        async Task<Presentation.NotificationsSnapshot> Notifications(string label, Presentation.NotificationsRequest request)
        {
            var reply = await s.CallAsync(label, NativeOps.ReadNotifications, request, cancellationToken).ConfigureAwait(false);
            return reply.Outcome?.Notifications ?? throw new AcceptanceException($"{label}: expected notifications, got {reply.Outcome}");
        }

        var snapshot = await Notifications("notifications", new Presentation.NotificationsRequest { Identity = s.Identity }).ConfigureAwait(false);
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

        // A fresh colony always has at least "Need colonist beds": an empty list means the headless AlertsReadout stopped running.
        if (string.IsNullOrEmpty(alerts.SnapshotFingerprint) || alerts.Listing is not { TotalCount: > 0 })
        {
            throw new AcceptanceException($"notifications: expected a fresh colony's alerts under a fingerprint, got {alerts}");
        }

        var narrowed = await Notifications("notifications-no-letters", new Presentation.NotificationsRequest { Identity = s.Identity, IncludeLetters = false, AlertLimit = 1 }).ConfigureAwait(false);
        if (narrowed.Letters is not null)
        {
            throw new AcceptanceException($"notifications-no-letters: includeLetters:false still returned a letter section: {narrowed.Letters}");
        }
    }
}
