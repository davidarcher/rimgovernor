package policy

import (
	"encoding/json"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// tidyFixture is an idle Masonry colony with no rooms.
func tidyFixture() TidyRequest {
	return TidyRequest{Tier: domain.Known(BuildTierMasonry), Busy: domain.Known(false)}
}

func TestTidyLayoutNothingToTidyProposesNothing(t *testing.T) {
	review := PlanTidyLayout(tidyFixture())
	if !review.Known || review.Active || review.Candidates != 0 || review.Reason != "nothing to tidy" {
		t.Fatalf("review %+v", review)
	}
}

func TestTidyLayoutInFlightKeepsTheGoalActive(t *testing.T) {
	// A re-site in flight proposes nothing but keeps the goal active until
	// its move closes, even once the moving item is tidied and no candidate
	// remains.
	r := tidyFixture()
	r.InFlight, r.Tidied = true, []string{"Room_9"}
	review := PlanTidyLayout(r)
	if !review.Known || !review.Active || review.Proposal != nil || review.Candidates != 0 || review.Reason != "re-site in flight" {
		t.Fatalf("in flight: review %+v", review)
	}
}

func TestTidyLayoutGatesOnTier(t *testing.T) {
	r := tidyFixture()
	r.Tier = domain.Known(BuildTierCamp)
	if review := PlanTidyLayout(r); !review.Known || review.Active {
		t.Fatalf("camp review %+v", review)
	}
	r.Tier = domain.Unknown[BuildTier]()
	if review := PlanTidyLayout(r); review.Known || review.Active {
		t.Fatalf("unknown tier review %+v", review)
	}
}

func TestTidyReviewProposalSurvivesJSON(t *testing.T) {
	review := TidyReview{Known: true, Active: true, Candidates: 1, Proposal: &TidyProposal{
		Item: TidyItem{Kind: TidyFurniture, ID: "Room_9", Footprint: Rectangle{0, 0, 8, 6}, Cells: 1}, Gain: 2, Explanation: "move",
		Moves: []TidyMove{{}},
	}}
	encoded, err := json.Marshal(review)
	if err != nil {
		t.Fatal(err)
	}
	var decoded TidyReview
	if err := json.Unmarshal(encoded, &decoded); err != nil {
		t.Fatal(err)
	}
	if decoded.Proposal == nil || !reflect.DeepEqual(*decoded.Proposal, *review.Proposal) {
		t.Fatalf("proposal lost in JSON: %s", encoded)
	}
}
