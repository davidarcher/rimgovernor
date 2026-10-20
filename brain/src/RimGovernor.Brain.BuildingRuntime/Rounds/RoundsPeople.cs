using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// The runtime half of the people family (#3304): its read (the colony census's open choice dialog, and the review's
/// mood states with the Needs at work) and the steps. The people/dialog step answers the open window with the
/// option the answer policy picks, once per window and option, committed under the Need and claiming the window; it
/// replaced the AnswerDialog planner. A people/mood step commits nothing: it names what the pawn's mood waits on; it
/// replaced the mood relief planner.
/// </summary>
public sealed class PeopleRounds
{
    private readonly Rounder _reviewer;
    private readonly PeopleNeeds _needs;
    private readonly FacadeNativeClient? _native;
    private readonly DialogAnswerPolicy? _policy;

    /// <summary>The dialog Need reads and answers through <paramref name="native"/> with <paramref name="answer"/>, required when it is composed.</summary>
    public PeopleRounds(Rounder reviewer, PeopleNeeds needs, FacadeNativeClient? native, DialogAnswerPolicy? answer)
    {
        _reviewer = reviewer ?? throw new ControlException("PeopleRounds: reviewer is missing");
        _needs = needs ?? throw new ControlException("PeopleRounds: needs are missing");
        if (needs.Dialog)
        {
            _native = native ?? throw new ControlException("PeopleRounds: native is missing");
            _policy = answer ?? throw new ControlException("PeopleRounds: answer policy is missing");
        }
    }

    /// <summary>A critical entry, as the dialog planner was: a force-paused game waits on its answer. The mood step reads only the journal.</summary>
    public static FamilySchedule Schedule { get; } = new()
    {
        Class = PlannerClass.Critical,
        Kinds = [ActionKind.DialogAnswer],
    };

    /// <summary>The family's scheduler entry.</summary>
    public FamilyRunner Runner() => new FamilyRunner<PeopleFacts>(new PeopleFamily(_needs), ReadAsync, Schedule, WorkAsync);

    /// <summary>
    /// Reads, in <paramref name="scope"/>'s world, whether a choice dialog is open and the review's mood states with the
    /// Needs at work (each composed Need's read). The immediate pass reads nothing.
    /// </summary>
    internal async Task<PeopleFacts> ReadAsync(FamilyReadScope scope, CancellationToken call)
    {
        if (scope.Immediate)
        {
            return new PeopleFacts();
        }
        var review = await _reviewer.Player.Journal.LoadRoundsAsync(call).ConfigureAwait(false);
        var facts = new PeopleFacts();
        if (_needs.Dialog)
        {
            var dialog = await ObserveAsync(scope.World, review.Tick, call).ConfigureAwait(false);
            facts = facts with { DialogOpen = Known(dialog is not null) };
        }
        if (_needs.Mood && review.Snapshot == scope.World)
        {
            facts = facts with
            {
                Mood = Known(EquatableArray.From(review.MoodHistory().States)),
                Working = EquatableArray.From(review.Progress.Where(p => p.Method.Length > 0 && new NeedId(p.Concern.Value).IsValid)
                    .Select(p => new NeedId(p.Concern.Value))),
            };
        }
        return facts;
    }

    /// <summary>
    /// The people/dialog step: open work waits; the window is read again, and an interactive one is answered with the
    /// policy's option unless its Method was already bound for this window. A people/mood step is <see cref="MoodVerdict"/>.
    /// </summary>
    internal async Task<Verdict> WorkAsync(NeedWork work, PeopleFacts facts, CancellationToken call)
    {
        if (!work.Immediate && PeopleFamily.IsMood(work.Need.Id))
        {
            return MoodVerdict(work.Need.Id, facts);
        }
        if (work.Immediate || work.Need.Id != PeopleFamily.Dialog || _native is null || _policy is null)
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
        if (work.Owner.Methods.Any(m => WorkProgress.Open(p.Journal.LoadPlan(m.Plan, call).Progress)))
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
        if (DialogAnswerRules.VoidNodeDisruptAbsent(dialog.Options))
        {
            DefensePipeline.DefenseAction("routine-dialog", TelemetryLevel.Error, "refused", "void_node_disrupt_absent", "void_node", new Dictionary<string, object?> { ["window"] = window }, call);
        }
        var chosen = DialogAnswerRules.ChooseDialogOption(dialog.Options, _policy);
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
        return AdminRounds.OutcomeVerdict([commit]) ?? BuildingReasonAdmitted;
    }

    /// <summary>
    /// What an unmet mood Need waits on: the owner Need of the pawn's dominant thought pressure while that Need is at
    /// work; a preventative cast, held as unsupported, when no measured need relief remains; otherwise the shared
    /// portfolio's need relief (or the pawn's own recovery from a break, or its release from player work).
    /// </summary>
    internal static Verdict MoodVerdict(NeedId need, PeopleFacts facts)
    {
        var pawn = facts.Mood.TryGetValue(out var states) ? states.FirstOrDefault(s => PeopleFamily.Mood(s.Pawn.Id) == need) : null;
        if (pawn is null)
        {
            return FieldUnavailable(need.Value);
        }
        var proposal = MoodRules.MoodNeedProposal(pawn, [.. facts.Working]);
        return proposal.Reason switch
        {
            MoodMethodReason.Provisioned => WaitFor(Cause.WaitingOnNeed, proposal.Provider.Value),
            MoodMethodReason.NoCause or MoodMethodReason.Exhausted or MoodMethodReason.Unowned => AwaitingPlan("unsupported_forecast_mood_cast",
                "Preventative mood casts require shared caster time, psyfocus and mood-outcome alternatives."),
            _ => WaitFor(Cause.MethodUsed, "mood_relief_method"),
        };
    }

    /// <summary>The open choice dialog of <paramref name="world"/>'s colony census, null when none is; throws when the read is another world or older than the review.</summary>
    private async Task<OpenDialog?> ObserveAsync(GenerationSnapshot world, Tick reviewed, CancellationToken call)
    {
        var reply = await _reviewer.ColonyFactsAsync(_native!, Boundary.Identity(world), false, call).ConfigureAwait(false);
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
