package policy

import (
	"math"
	"slices"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// HighTolerance is the chemical tolerance severity at and above which a
// pawn stops taking that chemical for joy: the addiction risk is high.
const HighTolerance = 0.5

// DependencyDays is the dependency drug's schedule, our own tuning: a
// gene's deficiency starts after five days without the chemical, so a dose
// every four days keeps it away. The drug of each chemical is the catalog's
// (ItemFacts.DependencyDrug, #1539).
const DependencyDays = 4

// MaintainDays is the days between maintenance doses of a weanable
// addiction, our own tuning: inside the chemical need's decay, so a
// scheduled dose lands before withdrawal. A chemical whose addiction never
// fades takes DependencyDays.
const MaintainDays = 2

// DiseaseBiome reports whether the biome's diseases include one the
// preventive drug makes its taker immune to; false without such a drug.
func DiseaseBiome(items ItemFacts, diseases []string) bool {
	if items.Prevention == nil {
		return false
	}
	for _, d := range diseases {
		if slices.Contains(items.Prevention.Diseases, d) {
			return true
		}
	}
	return false
}

// DrugPolicyEntry is one native drug policy: its load id, label, holders
// and the entries that allow anything.
type DrugPolicyEntry struct {
	ID, Label string
	Pawns     []PawnID
	Entries   []domain.DrugPolicyEntry
}

// DrugPolicyChange is what one pawn's drug policy owes: the contents to
// write (nil when the policy labelled with its short name already carries
// exactly them) and the assignment (nil when it already holds it).
type DrugPolicyChange struct {
	Write  *domain.DrugPolicy
	Assign *domain.PawnSettings
}

// joyEntry is a drug taken for joy only, scheduling fields at vanilla's
// defaults.
func joyEntry(def string) domain.DrugPolicyEntry {
	return domain.DrugPolicyEntry{Drug: def, Joy: true, DaysFrequency: 1, OnlyIfMoodBelow: 1, OnlyIfJoyBelow: 1}
}

// DrugEntries is the drug policy pawn should hold (#1537): beer, smokeleaf
// and psychite tea for joy, each unless the pawn is addicted to its
// chemical or its tolerance is high. A child, a teetotaler and a pawn with
// a chemical interest or fascination (every recreation drug is addictive)
// get none. Every pawn takes penoxycyline on schedule in a disease biome,
// and the drug of each chemical-dependency gene on schedule (#1539).
// Everything else is off. Unknown while the pawn's age, traits or chemical
// state are.
func DrugEntries(pawn WorkPawn, items ItemFacts, diseaseBiome bool) ([]domain.DrugPolicyEntry, bool) {
	age, ak := pawn.Age.Value()
	traits, tk := pawn.Traits.Value()
	inputs, ik := pawn.PolicyInputs.Value()
	if !ak || !tk || !ik {
		return nil, false
	}
	entries := []domain.DrugPolicyEntry{}
	interest := 0
	for _, t := range traits {
		interest += TraitEffect(t).ChemicalInterest
	}
	if age >= childAge && interest == 0 {
		entries = joyEntries(inputs, items)
	}
	schedule := func(def string, days float64) {
		entries = mergeEntry(entries, domain.DrugPolicyEntry{Drug: def, Scheduled: true, DaysFrequency: days, OnlyIfMoodBelow: 1, OnlyIfJoyBelow: 1})
	}
	if diseaseBiome && items.Prevention != nil {
		schedule(string(items.Prevention.Drug), items.Prevention.Days)
	}
	for _, c := range inputs.DependencyChemicals {
		if def, ok := items.DependencyDrug(c); ok {
			schedule(string(def), DependencyDays)
		}
	}
	return entries, true
}

// joyEntries are the recreation drugs a pawn may take for joy: each unless
// it is addicted to or highly tolerant of the drug's chemical.
func joyEntries(inputs PawnPolicyInputs, items ItemFacts) []domain.DrugPolicyEntry {
	entries := []domain.DrugPolicyEntry{}
	for _, d := range items.RecreationDrugs() {
		if !chemicalRisky(inputs, d.Chemical) {
			entries = append(entries, joyEntry(string(d.Def)))
		}
	}
	return entries
}

// chemicalRisky reports whether pawn is addicted to chemical, in its
// withdrawal or highly tolerant of it.
func chemicalRisky(inputs PawnPolicyInputs, chemical string) bool {
	for _, c := range inputs.Chemicals {
		if c.Chemical != chemical {
			continue
		}
		_, addicted := c.Addiction.Value()
		tolerance, tolerant := c.Tolerance.Value()
		if addicted || c.Withdrawal || tolerant && tolerance >= HighTolerance {
			return true
		}
	}
	return false
}

// CarryDrug is the combat drug a soldier carries (#1540): the preferred
// combat-enhancing drug (ItemFacts.CombatDrugs), or the first of them in
// stock when the colony's stock is known and the preferred one is out.
// Ingesting on draft is #1311. false when the game has no combat drug.
func CarryDrug(items ItemFacts, stock map[string]int64) (Drug, bool) {
	drugs := items.CombatDrugs()
	if len(drugs) == 0 {
		return Drug{}, false
	}
	if stock != nil {
		for _, d := range drugs {
			if stock[string(d.Def)] > 0 {
				return d, true
			}
		}
	}
	return drugs[0], true
}

// CarryEntries is the combat drug a soldier-squad member carries (#1540):
// one dose of drug taken to inventory, not for joy, under the same
// exclusions as DrugEntries. Unknown while DrugEntries is.
func CarryEntries(pawn WorkPawn, items ItemFacts, drug Drug) ([]domain.DrugPolicyEntry, bool) {
	if _, ok := DrugEntries(pawn, items, false); !ok {
		return nil, false
	}
	age, _ := pawn.Age.Value()
	traits, _ := pawn.Traits.Value()
	inputs, _ := pawn.PolicyInputs.Value()
	interest := 0
	for _, t := range traits {
		interest += TraitEffect(t).ChemicalInterest
	}
	if age < childAge || interest != 0 || chemicalRisky(inputs, drug.Chemical) {
		return nil, true
	}
	return []domain.DrugPolicyEntry{{Drug: string(drug.Def), TakeToInventory: 1, DaysFrequency: 1, OnlyIfMoodBelow: 1, OnlyIfJoyBelow: 1}}, true
}

// AddictionDrug is a chemical a pawn can be addicted to, the drugs that
// satisfy it in preference order and the days between maintenance doses.
type AddictionDrug struct {
	Chemical       string
	Drugs          []string
	MaintainDays   float64
	AlwaysMaintain bool
}

// AddictionDrugs are the addictive chemicals of the catalog's drugs, by
// chemical name (#1538). A chemical whose addiction does not fade on its own
// (ItemFacts.Chemicals) is never weaned: it is always maintained.
func (i ItemFacts) AddictionDrugs() []AddictionDrug {
	var out []AddictionDrug
	for _, chemical := range i.DrugChemicals() {
		a := AddictionDrug{Chemical: chemical, MaintainDays: MaintainDays, AlwaysMaintain: !i.Chemicals[chemical].Weanable}
		if a.AlwaysMaintain {
			a.MaintainDays = DependencyDays
		}
		for _, d := range i.DrugsOf(chemical) {
			a.Drugs = append(a.Drugs, string(d.Def))
		}
		out = append(out, a)
	}
	return out
}

// WeanDays is how long a full-severity addiction takes to end without the
// drug; a weaning plan budgets doses over severity times it.
const WeanDays = 30

// maxWeanWidening caps how far apart weaning doses spread, as a multiple of
// the maintenance interval.
const maxWeanWidening = 4

// WeanInterval is the whole days between weaning doses at addiction
// severity: the maintenance interval widened as the addiction fades.
func WeanInterval(a AddictionDrug, severity float64) float64 {
	widening := math.Min(maxWeanWidening, 1/math.Max(severity, 1.0/maxWeanWidening))
	return math.Max(1, math.Round(a.MaintainDays*widening))
}

// WeanDoses bounds the doses a wean from severity takes: the remaining
// weaning period at the current (narrowest) interval.
func WeanDoses(a AddictionDrug, severity float64) int64 {
	return int64(math.Ceil(WeanDays * math.Max(severity, 0) / WeanInterval(a, severity)))
}

// AddictionEntries are the scheduled doses pawn's addictions owe (#1538),
// never cold turkey: an addiction is weaned at a widening interval when the
// colony's remaining stock (nil while unknown) of its first stocked drug
// covers the weaning doses, which it then consumes; otherwise maintained at
// its interval and allowed for the need. Luciferium is always maintained.
// An addiction with none of its drugs in stock schedules nothing.
func AddictionEntries(pawn WorkPawn, items ItemFacts, stock map[string]int64) []domain.DrugPolicyEntry {
	inputs, ok := pawn.PolicyInputs.Value()
	if !ok {
		return nil
	}
	return addictionEntries(inputs, items, stock)
}

func addictionEntries(inputs PawnPolicyInputs, items ItemFacts, stock map[string]int64) []domain.DrugPolicyEntry {
	var out []domain.DrugPolicyEntry
	for _, a := range items.AddictionDrugs() {
		severity, addicted := 0.0, false
		for _, c := range inputs.Chemicals {
			if v, ok := c.Addiction.Value(); ok && c.Chemical == a.Chemical {
				severity, addicted = math.Max(severity, v), true
			}
		}
		if !addicted {
			continue
		}
		drug := a.Drugs[0]
		if stock != nil {
			drug = ""
			for _, d := range a.Drugs {
				if stock[d] > 0 {
					drug = d
					break
				}
			}
			if drug == "" {
				continue
			}
		}
		entry := domain.DrugPolicyEntry{Drug: drug, Scheduled: true, OnlyIfMoodBelow: 1, OnlyIfJoyBelow: 1}
		if doses := WeanDoses(a, severity); !a.AlwaysMaintain && stock != nil && stock[drug] >= doses {
			stock[drug] -= doses
			entry.DaysFrequency = WeanInterval(a, severity)
		} else {
			entry.Addiction, entry.DaysFrequency = true, a.MaintainDays
		}
		out = append(out, entry)
	}
	return out
}

// DrugPolicyChanges are the per-pawn drug policy writes owed (#1537): each
// owned pawn with a drug tracker holds the policy labelled with its short
// name, carrying DrugEntries and AddictionEntries against the colony's
// drug stock (#1538), allotted to pawns in order, and squad members'
// CarryEntries (#1540). A pawn whose short name another owned pawn
// shares (#1310 renames it) or that several policies carry waits.
// biomeDiseases is the colony map biome's disease hediffs (#1539).
// Each prisoner holds its own policy too (#1554), allowing no recreation,
// only the addiction maintenance and weaning its chemicals owe.
func DrugPolicyChanges(items ItemFacts, pawns []WorkPawn, prisoners []PrisonerFacts, names []OwnedName, policies []DrugPolicyEntry, stock domain.Fact[[]Amount], biomeDiseases []string, squad SoldierSquad) []DrugPolicyChange {
	diseaseBiome := DiseaseBiome(items, biomeDiseases)
	var remaining map[string]int64
	if rows, ok := stock.Value(); ok {
		remaining = map[string]int64{}
		for _, r := range rows {
			remaining[string(r.Resource)] += r.Count
		}
	}
	carry, hasCarry := CarryDrug(items, remaining)
	short, count := map[PawnID]string{}, map[string]int{}
	for _, n := range names {
		short[n.Pawn] = n.Short
		count[strings.ToLower(n.Short)]++
	}
	// owed is a policy holder; want builds its entries only once the holder
	// passes the name checks, because it draws on the shared drug stock.
	type owed struct {
		id     PawnID
		inputs PawnPolicyInputs
		want   func() ([]domain.DrugPolicyEntry, bool)
	}
	var rows []owed
	for _, pawn := range pawns {
		if inputs, ok := pawn.PolicyInputs.Value(); ok {
			rows = append(rows, owed{pawn.ID, inputs, func() ([]domain.DrugPolicyEntry, bool) {
				want, ok := DrugEntries(pawn, items, diseaseBiome)
				if !ok {
					return nil, false
				}
				if squad.IsSoldier(pawn.ID) && hasCarry {
					entries, _ := CarryEntries(pawn, items, carry)
					for _, e := range entries {
						want = mergeEntry(want, e)
					}
				}
				for _, e := range AddictionEntries(pawn, items, remaining) {
					want = mergeEntry(want, e)
				}
				return want, true
			}})
		}
	}
	for _, prisoner := range prisoners {
		if inputs, ok := prisoner.PolicyInputs.Value(); ok {
			rows = append(rows, owed{PawnID(prisoner.Pawn), inputs, func() ([]domain.DrugPolicyEntry, bool) {
				return append([]domain.DrugPolicyEntry{}, addictionEntries(inputs, items, remaining)...), true
			}})
		}
	}
	var out []DrugPolicyChange
	for _, row := range rows {
		inputs := row.inputs
		name := short[row.id]
		if inputs.DrugPolicy == "" || name == "" || count[strings.ToLower(name)] != 1 {
			continue
		}
		want, ok := row.want()
		if !ok {
			continue
		}
		v, err := domain.NewDrugPolicy(name, want)
		if err != nil {
			continue
		}
		var own []DrugPolicyEntry
		for _, p := range policies {
			if p.Label == name {
				own = append(own, p)
			}
		}
		if len(own) > 1 {
			continue
		}
		var change DrugPolicyChange
		if len(own) == 0 || !drugEntriesEqual(own[0].Entries, v.Entries()) {
			change.Write = &v
		}
		if len(own) == 0 || inputs.DrugPolicy != own[0].ID {
			s, err := domain.NewDrugPolicySetting(domain.PawnID(row.id), name)
			if err != nil {
				continue
			}
			change.Assign = &s
		}
		if change.Write != nil || change.Assign != nil {
			out = append(out, change)
		}
	}
	return out
}

// mergeEntry adds e to entries, or folds it into the entry for the same
// drug: allowed for whatever either allows, at the shorter schedule.
func mergeEntry(entries []domain.DrugPolicyEntry, e domain.DrugPolicyEntry) []domain.DrugPolicyEntry {
	for i := range entries {
		if x := &entries[i]; x.Drug == e.Drug {
			if e.Scheduled && (!x.Scheduled || e.DaysFrequency < x.DaysFrequency) {
				x.DaysFrequency = e.DaysFrequency
			}
			x.Joy, x.Addiction, x.Scheduled = x.Joy || e.Joy, x.Addiction || e.Addiction, x.Scheduled || e.Scheduled
			x.TakeToInventory = max(x.TakeToInventory, e.TakeToInventory)
			return entries
		}
	}
	return append(entries, e)
}

// drugEntriesEqual compares observed entries with canonical ones; the
// observed order is native's.
func drugEntriesEqual(observed, want []domain.DrugPolicyEntry) bool {
	got := slices.Clone(observed)
	slices.SortFunc(got, func(a, b domain.DrugPolicyEntry) int { return strings.Compare(a.Drug, b.Drug) })
	return slices.Equal(got, want)
}
