using System.Collections.Generic;
using HarmonyLib;
using RavenfieldVR.Rig;
using RavenfieldVR.Weapons;
using UnityEngine;
using UnityEngine.Rendering;

namespace RavenfieldVR.Core.Showcase
{
    /// <summary>
    /// The showcase's cameras (docs/Showcase-Plan.md): where the recorder's camera stands each frame and what it shows. First person is
    /// the headset's own view (mono, with the effects on the lens); the cinematic ones (a camera on the gun looking back at the face,
    /// over the shoulder, on the helmet, a fixed spot, a close-up on a part) show the player's body, which the eyes never draw. A shake that
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
            /// <summary>What a third-person camera films (the head, the gun): the camera is kept on its side of walls and ground.</summary>
            public Vector3? Subject;
            /// <summary>The holstered weapons' copies hidden for this render (the helmet cam: a slung rifle right in front of it).</summary>
            public bool HideHolstered;
            /// <summary>
            /// From the eyes: the body drawn too, its head and helmet folded away before the frame's drawing (<see cref="ShowcaseBody"/>),
            /// so the arms come from the shoulders to the gloves. The weapons' own first-person arms, stretched there by the IK, broke
            /// into spiky fragments in the air (the user: "the hands/arms ... they are not in the view").
            /// </summary>
            public bool FromEyes;

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
            MirrorPostProcess(cam, rig, shot.FromEyes); // (the goggles' green is the wearer's view: from the eyes only)
            cam.stereoTargetEye = StereoTargetEyeMask.None;
            Camera ui = UI.UISystem.Instance != null ? UI.UISystem.Instance.UiCamera : null;
            cam.cullingMask = shot.Lens ? source.cullingMask | (ui != null ? ui.cullingMask : 1 << UiLayer) : source.cullingMask & ~(1 << UiLayer);
            cam.fieldOfView = shot.Fov;
            cam.aspect = rt.width / (float)rt.height;
            cam.nearClipPlane = 0.02f;
            cam.targetTexture = rt;
            (Vector3 p, Quaternion r) = Shake.Apply(KeepOut(shot.Position, shot.Subject), shot.Rotation);
            cam.transform.SetPositionAndRotation(p, r);
            List<Shown> shown = shot.ShowBody ? ShowBody(!shot.FromEyes) : null;
            Weapons.HolsterDisplay holsters = shot.HideHolstered && Weapons.WeaponSystem.Instance != null ? Weapons.WeaponSystem.Instance.Holsters : null;
            holsters?.HideForRender(true);
            // Seen from outside, the soldier's own arms and hands hold the gun (ShowcaseBody leads them to the weapon's hands): the
            // weapon's first-person arms and the gloves are not drawn then (the user, 2026-11-09, on the arms: "this happens in every
            // clip which is very bad"). From the eyes they are drawn as in play, and the body is not.
            List<Renderer> hidden = shot.ShowBody && !shot.FromEyes ? HideHands() : null;
            try
            {
                cam.Render();
            }
            finally
            {
                if (hidden != null)
                {
                    foreach (Renderer back in hidden)
                    {
                        if (back != null)
                        {
                            back.enabled = true;
                        }
                    }
                }
                holsters?.HideForRender(false);
                if (shown != null)
                {
                    HideAgain(shown);
                }
                cam.targetTexture = null;
            }
        }

        /// <summary>The weapon's first-person arms and the gloves switched off for one picture; the ones that were on, to switch on again.</summary>
        private static List<Renderer> HideHands()
        {
            var off = new List<Renderer>();
            void Hide(Renderer r)
            {
                if (r != null && r.enabled)
                {
                    r.enabled = false;
                    off.Add(r);
                }
            }
            Weapons.HeldWeapon held = Weapons.WeaponSystem.Instance != null ? Weapons.WeaponSystem.Instance.Held : null;
            if (held != null && held.Rig != null && held.Rig.ArmRenderers != null)
            {
                foreach (Renderer r in held.Rig.ArmRenderers)
                {
                    Hide(r);
                }
            }
            VRRig rig = VRRig.Instance;
            if (rig != null)
            {
                foreach (VRHand hand in rig.Hands)
                {
                    if (hand != null && hand.Model != null && hand.Model.Root != null)
                    {
                        foreach (Renderer r in hand.Model.Root.GetComponentsInChildren<Renderer>())
                        {
                            Hide(r);
                        }
                    }
                }
            }
            return off;
        }

        private static UnityEngine.Rendering.PostProcessing.PostProcessLayer _pp;

        /// <summary>
        /// The eyes' post-processing on the showcase camera too, as the eyes have it this frame (the goggles' green, the game's grade with
        /// PostProcessing on): a camera copied from the eyes takes their settings, not their components, and filmed no night vision.
        /// </summary>
        private static void MirrorPostProcess(Camera cam, VRRig rig, bool fromEyes)
        {
            UnityEngine.Rendering.PostProcessing.PostProcessLayer eye = rig != null ? rig.EyePostProcess : null;
            bool on = fromEyes && eye != null && eye.enabled;
            if (on && _pp == null)
            {
                object resources = PostProcessingManager.instance != null && PostProcessingManager.instance.ppResources != null
                    ? PostProcessingManager.instance.ppResources : VRRig.PostProcessResources(eye);
                if (resources == null)
                {
                    return;
                }
                GameObject go = cam.gameObject;
                go.SetActive(false);
                _pp = go.AddComponent<UnityEngine.Rendering.PostProcessing.PostProcessLayer>();
                AccessTools.Field(typeof(UnityEngine.Rendering.PostProcessing.PostProcessLayer), "m_Resources")?.SetValue(_pp, resources);
                go.SetActive(true);
            }
            if (_pp == null)
            {
                return;
            }
            if (on)
            {
                _pp.volumeLayer = eye.volumeLayer;
                _pp.volumeTrigger = cam.transform;
                _pp.antialiasingMode = eye.antialiasingMode;
                _pp.stopNaNPropagation = true;
                cam.allowHDR = true;
            }
            _pp.enabled = on;
        }

        // ---- the player's body for the cinematic cameras --------------------------------------------------------------------

        private struct Shown
        {
            public Renderer Renderer;
            public bool Off;
            public ShadowCastingMode Mode;
        }

        private static readonly List<Shown> ShownNow = new List<Shown>();

        /// <summary>
        /// The player's body drawn for this render (the game draws it shadows-only in first person, the mod forces it off), posed to the
        /// headset and the gloves by <see cref="ShowcaseBody"/> before the frame's drawing. (Its arms scaled away here, at the frame's end, never
        /// showed: the body is skinned for the frame by then.)
        /// </summary>
        private static List<Shown> ShowBody(bool accessories = true)
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
            if (accessories && a.accessoryRenderer != null)
            {
                foreach (Renderer r in a.accessoryRenderer)
                {
                    Show(r);
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
            }
            shown.Clear();
        }

        // ---- the rigs ---------------------------------------------------------------------------------------------------------

        private static Transform Head => VRRig.Instance != null ? VRRig.Instance.Head : null;

        /// <summary>
        /// A third-person camera kept on its subject's side of the level: a sphere swept from the subject to the camera stops short of
        /// whatever is between (take 5's orbit, since removed, filmed from inside a helicopter and under the ground, 2026-11-08).
        /// </summary>
        private static Vector3 KeepOut(Vector3 at, Vector3? subject)
        {
            if (subject == null)
            {
                return at;
            }
            Vector3 from = subject.Value, to = at - from;
            float dist = to.magnitude;
            int level = ~Hitbox.ALL_HITBOX_LAYER_MASK & ~(1 << 2);
            if (dist > 0.05f && Physics.SphereCast(from, 0.12f, to / dist, out RaycastHit hit, dist, level, QueryTriggerInteraction.Ignore))
            {
                return from + to / dist * Mathf.Max(0.15f, hit.distance - 0.05f);
            }
            return at;
        }

        /// <summary>
        /// The headset's own view, mono, 16:9. <paramref name="lens"/>: the effects on the lens drawn (mud, water drops); they share the UI
        /// layer with the game's labels over capture points ("LANDING" floated over the flag), so only for shots that show them.
        /// </summary>
        internal static Shot FirstPerson(float fov = 62f, bool lens = false)
        {
            Transform h = Head;
            return h == null ? Shot.None : new Shot { Valid = true, Name = "first person", Position = h.position, Rotation = h.rotation, Fov = fov, Lens = lens, ShowBody = false, FromEyes = true };
        }

        /// <summary>
        /// One camera turning into another over <paramref name="k"/> (0: <paramref name="a"/>, 1: <paramref name="b"/>): position,
        /// turn and view eased together, so a shot that starts looking at the hands rises with the gun into its sights in one move
        /// (switched at once it jumped within the shot).
        /// </summary>
        internal static Shot Blend(Shot a, Shot b, float k)
        {
            if (!a.Valid || !b.Valid)
            {
                return a.Valid ? a : b;
            }
            k = Mathf.Clamp01(k);
            return new Shot
            {
                // (One name for the whole blend: switched half way, the edit's mark runs split there and a cut took 5 frames of it.)
                Valid = true, Name = a.Name + " into " + b.Name, Position = Vector3.Lerp(a.Position, b.Position, k), Rotation = Quaternion.Slerp(a.Rotation, b.Rotation, k),
                Fov = Mathf.Lerp(a.Fov, b.Fov, k), ShowBody = k < 0.5f ? a.ShowBody : b.ShowBody, Lens = k < 0.5f ? a.Lens : b.Lens, Subject = k < 0.5f ? a.Subject : b.Subject,
                FromEyes = k < 0.5f ? a.FromEyes : b.FromEyes, HideHolstered = k < 0.5f ? a.HideHolstered : b.HideHolstered
            };
        }

        /// <summary>
        /// First person looking at the hands: from the eyes, turned <paramref name="look"/> of the way (0-1) from where the head looks
        /// to <paramref name="target"/> (a magazine well, the gun's middle), the horizon kept level. The headset's view would show the
        /// same when the player looks down at their hands; the null headset's head stands still, so the camera turns instead.
        /// </summary>
        internal static Shot FirstPersonLook(Vector3 target, float look, float fov = 64f)
        {
            Transform h = Head;
            if (h == null)
            {
                return Shot.None;
            }
            Vector3 to = target - h.position;
            Quaternion at = to.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(to, Vector3.up) : h.rotation;
            float k = Mathf.Clamp01(look);
            k = k * k * (3f - 2f * k);
            return new Shot { Valid = true, Name = "first person look", Position = h.position, Rotation = Quaternion.Slerp(h.rotation, at, k), Fov = fov, ShowBody = false, FromEyes = true };
        }

        /// <summary>
        /// First person as the gun comes up: from the head to the sight's own line by <paramref name="aim"/> (0 at the hip, 1 aimed),
        /// so the sights sit in the middle of the picture and the view narrows a little, as aiming does on a flat screen.
        /// </summary>
        internal static Shot FirstPersonAim(float aim, float fov = 62f, float aimedFov = 50f, bool lens = false)
        {
            Transform h = Head;
            HeldWeapon held = WeaponSystem.Instance != null ? WeaponSystem.Instance.Held : null;
            if (h == null)
            {
                return Shot.None;
            }
            if (held == null || aim <= 0.001f)
            {
                return FirstPerson(fov, lens);
            }
            SightLine sight = held.CurrentSight;
            Vector3 eyeGun = sight != null ? sight.EyePoint : held.Rig.Data.EyePoint;
            Vector3 dirGun = sight != null ? sight.EyeDirection : held.Rig.Data.EyeDirection;
            Vector3 dir = held.Rig.DirToWorld(dirGun).normalized;
            Vector3 eye = held.Rig.ToWorld(eyeGun) - dir * 0.05f;
            float k = Mathf.Clamp01(aim);
            k = k * k * (3f - 2f * k);
            return new Shot
            {
                Valid = true, Name = "first person", ShowBody = false, FromEyes = true, Position = Vector3.Lerp(h.position, eye, k),
                Rotation = Quaternion.Slerp(h.rotation, Quaternion.LookRotation(dir, h.up), k), Fov = Mathf.Lerp(fov, aimedFov, k), Lens = lens
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
            // The horizon mostly level (a fifth of the gun's roll: alive, not seasick).
            Vector3 camUp = Vector3.Slerp(Vector3.up, upW, 0.2f);
            return new Shot { Valid = true, Name = "gun cam", Position = at, Rotation = Quaternion.LookRotation(face - at, camUp), Fov = fov, ShowBody = true, Subject = face };
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
            return new Shot { Valid = true, Name = "over the shoulder", Position = at, Rotation = Quaternion.LookRotation(target - at, Vector3.up), Fov = fov, ShowBody = true, Subject = h.position };
        }

        /// <summary>
        /// A camera on the helmet: just behind it, a little right and above, rigid with the head and looking where it looks (converging 25
        /// m ahead), the helmet big in the lower left and the gun ahead of it. Found by chance: an over-the-shoulder view pushed in by a
        /// rock behind the player (take 7, frame 251), and the user wanted it kept ("we can take that as a additional helmet camera").
        /// Higher, further back and over the other shoulder since the body's own arms hold the gun (2026-11-09): the gun arm, raised to
        /// aim, filled half of the view from behind the gun shoulder.
        /// </summary>
        internal static Shot HelmetCam(float back = 0.42f, float side = -0.13f, float up = 0.2f, float fov = 50f)
        {
            Transform h = Head;
            if (h == null)
            {
                return Shot.None;
            }
            Vector3 at = h.position + h.rotation * new Vector3(side, up, -back);
            Vector3 target = h.position + h.forward * 25f;
            return new Shot { Valid = true, Name = "helmet cam", Position = at, Rotation = Quaternion.LookRotation(target - at, h.up), Fov = fov, ShowBody = true, Subject = h.position, HideHolstered = true };
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
                    float dt = Mathf.Max(Time.deltaTime, 1f / 120f); // (game time: steps of 1/60 s in the picture pass)
                    _trauma = Mathf.Max(0f, _trauma - 1.8f * dt);
                    _kick = Mathf.Max(0f, _kick - 12f * dt);
                }
                float s = _trauma * _trauma;
                if (s <= 1e-5f && _kick <= 1e-5f)
                {
                    return (p, r);
                }
                float t = Time.time * 22f;
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
