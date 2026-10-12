package policy

// Fields (#2961, #3005). The survey's whole-map field patches (Zone) no longer
// make the plan's field zones: the station chain's field nodes do, sized by
// FieldCapacityTarget, placed by the embedder beside the rooms and written
// through fieldZone. The tiled plan carries them at every review.

// fieldCrops is the judgment table of the crop a colony's field is planned for
// while nothing is sown: a new colony sows nothing, and a field node exists
// only for a crop, so without a default no field is ever planned and nothing is
// ever sown. The first of the table the catalog offers as an edible, available
// crop wins (rice, the fast staple that tolerates poor soil, then the others a
// tribal start can plant); the field planner's own choice (PlanField) replaces
// it the moment a crop is sown. Registered in DefTables.
var fieldCrops = []string{"Plant_Rice", "Plant_Potato", "Plant_Corn"}

// ReasonFieldDefaultCrop is why a field is planned for a crop nobody sowed.
const ReasonFieldDefaultCrop = "field_crop_unsown_default"

// DefaultFieldCrop is the crop a field is planned for while none is sown: the
// first of the fieldCrops table that choices offers as available and edible.
func DefaultFieldCrop(choices []CropChoice) (CropChoice, bool) {
	for _, name := range fieldCrops {
		for _, c := range choices {
			avail, ak := c.Available.Value()
			edible, ek := c.Edible.Value()
			if c.Name == name && ak && avail && ek && edible {
				return c, true
			}
		}
	}
	return CropChoice{}, false
}

// seedFields are the field nodes of a first plan's seed graph: the crops chosen
// (Crops) sized for the colonists read, with no flow edge (no station is known
// yet). A colony not counted gets none.
func (f StationFacts) seedFields() []FieldNode {
	colonists, ok := f.Colonists.Value()
	if !ok {
		return nil
	}
	b := &graphBuilder{in: StationGraphInput{Crops: f.Crops, FoodReserveDays: f.FoodReserveDays, Population: StationPopulation{Colonists: int(colonists)}}, nodes: map[string]*StationNode{}, edges: map[[2]string]*StationEdge{}}
	return b.fieldNodes(nil)
}

// withFieldZones is zones less the survey's field patches, then fields.
func withFieldZones(zones, fields []LayoutZone) []LayoutZone {
	out := make([]LayoutZone, 0, len(zones)+len(fields))
	for _, z := range zones {
		if z.Kind != ZoneField {
			out = append(out, z)
		}
	}
	return append(out, fields...)
}
