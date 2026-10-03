package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// claimFixture is a Yeoman with the favor for the Knight, a bedroom that
// meets the Knight's requirements and a standing Knight throne room at 60.
func claimFixture(t *testing.T) TitleClaimFacts {
	t.Helper()
	plan, room, _ := throneFixture()
	in, _ := InteriorRoomFromLayout(room)
	def := InteriorPieceDef{Def: "Throne", Size: domain.Cell{X: 1, Z: 1}}
	interior, _ := PlanInterior(in, def)
	var built []CurrentBuilding
	for _, p := range interior.Pieces {
		if p.Slot == throneSlot {
			built = append(built, standingThrone(t, p))
		}
	}
	obs, tidy, _ := upgradeFixture(t, RoomQuality{Impressiveness: 80})
	obs.People = []SleepingPerson{{ID: "Alice"}}
	ladder := throneLadder()
	ladder[1].BedroomMinImpressiveness = domain.Known(50)
	ladder[1].BedroomThings = []BedroomThing{{AnyOf: []Resource{"EndTable"}, Count: 1}}
	obs.Beds = []SleepingBed{replacementBed("Bed_1", "Good", "Alice")}
	tidy[0].Pieces = append(tidy[0].Pieces, TidyPiece{Def: "EndTable"})
	return TitleClaimFacts{
		Royalty:        RoyaltyFacts{Ladder: ladder, Holders: map[PawnID][]RoyalHolding{"Alice": royalHolder("Yeoman", 10)}},
		Sleeping:       obs,
		BedroomPieces:  tidy,
		Plan:           plan,
		Rooms:          tombStanding(room),
		Built:          built,
		Impressiveness: map[string]float64{"r1": 60},
	}
}

func TestTitleClaimWhenUpkeepMeetsTheNextTitle(t *testing.T) {
	got := NextTitleClaim(claimFixture(t))
	if !got.Claim || got.Holder != "Alice" || got.Title != "Knight" || got.Reason != "" {
		t.Fatalf("%+v", got)
	}
}

func TestTitleClaimHoldsWhileRequirementsAreUnmet(t *testing.T) {
	for name, c := range map[string]struct {
		edit   func(*TitleClaimFacts)
		reason TitleClaimReason
	}{
		"favor short": {func(f *TitleClaimFacts) { f.Royalty.Holders["Alice"] = royalHolder("Yeoman", 9) }, ClaimNoFavor},
		"bedroom plain": {func(f *TitleClaimFacts) {
			f.Sleeping.Rooms = domain.Known([]UpkeepRoom{{ID: "Room_1", Quality: domain.Known(RoomQuality{Impressiveness: 40})}})
		}, ClaimBedroomUnmet},
		"bedroom no table": {func(f *TitleClaimFacts) { f.BedroomPieces[0].Pieces = f.BedroomPieces[0].Pieces[:1] }, ClaimBedroomUnmet},
		"throne unbuilt":   {func(f *TitleClaimFacts) { f.Built = nil }, ClaimThroneUnmet},
		"throne plain":     {func(f *TitleClaimFacts) { f.Impressiveness["r1"] = 54 }, ClaimThroneUnmet},
		"throne unread":    {func(f *TitleClaimFacts) { f.Impressiveness = nil }, ClaimUnknown},
		"favor unread":     {func(f *TitleClaimFacts) { f.Royalty.Holders["Alice"] = []RoyalHolding{{Title: "Yeoman"}} }, ClaimUnknown},
		"no ladder":        {func(f *TitleClaimFacts) { f.Royalty.Ladder = nil }, ClaimUnknown},
	} {
		f := claimFixture(t)
		c.edit(&f)
		if got := NextTitleClaim(f); got.Claim || got.Reason != c.reason {
			t.Errorf("%s: %+v", name, got)
		}
	}
}

func TestTitleClaimTopOfTheLadderHasNothingToClaim(t *testing.T) {
	f := claimFixture(t)
	f.Royalty.Holders["Alice"] = royalHolder("Baron", 99)
	if got := NextTitleClaim(f); got.Claim || got.Reason != ClaimNoFavor {
		t.Fatalf("%+v", got)
	}
}
