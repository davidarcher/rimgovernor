namespace RimGovernor.Brain.Policy;

// Go: care_orders.go
// The medical reserve, the surgery part bills, baby food and mech gestation declare the one bill they want to
// the work ledger (OrderDeclarer) and place nothing themselves. Their bills are declare-only in the first
// pass (#2604): the ledger places a missing one and keeps a matching one, but never removes one as an orphan,
// because a gestation bill is the mech being formed and a part or medicine bill serves a patient. What makes a
// bill declare-only is its recipe (LedgerBillKind, set by the bench census), not who placed it, so the
// guarantee holds across a restart and for a bill the ledger itself placed.

public static partial class PolicyFunctions
{
    /// <summary>
    /// A bill selection as the ledger's order spec on its bench's definition. Null when the selected bench is
    /// not in the readback or has no definition: the declaration is then unknown.
    /// </summary>
    public static OrderSpec? SelectionOrder(BillSelection s, IEnumerable<GearBench> benches)
    {
        foreach (var b in benches)
        {
            if (b.ID == s.Bench && b.Def.Length != 0)
            {
                return new OrderSpec
                {
                    Recipe = s.Recipe,
                    Ingredients = s.Ingredients,
                    Worker = s.Worker,
                    Mode = s.Mode,
                    Target = s.Target,
                    BenchKind = b.Def,
                };
            }
        }

        return null;
    }

    /// <summary>
    /// A declare-only owner's declaration for one Round: the order of its selected bill, nothing when the
    /// selector chose none (a bill already standing makes its need, and the ledger keeps it), and an abstain
    /// when the selected bench cannot be named.
    /// </summary>
    public static Declared DeclareSelection(BillSelection s, bool selected, IEnumerable<GearBench> benches)
    {
        if (!selected)
        {
            return new Declared();
        }

        var spec = SelectionOrder(s, benches);
        if (spec is null)
        {
            return Abstaining(Cause.UnreadBenchDef);
        }

        var output = new Declared();
        output.Orders.Add(spec);
        return output;
    }

    /// <summary>
    /// MaintainMedicalReserves' declaration: the stock-target medicine bill SelectMedicineMethod produces. Any
    /// other choice declares nothing (the planner harvests, waits or has no deficit), except an unknown
    /// choice, which abstains.
    /// </summary>
    public static Declared DeclareMedicine(MedicineMethod m, IEnumerable<GearBench> benches)
    {
        switch (m.Kind)
        {
            case MedicineMethodKind.Unknown:
                return Abstaining(Cause.UnreadMedicine);
            case MedicineMethodKind.Produce:
                return DeclareSelection(
                    new BillSelection
                    {
                        Bench = m.Bench,
                        Recipe = m.Recipe,
                        Mode = BillMode.StockTarget,
                        Target = (int)m.Target,
                    },
                    true,
                    benches
                );
            default:
                return new Declared();
        }
    }
}
