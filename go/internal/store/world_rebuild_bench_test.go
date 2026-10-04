package store

import (
	"context"
	"encoding/json"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// BenchmarkWorldRebuild times the store half of the per-world rebuild
// (#1123/#1251) over a save holding one goal blob: goals, families and the
// rounds reset, as serve's worldRebuild.ensure runs them.
func BenchmarkWorldRebuild(b *testing.B) {
	ctx := context.Background()
	s, err := Open(ctx, filepath.Join(b.TempDir(), "rebuild.sqlite"))
	if err != nil {
		b.Fatal(err)
	}
	b.Cleanup(func() { _ = s.Close() })
	goal, err := domain.NewStandard("routine-0000000000000000-MaintainResource-0", 2, scope(), 10)
	if err != nil {
		b.Fatal(err)
	}
	if err = s.SeedStandard(ctx, goal); err != nil {
		b.Fatal(err)
	}
	first, err := s.ReviewStandard(ctx, goal.ID, 0, scope(), 10, domain.FindingUnmet)
	if err != nil {
		b.Fatal(err)
	}
	blob, err := json.Marshal(GovernorStandardBlob{SchemaVersion: GovernorStateSchemaVersion, Standard: first.Standard, Revision: first.Revision})
	if err != nil {
		b.Fatal(err)
	}
	saved := map[string]string{GovernorStandardKeyPrefix + string(first.Standard.ID): string(blob)}
	b.ResetTimer()
	for range b.N {
		if err = s.RebuildStandards(ctx, saved, nil); err != nil {
			b.Fatal(err)
		}
		if err = s.RebuildFamilies(ctx, saved); err != nil {
			b.Fatal(err)
		}
		if err = s.ResetRounds(ctx); err != nil {
			b.Fatal(err)
		}
	}
}
