package interpreter

import "github.com/davidarcher/RimGovernor/go/internal/model"

// NewLocal rechecks the loaded LM Studio window before budgeting each request.
// The caller owns client. No model is loaded or switched, and the budget remains
// a conservative byte approximation rather than an exact tokenizer count.
func NewLocal(config Config, client *model.Client) (*Interpreter, error) {
	if client == nil {
		return nil, fail(InvalidInput, "local model client required")
	}
	result, err := New(config, client)
	if err != nil {
		return nil, err
	}
	result.capacity = client
	return result, nil
}
