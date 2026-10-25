using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>custody/population</c>: population custody through the Capture and Rescue GiveJobIntents on Actions/Apply, the
/// intents the brain's capture and rescue actions send. A downed hostile is captured into prisoner custody and a downed,
/// unadmitted friendly guest is rescued into a bed: roster and status change observed over native ticks, not a receipt.
/// Between the two, the captured prisoner takes PrisonerInteractionIntents (ReduceResistance, AttemptRecruit) that apply
/// and read back, and the same intent on a pawn that is no prisoner is refused. test/population_setup stays a bespoke op:
/// a deterministic downed candidate and guest beside a ready prison and spare beds.
/// </summary>
internal static class CustodyPopulation
{
    // The capture's share of the case's ticks; the rescue takes what is left.
    private const int CaptureTicks = 15_000;

    private static readonly FixtureOp<NoArgs, Fx.PopulationSetup> Setup = ServedOps.Read<Fx.PopulationSetup>("test/population_setup");

    internal static TypedCase Case() => new()
    {
        Name = "custody/population",
        Scope = "Population custody through GiveJobIntents on Actions/Apply: a Capture job carries a downed hostile into prisoner "
            + "custody and a Rescue job carries a downed unadmitted guest into a bed, with live refusals (capture of a non-hostile "
            + "guest, a missing patient), a resend that issues no second job, and the status change observed over native ticks. "
            + "The captured prisoner then takes ReduceResistance and AttemptRecruit interaction intents that read back in the "
            + "population census, and a prisoner interaction on a pawn that is no prisoner is refused.",
        Ticks = TypedCase.StagedTickCap,
        Tier = CaseTier.Nightly,
        Start = new LabStart(),
        Ops = [Setup.Tool],
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        // The fixture runs before authority is acquired: its direct spawns would revoke a held grant.
        var setup = await s.FixtureAsync("setup", Setup, NoArgs.Value, cancellationToken).ConfigureAwait(false);
        s.Record("fixture", ServedOps.Json(setup));
        if (!setup.Success || setup.Candidate.Length == 0 || setup.Visitor.Length == 0 || setup.Prison.Length == 0)
        {
            throw new AcceptanceException("setup: missing fixture ids");
        }

        var capturer = setup.BedChecks.FirstOrDefault(b => b is { Usable: true, Reachable: true, Reservable: true } && b.NativeBed == setup.Prison)?.Pawn
            ?? throw new AcceptanceException("setup: no fixture worker can reach and reserve the prisoner bed");
        s.Record("capturer", capturer);
        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);

        // Capture: the downed hostile candidate.
        var before = await PawnAsync(s, "candidate-before", setup.Candidate, cancellationToken).ConfigureAwait(false);
        if (!before.Downed || before.Prisoner)
        {
            throw new AcceptanceException($"candidate-before: want a downed non-prisoner, got downed={before.Downed} prisoner={before.Prisoner}");
        }

        await RefusedAsync(s, "capture-guest", TypedPawnOps.GiveJob(capturer, "Capture", setup.Visitor), r => r.Reason?.Contains("Capture refused", StringComparison.Ordinal) == true, cancellationToken).ConfigureAwait(false);
        await RefusedAsync(s, "capture-missing", TypedPawnOps.GiveJob(capturer, "Capture", "Thing_NoSuchPatient"), r => r.Code == RimGovernor.Brain.GameFacts.Common.FailureCode.NotFound, cancellationToken).ConfigureAwait(false);
        await OrderedAsync(s, "custody-capture", capturer, setup.Candidate, "Capture", cancellationToken).ConfigureAwait(false);
        _ = await s.RunUntilAsync("observe-capture", CaptureTicks, Until(s, setup.Candidate, p => p.Prisoner && p.InBed), cancellationToken).ConfigureAwait(false);

        // Prisoner interaction: the captured candidate.
        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);
        var prisoner = await PersonAsync(s, "prisoner-before", setup.Candidate, cancellationToken).ConfigureAwait(false);
        if (prisoner.Custody is not RimGovernor.Brain.GameFacts.Observations.PersonCustody.Prisoner held)
        {
            throw new AcceptanceException($"prisoner-before: want the census to hold {setup.Candidate} as a colony prisoner, got {OrdersJson.Serialize(prisoner.Custody)}");
        }

        s.Record("prisoner_ticks", held.HeldTicks);
        await RefusedAsync(s, "interaction-missing", Interaction("Thing_NoSuchPrisoner", RimGovernor.Brain.GameOrders.Operations.PrisonerInteraction.ReduceResistance), r => r.Code == RimGovernor.Brain.GameFacts.Common.FailureCode.NotFound, cancellationToken).ConfigureAwait(false);

        // A Core mode beyond the routine Recruit/Maintain pair; resending the same intent applies again as it stands.
        foreach (var key in new[] { "reduce-resistance", "reduce-resistance-resend" })
        {
            await InteractionAsync(s, key, setup.Candidate, RimGovernor.Brain.GameOrders.Operations.PrisonerInteraction.ReduceResistance, "ReduceResistance", cancellationToken).ConfigureAwait(false);
        }

        await InteractionAsync(s, "attempt-recruit", setup.Candidate, RimGovernor.Brain.GameOrders.Operations.PrisonerInteraction.AttemptRecruit, "AttemptRecruit", cancellationToken).ConfigureAwait(false);

        // The live custody check: the guest is no prisoner, so an interaction on it is refused before any write.
        await RefusedAsync(s, "interaction-non-prisoner", Interaction(setup.Visitor, RimGovernor.Brain.GameOrders.Operations.PrisonerInteraction.MaintainOnly), _ => true, cancellationToken).ConfigureAwait(false);

        // Rescue: the downed, unadmitted friendly guest.
        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);
        var visitor = await PawnAsync(s, "visitor-before", setup.Visitor, cancellationToken).ConfigureAwait(false);
        if (!visitor.Downed || visitor.InBed)
        {
            throw new AcceptanceException($"visitor-before: want a downed guest out of bed, got downed={visitor.Downed} inBed={visitor.InBed}");
        }

        await OrderedAsync(s, "custody-rescue", capturer, setup.Visitor, "Rescue", cancellationToken).ConfigureAwait(false);
        _ = await s.RunUntilAsync("observe-rescue", s.TicksLeft, Until(s, setup.Visitor, p => p.InBed), cancellationToken).ConfigureAwait(false);
    }

    private static RimGovernor.Brain.GameOrders.Operations.ActionIntent Interaction(string pawn, RimGovernor.Brain.GameOrders.Operations.PrisonerInteraction mode) =>
        new RimGovernor.Brain.GameOrders.Operations.PrisonerInteractionIntent { PawnId = pawn, Interaction = mode };

    /// <summary>Applies one custody order, requires an issued <paramref name="job"/> job, then resends it and requires no second job.</summary>
    private static async Task OrderedAsync(TypedSession s, string key, string capturer, string target, string job, CancellationToken cancellationToken)
    {
        var issued = (await TypedPawns.AppliedAsync(s, key, TypedPawnOps.GiveJob(capturer, job, target), cancellationToken).ConfigureAwait(false))?.Job;
        if (issued is not { Issued: true } || issued.PawnId != capturer || issued.JobDef != job)
        {
            throw new AcceptanceException($"{key}: want an issued {job} job by {capturer}, got {OrdersJson.Serialize(issued)}");
        }

        var again = (await TypedPawns.AppliedAsync(s, key + "-resend", TypedPawnOps.GiveJob(capturer, job, target), cancellationToken).ConfigureAwait(false))?.Job;
        if (again?.Issued == true)
        {
            throw new AcceptanceException($"{key}: the resend issued a second job: {OrdersJson.Serialize(again)}");
        }
    }

    /// <summary>Applies one prisoner interaction, requires held <paramref name="def"/> evidence and the census to read it back.</summary>
    private static async Task InteractionAsync(TypedSession s, string key, string pawn, RimGovernor.Brain.GameOrders.Operations.PrisonerInteraction mode, string def, CancellationToken cancellationToken)
    {
        var effect = (await TypedPawns.AppliedAsync(s, key, Interaction(pawn, mode), cancellationToken).ConfigureAwait(false))?.Prisoner;
        if (effect is not { Outcome: "held" } || effect.InteractionDef != def)
        {
            throw new AcceptanceException($"{key}: want held {def} evidence, got {OrdersJson.Serialize(effect)}");
        }

        var person = await PersonAsync(s, key + "-read", pawn, cancellationToken).ConfigureAwait(false);
        if (person.Custody is not RimGovernor.Brain.GameFacts.Observations.PersonCustody.Prisoner { } held || held.Interaction != def)
        {
            throw new AcceptanceException($"{key}: the census reads custody {OrdersJson.Serialize(person.Custody)}, want prisoner interaction {def}");
        }
    }

    /// <summary>Applies <paramref name="intent"/> and requires a refusal <paramref name="want"/> accepts.</summary>
    private static async Task RefusedAsync(TypedSession s, string key, RimGovernor.Brain.GameOrders.Operations.ActionIntent intent, Func<RimGovernor.Brain.GameFacts.Operations.Refusal, bool> want, CancellationToken cancellationToken)
    {
        var result = await TypedPawns.ApplyAsync(s, key, intent, cancellationToken).ConfigureAwait(false);
        s.RecordWire(key, result);
        if (result.Outcome?.Refused is not { } refusal || !want(refusal))
        {
            throw new AcceptanceException($"{key}: want a refusal, got {OrdersJson.Serialize(result)}");
        }
    }

    private static Probe Until(TypedSession s, string pawn, Func<RimGovernor.Brain.GameFacts.Observations.PawnState, bool> done) => async ct =>
    {
        // The spawned-pawn census omits a patient while the carrier holds it.
        // Absence is pending, never successful custody; the tick budget still
        // fails a patient that does not reappear with the required outcome.
        var rows = await TypedPawns.ListAsync(s, "custody-" + pawn,
            new RimGovernor.Brain.GameFacts.Observations.PawnFilter { Ids = [pawn] }, ct).ConfigureAwait(false);
        var row = rows.FirstOrDefault(p => p.Pawn.Id == pawn);
        if (row is null) return ("patient unobserved; awaiting spawned custody outcome", false);
        return ($"{row.Prisoner} {row.InBed} {row.Downed}", done(row));
    };

    private static Task<RimGovernor.Brain.GameFacts.Observations.PawnState> PawnAsync(TypedSession s, string label, string pawn, CancellationToken cancellationToken) =>
        TypedPawns.OneAsync(s, label, pawn, cancellationToken);

    /// <summary>The population census row of <paramref name="pawn"/>.</summary>
    private static async Task<RimGovernor.Brain.GameFacts.Observations.PopulationPerson> PersonAsync(TypedSession s, string label, string pawn, CancellationToken cancellationToken)
    {
        var population = await s.CallAsync(label, NativeOps.ReadPopulation, new RimGovernor.Brain.GameOrders.Observations.PopulationRequest { Scope = s.Scope }, cancellationToken).ConfigureAwait(false);
        return population.Persons.FirstOrDefault(p => p.Pawn.Value == pawn) ?? throw new AcceptanceException($"{label}: pawn {pawn} missing from the population census");
    }
}
