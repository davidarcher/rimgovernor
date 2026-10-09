package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

var surgeryKinds = map[o.SurgeryKind]policy.SurgeryKind{
	o.SurgeryKind_SURGERY_KIND_RESTORE:  policy.SurgeryRestore,
	o.SurgeryKind_SURGERY_KIND_CURE:     policy.SurgeryCure,
	o.SurgeryKind_SURGERY_KIND_AMPUTATE: policy.SurgeryAmputate,
	o.SurgeryKind_SURGERY_KIND_INSTALL:  policy.SurgeryInstall,
	o.SurgeryKind_SURGERY_KIND_HARVEST:  policy.SurgeryHarvest,
	o.SurgeryKind_SURGERY_KIND_OTHER:    policy.SurgeryOther,
}

// SurgeryFacts maps the native surgery facts as observed, for a
// colonist's care read and a prisoner's population row. A list
// carrying a read issue is unknown; values are never recomputed here.
//
// Each operation carries the part item its recipe installs, read from the
// catalog's recipe rows; a recipe the catalog has no row for is an error.
func SurgeryFacts(h *o.PawnHealth, catalog *DefinitionCatalog) (domain.Fact[[]policy.MissingPart], domain.Fact[[]policy.SurgeryOperation], error) {
	parts, ops := domain.Unknown[[]policy.MissingPart](), domain.Unknown[[]policy.SurgeryOperation]()
	if !surgeryIssue(h.Issues, "missing_parts") {
		rows := make([]policy.MissingPart, 0, len(h.MissingParts))
		for _, m := range h.MissingParts {
			if m != nil {
				rows = append(rows, policy.MissingPart{
					PartIndex: surgeryInt(m.PartIndex), ParentIndex: surgeryInt(m.ParentIndex),
					PartDefName: surgeryFact(m.PartDefName), ParentDefName: surgeryFact(m.ParentDefName), Vital: surgeryFact(m.Vital),
				})
			}
		}
		parts = domain.Known(rows)
	}
	if !surgeryIssue(h.Issues, "operations") {
		rows := make([]policy.SurgeryOperation, 0, len(h.Operations))
		for _, op := range h.Operations {
			if op == nil {
				continue
			}
			row := policy.SurgeryOperation{
				PartIndex: surgeryInt(op.PartIndex), PartDefName: surgeryFact(op.PartDefName), Kind: surgeryKinds[op.GetKind()],
				SuccessChance: surgeryFact(op.SuccessChance), DoctorSuccessChance: surgeryFact(op.DoctorSuccessChance), IngredientsOnMap: surgeryFact(op.IngredientsOnMap),
				Violation: surgeryFact(op.Violation), Lethal: surgeryFact(op.Lethal), YieldValue: surgeryFact(op.YieldMarketValue),
				AddedPart: surgeryFact(op.AddedPartHediff), YieldThing: surgeryFact(op.YieldThingDef), MedicineValue: surgeryFact(op.MedicineMarketValue),
				CareLimited: surgeryFact(op.MedicineCareLimited),
			}
			if op.Recipe != nil {
				row.Recipe = surgeryFact(op.Recipe.DefName)
				if op.Recipe.DefName != nil {
					item, err := catalog.InstallItem(op.Recipe.GetDefName())
					if err != nil {
						return parts, ops, err
					}
					row.Item = item
				}
			}
			if op.EligibleDoctors != nil {
				row.EligibleDoctors = domain.Known(int(op.GetEligibleDoctors()))
			}
			for doctor, chance := range op.GetDoctorChances() {
				if row.DoctorChances == nil {
					row.DoctorChances = map[domain.PawnID]float64{}
				}
				row.DoctorChances[domain.PawnID(doctor)] = chance
			}
			rows = append(rows, row)
		}
		ops = domain.Known(rows)
	}
	return parts, ops, nil
}

// QueuedSurgeryItems lists the part item each queued medical bill installs,
// by the catalog's recipe rows; a bill that installs no item adds none.
func QueuedSurgeryItems(recipes []string, catalog *DefinitionCatalog) ([]policy.Resource, error) {
	var items []policy.Resource
	for _, recipe := range recipes {
		item, err := catalog.InstallItem(recipe)
		if err != nil {
			return nil, err
		}
		if item != "" {
			items = append(items, item)
		}
	}
	return items, nil
}

// QueuedSurgeries counts the medical bills on the patient; unknown when the
// bill stack carried a read issue.
func QueuedSurgeries(h *o.PawnHealth) domain.Fact[int] {
	if surgeryIssue(h.Issues, "surgery_bills") {
		return domain.Unknown[int]()
	}
	return domain.Known(len(h.SurgeryBills))
}

// QueuedSurgeryRecipes lists each medical bill's recipe on the patient;
// nil when the bill stack carried a read issue.
func QueuedSurgeryRecipes(h *o.PawnHealth) []string {
	if surgeryIssue(h.Issues, "surgery_bills") {
		return nil
	}
	var recipes []string
	for _, bill := range h.SurgeryBills {
		if recipe := bill.GetRecipe(); recipe != "" {
			recipes = append(recipes, recipe)
		}
	}
	return recipes
}

func surgeryFact[T any](p *T) domain.Fact[T] {
	if p == nil {
		return domain.Unknown[T]()
	}
	return domain.Known(*p)
}

func surgeryInt(p *int32) domain.Fact[int] {
	if p == nil {
		return domain.Unknown[int]()
	}
	return domain.Known(int(*p))
}

func surgeryIssue(issues []*o.ReadIssue, field string) bool {
	for _, issue := range issues {
		if issue.GetField() == field {
			return true
		}
	}
	return false
}

// InstalledParts maps the native installed added parts. A read issue
// on installed_parts is unknown, never an empty list. The tier comes from the
// hediff def name; the priced item is the hediff's spawnThingOnRemoved, unknown
// when the part spawns nothing.
func InstalledParts(h *o.PawnHealth) domain.Fact[[]policy.InstalledPart] {
	if surgeryIssue(h.Issues, "installed_parts") {
		return domain.Unknown[[]policy.InstalledPart]()
	}
	rows := make([]policy.InstalledPart, 0, len(h.InstalledParts))
	for _, p := range h.InstalledParts {
		if p == nil || p.GetDefinition().GetDefName() == "" {
			continue
		}
		row := policy.InstalledPart{
			Hediff: p.GetDefinition().GetDefName(), Part: surgeryFact(p.PartDefName), PartIndex: surgeryInt(p.PartIndex),
			Tier: policy.PartTier(p.GetDefinition().GetDefName()),
		}
		if p.SpawnThingDefName != nil {
			row.Item = domain.Known(policy.Resource(p.GetSpawnThingDefName()))
		}
		rows = append(rows, row)
	}
	return domain.Known(rows)
}
