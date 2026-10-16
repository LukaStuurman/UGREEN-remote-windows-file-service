// Remote Drive extensions to the pinned upstream v1.1.0, BSD-2-Clause.
// Callers must serialize operations on each File, including SetContext.
package smb2

import (
	"context"
	. "github.com/hirochachacha/go-smb2/internal/smb2"
	"os"
	"time"
)

// OpenHandle preserves the caller's access, sharing and creation semantics.
// Final-component reparse points are opened themselves, never followed.
func (fs *Share) OpenHandle(name string, access, share, disposition, options, attributes uint32) (*File, error) {
	name = normPath(name)
	if err := validatePath("open", name, false); err != nil {
		return nil, err
	}
	return fs.createFile(name, &CreateRequest{DesiredAccess: access, ShareAccess: share,
		CreateDisposition: disposition, CreateOptions: options | FILE_OPEN_REPARSE_POINT,
		FileAttributes: attributes, ImpersonationLevel: Impersonation}, false)
}

// Inspect opens only for attributes and shares delete as well as read/write.
// The upstream Lstat omits FILE_SHARE_DELETE, so it cannot inspect a target
// while a retained rename/delete-capable handle is open on that target.
func (fs *Share) Inspect(name string) (os.FileInfo, error) {
	f, err := fs.OpenHandle(name, FILE_READ_ATTRIBUTES,
		FILE_SHARE_READ|FILE_SHARE_WRITE|FILE_SHARE_DELETE, FILE_OPEN, 0, FILE_ATTRIBUTE_NORMAL)
	if err != nil {
		return nil, err
	}
	info := f.fileStat
	if err = f.Close(); err != nil {
		return nil, err
	}
	return info, nil
}

// SetContext updates only this file's share view, never the session or other files.
func (f *File) SetContext(ctx context.Context) { f.fs = f.fs.WithContext(ctx) }

// RenameTo uses the already open file ID and one server-side atomic rename.
func (f *File) RenameTo(target string, replace bool) error {
	target = normPath(target)
	if err := validatePath("rename to", target, false); err != nil {
		return err
	}
	var flag uint8
	if replace {
		flag = 1
	}
	err := f.setInfo(&SetInfoRequest{FileInfoClass: FileRenameInformation,
		Input: &FileRenameInformationType2Encoder{ReplaceIfExists: flag, FileName: target}})
	if err == nil {
		f.name = target
	}
	return err
}

func (f *File) SetDeletePending(pending bool) error {
	var flag uint8
	if pending {
		flag = 1
	}
	return f.setInfo(&SetInfoRequest{FileInfoClass: FileDispositionInformation,
		Input: &FileDispositionInformationEncoder{DeletePending: flag}})
}

// Reserve changes allocation, not logical EOF (except server-required shrink).
func (f *File) Reserve(length int64) error {
	if length < 0 {
		return os.ErrInvalid
	}
	return f.setInfo(&SetInfoRequest{FileInfoClass: FileAllocationInformation,
		Input: allocationSize(length)})
}

type allocationSize int64

func (allocationSize) Size() int { return 8 }
func (n allocationSize) Encode(p []byte) {
	value := uint64(n)
	for i := uint(0); i < 8; i++ {
		p[i] = byte(value >> (8 * i))
	}
}

func (f *File) SetAttributes(attributes uint32) error {
	return f.setInfo(&SetInfoRequest{FileInfoClass: FileBasicInformation,
		Input: &FileBasicInformationEncoder{FileAttributes: attributes}})
}

func (f *File) Chtimes(atime, mtime time.Time) error {
	return f.setInfo(&SetInfoRequest{FileInfoClass: FileBasicInformation,
		Input: &FileBasicInformationEncoder{LastAccessTime: NsecToFiletime(atime.UnixNano()), LastWriteTime: NsecToFiletime(mtime.UnixNano())}})
}
