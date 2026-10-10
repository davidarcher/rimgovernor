package httpapi

import (
	"context"
	"encoding/json"
	"net/http"
	"strings"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

type ledgerFunc func() (policy.LedgerView, bool)

func (f ledgerFunc) WorkLedger() (policy.LedgerView, bool) { return f() }

func ledgerServer(t *testing.T, ledger LedgerProvider) string {
	t.Helper()
	s, err := New(Config{ReadTimeout: time.Second, ShutdownTimeout: time.Second, MaxResponseBytes: 1 << 20, Ledger: ledger},
		snapshotFunc(func(context.Context) (Snapshot, error) { return Snapshot{}, nil }), planFunc(unavailablePlan))
	if err != nil {
		t.Fatal(err)
	}
	return testHTTP(t, s).URL
}

func TestLedgerRouteServesTheViewReadOnly(t *testing.T) {
	view := policy.NewLedgerView(policy.LedgerViewReconciled, 42)
	view.Orders = append(view.Orders, policy.LedgerOrderView{Recipe: "Make_Vest", State: policy.OrderPlaced, Benches: []string{"Bench_1"}})
	url := ledgerServer(t, ledgerFunc(func() (policy.LedgerView, bool) { return view, true }))
	status, body := get(t, url+"/api/ledger")
	var got policy.LedgerView
	if err := json.Unmarshal(body, &got); err != nil {
		t.Fatal(err)
	}
	if status != 200 || got.Tick != 42 || len(got.Orders) != 1 || got.Orders[0].Benches[0] != "Bench_1" || !strings.Contains(string(body), `"orphans":[]`) {
		t.Fatalf("%d %s", status, body)
	}
	for _, method := range []string{http.MethodPost, http.MethodPut, http.MethodDelete} {
		req, _ := http.NewRequest(method, url+"/api/ledger", nil)
		resp, err := http.DefaultClient.Do(req)
		if err != nil {
			t.Fatal(err)
		}
		resp.Body.Close()
		if resp.StatusCode != 405 {
			t.Fatalf("%s /api/ledger = %d, want 405", method, resp.StatusCode)
		}
	}
	if status, _ := get(t, url+"/api/ledger?x=1"); status != 400 {
		t.Fatalf("query accepted: %d", status)
	}
}

func TestLedgerRouteNotServedWithoutRounds(t *testing.T) {
	if status, _ := get(t, ledgerServer(t, nil)+"/api/ledger"); status != 404 {
		t.Fatalf("no provider: %d", status)
	}
	url := ledgerServer(t, ledgerFunc(func() (policy.LedgerView, bool) { return policy.LedgerView{}, false }))
	if status, _ := get(t, url+"/api/ledger"); status != 404 {
		t.Fatalf("no rounds: %d", status)
	}
}
