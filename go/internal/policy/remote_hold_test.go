package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func remoteReadyReach() ResourceReachRequest {
	r := tribal8Reach()
	r.Armed, r.FreeHaulers = domain.Known(int64(6)), domain.Known(int64(3))
	return r
}

func remoteSalvageRow() ClearanceTarget {
	return ClearanceTarget{EntityID: "ruin", DefName: "Battery", Minimum: domain.Cell{X: 95, Z: 95}, Maximum: domain.Cell{X: 95, Z: 95}, Deconstructible: true,
		Salvage: &SalvageEvidence{Safe: domain.Known(true), Candidate: AcquisitionCandidate{PathDistance: domain.Known(120.0), Labor: domain.Known(100.0), NeedsHaul: true, UnitsPerTrip: 75,
			Yields: []AcquisitionYield{{ResourceQuantity: ResourceQuantity{Key: ResourceKey{Def: "Steel"}, Count: 35}, Headroom: domain.Known(int64(75))}}}}}
}

// Every explicit hold reason, for each remote kind, from the same request
// edits: a threat outranks the route verdict it causes, urgent work outranks
// the reach stage, and roof, route and storage each name themselves.
func TestRemoteWorkHoldsAreExplicitAcrossKinds(t *testing.T) {
	urgent := AcquisitionCompetition{UrgentPriority: UrgentWorkPriority}
	for _, tt := range []struct {
		name    string
		request func(*RemoteWorkRequest)
		loot    func(*LootItem)
		salvage func(*ClearanceTarget)
		mining  func(*ResourceSource)
		reason  string
	}{
		{"threat", func(r *RemoteWorkRequest) { r.Reach.Threat = domain.Known(true) },
			func(l *LootItem) { l.SafeToHaul = false; l.Forbidden = true }, // an unsafe stack is #336's forbid, not a reach hold
			func(c *ClearanceTarget) { c.Salvage.Safe = domain.Known(false) },
			func(s *ResourceSource) { s.Reachable = domain.Known(false) }, RemoteHoldThreat},
		{"urgent work", func(r *RemoteWorkRequest) { r.Competition = urgent }, nil, nil, nil, RemoteHoldUrgentWork},
		{"roof support", nil, nil, func(c *ClearanceTarget) { c.RoofBlocker = "collapse" }, func(s *ResourceSource) { s.Safety = "roofed" }, RemoteHoldRoofSupport},
		{"route", nil, nil, func(c *ClearanceTarget) { c.Salvage.Safe = domain.Known(false) }, func(s *ResourceSource) { s.Reachable = domain.Known(false) }, RemoteHoldRouteUnsafe},
		{"storage", func(r *RemoteWorkRequest) { r.Reach.StorageHeadroom = domain.Known(int64(0)) },
			func(l *LootItem) { l.StorageHeadroom = domain.Known(int64(0)) },
			func(c *ClearanceTarget) { c.Salvage.Candidate.Yields[0].Headroom = domain.Known(int64(0)) }, nil, RemoteHoldMissingStorage},
	} {
		t.Run(tt.name, func(t *testing.T) {
			r := RemoteWorkRequest{Reach: remoteReadyReach(), Demand: steelDemand()}
			if tt.request != nil {
				tt.request(&r)
			}
			if tt.loot != nil || tt.request != nil {
				row := remoteLootRow("s", domain.Cell{X: 95, Z: 95}, true, true)
				if tt.loot != nil {
					tt.loot(&row)
				}
				kept, holds, err := FilterLootReach(domain.Known([]LootItem{row}), r)
				if err != nil {
					t.Fatal(err)
				}
				rows, _ := kept.Value()
				if row.SafeToHaul {
					if len(rows) != 0 || len(holds) != 1 || holds[0].Reason != tt.reason {
						t.Fatalf("loot kept %v holds %v", rows, holds)
					}
				} else if len(rows) != 1 || len(holds) != 0 {
					t.Fatalf("unsafe loot must reach the safety review: kept %v holds %v", rows, holds)
				}
			}
			salvage := remoteSalvageRow()
			if tt.salvage != nil {
				tt.salvage(&salvage)
			}
			got, holds, err := FilterRemoteSalvage([]ClearanceTarget{salvage}, r)
			if err != nil || len(got) != 1 || got[0].SalvageSelected || len(holds) != 1 || holds[0].Reason != tt.reason {
				t.Fatalf("salvage %v holds %v err %v", got, holds, err)
			}
			ore := ResourceSource{ThingID: "ore", Method: ResourceSourceMine, Yield: 40, Distance: 80, Cell: domain.Cell{X: 90, Z: 90}, Safety: "open_surface", Reachable: domain.Known(true)}
			if tt.mining != nil {
				tt.mining(&ore)
			}
			selected, mined := SelectReachableResourceSources([]ResourceSource{ore}, 40, 0, r)
			if len(selected) != 0 || len(mined) != 1 || mined[0].Reason != tt.reason || mined[0].Target != "ore" || mined[0].Kind != RemoteMining {
				t.Fatalf("mining selected %v holds %v", selected, mined)
			}
		})
	}
}

func TestRemoteWorkClearsHoldsWhenSafe(t *testing.T) {
	r := RemoteWorkRequest{Reach: remoteReadyReach(), Demand: steelDemand()}
	got, holds, err := FilterRemoteSalvage([]ClearanceTarget{remoteSalvageRow()}, r)
	if err != nil || len(holds) != 0 || !got[0].SalvageSelected {
		t.Fatal(got, holds, err)
	}
	selected, mined := SelectReachableResourceSources([]ResourceSource{{ThingID: "ore", Method: ResourceSourceMine, Yield: 40, Distance: 80, Cell: domain.Cell{X: 90, Z: 90}, Safety: "open_surface", Reachable: domain.Known(true)}}, 40, 0, r)
	if len(selected) != 1 || len(mined) != 0 {
		t.Fatal(selected, mined)
	}
}

func TestUrgentWorkCompeting(t *testing.T) {
	var f RoutineFacts
	if _, known := UrgentWorkCompeting(f).Value(); known {
		t.Fatal("unknown patients must not decide urgency")
	}
	f.UrgentPatients = domain.Known(int64(0))
	if urgent, _ := UrgentWorkCompeting(f).Value(); urgent || RemoteCompetition(f).UrgentPriority != 0 {
		t.Fatal("no urgency")
	}
	f.UrgentPatients = domain.Known(int64(1))
	if urgent, _ := UrgentWorkCompeting(f).Value(); !urgent || RemoteCompetition(f).UrgentPriority != UrgentWorkPriority {
		t.Fatal("urgent patient")
	}
	f.UrgentPatients = domain.Known(int64(0))
	for phase, want := range map[DisasterPhase]bool{DisasterDisrupted: true, DisasterSurvival: true, DisasterRecovering: false, DisasterRestored: false} {
		f.Disaster = &DisasterHistory{Phase: phase}
		if urgent, _ := UrgentWorkCompeting(f).Value(); urgent != want {
			t.Fatal(phase, urgent)
		}
	}
}

func TestRemoteHoldReasonKeepsOtherReasons(t *testing.T) {
	for reason, want := range map[string]string{
		"outside_base:insufficient_defense": "outside_base:insufficient_defense",
		"outside_base:threat_present":       RemoteHoldThreat,
		"outside_near:no_storage":           RemoteHoldMissingStorage,
		"demand:no_demand":                  "demand:no_demand",
		"demand:no_storage_headroom":        RemoteHoldMissingStorage,
		"demand:competing_urgent_work":      RemoteHoldUrgentWork,
		"route_unknown":                     RemoteHoldRouteUnsafe,
		"":                                  "",
	} {
		if got := RemoteHoldReason(RemoteLoot, reason); got != want {
			t.Fatal(reason, got, want)
		}
	}
	if RemoteHoldReason(RemoteMining, "ineligible") != RemoteHoldRoofSupport || RemoteHoldReason(RemoteSalvage, "ineligible") != RemoteHoldRouteUnsafe {
		t.Fatal("ineligible is kind-specific")
	}
}
