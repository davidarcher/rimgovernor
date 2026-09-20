package production

import (
	"context"
	"fmt"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{
		Name:        "production/configuration",
		Scope:       "Kibble save-time configuration flip identifies fixed-filter pruning; scalar changes and bill reorder remain unsuccessful with bounded before/after diagnostics.",
		Start:       cases.Save{Name: baselineSave},
		RequiredOps: []string{"test/bill_configuration_diagnostics"},
		Budget:      time.Minute,
		Run: func(ctx context.Context, s cases.Session) error {
			result, err := s.Harness().Call(ctx, "bill-configuration", "test/bill_configuration_diagnostics", map[string]any{})
			if err != nil {
				return err
			}
			s.Report()["configuration"] = result
			if ok, _ := na.AsBool(result["success"]); !ok {
				return fmt.Errorf("bill configuration diagnostic checks failed: %v", result)
			}
			return nil
		},
	})
}
