package bridge

import (
	"errors"
	"fmt"
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// The royalty static defs as a Go view of the def mirror:
// the title ladder from every RoyalTitleDef row and the permit catalog from
// every RoyalTitlePermitDef row. The native royalty read carries only the
// colonists' holdings; nothing here is read from native and no def name is
// listed.

// ErrRoyaltyDefs marks a royalty def the mirror carries unreadably; the
// wrapped text names the def.
var ErrRoyaltyDefs = errors.New("royalty defs")

func royaltyDefError(kind, name, format string, args ...any) error {
	return fmt.Errorf("%w: %s %s: %s", ErrRoyaltyDefs, kind, name, fmt.Sprintf(format, args...))
}

// WithTitleDefs is f with its Ladder and Permits read from the mirror: every
// RoyalTitleDef by ascending seniority (then name), each with its favor,
// bedroom and throne requirements, and every RoyalTitlePermitDef by name. An
// unreadable def, or a catalog holding no RoyalTitleDef, is an error wrapping
// ErrRoyaltyDefs or ErrThroneRequirements. f is not changed.
func (catalog *DefinitionCatalog) WithTitleDefs(f policy.RoyaltyFacts) (policy.RoyaltyFacts, error) {
	if catalog == nil {
		return policy.RoyaltyFacts{}, royaltyDefError("catalog", "", "no definition catalog")
	}
	rows := catalog.Defs[(&d.RoyalTitleDef{}).ProtoReflect().Descriptor().FullName()]
	titles := make([]*d.RoyalTitleDef, 0, len(rows))
	for name, row := range rows {
		title, ok := row.(*d.RoyalTitleDef)
		if !ok || validID(name) != nil {
			return policy.RoyaltyFacts{}, royaltyDefError("title", name, "unreadable row")
		}
		titles = append(titles, title)
	}
	if len(titles) == 0 {
		return policy.RoyaltyFacts{}, royaltyDefError("catalog", "", "no RoyalTitleDef rows")
	}
	sort.Slice(titles, func(i, j int) bool {
		if titles[i].GetSeniority() != titles[j].GetSeniority() {
			return titles[i].GetSeniority() < titles[j].GetSeniority()
		}
		return titles[i].GetDefName() < titles[j].GetDefName()
	})
	f.Ladder = make([]policy.RoyalRung, 0, len(titles))
	for _, title := range titles {
		rung, err := catalog.royalRung(title)
		if err != nil {
			return policy.RoyaltyFacts{}, err
		}
		f.Ladder = append(f.Ladder, rung)
	}
	f.Permits = map[string]policy.RoyalPermit{}
	for name, row := range catalog.Defs[(&d.RoyalTitlePermitDef{}).ProtoReflect().Descriptor().FullName()] {
		permit, ok := row.(*d.RoyalTitlePermitDef)
		if !ok || validID(name) != nil || permit.GetMinTitle() != "" && validID(permit.GetMinTitle()) != nil {
			return policy.RoyaltyFacts{}, royaltyDefError("permit", name, "unreadable row")
		}
		// The worker class is the type name without its namespace (the base
		// RoyalTitlePermitWorker for a permit with no worker of its own).
		worker := permit.GetWorkerClass()
		worker = worker[strings.LastIndex(worker, ".")+1:]
		out := policy.RoyalPermit{Name: name, PermitPoints: domain.Known(int(permit.GetPermitPointCost())), Acts: domain.Known(permit.GetRoyalAid() != nil),
			CooldownDays: domain.Known(float64(permit.GetCooldownDays())), Worker: worker}
		if permit.GetMinTitle() != "" {
			out.MinTitle = domain.Known(permit.GetMinTitle())
		}
		if permit.GetRoyalAid() != nil {
			out.FavorCost = domain.Known(int(permit.GetRoyalAid().GetFavorCost()))
		}
		f.Permits[name] = out
	}
	return f, nil
}

// royalRung is title's ladder row: seniority, favor needed (favorCost), the
// bedroom requirements and the throne requirement.
func (catalog *DefinitionCatalog) royalRung(title *d.RoyalTitleDef) (policy.RoyalRung, error) {
	name := title.GetDefName()
	rung := policy.RoyalRung{Title: name, Seniority: domain.Known(int(title.GetSeniority())), FavorNeeded: domain.Known(int(title.GetFavorCost()))}
	bedroom, err := bedroomRequirements(title, func([]string) bool { return false })
	if err != nil {
		return policy.RoyalRung{}, err
	}
	rung.BedroomThings = bedroom.things
	if bedroom.haveArea {
		rung.BedroomMinArea = domain.Known(bedroom.area)
	}
	if bedroom.haveImpressiveness {
		rung.BedroomMinImpressiveness = domain.Known(bedroom.impressiveness)
	}
	if bedroom.floored {
		rung.BedroomFloored = domain.Known(true)
	}
	req, err := catalog.ThroneRequirements(name)
	if err != nil {
		return policy.RoyalRung{}, err
	}
	req.ForbiddenDefs = catalog.forbiddenDefs(req)
	rung.Throne = domain.Known(req)
	return rung, nil
}

// bedroom is a title's bedroom requirements: the largest area and
// impressiveness any requires (have* when one does), whether a floor is
// required, and the furniture.
type bedroom struct {
	area, impressiveness         int
	haveArea, haveImpressiveness bool
	floored                      bool
	things                       []policy.BedroomThing
}

// bedroomRequirements reads title's bedroomRequirements, skipping each whose
// disablingPrecepts disabled reports waived. A requirement with no furniture
// or an invalid name is an error.
func bedroomRequirements(title *d.RoyalTitleDef, disabled func(disablingPrecepts []string) bool) (bedroom, error) {
	name := title.GetDefName()
	var out bedroom
	thing := func(anyOf []string, count int32) error {
		if len(anyOf) == 0 || count < 1 {
			return royaltyDefError("title", name, "invalid bedroom furniture requirement")
		}
		row := policy.BedroomThing{Count: int(count)}
		for _, def := range anyOf {
			if validID(def) != nil {
				return royaltyDefError("title", name, "invalid bedroom furniture definition")
			}
			row.AnyOf = append(row.AnyOf, policy.Resource(def))
		}
		out.things = append(out.things, row)
		return nil
	}
	for _, opt := range title.GetBedroomRequirements() {
		var err error
		switch req := opt.GetValue().GetValue().(type) {
		case *d.RoomRequirementAny_RoomRequirement_Area:
			if !disabled(req.RoomRequirement_Area.GetDisablingPrecepts()) {
				out.haveArea, out.area = true, max(out.area, int(req.RoomRequirement_Area.GetArea()))
			}
		case *d.RoomRequirementAny_RoomRequirement_Impressiveness:
			if !disabled(req.RoomRequirement_Impressiveness.GetDisablingPrecepts()) {
				out.haveImpressiveness, out.impressiveness = true, max(out.impressiveness, int(req.RoomRequirement_Impressiveness.GetImpressiveness()))
			}
		case *d.RoomRequirementAny_RoomRequirement_TerrainWithTags:
			out.floored = out.floored || !disabled(req.RoomRequirement_TerrainWithTags.GetDisablingPrecepts())
		case *d.RoomRequirementAny_RoomRequirement_ThingAnyOfCount:
			if !disabled(req.RoomRequirement_ThingAnyOfCount.GetDisablingPrecepts()) {
				err = thing(req.RoomRequirement_ThingAnyOfCount.GetThings(), req.RoomRequirement_ThingAnyOfCount.GetCount())
			}
		case *d.RoomRequirementAny_RoomRequirement_ThingAnyOf:
			if !disabled(req.RoomRequirement_ThingAnyOf.GetDisablingPrecepts()) {
				err = thing(req.RoomRequirement_ThingAnyOf.GetThings(), 1)
			}
		case *d.RoomRequirementAny_RoomRequirement_ThingCount:
			if !disabled(req.RoomRequirement_ThingCount.GetDisablingPrecepts()) {
				err = thing([]string{req.RoomRequirement_ThingCount.GetThingDef()}, req.RoomRequirement_ThingCount.GetCount())
			}
		case *d.RoomRequirementAny_RoomRequirement_Thing:
			if !disabled(req.RoomRequirement_Thing.GetDisablingPrecepts()) {
				err = thing([]string{req.RoomRequirement_Thing.GetThingDef()}, 1)
			}
		}
		if err != nil {
			return bedroom{}, err
		}
	}
	return out, nil
}
