using System.Collections.Generic;
using RavenfieldVR.Rig;
using RavenfieldVR.Weapons;
using UnityEngine;
using UnityEngine.Rendering;

namespace RavenfieldVR.Core.Showcase
{
    /// <summary>
    /// The showcase's cameras (docs/Showcase-Plan.md): where the recorder's camera stands each frame and what it shows. First person is
    /// the headset's own view (mono, with the effects on the lens); the cinematic ones (a camera on the gun looking back at the face,
    /// over the shoulder, an orbit, a fixed spot, a close-up on a part) show the player's body, which the eyes never draw. A shake that
    /// rises on each shot and blast and dies away (trauma squared, as trailers' camera work does it) rides on every one.
    /// </summary>
    internal static class ShowcaseCamera
    {
        internal struct Shot
        {
            public bool Valid;
            public string Name;
            public Vector3 Position;
            public Quaternion Rotation;
            /// <summary>Vertical field of view (degrees) of the 16:9 picture.</summary>
            public float Fov;
            /// <summary>The player's body drawn (cinematic cameras): the eyes hide it.</summary>
            public bool ShowBody;
            /// <summary>The effects on the lens drawn (mud, water, the low-health edge): first person only.</summary>
            public bool Lens;

            internal static Shot None => default;
        }

        private const int UiLayer = 5;
        private static readonly HashSet<Camera> Ignored = new HashSet<Camera>();

        /// <summary>Draws <paramref name="shot"/> into <paramref name="rt"/> with the headset camera's settings.</summary>
        internal static void Render(Camera cam, Shot shot, RenderTexture rt)
        {
            VRRig rig = VRRig.Instance;
            Camera source = rig != null ? rig.Camera : Camera.main;
            if (source == null)
            {
                return;
            }
            if (rig != null && Ignored.Add(cam))
            {
                rig.Suppressor.Ignore(cam); // (the suppressor empties the game's cameras' masks: not this one)
            }
            cam.CopyFrom(source); // (its transform and its target too: both set below)
            cam.stereoTargetEye = StereoTargetEyeMask.None;
            Camera ui = UI.UISystem.Instance != null ? UI.UISystem.Instance.UiCamera : null;
            cam.cullingMask = shot.Lens ? source.cullingMask | (ui != null ? ui.cullingMask : 1 << UiLayer) : source.cullingMask & ~(1 << UiLayer);
            cam.fieldOfView = shot.Fov;
            cam.aspect = rt.width / (float)rt.height;
            cam.nearClipPlane = 0.02f;
            cam.targetTexture = rt;
            (Vector3 p, Quaternion r) = Shake.Apply(shot.Position, shot.Rotation);
            cam.transform.SetPositionAndRotation(p, r);
            List<Shown> shown = shot.ShowBody ? ShowBody() : null;
            try
            {
                cam.Render();
            }
            finally
            {
                if (shown != null)
                {
                    HideAgain(shown);
                }
                cam.targetTexture = null;
            }
        }

        // ---- the player's body for the cinematic cameras --------------------------------------------------------------------

        private struct Shown
        {
            public Renderer Renderer;
            public bool Off;
            public ShadowCastingMode Mode;
            public Transform Bone;
            public Vector3 Scale;
        }

        private static readonly List<Shown> ShownNow = new List<Shown>();

        /// <summary>
        /// The player's body drawn for this render (the game draws it shadows-only in first person, the mod forces it off), its arms folded
        /// away: the hands and arms on screen are the VR ones, with the gun, and the body's own arms held its third-person pose beside them.
        /// </summary>
        private static List<Shown> ShowBody()
        {
            ShownNow.Clear();
            FpsActorController player = LocalPlayer.controller;
            Actor a = LocalPlayer.actor;
            if (player == null || a == null || player.thirdpersonRenderers == null)
            {
                return ShownNow;
            }
            void Show(Renderer r)
            {
                if (r == null)
                {
                    return;
                }
                ShownNow.Add(new Shown { Renderer = r, Off = r.forceRenderingOff, Mode = r.shadowCastingMode });
                r.forceRenderingOff = false;
                r.shadowCastingMode = ShadowCastingMode.On;
            }
            foreach (Renderer r in player.thirdpersonRenderers)
            {
                Show(r);
            }
            if (a.accessoryRenderer != null)
            {
                foreach (Renderer r in a.accessoryRenderer)
                {
                    Show(r);
                }
            }
            if (a.ragdoll != null)
            {
                foreach (HumanBodyBones b in new[] { HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm })
                {
                    Transform t = a.ragdoll.HumanBoneTransformAnimated(b);
                    if (t != null)
                    {
                        ShownNow.Add(new Shown { Bone = t, Scale = t.localScale });
                        t.localScale = Vector3.one * 0.001f;
                    }
                }
            }
            return ShownNow;
        }

        private static void HideAgain(List<Shown> shown)
        {
            foreach (Shown s in shown)
            {
                if (s.Renderer != null)
                {
                    s.Renderer.forceRenderingOff = s.Off;
                    s.Renderer.shadowCastingMode = s.Mode;
                }
                if (s.Bone != null)
                {
                    s.Bone.localScale = s.Scale;
                }
            }
            shown.Clear();
        }

        // ---- the rigs ---------------------------------------------------------------------------------------------------------

        private static Transform Head => VRRig.Instance != null ? VRRig.Instance.Head : null;

        /// <summary>The headset's own view, mono, 16:9, with the lens effects.</summary>
        internal static Shot FirstPerson(float fov = 62f)
        {
            Transform h = Head;
            return h == null ? Shot.None : new Shot { Valid = true, Name = "first person", Position = h.position, Rotation = h.rotation, Fov = fov, Lens = true };
        }

        /// <summary>
        /// First person as the gun comes up: from the head to the sight's own line by <paramref name="aim"/> (0 at the hip, 1 aimed),
        /// so the sights sit in the middle of the picture and the view narrows a little, as aiming does on a flat screen.
        /// </summary>
        internal static Shot FirstPersonAim(float aim, float fov = 62f, float aimedFov = 50f)
        {
            Transform h = Head;
            HeldWeapon held = WeaponSystem.Instance != null ? WeaponSystem.Instance.Held : null;
            if (h == null)
            {
                return Shot.None;
            }
            if (held == null || aim <= 0.001f)
            {
                return FirstPerson(fov);
            }
            SightLine sight = held.CurrentSight;
            Vector3 eyeGun = sight != null ? sight.EyePoint : held.Rig.Data.EyePoint;
            Vector3 dirGun = sight != null ? sight.EyeDirection : held.Rig.Data.EyeDirection;
            Vector3 dir = held.Rig.DirToWorld(dirGun).normalized;
            Vector3 eye = held.Rig.ToWorld(eyeGun) - dir * 0.02f;
            float k = Mathf.Clamp01(aim);
            k = k * k * (3f - 2f * k);
            return new Shot
            {
                Valid = true, Name = "first person", Position = Vector3.Lerp(h.position, eye, k),
                Rotation = Quaternion.Slerp(h.rotation, Quaternion.LookRotation(dir, h.up), k), Fov = Mathf.Lerp(fov, aimedFov, k), Lens = true
            };
        }

        /// <summary>
        /// On the gun at its muzzle, just above and beside the barrel, looking back along it at the face (the user's "the camera should be
        /// on the gun facing the face"): the gun runs from the bottom of the picture to the hands and the face behind them, and the camera
        /// moves rigidly with the gun, so every step and shot moves the whole picture. (Placed a metre ahead of the gun it only showed the
        /// player from the front, the gun lost at the edge.)
        /// </summary>
        internal static Shot GunCam(float back = 0.06f, float side = 0.075f, float up = 0.085f, float fov = 62f)
        {
            HeldWeapon held = WeaponSystem.Instance != null ? WeaponSystem.Instance.Held : null;
            Transform h = Head;
            if (held == null || held.Rig.GunBody == null || h == null)
            {
                return Shot.None;
            }
            WeaponRig rig = held.Rig;
            Vector3 fwd = rig.DirToWorld(rig.Data.AimAxis).normalized;
            Vector3 upW = rig.DirToWorld(rig.Data.Up).normalized;
            Vector3 sideW = Vector3.Cross(upW, fwd).normalized;
            Vector3 muzzle = rig.ToWorld(rig.Data.MuzzlePoint);
            Vector3 at = muzzle - fwd * back + sideW * side + upW * up;
            Vector3 face = h.position - Vector3.up * 0.05f;
            return new Shot { Valid = true, Name = "gun cam", Position = at, Rotation = Quaternion.LookRotation(face - at, upW), Fov = fov, ShowBody = true };
        }

        /// <summary>Behind and beside the head, looking where the head looks, far ahead.</summary>
        internal static Shot OverShoulder(float back = 1.15f, float side = 0.5f, float up = 0.28f, float fov = 50f)
        {
            Transform h = Head;
            if (h == null)
            {
                return Shot.None;
            }
            Vector3 flat = Vector3.ProjectOnPlane(h.forward, Vector3.up).normalized;
            Vector3 right = Vector3.Cross(Vector3.up, flat);
            Vector3 at = h.position - flat * back + right * side + Vector3.up * up;
            Vector3 target = h.position + h.forward * 25f;
            return new Shot { Valid = true, Name = "over the shoulder", Position = at, Rotation = Quaternion.LookRotation(target - at, Vector3.up), Fov = fov, ShowBody = true };
        }

        /// <summary>Round a point at a height, <paramref name="degrees"/> along the circle (the scene moves it each frame).</summary>
        internal static Shot Orbit(Vector3 centre, float radius, float height, float degrees, float lookUp = 0f, float fov = 50f)
        {
            Vector3 at = centre + Quaternion.Euler(0f, degrees, 0f) * Vector3.forward * radius + Vector3.up * height;
            Vector3 target = centre + Vector3.up * lookUp;
            return new Shot { Valid = true, Name = "orbit", Position = at, Rotation = Quaternion.LookRotation(target - at, Vector3.up), Fov = fov, ShowBody = true };
        }

        /// <summary>A camera standing still, looking at a point.</summary>
        internal static Shot Fixed(Vector3 at, Vector3 target, float fov = 50f, string name = "fixed")
        {
            return new Shot { Valid = true, Name = name, Position = at, Rotation = Quaternion.LookRotation(target - at, Vector3.up), Fov = fov, ShowBody = true };
        }

        /// <summary>Close on a point of the gun (in its own space), from an offset in the gun's own frame.</summary>
        internal static Shot CloseUp(Vector3 gunPoint, Vector3 offset, float fov = 40f)
        {
            HeldWeapon held = WeaponSystem.Instance != null ? WeaponSystem.Instance.Held : null;
            if (held == null || held.Rig.GunBody == null)
            {
                return Shot.None;
            }
            WeaponRig rig = held.Rig;
            Vector3 target = rig.ToWorld(gunPoint);
            Vector3 fwd = rig.DirToWorld(rig.Data.AimAxis).normalized;
            Vector3 upW = rig.DirToWorld(rig.Data.Up).normalized;
            Vector3 sideW = Vector3.Cross(upW, fwd).normalized;
            Vector3 at = target + fwd * offset.z + sideW * offset.x + upW * offset.y;
            return new Shot { Valid = true, Name = "close-up", Position = at, Rotation = Quaternion.LookRotation(target - at, upW), Fov = fov, ShowBody = true };
        }

        /// <summary>
        /// The camera's shake: "trauma" added by shots and blasts, falling off over about half a second; the shake is its square (small
        /// kicks barely move it, big ones throw it), smooth noise in each axis, plus a quick kick back along the view on each shot.
        /// </summary>
        internal static class Shake
        {
            private static float _trauma, _kick;
            private static int _frame = -1;

            /// <summary>A shot (0.2-0.4), a heavy gun (0.5), a blast near by (0.8-1).</summary>
            internal static void Add(float trauma, float kick = 0f)
            {
                _trauma = Mathf.Clamp01(_trauma + trauma);
                _kick = Mathf.Max(_kick, kick);
            }

            internal static void Reset()
            {
                _trauma = _kick = 0f;
            }

            internal static (Vector3, Quaternion) Apply(Vector3 p, Quaternion r)
            {
                if (_frame != Time.frameCount)
                {
                    _frame = Time.frameCount;
                    float dt = Mathf.Max(Time.unscaledDeltaTime, 1f / 120f);
                    _trauma = Mathf.Max(0f, _trauma - 1.8f * dt);
                    _kick = Mathf.Max(0f, _kick - 12f * dt);
                }
                float s = _trauma * _trauma;
                if (s <= 1e-5f && _kick <= 1e-5f)
                {
                    return (p, r);
                }
                float t = Time.unscaledTime * 22f;
                float pitch = (Mathf.PerlinNoise(t, 0.1f) * 2f - 1f) * 6f * s;
                float yaw = (Mathf.PerlinNoise(0.3f, t) * 2f - 1f) * 6f * s;
                float roll = (Mathf.PerlinNoise(t, 7.7f) * 2f - 1f) * 4f * s;
                Quaternion q = r * Quaternion.Euler(pitch - _kick * 2.5f, yaw, roll);
                Vector3 jitter = r * new Vector3((Mathf.PerlinNoise(t, 3.3f) * 2f - 1f) * 0.015f * s, (Mathf.PerlinNoise(5.5f, t) * 2f - 1f) * 0.015f * s, -_kick * 0.025f);
                return (p + jitter, q);
            }
        }
    }
}
