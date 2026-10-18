using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Store;

namespace RimGovernor.Brain.PlayerApi;

/// <summary>
/// The in-game goals panel's state apart from its drawing (#3497): the cached model the draw reads without blocking,
/// the poll that refreshes it and the queue of edits. The host's seams are fetched per use (null while no brain
/// runs). Nothing here awaits on the caller; the game thread only reads <see cref="Model"/> and <see cref="Message"/>
/// and starts <see cref="RefreshAsync"/> and <see cref="Submit"/>.
/// </summary>
public sealed class GoalsPanel(Func<IGoalsView?> view, Func<IGoalsEditor?> editor, Func<PresetBook?>? presets = null)
{
    private readonly object _mu = new();
    private readonly Queue<Func<GoalsRecord, GoalEdit>> _edits = new();
    private GoalsSnapshot? _snapshot;
    private GoalsViewModel? _model;
    private string _message = "";
    private bool _saving;
    private bool _refreshing;
    private long _version;

    /// <summary>The shipped presets over the installed catalog; null until the host has built the catalog.</summary>
    public PresetBook? Presets => presets?.Invoke();

    /// <summary>The latest model; null until the first refresh, or while no brain runs.</summary>
    public GoalsViewModel? Model
    {
        get
        {
            lock (_mu)
            {
                return _model;
            }
        }
    }

    /// <summary>Whether the brain's goals seam exists; false shows "brain not started".</summary>
    public bool BrainStarted => view() is not null && editor() is not null;

    /// <summary>The last edit's outcome: "Saved", or the reason it was refused; empty before any edit.</summary>
    public string Message
    {
        get
        {
            lock (_mu)
            {
                return _message;
            }
        }
    }

    /// <summary>Reads the snapshot and replaces the model, unless an edit is queued or saved meanwhile (its result is newer).</summary>
    public Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (view() is not { } source)
        {
            return Task.CompletedTask;
        }
        long version;
        lock (_mu)
        {
            if (_refreshing || _saving || _edits.Count > 0)
            {
                return Task.CompletedTask;
            }
            _refreshing = true;
            version = _version;
        }
        return Task.Run(
            () =>
            {
                try
                {
                    var snapshot = source.Current();
                    var model = GoalsViewModel.From(snapshot, Presets);
                    lock (_mu)
                    {
                        if (_version == version)
                        {
                            (_snapshot, _model) = (snapshot, model);
                        }
                    }
                }
                finally
                {
                    lock (_mu)
                    {
                        _refreshing = false;
                    }
                }
            },
            cancellationToken);
    }

    /// <summary>
    /// Queues an edit of the latest record. Edits apply in order, one save at a time, each to the record the one before
    /// left, so an edit made during a save is never lost. A refused edit sets <see cref="Message"/> and changes nothing.
    /// </summary>
    public void Submit(Func<GoalsRecord, GoalEdit> edit)
    {
        lock (_mu)
        {
            _edits.Enqueue(edit);
            if (_saving)
            {
                return;
            }
            _saving = true;
        }
        _ = Task.Run(PumpAsync);
    }

    private async Task PumpAsync()
    {
        while (true)
        {
            Func<GoalsRecord, GoalEdit> edit;
            GoalsSnapshot? snapshot;
            lock (_mu)
            {
                if (!_edits.TryDequeue(out edit!))
                {
                    _saving = false;
                    return;
                }
                snapshot = _snapshot;
            }
            string message;
            if (snapshot is null || editor() is not { } target)
            {
                message = "The brain has not started.";
            }
            else
            {
                message = await SaveAsync(edit, snapshot, target).ConfigureAwait(false);
            }
            lock (_mu)
            {
                _message = message;
            }
        }
    }

    private async Task<string> SaveAsync(Func<GoalsRecord, GoalEdit> edit, GoalsSnapshot snapshot, IGoalsEditor target)
    {
        var result = edit(snapshot.Record);
        if (!result.Ok)
        {
            return result.Error!;
        }
        try
        {
            await target.SaveAsync(result.Record, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The store or the validation refused; the panel shows why and keeps the previous record.
            return e.Message;
        }
        var saved = snapshot with { Record = result.Record };
        lock (_mu)
        {
            _version++;
            (_snapshot, _model) = (saved, GoalsViewModel.From(saved, Presets));
        }
        return "Saved";
    }
}
