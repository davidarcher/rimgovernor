package bridge

import (
	"errors"
	"fmt"
	"testing"
)

func TestNotMirroredNamesClassAndFact(t *testing.T) {
	err := fmt.Errorf("evaluate MarketValue: %w", &NotMirrored{Class: "StatPart_Quality", Fact: "quality factor"})
	var nm *NotMirrored
	if !errors.As(err, &nm) || nm.Class != "StatPart_Quality" || nm.Fact != "quality factor" {
		t.Fatalf("errors.As = %v, %+v", errors.As(err, &nm), nm)
	}
	want := "stat class StatPart_Quality: quality factor is computed in game code and not mirrored"
	if nm.Error() != want {
		t.Errorf("Error() = %q, want %q", nm.Error(), want)
	}
}
