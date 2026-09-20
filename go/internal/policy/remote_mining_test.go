package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"testing"
)

func TestSurfaceMiningReachDemandAndSafety(t *testing.T) {
	reach := tribal8Reach()
	reach.Armed, reach.FreeHaulers = domain.Known(int64(6)), domain.Known(int64(3))
	ore := ResourceSource{ThingID: "ore", Method: ResourceSourceMine, Yield: 40, Distance: 80,
		Cell: domain.Cell{X: 90, Z: 90}, Safety: "open_surface", Reachable: domain.Known(true)}
	for _, tt := range []struct {
		name  string
		stock int64
		edit  func(*ResourceSource, *ResourceReachRequest)
		want  int
	}{
		{"far deposit while demand open", 0, nil, 1},
		{"one unit needs at most one rock", 39, nil, 1},
		{"satisfied", 40, nil, 0},
		{"surplus", 50, nil, 0},
		{"mountain interior without safe route", 0, func(s *ResourceSource, _ *ResourceReachRequest) { s.Reachable = domain.Known(false) }, 0},
		{"unknown route", 0, func(s *ResourceSource, _ *ResourceReachRequest) { s.Reachable = domain.Unknown[bool]() }, 0},
		{"roof support", 0, func(s *ResourceSource, _ *ResourceReachRequest) { s.Safety = "roofed" }, 0},
		{"foreign designation covers demand", 0, func(s *ResourceSource, _ *ResourceReachRequest) { s.Designated = true }, 0},
		{"outside base reach", 0, func(_ *ResourceSource, r *ResourceReachRequest) { r.Armed = domain.Known(int64(0)) }, 0},
	} {
		t.Run(tt.name, func(t *testing.T) {
			s, r := ore, reach
			if tt.edit != nil {
				tt.edit(&s, &r)
			}
			other := ore
			other.ThingID = "other"
			if tt.want == 0 && !s.Designated {
				other = s
				other.ThingID = "other"
			}
			got, _ := SelectReachableResourceSources([]ResourceSource{s, other}, 40, tt.stock, RemoteWorkRequest{Reach: r})
			if len(got) != tt.want {
				t.Fatalf("selected %v, want %d", got, tt.want)
			}
		})
	}
}
