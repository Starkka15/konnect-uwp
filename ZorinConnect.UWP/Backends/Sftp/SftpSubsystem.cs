using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;
using ZorinConnect.Core;

namespace ZorinConnect.Backends.Sftp
{
    /// <summary>
    /// SFTP v3 subsystem over one SSH channel. Maps a virtual "/" (the exposed roots by name) onto
    /// StorageFolders. Read/write/list/mkdir/remove/rename supported. Packets are length-prefixed;
    /// processed in-order on a single async loop (responses carry the request id anyway).
    /// </summary>
    internal sealed class SftpSubsystem
    {
        // request types
        private const byte INIT = 1, OPEN = 3, CLOSE = 4, READ = 5, WRITE = 6, LSTAT = 7, FSTAT = 8,
            SETSTAT = 9, FSETSTAT = 10, OPENDIR = 11, READDIR = 12, REMOVE = 13, MKDIR = 14, RMDIR = 15,
            REALPATH = 16, STAT = 17, RENAME = 18, READLINK = 19, SYMLINK = 20;
        // response types
        private const byte VERSION = 2, STATUS = 101, HANDLE = 102, DATA = 103, NAME = 104, ATTRS = 105;
        // status codes
        private const uint OK = 0, EOF = 1, NO_SUCH_FILE = 2, PERMISSION_DENIED = 3, FAILURE = 4, OP_UNSUPPORTED = 8;
        // attr flags
        private const uint ATTR_SIZE = 0x1, ATTR_PERMISSIONS = 0x4, ATTR_ACMODTIME = 0x8;

        private readonly Dictionary<string, StorageFolder> _roots;
        private readonly Action<byte[]> _send;
        private readonly Action<string> _log;

        private readonly List<byte> _buffer = new List<byte>();
        private readonly object _lock = new object();
        private bool _processing;

        private int _handleSeq;
        private readonly Dictionary<string, DirHandle> _dirs = new Dictionary<string, DirHandle>();
        private readonly Dictionary<string, FileHandle> _files = new Dictionary<string, FileHandle>();

        private sealed class DirHandle { public List<IStorageItem> Items; public List<string> Names; public int Cursor; public string Path; }
        private sealed class FileHandle { public Stream Stream; public StorageFile File; public bool Write; }

        public SftpSubsystem(List<KeyValuePair<string, StorageFolder>> roots, Action<byte[]> send, Action<string> log)
        {
            // Keyed by the SAME names the sftp packet advertised (multiPaths/pathNames) so every
            // advertised root resolves exactly.
            _roots = new Dictionary<string, StorageFolder>();
            foreach (var kv in roots) _roots[kv.Key] = kv.Value;
            _send = send;
            _log = log;
        }

        public void Feed(byte[] data)
        {
            lock (_lock) { _buffer.AddRange(data); if (_processing) return; _processing = true; }
            var _ = ProcessLoopAsync();
        }

        private async Task ProcessLoopAsync()
        {
            while (true)
            {
                byte[] packet;
                lock (_lock)
                {
                    if (_buffer.Count < 4) { _processing = false; return; }
                    int len = (_buffer[0] << 24) | (_buffer[1] << 16) | (_buffer[2] << 8) | _buffer[3];
                    if (_buffer.Count < 4 + len) { _processing = false; return; }
                    packet = _buffer.GetRange(4, len).ToArray();
                    _buffer.RemoveRange(0, 4 + len);
                }
                try { await HandleAsync(packet); }
                catch (Exception e) { StartupTrace.MarkError("sftp-handler", e); }
            }
        }

        private async Task HandleAsync(byte[] packet)
        {
            var r = new SshReader(packet, 0);
            byte type = r.Byte();
            if (type == INIT)
            {
                StartupTrace.Mark("sftp-init->ver3");
                var w = new SshWriter(); w.Byte(VERSION); w.UInt32(3);
                SendSftp(w.ToArray());
                return;
            }
            uint id = r.UInt32();
            switch (type)
            {
                case REALPATH: await DoRealPath(id, r); break;
                case OPENDIR: await DoOpenDir(id, r); break;
                case READDIR: DoReadDir(id, r); break;
                case CLOSE: DoClose(id, r); break;
                case STAT: case LSTAT: await DoStat(id, r); break;
                case FSTAT: DoFStat(id, r); break;
                case OPEN: await DoOpen(id, r); break;
                case READ: await DoRead(id, r); break;
                case WRITE: await DoWrite(id, r); break;
                case MKDIR: await DoMkdir(id, r); break;
                case RMDIR: case REMOVE: await DoRemove(id, r); break;
                case RENAME: await DoRename(id, r); break;
                case SETSTAT: case FSETSTAT: SendStatus(id, OK, "ok"); break; // accept, no-op
                // We expose no symlinks. Mirror openssh: readlink on a non-link fails with EINVAL ->
                // SSH_FX_FAILURE (NOT OP_UNSUPPORTED, which makes gvfs reject query_info and retry-loop).
                // READLINK: gvfs issues this during query_info and FAILS the whole query_info if it
                // returns an error status (-> Nautilus "unknown type", enumerate cancelled). We expose
                // no symlinks, so reply SUCCESS with the path as its own target; lstat already reported
                // the item as non-symlink, so gvfs ignores the target and query_info completes.
                case READLINK: DoReadLink(id, r); break;
                case SYMLINK: SendStatus(id, PERMISSION_DENIED, "read-only"); break;
                default: SendStatus(id, OP_UNSUPPORTED, "unsupported"); break;
            }
        }

        // ---------- handlers ----------

        private async Task DoRealPath(uint id, SshReader r)
        {
            var path = Normalize(r.Utf8String());
            var item = await ResolveAsync(path);
            bool isDir = item is StorageFolder || (item == null && path == "/");
            var w = new SshWriter();
            w.Byte(NAME); w.UInt32(id); w.UInt32(1);
            w.String(path);                                   // filename
            w.String(LongName(path, isDir, item as StorageFile)); // proper ls -l longname (was bare path)
            WriteAttrs(w, item as StorageFile, isDir);
            SendSftp(w.ToArray());
        }

        private void DoReadLink(uint id, SshReader r)
        {
            var path = Normalize(r.Utf8String());
            // We expose NO symlinks. A NAME reply would make gvfs treat the path as a symlink-to-self
            // and loop -> "unknown type". openssh returns EINVAL -> SSH_FX_FAILURE for readlink on a
            // non-link; gvfs reads that as "not a symlink" and proceeds to enumerate.
            SendStatus(id, FAILURE, "not a symlink");
        }

        private async Task DoStat(uint id, SshReader r)
        {
            var path = Normalize(r.Utf8String());
            var item = await ResolveAsync(path);
            if (item == null && path != "/") { SendStatus(id, NO_SUCH_FILE, "no such file"); return; }
            bool isDir = item is StorageFolder || path == "/";
            var w = new SshWriter(); w.Byte(ATTRS); w.UInt32(id);
            WriteAttrs(w, item as StorageFile, isDir);
            SendSftp(w.ToArray());
        }

        private void DoFStat(uint id, SshReader r)
        {
            var h = r.Utf8String();
            var w = new SshWriter(); w.Byte(ATTRS); w.UInt32(id);
            if (_files.TryGetValue(h, out var fh)) WriteAttrs(w, fh.File, false);
            else WriteAttrs(w, null, true);
            SendSftp(w.ToArray());
        }

        private async Task DoOpenDir(uint id, SshReader r)
        {
            var path = Normalize(r.Utf8String());
            var items = new List<IStorageItem>();
            List<string> names = null;
            if (path == "/")
            {
                // Virtual root: list each root under its LABEL ("Main"/"SD Card"), NOT the folder's real
                // name ("D:\"/"Public") — the label is the path segment the client must use to descend.
                names = new List<string>();
                foreach (var kv in _roots) { items.Add(kv.Value); names.Add(kv.Key); }
            }
            else
            {
                var folder = await ResolveAsync(path) as StorageFolder;
                if (folder == null) { SendStatus(id, NO_SUCH_FILE, "no such dir"); return; }
                foreach (var it in await folder.GetItemsAsync()) items.Add(it);
            }
            var handle = "d" + (_handleSeq++);
            _dirs[handle] = new DirHandle { Items = items, Names = names, Cursor = 0, Path = path };
            StartupTrace.Mark($"sftp-opendir:{path} n={items.Count} h={handle}");
            SendHandle(id, handle);
        }

        private void DoReadDir(uint id, SshReader r)
        {
            var h = r.Utf8String();
            if (!_dirs.TryGetValue(h, out var dh) || dh.Cursor >= dh.Items.Count)
            { SendStatus(id, EOF, "eof"); return; }

            int take = Math.Min(50, dh.Items.Count - dh.Cursor);
            var w = new SshWriter(); w.Byte(NAME); w.UInt32(id); w.UInt32((uint)take);
            for (int i = 0; i < take; i++)
            {
                int idx = dh.Cursor++;
                var it = dh.Items[idx];
                bool isDir = it is StorageFolder;
                string entryName = dh.Names != null ? dh.Names[idx] : it.Name; // root -> label, else real name
                w.String(entryName);
                w.String(LongName(entryName, isDir, it as StorageFile));
                WriteAttrs(w, it as StorageFile, isDir);
            }
            SendSftp(w.ToArray());
        }

        private void DoClose(uint id, SshReader r)
        {
            var h = r.Utf8String();
            _dirs.Remove(h);
            if (_files.TryGetValue(h, out var fh))
            {
                try { fh.Stream?.Flush(); fh.Stream?.Dispose(); } catch { }
                _files.Remove(h);
            }
            SendStatus(id, OK, "ok");
        }

        private async Task DoOpen(uint id, SshReader r)
        {
            var path = Normalize(r.Utf8String());
            uint flags = r.UInt32();
            // pflags: 0x1 READ, 0x2 WRITE, 0x8 CREAT, 0x10 TRUNC
            bool write = (flags & 0x2) != 0;
            try
            {
                StorageFile file;
                if (write)
                {
                    var pr = await ParentAsync(path);
                    if (pr.Parent == null) { SendStatus(id, NO_SUCH_FILE, "no parent"); return; }
                    file = await pr.Parent.CreateFileAsync(pr.Name, CreationCollisionOption.ReplaceExisting);
                    var s = await file.OpenStreamForWriteAsync();
                    var fh = new FileHandle { Stream = s, File = file, Write = true };
                    var handle = "f" + (_handleSeq++); _files[handle] = fh; SendHandle(id, handle);
                }
                else
                {
                    file = await ResolveAsync(path) as StorageFile;
                    if (file == null) { SendStatus(id, NO_SUCH_FILE, "no such file"); return; }
                    var s = await file.OpenStreamForReadAsync();
                    var fh = new FileHandle { Stream = s, File = file, Write = false };
                    var handle = "f" + (_handleSeq++); _files[handle] = fh; SendHandle(id, handle);
                }
            }
            catch (Exception e) { _log?.Invoke($"sftp open failed: {e.Message}"); SendStatus(id, FAILURE, "open failed"); }
        }

        private async Task DoRead(uint id, SshReader r)
        {
            var h = r.Utf8String();
            ulong offset = r.UInt64();
            uint length = r.UInt32();
            if (!_files.TryGetValue(h, out var fh)) { SendStatus(id, FAILURE, "bad handle"); return; }
            try
            {
                fh.Stream.Seek((long)offset, SeekOrigin.Begin);
                var buf = new byte[length];
                int read = await fh.Stream.ReadAsync(buf, 0, (int)length);
                if (read <= 0) { SendStatus(id, EOF, "eof"); return; }
                var w = new SshWriter(); w.Byte(DATA); w.UInt32(id); w.UInt32((uint)read); w.Bytes(buf, 0, read);
                SendSftp(w.ToArray());
            }
            catch { SendStatus(id, FAILURE, "read failed"); }
        }

        private async Task DoWrite(uint id, SshReader r)
        {
            var h = r.Utf8String();
            ulong offset = r.UInt64();
            var data = r.String();
            if (!_files.TryGetValue(h, out var fh) || !fh.Write) { SendStatus(id, FAILURE, "bad handle"); return; }
            try
            {
                fh.Stream.Seek((long)offset, SeekOrigin.Begin);
                await fh.Stream.WriteAsync(data, 0, data.Length);
                SendStatus(id, OK, "ok");
            }
            catch { SendStatus(id, FAILURE, "write failed"); }
        }

        private async Task DoMkdir(uint id, SshReader r)
        {
            var path = Normalize(r.Utf8String());
            var pr = await ParentAsync(path);
            if (pr.Parent == null) { SendStatus(id, FAILURE, "no parent"); return; }
            try { await pr.Parent.CreateFolderAsync(pr.Name, CreationCollisionOption.OpenIfExists); SendStatus(id, OK, "ok"); }
            catch { SendStatus(id, FAILURE, "mkdir failed"); }
        }

        private async Task DoRemove(uint id, SshReader r)
        {
            var path = Normalize(r.Utf8String());
            var item = await ResolveAsync(path);
            if (item == null) { SendStatus(id, NO_SUCH_FILE, "no such"); return; }
            try { await item.DeleteAsync(StorageDeleteOption.PermanentDelete); SendStatus(id, OK, "ok"); }
            catch { SendStatus(id, PERMISSION_DENIED, "delete failed"); }
        }

        private async Task DoRename(uint id, SshReader r)
        {
            var oldPath = Normalize(r.Utf8String());
            var newPath = Normalize(r.Utf8String());
            var item = await ResolveAsync(oldPath);
            var pr = await ParentAsync(newPath);
            if (item == null || pr.Parent == null) { SendStatus(id, NO_SUCH_FILE, "no such"); return; }
            try { await item.RenameAsync(pr.Name, NameCollisionOption.ReplaceExisting); SendStatus(id, OK, "ok"); }
            catch { SendStatus(id, FAILURE, "rename failed"); }
        }

        // ---------- path resolution ----------

        private static string Normalize(string p)
        {
            if (string.IsNullOrEmpty(p) || p == ".") return "/";
            p = p.Replace('\\', '/');
            if (!p.StartsWith("/")) p = "/" + p;
            while (p.Length > 1 && p.EndsWith("/")) p = p.Substring(0, p.Length - 1);
            return p;
        }

        private async Task<IStorageItem> ResolveAsync(string path)
        {
            if (path == "/") return null; // virtual root
            var parts = path.Trim('/').Split('/');
            if (!_roots.TryGetValue(parts[0], out var folder)) return null;
            IStorageItem current = folder;
            for (int i = 1; i < parts.Length; i++)
            {
                if (!(current is StorageFolder f)) return null;
                current = await f.TryGetItemAsync(parts[i]);
                if (current == null) return null;
            }
            return current;
        }

        private sealed class ParentRef { public StorageFolder Parent; public string Name; }

        private async Task<ParentRef> ParentAsync(string path)
        {
            var parts = path.Trim('/').Split('/');
            if (parts.Length == 1) return new ParentRef { Parent = null, Name = null }; // can't create at virtual root
            var parentPath = "/" + string.Join("/", parts.Take(parts.Length - 1));
            var parent = await ResolveAsync(parentPath) as StorageFolder;
            return new ParentRef { Parent = parent, Name = parts[parts.Length - 1] };
        }

        // ---------- attrs / framing ----------

        private const uint ATTR_UIDGID = 0x2;

        private void WriteAttrs(SshWriter w, StorageFile file, bool isDir)
        {
            // Emit the FULL attr set a real sftp-server (openssh) sends: SIZE|UIDGID|PERMISSIONS|ACMODTIME.
            // gvfs refuses to treat the mount root as a directory (shows "unknown type", never READDIRs)
            // when only PERMISSIONS is present -> it wants a complete stat.
            long size = 0;
            uint perms = isDir ? 0x41EDu /*040755*/ : 0x81A4u /*0100644*/;
            if (file != null)
            {
                try { size = (long)file.GetBasicPropertiesAsync().AsTask().GetAwaiter().GetResult().Size; } catch { }
            }
            uint flags = ATTR_SIZE | ATTR_UIDGID | ATTR_PERMISSIONS | ATTR_ACMODTIME;
            w.UInt32(flags);
            w.UInt64((ulong)size);   // size
            w.UInt32(1000); w.UInt32(1000); // uid, gid
            w.UInt32(perms);         // permissions (S_IFDIR / S_IFREG in high bits)
            w.UInt32(1704067200); w.UInt32(1704067200); // atime, mtime (fixed: 2024-01-01)
        }

        private static string LongName(string name, bool isDir, StorageFile file)
        {
            string perm = isDir ? "drwxr-xr-x" : "-rw-r--r--";
            long size = 0;
            if (file != null) { try { size = (long)file.GetBasicPropertiesAsync().AsTask().GetAwaiter().GetResult().Size; } catch { } }
            return $"{perm} 1 kde kde {size,10} Jan  1 00:00 {name}";
        }

        private void SendHandle(uint id, string handle)
        {
            var w = new SshWriter(); w.Byte(HANDLE); w.UInt32(id); w.String(handle); SendSftp(w.ToArray());
        }

        private void SendStatus(uint id, uint code, string msg)
        {
            var w = new SshWriter(); w.Byte(STATUS); w.UInt32(id); w.UInt32(code); w.String(msg); w.String("");
            SendSftp(w.ToArray());
        }

        /// <summary>Frame an SFTP packet (length-prefixed) and hand it to the SSH channel.</summary>
        private void SendSftp(byte[] payload)
        {
            var w = new SshWriter(); w.UInt32((uint)payload.Length); w.Bytes(payload);
            _send(w.ToArray());
        }
    }
}
