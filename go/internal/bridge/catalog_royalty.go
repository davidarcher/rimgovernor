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
	var area, impressiveness int
	var haveArea, haveImpressiveness, floored bool
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
		rung.BedroomThings = append(rung.BedroomThings, row)
		return nil
	}
	for _, opt := range title.GetBedroomRequirements() {
		var err error
		switch req := opt.GetValue().GetValue().(type) {
		case *d.RoomRequirementAny_RoomRequirement_Area:
			haveArea, area = true, max(area, int(req.RoomRequirement_Area.GetArea()))
		case *d.RoomRequirementAny_RoomRequirement_Impressiveness:
			haveImpressiveness, impressiveness = true, max(impressiveness, int(req.RoomRequirement_Impressiveness.GetImpressiveness()))
		case *d.RoomRequirementAny_RoomRequirement_TerrainWithTags:
			floored = true
		case *d.RoomRequirementAny_RoomRequirement_ThingAnyOfCount:
			err = thing(req.RoomRequirement_ThingAnyOfCount.GetThings(), req.RoomRequirement_ThingAnyOfCount.GetCount())
		case *d.RoomRequirementAny_RoomRequirement_ThingAnyOf:
			err = thing(req.RoomRequirement_ThingAnyOf.GetThings(), 1)
		case *d.RoomRequirementAny_RoomRequirement_ThingCount:
			err = thing([]string{req.RoomRequirement_ThingCount.GetThingDef()}, req.RoomRequirement_ThingCount.GetCount())
		case *d.RoomRequirementAny_RoomRequirement_Thing:
			err = thing([]string{req.RoomRequirement_Thing.GetThingDef()}, 1)
		}
		if err != nil {
			return policy.RoyalRung{}, err
		}
	}
	if haveArea {
		rung.BedroomMinArea = domain.Known(area)
	}
	if haveImpressiveness {
		rung.BedroomMinImpressiveness = domain.Known(impressiveness)
	}
	if floored {
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
