package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A luciferium addict is never captured, and no downed raider is
// captured before it is stripped.
func TestCaptureWorthKeeping(t *testing.T) {
	addict := downedRaider("1", true)
	addict.Luciferium = domain.Known(true)
	clothed := downedRaider("2", true)
	clothed.WearingApparel = domain.Known(true)
	unread := downedRaider("3", true)
	unread.WearingApparel = domain.Unknown[bool]()
	for _, rows := range [][]CustodyFacts{{addict}, {clothed}, {unread}} {
		if got := SelectCustodyMethod(domain.Known(rows)); got.Reason != CustodyNoDeficit {
			t.Fatalf("%+v captured: %+v", rows[0], got)
		}
		if positive(CustodyDeficit(domain.Known(rows))) {
			t.Fatalf("%+v is a custody deficit", rows[0])
		}
	}
	worthy := downedRaider("4", false)
	worthy.Luciferium = domain.Known(false)
	if got := SelectCustodyMethod(domain.Known([]CustodyFacts{addict, clothed, worthy})); got.Pawn != "4" || got.Decision != CustodyCapture {
		t.Fatalf("got %+v, want the stripped worthy raider captured", got)
	}
}

// Every downed raider is stripped and waited on until its apparel is
// gone; a refused strip does not hold the fight.
func TestStripAddicted(t *testing.T) {
	worn, bare, unknown := domain.Known(true), domain.Known(false), domain.Unknown[bool]()
	for _, tc := range []struct {
		worn  domain.Fact[bool]
		strip StripState
		want  PostFightStep
	}{
		{worn, StripNone, PostFightStrip},
		{unknown, StripNone, PostFightStrip},
		{worn, StripOpen, PostFightWait},
		{bare, StripOpen, PostFightWait},
		{worn, StripPlaced, PostFightWait},
		{bare, StripPlaced, PostFightDone},
		{unknown, StripPlaced, PostFightDone},
		{worn, StripRefused, PostFightDone},
		{bare, StripNone, PostFightDone},
	} {
		if got := PostFightNext(tc.worn, tc.strip); got != tc.want {
			t.Fatalf("PostFightNext(%v, %v) = %v, want %v", tc.worn, tc.strip, got, tc.want)
		}
	}
	if CaptureWorthy(CombatPawnState{Downed: true, Luciferium: true}) {
		t.Fatal("a stripped addict is captured instead of finished")
	}
}
