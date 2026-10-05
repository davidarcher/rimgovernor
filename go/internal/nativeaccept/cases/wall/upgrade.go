// The wall/upgrade case proves the typed wall-upgrade census behind
// rimgovernor/observations_list_wall_upgrade_sites (#78) on a loaded save:
// the cleanup census (no target) is complete, and for every colonist wall
// each per-target replacement row names the target with its CAS token, a
// normal, both side supports, its backup cells and priced stone materials. At least one wall must yield a
// site or the run is vacuous. A fixture build's test/lighting_prepare first
// builds an enclosed roofed room so the census has colonist walls to read;
// the stock saves hold none.
package wall

import (
	"context"
	"fmt"
	"sort"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

// upgradeColonistWalls reads the typed building census for every player Wall id.
func upgradeColonistWalls(ctx context.Context, h *na.Harness, scope map[string]any) ([]string, error) {
	request := map[string]any{"scope": scope, "defNames": []any{"Wall"}, "statuses": []any{"built"}, "playerOnly": true}
	reply, err := h.Wire(ctx, "walls", "observations_list_buildings", request)
	if err != nil {
		return nil, err
	}
	_, observed, err := na.Outcome(reply, "observed")
	if err != nil {
		return nil, fmt.Errorf("%s: %w", "walls", err)
	}
	var ids []string
	for _, raw := range na.AsSlice(observed["buildings"]) {
		row, _ := na.AsMap(raw)
		building, _ := na.AsMap(row["building"])
		ids = append(ids, na.AsString(building["id"]))
	}
	sort.Strings(ids)
	return ids, nil
}

// checkSites asserts every per-target row is a complete replacement site.
func checkSites(wall string, rows []any) error {
	for _, raw := range rows {
		row, _ := na.AsMap(raw)
		target, _ := na.AsMap(row["target"])
		original, _ := na.AsMap(row["original"])
		snapshot, _ := na.AsMap(row["targetSnapshot"])
		if na.AsString(target["id"]) != wall || na.AsString(original["id"]) != wall || na.AsString(snapshot["token"]) == "" {
			return fmt.Errorf("%s: replacement row must target the original wall with a CAS token", wall)
		}
		if present, _ := na.AsBool(row["targetPresent"]); !present {
			return fmt.Errorf("%s: target reported absent", wall)
		}
		normal, _ := na.AsMap(row["normal"])
		leftSupport, _ := na.AsMap(row["leftSupport"])
		rightSupport, _ := na.AsMap(row["rightSupport"])
		if na.AsNumber(normal["x"]) == 0 && na.AsNumber(normal["z"]) == 0 {
			return fmt.Errorf("%s: site without a normal", wall)
		}
		if na.AsString(leftSupport["id"]) == "" || na.AsString(rightSupport["id"]) == "" {
			return fmt.Errorf("%s: site without both supports: %v", wall, row)
		}
		// A corner (diagonal normal) is replaced in place and has no backup
		// cells; a straight site has its three exterior backups.
		corner := na.AsNumber(normal["x"]) != 0 && na.AsNumber(normal["z"]) != 0
		if backups := len(na.AsSlice(row["backupCells"])); corner && backups != 0 || !corner && backups != 3 {
			return fmt.Errorf("%s: site with %d backup cells for normal %v: %v", wall, backups, normal, row)
		}
		options := na.AsSlice(row["replacementMaterials"])
		if len(options) == 0 {
			return fmt.Errorf("%s: site without replacement materials", wall)
		}
		for _, rawOption := range options {
			option, _ := na.AsMap(rawOption)
			costs := na.AsSlice(option["costs"])
			if na.AsString(option["stuff"]) == "" || len(costs) == 0 {
				return fmt.Errorf("%s: material without a stuff or costs: %v", wall, option)
			}
			for _, rawCost := range costs {
				cost, _ := na.AsMap(rawCost)
				if na.AsString(cost["defName"]) == "" || na.AsNumber(cost["units"]) <= 0 {
					return fmt.Errorf("%s: material %v has an unpriced cost %v", wall, option["stuff"], cost)
				}
			}
		}
	}
	return nil
}

// maxWalls bounds the colonist walls cross-checked per target (the first
// N by id).
const maxWalls = 64

func init() {
	cases.Register(cases.Case{
		Name:  "wall/upgrade",
		Scope: "Typed wall-upgrade site census: complete cleanup rows and, per colonist wall, complete replacement sites with supports, backup cells and priced stone materials; read-only, no designation or construction.",
		// The save carries its own expansion list (the runner enables them);
		// the lighting fixture builds the walls on top of it.
		Start:  cases.Fixture{Op: "test/lighting_prepare", On: cases.LabStart()},
		Budget: 5 * time.Minute,
		Run:    runUpgrade,
	})
}

func runUpgrade(ctx context.Context, s cases.Session) error {
	report := s.Report()
	h := s.Harness()
	if !na.Contains(s.Names(), "rimgovernor/observations_list_wall_upgrade_sites") {
		return fmt.Errorf("missing rimgovernor/observations_list_wall_upgrade_sites in discovery")
	}
	identityReply, err := h.Wire(ctx, "identity-before", "lifecycle_read_identity", map[string]any{})
	if err != nil {
		return err
	}
	_, before, err := na.Outcome(identityReply, "loaded")
	if err != nil {
		return err
	}
	beforeContext, _ := before["context"].(map[string]any)
	identity, _ := beforeContext["identity"].(map[string]any)
	scope := map[string]any{"expectedIdentity": identity}

	walls, err := upgradeColonistWalls(ctx, h, scope)
	if err != nil {
		return err
	}
	if len(walls) == 0 {
		return fmt.Errorf("save has no colonist wall; the census assertion would be vacuous")
	}
	report["colonist_walls"] = len(walls)

	cleanupReply, err := h.Wire(ctx, "cleanup-census", "observations_list_wall_upgrade_sites", map[string]any{"scope": scope})
	if err != nil {
		return err
	}
	_, cleanup, err := na.Outcome(cleanupReply, "observed")
	if err != nil {
		return fmt.Errorf("cleanup census: %w", err)
	}
	if observedContext, _ := cleanup["context"].(map[string]any); !na.DeepEqual(observedContext, beforeContext) {
		return fmt.Errorf("cleanup census context drifted mid-run")
	}
	cleanupRows := na.AsSlice(cleanup["sites"])
	for _, raw := range cleanupRows {
		row, _ := na.AsMap(raw)
		if _, ok := row["replacement"]; !ok || len(na.AsSlice(row["completedBackups"])) == 0 {
			return fmt.Errorf("cleanup row without a permanent replacement and remaining backups: %v", row["target"])
		}
	}
	report["cleanup_sites"] = len(cleanupRows)

	if len(walls) > maxWalls {
		walls = walls[:maxWalls]
	}
	sitesFound, wallsWithSites := 0, 0
	for _, wall := range walls {
		typedReply, err := h.Wire(ctx, "typed-"+wall, "observations_list_wall_upgrade_sites", map[string]any{"scope": scope, "targetId": wall})
		if err != nil {
			return err
		}
		_, observed, err := na.Outcome(typedReply, "observed")
		if err != nil {
			return fmt.Errorf("%s: %w", wall, err)
		}
		rows := na.AsSlice(observed["sites"])
		if err := checkSites(wall, rows); err != nil {
			return err
		}
		if len(rows) > 0 {
			wallsWithSites++
			sitesFound += len(rows)
		}
	}
	if sitesFound == 0 {
		return fmt.Errorf("no colonist wall yielded a replacement site; the geometry assertion is vacuous")
	}
	report["walls_checked"] = len(walls)
	report["walls_with_sites"] = wallsWithSites
	report["replacement_sites"] = sitesFound

	for _, c := range []struct {
		label   string
		request map[string]any
		code    string
	}{
		{"blank-target", map[string]any{"scope": scope, "targetId": " "}, "FAILURE_CODE_INVALID_REQUEST"},
		{"unknown-target", map[string]any{"scope": scope, "targetId": "Thing_NoSuchWall0"}, "FAILURE_CODE_NOT_FOUND"},
	} {
		reply, err := h.Wire(ctx, c.label, "observations_list_wall_upgrade_sites", c.request)
		if err != nil {
			return err
		}
		if code, ok := na.FailureCode(reply); !ok || code != c.code {
			return fmt.Errorf("%s: expected %s, got %q", c.label, c.code, code)
		}
	}

	identityAfterReply, err := h.Wire(ctx, "identity-after", "lifecycle_read_identity", map[string]any{})
	if err != nil {
		return err
	}
	_, after, err := na.Outcome(identityAfterReply, "loaded")
	if err != nil {
		return err
	}
	if afterContext, _ := after["context"].(map[string]any); !na.DeepEqual(afterContext, beforeContext) {
		return fmt.Errorf("identity/tick changed during a read-only pass")
	}
	return nil
}
