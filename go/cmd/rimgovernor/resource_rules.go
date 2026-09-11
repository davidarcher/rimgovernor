package main

import (
	"errors"
	"strconv"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

type resourceRuleFlags []policy.ResourceRule

func (rules *resourceRuleFlags) String() string {
	values := []string{}
	for _, r := range *rules {
		values = append(values, string(r.Resource)+":"+string(r.Spending)+":"+strconv.FormatInt(r.Reserve, 10))
	}
	return strings.Join(values, ";")
}
func (rules *resourceRuleFlags) Set(value string) error {
	last := strings.LastIndexByte(value, ':')
	if last < 0 {
		return errors.New("resource rule requires RESOURCE:SPENDING:RESERVE")
	}
	reserveText := value[last+1:]
	reserve, err := strconv.ParseInt(reserveText, 10, 64)
	if err != nil || reserve < 0 || strconv.FormatInt(reserve, 10) != reserveText {
		return errors.New("reserve must be a canonical nonnegative integer")
	}
	value = value[:last]
	split := strings.LastIndexByte(value, ':')
	if split < 0 {
		return errors.New("resource rule requires a spending mode")
	}
	result := append(append([]policy.ResourceRule(nil), (*rules)...), policy.ResourceRule{Resource: policy.Resource(value[:split]), Spending: policy.Spending(value[split+1:]), Reserve: reserve})
	if err := policy.ValidateResourceRules(result); err != nil {
		return err
	}
	*rules = result
	return nil
}
