namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>Focuses the native map view on a target (Go <c>playerMapFocus</c>), implemented by the session.</summary>
internal interface IPlayerMapFocus
{
    Task<NativeGeneration> FocusMapAsync(GenerationSnapshot before, GenerationSnapshot target, CancellationToken ct);
}

/// <summary>Proves the view changed while the prior Auto grant was live (Go <c>playerMapFocusConfirmation</c>).</summary>
internal interface IPlayerMapFocusConfirmation
{
    Task ConfirmMapFocusAsync(GenerationSnapshot before, GenerationSnapshot target, NativeGeneration generation, CancellationToken ct);
}

public sealed partial class Player
{
    internal async Task<bool> ExpeditionIncomingHandoffAsync(PbObservations.BundleSnapshot? loaded, CancellationToken call, CancellationToken epoch)
    {
        var state = _session.State();
        if (!state.ObservationKnown || loaded?.Context?.Identity is null || loaded.WorldProgression is null)
        {
            return false;
        }
        var identity = loaded.Context.Identity;
        if (identity.ColonyId != state.Snapshot.Colony.Value || identity.LoadToken != state.Snapshot.Load.Value || new MapId(identity.MapId) == state.Snapshot.Map)
        {
            return false;
        }
        var prior = await _journal.CurrentControlAsync(call).ConfigureAwait(false);
        if (prior.Phase != ControlPhase.Running || prior.Request.World != PlayerWorld(state.Snapshot))
        {
            return false;
        }
        var world = BridgeApi.DecodeWorldProgressionSnapshot(loaded.WorldProgression, identity);
        var plans = await Task.Run(() => _journal.PlanHistoryWithMethods(256, ["quest-expedition-*", "quest-site-*-return-*"], call), call).ConfigureAwait(false);
        if (!ExpeditionHandoffTarget(state.Snapshot, world, plans).TryGetValue(out var target) || target.Map != new MapId(identity.MapId))
        {
            return false;
        }
        if (_session is not IPlayerMapFocusConfirmation confirm)
        {
            return false;
        }
        var generation = new NativeGeneration(loaded.Context.NativeGeneration);
        _session.Disable();
        await confirm.ConfirmMapFocusAsync(state.Snapshot, target, generation, call).ConfigureAwait(false);
        return await HandoffHeldAsync(state, target, generation, call, epoch).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs under the caller's player gate. Durable departure evidence authorizes only its exact physical
    /// destination and observed crew.
    /// </summary>
    internal async Task<bool> ExpeditionHandoffAsync(ControlState state, RoundsReading read, CancellationToken call, CancellationToken epoch)
    {
        if (!state.Enabled || !state.ObservationKnown || read.Frame.Quests is null)
        {
            return false;
        }
        var prior = await _journal.CurrentControlAsync(call).ConfigureAwait(false);
        if (prior.Phase != ControlPhase.Running || prior.Request.World != PlayerWorld(state.Snapshot))
        {
            return false;
        }
        var plans = await Task.Run(() => _journal.PlanHistoryWithMethods(256, ["quest-expedition-*", "quest-site-*-return-*"], call), call).ConfigureAwait(false);
        if (!ExpeditionHandoffTarget(state.Snapshot, read.Frame.Quests, plans).TryGetValue(out var target))
        {
            return false;
        }
        return await HandoffHeldAsync(state, target, null, call, epoch).ConfigureAwait(false);
    }

    private async Task<bool> HandoffHeldAsync(ControlState state, GenerationSnapshot target, NativeGeneration? alreadyFocused, CancellationToken call, CancellationToken epoch)
    {
        if (_session is not IPlayerMapFocus focus)
        {
            return false;
        }
        Current(call, epoch);
        var request = new ControlRequest(
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"expedition-focus-{state.Snapshot.Colony.Value}-{state.Snapshot.Load.Value}-{target.Map.Value}-{state.Snapshot.Native.Value}"),
            ControlKind.Resume,
            PlayerWorld(target));
        var (record, created) = await _journal.BeginControlAsync(request, call).ConfigureAwait(false);
        if (!created)
        {
            return false;
        }
        NativeGeneration generation;
        try
        {
            _session.Disable();
            _ = await StopRoundsAsync(call).ConfigureAwait(false);
            Current(call, epoch);
            generation = alreadyFocused ?? await focus.FocusMapAsync(state.Snapshot, target, call).ConfigureAwait(false);
            Current(call, epoch);
            await WorldAsync(request.World, call).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // The uncertain record always throws; the handoff is reported as handled (Go returned true).
            await UncertainAsync(record, e).ConfigureAwait(false);
            throw;
        }
        _ = await CompleteResumeHeldAsync(record, generation, call, epoch).ConfigureAwait(false);
        return true;
    }

    internal static Fact<GenerationSnapshot> ExpeditionHandoffTarget(GenerationSnapshot current, WorldProgressionRead world, IReadOnlyList<PlanState> plans)
    {
        foreach (var plan in plans)
        {
            foreach (var action in plan.Spec.Actions)
            {
                if (action.CaravanDeparture is not { } departure)
                {
                    continue;
                }
                var proof = new ProgressView();
                foreach (var progress in plan.Progress)
                {
                    if (progress.View.Action == action.Id)
                    {
                        proof = progress.View;
                        break;
                    }
                }
                if (!proof.Receipt.TryGetValue(out var receipt) || receipt != Domain.Receipt.Accepted || proof.Snapshot.Colony != current.Colony || proof.Snapshot.Load != current.Load || proof.Snapshot.Map != current.Map)
                {
                    continue;
                }
                foreach (var m in world.Maps)
                {
                    if (new MapId(m.Id) == current.Map || m.Tile != departure.DestinationTile)
                    {
                        continue;
                    }
                    var crew = departure.Crew;
                    if (crew.Count == 0 || !AllExpeditionCrewPresent(crew, m.PawnIds))
                    {
                        continue;
                    }
                    if (!m.Home)
                    {
                        var linked = false;
                        foreach (var site in world.Sites)
                        {
                            if (site.MapId == m.Id && site.HasMapId && site.Tile == m.Tile)
                            {
                                foreach (var quest in site.QuestIds)
                                {
                                    if (plan.Method.Value.StartsWith("quest-expedition-" + quest + "-" + site.Id + "-", StringComparison.Ordinal))
                                    {
                                        linked = true;
                                        break;
                                    }
                                }
                            }
                        }
                        if (!linked)
                        {
                            continue;
                        }
                    }
                    return Known(current with { Map = new MapId(m.Id) });
                }
            }
        }
        return Unknown<GenerationSnapshot>();
    }

    private static bool AllExpeditionCrewPresent(IReadOnlyList<Domain.PawnId> crew, IReadOnlyList<string> present)
    {
        foreach (var pawn in crew)
        {
            if (!present.Contains(pawn.Value, StringComparer.Ordinal))
            {
                return false;
            }
        }
        return true;
    }
}
