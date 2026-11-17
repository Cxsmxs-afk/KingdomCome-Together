// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace KCDMP_launcher.Components.Shared
{
    /// <summary>
    /// WO-32 follow-up: detects a partial install at launcher startup.
    ///
    /// Motivated by a real incident: Setup 0.11.8 ran while agent/relay
    /// processes were alive, silently left KcdMpClient.dll and
    /// KcdMpServer.dll on an old build while the launcher itself updated,
    /// and the newly-shipped NPC sync was inert with no error anywhere. The
    /// wire protocol cannot catch a mixed install because the mix is inside
    /// ONE machine -- so the launcher, which is the piece the user actually
    /// looks at, has to.
    ///
    /// Every sibling assembly is stamped at build time from the same
    /// repo-root VERSION file this launcher is stamped from (see each
    /// csproj's VersionFileContent property), so on a healthy install all
    /// versions are identical by construction. Any difference means the
    /// install directory holds a mix of two builds.
    ///
    /// This warns; it does not block. A false positive that stops a tester
    /// from connecting would be worse than a warned mismatch, and the check
    /// compares string stamps, not behaviour.
    /// </summary>
    public static class InstallIntegrity
    {
        // The stamped .NET assemblies that ship beside the launcher. The
        // native KCDMP.dll carries no InformationalVersion and is deliberately
        // not listed. MasterServer ships in its own subfolder with its own
        // copies; checked via its relative path.
        private static readonly string[] SiblingAssemblies =
        [
            "KcdMpClient.dll",
            "KcdMpServer.dll",
            "KcdMp.Protocol.dll",
            "KcdMp.Farkle.dll",
        ];

        /// <summary>
        /// Null when the install looks whole; otherwise a user-facing message
        /// naming what is stale. A missing file is reported too -- an install
        /// that lost a component entirely is the same failure, worse.
        /// </summary>
        public static string? CheckForPartialInstall()
        {
            string baseDir = AppContext.BaseDirectory;
            var wrong = new List<string>();

            foreach (var name in SiblingAssemblies)
            {
                string path = Path.Combine(baseDir, name);
                if (!File.Exists(path))
                {
                    wrong.Add($"{name} (missing)");
                    continue;
                }

                string? version;
                try { version = FileVersionInfo.GetVersionInfo(path).ProductVersion; }
                catch { continue; }   // unreadable is not evidence of a mix

                // Older, unstamped builds carry "1.0.0+<git hash>". Any
                // difference from the launcher's own stamp counts -- the
                // point is "one build, one version", not version ordering.
                if (!string.IsNullOrEmpty(version) && version != Globals.Version)
                    wrong.Add($"{name} is {version}");
            }

            string dllPath = Path.Combine(baseDir, "KCDMP.dll");
            string? pakPath = PakPath(baseDir);
            if (File.Exists(dllPath) || pakPath is not null)
            {
                string? dllStamp = StampOf(dllPath);
                string? pakStamp = StampOf(pakPath);
                if (File.Exists(dllPath) && dllStamp is null) wrong.Add("KCDMP.dll has no build stamp");
                else if (dllStamp is not null && dllStamp != Globals.Version) wrong.Add($"KCDMP.dll is {dllStamp}");
                if (pakPath is not null && pakStamp is null) wrong.Add("kdcmp.pak has no build stamp");
                else if (pakStamp is not null && pakStamp != Globals.Version) wrong.Add($"kdcmp.pak is {pakStamp}");
                if (dllStamp is not null && pakStamp is not null && dllStamp != pakStamp)
                    wrong.Add($"KCDMP.dll ({dllStamp}) and kdcmp.pak ({pakStamp}) are not the same build");
            }

            if (wrong.Count == 0) return null;

            return $"This install is a mix of two builds -- the launcher is {Globals.Version} but " +
                   string.Join(", ", wrong) + ". " +
                   "That usually means an installer ran while the launcher, agent, relay or game was still open, " +
                   "and the files in use silently kept their old version. " +
                   "Close the game and the launcher, then extract the new zip over the Modding Tools folder (overwrite) or re-run Setup. install-verify.txt should say PASS.";
        }

        /// <summary>The ASCII stamp <c>KCDMP-BUILD:version</c> inside a DLL or a pak, or null.</summary>
        public static string? StampOf(string? path)
        {
            if (path is null || !File.Exists(path)) return null;
            try
            {
                var bytes = File.ReadAllBytes(path);
                var needle = System.Text.Encoding.ASCII.GetBytes("KCDMP-BUILD:");
                for (int i = 0; i + needle.Length + 1 < bytes.Length; i++)
                {
                    bool hit = true;
                    for (int j = 0; j < needle.Length; j++) if (bytes[i + j] != needle[j]) { hit = false; break; }
                    if (!hit) continue;
                    int s = i + needle.Length;
                    int e = s;
                    while (e < bytes.Length && e - s < 32 && IsStampChar(bytes[e])) e++;
                    if (e > s) return System.Text.Encoding.ASCII.GetString(bytes, s, e - s);
                }
            }
            catch { return null; }
            return null;
        }

        static bool IsStampChar(byte b) => b == (byte)'.' || (b >= (byte)'0' && b <= (byte)'9');

        static string? PakPath(string baseDir)
        {
            foreach (var rel in new[] { Path.Combine("mod", "kdcmp", "Data", "kdcmp.pak"), Path.Combine("..", "Mods", "kdcmp", "Data", "kdcmp.pak") })
            {
                string p = Path.GetFullPath(Path.Combine(baseDir, rel));
                if (File.Exists(p)) return p;
            }
            return null;
        }
    }
}
