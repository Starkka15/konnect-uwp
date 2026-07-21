using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Windows.Networking.Sockets;
using Windows.Storage;
using ZorinConnect.Backends.Lan;
using ZorinConnect.Backends.Sftp;
using ZorinConnect.Core;

namespace ZorinConnect.Plugins
{
    /// <summary>
    /// SFTP / Mount (SPEC T36). On kdeconnect.sftp.request{startBrowsing:true} it spins up our
    /// hand-rolled SSH2+SFTP server (SshServer/SftpSubsystem) on a free port 1739..1764, then sends
    /// kdeconnect.sftp with ip/port/user/password/path/multiPaths/pathNames so the desktop mounts it.
    /// Exposes the Pictures/Music/Videos libraries (the ones we hold caps for), each as a root.
    /// </summary>
    public sealed class SftpPlugin : IPlugin
    {
        private const string RequestType = "kdeconnect.sftp.request";
        private const string ReplyType = "kdeconnect.sftp";
        private const string User = "kdeconnect";
        private const int MinPort = 1739, MaxPort = 1764;

        private PluginContext _ctx;
        private StreamSocketListener _listener;
        private SshCrypto _crypto;
        private string _password;
        private int _port;

        public string Key => "SftpPlugin";
        public string DisplayName => "SFTP";
        public bool EnabledByDefault => true;
        public IEnumerable<string> SupportedPacketTypes => new[] { RequestType };
        public IEnumerable<string> OutgoingPacketTypes => new[] { ReplyType };

        public void OnCreate(PluginContext context) { _ctx = context; }
        public void OnDestroy() { StopServer(); }

        public bool OnPacketReceived(NetworkPacket np)
        {
            if (np.Type != RequestType) return false;
            if (np.GetBool("startBrowsing")) { var _ = StartBrowsingAsync(); }
            return true;
        }

        private async Task StartBrowsingAsync()
        {
            try
            {
                var raw = await GatherRootsAsync();
                if (raw.Count == 0)
                {
                    _ctx?.SendPacket(new NetworkPacket(ReplyType).Set("errorMessage", "No accessible storage"));
                    return;
                }

                // Final root names = sanitized + collision-free labels ("Main", "SD Card", …), used
                // IDENTICALLY for the advertised paths and the server's virtual root keys.
                var rawNames = new List<string>(raw.Count);
                foreach (var r in raw) rawNames.Add(r.Key);
                var names = StorageRoots.UniqueNames(rawNames);
                var named = new List<KeyValuePair<string, StorageFolder>>(raw.Count);
                for (int i = 0; i < raw.Count; i++)
                    named.Add(new KeyValuePair<string, StorageFolder>(names[i], raw[i].Value));

                StopServer();
                _crypto = new SshCrypto();
                _password = RandomPassword(28);

                if (!await BindListenerAsync(named))
                {
                    _ctx?.SendPacket(new NetworkPacket(ReplyType).Set("errorMessage", "No free SFTP port"));
                    return;
                }

                var ip = NetworkHelper.LocalIPv4();
                var multiPaths = new JArray();
                var pathNames = new JArray();
                foreach (var n in names) { multiPaths.Add("/" + n); pathNames.Add(n); }

                var reply = new NetworkPacket(ReplyType)
                    .Set("ip", ip ?? "")
                    .Set("port", _port)
                    .Set("user", User)
                    .Set("password", _password)
                    .Set("path", "/")
                    .Set("multiPaths", multiPaths)
                    .Set("pathNames", pathNames);
                _ctx?.SendPacket(reply);
                StartupTrace.Mark($"sftp-listening:{ip}:{_port} roots={string.Join(",", names)}");
            }
            catch (Exception e)
            {
                StartupTrace.MarkError("sftp-start", e);
                _ctx?.SendPacket(new NetworkPacket(ReplyType).Set("errorMessage", e.Message));
            }
        }

        private async Task<bool> BindListenerAsync(List<KeyValuePair<string, StorageFolder>> named)
        {
            for (int port = MinPort; port <= MaxPort; port++)
            {
                var listener = new StreamSocketListener();
                listener.ConnectionReceived += (s, e) =>
                {
                    var sock = e.Socket;
                    Windows.System.Threading.ThreadPool.RunAsync(_ =>
                    {
                        try { new SshServer(sock, _crypto, User, _password, named, m => _ctx?.Log?.Invoke(m)).Run(); }
                        catch (Exception ex) { StartupTrace.Mark($"sftp-conn-err:{ex.Message}"); }
                    });
                };
                try { await listener.BindServiceNameAsync(port.ToString()); _listener = listener; _port = port; return true; }
                catch { listener.Dispose(); }
            }
            return false;
        }

        /// <summary>(name, folder) roots to expose: the user's named grants, else media-library defaults.</summary>
        private static async Task<List<KeyValuePair<string, StorageFolder>>> GatherRootsAsync()
        {
            var roots = new List<KeyValuePair<string, StorageFolder>>();
            void Add(string name, StorageFolder f) { if (f != null) roots.Add(new KeyValuePair<string, StorageFolder>(name, f)); }

            // Prefer the user's named grants (FolderPicker -> FutureAccessList) — any location incl.
            // SD card, no special cap. Fall back to media libraries only when nothing is granted yet.
            foreach (var m in await StorageRoots.GetMountRootsAsync()) Add(m.Name, m.Folder);
            if (roots.Count == 0)
            {
                try { Add("Pictures", KnownFolders.PicturesLibrary); } catch { }
                try { Add("Music", KnownFolders.MusicLibrary); } catch { }
                try { Add("Videos", KnownFolders.VideosLibrary); } catch { }
            }
            return roots;
        }

        private void StopServer()
        {
            try { _listener?.Dispose(); } catch { }
            _listener = null;
        }

        private static string RandomPassword(int len)
        {
            const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            var bytes = new byte[len];
            SshCrypto.Random.NextBytes(bytes);
            var sb = new System.Text.StringBuilder(len);
            foreach (var b in bytes) sb.Append(chars[b % chars.Length]);
            return sb.ToString();
        }
    }
}
