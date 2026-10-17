using System.Globalization;
using RimGovernor.Brain.Bridge;
using Common = RimGovernor.Contracts.Wire.Common;
using Lifecycle = RimGovernor.Contracts.Wire.Lifecycle;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>lifecycle/new_colony</c>: the production new-colony start on games the case launches itself. The first launch
/// probes the refusals, starts the pinned spec, is refused a second start over the live colony and reloads the save;
/// the second launch proves the same pinned spec gives the same colony; a third and a fourth force a save failure and
/// a timeout. Every outcome is a typed failure code and the phase it ended in, never message text.
/// </summary>
internal static class LifecycleNewColony
{
    private static readonly Lifecycle.NewColonyPhase[] InOrder =
    [
        Lifecycle.NewColonyPhase.GeneratingWorld,
        Lifecycle.NewColonyPhase.ChoosingTile,
        Lifecycle.NewColonyPhase.RollingColonists,
        Lifecycle.NewColonyPhase.GeneratingMap,
        Lifecycle.NewColonyPhase.Finishing,
        Lifecycle.NewColonyPhase.Saving,
    ];

    internal static TypedCase Case() => new()
    {
        Name = "lifecycle/new_colony",
        Scope = "Production new-colony start (#2021) from a fresh main menu: a small pinned spec (150 map, 0.05 coverage) completes paused through the phases in order "
            + "with the requested colonist count, biome and map size, the seed echoed and a save in profile/Saves of the reported byte length, which lifecycle_load "
            + "restores to the same colonists, tick and pause; the same spec on a second launch gives the same tile, biome and colonist names and skills. "
            + "A bad scenario, difficulty, storyteller or biome defName is INVALID_REQUEST before generation, an unsatisfiable temperature band INVALID_REQUEST in "
            + "CHOOSING_TILE (the menu stays usable), a start over a loaded game UNAVAILABLE, an unwritable save NATIVE_FAILURE in SAVING and the timeout "
            + "DEADLINE_EXCEEDED, never a hung pending. Spends no ticks: each phase is a long event outside the tick loop.",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        Start = new OwnedStart(),
        ClaimsStart = new StartClaim { Launches = 4 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ITypedSession s, CancellationToken cancellationToken)
    {
        var first = "";
        await NewColonyStart.MenuAsync(s, "first", async () => first = await FirstAsync(s, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        var second = "";
        await NewColonyStart.MenuAsync(
            s,
            "second",
            async () =>
            {
                var (completed, _) = await NewColonyStart.CompleteAsync(s, "second", NewColonyStart.Request(TypedLifecycle.Stamp("newcolony-second"), NewColonyStart.Spec()), cancellationToken).ConfigureAwait(false);
                second = await DigestAsync(s, "second", Identity(completed), cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
        if (first != second)
        {
            throw new AcceptanceException($"the same pinned spec gave different colonies:\nfirst:  {first}\nsecond: {second}");
        }

        s.Record("same_spec_same_colony", first);
        await SaveFailureAsync(s, cancellationToken).ConfigureAwait(false);
        await NewColonyStart.MenuAsync(
            s,
            "timeout",
            async () =>
            {
                var phases = await NewColonyStart.FailsAsync(
                    s, "timeout", NewColonyStart.Request(TypedLifecycle.Stamp("newcolony-timeout"), NewColonyStart.Spec(), timeoutMs: 1000), Common.FailureCode.DeadlineExceeded, cancellationToken).ConfigureAwait(false);
                s.Record("timeout_phases", NewColonyStart.Strings(phases.Select(p => p.ToString())));
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The refusals, the real start and its save, the refusal over the live colony and the reload; returns the colony digest.</summary>
    private static async Task<string> FirstAsync(ITypedSession s, CancellationToken cancellationToken)
    {
        await RefusalsAsync(s, cancellationToken).ConfigureAwait(false);
        var spec = NewColonyStart.Spec();
        var (completed, phases) = await NewColonyStart.CompleteAsync(s, "first", NewColonyStart.Request(TypedLifecycle.Stamp("newcolony-first"), spec), cancellationToken).ConfigureAwait(false);
        s.Record("phases", NewColonyStart.Strings(phases.Select(p => p.ToString())));
        if (!phases.SequenceEqual(InOrder.Where(phases.Contains)) || phases[^1] != Lifecycle.NewColonyPhase.Saving)
        {
            throw new AcceptanceException($"phases out of order or not ending in SAVING: [{string.Join(' ', phases)}]");
        }

        if (completed.SaveName != spec.SaveName || completed.Seed != spec.Seed || completed.Paused != true)
        {
            throw new AcceptanceException($"want save {spec.SaveName}, seed {spec.Seed}, paused; got {completed}");
        }

        var file = new FileInfo(SavePath(s, spec.SaveName!));
        var bytes = file.Exists ? file.Length : 0;
        if (bytes == 0 || completed.ByteLength != (ulong)bytes)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"reported byte length {completed.ByteLength}, the save file is {bytes} bytes"));
        }

        s.Record("save_bytes", bytes);
        var identity = Identity(completed);
        var tick = completed.Context?.Tick ?? throw new AcceptanceException($"the completed start names no tick: {completed}");
        var digest = await DigestAsync(s, "generated", identity, cancellationToken).ConfigureAwait(false);

        // A second start over the live colony is refused without touching it.
        var again = await s.NewColonyAsync("loaded-refusal", NewColonyStart.Request(TypedLifecycle.Stamp("newcolony-again"), NewColonyStart.Spec()), cancellationToken).ConfigureAwait(false);
        if (again.Failure?.Code != Common.FailureCode.Unavailable || again.Phases.Count > 0)
        {
            throw new AcceptanceException($"start over a loaded game: want UNAVAILABLE before generation, got {(object?)again.Failure ?? again.Completed}");
        }

        // The save loads through lifecycle_load and restores the same colony, tick and pause.
        _ = await TypedLifecycle.LoadAsync(s, "reload", identity, spec.SaveName!, Lifecycle.Readiness.Map, cancellationToken).ConfigureAwait(false);
        var (reloaded, reloadedTick, paused) = await TypedLifecycle.IdentityAsync(s, "reloaded-identity", cancellationToken).ConfigureAwait(false);
        if (reloadedTick != tick || !paused)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"reloaded colony at tick {reloadedTick} (paused {paused}), generated at {tick} paused"));
        }

        var reloadedDigest = await DigestAsync(s, "reloaded", reloaded, cancellationToken).ConfigureAwait(false);
        return reloadedDigest == digest
            ? digest
            : throw new AcceptanceException($"the reloaded colony differs from the generated one:\ngenerated: {digest}\nreloaded:  {reloadedDigest}");
    }

    /// <summary>
    /// Bad defNames are refused before generation; an unsatisfiable temperature band passes validation and fails once
    /// the tiles are known, and the menu must stay usable for the real start that follows.
    /// </summary>
    private static async Task RefusalsAsync(ITypedSession s, CancellationToken cancellationToken)
    {
        var bad = new (string Field, Lifecycle.NewColonySpec Spec)[]
        {
            ("scenario", NewColonyStart.Spec() with { Scenario = "NoSuchScenario" }),
            ("difficulty", NewColonyStart.Spec() with { Difficulty = "NoSuchDifficulty" }),
            ("storyteller", NewColonyStart.Spec() with { Storyteller = "NoSuchStoryteller" }),
            ("biome", NewColonyStart.Spec() with { Biomes = ["NoSuchBiome"] }),
        };
        foreach (var (field, spec) in bad)
        {
            await NewColonyStart.RefusedAsync(s, "refuse-" + field, NewColonyStart.Request("newcolony-bad-" + field, spec), cancellationToken).ConfigureAwait(false);
        }

        var hot = NewColonyStart.Spec() with { MinTemperature = 90, MaxTemperature = 100 };
        var phases = await NewColonyStart.FailsAsync(s, "unsatisfiable", NewColonyStart.Request("newcolony-hot", hot), Common.FailureCode.InvalidRequest, cancellationToken).ConfigureAwait(false);
        if (phases.Count == 0 || phases[^1] != Lifecycle.NewColonyPhase.ChoosingTile)
        {
            throw new AcceptanceException($"unsatisfiable temperature band: want the refusal in CHOOSING_TILE, got phases [{string.Join(' ', phases)}]");
        }

        s.Record("refusals", NewColonyStart.Strings([.. bad.Select(b => b.Field), "temperature"]));
    }

    /// <summary>A directory squats the save path: the save cannot be written, and the start must fail in SAVING.</summary>
    private static async Task SaveFailureAsync(ITypedSession s, CancellationToken cancellationToken)
    {
        const string Save = "newcolony-accept-savefail";
        var squat = SavePath(s, Save);
        _ = Directory.CreateDirectory(squat);
        try
        {
            await NewColonyStart.MenuAsync(
                s,
                "savefail",
                async () =>
                {
                    var phases = await NewColonyStart.FailsAsync(
                        s, "savefail", NewColonyStart.Request(TypedLifecycle.Stamp("newcolony-savefail"), NewColonyStart.Spec() with { SaveName = Save }), Common.FailureCode.NativeFailure, cancellationToken).ConfigureAwait(false);
                    if (phases.Count == 0 || phases[^1] != Lifecycle.NewColonyPhase.Saving)
                    {
                        throw new AcceptanceException($"forced save failure: want NATIVE_FAILURE in SAVING, got phases [{string.Join(' ', phases)}]");
                    }

                    s.Record("savefail_phases", NewColonyStart.Strings(phases.Select(p => p.ToString())));
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (Directory.Exists(squat))
            {
                Directory.Delete(squat, recursive: true);
            }
        }
    }

    private static Common.Identity Identity(Lifecycle.NewColonyCompleted completed) =>
        completed.Context?.Identity ?? throw new AcceptanceException($"the completed start names no identity: {completed}");

    private static string SavePath(ITypedSession s, string saveName) => s.ProfileSave(saveName);

    /// <summary>
    /// Asserts the live map matches the spec and returns a digest of what a pinned seed must reproduce: the tile, biome
    /// and hilliness, and each colonist's name and skill levels.
    /// </summary>
    private static async Task<string> DigestAsync(ITypedSession s, string label, Common.Identity identity, CancellationToken cancellationToken)
    {
        var scope = TypedReads.Scope(identity);
        var factsReply = await s.CallAsync(label + "-facts", NativeOps.ReadColonyFacts, new Observations.ColonyFactsRequest { Scope = scope }, cancellationToken).ConfigureAwait(false);
        var facts = TypedReads.Observed(label + "-facts", NativeOps.ReadColonyFacts, factsReply, factsReply.Outcome?.Observed);
        if (facts.ColonistCount != 3 || facts.MapSize.Width != 150 || facts.MapSize.Height != 150 || !NewColonyStart.Biomes.Contains(facts.Biome, StringComparer.Ordinal))
        {
            throw new AcceptanceException($"{label}: want 3 colonists on a 150x150 map in one of [{string.Join(' ', NewColonyStart.Biomes)}], got {facts.ColonistCount} on {facts.MapSize.Width}x{facts.MapSize.Height} in {facts.Biome}");
        }

        var worldReply = await s.CallAsync(label + "-world", NativeOps.ReadWorld, new Observations.WorldRequest { Scope = scope }, cancellationToken).ConfigureAwait(false);
        var tile = TypedReads.Observed(label + "-world", NativeOps.ReadWorld, worldReply, worldReply.Outcome?.Observed).Tile
            ?? throw new AcceptanceException($"{label}-world: no settlement tile");
        var colonists = (await NewColonyStart.ColonistsAsync(s, label + "-pawns", identity, cancellationToken).ConfigureAwait(false))
            .Select(p => string.Create(
                CultureInfo.InvariantCulture,
                $"{p.Pawn.Label}[{string.Join(',', (p.Biography?.Skills ?? []).Select(k => $"{k.DefName}={Math.Max(k.Level, 0)}/{k.Passion}").Order(StringComparer.Ordinal))}]"))
            .Order(StringComparer.Ordinal)
            .ToList();
        if (colonists.Count != 3)
        {
            throw new AcceptanceException($"{label}-pawns: want 3 colonists, got {colonists.Count}");
        }

        var digest = string.Create(CultureInfo.InvariantCulture, $"tile={tile.Tile} biome={facts.Biome} hilliness={tile.Hilliness} colonists={string.Join(';', colonists)}");
        s.Record(label + "_colony", digest);
        return digest;
    }
}
