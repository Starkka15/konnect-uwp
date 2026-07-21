using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.AccessCache;

namespace ZorinConnect.Core
{
    /// <summary>
    /// User-granted storage locations — the UWP analog of Android's SAF. The user picks folders with a
    /// FolderPicker and we persist access in the FutureAccessList, so we can reopen them later with NO
    /// special capability (works on W10M, unlike broadFileSystemAccess). Entries are tagged in metadata:
    ///   "share"        — the single folder where received (Share) files are saved
    ///   "mount:&lt;Name&gt;" — a folder exposed as an SFTP root under the user-chosen display name (e.g.
    ///                    "Main", "SD Card"). The Name becomes the root's path segment, so the user's
    ///                    label is the sanitization (no ugly "D:\" segments).
    /// If nothing is picked, callers fall back to the media libraries we hold caps for.
    /// </summary>
    public static class StorageRoots
    {
        public const string ShareTag = "share";
        public const string MountPrefix = "mount:";

        public sealed class MountRoot
        {
            public string Token;
            public string Name;
            public StorageFolder Folder;
        }

        private static StorageItemAccessList Fal => StorageApplicationPermissions.FutureAccessList;

        // ---- share (received files) destination ----

        public static string SetShareFolder(StorageFolder folder)
        {
            RemoveWhere(m => m == ShareTag);
            return Fal.Add(folder, ShareTag);
        }

        public static async Task<StorageFolder> GetShareFolderAsync()
        {
            foreach (var e in Fal.Entries)
                if (e.Metadata == ShareTag)
                    try { return await Fal.GetFolderAsync(e.Token); } catch { }
            return null;
        }

        // ---- named SFTP mount roots ----

        /// <summary>Grant (or re-grant) a mount root under a fixed display name; replaces any prior one.</summary>
        public static string SetMountRoot(StorageFolder folder, string name)
        {
            RemoveWhere(m => m == MountPrefix + name);
            return Fal.Add(folder, MountPrefix + name);
        }

        public static async Task<List<MountRoot>> GetMountRootsAsync()
        {
            var list = new List<MountRoot>();
            foreach (var e in Fal.Entries)
                if (e.Metadata != null && e.Metadata.StartsWith(MountPrefix, StringComparison.Ordinal))
                    try
                    {
                        list.Add(new MountRoot
                        {
                            Token = e.Token,
                            Name = e.Metadata.Substring(MountPrefix.Length),
                            Folder = await Fal.GetFolderAsync(e.Token),
                        });
                    }
                    catch { }
            return list;
        }

        public static void ClearMountRoots() =>
            RemoveWhere(m => m != null && m.StartsWith(MountPrefix, StringComparison.Ordinal));

        // ---- shared name hygiene (SFTP path segments must be clean + unique) ----

        /// <summary>Clean a label into a safe SFTP path segment: no ':' '\' '/' or control chars.</summary>
        public static string SafeSegment(string name)
        {
            if (string.IsNullOrEmpty(name)) return "root";
            var sb = new System.Text.StringBuilder(name.Length);
            foreach (var c in name)
                if (c != ':' && c != '\\' && c != '/' && c >= 0x20) sb.Append(c);
            var seg = sb.ToString().Trim().Trim('.');
            return string.IsNullOrEmpty(seg) ? "root" : seg;
        }

        /// <summary>Sanitized, collision-free segments for the given labels, preserving order.</summary>
        public static List<string> UniqueNames(IList<string> rawNames)
        {
            var names = new List<string>(rawNames.Count);
            var seen = new Dictionary<string, int>();
            foreach (var raw in rawNames)
            {
                var baseName = SafeSegment(raw);
                if (seen.TryGetValue(baseName, out int n)) { seen[baseName] = ++n; names.Add($"{baseName} ({n})"); }
                else { seen[baseName] = 1; names.Add(baseName); }
            }
            return names;
        }

        private static void RemoveWhere(Func<string, bool> metadataMatch)
        {
            var toks = new List<string>();
            foreach (var e in Fal.Entries) if (metadataMatch(e.Metadata)) toks.Add(e.Token);
            foreach (var t in toks) Fal.Remove(t);
        }
    }
}
