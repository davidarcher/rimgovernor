using System.Text.Json;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>The persisted text of a pending reform (a <c>Standard.Record</c>); JSON with the Go field names.</summary>
public sealed partial class Rounder
{
    /// <summary>The reform the colony's ideoligion owes, or unknown while any input is unread or the pawns disagree on one faction.</summary>
    internal static Fact<DesignChoice> ReformChoice(RoundsReading read, GenerationSnapshot snapshot)
    {
        if (!read.Projection.Facts.Ideology.TryGetValue(out var ideo) || read.Catalog is not { } catalog || read.Frame.Pawns is null)
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

        DesignOptions options;
        try
        {
            options = catalog.IdeoligionOptions(faction);
        }
        catch (DomainException)
        {
            return Unknown<DesignChoice>();
        }

        options = options with { Needs = PolicyFunctions.IdeoligionNeeds(ideo.Facts, read.Projection.WorkPawns, read.Projection.Facts.MoodPawns) };
        var precepts = new List<string>();
        foreach (var held in ideo.Facts.Precepts)
        {
            var def = catalog.DefRow<RimWorld.PreceptDef>(held.Def);
            if (def is null)
            {
                return Unknown<DesignChoice>();
            }

            if (def.preceptClass == typeof(RimWorld.Precept))
            {
                precepts.Add(held.Def);
            }
        }

        var current = new IdeoligionDesign([.. ideo.Facts.Memes], [.. precepts], true);
        var calm = Unknown<bool>();
        var tick = read.Projection.Identity.Tick;
        try
        {
            var emergency = EmergencySnapshot.New(snapshot, tick, read.Emergency);
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
    internal void ReviewIdeoligion(ref RoundsReading read, GenerationSnapshot snapshot, CancellationToken ct)
    {
        var choice = ReformChoice(read, snapshot);
        var owed = choice.TryGetValue(out var v) ? Known(v.Design.Memes.Count > 0) : Unknown<bool>();
        read = WithReformOwed(read, owed);
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
            if (binding.Concern == PolicyConstants.ImproveIdeoligion)
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
            standard = Player.Journal.LoadStandard(id, ct);
        }
        catch (NotFoundException)
        {
            return;
        }

        if (string.IsNullOrEmpty(standard.Standard.Record))
        {
            return;
        }

        var intent = ReformRecord(standard.Standard.Record ?? "");
        if (!read.Projection.Facts.Ideology.TryGetValue(out var ideo))
        {
            read = WithReformOwed(read, Unknown<bool>());
            return;
        }

        if (read.Catalog is not { } catalog)
        {
            read = WithReformOwed(read, Unknown<bool>());
            return;
        }

        var precepts = new List<string>();
        foreach (var held in ideo.Facts.Precepts)
        {
            var def = catalog.DefRow<RimWorld.PreceptDef>(held.Def);
            if (def is null)
            {
                read = WithReformOwed(read, Unknown<bool>());
                return;
            }

            if (def.preceptClass == typeof(RimWorld.Precept))
            {
                precepts.Add(held.Def);
            }
        }

        var actual = new IdeoligionDesign([.. ideo.Facts.Memes], [.. precepts], true);
        if (ObservedReform(ideo, actual, intent).TryGetValue(out var complete) && complete)
        {
            read = WithReformOwed(read, Known(false));
            Player.Journal.RecordStandard(standard.Standard.Id, standard.Revision, "", ct);
            return;
        }

        if (ideo.Facts.Development.TryGetValue(out var state))
        {
            var canonical = IdeoligionDesign.CanonicalIdeoligionDesign(actual);
            if (state.ReformCount != intent.Count || canonical != intent.Expected || ideo.Facts.IdeoId != intent.IdeoId)
            {
                Player.Journal.RecordStandard(standard.Standard.Id, standard.Revision, "", ct);
            }
        }
    }
}

internal static partial class BuildingRuntimePackage
{
    internal static RoundsReading WithReformOwed(RoundsReading read, Fact<bool> owed) =>
        read with { Reading = read.Reading with { Projection = read.Projection with { Facts = read.Projection.Facts with { ReformOwed = owed } } } };

    internal static IdeoligionReform ReformRecord(string record)
    {
        using var doc = JsonDocument.Parse(record);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new DomainException("ideoligion record is empty");
        }
        IdeoligionDesign Design(string name) =>
            global::RimGovernor.Brain.Store.Codec.StoreJson.Deserialize<IdeoligionDesign>(root.GetProperty(name).GetRawText());
        return new IdeoligionReform(root.GetProperty("Ideo").GetString() ?? "", Design("Expected"), Design("Target"), root.GetProperty("Count").GetInt32());
    }

    internal static string ReformRecordText(IdeoligionReform intent)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("Ideo", intent.IdeoId);
            w.WritePropertyName("Expected");
            w.WriteRawValue(global::RimGovernor.Brain.Store.Codec.StoreJson.Serialize(intent.Expected));
            w.WritePropertyName("Target");
            w.WriteRawValue(global::RimGovernor.Brain.Store.Codec.StoreJson.Serialize(intent.Design));
            w.WriteNumber("Count", intent.Count);
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

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
        if (r is null || !r.MethodEnabled(PolicyConstants.ImproveIdeoligion))
        {
            throw new ControlException("ideoligion reviewer unavailable");
        }

        return new RoundsIdeoligionPlanner { Reviewer = r };
    }
}

public readonly record struct RoundsIdeoligionResult : IPlannerMethod
{
    public Verdict Verdict { get; init; }

    public PlanId Plan { get; init; }

    public DesignComparison? Comparison { get; init; }
}

public sealed partial class RoundsIdeoligionPlanner
{
    internal async Task<RoundsIdeoligionResult> StepAsync(StepArbiter arbiter, CancellationToken call, CancellationToken epoch)
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

        var (concern, workable) = p.Journal.Workable(review, PolicyConstants.ImproveIdeoligion, call);
        if (!workable || concern is null)
        {
            return new RoundsIdeoligionResult { Verdict = BuildingReasonNoDeficit };
        }

        foreach (var m in concern.Methods)
        {
            var open = p.Journal.LoadPlan(m.Plan, call);
            if (StorePackage.PlanOpen(open))
            {
                return new RoundsIdeoligionResult { Verdict = BuildingReasonExistingWork };
            }
        }

        var expected = await StepScopeAsync(Reviewer.Native, call).ConfigureAwait(false);
        if (!RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("step: boundary moved");
        }

        var started = Reviewer.Clock.Now();
        var read = await Reviewer.ObserveOwnedAsync(Reviewer.Native, expected, Unknown<EquatableArray<ConstructionClaim>>(), call).ConfigureAwait(false);
        if (!Rounder.ReformChoice(read, state.Snapshot).TryGetValue(out var choice) || choice.Design.Memes.Count == 0)
        {
            return new RoundsIdeoligionResult { Verdict = WaitFor(Cause.MethodUsed, "ideoligion_design"), Comparison = choice?.Comparison };
        }

        if (read.Catalog is not { } catalog || !read.Projection.Facts.Ideology.TryGetValue(out var ideo))
        {
            return new RoundsIdeoligionResult { Verdict = FieldUnavailable("ideoligion") };
        }

        ideo.Facts.Development.TryGetValue(out var development);
        var precepts = new List<string>();
        foreach (var held in ideo.Facts.Precepts)
        {
            var def = catalog.DefRow<RimWorld.PreceptDef>(held.Def);
            if (def is not null && def.preceptClass == typeof(RimWorld.Precept))
            {
                precepts.Add(held.Def);
            }
        }

        var current = new IdeoligionDesign([.. ideo.Facts.Memes], [.. precepts], true);
        var prefix = $"ideoligion-{development.ReformCount}-";
        var (method, verdict, ok) = AdmitStandardMethod(p.Journal, concern, prefix, state.Snapshot, call);
        if (!ok)
        {
            return new RoundsIdeoligionResult { Verdict = verdict };
        }

        var intent = new IdeoligionReform(ideo.Facts.IdeoId, current, choice.Design, development.ReformCount);
        if (!string.IsNullOrEmpty(concern.Standard.Record))
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
        var plan = new PlanSpec(id, new PlanRevision(1), [action]);
        p.Current(call, epoch);
        var elapsed = Reviewer.Clock.Now() - started;
        if (p.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > Reviewer.MaxAge)
        {
            throw new ControlException("step: session state changed or the read took too long");
        }

        var record = ReformRecordText(intent);
        if (choice.Comparison is not { } comparison)
        {
            return new RoundsIdeoligionResult { Verdict = WaitFor(Cause.MethodUsed, "ideoligion_design") };
        }

        var reason = FormattableString.Invariant($"ideoligion: restrictions {comparison.Current.Restrictions}→{comparison.Candidate.Restrictions}, mood cost {comparison.Current.MoodCost:G}→{comparison.Candidate.MoodCost:G}, obligations {comparison.Current.Obligations}→{comparison.Candidate.Obligations}; work relief {(comparison.WorkRelief ? "true" : "false")}, observed mood relief {comparison.MoodRelief:G}, new mood cost {comparison.NewMoodCost:G}");
        p.Journal.CommitMethodRecord(concern.Standard.Id, concern.Revision, method, reason, plan, record, call);
        return new RoundsIdeoligionResult { Verdict = BuildingReasonAdmitted, Plan = id, Comparison = comparison };
    }
}
