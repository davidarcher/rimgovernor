package policy

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Fields as outdoor nodes of the station graph (#2885). A food crop's field is
// land use the colony owes by demand: the node asks the cells FieldCapacityTarget
// gives (the count at which FieldCoverage reads 1, no stored margin) and sits in
// the same graph as the rooms, because the crops-to-kitchen and crops-to-storage
// haul is the largest flow and a field's position trades against a room's in one
// walk term. Pasture, wood and mining are not nodes: nothing at runtime reads
// pasture or wood and ore is fixed by the map, so they stay the zoning masks
// (Zone). The node names its mask, ZoneField (soil of at least FertilityMin);
// placement and the tiler's mask are the next stage, and crop choice is the
// caller's: Crops are the crops already chosen.

// ReasonFieldDemand is why the graph holds a field: the crop's capacity target.
const ReasonFieldDemand = "field_crop_capacity_target"

// ReasonFlowCrop is a crop's haul from its field.
const ReasonFlowCrop = "flow_crop"

// FieldNode is the outdoor land one crop needs.
type FieldNode struct {
	ID string
	// Crop is the crop's name and Harvests the resource its harvest yields.
	Crop     string
	Harvests Resource
	// Area is the field's cells: FieldCapacityTarget for the colony.
	Area int
	// Mask is the zoning mask the field sits on (ZoneField) and FertilityMin the
	// least soil fertility the crop grows in; the cells where both hold are the
	// candidates the tiler gets.
	Mask         ZoneKind
	FertilityMin float64
	Reason       string
}

// FieldNode is the field with the id.
func (g StationGraph) FieldNode(id string) (FieldNode, bool) {
	for _, f := range g.Fields {
		if f.ID == id {
			return f, true
		}
	}
	return FieldNode{}, false
}

func fieldID(crop string) string { return "field/" + crop }

// fieldNodes adds a node for each edible crop with a known capacity target and
// the crop's haul edges: its harvest to the store that holds it, and to each
// station whose recipe takes it in. A crop whose target is unknown gets no node;
// an unknown yield or grow time adds the node without flow edges.
func (b *graphBuilder) fieldNodes(loads []recipeLoad) []FieldNode {
	colonists := domain.Unknown[int64]()
	if n := b.in.Population.Colonists; n > 0 {
		colonists = domain.Known(int64(n))
	}
	var out []FieldNode
	for _, crop := range b.in.Crops {
		if edible, known := crop.Edible.Value(); !known || !edible {
			continue
		}
		cells, known := FieldCapacityTarget(colonists, crop, b.in.FoodReserveDays).Value()
		if !known {
			continue
		}
		harvests, hk := crop.Harvests.Value()
		fertility, _ := crop.FertilityMin.Value()
		node := FieldNode{ID: fieldID(crop.Name), Crop: crop.Name, Harvests: harvests, Area: cells, Mask: ZoneField, FertilityMin: max(fertility, zoneFieldFertility), Reason: ReasonFieldDemand}
		out = append(out, node)
		perCell, uk := crop.UnitsPerCell.Value()
		days, gk := crop.GrowDays.Value()
		if !hk || !uk || !gk || !fieldPositive(perCell) || !fieldPositive(days) {
			continue
		}
		if store, ok := b.storeFor(harvests); ok {
			b.addEdge(node.ID, store, float64(cells)*perCell/days, false, ReasonFlowCrop)
		}
		for _, l := range loads {
			for _, pick := range recipePicks(b.in.Recipes[l.recipe]) {
				if pick.Resource == harvests {
					b.addEdge(node.ID, nodeID(l.station, PurposeWork), l.runs*float64(pick.Count), false, ReasonFlowCrop)
				}
			}
		}
	}
	slices.SortFunc(out, func(a, c FieldNode) int {
		switch {
		case a.ID < c.ID:
			return -1
		case a.ID > c.ID:
			return 1
		}
		return 0
	})
	return out
}
