package nativeaccept

import (
	"context"
	"fmt"
)

// This file is the post-#52 authority ceremony shared by the acceptance
// binaries: authority is SetMode(Auto|Manual)/Revoke plus native generation
// continuity. There is no acquire/renew handshake and no lease; the only thing
// a caller carries forward is the generation its grant was admitted at.

// WireFunc is the rimgovernor/* ProtoJSON transport (ordinarily Harness.Wire).
type WireFunc func(ctx context.Context, label, method string, request map[string]any) (map[string]any, error)

// AuthorityStatus reads authority_read_status for identity and returns the
// status body plus its native generation.
func AuthorityStatus(ctx context.Context, wire WireFunc, label string, identity map[string]any) (map[string]any, uint64, error) {
	reply, err := wire(ctx, label, "authority_read_status", map[string]any{"identity": identity})
	if err != nil {
		return nil, 0, fmt.Errorf("%s: %w", label, err)
	}
	_, status, err := Outcome(reply, "status")
	if err != nil {
		return nil, 0, fmt.Errorf("%s: %w", label, err)
	}
	statusContext, _ := AsMap(status["context"])
	generation := uint64(AsNumber(statusContext["nativeGeneration"]))
	if _, unavailable := status["unavailable"]; unavailable && generation == 0 {
		// Authority is initialized by the game's own update polling; a read
		// that lands before that reports it unavailable, and the first
		// generation is always 1.
		generation = 1
	}
	return status, generation, nil
}

// GrantAuto issues SetMode(Auto) at identity's current native generation and
// returns the "granted" body ({context, authority}); callers thread its
// context.nativeGeneration into every later WritePrecondition. Re-issuing it
// while already Auto is admitted and advances the generation again.
func GrantAuto(ctx context.Context, wire WireFunc, label string, identity map[string]any) (map[string]any, error) {
	_, generation, err := AuthorityStatus(ctx, wire, label+"-status", identity)
	if err != nil {
		return nil, err
	}
	return GrantAutoAt(ctx, wire, label, identity, generation)
}

// GrantAutoAt issues SetMode(Auto) at an explicit expected generation.
func GrantAutoAt(ctx context.Context, wire WireFunc, label string, identity map[string]any, expected uint64) (map[string]any, error) {
	reply, err := wire(ctx, label, "authority_control", map[string]any{"setMode": map[string]any{
		"identity": identity, "expectedGeneration": fmt.Sprint(expected), "mode": "MODE_AUTO",
	}})
	if err != nil {
		return nil, fmt.Errorf("%s: %w", label, err)
	}
	_, granted, err := Outcome(reply, "granted")
	if err != nil {
		return nil, fmt.Errorf("%s: %w", label, err)
	}
	if AsString(dig(granted, "authority", "mode")) != "MODE_AUTO" {
		return nil, fmt.Errorf("%s: expected MODE_AUTO, got %#v", label, granted)
	}
	return granted, nil
}

// GrantGeneration is the native generation a GrantAuto body was admitted at.
func GrantGeneration(grant map[string]any) uint64 {
	return uint64(AsNumber(dig(grant, "context", "nativeGeneration")))
}

// RevokeManual issues Revoke(MANUAL) at grant's generation and returns the
// "revoked" body.
func RevokeManual(ctx context.Context, wire WireFunc, label string, identity, grant map[string]any) (map[string]any, error) {
	reply, err := wire(ctx, label, "authority_control", map[string]any{"revoke": map[string]any{
		"identity": identity, "expectedGeneration": dig(grant, "context", "nativeGeneration"), "reason": "REVOCATION_REASON_MANUAL",
	}})
	if err != nil {
		return nil, fmt.Errorf("%s: %w", label, err)
	}
	_, revoked, err := Outcome(reply, "revoked")
	if err != nil {
		return nil, fmt.Errorf("%s: %w", label, err)
	}
	return revoked, nil
}

// RequireInactive asserts an authority_read_status body is Inactive with
// exactly reason and returns its inactive case.
func RequireInactive(status map[string]any, reason string) (map[string]any, error) {
	inactive, ok := AsMap(status["inactive"])
	if !ok || AsString(inactive["reason"]) != reason {
		return nil, fmt.Errorf("expected inactive authority %s, got %#v", reason, status)
	}
	return inactive, nil
}
