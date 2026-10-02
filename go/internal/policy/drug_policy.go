package policy

import (
	"math"
	"slices"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// RecreationDrug is a social drug a pawn may take for joy and the chemical
// whose tolerance and addiction it builds.
type RecreationDrug struct {
	Def, Chemical string
}

// RecreationDrugs are the Core joy drugs a per-pawn policy may allow
// (#1537). All three are addictive.
var RecreationDrugs = []RecreationDrug{{"Beer", "Alcohol"}, {"SmokeleafJoint", "Smokeleaf"}, {"PsychiteTea", "Psychite"}}

// HighTolerance is the chemical tolerance severity at and above which a
// pawn stops taking that chemical for joy: the addiction risk is high.
const HighTolerance = 0.5

// PreventiveDiseases are the biome disease hediffs penoxycyline prevents.
var PreventiveDiseases = []string{"Malaria", "Plague"}

// Penoxycyline is the preventive drug and PenoxycylineDays its schedule:
// one dose protects for five days.
const (
	Penoxycyline     = "Penoxycyline"
	PenoxycylineDays = 5
)

// DependencyDrugs is the drug scheduled for each chemical a Biotech
// chemical-dependency gene needs (#1539).
var DependencyDrugs = map[string]string{
	"Alcohol": "Beer", "Smokeleaf": "SmokeleafJoint", "Psychite": "PsychiteTea",
	"GoJuice": "GoJuice", "WakeUp": "WakeUp", "Luciferium": "Luciferium",
}

// DependencyDays is the dependency drug's schedule: a gene's deficiency
// starts after five days without the chemical, so a dose every four days
// keeps it away.
const DependencyDays = 4

// DiseaseBiome reports whether the biome's diseases include one
// penoxycyline prevents.
func DiseaseBiome(diseases []string) bool {
	for _, d := range diseases {
		if slices.Contains(PreventiveDiseases, d) {
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
func DrugEntries(pawn WorkPawn, diseaseBiome bool) ([]domain.DrugPolicyEntry, bool) {
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
		entries = joyEntries(inputs)
	}
	schedule := func(def string, days float64) {
		entries = mergeEntry(entries, domain.DrugPolicyEntry{Drug: def, Scheduled: true, DaysFrequency: days, OnlyIfMoodBelow: 1, OnlyIfJoyBelow: 1})
	}
	if diseaseBiome {
		schedule(Penoxycyline, PenoxycylineDays)
	}
	for _, c := range inputs.DependencyChemicals {
		if def, ok := DependencyDrugs[c]; ok {
			schedule(def, DependencyDays)
		}
	}
	return entries, true
}

// joyEntries are the recreation drugs a pawn may take for joy: each unless
// it is addicted to or highly tolerant of the drug's chemical.
func joyEntries(inputs PawnPolicyInputs) []domain.DrugPolicyEntry {
	entries := []domain.DrugPolicyEntry{}
	for _, d := range RecreationDrugs {
		risky := false
		for _, c := range inputs.Chemicals {
			if c.Chemical != d.Chemical {
				continue
			}
			_, addicted := c.Addiction.Value()
			tolerance, tolerant := c.Tolerance.Value()
			risky = risky || addicted || c.Withdrawal || tolerant && tolerance >= HighTolerance
		}
		if !risky {
			entries = append(entries, joyEntry(d.Def))
		}
	}
	return entries
}

// AddictionDrug is a chemical a pawn can be addicted to, the drugs that
// satisfy it in preference order and the days between maintenance doses.
type AddictionDrug struct {
	Chemical       string
	Drugs          []string
	MaintainDays   float64
	AlwaysMaintain bool
}

// AddictionDrugs are the Core addictive chemicals (#1538). Each maintenance
// interval sits inside the chemical need's decay, so a scheduled dose lands
// before withdrawal; luciferium is never weaned.
var AddictionDrugs = []AddictionDrug{
	{Chemical: "Alcohol", Drugs: []string{"Beer"}, MaintainDays: 2},
	{Chemical: "Smokeleaf", Drugs: []string{"SmokeleafJoint"}, MaintainDays: 2},
	{Chemical: "Psychite", Drugs: []string{"PsychiteTea", "Yayo", "Flake"}, MaintainDays: 2},
	{Chemical: "GoJuice", Drugs: []string{"GoJuice"}, MaintainDays: 2},
	{Chemical: "WakeUp", Drugs: []string{"WakeUp"}, MaintainDays: 2},
	{Chemical: "Luciferium", Drugs: []string{"Luciferium"}, MaintainDays: 4, AlwaysMaintain: true},
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
func AddictionEntries(pawn WorkPawn, stock map[string]int64) []domain.DrugPolicyEntry {
	inputs, ok := pawn.PolicyInputs.Value()
	if !ok {
		return nil
	}
	var out []domain.DrugPolicyEntry
	for _, a := range AddictionDrugs {
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
// drug stock (#1538), allotted to pawns in order. A pawn whose short name another owned pawn
// shares (#1310 renames it) or that several policies carry waits.
// biomeDiseases is the colony map biome's disease hediffs (#1539).
func DrugPolicyChanges(pawns []WorkPawn, names []OwnedName, policies []DrugPolicyEntry, stock domain.Fact[[]Amount], biomeDiseases []string) []DrugPolicyChange {
	diseaseBiome := DiseaseBiome(biomeDiseases)
	var remaining map[string]int64
	if rows, ok := stock.Value(); ok {
		remaining = map[string]int64{}
		for _, r := range rows {
			remaining[string(r.Resource)] += r.Count
		}
	}
	short, count := map[PawnID]string{}, map[string]int{}
	for _, n := range names {
		short[n.Pawn] = n.Short
		count[strings.ToLower(n.Short)]++
	}
	var out []DrugPolicyChange
	for _, pawn := range pawns {
		inputs, ok := pawn.PolicyInputs.Value()
		name := short[pawn.ID]
		if !ok || inputs.DrugPolicy == "" || name == "" || count[strings.ToLower(name)] != 1 {
			continue
		}
		want, ok := DrugEntries(pawn, diseaseBiome)
		if !ok {
			continue
		}
		for _, e := range AddictionEntries(pawn, remaining) {
			want = mergeEntry(want, e)
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
			s, err := domain.NewDrugPolicySetting(domain.PawnID(pawn.ID), name)
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
