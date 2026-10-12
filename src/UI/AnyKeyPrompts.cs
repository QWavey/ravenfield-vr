using HarmonyLib;
using RavenfieldVR.Core;
using RavenfieldVR.Input;

namespace RavenfieldVR.UI
{
    /// <summary>
    /// The intro splash and the title screen wait for Input.anyKeyDown, which controller buttons never trigger.
    /// In VR any face button, trigger or grip press continues them too.
    /// </summary>
    internal static class AnyKeyPrompts
    {
        public static bool ControllerPressed()
        {
            if (!VRManager.Active)
            {
                return false;
            }
            return VRInput.GetPressed(DigitalAction.Fire) || VRInput.GetPressed(DigitalAction.Use) || VRInput.GetPressed(DigitalAction.Grab)
                   || VRInput.GetPressed(DigitalAction.MagRelease) || VRInput.GetPressed(DigitalAction.SlideRelease)
                   || VRInput.GetPressed(DigitalAction.Squad) || VRInput.GetPressed(DigitalAction.Map) || VRInput.GetPressed(DigitalAction.Pause);
        }
    }

    [HarmonyPatch(typeof(MainMenu), "Update")]
    internal static class MainMenuAnyKeyPatch
    {
        private static void Postfix(MainMenu __instance)
        {
            // Page 0 is the title screen ("press any key").
            if (__instance.page == 0 && ModManager.instance != null && ModManager.instance.contentHasFinishedLoading && AnyKeyPrompts.ControllerPressed())
            {
                __instance.OpenPageIndex(1);
            }
        }
    }

    [HarmonyPatch(typeof(GotoMenu), "LateUpdate")]
    internal static class GotoMenuAnyKeyPatch
    {
        private static void Postfix(GotoMenu __instance)
        {
            if (AnyKeyPrompts.ControllerPressed() && __instance.CanSkip())
            {
                __instance.GotoNextScene();
            }
        }
    }
}
