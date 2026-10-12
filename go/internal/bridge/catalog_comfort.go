package bridge

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// comfortModel is the comfort pricing's catalog input (#2975): the NeedDef
// curves of comfort and beauty, the chairs a pawn can sit on and the beds an
// adult sleeps in, each at every stuff the game allows, with the comfort stat it
// shows at Normal quality and the market value of its adjusted costs. A piece
// whose comfort the game does not show is left out. The bed offset is the
// comfort the end table and dresser every bedroom template places add
// (CompProperties_Facility's stat offsets). A catalog without the NeedDef rows
// has an unknown curve, not an error; the pricing then stays off.
//
// A throne is sittable but a room role's building, never a work chair, and is
// left out of the chairs.
func (catalog *DefinitionCatalog) comfortModel(furniture policy.RoomFurniture) (policy.ComfortModel, error) {
	game := policy.SeekerGame()
	var out policy.ComfortModel
	curve := func(name string) policy.NeedCurve {
		row := DefRow[*d.NeedDef](catalog, name)
		if row == nil {
			return policy.NeedCurve{}
		}
		return policy.NeedCurve{Base: float32Number(row.GetBaseLevel()), RisePerHour: float32Number(row.GetSeekerRisePerHour()), FallPerHour: float32Number(row.GetSeekerFallPerHour()), FreezeAsleep: row.GetFreezeWhileSleeping()}
	}
	out.Comfort, out.Beauty = curve(game.Comfort.Need), curve(game.Beauty.Need)

	names := make([]string, 0, len(catalog.ThingDefs))
	for name := range catalog.ThingDefs {
		names = append(names, name)
	}
	sort.Strings(names)
	for _, name := range names {
		row := catalog.ThingDefs[name]
		if !Buildable(row) || !row.GetBuilding().GetIsSittable() {
			continue
		}
		if throne, err := catalog.ClassIsA(row.GetThingClass(), classThrone); err != nil || throne {
			if err != nil {
				return out, err
			}
			continue
		}
		options, err := catalog.comfortOptions(name)
		if err != nil {
			return out, err
		}
		out.Chairs = append(out.Chairs, options...)
	}

	beds, err := catalog.bedRanks()
	if err != nil {
		return out, err
	}
	for _, b := range beds.sleeping {
		if b.slots != 1 {
			continue
		}
		options, err := catalog.comfortOptions(b.def)
		if err != nil {
			return out, err
		}
		out.Beds = append(out.Beds, options...)
	}

	facilities, err := catalog.facilityRows()
	if err != nil {
		return out, err
	}
	for _, link := range []policy.FacilityLink{furniture.EndTable, furniture.Dresser} {
		out.BedOffset += float64(facilities[link.Def].offsets[StatComfort])
	}
	return out, nil
}

// comfortOptions are a def's pieces, one per allowed stuff (one for a def with
// none): the comfort stat the game shows and the cost's market value.
func (catalog *DefinitionCatalog) comfortOptions(def string) ([]policy.ComfortOption, error) {
	stuffs, err := catalog.AllowedStuffs(def)
	if err != nil {
		return nil, err
	}
	if len(stuffs) == 0 {
		stuffs = []string{""}
	}
	var out []policy.ComfortOption
	for _, stuff := range stuffs {
		comfort, shown, err := catalog.ShownStatValue(def, stuff, StatComfort)
		if err != nil {
			return nil, err
		}
		if !shown {
			continue
		}
		cost, err := catalog.costValue(def, stuff)
		if err != nil {
			return nil, err
		}
		out = append(out, policy.ComfortOption{Def: def, Stuff: stuff, Comfort: float64(comfort), Build: cost})
	}
	return out, nil
}
