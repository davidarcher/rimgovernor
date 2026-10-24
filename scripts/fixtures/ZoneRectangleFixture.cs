#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Telemetry;
using RimGovernor.Contracts.Wire;
using RimGovernor.Fixtures.Contracts;
using RimGovernor.Host.Sdk;
using RimWorld;
using Common = RimGovernor.Contracts.Wire.Common;
using Control = RimGovernor.Contracts.Wire.Authority;
using Observations = RimGovernor.Contracts.Wire.Observations;
using Operations = RimGovernor.Contracts.Wire.Operations;
using Receipts = RimGovernor.Contracts.Wire.Receipts;

namespace HomeBridge.BridgeTools;

/// <summary>Only compiled in fixture builds. The rectangle assertions have one owner, here.</summary>
public sealed class ZoneRectangleFixture
{
    [Tool("test/zone_rectangle_case", Description = "UNSAFE FOR MODEL EXECUTION. Run the prepared zone/rectangle body in-process; spec is RectangleCaseRequest JSON. Fault injection is private acceptance only.")]
    public Task<object> Run(IRimBridgeContext ctx, CancellationToken cancellationToken, string spec) =>
        Task.Run(async () =>
        {
            if (ctx.MainThread.IsMainThread) throw new InvalidOperationException("The case body must run off the game thread.");
            var request = FixtureJson.Deserialize<RectangleCaseRequest>(spec);
            if (!Enum.IsDefined(request.Fault)) throw new ArgumentException("Unknown pilot fault.");
            var run = new RectangleRun(request,
                NativeFacadeRegistry.Current ?? throw new InvalidOperationException("Native facade unavailable."),
                BrainBootstrap.Host?.Telemetry ?? throw new InvalidOperationException("Evidence sink unavailable."));
            return (object)FixtureKit.Reply(await run.ExecuteAsync(cancellationToken).ConfigureAwait(false));
        }, cancellationToken);
}

internal sealed class RectangleRun(RectangleCaseRequest request, INativeFacade native, TelemetryEmitter telemetry)
{
    private readonly Dictionary<string, string> evidence = new(StringComparer.Ordinal);
    private string phase = "started";
    private Common.Identity Identity => new() { ColonyId = request.ColonyId, LoadToken = request.LoadToken, MapId = request.MapId };
    private Observations.ReadScope Scope => new() { ExpectedIdentity = Identity };

    internal async Task<RectangleCaseResult> ExecuteAsync(CancellationToken ct)
    {
        Publish(false, null);
        try
        {
            await BodyAsync(ct).ConfigureAwait(false);
            phase = "completed";
            return Publish(true, null);
        }
        catch (Exception e)
        {
            var result = Publish(false, e.GetType().Name + ": " + e.Message);
            if (e is OperationCanceledException) throw;
            return result;
        }
    }

    private RectangleCaseResult Publish(bool success, string? error)
    {
        var result = new RectangleCaseResult
        {
            RunId = request.RunId, Phase = phase, Success = success, Error = error,
            Evidence = new Dictionary<string, string>(evidence, StringComparer.Ordinal),
        };
        telemetry.EmitDurableRow(TelemetryKinds.AcceptanceCase.Name, new RowContext { Component = "acceptance" },
            new Dictionary<string, object?> { ["case"] = "zone/rectangle", ["run_id"] = request.RunId, ["result"] = FixtureJson.Serialize(result) });
        return result;
    }

    private void Record<T>(string label, T value)
    {
        phase = label;
        evidence[label] = WireJson.Serialize(value);
        Publish(false, null);
    }

    private async Task InjectFaultAsync(CancellationToken ct)
    {
        switch (request.Fault)
        {
            case RectangleCaseFault.Assertion: throw new InvalidOperationException("injected rectangle assertion failure");
            case RectangleCaseFault.Exit: Environment.Exit(73); break; // This private game only; evidence above was durable.
            case RectangleCaseFault.Hang:
                await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task.WaitAsync(ct).ConfigureAwait(false);
                break;
        }
    }

    private Task<TReply> CallAsync<TRequest, TReply>(string label, NativeOp<TRequest, TReply> op, TRequest value, CancellationToken ct)
        where TRequest : class where TReply : class
    {
        phase = label;
        return op.CallAsync(native, value, ct);
    }

    private async Task GrantAsync(CancellationToken ct)
    {
        var read = await CallAsync("acquire-status", NativeOps.ReadAuthorityStatus, new Control.StatusRequest { Identity = Identity }, ct).ConfigureAwait(false);
        var generation = read.Context?.NativeGeneration ?? throw new InvalidOperationException($"acquire-status: no native generation: {read}");
        var granted = await CallAsync("acquire", NativeOps.ControlAuthority, new Control.ControlRequest
        {
            Operation = new Control.SetMode { Identity = Identity, ExpectedGeneration = generation, Mode = Control.Mode.Auto },
        }, ct).ConfigureAwait(false);
        if (granted.Outcome?.Granted?.Authority?.Mode != Control.Mode.Auto || granted.Outcome.Granted.Context?.NativeGeneration is null)
            throw new InvalidOperationException($"acquire: expected MODE_AUTO and generation: {granted}");
    }

    private async Task<Operations.ActionResultOutcome> AppliedAsync(string key, Operations.ActionIntent intent, CancellationToken ct)
    {
        var reply = await CallAsync(key, NativeOps.Apply, new Operations.ApplyRequest
        {
            Identity = Identity, Actions = [new Operations.Action { Key = key, Intent = intent }],
        }, ct).ConfigureAwait(false);
        if (reply.BatchFailure is not null || reply.Results.ToList() is not [{ Outcome: { Applied: not null } outcome }])
            throw new InvalidOperationException($"{key}: want one applied result: {reply}");
        return outcome;
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<Cell>>> CellsAsync(string label, CancellationToken ct)
    {
        var site = request.Site;
        var read = await CallAsync(label, NativeOps.GetCells, new Observations.GetCellsRequest
        {
            Scope = Scope, Rectangle = new Observations.Rectangle
            {
                Minimum = new Common.Cell { X = site.X, Z = site.Z },
                Maximum = new Common.Cell { X = site.X + site.Width - 1, Z = site.Z + site.Height - 1 },
            },
        }, ct).ConfigureAwait(false);
        var grid = read.Grid;
        var count = (long)grid.Width * grid.Height;
        if (grid.Width < 1 || grid.Height < 1 || grid.Held.Length != count || grid.Things.Length != count || grid.ZoneId.Length != count)
            throw new InvalidOperationException($"{label}: no complete cell grid: {read}");
        var groups = new Dictionary<string, List<Cell>>(StringComparer.Ordinal);
        for (var j = 0; j < grid.Held.Length; j++)
        {
            if (!grid.Held[j] || grid.ZoneId[j] is not { } id) continue;
            if (!groups.TryGetValue(id, out var cells)) groups[id] = cells = [];
            cells.Add(new Cell(grid.X + j % grid.Width, grid.Z + j / grid.Width));
        }
        return groups.ToDictionary(p => p.Key, p => (IReadOnlyList<Cell>)p.Value);
    }

    private async Task BodyAsync(CancellationToken cancellationToken)
    {
        var site = request.Site;
        var (x, z) = (site.X, site.Z);
        var playerId = request.PlayerZone;
        await GrantAsync(cancellationToken).ConfigureAwait(false);

        var intent = new Operations.ZoneIntent
        {
            Label = "Rectangle storage",
            Kind = Operations.ZoneType.Stockpile,
            AddCells = new Operations.Cells { Selection = new Operations.Rectangle { Origin = new Common.Cell { X = x, Z = z }, Width = 7, Height = 5 } },
            Stockpile = new Operations.StockpileSettings
            {
                Priority = StockpilePriority.Important,
                Preset = FilterBase.Nothing,
                Filter = new Operations.FilterPatch { Allow = [new Operations.FilterSelector { Definition = new Operations.FilterSelectorDefinition.ThingDefCase(ThingDefOf.Steel.defName) }] },
            },
        };

        async Task<Receipts.ZoneEffect> ApplyAsync(string label)
        {
            var outcome = await AppliedAsync("rectangle-stockpile", intent, cancellationToken).ConfigureAwait(false);
            var effect = outcome.Applied?.Outcome?.Applied?.Observed?.Effect?.Zone
                ?? throw new InvalidOperationException($"{label}: missing applied stockpile evidence: {outcome}");
            Record(label, effect);
            return effect;
        }

        var first = await ApplyAsync("rectangle-create").ConfigureAwait(false);
        await InjectFaultAsync(cancellationToken).ConfigureAwait(false);
        var rows = first.Created.ToList();
        if (rows.Count != 2)
        {
            throw new InvalidOperationException($"expected both components: {first}");
        }

        var ids = new List<string> { playerId };
        var actual = new Dictionary<string, List<Cell>>(StringComparer.Ordinal);
        var expected = (from dx in Enumerable.Range(0, 7)
                        from dz in Enumerable.Range(0, 5)
                        where dx != 3 && !(dx == 1 && dz == 2) && !(dx == 6 && dz == 4)
                        select new Cell(x + dx, z + dz)).ToHashSet();
        var count = 0;
        foreach (var row in rows)
        {
            var id = row.ZoneId ?? "";
            if (id.Length == 0 || id == playerId || actual.ContainsKey(id))
            {
                throw new InvalidOperationException($"invalid created identity: {row}");
            }

            ids.Add(id);
            actual[id] = [];
            foreach (var c in row.Cells.Select(c => new Cell(c.X, c.Z)))
            {
                if (!expected.Remove(c))
                {
                    throw new InvalidOperationException($"unexpected/duplicate cell {c}");
                }

                actual[id].Add(c);
                count++;
            }
        }

        if (count != 28 || expected.Count != 0)
        {
            throw new InvalidOperationException($"lost usable cells: count={count} missing={string.Join(' ', expected)}");
        }

        var byZone = await CellsAsync("rectangle-grid", cancellationToken).ConfigureAwait(false);
        foreach (var (id, cells) in actual)
        {
            var gridCells = byZone.GetValueOrDefault(id) ?? [];
            if (!gridCells.ToHashSet().SetEquals(cells))
            {
                throw new InvalidOperationException($"zone {id}: receipt/grid mismatch: {string.Join(' ', cells)} vs {string.Join(' ', gridCells)}");
            }
        }

        var zonesReply = await CallAsync("rectangle-zone-settings", NativeOps.ListZones, new Observations.ListZonesRequest
        {
            Scope = Scope,
            Ids = [.. ids],
            IncludeFilter = true,
        }, cancellationToken).ConfigureAwait(false);
        Record("zones", zonesReply);
        var zones = zonesReply.Zones.OfType<Observations.ZoneState.Stockpile>().ToList();
        if (zonesReply.Zones.Count != 3 || zones.Count != 3)
        {
            throw new InvalidOperationException($"expected three stockpile zones, got {zonesReply}");
        }

        foreach (var row in zones)
        {
            var allowed = row.Filter?.AllowedDefNames.ToList() ?? [];
            if (row.Id == playerId)
            {
                if (row.Priority != "Low" || allowed.Count != 0)
                {
                    throw new InvalidOperationException($"player zone changed: {row}");
                }
            }
            else if (row.Priority != "Important" || (allowed.Count != 1 || allowed[0] != ThingDefOf.Steel.defName))
            {
                throw new InvalidOperationException($"created settings mismatch: {row}");
            }
        }

        // Repeating creation must refuse occupied cells, even after a large settings batch.
        var churn = await CallAsync("rectangle-replay-churn", NativeOps.Apply, new Operations.ApplyRequest
        {
            Identity = Identity,
            Actions = [.. Enumerable.Range(0, 260).Select(i => new Operations.Action
            {
                Key = $"rectangle-settings-{i:D3}",
                Intent = new Operations.ZoneIntent
                {
                    Zone = new Common.Ref { Id = ids[1] },
                    Stockpile = new Operations.StockpileSettings { Priority = StockpilePriority.Important },
                },
            })],
        }, cancellationToken).ConfigureAwait(false);
        if (churn.BatchFailure is not null || churn.Results.Count != 260)
        {
            throw new InvalidOperationException($"rectangle-replay-churn: {churn.BatchFailure} with {churn.Results.Count} results");
        }

        var replay = await CallAsync("rectangle-repeat", NativeOps.Apply, new Operations.ApplyRequest
        {
            Identity = Identity, Actions = [new Operations.Action { Key = "rectangle-stockpile", Intent = intent }],
        }, cancellationToken).ConfigureAwait(false);
        Record("rectangle-repeat", replay);
        if (replay.BatchFailure is not null || replay.Results.ToList() is not [{ Outcome.Refused: not null }])
        {
            throw new InvalidOperationException($"repeated creation must refuse: {replay}");
        }

        var after = await CellsAsync("rectangle-replay-grid", cancellationToken).ConfigureAwait(false);
        if (!byZone.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(after.Keys))
            throw new InvalidOperationException("repeated creation changed zone identities");
        foreach (var id in ids)
        {
            if (!(byZone.GetValueOrDefault(id) ?? []).SequenceEqual(after.GetValueOrDefault(id) ?? []))
            {
                throw new InvalidOperationException($"replay changed zone {id}");
            }
        }
    }
}
