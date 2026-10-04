package bridge

import (
	"errors"
	"fmt"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// The throne room's requirements as a Go view of the def mirror (#1861,
// epic #1696): RoyalTitleDef.throneRoomRequirements, a list of typed
// RoomRequirement messages, decoded into policy.ThroneRequirements. Nothing
// is read from native and no def name is listed here.

// ErrThroneRequirements marks a title whose throne requirements the mirror
// does not carry or carries unreadably; the wrapped text names the title and
// the requirement.
var ErrThroneRequirements = errors.New("throne requirements")

func throneError(title, format string, args ...any) error {
	return fmt.Errorf("%w: title %s: %s", ErrThroneRequirements, title, fmt.Sprintf(format, args...))
}

// ThroneRequirements is title's throne-room requirement from its RoyalTitleDef
// row. A title with no throneRoomRequirements asks for no throne (no Things).
// A missing row, a requirement the row leaves unset, a malformed one (no
// things, a count below one, a tag-less floor), a kind given twice that may
// appear once, or a throne without an area is an error wrapping
// ErrThroneRequirements.
func (catalog *DefinitionCatalog) ThroneRequirements(title string) (policy.ThroneRequirements, error) {
	if catalog == nil {
		return policy.ThroneRequirements{}, throneError(title, "no definition catalog")
	}
	row := DefRow[*d.RoyalTitleDef](catalog, title)
	if row == nil {
		return policy.ThroneRequirements{}, throneError(title, "the catalog has no RoyalTitleDef row")
	}
	var out policy.ThroneRequirements
	var haveArea, haveImpressiveness, haveThrone, haveFloor bool
	ids := func(kind string, list []string) error {
		if len(list) == 0 {
			return throneError(title, "%s names no def", kind)
		}
		for _, id := range list {
			if validID(id) != nil {
				return throneError(title, "%s names an invalid def", kind)
			}
		}
		return nil
	}
	count := func(kind string, n int32) error {
		if n < 1 {
			return throneError(title, "%s count is %d", kind, n)
		}
		return nil
	}
	for i, opt := range row.GetThroneRoomRequirements() {
		switch req := opt.GetValue().GetValue().(type) {
		case *d.RoomRequirementAny_RoomRequirement_HasAssignedThroneAnyOf:
			if haveThrone {
				return policy.ThroneRequirements{}, throneError(title, "two assigned-throne requirements")
			}
			if err := ids("HasAssignedThroneAnyOf", req.RoomRequirement_HasAssignedThroneAnyOf.GetThings()); err != nil {
				return policy.ThroneRequirements{}, err
			}
			haveThrone, out.Assigned = true, true
			out.Things = slices.Clone(req.RoomRequirement_HasAssignedThroneAnyOf.GetThings())
		case *d.RoomRequirementAny_RoomRequirement_Area:
			if haveArea || req.RoomRequirement_Area.GetArea() < 1 {
				return policy.ThroneRequirements{}, throneError(title, "invalid or repeated Area requirement")
			}
			haveArea, out.MinArea = true, int(req.RoomRequirement_Area.GetArea())
		case *d.RoomRequirementAny_RoomRequirement_Impressiveness:
			if haveImpressiveness || req.RoomRequirement_Impressiveness.GetImpressiveness() < 0 {
				return policy.ThroneRequirements{}, throneError(title, "invalid or repeated Impressiveness requirement")
			}
			haveImpressiveness, out.MinImpressiveness = true, int(req.RoomRequirement_Impressiveness.GetImpressiveness())
		case *d.RoomRequirementAny_RoomRequirement_TerrainWithTags:
			if haveFloor {
				return policy.ThroneRequirements{}, throneError(title, "two floor requirements")
			}
			if err := ids("TerrainWithTags", req.RoomRequirement_TerrainWithTags.GetTags()); err != nil {
				return policy.ThroneRequirements{}, err
			}
			haveFloor = true
			out.FloorTags = slices.Clone(req.RoomRequirement_TerrainWithTags.GetTags())
			out.FloorLabel = req.RoomRequirement_TerrainWithTags.GetLabelKey()
		case *d.RoomRequirementAny_RoomRequirement_ThingAnyOfCount:
			r := req.RoomRequirement_ThingAnyOfCount
			if err := errors.Join(ids("ThingAnyOfCount", r.GetThings()), count("ThingAnyOfCount", r.GetCount())); err != nil {
				return policy.ThroneRequirements{}, err
			}
			out.AnyOfCounts = append(out.AnyOfCounts, policy.ThingAnyOfCount{Things: slices.Clone(r.GetThings()), Count: int(r.GetCount())})
		case *d.RoomRequirementAny_RoomRequirement_ThingCount:
			r := req.RoomRequirement_ThingCount
			if err := errors.Join(ids("ThingCount", []string{r.GetThingDef()}), count("ThingCount", r.GetCount())); err != nil {
				return policy.ThroneRequirements{}, err
			}
			out.Counts = append(out.Counts, policy.ThingCount{Def: r.GetThingDef(), Count: int(r.GetCount())})
		case *d.RoomRequirementAny_RoomRequirement_Thing:
			if err := ids("Thing", []string{req.RoomRequirement_Thing.GetThingDef()}); err != nil {
				return policy.ThroneRequirements{}, err
			}
			out.Counts = append(out.Counts, policy.ThingCount{Def: req.RoomRequirement_Thing.GetThingDef(), Count: 1})
		case *d.RoomRequirementAny_RoomRequirement_ThingAnyOf:
			if err := ids("ThingAnyOf", req.RoomRequirement_ThingAnyOf.GetThings()); err != nil {
				return policy.ThroneRequirements{}, err
			}
			out.AnyOf = append(out.AnyOf, slices.Clone(req.RoomRequirement_ThingAnyOf.GetThings()))
		case *d.RoomRequirementAny_RoomRequirement_AllThingsAnyOfAreGlowing:
			if err := ids("AllThingsAnyOfAreGlowing", req.RoomRequirement_AllThingsAnyOfAreGlowing.GetThings()); err != nil {
				return policy.ThroneRequirements{}, err
			}
			out.Glowing = append(out.Glowing, slices.Clone(req.RoomRequirement_AllThingsAnyOfAreGlowing.GetThings()))
		case *d.RoomRequirementAny_RoomRequirement_AllThingsAreGlowing:
			if err := ids("AllThingsAreGlowing", []string{req.RoomRequirement_AllThingsAreGlowing.GetThingDef()}); err != nil {
				return policy.ThroneRequirements{}, err
			}
			out.Glowing = append(out.Glowing, []string{req.RoomRequirement_AllThingsAreGlowing.GetThingDef()})
		case *d.RoomRequirementAny_RoomRequirement_ForbiddenBuildings:
			r := req.RoomRequirement_ForbiddenBuildings
			tags := slices.Clone(r.GetBuildingTags())
			for _, tag := range r.GetBuildingTagsSet() {
				if !slices.Contains(tags, tag) {
					tags = append(tags, tag)
				}
			}
			if err := ids("ForbiddenBuildings", tags); err != nil {
				return policy.ThroneRequirements{}, err
			}
			for _, tag := range tags {
				if !slices.Contains(out.ForbiddenBuildingTags, tag) {
					out.ForbiddenBuildingTags = append(out.ForbiddenBuildingTags, tag)
				}
			}
		case *d.RoomRequirementAny_RoomRequirement_ForbidAltars:
			out.ForbidAltars = true
		default:
			return policy.ThroneRequirements{}, throneError(title, "requirement %d is unset or of a kind the view does not read (%T)", i, req)
		}
	}
	if haveThrone && !haveArea {
		return policy.ThroneRequirements{}, throneError(title, "a throne without an Area requirement")
	}
	return out, nil
}

// WithThroneRequirements is f with every rung's Throne read from the mirror,
// the first unreadable title's error otherwise. f is not changed.
func (catalog *DefinitionCatalog) WithThroneRequirements(f policy.RoyaltyFacts) (policy.RoyaltyFacts, error) {
	f.Ladder = slices.Clone(f.Ladder)
	for i := range f.Ladder {
		req, err := catalog.ThroneRequirements(f.Ladder[i].Title)
		if err != nil {
			return policy.RoyaltyFacts{}, err
		}
		req.ForbiddenDefs = catalog.forbiddenDefs(req)
		f.Ladder[i].Throne = domain.Known(req)
	}
	return f, nil
}

// forbiddenDefs are the building defs req forbids: those whose
// building.buildingTags meet ForbiddenBuildingTags (the game's
// RoomRequirement_ForbiddenBuildings test) and, when altars are forbidden,
// those with isAltar (RoomRequirement_ForbidAltars), sorted (#1865).
func (catalog *DefinitionCatalog) forbiddenDefs(req policy.ThroneRequirements) []string {
	if catalog == nil || len(req.ForbiddenBuildingTags) == 0 && !req.ForbidAltars {
		return nil
	}
	var out []string
	for name, def := range catalog.ThingDefs {
		forbidden := req.ForbidAltars && def.GetIsAltar()
		for _, tag := range def.GetBuilding().GetBuildingTags() {
			forbidden = forbidden || slices.Contains(req.ForbiddenBuildingTags, tag)
		}
		if forbidden {
			out = append(out, name)
		}
	}
	slices.Sort(out)
	return out
}
