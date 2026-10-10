package stateval

import (
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"

	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// The FleshTypeDefOf defs ShouldShowFor tells apart.
const (
	fleshNormal           = "Normal"
	fleshMechanoid        = "Mechanoid"
	fleshEntityMechanical = "EntityMechanical"
	fleshEntityFlesh      = "EntityFlesh"
	fleshFleshbeast       = "Fleshbeast"
	fleshDrone            = "Drone"
)

// everHaulable is ThingDef.EverHaulable.
func everHaulable(t *d.ThingDef) bool { return t.GetAlwaysHaulable() || t.GetDesignateHaulable() }

// minifiable is ThingDef.Minifiable.
func minifiable(t *d.ThingDef) bool { return t.GetMinifiedDef() != "" }

// humanlike is RaceProperties.Humanlike.
func humanlike(r *d.RaceProperties) bool {
	return r.GetIntelligence() == d.Intelligence_INTELLIGENCE_HUMANLIKE
}

// fleshType is RaceProperties.FleshType's def name (Normal when unset).
func fleshType(r *d.RaceProperties) string {
	if f := r.GetFleshType(); f != "" {
		return f
	}
	return fleshNormal
}

// anomalyEntity is RaceProperties.IsAnomalyEntity.
func (e *Evaluator) anomalyEntity(flesh string) (bool, error) {
	active, err := e.modActive(modAnomaly)
	if err != nil || !active {
		return false, err
	}
	return flesh == fleshEntityMechanical || flesh == fleshEntityFlesh || flesh == fleshFleshbeast, nil
}

// animal is RaceProperties.Animal: not a tool user, flesh (FleshTypeDef.isOrganic), not an entity.
func (e *Evaluator) animal(r *d.RaceProperties, anomalyEntity bool) (bool, error) {
	if r.GetIntelligence() != d.Intelligence_INTELLIGENCE_ANIMAL || anomalyEntity {
		return false, nil
	}
	flesh := bridge.DefRow[*d.FleshTypeDef](e.catalog, fleshType(r))
	if flesh == nil {
		return false, fmt.Errorf("catalog has no flesh type %s", fleshType(r))
	}
	return flesh.GetIsOrganic(), nil
}

// classIsA is typeof(base).IsAssignableFrom(class) over the catalog's class
// chains; an unset class (a null Type) is assignable to nothing.
func (e *Evaluator) classIsA(class, base string) (bool, error) {
	if class == "" {
		return false, nil
	}
	return e.catalog.ClassIsA(class, base)
}

// hasCompFrom is ThingDef.HasAssignableCompFrom: some comp's class derives
// from base.
func (e *Evaluator) hasCompFrom(t *d.ThingDef, base string) (bool, error) {
	for i, opt := range t.GetComps() {
		comp := opt.GetValue()
		if comp == nil {
			return false, fmt.Errorf("thing def %s has an empty comp at index %d", t.GetDefName(), i)
		}
		msg := comp.ProtoReflect()
		field := msg.WhichOneof(msg.Descriptor().Oneofs().ByName("value"))
		if field == nil {
			return false, fmt.Errorf("thing def %s comp %d names no class", t.GetDefName(), i)
		}
		inner := msg.Get(field).Message()
		class := inner.Get(inner.Descriptor().Fields().ByName("compClass")).String()
		ok, err := e.classIsA(class, base)
		if err != nil || ok {
			return ok, err
		}
	}
	return false, nil
}
