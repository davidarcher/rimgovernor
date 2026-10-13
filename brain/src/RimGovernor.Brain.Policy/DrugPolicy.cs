namespace RimGovernor.Brain.Policy;

/// <summary>One native drug policy: its load id, label, holders and the entries that allow anything.</summary>
public sealed record OwnedDrugPolicy(string Id, string Label, EquatableArray<PawnId> Pawns, EquatableArray<DrugPolicyEntry> Entries);

/// <summary>
/// What one pawn's drug policy owes: the contents to write (null when the policy labelled with its short name
/// already carries exactly them) and the assignment (null when it already holds it).
/// </summary>
public sealed record DrugPolicyChange(DrugPolicy? Write, PawnSettings? Assign);

/// <summary>An addictive chemical a pawn can be addicted to, the drugs that satisfy it in preference order and the days between maintenance doses.</summary>
public sealed record AddictionDrug(string Chemical, EquatableArray<string> Drugs, double MaintainDays, bool AlwaysMaintain);

public static partial class PolicyFunctions
{
    /// <summary>The chemical tolerance severity at and above which a pawn stops taking that chemical for joy: the addiction risk is high.</summary>
    public const double HighTolerance = 0.5;

    /// <summary>
    /// The dependency drug's schedule, our own tuning: a gene's deficiency starts after five days without the
    /// chemical, so a dose every four days keeps it away.
    /// </summary>
    public const int DependencyDays = 4;

    /// <summary>
    /// The days between maintenance doses of a weanable addiction, our own tuning: inside the chemical need's
    /// decay, so a scheduled dose lands before withdrawal. A chemical whose addiction never fades takes DependencyDays.
    /// </summary>
    public const int MaintainDays = 2;

    /// <summary>How long a full-severity addiction takes to end without the drug; a weaning plan budgets doses over severity times it.</summary>
    public const int WeanDays = 30;

    /// <summary>Caps how far apart weaning doses spread, as a multiple of the maintenance interval.</summary>
    private const int MaxWeanWidening = 4;

    /// <summary>Reports whether the biome's diseases include one the preventive drug makes its taker immune to; false without such a drug.</summary>
    public static bool DiseaseBiome(ItemFacts items, IEnumerable<string> diseases)
    {
        if (items.Prevention is not { } prevention)
        {
            return false;
        }
        return diseases.Any(d => prevention.Diseases.Contains(d));
    }

    /// <summary>A drug taken for joy only, scheduling fields at vanilla's defaults.</summary>
    private static DrugPolicyEntry JoyEntry(string def) => new(def, true, false, false, 1, 1, 1, 0);

    /// <summary>
    /// The drug policy pawn should hold: beer, smokeleaf and psychite tea for joy, each unless the pawn is
    /// addicted to its chemical or its tolerance is high. A child, a teetotaler and a pawn with a chemical
    /// interest or fascination get none. Every pawn takes penoxycyline on schedule in a disease biome, and the
    /// drug of each chemical-dependency gene on schedule. Everything else is off. Null while the pawn's age,
    /// traits or chemical state are unknown.
    /// </summary>
    public static List<DrugPolicyEntry>? DrugEntries(WorkPawn pawn, ItemFacts items, bool diseaseBiome)
    {
        if (!pawn.Age.TryGetValue(out var age) || !pawn.Traits.TryGetValue(out var traits) || !pawn.PolicyInputs.TryGetValue(out var inputs))
        {
            return null;
        }
        var entries = new List<DrugPolicyEntry>();
        var interest = 0;
        foreach (var t in traits)
        {
            interest += t.Effects.ChemicalInterest;
        }
        if (age >= ChildAge && interest == 0)
        {
            entries = JoyEntries(inputs, items);
        }
        void Schedule(string def, double days) =>
            MergeEntry(entries, new DrugPolicyEntry(def, false, false, true, days, 1, 1, 0));
        if (diseaseBiome && items.Prevention is { } prevention)
        {
            Schedule(prevention.Drug.Value, prevention.Days);
        }
        foreach (var c in inputs.DependencyChemicals)
        {
            var (def, defOk) = items.DependencyDrug(c);
            if (defOk)
            {
                Schedule(def.Value, DependencyDays);
            }
        }
        return entries;
    }

    /// <summary>The recreation drugs a pawn may take for joy: each unless it is addicted to or highly tolerant of the drug's chemical.</summary>
    private static List<DrugPolicyEntry> JoyEntries(PawnPolicyInputs inputs, ItemFacts items)
    {
        var entries = new List<DrugPolicyEntry>();
        foreach (var d in items.RecreationDrugs())
        {
            if (!ChemicalRisky(inputs, d.Chemical))
            {
                entries.Add(JoyEntry(d.Def.Value));
            }
        }
        return entries;
    }

    /// <summary>Reports whether pawn is addicted to chemical, in its withdrawal or highly tolerant of it.</summary>
    private static bool ChemicalRisky(PawnPolicyInputs inputs, string chemical)
    {
        foreach (var c in inputs.Chemicals)
        {
            if (c.Chemical != chemical)
            {
                continue;
            }
            var addicted = c.Addiction.TryGetValue(out _);
            var tolerant = c.Tolerance.TryGetValue(out var tolerance);
            if (addicted || c.Withdrawal || (tolerant && tolerance >= HighTolerance))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The combat drug a soldier carries: the preferred combat-enhancing drug, or the first of them in stock when
    /// the colony's stock is known and the preferred one is out. Null when the game has no combat drug.
    /// </summary>
    public static Drug? CarryDrug(ItemFacts items, IReadOnlyDictionary<string, long>? stock)
    {
        var drugs = items.CombatDrugs();
        if (drugs.Count == 0)
        {
            return null;
        }
        if (stock is not null)
        {
            foreach (var d in drugs)
            {
                if (stock.GetValueOrDefault(d.Def.Value) > 0)
                {
                    return d;
                }
            }
        }
        return drugs[0];
    }

    /// <summary>
    /// The combat drug a soldier-squad member carries: one dose taken to inventory, not for joy, under the same
    /// exclusions as DrugEntries. Null while DrugEntries is unknown.
    /// </summary>
    public static List<DrugPolicyEntry>? CarryEntries(WorkPawn pawn, ItemFacts items, Drug drug)
    {
        if (DrugEntries(pawn, items, false) is null)
        {
            return null;
        }
        pawn.Age.TryGetValue(out var age);
        pawn.Traits.TryGetValue(out var traits);
        pawn.PolicyInputs.TryGetValue(out var inputs);
        var interest = 0;
        foreach (var t in traits)
        {
            interest += t.Effects.ChemicalInterest;
        }
        if (age < ChildAge || interest != 0 || ChemicalRisky(inputs!, drug.Chemical))
        {
            return [];
        }
        return [new DrugPolicyEntry(drug.Def.Value, false, false, false, 1, 1, 1, 1)];
    }

    /// <summary>
    /// The addictive chemicals of the catalog's drugs, by chemical name. A chemical whose addiction does not
    /// fade on its own is never weaned: it is always maintained.
    /// </summary>
    public static IReadOnlyList<AddictionDrug> AddictionDrugs(this ItemFacts i)
    {
        var output = new List<AddictionDrug>();
        foreach (var chemical in i.DrugChemicals())
        {
            var always = !i.Chemicals[chemical].Weanable;
            output.Add(new AddictionDrug(
                chemical,
                EquatableArray.From(i.DrugsOf(chemical).Select(d => d.Def.Value)),
                always ? DependencyDays : MaintainDays,
                always));
        }
        return output;
    }

    /// <summary>The whole days between weaning doses at addiction severity: the maintenance interval widened as the addiction fades.</summary>
    public static double WeanInterval(AddictionDrug a, double severity)
    {
        var widening = Math.Min(MaxWeanWidening, 1 / Math.Max(severity, 1.0 / MaxWeanWidening));
        return Math.Max(1, Math.Round(a.MaintainDays * widening, MidpointRounding.AwayFromZero));
    }

    /// <summary>Bounds the doses a wean from severity takes: the remaining weaning period at the current (narrowest) interval.</summary>
    public static long WeanDoses(AddictionDrug a, double severity) =>
        (long)Math.Ceiling(WeanDays * Math.Max(severity, 0) / WeanInterval(a, severity));

    /// <summary>
    /// The scheduled doses pawn's addictions owe, never cold turkey: an addiction is weaned at a widening
    /// interval when the colony's remaining stock (null while unknown) of its first stocked drug covers the
    /// weaning doses, which it then consumes; otherwise maintained at its interval and allowed for the need.
    /// Luciferium is always maintained. An addiction with none of its drugs in stock schedules nothing.
    /// </summary>
    public static List<DrugPolicyEntry> AddictionEntries(WorkPawn pawn, ItemFacts items, Dictionary<string, long>? stock) =>
        pawn.PolicyInputs.TryGetValue(out var inputs) ? AddictionEntriesOf(inputs, items, stock) : [];

    private static List<DrugPolicyEntry> AddictionEntriesOf(PawnPolicyInputs inputs, ItemFacts items, Dictionary<string, long>? stock)
    {
        var output = new List<DrugPolicyEntry>();
        foreach (var a in items.AddictionDrugs())
        {
            var severity = 0.0;
            var addicted = false;
            foreach (var c in inputs.Chemicals)
            {
                if (c.Addiction.TryGetValue(out var v) && c.Chemical == a.Chemical)
                {
                    severity = Math.Max(severity, v);
                    addicted = true;
                }
            }
            if (!addicted)
            {
                continue;
            }
            var drug = a.Drugs[0];
            if (stock is not null)
            {
                drug = "";
                foreach (var d in a.Drugs)
                {
                    if (stock.GetValueOrDefault(d) > 0)
                    {
                        drug = d;
                        break;
                    }
                }
                if (drug == "")
                {
                    continue;
                }
            }
            var doses = WeanDoses(a, severity);
            if (!a.AlwaysMaintain && stock is not null && stock.GetValueOrDefault(drug) >= doses)
            {
                stock[drug] = stock.GetValueOrDefault(drug) - doses;
                output.Add(new DrugPolicyEntry(drug, false, false, true, WeanInterval(a, severity), 1, 1, 0));
            }
            else
            {
                output.Add(new DrugPolicyEntry(drug, false, true, true, a.MaintainDays, 1, 1, 0));
            }
        }
        return output;
    }

    /// <summary>
    /// The per-pawn drug policy writes owed: each owned pawn with a drug tracker holds the policy labelled with
    /// its short name, carrying DrugEntries and AddictionEntries against the colony's drug stock, allotted to
    /// pawns in order, and squad members' CarryEntries. A pawn whose short name another owned pawn shares or
    /// that several policies carry waits. biomeDiseases is the colony map biome's disease hediffs. Each
    /// prisoner holds its own policy too, allowing no recreation, only the addiction maintenance and weaning its
    /// chemicals owe.
    /// </summary>
    public static IReadOnlyList<DrugPolicyChange> DrugPolicyChanges(
        ItemFacts items,
        IReadOnlyList<WorkPawn> pawns,
        IReadOnlyList<PrisonerFacts> prisoners,
        IReadOnlyList<OwnedName> names,
        IReadOnlyList<OwnedDrugPolicy> policies,
        Fact<EquatableArray<Amount>> stock,
        IEnumerable<string> biomeDiseases,
        SoldierSquad squad)
    {
        var diseaseBiome = DiseaseBiome(items, biomeDiseases);
        Dictionary<string, long>? remaining = null;
        if (stock.TryGetValue(out var stockRows))
        {
            remaining = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var r in stockRows)
            {
                remaining[r.Resource.Value] = remaining.GetValueOrDefault(r.Resource.Value) + r.Count;
            }
        }
        var carry = CarryDrug(items, remaining);
        var shortNames = new Dictionary<PawnId, string>();
        var count = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var n in names)
        {
            shortNames[n.Pawn] = n.Short;
            var key = n.Short.ToLowerInvariant();
            count[key] = count.GetValueOrDefault(key) + 1;
        }

        // A policy holder; want builds its entries only once the holder passes the name checks, because it draws
        // on the shared drug stock.
        var rows = new List<(PawnId Id, PawnPolicyInputs Inputs, Func<List<DrugPolicyEntry>?> Want)>();
        foreach (var pawn in pawns)
        {
            if (!pawn.PolicyInputs.TryGetValue(out var inputs))
            {
                continue;
            }
            var captured = pawn;
            rows.Add((pawn.Id, inputs, () =>
            {
                var want = DrugEntries(captured, items, diseaseBiome);
                if (want is null)
                {
                    return null;
                }
                if (squad.IsSoldier(captured.Id) && carry is { } drug)
                {
                    foreach (var e in CarryEntries(captured, items, drug) ?? [])
                    {
                        MergeEntry(want, e);
                    }
                }
                foreach (var e in AddictionEntries(captured, items, remaining))
                {
                    MergeEntry(want, e);
                }
                return want;
            }));
        }
        foreach (var prisoner in prisoners)
        {
            if (prisoner.PolicyInputs.TryGetValue(out var inputs))
            {
                rows.Add((prisoner.Pawn, inputs, () => AddictionEntriesOf(inputs, items, remaining)));
            }
        }

        var output = new List<DrugPolicyChange>();
        foreach (var row in rows)
        {
            var inputs = row.Inputs;
            var name = shortNames.GetValueOrDefault(row.Id) ?? "";
            if (inputs.DrugPolicy == "" || name == "" || count.GetValueOrDefault(name.ToLowerInvariant()) != 1)
            {
                continue;
            }
            var want = row.Want();
            if (want is null)
            {
                continue;
            }
            DrugPolicy v;
            try
            {
                v = new DrugPolicy(name, want);
            }
            catch (DomainException)
            {
                continue;
            }
            var own = policies.Where(p => p.Label == name).ToList();
            if (own.Count > 1)
            {
                continue;
            }
            DrugPolicy? write = null;
            PawnSettings? assign = null;
            if (own.Count == 0 || !DrugEntriesEqual(own[0].Entries, v.Entries))
            {
                write = v;
            }
            if (own.Count == 0 || inputs.DrugPolicy != own[0].Id)
            {
                try
                {
                    assign = PawnSettings.NewDrugPolicySetting((Domain.PawnId)row.Id, name);
                }
                catch (DomainException)
                {
                    continue;
                }
            }
            if (write is not null || assign is not null)
            {
                output.Add(new DrugPolicyChange(write, assign));
            }
        }
        return output;
    }

    /// <summary>Adds e to entries, or folds it into the entry for the same drug: allowed for whatever either allows, at the shorter schedule.</summary>
    private static void MergeEntry(List<DrugPolicyEntry> entries, DrugPolicyEntry e)
    {
        for (var i = 0; i < entries.Count; i++)
        {
            var x = entries[i];
            if (x.Drug != e.Drug)
            {
                continue;
            }
            var days = x.DaysFrequency;
            if (e.Scheduled && (!x.Scheduled || e.DaysFrequency < x.DaysFrequency))
            {
                days = e.DaysFrequency;
            }
            entries[i] = x with
            {
                DaysFrequency = days,
                Joy = x.Joy || e.Joy,
                Addiction = x.Addiction || e.Addiction,
                Scheduled = x.Scheduled || e.Scheduled,
                TakeToInventory = Math.Max(x.TakeToInventory, e.TakeToInventory),
            };
            return;
        }
        entries.Add(e);
    }

    /// <summary>Compares observed entries with canonical ones; the observed order is native's.</summary>
    private static bool DrugEntriesEqual(IEnumerable<DrugPolicyEntry> observed, IEnumerable<DrugPolicyEntry> want) =>
        observed.OrderBy(e => e.Drug, StringComparer.Ordinal).SequenceEqual(want);
}
