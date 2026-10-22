using System;
using System.IO;

namespace RavenfieldVR.Core
{
    /// <summary>
    /// Verifies the native OpenVR XR runtime is where Unity looks for it.
    /// Unity scans ravenfield_Data/UnitySubsystems/*/UnitySubsystemsManifest.json once at startup and loads
    /// the provider DLL from ravenfield_Data/Plugins/x86_64, so the files must exist before launch. If only the
    /// plugin folder was extracted (mod manager installs), the copies shipped in plugins/RavenfieldVR/runtime are
    /// put in place and the user is told to restart once.
    /// </summary>
    public static class InstallCheck
    {
        // (The XR package's ucrtbased.dll, a debug C runtime, is not shipped: neither DLL imports it.)
        private static readonly string[] NativeFiles = { "XRSDKOpenVR.dll", "openvr_api.dll" };

        /// <returns>null when everything is in place, otherwise a message explaining why VR is off this session.</returns>
        public static string Run(string gameRoot)
        {
            try
            {
                string data = Path.Combine(gameRoot, "ravenfield_Data");
                string x64 = Path.Combine(data, "Plugins", "x86_64");
                string manifestDir = Path.Combine(data, "UnitySubsystems", "XRSDKOpenVR");
                string manifest = Path.Combine(manifestDir, "UnitySubsystemsManifest.json");

                bool missing = !File.Exists(manifest);
                foreach (string f in NativeFiles)
                {
                    missing |= !File.Exists(Path.Combine(x64, f));
                }
                if (!missing)
                {
                    return null;
                }

                string bundled = Path.Combine(VRPlugin.PluginDir ?? "", "runtime");
                if (!Directory.Exists(bundled))
                {
                    return "VR runtime files are missing from ravenfield_Data (Plugins/x86_64/XRSDKOpenVR.dll, openvr_api.dll " +
                           "and UnitySubsystems/XRSDKOpenVR/UnitySubsystemsManifest.json). Run install.ps1 from the Ravenfield VR release. Playing flat.";
                }

                // Only the release's own files: what is copied here is native code the game loads, and the manifest names the DLL
                // it loads (audit, issue #5). The OpenVR XR plugin 1.2.4's files (tools/fetch-deps.ps1 pins its package).
                string mismatch = Unexpected(bundled);
                if (mismatch != null)
                {
                    return "The VR runtime files shipped in BepInEx/plugins/RavenfieldVR/runtime are not the release's (" + mismatch +
                           "); nothing was copied. Reinstall Ravenfield VR from its release. Playing flat.";
                }

                Directory.CreateDirectory(x64);
                Directory.CreateDirectory(manifestDir);
                foreach (string f in NativeFiles)
                {
                    // Stored as .bin so BepInEx's plugin scan does not try to load them as .NET assemblies.
                    string src = Path.Combine(bundled, f + ".bin");
                    if (File.Exists(src))
                    {
                        File.Copy(src, Path.Combine(x64, f), true);
                    }
                }
                string bundledManifest = Path.Combine(bundled, "UnitySubsystemsManifest.json");
                if (File.Exists(bundledManifest))
                {
                    File.Copy(bundledManifest, manifest, true);
                }
                // A file the plugin folder lacks is not copied: said so, not "restart" (which asked for a restart on every start
                // while the runtime stayed incomplete; bug hunt, 2026-10-22).
                foreach (string f in NativeFiles)
                {
                    if (!File.Exists(Path.Combine(x64, f)))
                    {
                        return "VR runtime file ravenfield_Data/Plugins/x86_64/" + f + " is missing and BepInEx/plugins/RavenfieldVR/runtime has no copy of it. " +
                               "Reinstall Ravenfield VR from its release. Playing flat.";
                    }
                }
                if (!File.Exists(manifest))
                {
                    return "VR runtime file ravenfield_Data/UnitySubsystems/XRSDKOpenVR/UnitySubsystemsManifest.json is missing and BepInEx/plugins/RavenfieldVR/runtime " +
                           "has no copy of it. Reinstall Ravenfield VR from its release. Playing flat.";
                }
                return "Installed the OpenVR runtime into ravenfield_Data. Unity only discovers it at startup: restart Ravenfield to play in VR. Playing flat this session.";
            }
            catch (Exception e)
            {
                return "Could not verify the VR runtime install: " + e.Message + ". Playing flat.";
            }
        }

        /// <summary>SHA-256 of the files the self-repair copies, from the OpenVR XR plugin 1.2.4 package.</summary>
        private static readonly (string File, string Sha256)[] Expected =
        {
            ("XRSDKOpenVR.dll.bin", "59d3d9f6fe9e1a3484f3d70b709d379d37fcf5fe635e4f27d6956f82efb31a81"),
            ("openvr_api.dll.bin", "e61701f75930b4c47615d17f5901ca62edf1a948f5ea804d4cbbdc94ad2e6e2e"),
            ("UnitySubsystemsManifest.json", "2399997f6ddd01ea998e083bbe342103bf76ed2b41d37e73adf43ed4c5be93dc"),
        };

        /// <summary>The first bundled file that is present and differs from the release's, or null when all match.</summary>
        private static string Unexpected(string bundled)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                foreach ((string file, string expected) in Expected)
                {
                    string path = Path.Combine(bundled, file);
                    if (!File.Exists(path))
                    {
                        continue; // (a missing one is not copied either)
                    }
                    byte[] hash;
                    using (FileStream s = File.OpenRead(path))
                    {
                        hash = sha.ComputeHash(s);
                    }
                    if (!string.Equals(BitConverter.ToString(hash).Replace("-", ""), expected, StringComparison.OrdinalIgnoreCase))
                    {
                        return file + " differs";
                    }
                }
            }
            return null;
        }
    }
}
