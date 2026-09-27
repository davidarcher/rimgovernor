package buildingruntime

import "github.com/davidarcher/RimGovernor/go/internal/domain"

func allowOf(z domain.ZoneCreate) []string {
	names, _ := z.Filter().AllowOnlyDefinitions()
	return names
}
