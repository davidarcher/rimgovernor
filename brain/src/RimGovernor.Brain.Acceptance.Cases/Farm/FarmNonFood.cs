using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// One end-to-end non-food field case (Go <c>nonfood.go</c>): the lab colony has no stock of the product, no wild plant
/// harvesting it, and a standing MaintainResource demand for it, so the only way to serve the demand is the generic field
/// path: the supply plan prices a field, the field step places the growing zone, the colonists sow it and, once the
/// fixture has matured the crop (its grow days are not what is under test), harvest it. Registers
/// <c>farm/cotton-field</c> and <c>farm/healroot-field</c>.
/// </summary>
internal sealed class FarmNonFood
{
    private const string NonFoodOp = "test/nonfood_field";

    /// <summary>One serve phase's length in game ticks; a phase repeats up to <see cref="PhaseWindows"/> times until its audit is satisfied.</summary>
    private const ulong NonFoodWindow = 40000;

    private const int PhaseWindows = 3;

    private readonly string _name;
    private readonly string _scope;
    private readonly string _plant;
    private readonly string _product;

    // The standard whose deficit the field serves.
    private readonly ConcernId _concern;

    private FarmNonFood(string name, string scope, string plant, string product, ConcernId concern)
    {
        _name = name;
        _scope = scope;
        _plant = plant;
        _product = product;
        _concern = concern;
    }

    internal static IEnumerable<AcceptanceCase> Cases()
    {
        yield return new FarmNonFood(
            "farm/cotton-field",
            "Clothing floor with no cloth, leather or stored outfit and no wild cotton on the map: the generic field path opens a Plant_Cotton "
                + "growing zone, the colonists sow it, and the matured crop is harvested into cloth (#2288).",
            "Plant_Cotton",
            "Cloth",
            PolicyFunctions.MaintainResource).Build();
        yield return new FarmNonFood(
            "farm/healroot-field",
            "Medicine reserve with no medicine and no wild healroot on the map: the generic field path opens a Plant_Healroot "
                + "growing zone, the colonists sow it, and the matured crop is harvested into herbal medicine (#2288).",
            "Plant_Healroot",
            "MedicineHerbal",
            PolicyFunctions.MaintainMedicalReserves).Build();
    }

    private AcceptanceCase Build() => new()
    {
        Name = _name,
        Scope = _scope,
        Start = new FixtureStart
        {
            Op = NonFoodOp,
            Args = new JsonObject { ["action"] = "prepare", ["plant"] = _plant, ["product"] = _product },
            On = new LabStart(),
        },
        RequiredOps = [NonFoodOp],
        Serve = new ServeSpec
        {
            Families = ["field", "resource", "supply", "acquisition", "bill", "work", "medical", "tend", "rescue"],
            NativeTimeout = TimeSpan.FromSeconds(15),
            Prefix = "nonfood-field",
        },
        Budget = TimeSpan.FromMinutes(40),
        Crew = new Crew { Size = 3 },
        Reason = "sowing and harvest each wait for the planner to open the field and the colonists to work it: two serve phases on one colony",
        Run = RunAsync,
    };

    private sealed class Census
    {
        public List<JsonObject> Zones { get; } = [];

        public long Units { get; init; }

        public long Wild { get; init; }

        public long Sown() => Zones.Sum(z => (long)z["sown"].AsNumber());
    }

    private async Task<(Census Census, JsonObject Reply)> CensusAsync(Harness h, string label, CancellationToken cancellationToken)
    {
        var reply = await h.CallAsync(label, NonFoodOp, new JsonObject { ["action"] = "census", ["plant"] = _plant, ["product"] = _product }, cancellationToken).ConfigureAwait(false);
        var census = new Census { Units = (long)reply["productUnits"].AsNumber(), Wild = (long)reply["wildPlants"].AsNumber() };
        foreach (var raw in reply["zones"].AsSlice())
        {
            var zone = raw.AsMap() ?? [];
            if (zone["plant"].AsString() == _plant)
            {
                census.Zones.Add(zone);
            }
        }

        return (census, reply);
    }

    private async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var prepared = s.Prepared ?? [];
        report["fixture"] = prepared;
        if (prepared["success"].AsBool() != true)
        {
            throw new AcceptanceException($"fixture refused: {prepared.ToJson()}");
        }

        var observed = ObservedSession.Of(s);

        // Phase 1: the planner opens the field and the colonists sow it.
        var sown = new Census();
        for (var attempt = 1; attempt <= PhaseWindows && sown.Sown() == 0; attempt++)
        {
            var current = attempt;
            try
            {
                _ = await SustainedFood.ObserveAsync(
                    observed,
                    new SustainedObservation
                    {
                        Watch = new WatchConfig { Watch = TimeSpan.FromMinutes(12), Window = NonFoodWindow, Concern = _concern.Value },
                        Audit = async (h, rep, ct) =>
                        {
                            JsonObject reply;
                            (sown, reply) = await CensusAsync(h, string.Create(CultureInfo.InvariantCulture, $"sow-census-{current}"), ct).ConfigureAwait(false);
                            rep["sow_census"] = reply;
                            if (sown.Sown() == 0)
                            {
                                return;
                            }

                            // Phase 2 setup: grow the crop out rather than wait its grow days.
                            var grown = await h.CallAsync("mature", NonFoodOp, new JsonObject { ["action"] = "mature", ["plant"] = _plant, ["product"] = _product }, ct).ConfigureAwait(false);
                            rep["matured"] = grown;
                        },
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException($"sow window {attempt}: {e.Message}", e);
            }
        }

        if (sown.Zones.Count == 0)
        {
            throw new AcceptanceException($"no {_plant} growing zone opened for the {_product} deficit after {PhaseWindows} windows: {report["sow_census"].ToJson()}");
        }

        if (sown.Sown() == 0)
        {
            throw new AcceptanceException($"{_plant} zone opened but nothing was sown after {PhaseWindows} windows: {report["sow_census"].ToJson()}");
        }

        if (sown.Wild != 0)
        {
            throw new AcceptanceException($"a wild {_product} source stood on the map ({sown.Wild}), so the field is not what served it");
        }

        // Phase 2: the matured crop is harvested into the product.
        var harvested = new Census();
        for (var attempt = 1; attempt <= PhaseWindows && harvested.Units == 0; attempt++)
        {
            var current = attempt;
            try
            {
                _ = await SustainedFood.ObserveAsync(
                    observed,
                    new SustainedObservation
                    {
                        Watch = new WatchConfig { Watch = TimeSpan.FromMinutes(12), Window = NonFoodWindow, Concern = _concern.Value },
                        Audit = async (h, rep, ct) =>
                        {
                            JsonObject reply;
                            (harvested, reply) = await CensusAsync(h, string.Create(CultureInfo.InvariantCulture, $"harvest-census-{current}"), ct).ConfigureAwait(false);
                            rep["harvest_census"] = reply;
                        },
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException($"harvest window {attempt}: {e.Message}", e);
            }
        }

        if (harvested.Units == 0)
        {
            throw new AcceptanceException($"the matured {_plant} field yielded no {_product} after {PhaseWindows} windows: {report["harvest_census"].ToJson()}");
        }
    }
}
