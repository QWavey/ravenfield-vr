using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RavenfieldVR.Core;
using UnityEngine;

namespace RavenfieldVR.Rig
{
    /// <summary>
    /// The first-person controller's ground check (a sphere cast down from the hips) took any body with a rigidbody under
    /// the feet for a moving platform: the player was carried along with it and turned with its yaw, and its sloped side
    /// made him slide. A magazine dropped at your feet, a spent case, a weapon put down or the gun in your hand did that
    /// in VR ("the magazine moves me and turns my view"). Light loose bodies (dynamic, under 30 kg: every prop of the mod,
    /// a ragdoll's limbs) are not ground here; vehicles and the level are.
    /// </summary>
    [HarmonyPatch(typeof(UnityStandardAssets.Characters.FirstPerson.FirstPersonController), "FixedUpdate")]
    internal static class GroundCheckIgnoresPropsPatch
    {
        private const float LightBody = 30f;
        // Unsorted: over a pile of loose bodies (cases, limbs) a small buffer could fill before the ground was in it.
        private static readonly RaycastHit[] Hits = new RaycastHit[64];

        private static readonly MethodInfo SphereCast = AccessTools.Method(typeof(Physics), nameof(Physics.SphereCast),
            new[] { typeof(Ray), typeof(float), typeof(RaycastHit).MakeByRefType(), typeof(float), typeof(int) });

        private static readonly MethodInfo GroundCast = AccessTools.Method(typeof(GroundCheckIgnoresPropsPatch), nameof(Cast));

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            int replaced = 0;
            foreach (CodeInstruction ci in instructions)
            {
                if (SphereCast != null && ci.Calls(SphereCast))
                {
                    replaced++;
                    yield return new CodeInstruction(OpCodes.Call, GroundCast) { labels = ci.labels, blocks = ci.blocks };
                    continue;
                }
                yield return ci;
            }
            if (replaced != 1)
            {
                Log.Warn($"Ground check: expected one sphere cast in FirstPersonController.FixedUpdate, replaced {replaced}.");
            }
        }

        /// <summary>The same cast, the nearest hit that is not a light loose body.</summary>
        internal static bool Cast(Ray ray, float radius, out RaycastHit hit, float maxDistance, int mask)
        {
            if (!VRManager.Active)
            {
                return Physics.SphereCast(ray, radius, out hit, maxDistance, mask);
            }
            int n = Physics.SphereCastNonAlloc(ray, radius, Hits, maxDistance, mask);
            hit = default;
            float best = float.MaxValue;
            bool any = false;
            for (int i = 0; i < n; i++)
            {
                RaycastHit h = Hits[i];
                if (h.collider == null || h.distance <= 0f && h.point == Vector3.zero)
                {
                    continue; // overlapping at the start: a single sphere cast does not report those either
                }
                Rigidbody body = h.collider.attachedRigidbody;
                if (body != null && !body.isKinematic && body.mass < LightBody)
                {
                    continue;
                }
                if (h.distance < best)
                {
                    best = h.distance;
                    hit = h;
                    any = true;
                }
            }
            return any;
        }
    }
}
