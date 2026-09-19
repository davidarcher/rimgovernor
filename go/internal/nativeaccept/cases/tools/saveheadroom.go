package tools

import (
	"context"
	"strings"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
)

// CommittedSaveNames are the saves under cases.CommittedSavesDir that the
// tools/saveheadroom-<save> lint loads; a new committed save is added here.
// The checkpoint generators run the same check before committing.
var CommittedSaveNames = []string{sustained.BaselineSave, "RimGovernor-facility-startup", "RimGovernor-defense-layout"}

func init() {
	for _, save := range CommittedSaveNames {
		save := save
		cases.Register(cases.Case{
			Name: "tools/saveheadroom-" + strings.TrimPrefix(save, "RimGovernor-"),
			Scope: "Lint: the committed " + save + " save's planning colony facts read under the committed-save headroom (768 KiB of the 1 MiB envelope), " +
				"so a case that starts from it cannot tip the routine review into LIMIT_EXCEEDED (issue #320).",
			Start:  cases.Save{Name: save, From: cases.CommittedSaves()},
			Budget: 3 * time.Minute,
			Run: func(ctx context.Context, s cases.Session) error {
				return na.CheckCommittedSaveHeadroom(ctx, s.Harness(), s.Report())
			},
		})
	}
}
