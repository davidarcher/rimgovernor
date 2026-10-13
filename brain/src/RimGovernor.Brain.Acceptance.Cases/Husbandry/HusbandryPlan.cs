using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>husbandry/plan</c> (Go <c>plan.go</c>) proves the herd plan end to end on the blank lab. A lone cow is a founder and is
/// kept; an old milk race (superseded while the cow has no mate) loses one male to sterilize in the hospital ward, read back
/// sterilized; a wild bull is then tamed as the cow's mate and the old race retires one animal per review, never all at once.
/// The race catalog read is asserted first: cows with a milk product, wild and tame races, minimum handling skill.
/// A snapshot test cannot cover it: the assertion is native bed and room use (an animal bed flagged medical in a standing
/// hospital ward), a surgery bill the game's own doctors perform, and the tame and designation ops landing on live animals.
/// The case stages the hospital ward ring finished from the controller's own LayoutPlan (the planned hospital,
/// test/hut_shell_fixture stage) so the beds, the medical flag, the Hospital allowed area and the sterilize writes still run
/// the real controller path. Staging and native readbacks happen between service runs.
/// Native semantics this case asserts and does not confirm (each failure names the one it hit): the Wildness stat orders the
/// catalog's races; a PawnKindDef shares its race's defName, so lab_spawn can place the old race by the catalog's name; an
/// AnimalBed can be flagged medical; a doctor performs the sterilize bill on an animal in the hospital ward; the Hospital
/// allowed area exists and the sterilize flow moves the animal into it; native master ids equal pawn ids; the lab's colonists
/// clear a cow's minimum handling skill; a stack of Hay per animal keeps the feed review from gating taming.
/// </summary>
/// <remarks>
/// Adaptations: the race rows come from <see cref="GameCatalogReader.AnimalRacesAsync"/> (the catalog is game-side now); a
/// service "restart on the same journal" is a stop and a new serve of the in-process brain, whose journal persists; the
/// journal's review tick is the tick of the latest persisted layout plan read.
/// </remarks>
internal static class HusbandryPlan
{
    private const string HutShellOp = "test/hut_shell_fixture";

    /// <summary>DebugStartFixture.SkillLevel, every lab colonist's Animals skill.</summary>
    private const int LabAnimalsSkill = 8;

    /// <summary>The wall-clock ceiling of each service phase; the stall budget is what ends a broken one.</summary>
    private static readonly TimeSpan PlanWait = TimeSpan.FromMinutes(10);

    /// <summary>The game time a sterilize bill gets once the service has stopped (the harness runs the game for it).</summary>
    private const ulong SurgeryTicks = 3UL * Wait.TicksPerDay;

    /// <summary>The WoodLog dropped beside the hospital ward: the barn, pen fences and beds are built from it.</summary>
    private const int StageWood = 400;

    /// <summary>The old race's age in years: adult, so each adds capacity. The old race is two males and two females, so one male can be sterilized while a breeding pair stays.</summary>
    private const double OldAge = 8;

    /// <summary>An adult cow's age in years (HusbandryFixture's animals).</summary>
    private const double CowAge = 4;

    internal static AcceptanceCase Case() => new()
    {
        Name = "husbandry/plan",
        Scope = "Herd plan end to end on the lab (#1638): the catalog race rows carry cows with a milk product, wild and tame "
            + "races and a minimum handling skill; a lone cow founder is kept (never culled or sterilized) beside an old milk race; "
            + "the old race's surplus male is sterilized in the controller-built hospital ward (Hospital area, medical animal bed, "
            + "surgery bill) and reads back sterilized; a wild bull is tamed as the cow's mate and the old race then retires one "
            + "animal per review. A snapshot test cannot cover it: native bed and room use, surgery, and the tame and designation "
            + "ops are the signal. Unconfirmed native semantics: wildness stat, combat power and trainables approximation, animal "
            + "bed medical flag, surgery in the ward bed, bedroom role with a sleeping spot, the Hospital area, native master ids "
            + "equal pawn ids.",
        Start = new LabStart(),
        RequiredOps = [DebugGame.LabSpawnTool, HutShellOp],
        Serve = new ServeSpec
        {
            Families = ["husbandry", "animal-containment", "sheltering"],
            NativeTimeout = TimeSpan.FromSeconds(60),
            Prefix = "husbandry-plan",
        },
        Budget = TimeSpan.FromMinutes(28),
        Crew = new Crew { Size = 3 },
        Reason = "one unstaged run of three service phases: the controller builds the pen, the hospital ward beds and the medical flag "
            + "(construction), the game's doctors then sterilize, and a handler tames the mate before the retirement shows; "
            + "the pen and beds cannot be staged without leaving the controller path the case exists to prove",
        Run = RunAsync,
    };

    /// <summary>One animal of the husbandry census.</summary>
    private sealed record AnimalRow(string Id, string Gender, bool Sterilized, bool Slaughter, bool Release, bool Queued, string Area)
    {
        public override string ToString() =>
            $"{{Id:{Id} Gender:{Gender} Sterilized:{Sterilized} Slaughter:{Slaughter} Release:{Release} Queued:{Queued} Area:{Area}}}";
    }

    /// <summary>One husbandry write the controller journaled.</summary>
    private sealed record Write(string Method, string Animal, string Argument, ulong Tick, bool Done)
    {
        public override string ToString() => $"{{{Method} {Animal} {Argument} {Tick} {Done}}}";
    }

    private static double PerAdult(AnimalRaceRow race) => race.Milk!.Amount / race.Milk.IntervalDays;

    /// <summary>A milk race's ranking in the plan: yield per body size.</summary>
    private static double Score(AnimalRaceRow race) => PerAdult(race) / race.BodySize!.Value;

    /// <summary>
    /// Asserts the catalog read and picks the old milk race: the first (by name) a cow outranks for milk (yield per body
    /// size, the cow first on a tie) and two adult cows (the founder and her mate) out-yield four of, so the plan can retire it.
    /// </summary>
    private static string CheckCatalog(IReadOnlyList<AnimalRaceRow> rows, Report report)
    {
        var cow = rows.FirstOrDefault(r => r.Def == "Cow");
        if (cow?.Milk is null)
        {
            throw new AcceptanceException($"race catalog: Cow missing or without a milk product: {cow}");
        }

        if (cow.MinimumHandlingSkill is null || cow.Wildness is null || cow.BodySize is null || cow.BodySize <= 0)
        {
            throw new AcceptanceException($"race catalog: Cow facts unread (minimum handling skill, wildness or body size): {cow}");
        }

        if (cow.MinimumHandlingSkill > LabAnimalsSkill)
        {
            throw new AcceptanceException($"race catalog: Cow needs handling skill {cow.MinimumHandlingSkill}, the lab's colonists have {LabAnimalsSkill}");
        }

        // Wildness stat (unconfirmed): a cow is tameable below 1 and the catalog holds wilder races.
        var wilder = 0;
        var skills = new HashSet<int>();
        foreach (var r in rows)
        {
            if (r.Wildness is { } wildness && wildness > cow.Wildness)
            {
                wilder++;
            }

            if (r.MinimumHandlingSkill is { } skill)
            {
                skills.Add(skill);
            }
        }

        if (cow.Wildness >= 1 || wilder == 0)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"race catalog: wildness does not separate tame from wild races (Cow {cow.Wildness}, {wilder} wilder races)"));
        }

        if (skills.Count < 2)
        {
            throw new AcceptanceException($"race catalog: minimum handling skill is the same [{string.Join(' ', skills)}] for every race");
        }

        var names = rows
            .Where(r => r.Def != "Cow" && r.Milk is not null && r.BodySize is > 0)
            .Select(r => r.Def)
            .Order(StringComparer.Ordinal)
            .ToList();
        var old = "";
        foreach (var def in names)
        {
            var r = rows.First(x => x.Def == def);
            if (Score(cow) >= Score(r) * (1 - 1e-9) && 2 * PerAdult(cow) >= 4 * PerAdult(r))
            {
                old = def;
                break;
            }
        }

        report["catalog"] = new JsonObject
        {
            ["races"] = rows.Count,
            ["cow"] = new JsonObject { ["milk_per_day"] = PerAdult(cow), ["wildness"] = cow.Wildness, ["minimum_handling_skill"] = cow.MinimumHandlingSkill },
            ["wilder_races"] = wilder,
            ["milk_races"] = new JsonArray([.. names.Select(n => (JsonNode?)n)]),
            ["old_race"] = old,
        };
        return old.Length == 0
            ? throw new AcceptanceException($"race catalog: no other milk race that a cow outranks and two cows out-yield four of (milk races [{string.Join(' ', names)}])")
            : old;
    }

    /// <summary>Reads the player animals by id.</summary>
    private static async Task<Dictionary<string, AnimalRow>> CensusAsync(Harness h, string label, JsonObject identity, CancellationToken cancellationToken)
    {
        var reply = await h.WireAsync(
            label,
            "observations_read_husbandry",
            new JsonObject { ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() } },
            cancellationToken).ConfigureAwait(false);
        var (_, observed) = Harness.Outcome(reply, "observed");
        var animals = new Dictionary<string, AnimalRow>(StringComparer.Ordinal);
        foreach (var raw in observed["animals"].AsSlice())
        {
            var row = raw.AsMap() ?? [];
            var state = row["animal"].AsMap() ?? [];
            var id = PawnTable.PawnRef(row);
            animals[id] = new AnimalRow(
                id,
                state["gender"].AsString(),
                state["sterilized"].AsBool() == true,
                state["slaughter"].AsBool() == true,
                state["release"].AsBool() == true,
                state["sterilizeQueued"].AsBool() == true,
                state["allowedAreaId"].AsString());
        }

        return animals;
    }

    /// <summary>Lists the journal's husbandry writes, oldest first.</summary>
    private static async Task<List<Write>> WritesAsync(IWatchStore store, CancellationToken cancellationToken)
    {
        var plans = await store.PlanHistoryAsync(256, "tame-*", cancellationToken).ConfigureAwait(false);
        var all = new List<JsonObject>(plans);
        foreach (var glob in new[] { "slaughter-*", "release-*", "sterilize-*", "allowed_area-*" })
        {
            all.AddRange(await store.PlanHistoryAsync(256, glob, cancellationToken).ConfigureAwait(false));
        }

        var writes = new List<Write>();
        foreach (var plan in all.OrderBy(p => (long)p["order"].AsNumber()).Reverse())
        {
            foreach (var progress in plan["progress"].AsSlice())
            {
                var row = progress.AsMap() ?? [];
                if (row["kind"].AsString() == "husbandry")
                {
                    writes.Add(new Write(row["method"].AsString(), row["animal"].AsString(), row["argument"].AsString(), row["tick"].AsUInt64() ?? 0, row["stage"].AsString() == "completed"));
                }
            }
        }

        return writes;
    }

    /// <summary>One service run: the service, its journal and the report slot.</summary>
    private sealed class Phase
    {
        private readonly ISession _session;
        private readonly IObservedSession _observed;
        private readonly IWatchedService _service;
        private readonly IWatchStore _store;
        private readonly Wait _wait;

        private Phase(ISession session, IObservedSession observed, IWatchedService service, IWatchStore store)
        {
            _session = session;
            _observed = observed;
            _service = service;
            _store = store;
            _wait = new Wait { Ceiling = PlanWait, Stall = Wait.StallBudget(), Terminal = service.Exited };
        }

        public IWatchStore Store => _store;

        public static async Task<Phase> BeginAsync(ISession session, CancellationToken cancellationToken)
        {
            var observed = ObservedSession.Of(session);
            var service = await observed.ServeAsync(session.Spec, cancellationToken).ConfigureAwait(false);
            try
            {
                _ = await service.AcquireAsync(cancellationToken).ConfigureAwait(false);
                service.KeepAuthority(cancellationToken);
                var store = await service.StoreAsync(cancellationToken).ConfigureAwait(false);
                return new Phase(session, observed, service, store);
            }
            catch
            {
                _ = await service.StopAsync().ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>
        /// Polls the journal until <paramref name="done"/>; the signature is the write counts and the game day, so a broken
        /// phase stalls and a long build does not. <paramref name="done"/> sees the latest persisted layout plan.
        /// </summary>
        public async Task UntilAsync(string what, Func<LayoutPlan?, IReadOnlyList<Write>, bool> done, CancellationToken cancellationToken)
        {
            try
            {
                await _wait.WaitProgressAsync(
                    async ct =>
                    {
                        if (await _store.RoundsRevisionAsync(ct).ConfigureAwait(false) == 0)
                        {
                            return (Wait.Signature("no-review"), false);
                        }

                        var (tick, plan) = await ObservedSession.ReadLayoutPlanAsync(_session.Config, ct).ConfigureAwait(false);
                        var writes = await WritesAsync(_store, ct).ConfigureAwait(false);
                        var finished = writes.Count(w => w.Done);
                        var ok = done(plan, writes);
                        return (Wait.Signature(writes.Count, finished, tick / Wait.TicksPerDay), ok);
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException($"{what}: {e.Message}", e);
            }
        }

        /// <summary>Ends the phase and takes the game back, paused.</summary>
        public async Task<Harness> StopAsync(string label, CancellationToken cancellationToken)
        {
            (_store as IDisposable)?.Dispose();
            _session.Report[label] = await _service.StopAsync().ConfigureAwait(false);
            var h = await _observed.ReattachAsync(cancellationToken).ConfigureAwait(false);
            _ = await h.CallAsync("pause-" + label, "rimgovernor/set_time_speed", new JsonObject { ["speed"] = "Paused", ["ultraSpeedBoost"] = false }, cancellationToken).ConfigureAwait(false);
            return h;
        }

        /// <summary>Ends the phase after a failure.</summary>
        public async Task AbortAsync()
        {
            (_store as IDisposable)?.Dispose();
            _ = await _service.StopAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The plan's one hospital ward and its interior cells.</summary>
    private static (PlannedRoom Room, IReadOnlyList<Cell> Cells) VetRoom(LayoutPlan plan)
    {
        var rooms = plan.Rooms.Where(r => r.Role == PolicyFunctions.PlannedHospital).ToList();
        if (rooms.Count != 1)
        {
            throw new AcceptanceException($"layout plan has {rooms.Count} hospitals, want 1");
        }

        var cells = plan.HospitalCells();
        return cells.Count == 0 ? throw new AcceptanceException("layout plan hospital ward has no interior cells") : (rooms[0], cells);
    }

    /// <summary>Spawns the hospital ward's wall ring and door finished and drops the build wood beside it.</summary>
    private static async Task StageRingAsync(Harness h, PlannedRoom room, IReadOnlyList<Cell> cells, Report report, CancellationToken cancellationToken)
    {
        RoomFootprint footprint;
        try
        {
            footprint = new RoomFootprint(cells, room.Door, room.DoorRot ?? throw new DomainException("hospital ward has no door side"));
        }
        catch (DomainException e)
        {
            throw new AcceptanceException("hospital ward is not a room footprint: " + e.Message, e);
        }

        var walls = footprint.Walls.Where(c => c != room.Door).Select(c => string.Create(CultureInfo.InvariantCulture, $"{c.X},{c.Z}")).ToList();
        var rotation = (room.DoorRot ?? default).ToString().ToLowerInvariant();
        var reply = await h.CallAsync(
            "stage-vet-ring",
            HutShellOp,
            new JsonObject
            {
                ["action"] = "stage",
                ["walls"] = string.Join(';', walls),
                ["door"] = string.Create(CultureInfo.InvariantCulture, $"{room.Door.X},{room.Door.Z}"),
                ["doorRotation"] = rotation,
                ["wood"] = StageWood,
            },
            cancellationToken).ConfigureAwait(false);
        if (reply["success"].AsBool() != true)
        {
            throw new AcceptanceException($"{HutShellOp} stage refused: {reply.ToJson()}");
        }

        report["vet_ring"] = new JsonObject
        {
            ["interior_cells"] = cells.Count,
            ["walls"] = walls.Count,
            ["door"] = string.Create(CultureInfo.InvariantCulture, $"{room.Door.X},{room.Door.Z}"),
            ["rotation"] = rotation,
        };
    }

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var h = s.Harness;
        var prepared = s.Prepared ?? [];
        var center = prepared["center"].AsMap() ?? [];
        var cx = (int)center["x"].AsNumber();
        var cz = (int)center["z"].AsNumber();
        if (cx == 0 && cz == 0)
        {
            throw new AcceptanceException($"lab start: no centre in {prepared.ToJson()}");
        }

        // 1. The race catalog, then the old race it names.
        var rows = await GameCatalogReader.AnimalRacesAsync(s.Identity, cancellationToken).ConfigureAwait(false);
        var oldRace = CheckCatalog(rows, report);

        // 2. The herd: a lone cow, four old animals of the old race, and feed and wood in the open. (Unconfirmed: a
        // PawnKindDef shares its race's defName, so the catalog's name spawns the race.)
        async Task<string> Spawn(LabThing thing) => (await DebugGame.LabSpawnAsync(h, thing, cancellationToken).ConfigureAwait(false)).Id;

        var cow = await Spawn(new LabThing { Def = "Cow", X = cx + 6, Z = cz + 6, Gender = "female", Age = CowAge }).ConfigureAwait(false);
        var oldMales = new List<string>();
        var oldAll = new List<string>();
        var genders = new[] { "male", "male", "female", "female" };
        for (var i = 0; i < genders.Length; i++)
        {
            var id = await Spawn(new LabThing { Def = oldRace, X = cx + 8 + i, Z = cz + 6, Gender = genders[i], Age = OldAge }).ConfigureAwait(false);
            oldAll.Add(id);
            if (genders[i] == "male")
            {
                oldMales.Add(id);
            }
        }

        for (var i = 0; i < 10; i++)
        {
            foreach (var item in new[] { "Hay", "WoodLog" })
            {
                var z = item == "WoodLog" ? cz - 10 : cz - 8;
                _ = await Spawn(new LabThing { Def = item, X = cx - 10 + i, Z = z, Count = 75 }).ConfigureAwait(false);
            }
        }

        report["herd"] = new JsonObject
        {
            ["cow"] = cow,
            ["old_race"] = oldRace,
            ["old_animals"] = new JsonArray([.. oldAll.Select(x => (JsonNode?)x)]),
            ["old_males"] = new JsonArray([.. oldMales.Select(x => (JsonNode?)x)]),
        };

        // 3. Phase one: the controller plans the colony and its hospital ward.
        var one = await Phase.BeginAsync(s, cancellationToken).ConfigureAwait(false);
        PlannedRoom? room = null;
        IReadOnlyList<Cell> cells = [];
        try
        {
            await one.UntilAsync(
                "layout plan with a hospital ward",
                (plan, _) =>
                {
                    if (plan is null || plan.HospitalCells().Count == 0)
                    {
                        return false;
                    }

                    (room, cells) = VetRoom(plan);
                    return true;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await one.AbortAsync().ConfigureAwait(false);
            throw;
        }

        h = await one.StopAsync("plan_service", cancellationToken).ConfigureAwait(false);
        await StageRingAsync(h, room!, cells, report, cancellationToken).ConfigureAwait(false);

        // 4. Phase two: the hospital ward is furnished, flagged medical (the sleeping census reads it, or the sterilize flow
        // would never start) and one old male is sterilized; the cow founder is untouched.
        var two = await Phase.BeginAsync(s, cancellationToken).ConfigureAwait(false);
        var sterilized = "";
        try
        {
            await two.UntilAsync(
                "sterilize write on an old male",
                (_, writes) =>
                {
                    foreach (var x in writes)
                    {
                        if (x.Animal == cow)
                        {
                            throw new AcceptanceException($"the lone cow founder got a {x.Method} write");
                        }

                        if (x.Method == "sterilize" && x.Done)
                        {
                            if (!oldMales.Contains(x.Animal))
                            {
                                throw new AcceptanceException($"sterilize wrote {x.Animal}, not one of the old males [{string.Join(' ', oldMales)}]");
                            }

                            sterilized = x.Animal;
                        }

                        if (x.Method is "slaughter" or "release")
                        {
                            throw new AcceptanceException($"{x.Method} of {x.Animal} while the cow has no mate: the old race retired early");
                        }
                    }

                    return sterilized.Length > 0;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await two.AbortAsync().ConfigureAwait(false);
            throw;
        }

        h = await two.StopAsync("sterilize_service", cancellationToken).ConfigureAwait(false);
        var identity = await Session.ReadIdentityAsync(h, "identity-surgery", cancellationToken).ConfigureAwait(false);

        // The bill is queued; the game's doctors perform it while the game runs.
        try
        {
            _ = await RunAt.RunUntilAsync(
                h,
                "surgery",
                SurgeryTicks,
                new Wait { Stall = Wait.StallBudget() },
                async ct =>
                {
                    var herd = await CensusAsync(h, "surgery-census", identity, ct).ConfigureAwait(false);
                    if (!herd.TryGetValue(sterilized, out var a))
                    {
                        throw new AcceptanceException($"sterilized animal {sterilized} left the census");
                    }

                    return (Wait.Signature(a.Sterilized, a.Queued, a.Area), a.Sterilized);
                },
                HarnessQuiet.Get(h),
                report,
                cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"sterilized animal {sterilized} never read sterilized: {e.Message}", e);
        }

        report["sterilized"] = sterilized;

        // 5. Phase three: a wild bull appears; it is tamed as the cow's mate and the old race then retires gradually.
        var bull = await Spawn(new LabThing { Def = "Cow", X = cx + 14, Z = cz + 2, Unowned = true, Gender = "male", Age = CowAge }).ConfigureAwait(false);
        report["wild_bull"] = bull;
        var three = await Phase.BeginAsync(s, cancellationToken).ConfigureAwait(false);
        var removals = new List<Write>();
        try
        {
            await three.UntilAsync(
                "bull tamed and two old animals removed",
                (_, writes) =>
                {
                    var tamed = false;
                    removals.Clear();
                    foreach (var x in writes)
                    {
                        if (x.Animal == cow || (x.Animal == bull && x.Method != "tame"))
                        {
                            throw new AcceptanceException($"the cow pair got a {x.Method} write on {x.Animal}");
                        }

                        if (x.Method == "tame" && x.Animal == bull && x.Done)
                        {
                            tamed = true;
                        }
                        else if (x.Method is "slaughter" or "release" && x.Done)
                        {
                            if (!oldAll.Contains(x.Animal))
                            {
                                throw new AcceptanceException($"{x.Method} wrote {x.Animal}, not an old animal [{string.Join(' ', oldAll)}]");
                            }

                            removals.Add(x);
                        }
                    }

                    return tamed && removals.Count >= 2;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await three.AbortAsync().ConfigureAwait(false);
            throw;
        }

        h = await three.StopAsync("retire_service", cancellationToken).ConfigureAwait(false);

        // Gradual: one removal per review, so the first two land at different ticks and the old race is not gone at once.
        if (removals[0].Animal == removals[1].Animal || removals[0].Tick >= removals[1].Tick)
        {
            throw new AcceptanceException($"old race retired in one review, not gradually: [{removals[0]} {removals[1]}]");
        }

        report["removals"] = "[" + string.Join(' ', removals) + "]";

        // 6. Native readback: the bull is a player animal (tamed), the cow pair is whole and unmarked, the old race is going
        // and the male stays sterile.
        identity = await Session.ReadIdentityAsync(h, "identity-after", cancellationToken).ConfigureAwait(false);
        var final = await CensusAsync(h, "final-census", identity, cancellationToken).ConfigureAwait(false);
        foreach (var id in new[] { cow, bull })
        {
            if (!final.TryGetValue(id, out var a))
            {
                throw new AcceptanceException($"cow {id} is not a player animal after the run (the bull was not tamed, or the cow was lost)");
            }

            if (a.Slaughter || a.Release || a.Sterilized)
            {
                throw new AcceptanceException($"cow {id} is designated or sterilized: {a}");
            }
        }

        var gone = 0;
        foreach (var id in oldAll)
        {
            if (!final.TryGetValue(id, out var a) || a.Slaughter || a.Release)
            {
                gone++;
            }
        }

        if (gone < 2)
        {
            throw new AcceptanceException($"only {gone} of the old race [{string.Join(' ', oldAll)}] read as removed or designated");
        }

        if (final.TryGetValue(sterilized, out var male) && !male.Sterilized)
        {
            throw new AcceptanceException($"old male {sterilized} no longer reads sterilized: {male}");
        }

        report["old_race_going"] = gone;
    }
}
