package observation

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func personalProjection(pawns ...policy.WorkPawn) ColonyProjection {
	var p ColonyProjection
	p.WorkPawns = domain.Known(pawns)
	p.Facts.Wealth = domain.Known(policy.WealthFacts{Items: 6000, Buildings: 4000, Pawns: 900, Total: 10900})
	p.Facts.Sleeping = domain.Known(policy.SleepingObservation{Rooms: domain.Known([]policy.UpkeepRoom{})})
	return p
}

func unarmedRows(ids ...string) *o.PawnSnapshot {
	s := &o.PawnSnapshot{}
	for _, id := range ids {
		s.Pawns = append(s.Pawns, &o.PawnState{Pawn: &o.EntityRef{Id: proto.String(id)}, Equipment: &o.PawnEquipment{Armed: proto.Bool(false)}})
	}
	return s
}

func personalWorkPawn(id policy.PawnID, medicine int) policy.WorkPawn {
	return policy.WorkPawn{ID: id, Skills: domain.Known([]policy.WorkSkill{{Name: "Medicine", Level: medicine}}), Incapable: domain.Known([]policy.WorkType{})}
}

func TestPersonalSharesKnownAndWeighted(t *testing.T) {
	p := personalProjection(personalWorkPawn("a", 8), personalWorkPawn("b", 2))
	p.Facts.Gear = domain.Known(policy.GearObservation{Pawns: []policy.GearPawn{
		{Pawn: "a", LoadoutModel: domain.Known(policy.GearLoadoutInput{Worn: []policy.GearOption{{Definition: "Apparel_Parka", Quality: 2, Condition: 1, Cost: 100}}})},
		{Pawn: "b", LoadoutModel: domain.Known(policy.GearLoadoutInput{})},
	}})
	personalShares(&p, bridge.RoutineFrame{}, unarmedRows("a", "b"))
	// Pool 10000, f 0.2, a is the one doctor (weight 1.25) against b (1).
	a, b := p.PersonalShareOf("a"), p.PersonalShareOf("b")
	if share, _ := a.Share.Value(); share != 0.2*10000*1.25/2.25 {
		t.Fatal(share)
	}
	if share, _ := b.Share.Value(); share != 0.2*10000/2.25 {
		t.Fatal(share)
	}
	if spent, ok := a.Spent.Value(); !ok || spent != 100 {
		t.Fatal(a)
	}
	if remaining, ok := a.Remaining.Value(); !ok || remaining != 0.2*10000*1.25/2.25-100 {
		t.Fatal(a)
	}
	if spent, ok := b.Spent.Value(); !ok || spent != 0 {
		t.Fatal(b)
	}
}

func TestPersonalSharesUnknownInputsStayUnknown(t *testing.T) {
	// No gear census: spent is unknown, so remaining is and a charged upgrade
	// is refused while a necessity passes.
	p := personalProjection(personalWorkPawn("a", 0))
	personalShares(&p, bridge.RoutineFrame{}, unarmedRows("a"))
	s := p.PersonalShareOf("a")
	if _, ok := s.Share.Value(); !ok {
		t.Fatal("share should be known from the pool")
	}
	if _, ok := s.Spent.Value(); ok {
		t.Fatal(s)
	}
	if s.Allows(1) || !s.Allows(0) {
		t.Fatal(s)
	}
	// An unknown wealth fact leaves the share unknown too.
	q := personalProjection(personalWorkPawn("a", 0))
	q.Facts.Wealth = domain.Unknown[policy.WealthFacts]()
	q.Facts.Gear = domain.Known(policy.GearObservation{Pawns: []policy.GearPawn{{Pawn: "a", LoadoutModel: domain.Known(policy.GearLoadoutInput{})}}})
	personalShares(&q, bridge.RoutineFrame{}, unarmedRows("a"))
	if _, ok := q.PersonalShareOf("a").Share.Value(); ok {
		t.Fatal("share from an unknown pool")
	}
	// An unread sleeping census leaves spent unknown for everyone.
	r := personalProjection(personalWorkPawn("a", 0))
	r.Facts.Sleeping = domain.Unknown[policy.SleepingObservation]()
	r.Facts.Gear = q.Facts.Gear
	personalShares(&r, bridge.RoutineFrame{}, unarmedRows("a"))
	if _, ok := r.PersonalShareOf("a").Spent.Value(); ok {
		t.Fatal("spent without a sleeping census")
	}
}

func TestPersonalShareOfMissingIsNecessitiesOnly(t *testing.T) {
	var p ColonyProjection
	s := p.PersonalShareOf("nobody")
	if _, ok := s.Remaining.Value(); ok || s.Allows(1) || !s.Allows(0) {
		t.Fatal(s)
	}
	// A slave on the roster has a zero share and no spent, so only necessities pass.
	q := personalProjection(personalWorkPawn("a", 0), personalWorkPawn("s", 0))
	sleeping, _ := q.Facts.Sleeping.Value()
	sleeping.Slaves = []policy.SleepingPerson{{ID: "s"}}
	q.Facts.Sleeping = domain.Known(sleeping)
	q.Facts.Gear = domain.Known(policy.GearObservation{Pawns: []policy.GearPawn{{Pawn: "a", LoadoutModel: domain.Known(policy.GearLoadoutInput{})}, {Pawn: "s", LoadoutModel: domain.Known(policy.GearLoadoutInput{})}}})
	personalShares(&q, bridge.RoutineFrame{}, unarmedRows("a", "s"))
	if share, ok := q.PersonalShareOf("s").Share.Value(); !ok || share != 0 {
		t.Fatal("a slave's share is a known zero")
	}
	if share, _ := q.PersonalShareOf("a").Share.Value(); share != 0.2*10000 {
		t.Fatal(share)
	}
}
