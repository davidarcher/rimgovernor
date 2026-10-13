using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Simulation.Supply;

/// <summary>
/// A repeating calendar window: active on days d where d mod Period is in [From, To). A zero Period is
/// always active.
/// </summary>
public readonly record struct Window(int Period, int From, int To)
{
    /// <summary>Whether the window is open on day.</summary>
    public bool Active(int day)
    {
        if (Period <= 0)
        {
            return true;
        }
        var d = day % Period;
        return d >= From && d < To;
    }
}

/// <summary>An amount of a good per unit a source delivers (or, in Costs, per unit it consumes).</summary>
public readonly record struct Yield(Good Good, double PerUnit);

/// <summary>
/// A field of cells that grow only inside the Window, mature after GrowDays growing days and are harvested in
/// a burst of up to HarvestCells per day. It replants while the source is open.
/// </summary>
public sealed record CropField(double Cells, int GrowDays, double HarvestCells, Window Window);

/// <summary>
/// Delivers goods while opened. Capacity is units per day at full labor; Stock (when Finite) is the units
/// left, regenerating Regen per day up to Max; deliveries pause while Stock/Max is below Floor. Labor is pawn
/// ticks per day of work while delivering. Open and Stock are the initial state; a run keeps its own mutable
/// copy (<see cref="SourceRun"/>), so a Source is never mutated.
/// </summary>
public sealed record Source
{
    public string Id { get; init; } = "";

    public IReadOnlyList<Yield> Yields { get; init; } = [];

    public IReadOnlyList<Yield> Costs { get; init; } = [];

    public bool Open { get; init; }

    /// <summary>Days from opening to the first delivery.</summary>
    public int Lead { get; init; }

    public double Labor { get; init; }

    public double Capacity { get; init; }

    public bool Finite { get; init; }

    public double Stock { get; init; }

    public double Max { get; init; }

    public double Regen { get; init; }

    public double Floor { get; init; }

    public Window Window { get; init; }

    /// <summary>Refills Stock on the first day of each Window (trade arrival).</summary>
    public double Restock { get; init; }

    /// <summary>Varies daily capacity by up to +-Jitter, drawn from the world seed.</summary>
    public double Jitter { get; init; }

    public CropField? Crop { get; init; }

    /// <summary>The grid power the source needs while delivering (a deep drill); World.Power is shared in source order.</summary>
    public double PowerDraw { get; init; }

    /// <summary>Safe sources (loot, salvage) deliver only while no threat is active.</summary>
    public bool Safe { get; init; }

    private static Source Labored(Source s, double perUnit) => s with { Labor = s.Capacity * perUnit };

    /// <summary>
    /// A tree cluster holding wood (up to max) chopped at perDay and regrowing TreeRegrowFraction x max per day.
    /// </summary>
    public static Source NewTrees(string id, double wood, double max, double perDay) =>
        Labored(
            new Source
            {
                Id = id,
                Yields = [new Yield(Good.Wood, 1)],
                Capacity = perDay,
                Finite = true,
                Stock = wood,
                Max = max,
                Regen = SupplyConstants.TreeRegrowFraction * max,
            },
            SupplyConstants.ChopWork);

    /// <summary>A finite stone deposit mined as chunks at perDay.</summary>
    public static Source NewQuarry(string id, double chunks, double perDay) =>
        Labored(
            new Source
            {
                Id = id,
                Yields = [new Yield(Good.StoneChunks, 1)],
                Capacity = perDay,
                Finite = true,
                Stock = chunks,
                Max = chunks,
            },
            SupplyConstants.MineWork);

    /// <summary>A finite ore vein of good mined at perDay. A buried vein sets tunnelDays: the lead between opening and the first delivery.</summary>
    public static Source NewVein(string id, Good good, double units, double perDay, int tunnelDays) =>
        Labored(
            new Source
            {
                Id = id,
                Yields = [new Yield(good, 1)],
                Capacity = perDay,
                Finite = true,
                Stock = units,
                Max = units,
                Lead = tunnelDays,
            },
            SupplyConstants.MineWork);

    /// <summary>A drill over a lump of good: perDay while powered with powerDraw from World.Power, depleting the lump.</summary>
    public static Source NewDeepDrill(string id, Good good, double lump, double perDay, double powerDraw) =>
        Labored(
            new Source
            {
                Id = id,
                Yields = [new Yield(good, 1)],
                Capacity = perDay,
                Finite = true,
                Stock = lump,
                Max = lump,
                PowerDraw = powerDraw,
            },
            SupplyConstants.DeepDrillWork);

    /// <summary>
    /// A bench bill: batchesPerDay batches a day, each consuming inPerBatch of input and yielding outPerBatch
    /// of output, with ProduceWork ticks per output unit.
    /// </summary>
    public static Source NewRecipe(string id, Good input, double inPerBatch, Good output, double outPerBatch, double batchesPerDay) =>
        new()
        {
            Id = id,
            Yields = [new Yield(output, outPerBatch)],
            Costs = [new Yield(input, inPerBatch)],
            Capacity = batchesPerDay,
            Labor = batchesPerDay * outPerBatch * SupplyConstants.ProduceWork,
        };

    /// <summary>Cuts chunks into blocks at the Core recipe ratio.</summary>
    public static Source NewStonecutter(string id, double chunksPerDay) =>
        NewRecipe(id, Good.StoneChunks, 1, Good.StoneBlocks, SupplyConstants.StoneBlocksPerChunk, chunksPerDay);

    /// <summary>A one-shot windfall of good collected at perDay, only while no threat is active.</summary>
    public static Source NewLoot(string id, Good good, double amount, double perDay) =>
        Labored(
            new Source
            {
                Id = id,
                Yields = [new Yield(good, 1)],
                Capacity = perDay,
                Finite = true,
                Stock = amount,
                Max = amount,
                Safe = true,
            },
            SupplyConstants.LootWork);

    /// <summary>A one-shot wreck of good stripped at perDay under the same threat gate as loot.</summary>
    public static Source NewSalvage(string id, Good good, double amount, double perDay) =>
        NewLoot(id, good, amount, perDay) with { Labor = perDay * SupplyConstants.SalvageWork };

    /// <summary>Adds a co-yield per unit, such as leather from a hunt.</summary>
    public Source WithYield(Good g, double perUnit) => this with { Yields = [.. Yields, new Yield(g, perUnit)] };

    /// <summary>
    /// A fishing region: population regenerating 0.025 x max per day, paused below
    /// <see cref="DomainConstants.FishingPopulationFloor"/>, with catch limited by fishers.
    /// </summary>
    public static Source NewFishing(string id, double population, double max, int fishers, double nutritionPerFish) =>
        new()
        {
            Id = id,
            Yields = [new Yield(Good.Nutrition, nutritionPerFish)],
            Capacity = fishers * SupplyConstants.FishPerFisherDay,
            Labor = fishers * SupplyConstants.FishLaborPerFisher,
            Finite = true,
            Stock = population,
            Max = max,
            Regen = SupplyConstants.FishingRegenFraction * max,
            Floor = DomainConstants.FishingPopulationFloor,
        };

    /// <summary>A food crop field growing inside window; the first harvest comes growDays growing days after the first planting.</summary>
    public static Source NewCrop(string id, double cells, int growDays, double nutritionPerCell, Window window, double harvestCellsPerDay) =>
        NewCropOf(id, Good.Nutrition, cells, growDays, nutritionPerCell, window, harvestCellsPerDay);

    /// <summary>A crop field of any good (cotton).</summary>
    public static Source NewCropOf(string id, Good good, double cells, int growDays, double perCell, Window window, double harvestCellsPerDay) =>
        new()
        {
            Id = id,
            Yields = [new Yield(good, perCell)],
            Labor = harvestCellsPerDay * SupplyConstants.CropHarvestWork,
            Crop = new CropField(cells, growDays, harvestCellsPerDay, window),
        };

    /// <summary>A hunt herd: animals worth nutritionEach, killed by hunters and not regrowing. Danger in [0,1] cuts the kill rate and raises labor.</summary>
    public static Source NewHerd(string id, double animals, double nutritionEach, int hunters, double danger)
    {
        var capacity = hunters * SupplyConstants.KillsPerHunterDay * (1 - Shock.Clamp01(danger));
        return new Source
        {
            Id = id,
            Yields = [new Yield(Good.Nutrition, nutritionEach)],
            Capacity = capacity,
            Labor = capacity * SupplyConstants.HuntWorkTicks * (1 + Shock.Clamp01(danger)),
            Finite = true,
            Stock = animals,
            Max = animals,
        };
    }

    /// <summary>A seasonal forage patch regenerating regen units per day.</summary>
    public static Source NewForage(string id, double stock, double regen, double nutritionPerUnit, Window window, int foragers) =>
        new()
        {
            Id = id,
            Yields = [new Yield(Good.Nutrition, nutritionPerUnit)],
            Capacity = foragers * 4,
            Labor = foragers * 4 * SupplyConstants.ForageWorkTicks,
            Finite = true,
            Stock = stock,
            Max = stock,
            Regen = regen,
            Window = window,
        };

    /// <summary>Animal products (milk, eggs) at perAnimal units per day.</summary>
    public static Source NewProducts(string id, double animals, double perAnimal, double nutritionPerUnit) =>
        new()
        {
            Id = id,
            Yields = [new Yield(Good.Nutrition, nutritionPerUnit)],
            Capacity = animals * perAnimal,
            Labor = animals * SupplyConstants.ProductWork,
        };

    /// <summary>A food trader visiting inside window with restock units, paid for in Silver at price per unit.</summary>
    public static Source NewTrade(string id, Window window, double restock, double nutritionPerUnit, double price) =>
        NewTradeOf(id, Good.Nutrition, window, restock, nutritionPerUnit, price);

    /// <summary>A caravan selling any good. Caravans carry no pawn labor (policy.AcquisitionLaborPerUnit[trade] is 0).</summary>
    public static Source NewTradeOf(string id, Good good, Window window, double restock, double perUnit, double price) =>
        new()
        {
            Id = id,
            Yields = [new Yield(good, perUnit)],
            Costs = [new Yield(Good.Silver, price)],
            Capacity = restock,
            Finite = true,
            Max = restock,
            Restock = restock,
            Window = window,
        };
}

/// <summary>The mutable state of one <see cref="Source"/> inside one run.</summary>
internal sealed class SourceRun
{
    private readonly GoRandom _rng;

    public SourceRun(Source config, ulong seed, int index)
    {
        Config = config;
        Open = config.Open;
        Stock = config.Stock;
        CapScale = 1;
        _rng = new GoRandom(seed, (ulong)index + 1);
    }

    public Source Config { get; }

    public bool Open { get; set; }

    public double Stock { get; set; }

    /// <summary>A crop's cells in the ground.</summary>
    public double Planted { get; set; }

    /// <summary>A crop's growing days accrued.</summary>
    public int Progress { get; set; }

    public int OpenedDay { get; set; }

    public bool Removed { get; set; }

    public double CapScale { get; set; }

    public int PausedUntil { get; set; }

    public int OffUntil { get; set; }

    public double Delivered { get; set; }

    public double Last { get; set; }

    public bool Delivering(int day) => Open && !Removed && day >= OpenedDay + Config.Lead;

    public bool Paused(int day) => day < PausedUntil;

    /// <summary>The units deliverable on day at full labor, before costs.</summary>
    public double Available(int day)
    {
        if (!Delivering(day) || day < OffUntil)
        {
            return 0;
        }
        if (Config.Crop is { } c)
        {
            if (Progress < c.GrowDays || Planted <= 0)
            {
                return 0;
            }
            return Math.Min(Planted, c.HarvestCells) * CapScale;
        }
        if (!Config.Window.Active(day))
        {
            return 0;
        }
        if (Config.Finite && Config.Max > 0 && Stock < Config.Floor * Config.Max)
        {
            return 0;
        }
        var v = Config.Capacity * CapScale;
        if (Config.Jitter > 0)
        {
            v *= 1 + (Config.Jitter * ((2 * _rng.NextDouble()) - 1));
        }
        if (Config.Finite)
        {
            v = Math.Min(v, Stock);
        }
        return Math.Max(v, 0);
    }

    /// <summary>Records taken units delivered.</summary>
    public void Take(double taken)
    {
        Delivered += taken;
        Last = taken;
        if (Config.Crop is not null)
        {
            Planted -= taken;
        }
        else if (Config.Finite)
        {
            Stock -= taken;
        }
    }

    /// <summary>Restocks a trader on the first day of its window, before deliveries.</summary>
    public void Arrive(int day)
    {
        if (Config.Restock > 0 && !Removed && Config.Window.Active(day) && (day == 0 || !Config.Window.Active(day - 1)))
        {
            Stock = Config.Restock;
        }
    }

    /// <summary>Runs regeneration and crop growth at the end of day.</summary>
    public void Grow(int day)
    {
        if (Removed || Paused(day))
        {
            return;
        }
        if (Config.Max > 0 && Config.Regen > 0)
        {
            Stock = Math.Min(Config.Max, Stock + Config.Regen);
        }
        if (Config.Crop is { } c && Open)
        {
            if (Planted <= 1e-9)
            {
                Planted = 0;
                Progress = 0;
                if (c.Window.Active(day))
                {
                    Planted = c.Cells;
                }
            }
            else if (c.Window.Active(day) && Progress < c.GrowDays)
            {
                Progress++;
            }
        }
    }

    public SourceView View(int day)
    {
        var rate = 0.0;
        var crop = false;
        var growLeft = 0;
        if (Removed)
        {
            // no rate
        }
        else if (Config.Crop is { } c)
        {
            crop = true;
            rate = c.HarvestCells * CapScale;
            growLeft = c.GrowDays;
            if (Planted > 0)
            {
                growLeft = Math.Max(0, c.GrowDays - Progress);
            }
        }
        else if (Config.Window.Active(day))
        {
            rate = Config.Capacity * CapScale;
            if (Config.Finite)
            {
                rate = Math.Min(rate, Stock);
            }
        }
        return new SourceView
        {
            Id = Config.Id,
            Open = Open,
            Delivering = Delivering(day),
            Removed = Removed,
            LeadLeft = Math.Max(0, OpenedDay + Config.Lead - day),
            Stock = Stock,
            Max = Config.Max,
            Last = Last,
            Rate = rate,
            Crop = crop,
            GrowLeft = growLeft,
        };
    }
}
