package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func known[T any](f domain.Fact[T]) bool { _, ok := f.Value(); return ok }

func readFailed(field string) []*o.ReadIssue {
	return []*o.ReadIssue{{Field: proto.String(field), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_READ_FAILED.Enum()}}}
}

// TestPawnGeneLifecycle: each gene-lifecycle field round-trips to its
// typed fact, a known zero stays known, an absent field or a named read issue
// stays unknown, and malformed values are refused.
func TestPawnGeneLifecycle(t *testing.T) {
	b := biotechPawnFixture()
	b.XenogermRegrowTicksLeft, b.XenogermComaTicksLeft, b.InExtractor = proto.Int32(6000000), proto.Int32(0), proto.Bool(true)
	b.Extractable, b.ExtractableReason = proto.Bool(false), proto.String("In xenogermination coma")
	if err := validatePawnBiotech(b); err != nil {
		t.Fatal(err)
	}
	got, _ := PawnBiotech(b).Value()
	if v, ok := got.XenogermRegrowTicksLeft.Value(); !ok || v != 6000000 {
		t.Fatal("regrow", v, ok)
	}
	if v, ok := got.XenogermComaTicksLeft.Value(); !ok || v != 0 {
		t.Fatal("known zero coma lost", v, ok)
	}
	if v, ok := got.InExtractor.Value(); !ok || !v {
		t.Fatal("in extractor", v, ok)
	}
	if v, ok := got.Extractable.Value(); !ok || v {
		t.Fatal("extractable verdict", v, ok)
	}
	if v, ok := got.ExtractableReason.Value(); !ok || v != "In xenogermination coma" {
		t.Fatal("reason", v, ok)
	}

	// An accepting verdict has a known empty reason.
	b.Extractable, b.ExtractableReason = proto.Bool(true), nil
	got, _ = PawnBiotech(b).Value()
	if v, ok := got.Extractable.Value(); !ok || !v {
		t.Fatal("accepted verdict", v, ok)
	}
	if v, ok := got.ExtractableReason.Value(); !ok || v != "" {
		t.Fatal("accepted pawn reason", v, ok)
	}

	none, _ := PawnBiotech(biotechPawnFixture()).Value()
	for name, k := range map[string]bool{"regrow": known(none.XenogermRegrowTicksLeft), "coma": known(none.XenogermComaTicksLeft), "in extractor": known(none.InExtractor),
		"extractable": known(none.Extractable), "reason": known(none.ExtractableReason)} {
		if k {
			t.Errorf("absent %s became known", name)
		}
	}
	failed := biotechPawnFixture()
	failed.XenogermRegrowTicksLeft = proto.Int32(5)
	failed.Issues = readFailed("gene_lifecycle")
	if err := validatePawnBiotech(failed); err != nil {
		t.Fatal(err)
	}
	if got, _ := PawnBiotech(failed).Value(); known(got.XenogermRegrowTicksLeft) || known(got.InExtractor) {
		t.Fatal("a failed lifecycle read must stay unknown")
	}
	for name, mutate := range map[string]func(*o.PawnBiotech){
		"negative regrow":      func(v *o.PawnBiotech) { v.XenogermRegrowTicksLeft = proto.Int32(-1) },
		"negative coma":        func(v *o.PawnBiotech) { v.XenogermComaTicksLeft = proto.Int32(-1) },
		"reason on acceptance": func(v *o.PawnBiotech) { v.Extractable, v.ExtractableReason = proto.Bool(true), proto.String("x") },
		"reason without value": func(v *o.PawnBiotech) { v.Extractable, v.ExtractableReason = nil, proto.String("x") },
		"non-ascii reason":     func(v *o.PawnBiotech) { v.Extractable, v.ExtractableReason = proto.Bool(false), proto.String("é") },
		"verdict and issue": func(v *o.PawnBiotech) {
			v.Extractable = proto.Bool(true)
			v.Issues = readFailed("extractable")
		},
	} {
		v := biotechPawnFixture()
		mutate(v)
		if validatePawnBiotech(v) == nil {
			t.Errorf("%s accepted", name)
		}
	}
}
