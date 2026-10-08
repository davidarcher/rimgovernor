package buildingruntime

import (
	"context"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

const maxFailedIntentMethods = 8

// failedIntentMethods counts methods with a native refusal, once per method.
// Successful setting or designation writes do not consume the failure budget.
// History still supplies unique method identities independently of this count.
func failedIntentMethods(ctx context.Context, journal *store.Store, methods []domain.Method, episode uint64, prefix string) (int, error) {
	failures := 0
	for _, method := range methods {
		if method.Episode != episode || !strings.HasPrefix(string(method.Method), prefix) {
			continue
		}
		plan, err := journal.LoadPlan(ctx, method.Plan)
		if err != nil {
			return 0, err
		}
		for _, progress := range plan.Progress {
			if receipt, known := progress.View().Receipt.Value(); known && receipt == domain.ReceiptRefused {
				failures++
				break
			}
		}
	}
	return failures, nil
}
