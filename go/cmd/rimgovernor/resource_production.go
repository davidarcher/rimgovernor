package main

import (
	"errors"
	"sort"
	"strconv"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// resourceReserveFlags collects repeatable --routine-resource-reserve
// RESOURCE:FLOOR flags into RoutinePolicy.ResourceReserves, the
// ProductionPolicy goal's operator-declared floors for policy.ProductionFloors.
// Unlike resourceTargetFlags's positive-only MaintainResource target, a floor
// of zero is canonical (it simply declares no floor for that resource), so
// Set accepts any nonnegative canonical integer.
type resourceReserveFlags map[policy.Resource]int64

func (reserves *resourceReserveFlags) String() string {
	if *reserves == nil {
		return ""
	}
	names := make([]string, 0, len(*reserves))
	for name := range *reserves {
		names = append(names, string(name))
	}
	sort.Strings(names)
	values := make([]string, 0, len(names))
	for _, name := range names {
		values = append(values, name+":"+strconv.FormatInt((*reserves)[policy.Resource(name)], 10))
	}
	return strings.Join(values, ";")
}

// Map returns the plain map startServiceClock's policy.Resource-keyed
// parameter needs, without requiring every caller to import policy itself.
func (reserves resourceReserveFlags) Map() map[policy.Resource]int64 {
	return map[policy.Resource]int64(reserves)
}
func (reserves *resourceReserveFlags) Set(value string) error {
	split := strings.LastIndexByte(value, ':')
	if split < 0 {
		return errors.New("resource reserve requires RESOURCE:FLOOR")
	}
	floorText := value[split+1:]
	floor, err := strconv.ParseInt(floorText, 10, 64)
	if err != nil || floor < 0 || strconv.FormatInt(floor, 10) != floorText {
		return errors.New("floor must be a canonical nonnegative integer")
	}
	resource := policy.Resource(value[:split])
	result := map[policy.Resource]int64{}
	for k, v := range *reserves {
		result[k] = v
	}
	result[resource] = floor
	if _, _, err := policy.ProductionFloors(result, nil); err != nil {
		return err
	}
	*reserves = result
	return nil
}

// stoppedResourceFlags collects repeatable --routine-resource-stop RESOURCE
// flags into RoutinePolicy.StoppedResources, the ProductionPolicy goal's
// operator-declared stopped-production list for policy.ProductionFloors.
type stoppedResourceFlags []policy.Resource

func (stopped *stoppedResourceFlags) String() string {
	values := make([]string, 0, len(*stopped))
	for _, name := range *stopped {
		values = append(values, string(name))
	}
	return strings.Join(values, ";")
}

// Slice returns the plain slice startServiceClock's policy.Resource-typed
// parameter needs, without requiring every caller to import policy itself.
func (stopped stoppedResourceFlags) Slice() []policy.Resource {
	return append([]policy.Resource(nil), stopped...)
}
func (stopped *stoppedResourceFlags) Set(value string) error {
	if value == "" {
		return errors.New("stopped resource name required")
	}
	result := append(append([]policy.Resource(nil), (*stopped)...), policy.Resource(value))
	if _, _, err := policy.ProductionFloors(nil, result); err != nil {
		return err
	}
	*stopped = result
	return nil
}
