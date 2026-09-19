package policy

import (
	"fmt"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestHomeCoverageRestoresLegacyExcludedCells(t *testing.T) {
	r := homeCoverageRequest(t)
	r.Facts.Excluded = r.Facts.Missing
	if d := EvaluateHomeCoverage(r); !d.Admitted {
		t.Fatal("autonomous coverage must restore all missing cells", d)
	}
}

func TestHomeCoverageFindsWorkAfterBlockedPrefixAndRenewsOnEdit(t *testing.T) {
	rows := []HomeCoverageTarget{}
	for i := 0; i < 12; i++ {
		rows = append(rows, HomeCoverageTarget{ID: fmt.Sprintf("blocked-%02d", i), Blocker: "geometry unavailable"})
	}
	rows = append(rows, HomeCoverageTarget{ID: "owned", Shape: domain.Known("batch"), Missing: domain.Known(int64(256))})
	first, err := SelectHomeCoverageMethod(domain.Known(rows), 10, nil)
	if err != nil || first.Kind != HomeCoverageExtend || first.Target != "owned" {
		t.Fatal(first, err)
	}
	same, err := SelectHomeCoverageMethod(domain.Known(rows), 10, []domain.MethodID{first.ID})
	if err != nil || same.Kind != HomeCoverageBlocked {
		t.Fatal(same, err)
	}
	next, err := SelectHomeCoverageMethod(domain.Known(rows), 11, []domain.MethodID{first.ID})
	if err != nil || next.Kind != HomeCoverageExtend || next.ID == first.ID {
		t.Fatal(next, err)
	}
}
