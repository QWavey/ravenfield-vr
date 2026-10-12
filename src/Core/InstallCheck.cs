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
        private static readonly string[] NativeFiles = { "XRSDKOpenVR.dll", "openvr_api.dll", "ucrtbased.dll" };

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
                    return "VR runtime files are missing from ravenfield_Data (Plugins/x86_64/XRSDKOpenVR.dll, openvr_api.dll, ucrtbased.dll " +
                           "and UnitySubsystems/XRSDKOpenVR/UnitySubsystemsManifest.json). Run install.ps1 from the Ravenfield VR release. Playing flat.";
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
                return "Installed the OpenVR runtime into ravenfield_Data. Unity only discovers it at startup: restart Ravenfield to play in VR. Playing flat this session.";
            }
            catch (Exception e)
            {
                return "Could not verify the VR runtime install: " + e.Message + ". Playing flat.";
            }
        }
    }
}
