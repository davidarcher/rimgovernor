package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// A restarted Rounder holds no construction demand until its first review,
// and a review's demand serves only its own world.
func TestConstructionMemoryEmptyUntilFirstReview(t *testing.T) {
	t.Parallel()
	var m constructionMemory
	world := domain.GenerationSnapshot{Colony: "colony", Native: 1}
	if got := m.get(world); len(got) != 0 {
		t.Fatalf("restart window demand %v", got)
	}
	m.set(world, map[policy.Resource]int64{"Steel": 50})
	if got := m.get(world); got["Steel"] != 50 {
		t.Fatalf("reviewed demand %v", got)
	}
	if got := m.get(domain.GenerationSnapshot{Colony: "colony", Native: 2}); len(got) != 0 {
		t.Fatalf("another world's demand %v", got)
	}
}
