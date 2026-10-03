package policy

import (
	"regexp"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// tierOuterInfix marks the outer ring's sections (#1596): the name is
// TierPerimeterPrefix plus "outer-NN", and a re-cut revision keeps the infix.
const tierOuterInfix = "-outer-"

// IsOuterPerimeterTier reports whether a tier is an outer ring section.
func IsOuterPerimeterTier(name DefenseTierName) bool {
	return IsPerimeterTier(name) && strings.Contains(string(name), tierOuterInfix)
}

var corePerimeterTier = regexp.MustCompile(`^` + TierPerimeterPrefix + `(r\d+-)?\d+$`)

// IsCorePerimeterTier reports whether a tier is one of the core ring's wall
// sections: not the outer ring, the geothermal shell, a pump, pocket, lamp,
// bait room or prison turret, nor a removal.
func IsCorePerimeterTier(name DefenseTierName) bool {
	return corePerimeterTier.MatchString(string(name))
}

// PerimeterOuterSections cuts the plan's outer ring (ReserveOuterWall and
// ReserveOuterGate, #1595) into stone sections the way PerimeterSections cuts
// the core ring, nearest the killbox first. The ring is never bridged: a
// footing the stone wall cannot stand on is refused at placement and left
// out of its section. The plan holds no outer reservations before the ring
// is planned, which yields no sections.
func PerimeterOuterSections(plan LayoutPlan, wall, door string, rock func(domain.Cell) bool) ([]PerimeterSection, error) {
	outer := LayoutPlan{}
	for _, r := range plan.Reservations {
		switch r.Kind {
		case ReserveOuterWall:
			outer.Reservations = append(outer.Reservations, LayoutReservation{Kind: ReservePerimeter, Area: r.Area})
		case ReserveOuterGate:
			outer.Reservations = append(outer.Reservations, LayoutReservation{Kind: ReserveGate, Area: r.Area})
		case ReserveKillbox:
			outer.Reservations = append(outer.Reservations, r)
		}
	}
	sections, err := PerimeterSections(outer, wall, door, PerimeterBridge, rock)
	if err != nil {
		return nil, err
	}
	for i := range sections {
		sections[i].Name = DefenseTierName(TierPerimeterPrefix + "outer-" + strings.TrimPrefix(string(sections[i].Name), TierPerimeterPrefix))
	}
	return sections, nil
}
