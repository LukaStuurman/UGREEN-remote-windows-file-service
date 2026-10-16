package main

import (
	"context"
	"errors"
	"io"
	"os"
	"sync"

	smb "github.com/hirochachacha/go-smb2"
)

type openFile struct {
	mu            sync.Mutex
	file          *smb.File
	root          bool
	directory     bool
	closed        bool
	deletePending bool
	failure       error
}

func (a *app) handleFile(ctx context.Context, r request, data []byte) (any, []byte, error) {
	a.mu.RLock()
	defer a.mu.RUnlock()
	if a.share == nil {
		return nil, nil, os.ErrPermission
	}
	if r.Op == "open-handle" {
		path, err := safePath(r.Path, true)
		if err != nil {
			return nil, nil, err
		}
		// Root handles are metadata/directory only. Never truncate or replace root.
		if path == "." && (r.Disposition != 1 && r.Disposition != 3 || r.Options&0x1000 != 0) {
			return nil, nil, os.ErrPermission
		}
		if r.Disposition > 5 || r.Sharing & ^uint32(7) != 0 {
			return nil, nil, os.ErrInvalid
		}
		share := a.share.WithContext(ctx)
		if err = guard(share, path, r.Disposition != 1 && r.Disposition != 4); err != nil {
			return nil, nil, err
		}
		_, beforeErr := share.Lstat(path)
		existed := beforeErr == nil
		if beforeErr != nil && errorStatus(beforeErr) != 404 {
			return nil, nil, beforeErr
		}
		a.handlesMu.Lock()
		defer a.handlesMu.Unlock()
		if len(a.handles) >= 2048 {
			return nil, nil, errors.New("handle-limit")
		}
		f, err := share.OpenHandle(path, r.Access|0x80, r.Sharing, r.Disposition, r.Options, r.Attributes)
		if err != nil {
			return nil, nil, err
		}
		// Inspect the opened object, so a final symlink race is also rejected.
		stat, err := f.Stat()
		if err != nil || stat.Mode()&(os.ModeSymlink|os.ModeIrregular|os.ModeDevice|os.ModeNamedPipe|os.ModeSocket) != 0 {
			_ = f.Close()
			if err != nil {
				return nil, nil, err
			}
			return nil, nil, os.ErrPermission
		}
		a.nextHandle++
		id := a.nextHandle
		if a.handles == nil {
			a.handles = make(map[uint64]*openFile)
		}
		a.handles[id] = &openFile{file: f, root: path == ".", directory: stat.IsDir()}
		return map[string]any{"handle": id, "entry": toEntry(stat, path == "."), "existed": existed}, nil, nil
	}
	a.handlesMu.Lock()
	h := a.handles[r.Handle]
	a.handlesMu.Unlock()
	if h == nil {
		return nil, nil, os.ErrInvalid
	}
	h.mu.Lock()
	defer h.mu.Unlock()
	if h.closed {
		return nil, nil, os.ErrInvalid
	}
	h.file.SetContext(ctx)
	if h.failure != nil && r.Op != "handle-close" {
		return nil, nil, h.failure
	}
	fail := func(err error) error {
		if err != nil {
			h.failure = err
		}
		return err
	}
	switch r.Op {
	case "handle-stat":
		info, err := h.file.Stat()
		if err != nil {
			return nil, nil, err
		}
		return toEntry(info, h.root), nil, nil
	case "handle-read":
		if r.Offset < 0 || r.Length < 0 || r.Length > maxData {
			return nil, nil, os.ErrInvalid
		}
		b := make([]byte, r.Length)
		n, err := h.file.ReadAt(b, r.Offset)
		if err == io.EOF {
			err = nil
		}
		if n < 0 {
			n = 0
		}
		if err != nil {
			return nil, nil, err
		}
		return nil, b[:n], nil
	case "handle-write":
		if h.root || h.directory || r.Offset < -1 {
			return nil, nil, os.ErrInvalid
		}
		// Serialize this process's append offset selection with other writes.
		a.appendMu.Lock()
		defer a.appendMu.Unlock()
		offset := r.Offset
		if offset == -1 {
			st, err := h.file.Stat()
			if err != nil {
				return nil, nil, err
			}
			offset = st.Size()
		}
		n, err := h.file.WriteAt(data, offset)
		if err == nil && n != len(data) {
			err = io.ErrShortWrite
		}
		if err == nil {
			err = h.file.Sync()
		}
		return map[string]int{"written": n}, nil, fail(err)
	case "handle-flush":
		if h.directory {
			return nil, nil, nil
		}
		return nil, nil, fail(h.file.Sync())
	case "handle-resize", "handle-reserve":
		if h.root || h.directory || r.Size < 0 {
			return nil, nil, os.ErrInvalid
		}
		a.appendMu.Lock()
		defer a.appendMu.Unlock()
		var err error
		if r.Op == "handle-resize" {
			err = h.file.Truncate(r.Size)
		} else {
			err = h.file.Reserve(r.Size)
		}
		if err == nil {
			err = h.file.Sync()
		}
		return nil, nil, fail(err)
	case "handle-rename":
		if h.root {
			return nil, nil, os.ErrPermission
		}
		target, err := safePath(r.Target, false)
		if err != nil {
			return nil, nil, err
		}
		if err = guard(a.share.WithContext(ctx), target, true); err != nil {
			return nil, nil, err
		}
		return nil, nil, h.file.RenameTo(target, r.Replace)
	case "handle-delete":
		if h.root {
			return nil, nil, os.ErrPermission
		}
		if h.deletePending {
			return nil, nil, nil
		}
		err := h.file.SetDeletePending(true)
		if err == nil {
			h.deletePending = true
		}
		return nil, nil, err
	case "handle-times":
		if h.root {
			return nil, nil, os.ErrPermission
		}
		return nil, nil, h.file.Chtimes(r.Modified, r.Modified)
	case "handle-attributes":
		if h.root {
			return nil, nil, os.ErrPermission
		}
		return nil, nil, h.file.SetAttributes(r.Attributes)
	case "handle-close":
		// Writes/truncates already have acknowledged SMB flushes. A close failure
		// cannot be reported by Dokan's void CloseFile, so it is never used as the
		// only durability barrier for a successful write.
		err := h.file.Close()
		h.closed = true
		a.handlesMu.Lock()
		delete(a.handles, r.Handle)
		a.handlesMu.Unlock()
		return nil, nil, err
	}
	return nil, nil, os.ErrInvalid
}
