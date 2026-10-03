package cases

import (
	"context"
	"encoding/json"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/encoding/protojson"
)

// Catalog is the loaded game's definition catalog, read over client under the
// colony's identity (Session.Identity).
func Catalog(ctx context.Context, client *bridge.Client, identity map[string]any) (*bridge.DefinitionCatalog, error) {
	data, err := json.Marshal(identity)
	if err != nil {
		return nil, err
	}
	id := &c.Identity{}
	if err := protojson.Unmarshal(data, id); err != nil {
		return nil, err
	}
	return client.DefinitionCatalog(ctx, id)
}
