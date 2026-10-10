package bridge

// The plant and light rules the game's code states over the def rows, derived
// from the mirrored ThingDef rows (the native censuses used to send each answer
// beside the live row it annotated).

// woodHarvestTag is the PlantProperties.harvestTag of a tree
// (PlantProperties.IsTree compares it).
const woodHarvestTag = "Wood"

// PlantIsTree is PlantProperties.IsTree of def's row: a wood-harvest tag, or
// forceIsTree. False for a def without plant properties.
func (catalog *DefinitionCatalog) PlantIsTree(def string) bool {
	props := catalog.ThingDef(def).GetPlant()
	return props != nil && (props.GetHarvestTag() == woodHarvestTag || props.GetForceIsTree())
}

// PlantDiesToLight is PlantProperties.diesToLight of def's row (cave fungus).
func (catalog *DefinitionCatalog) PlantDiesToLight(def string) bool {
	return catalog.ThingDef(def).GetPlant().GetDiesToLight()
}

// GlowRadius is the glowRadius of def's CompProperties_Glower row, false when
// the def has no glower comp.
func (catalog *DefinitionCatalog) GlowRadius(def string) (radius float64, glows bool, err error) {
	row := catalog.ThingDef(def)
	if row == nil {
		return 0, false, nil
	}
	comp, err := catalog.CompOf(row, ClassGlowerComp)
	if err != nil || comp == nil {
		return 0, false, err
	}
	radius, err = CompFloat(comp, "glowRadius")
	return radius, err == nil, err
}
