using HarmonyLib;
using RavenfieldVR.Core;
using RavenfieldVR.Input;
using UnityEngine;

namespace RavenfieldVR.UI
{
    /// <summary>
    /// What the game's menus read from the desktop mouse, answered from the laser. On a flat screen a canvas's world corners are pixels, so the game
    /// compares them with <c>Input.mousePosition</c> and a pointer event's position. Moved into the world they are metres, and those sums came out
    /// as nonsense with nothing said: a click on the squad map went to a point nobody chose, the tactics arrow followed the desktop cursor, a mod tile
    /// never showed its text. The right button had no answer at all: see <see cref="VRInputModule"/> for B on a menu (2026-11-07, Tasks 366).
    /// </summary>
    internal static class MenuMouse
    {
        /// <summary>Where the laser meets this panel, as a share of its width and height (0-1 inside it, past that outside).</summary>
        internal static bool OnPanel(RectTransform panel, out Vector2 share)
        {
            share = default;
            VRInputModule laser = VRInputModule.Live;
            if (panel == null || laser == null || !laser.HasHit)
            {
                return false;
            }
            Rect r = panel.rect;
            if (r.width <= 0f || r.height <= 0f)
            {
                return false;
            }
            Vector3 local = panel.InverseTransformPoint(laser.HitPoint);
            share = new Vector2((local.x - r.xMin) / r.width, (local.y - r.yMin) / r.height);
            return true;
        }
    }

    /// <summary>The squad map: a click's place on the panel, from the laser. It ran a ray down from the map camera at a place made of pixels and metres.</summary>
    [HarmonyPatch(typeof(StrategyUi), nameof(StrategyUi.LocalPositionWorldPixel))]
    internal static class StrategyMapPointPatch
    {
        private static bool Prefix(ref Vector2 __result)
        {
            if (!VRManager.Active || StrategyUi.instance == null || !MenuMouse.OnPanel(StrategyUi.instance.strategyPanel, out Vector2 share))
            {
                return true;
            }
            __result = share;
            return false;
        }
    }

    /// <summary>The tactics map's arrow while it is dragged over empty map: to the laser, not the desktop cursor.</summary>
    [HarmonyPatch(typeof(BattlePlanUi), "Update")]
    internal static class TacticsArrowPatch
    {
        private static void Postfix(BattlePlanUi __instance)
        {
            if (VRManager.Active && __instance.isDragging && __instance.dragDestination == null && __instance.dragSource != null
                && MenuMouse.OnPanel(__instance.tacticsPanel, out Vector2 share))
            {
                __instance.SetArrowTransform(__instance.attackArrowPreviewTransform, ((RectTransform)__instance.dragSource.transform).anchorMin, share);
            }
        }
    }

    /// <summary>A tactics order is taken off with the right button held and swept over its arrow: B held, in the headset. No other way removed one.</summary>
    [HarmonyPatch(typeof(TacticsOrderIndicator), "Update")]
    internal static class TacticsOrderRemovePatch
    {
        private static void Postfix(TacticsOrderIndicator __instance)
        {
            if (VRManager.Active && __instance.graphic != null)
            {
                __instance.graphic.raycastTarget = VRInput.GetHeld(DigitalAction.MagRelease);
            }
        }
    }

    /// <summary>A mod tile on the main menu shows its text with the laser on it (the game's test compares the desktop cursor with metres). The game's Update, by the laser.</summary>
    [HarmonyPatch(typeof(FeaturedModPanel), "Update")]
    internal static class FeaturedModHoverPatch
    {
        private static bool Prefix(FeaturedModPanel __instance)
        {
            if (!VRManager.Active)
            {
                return true;
            }
            bool over = MenuMouse.OnPanel((RectTransform)__instance.transform.parent, out Vector2 share) && share.x > 0f && share.x < 1f && share.y > 0f && share.y < 1f;
            if (over && !__instance.lastMouseOver)
            {
                __instance.ShowDescription();
            }
            else if (!over && __instance.lastMouseOver)
            {
                __instance.HideDescription();
            }
            __instance.lastMouseOver = over;
            __instance.scale = Mathf.MoveTowards(__instance.scale, __instance.targetScale, 1.6f * Time.deltaTime);
            __instance.image.rectTransform.localScale = new Vector3(__instance.scale, __instance.scale, __instance.scale);
            return false;
        }
    }

    /// <summary>The picker's options open where the laser is (the game places them from the mouse with no camera, which means nothing on a canvas in the world).</summary>
    [HarmonyPatch(typeof(PickerEntryOptions), nameof(PickerEntryOptions.Show))]
    internal static class PickerOptionsPlacePatch
    {
        private static void Postfix(PickerEntryOptions __instance)
        {
            VRInputModule laser = VRInputModule.Live;
            if (!VRManager.Active || laser == null || !laser.HasHit)
            {
                return;
            }
            var rect = (RectTransform)__instance.transform;
            bool rightHalf = rect.parent is RectTransform parent && MenuMouse.OnPanel(parent, out Vector2 share) && share.x > 0.5f;
            rect.pivot = new Vector2(rightHalf ? 1f : 0f, 0f);
            Vector3 at = rect.position;
            at.x = laser.HitPoint.x;
            at.y = laser.HitPoint.y;
            at.z = laser.HitPoint.z;
            rect.position = at;
        }
    }

    /// <summary>The team config's "load part of it" list at the row the laser picked, as the game places it at the mouse's height.</summary>
    [HarmonyPatch(typeof(TeamConfig), nameof(TeamConfig.OnLoadTeam))]
    internal static class TeamConfigPartPlacePatch
    {
        private static void Postfix(TeamConfig __instance, UnityEngine.EventSystems.PointerEventData eventData)
        {
            VRInputModule laser = VRInputModule.Live;
            RectTransform list = __instance.loadPartOfTeamConfigDD;
            if (!VRManager.Active || laser == null || !laser.HasHit || eventData == null || list == null || !list.gameObject.activeSelf || !(list.parent is RectTransform parent))
            {
                return;
            }
            list.anchoredPosition = new Vector2(list.anchoredPosition.x, parent.InverseTransformPoint(laser.HitPoint).y);
        }
    }

    /// <summary>A dialog line that waits for the player (the game reads Space, Return or the mouse): any button goes on, as on the title screen.</summary>
    [HarmonyPatch(typeof(DialogPlayerBase), nameof(DialogPlayerBase.GetNextLineInput))]
    internal static class DialogNextLinePatch
    {
        private static void Postfix(ref bool __result)
        {
            __result |= AnyKeyPrompts.ControllerPressed();
        }
    }
}
