using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace RavenfieldVR.Core
{
    [BepInPlugin(PluginInfo.Guid, PluginInfo.Name, PluginInfo.Version)]
    [BepInProcess("ravenfield.exe")]
    public class VRPlugin : BaseUnityPlugin
    {
        public static VRPlugin Instance { get; private set; }

        /// <summary>Folder containing RavenfieldVR.dll (input manifests live under it).</summary>
        public static string PluginDir { get; private set; }

        private Harmony _harmony;

        private void Awake()
        {
            Instance = this;
            PluginDir = Path.GetDirectoryName(Info.Location);
            Log.Init(Logger);
            VRConfig.Bind(Config);
            Log.Info($"{PluginInfo.Name} {PluginInfo.Version} loading (Unity {Application.unityVersion}).");

            string installProblem = InstallCheck.Run(Paths.GameRootPath);
            bool wantVR = VRConfig.EnableVR.Value;
            if (installProblem != null)
            {
                Log.Error(installProblem);
                wantVR = false;
            }

            _harmony = new Harmony(PluginInfo.Guid);
            try
            {
                _harmony.PatchAll(Assembly.GetExecutingAssembly());
                Log.Info($"Applied {_harmony.GetPatchedMethods().Count()} Harmony patches.");
            }
            catch (System.Exception e)
            {
                // A failed patch usually means a game update changed a signature; play flat instead of crashing.
                Log.Exception("Harmony patching failed, VR disabled", e);
                _harmony.UnpatchSelf();
                wantVR = false;
            }

            var host = new GameObject("RavenfieldVR");
            DontDestroyOnLoad(host);
            host.hideFlags = HideFlags.DontSave;
            VRManager manager = host.AddComponent<VRManager>();
            manager.RequestVR = wantVR;
            AutoTest.Install();
            PerfProbe.Enabled |= System.Environment.GetCommandLineArgs().Any(a => a.Equals("-rfvr-perf", System.StringComparison.OrdinalIgnoreCase));
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }
    }
}
