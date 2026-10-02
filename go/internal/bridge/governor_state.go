package bridge

import (
	"context"

	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	"google.golang.org/protobuf/proto"
)

// Governor state (#882) is opaque ASCII blobs Go keeps in the save's
// GovernorState game component. Native stores them verbatim; a put is
// durable once the game next saves.
const (
	readGovernorStateMethod = "rimgovernor/lifecycle_read_governor_state"
	putGovernorStateMethod  = "rimgovernor/lifecycle_put_governor_state"
)

// GovernorState returns every saved blob by key.
func (caller *Client) GovernorState(ctx context.Context) (map[string]string, error) {
	reply := &l.GovernorStateReply{}
	raw, err := caller.protoRead(ctx, readGovernorStateMethod, &l.GovernorStateRequest{}, reply)
	if err != nil {
		return nil, err
	}
	return governorStateBlobs(reply, raw)
}

// PutGovernorState replaces the blob under key; an empty blob deletes it.
// The reply carries no blobs (#1362): a put no longer returns the map.
func (caller *Client) PutGovernorState(ctx context.Context, key, blob string) error {
	if key == "" || !asciiString(key) || !asciiString(blob) {
		return contract("governor state key must be non-empty ASCII and blob ASCII")
	}
	reply := &l.GovernorStateReply{}
	raw, err := caller.protoCall(ctx, putGovernorStateMethod, &l.PutGovernorStateRequest{Key: proto.String(key), Blob: proto.String(blob)}, reply)
	if err != nil {
		return err
	}
	_, err = governorStateBlobs(reply, raw)
	return err
}

func governorStateBlobs(reply *l.GovernorStateReply, raw Result) (map[string]string, error) {
	switch value := reply.Outcome.(type) {
	case *l.GovernorStateReply_Loaded:
		blobs := value.Loaded.GetBlobs()
		if blobs == nil {
			blobs = map[string]string{}
		}
		return blobs, nil
	case *l.GovernorStateReply_Unavailable:
		return nil, unavailable(value.Unavailable, raw)
	case *l.GovernorStateReply_Failure:
		return nil, failure(value.Failure, raw)
	}
	return nil, contract("governor state outcome missing")
}

func asciiString(value string) bool {
	for i := 0; i < len(value); i++ {
		if value[i] >= 0x80 {
			return false
		}
	}
	return true
}
