package model

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/url"
	"strings"
	"time"
	"unicode/utf8"
)

type Client struct {
	config    Config
	endpoint  string
	http      *http.Client
	transport *http.Transport
	owner     context.Context
	cancel    context.CancelFunc
}

func NewClient(config Config) (*Client, error) {
	if strings.TrimSpace(config.Model) == "" || !utf8.ValidString(config.Model) || config.Timeout <= 0 ||
		config.MaxResponseBytes < 1 || config.MaxResponseBytes > 64<<20 {
		return nil, fmt.Errorf("configure a model, positive timeout and response limit within 1..67108864 bytes")
	}
	base, err := url.Parse(config.BaseURL)
	if err != nil || base.Scheme != "http" || base.Opaque != "" || base.User != nil || base.RawQuery != "" || base.ForceQuery || base.Fragment != "" {
		return nil, fmt.Errorf("model URL must be an HTTP local base URL without credentials, query or fragment")
	}
	host := strings.ToLower(base.Hostname())
	if host != "localhost" && host != "127.0.0.1" && host != "::1" {
		return nil, fmt.Errorf("model URL must use localhost, 127.0.0.1, ::1")
	}
	if base.Port() == "0" {
		return nil, fmt.Errorf("model URL port must be nonzero")
	}
	base.Path = strings.TrimRight(base.Path, "/") + "/chat/completions"
	base.RawPath = ""
	dialer := &net.Dialer{Timeout: min(config.Timeout, 10*time.Second)}
	transport := &http.Transport{
		// Explicitly ignore environment proxy settings. Fresh connections also
		// avoid Transport's automatic stale-connection replay behavior.
		Proxy: nil, DisableKeepAlives: true,
		DialContext: func(ctx context.Context, network, address string) (net.Conn, error) {
			name, port, err := net.SplitHostPort(address)
			if err != nil {
				return nil, err
			}
			if strings.EqualFold(name, "localhost") {
				// Resolve the literal locally without consulting DNS or hosts files.
				address = net.JoinHostPort("127.0.0.1", port)
			}
			return dialer.DialContext(ctx, network, address)
		},
		ResponseHeaderTimeout: config.Timeout,
	}
	owner, cancel := context.WithCancel(context.Background())
	return &Client{config: config, endpoint: base.String(), transport: transport, owner: owner, cancel: cancel,
		http: &http.Client{Transport: transport, CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }}}, nil
}

// Close cancels active requests and releases owned HTTP resources. It is safe to
// call concurrently and more than once.
func (client *Client) Close() error {
	client.cancel()
	client.transport.CloseIdleConnections()
	return nil
}

func (client *Client) Complete(ctx context.Context, request Request) (Response, error) {
	return client.execute(ctx, request)
}

type completionRequest struct {
	Model     string    `json:"model"`
	Messages  []Message `json:"messages"`
	MaxTokens int       `json:"max_tokens"`
	N         int       `json:"n"`
	Stream    bool      `json:"stream"`
}

func (client *Client) execute(ctx context.Context, request Request) (Response, error) {
	var result Response
	if client.owner.Err() != nil {
		return result, ErrClosed
	}
	if request.MaxOutputTokens <= 0 || len(request.Messages) == 0 {
		return result, fmt.Errorf("messages and positive max output tokens are required")
	}
	for _, message := range request.Messages {
		if message.Role != System && message.Role != User && message.Role != Assistant || !utf8.ValidString(message.Content) {
			return result, fmt.Errorf("messages require text and a system, user or assistant role")
		}
	}
	call, cancel := context.WithTimeout(ctx, client.config.Timeout)
	stop := context.AfterFunc(client.owner, cancel)
	defer func() { stop(); cancel() }()
	if err := call.Err(); err != nil {
		return result, err
	}
	body := completionRequest{Model: client.config.Model, Messages: request.Messages, MaxTokens: request.MaxOutputTokens, N: 1}
	encoded, err := json.Marshal(body)
	if err != nil {
		return result, err
	}
	httpRequest, err := http.NewRequestWithContext(call, http.MethodPost, client.endpoint, bytes.NewReader(encoded))
	if err != nil {
		return result, err
	}
	httpRequest.Header.Set("Content-Type", "application/json")
	httpRequest.Header.Set("Accept", "application/json")
	reply, err := client.http.Do(httpRequest)
	if err != nil {
		return result, fmt.Errorf("local model request: %w", err)
	}
	defer reply.Body.Close()
	if reply.StatusCode != http.StatusOK {
		data, readErr := readBounded(reply.Body, client.config.MaxResponseBytes)
		if readErr != nil {
			return result, readErr
		}
		return result, &HTTPError{StatusCode: reply.StatusCode, Message: serverErrorMessage(data)}
	}
	data, err := readBounded(reply.Body, client.config.MaxResponseBytes)
	if err == nil {
		result, err = decodeCompletion(data)
	}
	if call.Err() != nil {
		return result, call.Err()
	}
	return result, err
}

func readBounded(reader io.Reader, limit int64) ([]byte, error) {
	data, err := io.ReadAll(io.LimitReader(reader, limit+1))
	if err != nil {
		return nil, err
	}
	if int64(len(data)) > limit {
		return nil, ErrResponseTooLarge
	}
	return data, nil
}
