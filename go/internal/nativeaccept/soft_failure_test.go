package nativeaccept

import (
	"errors"
	"strings"
	"testing"
)

func TestExpectRecordsEveryFailureAndPassesNil(t *testing.T) {
	r := NewReport("x", true)
	if !r.Expect("fine", nil) {
		t.Fatal("a nil error reported as a failure")
	}
	if r.SoftFailure() != nil {
		t.Fatalf("nothing failed yet: %v", r.SoftFailure())
	}
	r.Expect("access", errors.New("lost a cell"))
	r.Expect("cover", errors.New("no line"))
	got := r.SoftFailure()
	if got == nil || !strings.Contains(got.Error(), "access: lost a cell") || !strings.Contains(got.Error(), "cover: no line") {
		t.Fatalf("both failures belong in one error: %v", got)
	}
}
