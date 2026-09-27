package main

import (
	"context"
	"fmt"
	"io"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// governorStateNative is the save's governor state component (#882).
type governorStateNative interface {
	GovernorState(context.Context) (map[string]string, error)
	PutGovernorState(context.Context, string, string) (map[string]string, error)
}

// shadowGovernorState mirrors goals and family records into the save each
// refresh (#974): only changed keys are put, a vanished key is deleted.
// The first successful read logs drift between the save and the store;
// the store stays authoritative.
func shadowGovernorState(ctx context.Context, native governorStateNative, database *store.Store, refresh time.Duration, out io.Writer) {
	var written map[string]string
	ticker := time.NewTicker(refresh)
	defer ticker.Stop()
	for {
		call, cancel := context.WithTimeout(ctx, 10*time.Second)
		if err := shadowGovernorStateOnce(call, native, database, &written, out); err != nil && ctx.Err() == nil {
			fmt.Fprintf(out, "governor state: %v\n", err)
		}
		cancel()
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
		}
	}
}

func shadowGovernorStateOnce(ctx context.Context, native governorStateNative, database *store.Store, written *map[string]string, out io.Writer) error {
	blobs, err := database.GovernorStateBlobs(ctx)
	if err != nil {
		return err
	}
	if *written == nil {
		saved, err := native.GovernorState(ctx)
		if err != nil {
			return err
		}
		if drift := store.GovernorStateDrift(blobs, saved); len(drift) > 0 {
			fmt.Fprintf(out, "governor state: drift from store on load (%d keys): %v\n", len(drift), drift)
		}
		*written = map[string]string{}
		for key, blob := range saved {
			if store.GovernorShadowKey(key) {
				(*written)[key] = blob
			}
		}
	}
	for key, blob := range blobs {
		if (*written)[key] == blob {
			continue
		}
		if _, err := native.PutGovernorState(ctx, key, blob); err != nil {
			return fmt.Errorf("put %s: %w", key, err)
		}
		(*written)[key] = blob
	}
	for key := range *written {
		if _, ok := blobs[key]; ok {
			continue
		}
		if _, err := native.PutGovernorState(ctx, key, ""); err != nil {
			return fmt.Errorf("delete %s: %w", key, err)
		}
		delete(*written, key)
	}
	return nil
}
