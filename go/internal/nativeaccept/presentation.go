package nativeaccept

import (
	"fmt"
	"math"
)

func isCloseFloat(a, b float64) bool {
	const relTol, absTol = 1e-6, 1e-6
	diff := math.Abs(a - b)
	return diff <= math.Max(relTol*math.Max(math.Abs(a), math.Abs(b)), absTol)
}

// Listing asserts a completeness/count block reports exactly count rows with no
// truncation: a listing
// missing any of its four declared fields, or reporting a different count, is never
// treated as an implicit pass.
func Listing(value map[string]any, count int) error {
	want := map[string]any{"totalCount": float64(count), "returnedCount": float64(count), "complete": true, "truncated": false}
	if !DeepEqual(value, want) {
		return fmt.Errorf("listing does not match expected count %d: %#v", count, value)
	}
	return nil
}

func nonZero(m map[string]any, keys ...string) map[string]any {
	out := map[string]any{}
	for _, key := range keys {
		if v, ok := m[key]; ok && !DeepEqual(v, float64(0)) {
			out[key] = v
		}
	}
	return out
}

// CameraMatches asserts a typed presentation_camera reply reproduces the native
// camera state's finite root/zoom sizes, map position, zoom range, extension flag,
// and signed viewport bounds. Viewport coordinates may legitimately be negative, so the
// comparison preserves signed native bounds rather than treating zero specially
// except to drop fields ProtoJSON would have omitted.
func CameraMatches(typed, native map[string]any) error {
	success, ok := AsBool(native["success"])
	if !ok || !success {
		return fmt.Errorf("native camera reply was not successful: %#v", native)
	}
	sizeRange, _ := AsMap(native["sizeRange"])
	fields := map[string]float64{
		"rootSize": AsNumber(native["rootSize"]), "zoomRootSize": AsNumber(native["zoomRootSize"]),
		"minimumRootSize": AsNumber(sizeRange["min"]), "maximumRootSize": AsNumber(sizeRange["max"]),
	}
	for key, want := range fields {
		got := AsNumber(typed[key])
		if math.IsInf(got, 0) || math.IsNaN(got) {
			return fmt.Errorf("typed %s is not finite: %v", key, typed[key])
		}
		if !isCloseFloat(got, want) {
			return fmt.Errorf("typed %s=%v does not match native %v", key, got, want)
		}
	}
	nativePosition, _ := AsMap(native["mapPosition"])
	typedPosition, _ := AsMap(typed["mapPosition"])
	for _, key := range []string{"x", "z"} {
		got := 0.0
		if v, present := typedPosition[key]; present {
			got = AsNumber(v)
		}
		if !isCloseFloat(got, AsNumber(nativePosition[key])) {
			return fmt.Errorf("typed mapPosition.%s=%v does not match native %v", key, got, nativePosition[key])
		}
	}
	if AsString(typed["nativeZoomRange"]) != AsString(native["zoomRange"]) {
		return fmt.Errorf("typed nativeZoomRange does not match native zoomRange")
	}
	typedExtension, _ := AsBool(typed["zoomExtensionEnabled"])
	nativeExtension, _ := AsBool(native["cameraZoomExtensionEnabled"])
	if typedExtension != nativeExtension {
		return fmt.Errorf("typed zoomExtensionEnabled does not match native cameraZoomExtensionEnabled")
	}
	nativeViewRect, _ := AsMap(native["viewRect"])
	want := nonZero(nativeViewRect, "minX", "minZ", "maxX", "maxZ")
	if !DeepEqual(typed["viewRect"], want) {
		return fmt.Errorf("typed viewRect does not match native signed bounds: got %#v want %#v", typed["viewRect"], want)
	}
	return nil
}

// RosterMatches asserts a typed presentation_colonists reply names exactly the
// native colonist set, each spawned with its exact name and non-zero-filtered
// position, and stamped with the caller's single loaded map. A row for a pawn id the
// native roster never reported, or a native colonist the typed roster silently
// dropped, is never treated as an implicit pass.
func RosterMatches(typed, native, identity map[string]any) error {
	success, ok := AsBool(native["success"])
	count := AsNumber(native["count"])
	if !ok || !success || count <= 0 {
		return fmt.Errorf("native roster reply was not successful or reported no colonists: %#v", native)
	}
	rows := AsSlice(typed["colonists"])
	listing, _ := AsMap(typed["listing"])
	if err := Listing(listing, len(rows)); err != nil {
		return err
	}
	source := map[string]map[string]any{}
	for _, raw := range AsSlice(native["colonists"]) {
		entry, ok := AsMap(raw)
		if !ok {
			return fmt.Errorf("native colonist row must be an object")
		}
		source[AsString(entry["pawnId"])] = entry
	}
	if len(source) != int(count) || len(source) != len(rows) {
		return fmt.Errorf("roster size mismatch: native.count=%v source=%d typed=%d", native["count"], len(source), len(rows))
	}
	for _, raw := range rows {
		row, ok := AsMap(raw)
		if !ok {
			return fmt.Errorf("typed colonist row must be an object")
		}
		observed, ok := source[AsString(row["pawnId"])]
		if !ok {
			return fmt.Errorf("typed colonist %q is not present in the native roster", AsString(row["pawnId"]))
		}
		typedSpawned, _ := AsBool(row["spawned"])
		observedSpawned, _ := AsBool(observed["spawned"])
		if !typedSpawned || !observedSpawned {
			return fmt.Errorf("colonist %q is not spawned in both typed and native replies", AsString(row["pawnId"]))
		}
		if AsString(row["name"]) != AsString(observed["name"]) {
			return fmt.Errorf("colonist %q name mismatch", AsString(row["pawnId"]))
		}
		observedPosition, _ := AsMap(observed["position"])
		want := nonZero(observedPosition, "x", "z")
		if !DeepEqual(row["position"], want) {
			return fmt.Errorf("colonist %q position does not match native signed position", AsString(row["pawnId"]))
		}
		if !DeepEqual(row["mapId"], identity["mapId"]) {
			return fmt.Errorf("colonist %q mapId does not match the single loaded map", AsString(row["pawnId"]))
		}
	}
	return nil
}

// SelectionMatches asserts a typed presentation_selection reply names exactly the
// one selected native pawn, with its exact kind/type/label/defName/map/position and
// none of the fingerprint/gizmo/inspect fields a presentation read must never leak.
func SelectionMatches(typed, native map[string]any, pawnID string, identity map[string]any) error {
	success, ok := AsBool(native["success"])
	selectedCount := AsNumber(native["selectedCount"])
	if !ok || !success || selectedCount != 1 {
		return fmt.Errorf("native selection reply was not successful or did not select exactly one object: %#v", native)
	}
	rows := AsSlice(typed["selectedObjects"])
	listing, _ := AsMap(typed["listing"])
	if err := Listing(listing, 1); err != nil {
		return err
	}
	if len(rows) != 1 {
		return fmt.Errorf("expected exactly one typed selected object, got %d", len(rows))
	}
	row, ok := AsMap(rows[0])
	if !ok {
		return fmt.Errorf("typed selected object must be an object")
	}
	if AsString(row["id"]) != pawnID {
		return fmt.Errorf("typed selected object id %q does not match the observed pawn %q", AsString(row["id"]), pawnID)
	}
	nativeRows := AsSlice(native["selectedObjects"])
	if len(nativeRows) != 1 {
		return fmt.Errorf("expected exactly one native selected object, got %d", len(nativeRows))
	}
	observed, ok := AsMap(nativeRows[0])
	if !ok {
		return fmt.Errorf("native selected object must be an object")
	}
	if AsString(row["id"]) != AsString(observed["id"]) {
		return fmt.Errorf("typed selected object id does not match native id")
	}
	if AsString(row["nativeKind"]) != AsString(observed["kind"]) || AsString(observed["kind"]) != "pawn" {
		return fmt.Errorf("typed nativeKind does not match native kind %q", "pawn")
	}
	if AsString(row["nativeType"]) != AsString(observed["type"]) {
		return fmt.Errorf("typed nativeType does not match native type")
	}
	if AsString(row["label"]) != AsString(observed["label"]) {
		return fmt.Errorf("typed label does not match native label")
	}
	details, _ := AsMap(observed["details"])
	if AsString(row["defName"]) != AsString(details["defName"]) {
		return fmt.Errorf("typed defName does not match native details.defName")
	}
	if !DeepEqual(row["mapId"], identity["mapId"]) {
		return fmt.Errorf("typed mapId does not match the single loaded map")
	}
	detailsPosition, _ := AsMap(details["position"])
	want := nonZero(detailsPosition, "x", "z")
	if !DeepEqual(row["position"], want) {
		return fmt.Errorf("typed position does not match native signed details.position")
	}
	if _, present := typed["fingerprint"]; present {
		return fmt.Errorf("typed presentation_selection reply must never leak fingerprint")
	}
	if _, present := typed["visibleGizmoCount"]; present {
		return fmt.Errorf("typed presentation_selection reply must never leak visibleGizmoCount")
	}
	if _, present := row["inspectText"]; present {
		return fmt.Errorf("typed selected object must never leak inspectText")
	}
	if _, present := row["inspectLabel"]; present {
		return fmt.Errorf("typed selected object must never leak inspectLabel")
	}
	return nil
}
