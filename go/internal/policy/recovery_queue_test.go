package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func recoveryThing(id string, x int32, yields ...CandidateYield) RecoveryThing {
	return RecoveryThing{
		ID: id, Kind: RemoteSalvage, Cell: domain.Cell{X: x}, Evidenced: true, RouteSafe: domain.Known(true),
		Candidate: SupplyCandidate{Kind: CandidateSalvage, ID: id, Yields: yields},
	}
}

func recoveryYield(def Resource, n, headroom int64) CandidateYield {
	return SourceYield(ResourceKey{Def: def}, n, 0, domain.Known(headroom))
}

func recoveryOrder(q RecoveryQueue) []string {
	var ids []string
	for _, e := range q.Entries {
		ids = append(ids, e.ID)
	}
	return ids
}

func TestRankRecoveryTiersThenDistance(t *testing.T) {
	steel, gold := recoveryYield("Steel", 10, 100), recoveryYield("Gold", 1, 100)
	room := recoveryThing("room_far", 90, gold)
	room.RoomObstruction = true
	r := RecoveryRequest{
		Slot:  true,
		Short: map[Resource]bool{"Steel": true},
		Things: []RecoveryThing{
			recoveryThing("rest_near", 1, gold),
			recoveryThing("short_far", 50, steel),
			room,
			recoveryThing("short_near", 20, steel),
			recoveryThing("rest_far", 5, gold),
		},
	}
	q := RankRecovery(r)
	want := []string{"room_far", "short_near", "short_far", "rest_near", "rest_far"}
	if got := recoveryOrder(q); !slicesEqual(got, want) {
		t.Fatalf("order = %v, want %v", got, want)
	}
	if q.Admitted != "room_far" || q.Entries[0].Status != RecoveryAdmitted || q.Entries[1].Status != RecoveryQueued {
		t.Fatalf("admission = %+v", q)
	}
}

func slicesEqual(a, b []string) bool {
	if len(a) != len(b) {
		return false
	}
	for i := range a {
		if a[i] != b[i] {
			return false
		}
	}
	return true
}

func TestRankRecoveryDistanceUsesNearestOriginThenCenter(t *testing.T) {
	gold := recoveryYield("Gold", 1, 10)
	r := RecoveryRequest{Slot: true, Origins: []domain.Cell{{X: 0}, {X: 100}}, Things: []RecoveryThing{recoveryThing("a", 90, gold), recoveryThing("b", 30, gold)}}
	if got := recoveryOrder(RankRecovery(r)); got[0] != "a" {
		t.Fatalf("nearest-origin order = %v", got)
	}
	r.Origins, r.Center = nil, domain.Cell{X: 40}
	if got := recoveryOrder(RankRecovery(r)); got[0] != "b" {
		t.Fatalf("center order = %v", got)
	}
	near := recoveryThing("path_near", 500, gold)
	near.Candidate.PathDistance = domain.Known(1.0)
	r.Things = append(r.Things, near)
	if got := recoveryOrder(RankRecovery(r)); got[0] != "path_near" {
		t.Fatalf("native path distance order = %v", got)
	}
}

func TestRankRecoveryOneShortYieldIsTier2(t *testing.T) {
	r := RecoveryRequest{Slot: true, Short: map[Resource]bool{"Steel": true}, Things: []RecoveryThing{
		recoveryThing("multi", 50, recoveryYield("Gold", 3, 9), recoveryYield("Steel", 2, 9)),
		recoveryThing("covered", 1, recoveryYield("Gold", 3, 9)),
	}}
	q := RankRecovery(r)
	if q.Entries[0].ID != "multi" || q.Entries[0].Tier != RecoveryShortYield || len(q.Entries[0].Short) != 1 || q.Entries[0].Short[0] != "Steel" {
		t.Fatalf("entries = %+v", q.Entries)
	}
}

func TestRankRecoveryCoveredResourceIsNotHeld(t *testing.T) {
	r := RecoveryRequest{Slot: true, Things: []RecoveryThing{recoveryThing("gold", 1, recoveryYield("Gold", 3, 9))}}
	q := RankRecovery(r)
	if e := q.Entries[0]; e.Status != RecoveryAdmitted || e.Tier != RecoveryRest || e.Reason != "" {
		t.Fatalf("covered resource = %+v", e)
	}
}

func TestRankRecoveryHoldReasons(t *testing.T) {
	gold := recoveryYield("Gold", 1, 9)
	cases := []struct {
		name   string
		mutate func(*RecoveryRequest, *RecoveryThing)
		want   string
	}{
		{"threat", func(r *RecoveryRequest, _ *RecoveryThing) { r.Threat = domain.Known(true) }, RemoteHoldThreat},
		{"route", func(_ *RecoveryRequest, t *RecoveryThing) { t.RouteSafe = domain.Known(false) }, RemoteHoldRouteUnsafe},
		{"route unknown", func(_ *RecoveryRequest, t *RecoveryThing) { t.RouteSafe = domain.Unknown[bool]() }, RemoteHoldRouteUnsafe},
		{"urgent", func(r *RecoveryRequest, _ *RecoveryThing) { r.Urgent = domain.Known(true) }, RemoteHoldUrgentWork},
		{"no evidence", func(_ *RecoveryRequest, t *RecoveryThing) { t.Evidenced = false }, "salvage_unknown"},
		{"ancient", func(_ *RecoveryRequest, t *RecoveryThing) {
			*t, _ = RecoveryClearanceThing(ClearanceTarget{EntityID: "x", Deconstructible: true, AncientDanger: true, Salvage: &SalvageEvidence{Safe: domain.Known(true)}}, false)
		}, "ancient_danger"},
		{"casket", func(_ *RecoveryRequest, t *RecoveryThing) {
			*t, _ = RecoveryClearanceThing(ClearanceTarget{EntityID: "x", Deconstructible: true, Class: "ancient_casket", Salvage: &SalvageEvidence{Safe: domain.Known(true)}}, false)
		}, "casket"},
		{"not deconstructible", func(_ *RecoveryRequest, t *RecoveryThing) {
			*t, _ = RecoveryClearanceThing(ClearanceTarget{EntityID: "x", Salvage: &SalvageEvidence{Safe: domain.Known(true)}}, false)
		}, "not_deconstructible"},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			r := RecoveryRequest{Slot: true}
			thing := recoveryThing("x", 1, gold)
			c.mutate(&r, &thing)
			r.Things = []RecoveryThing{thing}
			e := RankRecovery(r).Entries[0]
			if e.Status != RecoveryHeld || e.Reason != c.want {
				t.Fatalf("entry = %+v, want held %s", e, c.want)
			}
		})
	}
}

func TestRankRecoveryHomeAndRemoteRuinsAreOneQueue(t *testing.T) {
	ok := &SalvageEvidence{Safe: domain.Known(true)}
	home, _ := RecoveryClearanceThing(ClearanceTarget{EntityID: "home", InHome: true, Deconstructible: true, Salvage: ok}, false)
	away, _ := RecoveryClearanceThing(ClearanceTarget{EntityID: "away", Deconstructible: true, Salvage: ok}, false)
	if _, own := RecoveryClearanceThing(ClearanceTarget{EntityID: "wall", Player: true}, false); own {
		t.Fatal("a player building is no recovery thing")
	}
	for _, thing := range []RecoveryThing{home, away} {
		if thing.Hold != "" {
			t.Fatalf("%s hold = %q", thing.ID, thing.Hold)
		}
	}
}

func TestRankRecoveryThreatWinsOverThingHold(t *testing.T) {
	thing := recoveryThing("x", 1)
	thing.Hold = "casket"
	e := RankRecovery(RecoveryRequest{Slot: true, Threat: domain.Known(true), Things: []RecoveryThing{thing}}).Entries[0]
	if e.Reason != RemoteHoldThreat {
		t.Fatalf("reason = %s", e.Reason)
	}
}

func TestRankRecoveryLaborExhaustedDefersNotDrops(t *testing.T) {
	r := RecoveryRequest{Things: []RecoveryThing{recoveryThing("a", 1, recoveryYield("Gold", 1, 9)), recoveryThing("b", 2, recoveryYield("Gold", 1, 9))}}
	q := RankRecovery(r)
	if q.Admitted != "" || len(q.Entries) != 2 {
		t.Fatalf("queue = %+v", q)
	}
	for _, e := range q.Entries {
		if e.Status != RecoveryDeferred || e.Reason != RecoveryReasonLaborExhausted {
			t.Fatalf("entry = %+v", e)
		}
	}
	r.Slot = true
	if q = RankRecovery(r); q.Admitted != "a" || q.Entries[1].Status != RecoveryQueued {
		t.Fatalf("with a slot = %+v", q)
	}
}

func TestRankRecoveryMissingStorageThrottlesOnly(t *testing.T) {
	r := RecoveryRequest{Slot: true, Things: []RecoveryThing{
		recoveryThing("full", 1, recoveryYield("Gold", 1, 0)),
		recoveryThing("roomy", 2, recoveryYield("Gold", 1, 9)),
		recoveryThing("nothing", 3),
	}}
	q := RankRecovery(r)
	if e := q.Entries[0]; e.ID != "full" || e.Status != RecoveryDeferred || e.Reason != RemoteHoldMissingStorage {
		t.Fatalf("throttled = %+v", e)
	}
	if q.Admitted != "roomy" {
		t.Fatalf("admitted = %q", q.Admitted)
	}
}

func TestRecoveryLootThing(t *testing.T) {
	row := LootItem{Supply: StartingSupply{Thing: "Thing_Gold1", Definition: "Gold"}, Forbidden: true, SafetyKnown: true, SafeToHaul: true, Count: 4, StorageHeadroom: domain.Known(int64(9))}
	thing, ok := RecoveryLootThing(row)
	if !ok || thing.Kind != RemoteLoot || thing.Hold != "" {
		t.Fatalf("thing = %+v", thing)
	}
	q := RankRecovery(RecoveryRequest{Slot: true, Short: map[Resource]bool{"Gold": true}, Things: []RecoveryThing{thing}})
	if q.Entries[0].Tier != RecoveryShortYield || q.Admitted != "Thing_Gold1" {
		t.Fatalf("queue = %+v", q)
	}
	row.SpawnForbidden = true
	if _, ok := RecoveryLootThing(row); ok {
		t.Fatal("a spawner-forbidden stack is never recovery")
	}
}

// A native per-building roof verdict no longer holds a ruin (#2301): the
// mirror's joint check decides in PlanRecoveryBatch, roofs first.
func TestRecoveryClearanceThingIgnoresNativeRoofBlocker(t *testing.T) {
	row := ClearanceTarget{EntityID: "x", Deconstructible: true, RoofBlocker: "unsupported", Salvage: &SalvageEvidence{Safe: domain.Known(true)}}
	thing, ok := RecoveryClearanceThing(row, false)
	if !ok || thing.Hold != "" {
		t.Fatalf("thing %+v ok %v, want no hold", thing, ok)
	}
	row.AncientDanger = true
	if thing, _ = RecoveryClearanceThing(row, false); thing.Hold != "ancient_danger" {
		t.Fatalf("hold %q, want ancient_danger", thing.Hold)
	}
}
