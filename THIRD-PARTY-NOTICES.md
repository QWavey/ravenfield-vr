# Third-party components

Ravenfield VR ships or builds on the following. Their licenses apply to their parts.

| Component | Use | License | Source |
| --- | --- | --- | --- |
| BepInEx 5.4.23.5 | Mod loader (shipped) | LGPL-2.1 | https://github.com/BepInEx/BepInEx |
| HarmonyX | Runtime patching (shipped with BepInEx) | MIT | https://github.com/BepInEx/HarmonyX |
| Unity Doorstop | Loader bootstrap (shipped with BepInEx) | LGPL-2.1 | https://github.com/NeighTools/UnityDoorstop |
| OpenVR Unity XR plugin 1.2.4 (`XRSDKOpenVR.dll`, `ucrtbased.dll`, `UnitySubsystemsManifest.json`) | SteamVR display provider (shipped) | BSD-3-Clause | https://github.com/ValveSoftware/unity-xr-plugin |
| OpenVR SDK (`openvr_api.dll`, `openvr_api.cs`) | SteamVR API (shipped / compiled in) | BSD-3-Clause | https://github.com/ValveSoftware/openvr |
| SteamVR Unity Plugin: glove model `vr_glove_right_model_slim.fbx`, its colour and normal textures, reference hand poses (converted into `src/Rig/Glove`, embedded in the DLL) | The tactical glove (`Glove = Tactical`) | BSD-3-Clause (© Valve Corporation; licence in `assets/hands/steamvr-glove/LICENSE.txt`) | https://github.com/ValveSoftware/steamvr_unity_plugin |
| Roslyn compiler (Microsoft.Net.Compilers.Toolset) | Build tool only, not shipped | MIT | https://github.com/dotnet/roslyn |
| Mono.Cecil | Build tool only (publicizer), not shipped | MIT | https://github.com/jbevain/cecil |
| ILSpy (ilspycmd) | Optional development tool, not shipped | MIT | https://github.com/icsharpcode/ILSpy |

Ravenfield is © SteelRaven7. This mod contains no game code or assets; it patches the game at runtime.
