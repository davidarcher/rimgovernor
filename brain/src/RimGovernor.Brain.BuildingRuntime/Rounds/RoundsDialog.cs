using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>The native read of the open dialog window.</summary>
public interface IRoundsDialogSource : IColonyFactsReader
{
}

public sealed class RoundsDialogResult : IPlannerMethod
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

    internal async Task<RoundsDialogResult> StepAsync(StepArbiter arbiter, CancellationToken call, CancellationToken epoch)
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
        var (incident, found) = IncidentDeficit(p.Journal, review, PolicyApi.AnswerDialog, call);
        if (!found)
        {
            return new RoundsDialogResult { Verdict = BuildingReasonNoDeficit };
        }
        if (IncidentOpenWork(p.Journal, incident!, call))
        {
            return new RoundsDialogResult { Verdict = BuildingReasonExistingWork };
        }
        var identity = Boundary.Identity(state.Snapshot);
        var reply = await _reviewer.ColonyFactsAsync(_native, identity, false, call).ConfigureAwait(false);
        var observed = reply.Outcome?.Observed;
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
        if ((observed.Context?.Tick).GetValueOrDefault() < review.Tick.Value)
        {
            throw new ControlException("step: err != nil || observed.Context.GetTick() < int64(review.Tick)");
        }
        var dialog = observed.Dialog;
        if (dialog is null || dialog.WindowId is null)
        {
            return new RoundsDialogResult { Verdict = FieldUnavailable("dialog") };
        }
        if (!dialog.Interactive.GetValueOrDefault())
        {
            return new RoundsDialogResult { Verdict = BuildingReasonNotInteractive };
        }
        var options = new List<DialogOption>(dialog.Options.Count);
        foreach (var option in dialog.Options)
        {
            options.Add(new DialogOption { Index = option.Index.GetValueOrDefault(), Label = option.Label ?? "", Keys = [.. option.Keys], Selectable = option.Selectable.GetValueOrDefault(), Resolves = option.Resolves.GetValueOrDefault() });
        }
        if (PolicyApi.VoidNodeDisruptAbsent(options))
        {
            DefenseAction("routine-dialog", TelemetryLevel.Error, "refused", "void_node_disrupt_absent", "void_node", new Dictionary<string, object?> { ["window"] = dialog.WindowId.GetValueOrDefault() }, call);
        }
        var chosen = PolicyApi.ChooseDialogOption(options, _policy);
        if (chosen is null)
        {
            return new RoundsDialogResult { Verdict = WaitFor(Cause.MethodUsed, "no_selectable_dialog_option") };
        }
        var windowId = dialog.WindowId.GetValueOrDefault();
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{windowId}/{chosen.Index}/{chosen.Label}")));
        var method = new MethodId("dialog-" + Convert.ToHexStringLower(digest.AsSpan(0, 16)));
        if (incident!.Methods.Any(m => m.Method == method))
        {
            return new RoundsDialogResult { Verdict = WaitFor(Cause.MethodUsed, "dialog_method") };
        }
        var id = PlanIds.MintPlanId();
        var value = new DialogAnswer(windowId, chosen.Index, chosen.Label);
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
