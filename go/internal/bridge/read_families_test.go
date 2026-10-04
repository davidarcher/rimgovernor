package bridge

import (
	"context"
	"testing"
)

// Every reviewed native read is either a read of one fact family or
// explicitly not a fact read, so a new read method cannot slip past the
// recording facts reader unclassified (#1916).
func TestEveryNativeReadIsClassified(t *testing.T) {
	t.Parallel()
	for name := range reviewedNativeMethods {
		if !nativeReadMethod(name) {
			continue
		}
		_, family := nativeReadFamily[name]
		if family == nonFactReads[name] {
			t.Errorf("%s: classified as family=%v nonFact=%v, want exactly one", name, family, nonFactReads[name])
		}
	}
	for name, family := range nativeReadFamily {
		known := false
		for _, f := range FactFamilies() {
			known = known || f == family
		}
		if !known || !reviewedNativeMethods[name] {
			t.Errorf("%s -> %q is not a reviewed read of a known family", name, family)
		}
	}
}

func TestNoteReadReachesContextNote(t *testing.T) {
	t.Parallel()
	var got []FactFamily
	ctx := WithReadNote(context.Background(), func(f FactFamily, source string) { got = append(got, f) })
	noteNativeRead(ctx, "rimgovernor/observations_read_status")
	noteNativeRead(ctx, "rimgovernor/clock_read_status")
	NoteRead(context.Background(), FactPawns, "no note on the context")
	if len(got) != 1 || got[0] != FactEmergency {
		t.Fatalf("noted %v, want [emergency]", got)
	}
}
