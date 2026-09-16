package interpreter

import (
	"context"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/model"
	"net/http"
	"net/http/httptest"
	"testing"
	"time"
)

func TestLocalCapacityRefreshAndBudget(t *testing.T) {
	capacity := 32768
	completions := 0
	reads := 0
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path == "/api/v1/models" {
			reads++
			fmt.Fprintf(w, `{"models":[{"key":"local","loaded_instances":[{"id":"local","config":{"context_length":%d}}]}]}`, capacity)
			return
		}
		completions++
		fmt.Fprintf(w, `{"choices":[{"index":0,"message":{"role":"assistant","content":%q},"finish_reason":"stop"}]}`, valid)
	}))
	defer server.Close()
	client, e := model.NewClient(model.Config{BaseURL: server.URL + "/v1", Model: "local", Timeout: time.Second, MaxResponseBytes: 65536})
	if e != nil {
		t.Fatal(e)
	}
	defer client.Close()
	i, e := NewLocal(Config{65536, 8192}, client)
	if e != nil {
		t.Fatal(e)
	}
	p, e := i.Interpret(context.Background(), inputFixture())
	if e != nil || p.Budget.InputAllowance != 32768-8192-2048 {
		t.Fatal(p.Budget, e)
	}
	capacity = 8192
	_, e = i.Interpret(context.Background(), inputFixture())
	assertKind(t, e, BudgetExceeded)
	if reads != 2 || completions != 1 {
		t.Fatal(reads, completions)
	}
	capacity = 131072
	p, e = i.Interpret(context.Background(), inputFixture())
	if e != nil || p.Budget.InputAllowance != 65536-8192-2048 {
		t.Fatal(p.Budget, e)
	}
}
