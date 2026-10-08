package bridge

import (
	"errors"
	"fmt"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// The containment planner's one catalog lookup (#1741): every def input of
// policy.ContainmentDefs, read from the generated def rows and the game's own
// stat values. A later swap to the planning views (#1731) replaces this file.

// containmentStat is StatDefOf.ContainmentStrength.
const containmentStat = "ContainmentStrength"

// maxHitPointsStat is StatDefOf.MaxHitPoints.
const maxHitPointsStat = "MaxHitPoints"

// ContainmentDefs reads the prediction's inputs for a cell shelled with the
// wall and door defs built from their stuff (empty for a def made of none).
// The holder is the platform def (a CompProperties_EntityHolderPlatform
// def) with the greatest containmentFactor, the first by name on a tie.
// Anything the defs do not carry is an error naming it.
func (catalog *DefinitionCatalog) ContainmentDefs(wall, wallStuff, door, doorStuff string) (policy.ContainmentDefs, error) {
	if catalog == nil {
		return policy.ContainmentDefs{}, errors.New("no definition catalog")
	}
	stat := DefRow[*d.StatDef](catalog, containmentStat)
	if stat == nil {
		return policy.ContainmentDefs{}, fmt.Errorf("the catalog has no %s stat def", containmentStat)
	}
	var holder *d.ThingDef
	var factor float32
	names := make([]string, 0, len(catalog.ThingDefs))
	for name := range catalog.ThingDefs {
		names = append(names, name)
	}
	sort.Strings(names)
	for _, name := range names {
		for _, comp := range catalog.ThingDefs[name].GetComps() {
			if p := comp.GetValue().GetCompProperties_EntityHolderPlatform(); p != nil && (holder == nil || p.GetContainmentFactor() > factor) {
				holder, factor = catalog.ThingDefs[name], p.GetContainmentFactor()
			}
		}
	}
	if holder == nil {
		return policy.ContainmentDefs{}, errors.New("the catalog has no holding platform def")
	}
	out := policy.ContainmentDefs{Holder: holder.GetDefName(), HolderFactor: float64(factor), HolderBase: float64(stat.GetDefaultBaseValue()), FloorStrength: float64(stat.GetDefaultBaseValue())}
	for _, m := range holder.GetStatBases() {
		if m.GetValue().GetStat() == containmentStat {
			out.HolderBase = float64(m.GetValue().GetValue())
		}
	}
	// The floor is the terrain whose stat table shows the greatest
	// ContainmentStrength above the plain floor's (#2435); a terrain the table
	// shows no such stat for adds none.
	terrains := make([]string, 0, len(catalog.TerrainDefs))
	for name := range catalog.TerrainDefs {
		terrains = append(terrains, name)
	}
	sort.Strings(terrains)
	for _, name := range terrains {
		if v, err := catalog.TerrainStatValue(name, containmentStat); err == nil && float64(v) > out.FloorStrength && (out.Floor.Def == "" || float64(v) > out.Floor.Strength) {
			out.Floor = policy.ContainmentFloor{Def: name, Strength: float64(v)}
		}
	}
	hitPoints := func(def, stuff string) (float64, error) {
		v, err := catalog.StatValue(def, stuff, maxHitPointsStat)
		switch {
		case err != nil:
			return 0, err
		case v <= 0:
			// An absent stat reads as 0 (#1782 turns it into an error).
			return 0, fmt.Errorf("%s has no %s", def, maxHitPointsStat)
		}
		return float64(v), nil
	}
	var err error
	if out.WallHP, err = hitPoints(wall, wallStuff); err != nil {
		return policy.ContainmentDefs{}, err
	}
	if out.DoorHP, err = hitPoints(door, doorStuff); err != nil {
		return policy.ContainmentDefs{}, err
	}
	var linkable []string
	for _, comp := range holder.GetComps() {
		if a := comp.GetValue().GetCompProperties_AffectedByFacilities(); a != nil {
			linkable = append(linkable, a.GetLinkableFacilities()...)
		}
	}
	for _, name := range linkable {
		def := catalog.ThingDefs[name]
		if def == nil {
			return policy.ContainmentDefs{}, fmt.Errorf("holder %s links facility %s, which the catalog lacks", out.Holder, name)
		}
		for _, comp := range def.GetComps() {
			f := comp.GetValue().GetCompProperties_Facility()
			if f == nil {
				continue
			}
			for _, m := range f.GetStatOffsets() {
				if m.GetValue().GetStat() == containmentStat {
					out.Facilities = append(out.Facilities, policy.ContainmentFacility{Def: name, Offset: float64(m.GetValue().GetValue()), MaxDistance: float64(f.GetMaxDistance()), MaxSimultaneous: int(f.GetMaxSimultaneous())})
				}
			}
		}
	}
	return out, nil
}
