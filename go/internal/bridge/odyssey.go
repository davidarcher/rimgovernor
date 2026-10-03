package bridge

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// OdysseyCatalog is the Odyssey facts the def mirror cannot give (#1708,
// #1791): each biome's wild animal tables by BiomeDef name, and the stockpile
// types. Biome, tile mutator, hackable and portal defs are the mirror's
// own rows. Nil without Odyssey. Animal kinds name PawnKindDefs; the races
// are the catalog's race rows (AnimalRaces).
type OdysseyCatalog struct {
	BiomeAnimals   map[string]*o.BiomeAnimals
	StockpileTypes map[string]*o.StockpileTypeRow
}

func odysseyNumbers(kind string, values ...*float64) error {
	for _, v := range values {
		if v != nil && (math.IsNaN(*v) || math.IsInf(*v, 0)) {
			return contract("nonfinite odyssey %s number", kind)
		}
	}
	return nil
}

func odysseyAnimals(kind string, rows []*o.BiomeAnimal) error {
	seen := map[string]bool{}
	for _, a := range rows {
		if a == nil || validID(a.GetKind()) != nil || seen[a.GetKind()] || a.Commonality == nil || math.IsNaN(a.GetCommonality()) || math.IsInf(a.GetCommonality(), 0) || a.GetCommonality() <= 0 {
			return contract("invalid or duplicate odyssey biome %s animal", kind)
		}
		seen[a.GetKind()] = true
	}
	return nil
}

// DecodeOdysseyCatalog validates the catalog's Odyssey section; nil in, nil
// out (the game has no Odyssey).
func DecodeOdysseyCatalog(v *o.OdysseyCatalog) (*OdysseyCatalog, error) {
	if v == nil {
		return nil, nil
	}
	out := &OdysseyCatalog{}
	var err error
	if out.BiomeAnimals, err = catalogIndex("odyssey biome animals", v.BiomeAnimals, (*o.BiomeAnimals).GetBiome); err != nil {
		return nil, err
	}
	if out.StockpileTypes, err = catalogIndex("odyssey stockpile type", v.StockpileTypes, (*o.StockpileTypeRow).GetName); err != nil {
		return nil, err
	}
	for _, row := range v.BiomeAnimals {
		for kind, rows := range map[string][]*o.BiomeAnimal{"wild": row.WildAnimals, "pollution": row.PollutionWildAnimals, "coastal": row.CoastalWildAnimals} {
			if err := odysseyAnimals(kind, rows); err != nil {
				return nil, err
			}
		}
	}
	return out, nil
}

// validateBuildingOdyssey bounds a building row's Odyssey block (#1708).
func validateBuildingOdyssey(b *o.OdysseyBuilding) error {
	if b == nil {
		return nil
	}
	if h := b.Hackable; h != nil {
		if err := odysseyNumbers("hackable", h.ProgressPercent, h.Defence); err != nil {
			return err
		}
		if h.ProgressPercent != nil && (h.GetProgressPercent() < 0 || h.GetProgressPercent() > 1) || h.Defence != nil && h.GetDefence() < 0 {
			return contract("odyssey hack number out of range")
		}
	}
	if p := b.Portal; p != nil {
		for _, id := range []*string{p.StockpileType, p.Layout} {
			if id != nil && validID(*id) != nil {
				return contract("invalid odyssey portal name")
			}
		}
		if p.PocketMapId != nil && (p.PocketMapExists == nil || !p.GetPocketMapExists() || p.GetPocketMapId() < 0) {
			return contract("odyssey pocket map id without a pocket map")
		}
	}
	return pawnsIssues(b.Issues, b.ProtoReflect())
}

// BuildingHack lifts a building row's hack block; unknown when the row
// carries none (not hackable, or the read failed: the row's issues say).
func BuildingHack(row *o.BuildingState) domain.Fact[policy.Hack] {
	h := row.GetOdyssey().GetHackable()
	if h == nil {
		return domain.Unknown[policy.Hack]()
	}
	return domain.Known(policy.Hack{ProgressPercent: optionalFact(h.ProgressPercent), Hacked: optionalFact(h.Hacked),
		LockedOut: optionalFact(h.LockedOut), Autohack: optionalFact(h.Autohack)})
}

// BuildingPortal lifts a building row's portal block; unknown when the row
// carries none.
func BuildingPortal(row *o.BuildingState) domain.Fact[policy.Portal] {
	p := row.GetOdyssey().GetPortal()
	if p == nil {
		return domain.Unknown[policy.Portal]()
	}
	out := policy.Portal{PocketMapExists: optionalFact(p.PocketMapExists), StockpileType: optionalFact(p.StockpileType), Layout: optionalFact(p.Layout)}
	if p.PocketMapId != nil {
		out.PocketMapID = domain.Known(int(p.GetPocketMapId()))
	} else {
		out.PocketMapID = domain.Unknown[int]()
	}
	return domain.Known(out)
}
