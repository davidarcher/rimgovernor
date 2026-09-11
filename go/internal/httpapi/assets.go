package httpapi

import (
	"errors"
	"fmt"
	"io/fs"
	"net/http"
	"os"
	"path"
	"path/filepath"
	"strings"
)

func openAssets(directory string) (*os.Root, error) {
	if directory == "" {
		return nil, nil
	}
	if !filepath.IsAbs(directory) {
		return nil, errors.New("assets directory must be absolute")
	}
	root, err := os.OpenRoot(directory)
	if err != nil {
		return nil, fmt.Errorf("open dashboard assets: %w", err)
	}
	index, err := root.Open("index.html")
	if err != nil {
		root.Close()
		return nil, fmt.Errorf("open dashboard index: %w", err)
	}
	info, err := index.Stat()
	index.Close()
	if err != nil || !info.Mode().IsRegular() {
		root.Close()
		return nil, errors.New("dashboard index must be a regular file")
	}
	return root, nil
}

func (s *Server) serveAsset(w http.ResponseWriter, r *http.Request) {
	if len(r.RequestURI) > 2048 || r.ContentLength != 0 || len(r.TransferEncoding) > 0 {
		s.failure(w, r, 400, "invalid_request", "Asset requests require a bounded URL and no body")
		return
	}
	name := strings.TrimPrefix(r.URL.Path, "/")
	// Reject ambiguous separators and decoded traversal before OS path handling.
	if strings.ContainsAny(name, "\\\x00:") || strings.Contains(name, "//") {
		s.failure(w, r, 400, "invalid_path", "Invalid asset path")
		return
	}
	for _, part := range strings.Split(name, "/") {
		if part == "." || part == ".." {
			s.failure(w, r, 400, "invalid_path", "Invalid asset path")
			return
		}
	}
	if name == "" || name == "colonies" || name == "scenario" {
		name = "index.html"
	}
	if !fs.ValidPath(name) {
		s.failure(w, r, 404, "not_found", "Asset not found")
		return
	}
	file, err := s.assets.Open(name)
	if err != nil {
		s.failure(w, r, 404, "not_found", "Asset not found")
		return
	}
	defer file.Close()
	info, err := file.Stat()
	if err != nil || !info.Mode().IsRegular() {
		s.failure(w, r, 404, "not_found", "Asset not found")
		return
	}
	contentType := "application/octet-stream"
	switch strings.ToLower(path.Ext(name)) {
	case ".html":
		contentType = "text/html; charset=utf-8"
	case ".js", ".mjs":
		contentType = "text/javascript; charset=utf-8"
	case ".css":
		contentType = "text/css; charset=utf-8"
	case ".json":
		contentType = "application/json; charset=utf-8"
	case ".svg":
		contentType = "image/svg+xml"
	case ".png":
		contentType = "image/png"
	case ".jpg", ".jpeg":
		contentType = "image/jpeg"
	case ".webp":
		contentType = "image/webp"
	case ".ico":
		contentType = "image/x-icon"
	case ".woff":
		contentType = "font/woff"
	case ".woff2":
		contentType = "font/woff2"
	}
	w.Header().Set("Content-Type", contentType)
	w.Header().Set("Content-Security-Policy", "default-src 'self'; connect-src 'self'; img-src 'self' data: blob:; media-src 'self' blob:; style-src 'self' 'unsafe-inline'; script-src 'self'; frame-ancestors 'none'; base-uri 'self'")
	http.ServeContent(w, r, info.Name(), info.ModTime(), file)
}
