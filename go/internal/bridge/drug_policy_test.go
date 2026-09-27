package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestBeerReserveBillOperation(t *testing.T) {
	bill, err := domain.NewProductionBill("brewery", "Make_Wort", "before", domain.BeerReserve, 12)
	if err != nil || !BillOperation(bill).GetAddBill().GetSettings().GetBeerReserve() {
		t.Fatal(bill, err)
	}
}
