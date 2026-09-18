package nativeaccept

import (
	"fmt"
	"sort"
)

var serviceReads = map[string]bool{
	"rimgovernor/lifecycle_read_identity":  true,
	"rimgovernor/lifecycle_read_tick":      true,
	"rimgovernor/observations_read_status": true,
}

var serviceDiagnostics = map[string]bool{"rimbridge/list_operation_events": true}

// ServiceTrace asserts a native operation-event history is gapless from baseline,
// attributes every non-diagnostic event to a known read capability, and never
// attributes a write capability, then returns the ordered read-operation names. It requires at least two
// observations_read_status and four lifecycle_read_identity or
// lifecycle_read_tick operations, matching
// the fixed read cadence a read-only Go service session must reproduce.
func ServiceTrace(events []map[string]any, baseline int, capabilities map[string][]string) ([]string, error) {
	ordered := append([]map[string]any(nil), events...)
	sort.Slice(ordered, func(i, j int) bool { return AsNumber(ordered[i]["Sequence"]) < AsNumber(ordered[j]["Sequence"]) })
	if len(ordered) == 0 {
		return nil, fmt.Errorf("native operation event history has a gap")
	}
	for i, row := range ordered {
		want := baseline + 1 + i
		if got := int(AsNumber(row["Sequence"])); got != want {
			return nil, fmt.Errorf("native operation event history has a gap: sequence %d, wanted %d", got, want)
		}
	}
	operations := map[string]string{}
	var operationOrder []string
	for _, row := range ordered {
		identifier := AsString(row["CapabilityId"])
		if identifier == "" {
			if AsString(row["OperationId"]) != "" {
				return nil, fmt.Errorf("operation event has no capability attribution")
			}
			continue
		}
		var selected []string
		for _, alias := range capabilities[identifier] {
			if serviceReads[alias] || serviceDiagnostics[alias] {
				selected = append(selected, alias)
			}
		}
		if len(selected) == 0 {
			return nil, fmt.Errorf("unexpected native capability during service ownership: %s", identifier)
		}
		var readAlias string
		for _, alias := range selected {
			if serviceReads[alias] {
				readAlias = alias
				break
			}
		}
		if readAlias == "" {
			continue
		}
		operation := AsString(row["OperationId"])
		if existing, ok := operations[operation]; ok {
			if existing != readAlias {
				return nil, fmt.Errorf("operation %s attributed to conflicting capabilities %q and %q", operation, existing, readAlias)
			}
		} else {
			operations[operation] = readAlias
			operationOrder = append(operationOrder, operation)
		}
	}
	names := make([]string, 0, len(operationOrder))
	statusCount, identityCount := 0, 0
	for _, operation := range operationOrder {
		name := operations[operation]
		names = append(names, name)
		switch name {
		case "rimgovernor/observations_read_status":
			statusCount++
		case "rimgovernor/lifecycle_read_identity", "rimgovernor/lifecycle_read_tick":
			identityCount++
		}
	}
	if statusCount < 2 {
		return nil, fmt.Errorf("expected at least 2 observations_read_status operations, got %d", statusCount)
	}
	if identityCount < 4 {
		return nil, fmt.Errorf("expected at least 4 lifecycle_read_identity/lifecycle_read_tick operations, got %d", identityCount)
	}
	return names, nil
}
