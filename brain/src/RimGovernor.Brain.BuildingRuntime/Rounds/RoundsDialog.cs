using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>The native read of the open dialog window.</summary>
public interface IRoundsDialogSource
{
    (O.ColonyFactsReply Reply, BridgeResult Result) ReadColonyFacts(C.Identity identity, bool full, CancellationToken ct);
}

public sealed class RoundsDialogResult
{
    public Verdict Verdict { get; init; }

    public PlanId Plan { get; init; }

    public string Option { get; init; } = "";
}

/// <summary>Answers an open dialog window with the option the answer policy picks, once per window and option.</summary>
public sealed class RoundsDialogPlanner
{
    private readonly Rounder _reviewer;
    private readonly IRoundsDialogSource _native;
    private readonly DialogAnswerPolicy _policy;

    public RoundsDialogPlanner(Rounder reviewer, IRoundsDialogSource native, DialogAnswerPolicy answer)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("NewRoundsDialogPlanner: reviewer == nil || native == nil");
        }
        _reviewer = reviewer;
        _native = native;
        _policy = answer;
    }

    internal RoundsDialogResult Step(CancellationToken call, CancellationToken epoch, StepArbiter arbiter)
    {
        var p = _reviewer.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return new RoundsDialogResult { Verdict = BuildingReasonDisabled };
        }
        if (!state.ObservationKnown || !state.Snapshot.IsValid)
        {
            throw new ControlException("step: !state.ObservationKnown || state.Snapshot.Validate() != nil");
        }
        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || review.Snapshot != state.Snapshot)
        {
            return new RoundsDialogResult { Verdict = BuildingReasonNoReview };
        }
        var (incident, found) = IncidentDeficit(call, p.Journal, review, PolicyApi.AnswerDialog);
        if (!found)
        {
            return new RoundsDialogResult { Verdict = BuildingReasonNoDeficit };
        }
        if (IncidentOpenWork(call, p.Journal, incident!))
        {
            return new RoundsDialogResult { Verdict = BuildingReasonExistingWork };
        }
        var identity = Boundary.Identity(state.Snapshot);
        var (reply, _) = _reviewer.ColonyFacts(call, _native, identity, false);
        var observed = reply.Observed;
        if (observed is null)
        {
            throw new ControlException("step: observed == nil");
        }
        try
        {
            Boundary.Context(observed.Context, state.Snapshot);
        }
        catch (ControlException)
        {
            throw new ControlException("step: err != nil || observed.Context.GetTick() < int64(review.Tick)");
        }
        if (observed.Context.Tick < (long)review.Tick)
        {
            throw new ControlException("step: err != nil || observed.Context.GetTick() < int64(review.Tick)");
        }
        var dialog = observed.Dialog;
        if (dialog is null || !dialog.HasWindowId)
        {
            return new RoundsDialogResult { Verdict = FieldUnavailable("dialog") };
        }
        if (!dialog.Interactive)
        {
            return new RoundsDialogResult { Verdict = BuildingReasonNotInteractive };
        }
        var options = new List<DialogOption>(dialog.Options.Count);
        foreach (var option in dialog.Options)
        {
            options.Add(new DialogOption { Index = option.Index, Label = option.Label, Keys = option.Keys, Selectable = option.Selectable, Resolves = option.Resolves });
        }
        if (PolicyApi.VoidNodeDisruptAbsent(options))
        {
            DefenseAction(call, "routine-dialog", TelemetryLevel.Error, "refused", "void_node_disrupt_absent", "void_node", new Dictionary<string, object?> { ["window"] = dialog.WindowId });
        }
        var (chosen, ok) = PolicyApi.ChooseDialogOption(options, _policy);
        if (!ok)
        {
            return new RoundsDialogResult { Verdict = WaitFor(PolicyApi.CauseMethodUsed, "no_selectable_dialog_option") };
        }
        var windowId = dialog.WindowId;
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{windowId}/{chosen.Index}/{chosen.Label}")));
        var method = new MethodId("dialog-" + Convert.ToHexStringLower(digest.AsSpan(0, 16)));
        if (incident!.Methods.Any(m => m.Method == method))
        {
            return new RoundsDialogResult { Verdict = WaitFor(PolicyApi.CauseMethodUsed, "dialog_method") };
        }
        var id = PlanIds.MintPlanId();
        var value = DomainApi.NewDialogAnswer(windowId, chosen.Index, chosen.Label);
        var action = DomainAction.NewDialogAnswerAction(new ActionId($"{id}-0"), value);
        var plan = new PlanSpec(id, new PlanRevision(1), [action]);
        p.Current(call, epoch);
        if (p.Session.State() != state)
        {
            throw new ControlException("step: p.session.State() != state");
        }
        p.Journal.CommitIncidentMethod(incident.Incident.Id, method, "", plan, call);
        return new RoundsDialogResult { Verdict = BuildingReasonAdmitted, Plan = id, Option = chosen.Label };
    }
}
