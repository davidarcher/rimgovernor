package policy

import (
	"encoding/json"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// tidyFixture is an idle Masonry colony on a 64x64 map whose grid origin
// sits at (16,16): every cell open, fertile soil, one managed 2x2 field
// off the grid at (20,5) (the Fields district lies south of the origin).
func tidyFixture() TidyRequest {
	r := TidyRequest{Tier: domain.Known(BuildTierMasonry), Grid: domain.Known(ColonyGrid{Origin: domain.Cell{X: 16, Z: 16}, Pitch: GridPitch, Axes: ColonyGridAxes}), Busy: domain.Known(false), Bounds: Bounds{64, 64}}
	for x := int32(0); x < 64; x++ {
		for z := int32(0); z < 64; z++ {
			r.Cells = append(r.Cells, SiteCell{Cell: domain.Cell{X: x, Z: z}, Walkable: domain.Known(true), Occupied: domain.Known(false), Zone: domain.Known(false), Fertility: domain.Known(1.0)})
		}
	}
	r.Items = []TidyItem{{Kind: TidyField, ID: "Zone_7", Footprint: Rectangle{20, 5, 2, 2}, Cells: 4, Crop: "Plant_Rice", Managed: true}}
	return tidyZoned(r, Rectangle{20, 5, 2, 2}, "Zone_7")
}

func tidyZoned(r TidyRequest, rect Rectangle, id string) TidyRequest {
	inside := map[domain.Cell]bool{}
	for _, c := range rectCells(rect) {
		inside[c] = true
	}
	for i := range r.Cells {
		if inside[r.Cells[i].Cell] {
			r.Cells[i].Zone, r.Cells[i].ZoneID = domain.Known(true), domain.Known(id)
		}
	}
	return r
}

func TestTidyLayoutIdleColonyProposesTheOffGridFieldAndExplainsItsGain(t *testing.T) {
	r := tidyFixture()
	review := PlanTidyLayout(r)
	if !review.Known || !review.Active || review.Proposal == nil || review.Candidates != 1 {
		t.Fatalf("review %+v", review)
	}
	proposal := *review.Proposal
	grid, _ := r.Grid.Value()
	if proposal.Item.ID != "Zone_7" || tidyAlignment(grid, TidyItem{Kind: TidyField, Footprint: proposal.Target}) != 0 || proposal.Target.Width != ColonyGridInterior || proposal.Target.Height != ColonyGridSubCell {
		t.Fatalf("proposal %+v", proposal)
	}
	if before := tidyAlignment(grid, r.Items[0]); proposal.Gain != before || proposal.Gain <= 0 {
		t.Fatalf("gain %d, alignment error %d", proposal.Gain, before)
	}
	if grid.District(domain.Cell{X: proposal.Target.X, Z: proposal.Target.Z}) != DistrictFields {
		t.Fatalf("target %+v is not in Fields", proposal.Target)
	}
	for _, want := range []string{"field Zone_7", "corner error", "alignment gain", "crop Plant_Rice kept", "on grid (error 0)"} {
		if !strings.Contains(proposal.Explanation, want) {
			t.Fatalf("explanation %q lacks %q", proposal.Explanation, want)
		}
	}
	if again := PlanTidyLayout(r); *again.Proposal != *review.Proposal {
		t.Fatal("proposal is not deterministic")
	}
}

func TestTidyLayoutBusyColonyOrInFlightProposesNothing(t *testing.T) {
	for name, mutate := range map[string]func(*TidyRequest){
		"busy":         func(r *TidyRequest) { r.Busy = domain.Known(true) },
		"busy unknown": func(r *TidyRequest) { r.Busy = domain.Unknown[bool]() },
	} {
		r := tidyFixture()
		mutate(&r)
		review := PlanTidyLayout(r)
		if !review.Known || review.Active || review.Candidates != 1 || review.Reason == "" {
			t.Fatalf("%s: review %+v", name, review)
		}
	}
	// A re-site in flight proposes nothing but keeps the goal active until
	// its delete phase closes, even once the moving item is tidied and no
	// candidate remains (the first live run recovered the goal there and
	// stranded the re-site at "moving").
	r := tidyFixture()
	r.InFlight, r.Tidied = true, []string{r.Items[0].ID}
	review := PlanTidyLayout(r)
	if !review.Known || !review.Active || review.Proposal != nil || review.Candidates != 0 || review.Reason != "re-site in flight" {
		t.Fatalf("in flight: review %+v", review)
	}
}

func TestTidyLayoutNeverTouchesPlayerZonesRoomsInUseOrTidiedItems(t *testing.T) {
	r := tidyFixture()
	r.Items = []TidyItem{
		{Kind: TidyField, ID: "Zone_7", Footprint: Rectangle{20, 5, 2, 2}, Cells: 4, Crop: "Plant_Rice"},
		{Kind: TidyStockpile, ID: "Zone_8", Footprint: Rectangle{3, 30, 3, 3}, Cells: 9},
		{Kind: TidyShell, ID: "Room_2", Footprint: Rectangle{40, 40, 7, 7}, Managed: true, InUse: true, Replaced: true},
		{Kind: TidyShell, ID: "Room_3", Footprint: Rectangle{50, 40, 7, 7}, Managed: true, Replaced: false},
		{Kind: TidyField, ID: "Zone_9", Footprint: Rectangle{20, 40, 2, 2}, Cells: 4, Crop: "Plant_Rice", Managed: true},
	}
	r.Tidied = []string{"Zone_9"}
	review := PlanTidyLayout(r)
	if !review.Known || review.Active || review.Candidates != 0 || review.Reason != "nothing off grid" {
		t.Fatalf("review %+v", review)
	}
}

func TestTidyLayoutSecondRunAfterTheTidyProposesNothing(t *testing.T) {
	r := tidyFixture()
	first := PlanTidyLayout(r)
	proposal := first.Proposal
	r.Tidied = []string{proposal.Item.ID}
	second := PlanTidyLayout(r)
	if !second.Known || second.Active || second.Candidates != 0 {
		t.Fatalf("second review %+v", second)
	}
}

func TestTidyLayoutGatesOnTierAndGrid(t *testing.T) {
	r := tidyFixture()
	r.Tier = domain.Known(BuildTierCamp)
	if review := PlanTidyLayout(r); !review.Known || review.Active {
		t.Fatalf("camp review %+v", review)
	}
	r.Tier = domain.Unknown[BuildTier]()
	if review := PlanTidyLayout(r); review.Known || review.Active {
		t.Fatalf("unknown tier review %+v", review)
	}
	r = tidyFixture()
	r.Grid = domain.Unknown[ColonyGrid]()
	if review := PlanTidyLayout(r); review.Known || review.Active {
		t.Fatalf("no grid review %+v", review)
	}
}

func TestTidyLayoutAnOnGridWholeModuleFieldIsNotACandidateButASmallOneIs(t *testing.T) {
	r := tidyFixture()
	// A whole-module field on a grid intersection needs nothing.
	r.Items = []TidyItem{{Kind: TidyField, ID: "Zone_1", Footprint: Rectangle{17, 1, 11, 11}, Cells: 121, Crop: "Plant_Rice", Managed: true}}
	if review := PlanTidyLayout(r); review.Active || review.Candidates != 0 {
		t.Fatalf("aligned module review %+v", review)
	}
	// A 2x2 on an intersection is smaller than the half module: re-sited.
	r.Items = []TidyItem{{Kind: TidyField, ID: "Zone_1", Footprint: Rectangle{17, 1, 2, 2}, Cells: 4, Crop: "Plant_Rice", Managed: true}}
	r = tidyZoned(r, Rectangle{17, 1, 2, 2}, "Zone_1")
	review := PlanTidyLayout(r)
	proposal := review.Proposal
	if !review.Active || proposal.Gain != 0 || proposal.Target.Width*proposal.Target.Height != int32(FieldHalfModuleCellCount) {
		t.Fatalf("small field review %+v", review)
	}
}

func TestTidyLayoutReplacedEmptyShellIsDeconstructedWithoutATarget(t *testing.T) {
	r := tidyFixture()
	r.Items = []TidyItem{{Kind: TidyShell, ID: "Room_1", Footprint: Rectangle{40, 40, 7, 7}, Managed: true, Replaced: true}}
	review := PlanTidyLayout(r)
	proposal := review.Proposal
	if !review.Active || proposal.Item.Kind != TidyShell || proposal.Target != (Rectangle{}) || proposal.Gain <= 0 || !strings.Contains(proposal.Explanation, "deconstruct") {
		t.Fatalf("shell review %+v", review)
	}
}

func TestTidyLayoutStockpileMovesToStorageAndNoFreeModuleIsReported(t *testing.T) {
	r := tidyFixture()
	r.Items = []TidyItem{{Kind: TidyStockpile, ID: "Zone_5", Footprint: Rectangle{3, 30, 3, 3}, Cells: 9, Managed: true}}
	r = tidyZoned(r, Rectangle{3, 30, 3, 3}, "Zone_5")
	review := PlanTidyLayout(r)
	proposal := review.Proposal
	grid, _ := r.Grid.Value()
	if !review.Active || proposal.Target.Width != ColonyGridSubCell || proposal.Target.Height != ColonyGridSubCell || grid.District(domain.Cell{X: proposal.Target.X + 2, Z: proposal.Target.Z + 2}) != DistrictStorage {
		t.Fatalf("stockpile review %+v", review)
	}
	for i := range r.Cells {
		r.Cells[i].Occupied = domain.Known(true)
	}
	if review := PlanTidyLayout(r); review.Active || review.Reason != "no free module" {
		t.Fatalf("occupied review %+v", review)
	}
}

// The review is persisted as JSON by the journal and reloaded by the tidy
// planner: the proposal must survive that round trip (the first layout/tidy
// run lost it behind an unexported Fact and never planned).
func TestTidyReviewProposalSurvivesJSON(t *testing.T) {
	review := PlanTidyLayout(tidyFixture())
	encoded, err := json.Marshal(review)
	if err != nil {
		t.Fatal(err)
	}
	var decoded TidyReview
	if err := json.Unmarshal(encoded, &decoded); err != nil {
		t.Fatal(err)
	}
	if decoded.Proposal == nil || *decoded.Proposal != *review.Proposal {
		t.Fatalf("proposal lost in JSON: %s", encoded)
	}
}
