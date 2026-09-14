package main

import (
	"errors"
	"sort"
	"strconv"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// herdPopulationMaxFlags collects repeatable --routine-herd-population-max
// RACE:MAX flags into RoutinePolicy.HerdPopulationMax, the MaintainHerd
// slaughter surplus's operator-declared per-race population ceiling. It is
// only ever consulted once --routine-allow-slaughter also opts in; declaring
// a maximum alone never dispatches a slaughter write.
type herdPopulationMaxFlags map[policy.Resource]int64

func (targets *herdPopulationMaxFlags) String() string {
	if *targets == nil {
		return ""
	}
	names := make([]string, 0, len(*targets))
	for name := range *targets {
		names = append(names, string(name))
	}
	sort.Strings(names)
	values := make([]string, 0, len(names))
	for _, name := range names {
		values = append(values, name+":"+strconv.FormatInt((*targets)[policy.Resource(name)], 10))
	}
	return strings.Join(values, ";")
}

// Map returns the plain map startServiceClock's policy.Resource-keyed
// parameter needs, without requiring every caller to import policy itself.
func (targets herdPopulationMaxFlags) Map() map[policy.Resource]int64 {
	return map[policy.Resource]int64(targets)
}
func (targets *herdPopulationMaxFlags) Set(value string) error {
	split := strings.LastIndexByte(value, ':')
	if split < 0 {
		return errors.New("herd population maximum requires RACE:MAX")
	}
	maxText := value[split+1:]
	max, err := strconv.ParseInt(maxText, 10, 64)
	if err != nil || max <= 0 || strconv.FormatInt(max, 10) != maxText {
		return errors.New("maximum must be a canonical positive integer")
	}
	race := policy.Resource(value[:split])
	result := map[policy.Resource]int64{}
	for k, v := range *targets {
		result[k] = v
	}
	result[race] = max
	if err := policy.ValidateHerdPopulationMax(result); err != nil {
		return err
	}
	*targets = result
	return nil
}
