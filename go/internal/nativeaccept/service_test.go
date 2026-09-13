package nativeaccept

import (
	"strconv"
	"testing"
)

func serviceRows() []map[string]any {
	kinds := []string{"identity", "status", "identity", "identity", "status", "identity"}
	rows := make([]map[string]any, len(kinds))
	for i, kind := range kinds {
		rows[i] = map[string]any{"Sequence": i + 11, "OperationId": strconv.Itoa(i), "CapabilityId": kind}
	}
	return rows
}

var serviceCapabilities = map[string][]string{
	"identity": {"rimgovernor/lifecycle_read_identity"},
	"status":   {"rimgovernor/observations_read_status"},
	"write":    {"rimgovernor/operations_execute"},
}

func TestServiceTraceRequiresTwoAttributedObservations(t *testing.T) {
	rows := serviceRows()
	reversed := make([]map[string]any, len(rows))
	for i, row := range rows {
		reversed[len(rows)-1-i] = row
	}
	names, err := ServiceTrace(reversed, 10, serviceCapabilities)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if len(names) != 6 {
		t.Fatalf("expected 6 attributed operations, got %d", len(names))
	}
}

func TestIncompleteOrMutatingServiceTraceCannotPass(t *testing.T) {
	cases := []struct {
		name   string
		mutate func(events []map[string]any) []map[string]any
	}{
		{"gap", func(events []map[string]any) []map[string]any {
			return append(events[:2], events[3:]...)
		}},
		{"duplicate", func(events []map[string]any) []map[string]any {
			last := copyCompatAny(events[len(events)-1])
			return append(events, last)
		}},
		{"write", func(events []map[string]any) []map[string]any {
			events[2]["CapabilityId"] = "write"
			return events
		}},
		{"missing_capability", func(events []map[string]any) []map[string]any {
			events[2]["CapabilityId"] = ""
			return events
		}},
		{"missing_status", func(events []map[string]any) []map[string]any {
			events[1]["CapabilityId"] = "identity"
			return events
		}},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			events := c.mutate(serviceRows())
			if _, err := ServiceTrace(events, 10, serviceCapabilities); err == nil {
				t.Fatalf("expected an error for fault %q", c.name)
			}
		})
	}
}
