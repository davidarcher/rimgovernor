package gabp

import (
	"bufio"
	"context"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"net"
	"runtime"
	"strconv"
	"sync"
	"time"
)

// Wire constants of gabp/1 (gabp-runtime v1.0.0 runtime/protocol.go).
const (
	Version = "gabp/1"

	TypeRequest  = "request"
	TypeResponse = "response"
	TypeEvent    = "event"

	MethodSessionHello    = "session/hello"
	MethodToolsList       = "tools/list"
	MethodToolsCall       = "tools/call"
	MethodEventsSubscribe = "events/subscribe"
)

// ErrDisconnected is returned by every call after the connection dropped or
// was closed; errors.Is matches it. Err reports the underlying cause.
var ErrDisconnected = errors.New("gabp: disconnected")

// DefaultMaxFrameBytes caps an incoming frame when Options leaves it zero.
const DefaultMaxFrameBytes = 50 << 20

// Message is the gabp/1 envelope in both directions.
type Message struct {
	V       string          `json:"v"`
	ID      string          `json:"id"`
	Type    string          `json:"type"`
	Method  string          `json:"method,omitempty"`
	Params  json.RawMessage `json:"params,omitempty"`
	Result  json.RawMessage `json:"result,omitempty"`
	Error   *RemoteError    `json:"error,omitempty"`
	Channel string          `json:"channel,omitempty"`
	Seq     int             `json:"seq,omitempty"`
	Payload json.RawMessage `json:"payload,omitempty"`
}

// RemoteError is a response's error object: the server answered, refusing.
type RemoteError struct {
	Code    int             `json:"code"`
	Message string          `json:"message"`
	Data    json.RawMessage `json:"data,omitempty"`
}

func (e *RemoteError) Error() string { return fmt.Sprintf("gabp error %d: %s", e.Code, e.Message) }

// Event is a server-pushed event frame.
type Event struct {
	Channel string
	Seq     int
	Payload json.RawMessage
}

// Welcome is the session/hello result (SessionWelcomeResult). Raw keeps the
// whole result, including fields this struct does not name.
type Welcome struct {
	AgentID string `json:"agentId"`
	App     struct {
		Name    string `json:"name"`
		Version string `json:"version"`
	} `json:"app"`
	Capabilities struct {
		Methods   []string `json:"methods"`
		Events    []string `json:"events"`
		Resources []string `json:"resources"`
	} `json:"capabilities"`
	SchemaVersion string `json:"schemaVersion"`
	ServerInfo    *struct {
		Name    string `json:"name"`
		Version string `json:"version"`
		Author  string `json:"author"`
	} `json:"serverInfo,omitempty"`
	Raw json.RawMessage `json:"-"`
}

// Options tunes Dial. The zero value is usable.
type Options struct {
	ClientName    string        // clientInfo.name; default "rimgovernor"
	ClientVersion string        // clientInfo.version and bridgeVersion
	MaxFrameBytes int           // incoming frame cap; default DefaultMaxFrameBytes
	BackoffMin    time.Duration // first redial delay; default 100ms
	BackoffMax    time.Duration // redial delay cap; default 2s
	// OnEvent receives server-pushed events on the reader goroutine; it
	// must not block. Nil drops events.
	OnEvent func(Event)
}

// Conn is one authenticated GABP session. Calls are safe for concurrent use.
type Conn struct {
	nc      net.Conn
	opts    Options
	welcome Welcome

	writeMu sync.Mutex

	mu      sync.Mutex
	pending map[string]chan *Message
	nextID  uint64
	idBase  string
	err     error
	done    chan struct{}
}

// Dial connects to addr, redialing with backoff until ctx ends (the game may
// still be booting), then performs the session/hello handshake with token.
// A refused handshake is not retried.
func Dial(ctx context.Context, addr, token string, opts Options) (*Conn, error) {
	if opts.ClientName == "" {
		opts.ClientName = "rimgovernor"
	}
	if opts.ClientVersion == "" {
		opts.ClientVersion = "dev"
	}
	if opts.MaxFrameBytes <= 0 {
		opts.MaxFrameBytes = DefaultMaxFrameBytes
	}
	if opts.BackoffMin <= 0 {
		opts.BackoffMin = 100 * time.Millisecond
	}
	if opts.BackoffMax < opts.BackoffMin {
		opts.BackoffMax = max(2*time.Second, opts.BackoffMin)
	}
	var d net.Dialer
	delay := opts.BackoffMin
	var nc net.Conn
	for {
		var err error
		nc, err = d.DialContext(ctx, "tcp", addr)
		if err == nil {
			break
		}
		if ctx.Err() != nil {
			return nil, fmt.Errorf("gabp: dial %s: %w (last: %v)", addr, ctx.Err(), err)
		}
		t := time.NewTimer(delay)
		select {
		case <-ctx.Done():
			t.Stop()
			return nil, fmt.Errorf("gabp: dial %s: %w (last: %v)", addr, ctx.Err(), err)
		case <-t.C:
		}
		delay = min(2*delay, opts.BackoffMax)
	}
	c := newConn(nc, opts)
	if err := c.hello(ctx, token); err != nil {
		c.Close()
		return nil, err
	}
	return c, nil
}

func newConn(nc net.Conn, opts Options) *Conn {
	var b [4]byte
	_, _ = rand.Read(b[:])
	c := &Conn{
		nc:      nc,
		opts:    opts,
		pending: map[string]chan *Message{},
		idBase:  hex.EncodeToString(b[:]) + "-",
		done:    make(chan struct{}),
	}
	go c.readLoop()
	return c
}

func (c *Conn) hello(ctx context.Context, token string) error {
	var b [16]byte
	_, _ = rand.Read(b[:])
	params := map[string]any{
		"token":         token,
		"bridgeVersion": c.opts.ClientVersion,
		"platform":      runtime.GOOS,
		"launchId":      hex.EncodeToString(b[:]),
		"clientInfo":    map[string]string{"name": c.opts.ClientName, "version": c.opts.ClientVersion},
	}
	raw, err := c.Call(ctx, MethodSessionHello, params)
	if err != nil {
		return fmt.Errorf("gabp: handshake: %w", err)
	}
	if err := json.Unmarshal(raw, &c.welcome); err != nil {
		return fmt.Errorf("gabp: handshake: bad welcome: %w", err)
	}
	c.welcome.Raw = raw
	return nil
}

// Welcome returns the handshake result.
func (c *Conn) Welcome() Welcome { return c.welcome }

// Done is closed once the connection is gone.
func (c *Conn) Done() <-chan struct{} { return c.done }

// Err reports why the connection ended (nil while it is up).
func (c *Conn) Err() error {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.err
}

// Close ends the connection; pending and later calls fail with ErrDisconnected.
func (c *Conn) Close() error {
	c.fail(errors.New("closed by client"))
	return nil
}

func (c *Conn) fail(cause error) {
	c.mu.Lock()
	if c.err != nil {
		c.mu.Unlock()
		return
	}
	c.err = fmt.Errorf("%w: %w", ErrDisconnected, cause)
	c.pending = map[string]chan *Message{}
	c.mu.Unlock()
	_ = c.nc.Close()
	close(c.done)
}

func (c *Conn) readLoop() {
	r := bufio.NewReaderSize(c.nc, 64<<10)
	for {
		body, err := ReadFrame(r, c.opts.MaxFrameBytes)
		if err != nil {
			c.fail(err)
			return
		}
		var m Message
		if err := json.Unmarshal(body, &m); err != nil {
			c.fail(fmt.Errorf("gabp: corrupt frame: %w", err))
			return
		}
		switch m.Type {
		case TypeResponse:
			c.mu.Lock()
			ch := c.pending[m.ID]
			delete(c.pending, m.ID)
			c.mu.Unlock()
			if ch != nil {
				ch <- &m // buffered 1; the waiter may have gone
			}
		case TypeEvent:
			if c.opts.OnEvent != nil {
				c.opts.OnEvent(Event{Channel: m.Channel, Seq: m.Seq, Payload: m.Payload})
			}
		}
	}
}

// Call sends one request and waits for its response. A server error
// response returns *RemoteError. Cancelling ctx abandons the wait (a late
// reply is dropped) and leaves the connection up.
func (c *Conn) Call(ctx context.Context, method string, params any) (json.RawMessage, error) {
	if params == nil {
		params = struct{}{}
	}
	p, err := json.Marshal(params)
	if err != nil {
		return nil, fmt.Errorf("gabp: marshal %s params: %w", method, err)
	}
	ch := make(chan *Message, 1)
	c.mu.Lock()
	if c.err != nil {
		err := c.err
		c.mu.Unlock()
		return nil, err
	}
	c.nextID++
	id := c.idBase + strconv.FormatUint(c.nextID, 10)
	c.pending[id] = ch
	c.mu.Unlock()

	body, err := json.Marshal(Message{V: Version, ID: id, Type: TypeRequest, Method: method, Params: p})
	if err != nil {
		c.drop(id)
		return nil, err
	}
	c.writeMu.Lock()
	if dl, ok := ctx.Deadline(); ok {
		_ = c.nc.SetWriteDeadline(dl)
	}
	err = WriteFrame(c.nc, ASCIIJSON(body))
	_ = c.nc.SetWriteDeadline(time.Time{})
	c.writeMu.Unlock()
	if err != nil {
		c.fail(fmt.Errorf("write: %w", err))
		return nil, c.Err()
	}

	select {
	case m := <-ch:
		if m.Error != nil {
			return nil, m.Error
		}
		return m.Result, nil
	case <-ctx.Done():
		c.drop(id)
		return nil, fmt.Errorf("gabp: %s: %w", method, ctx.Err())
	case <-c.done:
		select { // a reply read before the drop still counts
		case m := <-ch:
			if m.Error != nil {
				return nil, m.Error
			}
			return m.Result, nil
		default:
		}
		return nil, c.Err()
	}
}

func (c *Conn) drop(id string) {
	c.mu.Lock()
	delete(c.pending, id)
	c.mu.Unlock()
}
