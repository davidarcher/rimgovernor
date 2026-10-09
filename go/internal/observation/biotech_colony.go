package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// BiotechColony is the Biotech colony section: pollution makers and
// removers, wastepack stacks, mech gestators and chargers, and baby care. An
// absent scalar is unknown, never zero; a Fact for the whole section is
// unknown without Biotech or when the read failed.
type BiotechColony struct {
	TotalPollution  domain.Fact[int32]
	PollutableCells domain.Fact[uint32]
	ClearAreaID     domain.Fact[string]
	ClearAreaCells  domain.Fact[int32]
	// PollutedCells and PollutedUncoveredCells count the polluted pollutable cells, and those outside the clear area.
	PollutedCells, PollutedUncoveredCells domain.Fact[uint32]
	Polluters                             []Polluter
	Wastepacks                            []Wastepack
	Atomizers                             []WastepackAtomizer
	Pumps                                 []PollutionPump
	Gestators                             []MechGestator
	Chargers                              []MechCharger
	Babies                                []BabyCare
	Breastfeeders                         []string
	// Gene-building rows. Genes are GeneDef defNames; the totals are the game's own GeneSet values.
	GeneBanks      []GeneBank
	GeneAssemblers []GeneAssembler
	GeneExtractors []GeneExtractor
	Genepacks      []Genepack
	Xenogerms      []Xenogerm
}

// GeneBank is a genepack container; PackIDs are the packs it holds.
type GeneBank struct {
	BuildingRow
	Powered, AutoLoad domain.Fact[bool]
	Capacity          domain.Fact[int32]
	PackIDs           []string
}

// GeneAssembler is a gene assembler. The run facts (progress, total work,
// archites owed, can-work verdict) are unknown while it is idle.
type GeneAssembler struct {
	BuildingRow
	Powered, Working, CanWorkNow domain.Fact[bool]
	Progress, TotalWork          domain.Fact[float64]
	ArchitesOwed, MaxComplexity  domain.Fact[int32]
	PackIDs, LinkedBankIDs       []string
}

// GeneExtractor is a gene extractor; TicksRemaining and PowerCutTicks are
// unknown while it is idle, the pawn ids unknown while none is set.
type GeneExtractor struct {
	BuildingRow
	Powered, Working              domain.Fact[bool]
	SelectedPawnID, OccupantID    domain.Fact[string]
	TicksRemaining, PowerCutTicks domain.Fact[int32]
}

// GeneTotals are a gene set's own complexity, metabolism and archite totals.
type GeneTotals struct{ Complexity, Metabolism, Archites domain.Fact[int32] }

// Genepack is a pack in a bank (BankID known, no position) or loose on the
// map (Position known).
type Genepack struct {
	ID, Definition string
	Genes          []string
	GeneTotals
	BankID                  domain.Fact[string]
	Position                domain.Fact[domain.Cell]
	Deteriorating, AutoLoad domain.Fact[bool]
	HitPoints               domain.Fact[int32]
}

// Xenogerm is a spawned xenogerm item; TargetPawnID is unknown with no pending implant.
type Xenogerm struct {
	BuildingRow
	Genes []string
	GeneTotals
	TargetPawnID domain.Fact[string]
	Forbidden    domain.Fact[bool]
	// ImplantMetabolism is the game's metabolism after implanting this
	// xenogerm, per pawn it may target, sorted by pawn id.
	ImplantMetabolism []ImplantMetabolism
}

// ImplantMetabolism is GeneUtility.MetabolismAfterImplanting for one pawn;
// the implant is refused below the biostat range minimum.
type ImplantMetabolism struct {
	PawnID     string
	Metabolism domain.Fact[int32]
}

// BuildingRow heads every building row: the thing id, definition and cell.
type BuildingRow struct {
	ID, Definition string
	Position       domain.Cell
}
type Polluter struct {
	BuildingRow
	Polluting   domain.Fact[bool]
	CellsPerDay domain.Fact[int32]
}
type Wastepack struct {
	BuildingRow
	Count                                                   int32
	Frozen, Outdoors, InAtomizer, CanDissolveNow, Forbidden domain.Fact[bool]
	DeteriorationRate                                       domain.Fact[int32]
}
type WastepackAtomizer struct {
	BuildingRow
	Powered, AutoLoad    domain.Fact[bool]
	SpaceLeft, TicksLeft domain.Fact[int32]
	FillPercent          domain.Fact[float64]
}
type PollutionPump struct {
	BuildingRow
	Powered, DisabledByArtificialBuildings domain.Fact[bool]
}

// MechGestator is a gestator and its active bill; the bill facts are unknown
// (not zero) without one.
type MechGestator struct {
	BuildingRow
	Powered                              domain.Fact[bool]
	BillID, State, MechKind, BoundPawnID domain.Fact[string]
	CyclesCompleted, WasteCount          domain.Fact[int32]
	BandwidthCost, FormingPercent        domain.Fact[float64]
}
type MechCharger struct {
	BuildingRow
	Powered, FullOfWaste domain.Fact[bool]
	ChargingMechID       domain.Fact[string]
	WasteCount           domain.Fact[int32]
}
type BabyCare struct {
	PawnID                                            string
	WantsSuckle, CanSuckleNow, BeingPlayedWith, InBed domain.Fact[bool]
	Autofeeders                                       []BabyAutofeeder
}
type BabyAutofeeder struct{ PawnID, Mode string }

func geneTotals(complexity, metabolism, archites *int32) GeneTotals {
	return GeneTotals{optional(complexity), optional(metabolism), optional(archites)}
}

func buildingRow(id, def *string, at *c.Cell) BuildingRow {
	return BuildingRow{ID: *id, Definition: *def, Position: domain.Cell{X: at.GetX(), Z: at.GetZ()}}
}

// colonyBiotech projects a validated section; an absent or unavailable one is
// an unknown fact.
func colonyBiotech(section *o.BiotechSection) domain.Fact[BiotechColony] {
	f := section.GetObserved()
	if f == nil {
		return domain.Fact[BiotechColony]{}
	}
	p := f.Pollution
	if p == nil {
		p = &o.PollutionTotals{}
	}
	r := BiotechColony{TotalPollution: optional(p.TotalPollution), PollutableCells: optional(p.PollutableCells),
		ClearAreaID: optional(p.ClearAreaId), ClearAreaCells: optional(p.ClearAreaCells), PollutedCells: optional(p.PollutedCells), PollutedUncoveredCells: optional(p.PollutedUncoveredCells), Breastfeeders: f.Breastfeeders}
	for _, x := range f.Polluters {
		r.Polluters = append(r.Polluters, Polluter{BuildingRow: buildingRow(x.ThingId, x.DefName, x.Position), Polluting: optional(x.Polluting), CellsPerDay: optional(x.CellsPerDay)})
	}
	for _, x := range f.Wastepacks {
		r.Wastepacks = append(r.Wastepacks, Wastepack{BuildingRow: buildingRow(x.ThingId, x.DefName, x.Position), Count: x.GetCount(), Frozen: optional(x.Frozen),
			Outdoors: optional(x.Outdoors), InAtomizer: optional(x.InAtomizer), CanDissolveNow: optional(x.CanDissolveNow), Forbidden: optional(x.Forbidden),
			DeteriorationRate: optional(x.DeteriorationRate)})
	}
	for _, x := range f.Atomizers {
		r.Atomizers = append(r.Atomizers, WastepackAtomizer{BuildingRow: buildingRow(x.ThingId, x.DefName, x.Position), Powered: optional(x.Powered), AutoLoad: optional(x.AutoLoad),
			SpaceLeft: optional(x.SpaceLeft), TicksLeft: optional(x.TicksLeft), FillPercent: optional(x.FillPercent)})
	}
	for _, x := range f.Pumps {
		r.Pumps = append(r.Pumps, PollutionPump{BuildingRow: buildingRow(x.ThingId, x.DefName, x.Position), Powered: optional(x.Powered),
			DisabledByArtificialBuildings: optional(x.DisabledByArtificialBuildings)})
	}
	for _, x := range f.Gestators {
		r.Gestators = append(r.Gestators, MechGestator{BuildingRow: buildingRow(x.ThingId, x.DefName, x.Position), Powered: optional(x.Powered), BillID: optional(x.BillId),
			State: optional(x.State), MechKind: optional(x.MechKind), BoundPawnID: optional(x.BoundPawnId), CyclesCompleted: optional(x.CyclesCompleted),
			WasteCount: optional(x.WasteCount), BandwidthCost: optional(x.BandwidthCost), FormingPercent: optional(x.FormingPercent)})
	}
	for _, x := range f.Chargers {
		r.Chargers = append(r.Chargers, MechCharger{BuildingRow: buildingRow(x.ThingId, x.DefName, x.Position), Powered: optional(x.Powered), FullOfWaste: optional(x.FullOfWaste),
			ChargingMechID: optional(x.ChargingMechId), WasteCount: optional(x.WasteCount)})
	}
	for _, x := range f.Babies {
		b := BabyCare{PawnID: x.GetPawnId(), WantsSuckle: optional(x.WantsSuckle), CanSuckleNow: optional(x.CanSuckleNow), BeingPlayedWith: optional(x.BeingPlayedWith), InBed: optional(x.InBed)}
		for _, a := range x.Autofeeders {
			b.Autofeeders = append(b.Autofeeders, BabyAutofeeder{PawnID: a.GetPawnId(), Mode: a.GetMode()})
		}
		r.Babies = append(r.Babies, b)
	}
	for _, x := range f.GeneBanks {
		r.GeneBanks = append(r.GeneBanks, GeneBank{BuildingRow: buildingRow(x.ThingId, x.DefName, x.Position), Powered: optional(x.Powered), AutoLoad: optional(x.AutoLoad),
			Capacity: optional(x.Capacity), PackIDs: x.PackIds})
	}
	for _, x := range f.GeneAssemblers {
		r.GeneAssemblers = append(r.GeneAssemblers, GeneAssembler{BuildingRow: buildingRow(x.ThingId, x.DefName, x.Position), Powered: optional(x.Powered), Working: optional(x.Working),
			CanWorkNow: optional(x.CanWorkNow), Progress: optional(x.Progress), TotalWork: optional(x.TotalWork), ArchitesOwed: optional(x.ArchitesOwed),
			MaxComplexity: optional(x.MaxComplexity), PackIDs: x.PackIds, LinkedBankIDs: x.LinkedBankIds})
	}
	for _, x := range f.GeneExtractors {
		r.GeneExtractors = append(r.GeneExtractors, GeneExtractor{BuildingRow: buildingRow(x.ThingId, x.DefName, x.Position), Powered: optional(x.Powered), Working: optional(x.Working),
			SelectedPawnID: optional(x.SelectedPawnId), OccupantID: optional(x.OccupantId), TicksRemaining: optional(x.TicksRemaining), PowerCutTicks: optional(x.PowerCutTicks)})
	}
	for _, x := range f.Genepacks {
		p := Genepack{ID: x.GetThingId(), Definition: x.GetDefName(), Genes: x.Genes, GeneTotals: geneTotals(x.Complexity, x.Metabolism, x.Archites),
			BankID: optional(x.BankId), Deteriorating: optional(x.Deteriorating), AutoLoad: optional(x.AutoLoad), HitPoints: optional(x.HitPoints)}
		if x.Position != nil {
			p.Position = domain.Known(domain.Cell{X: x.Position.GetX(), Z: x.Position.GetZ()})
		}
		r.Genepacks = append(r.Genepacks, p)
	}
	for _, x := range f.Xenogerms {
		g := Xenogerm{BuildingRow: buildingRow(x.ThingId, x.DefName, x.Position), Genes: x.Genes,
			GeneTotals: geneTotals(x.Complexity, x.Metabolism, x.Archites), TargetPawnID: optional(x.TargetPawnId), Forbidden: optional(x.Forbidden)}
		for _, m := range x.ImplantMetabolism {
			g.ImplantMetabolism = append(g.ImplantMetabolism, ImplantMetabolism{PawnID: m.GetPawnId(), Metabolism: optional(m.MetabolismAfter)})
		}
		r.Xenogerms = append(r.Xenogerms, g)
	}
	return domain.Known(r)
}

// MechChargerRows are the section's chargers as the recharge policy reads
// them: charging is a mech attached.
func (b BiotechColony) MechChargerRows() []policy.MechCharger {
	out := make([]policy.MechCharger, 0, len(b.Chargers))
	for _, c := range b.Chargers {
		// Native sets charging_mech_id exactly while a mech is attached.
		_, charging := c.ChargingMechID.Value()
		row := policy.MechCharger{Powered: c.Powered, FullOfWaste: c.FullOfWaste, Charging: domain.Known(charging)}
		out = append(out, row)
	}
	return out
}

// mechChargerFact is the section's chargers as a fact, unknown with the
// section.
func mechChargerFact(section domain.Fact[BiotechColony]) domain.Fact[[]policy.MechCharger] {
	b, known := section.Value()
	if !known {
		return domain.Unknown[[]policy.MechCharger]()
	}
	return domain.Known(b.MechChargerRows())
}

// MechChargerDefs are the catalog definitions that are mech chargers
// (PlanningDefinition.MechCharger, from the game's Building_MechCharger
// class), in catalog order; the build planner picks among them.
func MechChargerDefs(defs []PlanningDefinition) []PlanningDefinition {
	var out []PlanningDefinition
	for _, d := range defs {
		if charger, ok := d.MechCharger.Value(); ok && charger {
			out = append(out, d)
		}
	}
	return out
}

// GeneBankFacts are the section's banks and packs as MaintainGeneBank reads
// them: a pack is loose when it has a map position, held when a bank
// names it, unknown when it reports neither.
func (b BiotechColony) GeneBankFacts() policy.GeneBankFacts {
	var out policy.GeneBankFacts
	for _, bank := range b.GeneBanks {
		out.Banks = append(out.Banks, policy.GeneBankSlot{Capacity: bank.Capacity, Held: int32(len(bank.PackIDs))})
	}
	for _, pack := range b.Genepacks {
		_, loose := pack.Position.Value()
		_, held := pack.BankID.Value()
		switch {
		case loose:
			out.Packs = append(out.Packs, domain.Known(true))
		case held:
			out.Packs = append(out.Packs, domain.Known(false))
		default:
			out.Packs = append(out.Packs, domain.Unknown[bool]())
		}
	}
	return out
}

// geneBankFact is the section's gene-building rows as a fact, unknown with
// the section.
func geneBankFact(section domain.Fact[BiotechColony]) domain.Fact[policy.GeneBankFacts] {
	b, known := section.Value()
	if !known {
		return domain.Unknown[policy.GeneBankFacts]()
	}
	return domain.Known(b.GeneBankFacts())
}
