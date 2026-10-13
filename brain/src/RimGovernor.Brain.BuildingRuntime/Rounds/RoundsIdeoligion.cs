using System.Text.Json;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>The persisted text of a pending reform (a <c>Standard.Record</c>); JSON with the Go field names.</summary>
internal sealed record IdeoligionRecord(string Ideo, IdeoligionDesign Expected, IdeoligionDesign Target, int Count);

public sealed partial class Rounder
{
    /// <summary>The reform the colony's ideoligion owes, or unknown while any input is unread or the pawns disagree on one faction.</summary>
    internal Fact<DesignChoice> ReformChoice(RoundsReading read, GenerationSnapshot snapshot)
    {
        if (!read.Projection.Facts.Ideology.TryGetValue(out var ideo) || read.Frame.Catalog is null || read.Frame.Pawns is null)
        {
            return Unknown<DesignChoice>();
        }

        var faction = "";
        foreach (var pawn in read.Frame.Pawns.Pawns)
        {
            if (!pawn.FreeColonist || pawn.Dead)
            {
                continue;
            }

            var name = pawn.Standing?.FactionDefName ?? "";
            if (name.Length == 0 || (faction.Length != 0 && faction != name))
            {
                return Unknown<DesignChoice>();
            }

            faction = name;
        }

        IdeoligionOptions options;
        try
        {
            options = read.Frame.Catalog.IdeoligionOptions(faction);
        }
        catch (DomainException)
        {
            return Unknown<DesignChoice>();
        }

        options = options with { Needs = PolicyFunctions.IdeoligionNeeds(ideo.Facts, read.Projection.WorkPawns, read.Projection.Facts.MoodPawns) };
        var precepts = new List<string>();
        foreach (var held in ideo.Facts.Precepts)
        {
            var def = BridgePackage.DefRow<Defs.PreceptDef>(read.Frame.Catalog, held.Def);
            if (def is null)
            {
                return Unknown<DesignChoice>();
            }

            if (def.PreceptClass == "RimWorld.Precept")
            {
                precepts.Add(held.Def);
            }
        }

        var current = new IdeoligionDesign { Memes = ideo.Facts.Memes, Fluid = true, Precepts = precepts };
        var calm = Unknown<bool>();
        var tick = read.Projection.Identity.Tick;
        try
        {
            var emergency = PolicyFunctions.NewEmergencySnapshot(snapshot, tick, read.Emergency);
            var (hostiles, patients) = PolicyFunctions.EmergencyNeeds(emergency, snapshot, tick);
            calm = PolicyFunctions.RitualCalm(hostiles, patients);
        }
        catch (DomainException)
        {
            // No emergency snapshot leaves calm unknown.
        }

        return PolicyFunctions.ChooseIdeoligionReform(options, current, ideo.Facts.Development, calm);
    }

    /// <summary>
    /// Sets the reform-owed fact from the chosen design, then settles a pending reform record: a completed
    /// reform clears the record, and so does a record that no longer matches the colony's ideoligion.
    /// </summary>
    internal void ReviewIdeoligion(CancellationToken ct, ref RoundsReading read, GenerationSnapshot snapshot)
    {
        var choice = ReformChoice(read, snapshot);
        var owed = choice.TryGetValue(out var v) ? Known(v.Design.Memes.Count > 0) : Unknown<bool>();
        read = read with { Projection = read.Projection with { Facts = read.Projection.Facts with { ReformOwed = owed } } };
        Rounds review;
        try
        {
            review = Player.Journal.LoadRounds(ct);
        }
        catch (NotFoundException)
        {
            return;
        }

        if (!review.Snapshot.Matches(snapshot))
        {
            return;
        }

        var id = new StandardId("");
        foreach (var binding in review.Standards)
        {
            if (binding.Concern == ConcernId.ImproveIdeoligion)
            {
                id = binding.Standard;
            }
        }

        if (id.IsEmpty)
        {
            return;
        }

        StandardState standard;
        try
        {
            standard = Player.Journal.LoadStandard(ct, id);
        }
        catch (NotFoundException)
        {
            return;
        }

        if (standard.Standard.Record.Length == 0)
        {
            return;
        }

        var intent = ReformRecord(standard.Standard.Record);
        void SetOwed(Fact<bool> fact) => read = read with { Projection = read.Projection with { Facts = read.Projection.Facts with { ReformOwed = fact } } };
        if (!read.Projection.Facts.Ideology.TryGetValue(out var ideo))
        {
            SetOwed(Unknown<bool>());
            return;
        }

        if (read.Frame.Catalog is null)
        {
            SetOwed(Unknown<bool>());
            return;
        }

        var precepts = new List<string>();
        foreach (var held in ideo.Facts.Precepts)
        {
            var def = BridgePackage.DefRow<Defs.PreceptDef>(read.Frame.Catalog, held.Def);
            if (def is null)
            {
                SetOwed(Unknown<bool>());
                return;
            }

            if (def.PreceptClass == "RimWorld.Precept")
            {
                precepts.Add(held.Def);
            }
        }

        var actual = new IdeoligionDesign { Memes = ideo.Facts.Memes, Fluid = true, Precepts = precepts };
        if (ObservedReform(ideo, actual, intent).TryGetValue(out var complete) && complete)
        {
            SetOwed(Known(false));
            Player.Journal.RecordStandard(ct, standard.Standard.Id, standard.Revision, "");
            return;
        }

        if (ideo.Facts.Development.TryGetValue(out var state))
        {
            var canonical = IdeoligionDesign.CanonicalIdeoligionDesign(actual);
            if (state.ReformCount != intent.Count || canonical != intent.Expected || ideo.Facts.IdeoId != intent.IdeoId)
            {
                Player.Journal.RecordStandard(ct, standard.Standard.Id, standard.Revision, "");
            }
        }
    }
}

internal static partial class BuildingRuntimePackage
{
    internal static IdeoligionReform ReformRecord(string record)
    {
        var v = JsonSerializer.Deserialize<IdeoligionRecord>(record) ?? throw new DomainException("ideoligion record is empty");
        return IdeoligionReform.NewIdeoligionReform(v.Ideo, v.Expected, v.Target, v.Count);
    }

    internal static string ReformRecordText(IdeoligionReform intent) =>
        JsonSerializer.Serialize(new IdeoligionRecord(intent.IdeoId, intent.Expected, intent.Design, intent.Count));

    /// <summary>Whether the colony's ideoligion now matches the intended design: unknown when the ideo or its development is unread, false while the reform count has not advanced.</summary>
    internal static Fact<bool> ObservedReform(Ideoligion ideo, IdeoligionDesign actual, IdeoligionReform intent)
    {
        if (!ideo.Facts.Development.TryGetValue(out var state) || ideo.Facts.IdeoId != intent.IdeoId)
        {
            return Unknown<bool>();
        }

        if (state.ReformCount != intent.Count + 1)
        {
            return Known(false);
        }

        try
        {
            var canonical = IdeoligionDesign.CanonicalIdeoligionDesign(actual with { Fluid = state.Fluid });
            return Known(canonical == intent.Design);
        }
        catch (DomainException)
        {
            return Unknown<bool>();
        }
    }
}

/// <summary>Composes ImproveIdeoligion's rounds step: reforms the ideoligion toward the policy's chosen design.</summary>
public sealed partial class RoundsIdeoligionPlanner
{
    internal Rounder Reviewer { get; init; } = null!;

    public static RoundsIdeoligionPlanner NewRoundsIdeoligionPlanner(Rounder r)
    {
        if (r is null || !r.MethodEnabled(ConcernId.ImproveIdeoligion))
        {
            throw new ControlException("ideoligion reviewer unavailable");
        }

        return new RoundsIdeoligionPlanner { Reviewer = r };
    }
}

public readonly record struct RoundsIdeoligionResult
{
    public Verdict Verdict { get; init; }

    public PlanId Plan { get; init; }

    public DesignComparison? Comparison { get; init; }
}

public sealed partial class RoundsIdeoligionPlanner
{
    internal RoundsIdeoligionResult Step(CancellationToken call, CancellationToken epoch, StepArbiter arbiter)
    {
        var p = Reviewer.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return new RoundsIdeoligionResult { Verdict = BuildingReasonDisabled };
        }

        if (!state.ObservationKnown || !state.Snapshot.IsValid)
        {
            throw new ControlException("step: observation unknown or snapshot invalid");
        }

        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || !review.Snapshot.Matches(state.Snapshot))
        {
            return new RoundsIdeoligionResult { Verdict = BuildingReasonNoReview };
        }

        var (concern, workable) = p.Journal.Workable(call, review, ConcernId.ImproveIdeoligion);
        if (!workable)
        {
            return new RoundsIdeoligionResult { Verdict = BuildingReasonNoDeficit };
        }

        foreach (var m in concern.Methods)
        {
            var open = p.Journal.LoadPlan(call, m.Plan);
            if (StorePackage.PlanOpen(open))
            {
                return new RoundsIdeoligionResult { Verdict = BuildingReasonExistingWork };
            }
        }

        var expected = StepScope(call, Reviewer.Native);
        if (!RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("step: boundary moved");
        }

        var started = Reviewer.Clock.Now();
        var read = Reviewer.ObserveOwned(call, Reviewer.Native, expected, Unknown<IReadOnlyList<ConstructionClaim>>());
        if (!Reviewer.ReformChoice(read, state.Snapshot).TryGetValue(out var choice) || choice.Design.Memes.Count == 0)
        {
            return new RoundsIdeoligionResult { Verdict = WaitFor(Cause.MethodUsed, "ideoligion_design"), Comparison = choice?.Comparison };
        }

        read.Projection.Facts.Ideology.TryGetValue(out var ideo);
        ideo!.Facts.Development.TryGetValue(out var development);
        var precepts = new List<string>();
        foreach (var held in ideo.Facts.Precepts)
        {
            var def = BridgePackage.DefRow<Defs.PreceptDef>(read.Frame.Catalog!, held.Def);
            if (def is not null && def.PreceptClass == "RimWorld.Precept")
            {
                precepts.Add(held.Def);
            }
        }

        var current = new IdeoligionDesign { Memes = ideo.Facts.Memes, Fluid = true, Precepts = precepts };
        var prefix = $"ideoligion-{development.ReformCount}-";
        var (method, verdict, ok) = AdmitStandardMethod(call, p.Journal, concern, prefix, state.Snapshot);
        if (!ok)
        {
            return new RoundsIdeoligionResult { Verdict = verdict };
        }

        var intent = IdeoligionReform.NewIdeoligionReform(ideo.Facts.IdeoId, current, choice.Design, development.ReformCount);
        if (concern.Standard.Record.Length != 0)
        {
            var pending = ReformRecord(concern.Standard.Record);
            if (ObservedReform(ideo, current, pending).TryGetValue(out var complete) && complete)
            {
                return new RoundsIdeoligionResult { Verdict = BuildingReasonNoDeficit };
            }

            var actual = IdeoligionDesign.CanonicalIdeoligionDesign(current);
            if (pending.IdeoId == ideo.Facts.IdeoId && pending.Count == development.ReformCount && actual == pending.Expected && pending.Design == choice.Design)
            {
                intent = pending;
            }
        }

        var id = PlanIds.MintPlanId();
        var action = DomainAction.NewIdeoligionReformAction(new ActionId($"{id}-0"), intent);
        var plan = PlanSpec.NewPlan(id, new PlanRevision(1), [action]);
        p.Current(call, epoch);
        var elapsed = Reviewer.Clock.Now() - started;
        if (p.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > Reviewer.MaxAge)
        {
            throw new ControlException("step: session state changed or the read took too long");
        }

        var record = ReformRecordText(intent);
        var comparison = choice.Comparison;
        var reason = FormattableString.Invariant($"ideoligion: restrictions {comparison.Current.Restrictions}→{comparison.Candidate.Restrictions}, mood cost {comparison.Current.MoodCost:G}→{comparison.Candidate.MoodCost:G}, obligations {comparison.Current.Obligations}→{comparison.Candidate.Obligations}; work relief {(comparison.WorkRelief ? "true" : "false")}, observed mood relief {comparison.MoodRelief:G}, new mood cost {comparison.NewMoodCost:G}");
        p.Journal.CommitMethodRecord(call, concern.Standard.Id, concern.Revision, method, reason, plan, record);
        return new RoundsIdeoligionResult { Verdict = BuildingReasonAdmitted, Plan = id, Comparison = comparison };
    }
}
