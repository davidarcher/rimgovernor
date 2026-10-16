namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>The native read of the open dialog window.</summary>
public interface IRoundsDialogSource : IColonyFactsReader
{
}

/// <summary>
/// The runtime half of the people family (#3304): its read (the colony census's open choice dialog) and the
/// people/dialog Need's step, which answers the open window with the option the answer policy picks, once per
/// window and option, committed under the Need and claiming the window. It replaced the AnswerDialog planner.
/// </summary>
public sealed class PeopleRounds
{
    private readonly Rounder _reviewer;
    private readonly IRoundsDialogSource _native;
    private readonly DialogAnswerPolicy _policy;

    public PeopleRounds(Rounder reviewer, IRoundsDialogSource native, DialogAnswerPolicy answer)
    {
        _reviewer = reviewer ?? throw new ControlException("PeopleRounds: reviewer is missing");
        _native = native ?? throw new ControlException("PeopleRounds: native is missing");
        _policy = answer ?? throw new ControlException("PeopleRounds: answer policy is missing");
    }

    /// <summary>A critical entry, as the dialog planner was: a force-paused game waits on its answer.</summary>
    public static FamilySchedule Schedule { get; } = new()
    {
        Class = PlannerClass.Critical,
        Kinds = [ActionKind.DialogAnswer],
    };

    /// <summary>The family's scheduler entry.</summary>
    public FamilyRunner Runner() => new FamilyRunner<PeopleFacts>(new PeopleFamily(), ReadAsync, Schedule, WorkAsync);

    /// <summary>Reads whether a choice dialog is open in <paramref name="scope"/>'s world. The immediate pass reads nothing.</summary>
    internal async Task<PeopleFacts> ReadAsync(FamilyReadScope scope, CancellationToken call)
    {
        if (scope.Immediate)
        {
            return new PeopleFacts();
        }
        var review = await _reviewer.Player.Journal.LoadRoundsAsync(call).ConfigureAwait(false);
        var dialog = await ObserveAsync(scope.World, review.Tick, call).ConfigureAwait(false);
        return new PeopleFacts { DialogOpen = Known(dialog is not null), Dialog = dialog };
    }

    /// <summary>
    /// The people/dialog step: open work waits; the window is read again, and an interactive one is answered with the
    /// policy's option unless its Method was already bound for this window.
    /// </summary>
    internal async Task<Verdict> WorkAsync(NeedWork work, PeopleFacts facts, CancellationToken call)
    {
        if (work.Immediate || work.Need.Id != PeopleFamily.Dialog)
        {
            return AwaitingPlan(work.Need.Id.Value, "no_method");
        }
        var p = _reviewer.Player;
        var state = work.State;
        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || review.Snapshot != state.Snapshot)
        {
            return BuildingReasonNoReview;
        }
        if (work.Owner.Methods.Any(m => Standard.WorkOpen(p.Journal.LoadPlan(m.Plan, call).Progress)))
        {
            return BuildingReasonExistingWork;
        }
        var started = _reviewer.Clock.Now();
        var dialog = await ObserveAsync(state.Snapshot, review.Tick, call).ConfigureAwait(false);
        if (dialog is null)
        {
            return BuildingReasonNoDeficit;
        }
        if (!dialog.Window.TryGetValue(out var window))
        {
            return FieldUnavailable("dialog");
        }
        if (!dialog.Interactive)
        {
            return BuildingReasonNotInteractive;
        }
        if (PolicyApi.VoidNodeDisruptAbsent(dialog.Options))
        {
            DefenseAction("routine-dialog", TelemetryLevel.Error, "refused", "void_node_disrupt_absent", "void_node", new Dictionary<string, object?> { ["window"] = window }, call);
        }
        var chosen = PolicyApi.ChooseDialogOption(dialog.Options, _policy);
        if (chosen is null)
        {
            return WaitFor(Cause.MethodUsed, "no_selectable_dialog_option");
        }
        var method = PeopleFamily.DialogMethod(window, chosen);
        if (p.Journal.OwnerMethodPlan(work.Owner, method, call) is not null)
        {
            return WaitFor(Cause.MethodUsed, "dialog_method");
        }
        var id = PlanIds.MintPlanId();
        var action = DomainAction.NewDialogAnswerAction(new ActionId($"{id}-0"), new DialogAnswer(window, chosen.Index, chosen.Label));
        p.Current(call, work.Epoch);
        var elapsed = _reviewer.Clock.Now() - started;
        if (p.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > _reviewer.MaxAge)
        {
            throw new ControlException("dialog: the session moved or the read took too long");
        }
        var commit = p.Journal.CommitClaimedMethod(work.Need.Id, work.Owner.Snapshot, method, "dialog: answer " + chosen.Label, new PlanSpec(id, new PlanRevision(1), [action]), call);
        return AdminRounds.OutcomeVerdict([(commit.Outcome, commit.Conflict)]) ?? BuildingReasonAdmitted;
    }

    /// <summary>The open choice dialog of <paramref name="world"/>'s colony census, null when none is; throws when the read is another world or older than the review.</summary>
    private async Task<OpenDialog?> ObserveAsync(GenerationSnapshot world, Tick reviewed, CancellationToken call)
    {
        var reply = await _reviewer.ColonyFactsAsync(_native, Boundary.Identity(world), false, call).ConfigureAwait(false);
        var observed = reply.Outcome?.Observed ?? throw new ControlException("dialog read: observed == nil");
        try
        {
            Boundary.Context(observed.Context, world);
        }
        catch (ControlException e)
        {
            throw new ControlException("dialog read: the census is from another world", e);
        }
        if ((observed.Context?.Tick).GetValueOrDefault() < reviewed.Value)
        {
            throw new ControlException("dialog read: the census is older than the review");
        }
        if (observed.Dialog is not { } dialog)
        {
            return null;
        }
        var options = new List<DialogOption>(dialog.Options.Count);
        foreach (var option in dialog.Options)
        {
            options.Add(new DialogOption { Index = option.Index.GetValueOrDefault(), Label = option.Label ?? "", Keys = [.. option.Keys], Selectable = option.Selectable.GetValueOrDefault(), Resolves = option.Resolves.GetValueOrDefault() });
        }
        return new OpenDialog
        {
            Window = dialog.WindowId is { } id ? Known(id) : Unknown<int>(),
            Interactive = dialog.Interactive.GetValueOrDefault(),
            Options = EquatableArray.From(options),
        };
    }
}
