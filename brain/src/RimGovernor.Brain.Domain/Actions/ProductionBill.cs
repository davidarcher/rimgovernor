namespace RimGovernor.Brain.Domain;

public enum BillMode
{
    FoodTarget,
    ButcherForever,
    HumanButcherForever,

    /// <summary>
    /// The generic "keep at least Target units of this recipe's output in stock" bill, the same
    /// pause-when-satisfied/unpause-below-half shape FoodTarget uses, reused as-is by GearProduce,
    /// MaintainResource-* and MaintainMedicalReserves rather than inventing a mode per consumer. Unlike
    /// FoodTarget it carries no food-specific recipe exclusion.
    /// </summary>
    StockTarget,
    GearBatch,
    BeerReserve,
}

/// <summary>Adds a native bill, optionally replacing the same recipe on its bench or an ordinary meal tier.</summary>
public sealed record ProductionBill : IActionPayload
{
    /// <summary>Go <c>NewProductionBill</c>; throws <see cref="DomainException"/> when invalid. Ingredients are stored sorted.</summary>
    public ProductionBill(string bench, string recipe, BillMode mode, int target, params string[] ingredients)
    {
        DomainException.Require(
            Identity.IsValidText(bench)
                && Identity.IsValidText(recipe)
                && mode is BillMode.FoodTarget or BillMode.ButcherForever or BillMode.StockTarget or BillMode.BeerReserve or BillMode.GearBatch
                && !(mode == BillMode.FoodTarget && target < 1)
                && !(mode == BillMode.ButcherForever && target != 0)
                && !(mode is BillMode.StockTarget or BillMode.BeerReserve or BillMode.GearBatch && target < 1),
            "invalid production bill"
        );
        DomainException.Require(!(mode == BillMode.ButcherForever && ingredients.Length > 0), "invalid bill ingredient override");
        var rows = ingredients.OrderBy(n => n, StringComparer.Ordinal).ToList();
        for (var i = 0; i < rows.Count; i++)
        {
            DomainException.Require(Identity.IsValidText(rows[i]) && !(i > 0 && rows[i] == rows[i - 1]), "invalid bill ingredient");
        }
        Bench = bench;
        Recipe = recipe;
        Mode = mode;
        Target = target;
        Ingredients = EquatableArray.From(rows);
        Worker = "";
        Replaces = "";
        Corpses = mode == BillMode.ButcherForever ? CorpseOf.Animal : null;
    }

    public string Bench { get; }

    public string Recipe { get; }

    public BillMode Mode { get; private init; }

    public int Target { get; }

    /// <summary>The exact allowed definition set, sorted; empty uses Auto recipe defaults.</summary>
    public EquatableArray<string> Ingredients { get; }

    /// <summary>The pinned worker's pawn identity; empty when the bill is not pinned.</summary>
    public string Worker { get; private init; }

    /// <summary>The native bill id this bill replaces (Go <c>Replaces</c>); empty when it replaces none.</summary>
    public string Replaces { get; private init; }

    /// <summary>A corpse bill's ingredient filter; null for other bills.</summary>
    public CorpseOf? Corpses { get; private init; }

    /// <summary>Go <c>NewHumanButcherBill</c>: a humanlike butcher bill is always pinned and never accepts animal corpses.</summary>
    public static ProductionBill NewHumanButcherBill(string bench, string recipe, string worker)
    {
        var bill = new ProductionBill(bench, recipe, BillMode.ButcherForever, 0);
        DomainException.Require(Identity.IsValidText(worker), "invalid human butcher bill");
        return bill with { Mode = BillMode.HumanButcherForever, Worker = worker, Corpses = CorpseOf.Stranger };
    }

    /// <summary>Go <c>PinWorker</c>: restricts a fixed-count batch to one pawn (one art bill per artist).</summary>
    public ProductionBill PinWorker(string worker)
    {
        DomainException.Require(Mode == BillMode.GearBatch && Identity.IsValidText(worker), "invalid pinned batch bill");
        return this with { Worker = worker };
    }

    /// <summary>
    /// Distinguishes the two mutually exclusive corpse filters while preserving claim keys for existing bills.
    /// </summary>
    public string ClaimRecipe =>
        Mode == BillMode.HumanButcherForever ? Recipe + "/humanlike"
        : Mode == BillMode.GearBatch && Worker.Length != 0 ? Recipe + "/" + Worker
        : Recipe;

    /// <summary>Go <c>ReplaceOwnedBill</c> (retains the wire name; replacement does not require ownership).</summary>
    public ProductionBill ReplaceOwnedBill(string id)
    {
        DomainException.Require(
            Identity.IsValidText(id) && Mode is BillMode.FoodTarget or BillMode.ButcherForever or BillMode.StockTarget or BillMode.HumanButcherForever,
            "invalid bill replacement"
        );
        return this with { Replaces = id };
    }
}

public sealed partial record DomainAction
{
    public static DomainAction NewProductionBillAction(ActionId id, ProductionBill bill) => Create(id, ActionKind.ProductionBill, bill);

    public ProductionBill? ProductionBill => PayloadOf<ProductionBill>(ActionKind.ProductionBill);
}
