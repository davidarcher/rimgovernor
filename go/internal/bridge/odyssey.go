package bridge

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// OdysseyCatalog is one load's Odyssey defs (#1708) by name, the native rows
// as read: hazards, animals and hack rules are the game defs' own, never Go
// name lists. Nil without Odyssey. Animal kinds name PawnKindDefs; the races
// are AnimalRaceCatalog's.
type OdysseyCatalog struct {
	Biomes         map[string]*o.BiomeRow
	TileMutators   map[string]*o.TileMutatorRow
	Hackables      map[string]*o.HackableRow
	Portals        map[string]*o.PortalRow
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
	if out.Biomes, err = biotechIndex("odyssey biome", v.Biomes, (*o.BiomeRow).GetDefName); err != nil {
		return nil, err
	}
	if out.TileMutators, err = biotechIndex("odyssey tile mutator", v.TileMutators, (*o.TileMutatorRow).GetDefName); err != nil {
		return nil, err
	}
	if out.Hackables, err = biotechIndex("odyssey hackable", v.Hackables, (*o.HackableRow).GetDefName); err != nil {
		return nil, err
	}
	if out.Portals, err = biotechIndex("odyssey portal", v.Portals, (*o.PortalRow).GetDefName); err != nil {
		return nil, err
	}
	if out.StockpileTypes, err = biotechIndex("odyssey stockpile type", v.StockpileTypes, (*o.StockpileTypeRow).GetName); err != nil {
		return nil, err
	}
	for _, row := range v.Biomes {
		if err := biotechIDs("odyssey biome", row.MapConditions); err != nil {
			return nil, err
		}
		if err := odysseyNumbers("biome", row.AnimalDensity, row.PlantDensity, row.DiseaseMtbDays, row.Forageability, row.MovementDifficulty,
			row.GeyserCountFactor, row.WildAnimalScariaChance, row.PollutionOffset, row.ConstantOutdoorTemperatureC); err != nil {
			return nil, err
		}
		for kind, rows := range map[string][]*o.BiomeAnimal{"wild": row.WildAnimals, "pollution": row.PollutionWildAnimals, "coastal": row.CoastalWildAnimals} {
			if err := odysseyAnimals(kind, rows); err != nil {
				return nil, err
			}
		}
		seen := map[string]bool{}
		for _, d := range row.Diseases {
			if d == nil || validID(d.GetIncident()) != nil || seen[d.GetIncident()] || d.Commonality == nil || !(d.GetCommonality() > 0) || math.IsInf(d.GetCommonality(), 0) {
				return nil, contract("invalid or duplicate odyssey biome disease")
			}
			seen[d.GetIncident()] = true
		}
	}
	for _, row := range v.TileMutators {
		if err := biotechIDs("odyssey tile mutator", row.Categories, row.AdditionalGameConditions, row.BiomeWhitelist, row.BiomeBlacklist); err != nil {
			return nil, err
		}
		for _, biome := range append(append([]string{}, row.BiomeWhitelist...), row.BiomeBlacklist...) {
			if out.Biomes[biome] == nil {
				return nil, contract("odyssey tile mutator %s names unknown biome %s", row.GetDefName(), biome)
			}
		}
		if err := odysseyNumbers("tile mutator", row.AnimalDensityFactor, row.PlantDensityFactor, row.GeyserCountFactor, row.FishPopulationFactor); err != nil {
			return nil, err
		}
	}
	for _, row := range v.Hackables {
		if row.CompletedQuest != nil && validID(row.GetCompletedQuest()) != nil || row.Comp != nil && validID(row.GetComp()) != nil {
			return nil, contract("invalid odyssey hackable name")
		}
		if err := odysseyNumbers("hackable", row.Defence); err != nil {
			return nil, err
		}
		if row.Defence != nil && row.GetDefence() < 0 || row.IntellectualSkillPrerequisite != nil && row.GetIntellectualSkillPrerequisite() < 0 ||
			row.LockoutHoursMin != nil && row.GetLockoutHoursMin() < 0 || row.LockoutHoursMax != nil && row.LockoutHoursMin != nil && row.GetLockoutHoursMax() < row.GetLockoutHoursMin() {
			return nil, contract("invalid odyssey hackable number")
		}
	}
	for _, row := range v.Portals {
		for _, id := range []*string{row.PocketMapGenerator, row.ExitDef} {
			if id != nil && validID(*id) != nil {
				return nil, contract("invalid odyssey portal name")
			}
		}
		if err := biotechIDs("odyssey portal", row.PocketTileMutators); err != nil {
			return nil, err
		}
		for _, m := range row.PocketTileMutators {
			if out.TileMutators[m] == nil {
				return nil, contract("odyssey portal %s names unknown tile mutator %s", row.GetDefName(), m)
			}
		}
		if row.PocketMapSize != nil && row.GetPocketMapSize() < 0 {
			return nil, contract("negative odyssey pocket map size")
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
