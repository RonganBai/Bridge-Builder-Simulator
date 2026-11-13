using System;

namespace BridgeArchitect3D.Data
{
    /// <summary>A file that records which game version wrote it (designs, progress, settings).</summary>
    public interface IVersionStamped
    {
        string GameVersion { get; set; }
    }

    /// <summary>
    /// The game version (semantic: major.minor.patch). The single source of truth: project.godot
    /// (application/config/version), export_presets.cfg (file / product version) and the installer
    /// (installer/BridgeArchitect3D.iss AppVersion) must match it — a test checks this.
    /// Bump: patch = fixes, minor = new content or rules that keep old saves loading, major = breaking change.
    /// Every bump gets a CHANGELOG.md entry.
    /// </summary>
    public static class GameVersion
    {
        public const string Current = "1.1.0";

        /// <summary>Files written before versioning carry no version; they count as 1.0.0.</summary>
        public const string Unversioned = "1.0.0";

        public static Version Parse(string v) =>
            System.Version.TryParse(string.IsNullOrWhiteSpace(v) ? Unversioned : v.Trim().TrimStart('v', 'V'), out var p) ? p : new Version(1, 0, 0);

        /// <summary>&lt; 0 when a is older than b.</summary>
        public static int Compare(string a, string b) => Parse(a).CompareTo(Parse(b));

        public static bool IsOlder(string v) => Compare(v, Current) < 0;
        /// <summary>Written by a newer game than this one (may contain data this version does not know).</summary>
        public static bool IsNewer(string v) => Compare(v, Current) > 0;

        public static string Display => "v" + Current;

        /// <summary>Stamp a file with the running version just before it is written.</summary>
        public static T Stamp<T>(T data)
        {
            if (data is IVersionStamped s) s.GameVersion = Current;
            return data;
        }
    }
}
