package model

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"unicode/utf8"
)

// LoadedCapacity describes one currently loaded instance, not a model's theoretical maximum.
type LoadedCapacity struct {
	Model         string
	InstanceID    string
	ContextTokens int
}

// LoadedCapacity reads LM Studio metadata without loading or switching models.
// It is a snapshot: callers must refresh after external model configuration changes.
func (client *Client) LoadedCapacity(ctx context.Context) (LoadedCapacity, error) {
	var zero LoadedCapacity
	if client.owner.Err() != nil {
		return zero, ErrClosed
	}
	call, cancel := context.WithTimeout(ctx, client.config.Timeout)
	stop := context.AfterFunc(client.owner, cancel)
	defer func() { stop(); cancel() }()
	endpoint, err := url.Parse(client.endpoint)
	if err != nil {
		return zero, err
	}
	endpoint.Path = "/api/v1/models"
	endpoint.RawPath = ""
	req, err := http.NewRequestWithContext(call, http.MethodGet, endpoint.String(), nil)
	if err != nil {
		return zero, err
	}
	req.Header.Set("Accept", "application/json")
	response, err := client.http.Do(req)
	if err != nil {
		return zero, fmt.Errorf("local model capacity: %w", err)
	}
	defer response.Body.Close()
	data, err := readBounded(response.Body, client.config.MaxResponseBytes)
	if err != nil {
		return zero, err
	}
	if response.StatusCode != http.StatusOK {
		return zero, &HTTPError{StatusCode: response.StatusCode, Message: serverErrorMessage(data)}
	}
	return decodeCapacity(data, client.config.Model)
}

func decodeCapacity(data []byte, configured string) (LoadedCapacity, error) {
	var zero LoadedCapacity
	invalid := func() (LoadedCapacity, error) {
		return zero, fmt.Errorf("%w: expected exactly one configured loaded instance with positive integer context_length", ErrInvalidResponse)
	}
	if !utf8.Valid(data) {
		return invalid()
	}
	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.UseNumber()
	if uniqueJSON(decoder, 0) != nil {
		return invalid()
	}
	if _, err := decoder.Token(); err != io.EOF {
		return invalid()
	}
	var root map[string]json.RawMessage
	if json.Unmarshal(data, &root) != nil || root == nil || present(root["error"]) {
		return invalid()
	}
	var models []map[string]json.RawMessage
	if !present(root["models"]) || json.Unmarshal(root["models"], &models) != nil {
		return invalid()
	}
	matches := 0
	for _, entry := range models {
		var key string
		if json.Unmarshal(entry["key"], &key) != nil {
			return invalid()
		}
		var instances []map[string]json.RawMessage
		if json.Unmarshal(entry["loaded_instances"], &instances) != nil {
			return invalid()
		}
		for _, instance := range instances {
			var id string
			if json.Unmarshal(instance["id"], &id) != nil || id == "" {
				return invalid()
			}
			if configured != key && configured != id {
				continue
			}
			var config map[string]json.RawMessage
			if json.Unmarshal(instance["config"], &config) != nil {
				return invalid()
			}
			var tokens int
			if json.Unmarshal(config["context_length"], &tokens) != nil || tokens < 1 || tokens > 1<<24 {
				return invalid()
			}
			matches++
			zero = LoadedCapacity{Model: configured, InstanceID: id, ContextTokens: tokens}
		}
	}
	if matches != 1 {
		return invalid()
	}
	return zero, nil
}
