package observation

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func ledgerState(id string, traits ...string) *o.PawnState {
	bio := &o.PawnBiography{}
	for _, t := range traits {
		bio.Traits = append(bio.Traits, &o.Trait{DefName: proto.String(t)})
	}
	return &o.PawnState{Pawn: &o.EntityRef{Id: proto.String(id)}, Biography: bio, Settings: &o.PawnSettings{PolicyInputs: &o.PawnPolicyInputs{}}}
}

// The thoughts of the pawn recorded in policy/testdata/mood-provision-pressured.json
// (inlined: the snapshot codec imports this package) go through the production
// ledger path with a trait-immune twin, a pawn whose biography was unread and
// a pawn whose thoughts were unreadable.
func TestRoundsMoodLedgerFromRecordedSnapshot(t *testing.T) {
	recorded := policy.MoodPawn{ID: "Thing_Human1023", Thoughts: domain.Known([]policy.MoodThought{{Def: "NeedJoy", Offset: -10}, {Def: "EnvironmentCold", Offset: -4}, {Def: "SleptOutside", Offset: -4}})}
	twin, blind, lost := recorded, recorded, recorded
	twin.ID, blind.ID, lost.ID = "twin", "blind", "lost"
	lost.Thoughts = domain.Unknown[[]policy.MoodThought]()
	pawns := &o.PawnSnapshot{Pawns: []*o.PawnState{ledgerState(string(recorded.ID)), ledgerState("twin", "Tough"), {Pawn: &o.EntityRef{Id: proto.String("blind")}}, ledgerState("lost")}}
	facts := map[string]policy.ThoughtFacts{
		"NeedJoy":         {Def: "NeedJoy"},
		"EnvironmentCold": {Def: "EnvironmentCold", NullifyingTraits: []string{"Tough"}},
	}
	got, known := roundsMoodLedger(domain.Known([]policy.MoodPawn{recorded, twin, blind, lost}), pawns, facts).Value()
	if !known {
		t.Fatal("ledger unknown for a known census")
	}
	if got.UnknownPawns != 1 {
		t.Fatalf("unreadable thoughts: %+v", got)
	}
	by := map[string]policy.MoodLedgerSource{}
	var order []string
	for _, s := range got.Sources {
		by[s.Def] = s
		order = append(order, s.Def)
	}
	if want := []string{"NeedJoy", "SleptOutside", "EnvironmentCold"}; len(order) != 3 || order[0] != want[0] || order[1] != want[1] || order[2] != want[2] {
		t.Fatalf("ranking = %v, want %v", order, want)
	}
	if s := by["NeedJoy"]; s.Pawns != 3 || s.Lost != -30 || s.Unverified != 0 {
		t.Fatalf("NeedJoy = %+v (no gating attribute needed)", s)
	}
	if s := by["EnvironmentCold"]; s.Pawns != 2 || s.Unverified != 1 {
		t.Fatalf("EnvironmentCold = %+v (immune twin dropped, unread pawn kept unverified)", s)
	}
	if s := by["SleptOutside"]; s.Pawns != 3 || s.Unverified != 3 {
		t.Fatalf("SleptOutside has no facts, every pawn unverified: %+v", s)
	}
	for _, u := range got.Unowned {
		if row, ok := policy.ThoughtAuditRow(u.Def); ok && row.Owner != "" {
			t.Fatalf("owned def in unowned bucket: %+v", u)
		}
	}
	if _, known = roundsMoodLedger(domain.Unknown[[]policy.MoodPawn](), pawns, facts).Value(); known {
		t.Fatal("unknown census produced a ledger")
	}
}
