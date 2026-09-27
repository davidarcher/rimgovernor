package melee

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestWriteMeleeSendsTheIntent(t *testing.T) {
	b, f, p := NewFixture(t)
	receipt, err := b.WriteMelee(context.Background(), p)
	if err != nil || receipt.Kind != domain.ReceiptAccepted || f.Applies != 1 {
		t.Fatal(receipt, err)
	}
	m := f.Last.GetMelee()
	if f.Last.GetKey() != "attack/1" || m.GetPawnId() != "pawn" || m.GetTargetId() != "target" || !m.GetSubdue() {
		t.Fatal(f.Last)
	}
	f.Refuse = true
	if receipt, err = b.WriteMelee(context.Background(), p); err != nil || receipt.Kind != domain.ReceiptRefused {
		t.Fatal(receipt, err)
	}
}
