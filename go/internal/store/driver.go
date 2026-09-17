package store

import (
	"context"
	"database/sql"
	"database/sql/driver"
	"errors"
	"strings"
	"sync"

	"modernc.org/sqlite"
)

// DriverName is the database/sql driver the store opens its handles with:
// modernc's SQLite behind a per-connection prepared-statement cache.
//
// database/sql prepares, runs and finalizes a fresh statement for every
// Exec/Query on a connection that implements ExecerContext/QueryerContext,
// which is what modernc's conn does; with the pure-Go build, parsing the
// SQL is the dominant cost of the store's small indexed statements. The
// wrapper below hides those two interfaces so database/sql goes through
// PrepareContext, and answers repeated query text with the statement it
// compiled the first time (SQLite resets it between uses and re-prepares it
// itself after a schema change). The store already pins every handle to one
// connection, so one cache per connection is one cache per database.
const DriverName = "sqlite-cached"

func init() {
	sql.Register(DriverName, cachingDriver{})
}

type cachingDriver struct{}

func (cachingDriver) Open(name string) (driver.Conn, error) {
	c, err := (&sqlite.Driver{}).Open(name)
	if err != nil {
		return nil, err
	}
	return &cachingConn{Conn: c, stmts: map[string]*cachedStmt{}}, nil
}

// cachingConn deliberately implements neither driver.ExecerContext nor
// driver.QueryerContext.
type cachingConn struct {
	driver.Conn
	mu    sync.Mutex
	stmts map[string]*cachedStmt
}

// cacheLimit bounds the statements a connection keeps compiled; the store's
// query text is fixed, so it is a guard against a runaway, not a working
// set to tune.
const cacheLimit = 256

type cachedStmt struct {
	driver.Stmt
	conn *cachingConn
	sql  string
	// busy is set while database/sql holds the statement: a second use of
	// the same text before the first is released (rows still open) gets its
	// own uncached statement instead of resetting the cursor underneath it.
	busy bool
}

// Close hands the statement back to the cache; the underlying handle is
// finalized when the connection closes or the entry is evicted.
func (s *cachedStmt) Close() error {
	s.conn.mu.Lock()
	defer s.conn.mu.Unlock()
	if s.conn.stmts[s.sql] != s {
		// Evicted or connection closed while in use.
		return s.Stmt.Close()
	}
	s.busy = false
	return nil
}

func (s *cachedStmt) ExecContext(ctx context.Context, args []driver.NamedValue) (driver.Result, error) {
	return s.Stmt.(driver.StmtExecContext).ExecContext(ctx, args)
}

func (s *cachedStmt) QueryContext(ctx context.Context, args []driver.NamedValue) (driver.Rows, error) {
	return s.Stmt.(driver.StmtQueryContext).QueryContext(ctx, args)
}

func (c *cachingConn) PrepareContext(ctx context.Context, query string) (driver.Stmt, error) {
	prepare := func() (driver.Stmt, error) {
		return c.Conn.(driver.ConnPrepareContext).PrepareContext(ctx, query)
	}
	// modernc re-parses multi-statement scripts on every execution, so there
	// is nothing to keep.
	if strings.Contains(strings.TrimRight(strings.TrimSpace(query), ";"), ";") {
		return prepare()
	}
	c.mu.Lock()
	if c.stmts == nil {
		c.mu.Unlock()
		return nil, errors.New("sqlite connection closed")
	}
	if s, ok := c.stmts[query]; ok {
		if s.busy {
			c.mu.Unlock()
			return prepare()
		}
		s.busy = true
		c.mu.Unlock()
		return s, nil
	}
	c.mu.Unlock()
	inner, err := prepare()
	if err != nil {
		return nil, err
	}
	s := &cachedStmt{Stmt: inner, conn: c, sql: query, busy: true}
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.stmts == nil {
		return inner, nil
	}
	if len(c.stmts) >= cacheLimit {
		for k, old := range c.stmts {
			if !old.busy {
				delete(c.stmts, k)
				_ = old.Stmt.Close()
				break
			}
		}
	}
	if len(c.stmts) < cacheLimit {
		c.stmts[query] = s
		return s, nil
	}
	return inner, nil
}

func (c *cachingConn) Prepare(query string) (driver.Stmt, error) {
	return c.PrepareContext(context.Background(), query)
}

func (c *cachingConn) Close() error {
	c.mu.Lock()
	stmts := c.stmts
	c.stmts = nil
	c.mu.Unlock()
	var err error
	for _, s := range stmts {
		if !s.busy {
			if e := s.Stmt.Close(); e != nil && err == nil {
				err = e
			}
		}
	}
	if e := c.Conn.Close(); e != nil && err == nil {
		err = e
	}
	return err
}

func (c *cachingConn) BeginTx(ctx context.Context, opts driver.TxOptions) (driver.Tx, error) {
	return c.Conn.(driver.ConnBeginTx).BeginTx(ctx, opts)
}

func (c *cachingConn) Ping(ctx context.Context) error {
	return c.Conn.(driver.Pinger).Ping(ctx)
}

func (c *cachingConn) ResetSession(ctx context.Context) error {
	return c.Conn.(driver.SessionResetter).ResetSession(ctx)
}

func (c *cachingConn) IsValid() bool {
	return c.Conn.(driver.Validator).IsValid()
}

// CopyPages copies the database behind src into the database at dstURI
// through SQLite's online backup, page by page, so nothing is parsed on
// either side. Test fixtures clone a migrated template with it; VACUUM
// INTO would re-execute the schema DDL into the copy.
func CopyPages(ctx context.Context, src *sql.DB, dstURI string) error {
	conn, err := src.Conn(ctx)
	if err != nil {
		return err
	}
	defer conn.Close()
	return conn.Raw(func(raw any) error {
		if c, ok := raw.(*cachingConn); ok {
			raw = c.Conn
		}
		b, err := raw.(interface {
			NewBackup(string) (*sqlite.Backup, error)
		}).NewBackup(dstURI)
		if err != nil {
			return err
		}
		if _, err = b.Step(-1); err != nil {
			_ = b.Finish()
			return err
		}
		return b.Finish()
	})
}
