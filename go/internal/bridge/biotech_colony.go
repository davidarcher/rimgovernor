package bridge

import (
	"math"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// validateBiotechColony checks the Biotech colony section: every row
// names a unique thing on the map, counts are nonnegative and a gestator's
// bill fields agree. Absent scalars stay unknown.
func validateBiotechColony(v *o.ColonyFactsSnapshot) error {
	if v.Biotech == nil {
		return nil
	}
	switch s := v.Biotech.Outcome.(type) {
	case *o.BiotechSection_Unavailable:
		return validateUnavailable(s.Unavailable)
	case *o.BiotechSection_Observed:
		f := s.Observed
		if f == nil {
			return contract("incomplete biotech colony facts")
		}
		if p := f.Pollution; p != nil {
			if p.TotalPollution != nil && p.GetTotalPollution() < 0 || p.ClearAreaCells != nil && p.GetClearAreaCells() < 0 ||
				p.ClearAreaId != nil && validID(p.GetClearAreaId()) != nil {
				return contract("invalid pollution totals")
			}
			if p.PollutedUncoveredCells != nil && (p.PollutedCells == nil || p.GetPollutedUncoveredCells() > p.GetPollutedCells()) ||
				p.PollutedCells != nil && p.PollutableCells != nil && p.GetPollutedCells() > p.GetPollutableCells() {
				return contract("invalid polluted cell counts")
			}
		}
		seen := map[string]bool{}
		head := func(id, def *string, at *c.Cell) bool {
			if id == nil || def == nil || validID(*id) != nil || validID(*def) != nil || !colonyCell(at, v.MapSize) {
				return false
			}
			return true
		}
		// A thing id is unique within its own table: a building may carry
		// several comps (a polluter that is also a pump), so tables do not
		// share one set.
		unique := func(table string, id string) bool {
			key := table + "/" + id
			if seen[key] {
				return false
			}
			seen[key] = true
			return true
		}
		for _, r := range f.Polluters {
			if r == nil || !head(r.ThingId, r.DefName, r.Position) || !unique("polluter", r.GetThingId()) || r.CellsPerDay != nil && r.GetCellsPerDay() < 0 {
				return contract("invalid biotech polluter")
			}
		}
		for _, r := range f.Wastepacks {
			if r == nil || !head(r.ThingId, r.DefName, r.Position) || !unique("wastepack", r.GetThingId()) || r.Count == nil || r.GetCount() <= 0 {
				return contract("invalid wastepack")
			}
		}
		for _, r := range f.Atomizers {
			if r == nil || !head(r.ThingId, r.DefName, r.Position) || !unique("atomizer", r.GetThingId()) || r.SpaceLeft != nil && r.GetSpaceLeft() < 0 || r.TicksLeft != nil && r.GetTicksLeft() < 0 ||
				r.FillPercent != nil && (math.IsNaN(r.GetFillPercent()) || r.GetFillPercent() < 0 || r.GetFillPercent() > 1) {
				return contract("invalid wastepack atomizer")
			}
		}
		for _, r := range f.Pumps {
			if r == nil || !head(r.ThingId, r.DefName, r.Position) || !unique("pump", r.GetThingId()) {
				return contract("invalid pollution pump")
			}
		}
		for _, r := range f.Gestators {
			if r == nil || !head(r.ThingId, r.DefName, r.Position) || !unique("gestator", r.GetThingId()) || r.WasteCount != nil && r.GetWasteCount() < 0 || r.CyclesCompleted != nil && r.GetCyclesCompleted() < 0 ||
				r.BandwidthCost != nil && (math.IsNaN(r.GetBandwidthCost()) || math.IsInf(r.GetBandwidthCost(), 0) || r.GetBandwidthCost() < 0) ||
				r.FormingPercent != nil && (math.IsNaN(r.GetFormingPercent()) || r.GetFormingPercent() < 0 || r.GetFormingPercent() > 1) {
				return contract("invalid mech gestator")
			}
			for _, id := range []*string{r.BillId, r.State, r.MechKind, r.BoundPawnId} {
				if id != nil && validID(*id) != nil {
					return contract("invalid mech gestator bill")
				}
			}
			if r.BillId == nil && (r.State != nil || r.MechKind != nil || r.BoundPawnId != nil || r.CyclesCompleted != nil) {
				return contract("gestator bill fields without a bill")
			}
		}
		for _, r := range f.Chargers {
			if r == nil || !head(r.ThingId, r.DefName, r.Position) || !unique("charger", r.GetThingId()) || r.WasteCount != nil && r.GetWasteCount() < 0 ||
				r.ChargingMechId != nil && validID(r.GetChargingMechId()) != nil {
				return contract("invalid mech charger")
			}
		}
		babies := map[string]bool{}
		for _, r := range f.Babies {
			if r == nil || r.PawnId == nil || validID(r.GetPawnId()) != nil || babies[r.GetPawnId()] {
				return contract("invalid baby care row")
			}
			babies[r.GetPawnId()] = true
			feeders := map[string]bool{}
			for _, a := range r.Autofeeders {
				if a == nil || validID(a.GetPawnId()) != nil || validID(a.GetMode()) != nil || feeders[a.GetPawnId()] {
					return contract("invalid baby autofeeder")
				}
				feeders[a.GetPawnId()] = true
			}
		}
		feeders := map[string]bool{}
		for _, id := range f.Breastfeeders {
			if validID(id) != nil || feeders[id] {
				return contract("invalid breastfeeder")
			}
			feeders[id] = true
		}
		return validateGeneBuilding(f, head, unique)
	default:
		return contract("missing biotech colony outcome")
	}
}

// validateGeneBuilding checks the gene-building rows: unique ids per
// table, nonnegative counts and ticks, distinct valid gene and pack ids, and a
// genepack that is in exactly one place (a listed bank, or a cell on the map).
func validateGeneBuilding(f *o.BiotechColonyFacts, head func(id, def *string, at *c.Cell) bool, unique func(table, id string) bool) error {
	ids := func(list []string) bool {
		seen := map[string]bool{}
		for _, id := range list {
			if validID(id) != nil || seen[id] {
				return false
			}
			seen[id] = true
		}
		return true
	}
	nonneg := func(v *int32) bool { return v == nil || *v >= 0 }
	finite := func(v *float64) bool { return v == nil || !math.IsNaN(*v) && !math.IsInf(*v, 0) && *v >= 0 }
	banks := map[string]bool{}
	for _, r := range f.GeneBanks {
		if r == nil || !head(r.ThingId, r.DefName, r.Position) || !unique("gene_bank", r.GetThingId()) || !nonneg(r.Capacity) || !ids(r.PackIds) ||
			r.Capacity != nil && len(r.PackIds) > int(r.GetCapacity()) {
			return contract("invalid gene bank")
		}
		banks[r.GetThingId()] = true
	}
	for _, r := range f.GeneAssemblers {
		if r == nil || !head(r.ThingId, r.DefName, r.Position) || !unique("gene_assembler", r.GetThingId()) || !nonneg(r.ArchitesOwed) || !nonneg(r.MaxComplexity) ||
			!finite(r.Progress) || !finite(r.TotalWork) || !ids(r.PackIds) || !ids(r.LinkedBankIds) {
			return contract("invalid gene assembler")
		}
	}
	for _, r := range f.GeneExtractors {
		if r == nil || !head(r.ThingId, r.DefName, r.Position) || !unique("gene_extractor", r.GetThingId()) || !nonneg(r.TicksRemaining) || !nonneg(r.PowerCutTicks) ||
			r.SelectedPawnId != nil && validID(r.GetSelectedPawnId()) != nil || r.OccupantId != nil && validID(r.GetOccupantId()) != nil {
			return contract("invalid gene extractor")
		}
	}
	for _, r := range f.Genepacks {
		if r == nil || r.ThingId == nil || r.DefName == nil || validID(r.GetThingId()) != nil || validID(r.GetDefName()) != nil || !unique("genepack", r.GetThingId()) ||
			!ids(r.Genes) || !nonneg(r.HitPoints) || !nonneg(r.Complexity) || !nonneg(r.Archites) {
			return contract("invalid genepack")
		}
		if (r.BankId == nil) == (r.Position == nil) || r.BankId != nil && !banks[r.GetBankId()] {
			return contract("genepack is not in exactly one place")
		}
	}
	for _, r := range f.Xenogerms {
		if r == nil || !head(r.ThingId, r.DefName, r.Position) || !unique("xenogerm", r.GetThingId()) || !ids(r.Genes) || !nonneg(r.Complexity) || !nonneg(r.Archites) ||
			r.TargetPawnId != nil && validID(r.GetTargetPawnId()) != nil {
			return contract("invalid xenogerm")
		}
		pawns := map[string]bool{}
		for _, m := range r.ImplantMetabolism {
			if m == nil || m.PawnId == nil || m.MetabolismAfter == nil || validID(m.GetPawnId()) != nil || pawns[m.GetPawnId()] {
				return contract("invalid xenogerm implant metabolism")
			}
			pawns[m.GetPawnId()] = true
		}
	}
	return nil
}
