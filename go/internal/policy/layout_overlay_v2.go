package policy

import (
	"fmt"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The overlay draws the layout plan over the whole
// map: zones as filled row runs, each room's outline with its role label, and
// the labelled reservations (the perimeter and killbox as outlines).

var zoneOverlay = map[ZoneKind]overlayStyle{
	ZoneField:  {planGreen, "field"},
	ZoneMining: {planBrown, "mining"},
	ZoneNoGo:   {planDarkPurple, "no-go"},
}

var roomOverlay = map[PlannedRole]overlayStyle{
	PlannedBedroom:          {planBlue, "bedroom"},
	PlannedSuite:            {planBlue, "suite"},
	PlannedShelter:          {planLightBlue, "shelter"},
	PlannedAirlock:          {planLightBlue, "airlock"},
	PlannedPorch:            {planLightBlue, "porch"},
	PlannedDining:           {planAmber, "dining"},
	PlannedRec:              {planPink, "rec room"},
	PlannedLab:              {planViolet, "research"},
	PlannedBattery:          {planCyan, "battery room"},
	PlannedHospital:         {planWhite, "hospital"},
	PlannedPrison:           {planDarkPurple, "jail"},
	PlannedKitchen:          {planYellow, "kitchen"},
	PlannedFreezer:          {planCyan, "freezer"},
	PlannedMealCloset:       {planCyan, "meal closet"},
	PlannedButchery:         {planBrown, "butchery"},
	PlannedStorage:          {planTan, "storage"},
	PlannedArmory:           {planTan, "armory"},
	PlannedWardrobe:         {planTan, "wardrobe"},
	PlannedWorkshop:         {planBrown, "workshop"},
	PlannedReserve:          {planGray, "reserve"},
	PlannedCirculation:      {planGray, "circulation"},
	PlannedTomb:             {planGray, "tomb"},
	PlannedMorgue:           {planCyan, "morgue"},
	PlannedWasteYard:        {planRed, "waste yard"},
	PlannedIncinerator:      {planRed, "incinerator"},
	PlannedGraveyard:        {planGreen, "graveyard"},
	PlannedTrainingRange:    {planBrown, "training range"},
	PlannedSparringRing:     {planBrown, "sparring ring"},
	PlannedYard:             {planTan, "materials yard"},
	PlannedThrone:           {planAmber, "throne room"},
	PlannedNursery:          {planGreen, "nursery"},
	PlannedPlayroom:         {planYellow, "playroom"},
	PlannedClassroom:        {planWhite, "classroom"},
	PlannedDeathrestChamber: {planDarkPurple, "deathrest chamber"},
	PlannedWorship:          {planAmber, "worship room"},
	PlannedContainmentCell:  {planDarkPurple, "containment cell"},
	PlannedIsolationRoom:    {planDarkPurple, "isolation room"},
}

var reservationOverlay = map[ReservationKind]overlayStyle{
	ReserveBatteryRoom: {planCyan, "battery room"},
	ReserveGeothermal:  {planCyan, "geothermal"},
	ReserveYard:        {planTan, "materials yard"},
	ReserveBarn:        {planBrown, "barn"},
	ReservePen:         {planGreen, "pen"},
}

var siteOverlay = map[NodeKind]overlayStyle{
	NodeRing:       {planRed, "perimeter"},
	NodeGate:       {planYellow, "gate"},
	NodePump:       {planCyan, "moisture pump"},
	NodeKillbox:    {planRed, "killbox"},
	NodeFence:      {planYellow, "killbox fence"},
	NodeMortar:     {planRed, "mortar"},
	NodeCoverClear: {planGray, "clear cover"},
	NodePocketWall: {planRed, "pocket wall"},
	NodeVent:       {planCyan, "vent"},
	NodeTurbine:    {planCyan, "turbine"},
	NodeSolar:      {planCyan, "solar"},
	// The defense tiers PlanDefenseTiers cuts under the killbox.
	NodeDefCorridor: {planRed, "corridor"},
	NodeDefLane:     {planAmber, "trap lane"},
	NodeDefFunnel:   {planRed, "funnel"},
	NodeDefFence:    {planYellow, "kill fence"},
	NodeDefTraps:    {planPink, "spike traps"},
	NodeDefFiring:   {planGreen, "firing line"},
	NodeDefShooter:  {planBlue, "shooter floor"},
	NodeDefTurrets:  {planCyan, "turrets"},
	NodeDefMortars:  {planViolet, "mortars"},
	NodeDefIEDs:     {planBrown, "IEDs"},
	NodeDefBait:     {planTan, "bait"},
}

// Ring attributes drawn beside the ring itself.
var (
	bridgeOverlay = overlayStyle{planBrown, "bridge"}
	gapOverlay    = overlayStyle{planYellow, "open gap"}
	laneOverlay   = overlayStyle{planGreen, "turbine lane"}
)

// Overlay draws p inside bounds. Layer order is zones, reservations,
// rooms, so the smaller shapes draw last.
func (p LayoutPlan) Overlay(bounds Bounds) LayoutOverlay {
	var out LayoutOverlay
	label := func(text string, at domain.Cell) {
		if at.X >= 0 && at.Z >= 0 && at.X < bounds.Width && at.Z < bounds.Height {
			out.Labels = append(out.Labels, OverlayLabel{Text: text, Cell: at})
		}
	}
	add := func(style overlayStyle, shape OverlayStyle, rects []Rectangle, runs []RowRun) bool {
		layer := OverlayLayer{Style: shape, Label: style.label, Color: style.hue.fill()}
		if shape == OverlayOutline {
			layer.Color = style.hue.outline()
		}
		for _, r := range rects {
			if c, ok := clip(r, bounds); ok {
				layer.Rects = append(layer.Rects, c)
			}
		}
		for _, r := range runs {
			if c, ok := clipRun(r, bounds); ok {
				layer.Runs = append(layer.Runs, c)
			}
		}
		if len(layer.Rects)+len(layer.Runs) == 0 {
			return false
		}
		out.Layers = append(out.Layers, layer)
		return true
	}
	centre := func(r Rectangle) domain.Cell { return domain.Cell{X: r.X + r.Width/2, Z: r.Z + r.Height/2} }
	for _, z := range p.Zones {
		if style, ok := zoneOverlay[z.Kind]; ok {
			add(style, OverlayFill, nil, z.Runs)
		}
	}
	for _, r := range p.Reservations {
		style, ok := reservationOverlay[r.Kind]
		if !ok {
			continue
		}
		if add(style, OverlayFill, []Rectangle{r.Area.Bounds()}, nil) {
			label(style.label, centre(r.Area.Bounds()))
		}
	}
	// The defense nodes draw as their own cells. The cover band is one wide
	// region: one label, on its first run.
	for _, s := range p.Sites {
		style, ok := siteOverlay[s.Kind]
		if !ok {
			continue
		}
		shape := OverlayFill
		if s.Kind == NodeRing || s.Kind == NodeKillbox {
			shape = OverlayOutline
		}
		// A rectangle goes out as one, any other shape as its row runs.
		var rects []Rectangle
		runs := s.Footprint.Runs
		if s.Footprint.IsRect() {
			rects, runs = []Rectangle{s.Footprint.Bounds()}, nil
		}
		for _, l := range s.Lanes {
			add(laneOverlay, OverlayFill, nil, l.Region.Runs)
		}
		drawn := add(style, shape, rects, runs)
		switch {
		case s.Ring != nil:
			add(bridgeOverlay, OverlayFill, nil, s.Ring.Bridged.Runs)
			add(gapOverlay, OverlayFill, nil, s.Ring.Gaps.Runs)
		case drawn && s.Kind == NodeCoverClear:
			first := s.Footprint.Runs[0]
			label(style.label, domain.Cell{X: first.X + first.Length/2, Z: first.Z})
		case drawn:
			label(style.label, centre(s.Footprint.Bounds()))
		}
	}
	for _, r := range p.Rooms {
		style, ok := roomOverlay[r.Role]
		if !ok {
			continue
		}
		// The room's own cells, not its bounding box: the outline follows
		// the floor and its wall ring, so an L or a ring draws as itself.
		shape := r.Region
		if !r.Unfenced() {
			shape = unionRegions(r.Region, r.Region.Walls())
		}
		text := style.label
		if r.ID > 0 {
			text = fmt.Sprintf("#%d %s", r.ID, style.label)
		}
		if r.Replaces > 0 {
			// A successor also fills its floor, so it reads apart from
			// the room it replaces.
			text += fmt.Sprintf(" (replaces #%d)", r.Replaces)
			add(overlayStyle{style.hue, "successor"}, OverlayFill, nil, r.Region.Runs)
		}
		if add(style, OverlayOutline, nil, shape.Runs) {
			label(text, labelCell(r.Region))
		}
	}
	var doors []domain.Cell
	for _, r := range p.Rooms {
		if r.Unfenced() {
			continue
		}
		doors = append(doors, p.DoorsOf(r)...)
	}
	add(overlayStyle{planWhite, "door"}, OverlayFill, nil, cellRuns(doors))
	p.linesOverlay(bounds, &out)
	return out
}

// labelCell is the region cell nearest the middle of its bounds, so a ring's
// label lands on its floor rather than in its hole.
func labelCell(g Region) domain.Cell {
	b := g.Bounds()
	mid := domain.Cell{X: b.X + b.Width/2, Z: b.Z + b.Height/2}
	best, found := mid, false
	for _, c := range g.Cells() {
		if !found || distanceSquared(c, mid) < distanceSquared(best, mid) {
			best, found = c, true
		}
	}
	return best
}

// SplitFields moves o's field zone layers into their own overlay, so the
// native can hide the busy field-block lattice on its own.
func SplitFields(o LayoutOverlay) (rest, fields LayoutOverlay) {
	rest.Labels = o.Labels
	for _, l := range o.Layers {
		if l.Label == zoneOverlay[ZoneField].label {
			fields.Layers = append(fields.Layers, l)
		} else {
			rest.Layers = append(rest.Layers, l)
		}
	}
	return rest, fields
}

func unionRegions(a, b Region) Region {
	return RegionOfRuns(append(slices.Clone(a.Runs), b.Runs...))
}
