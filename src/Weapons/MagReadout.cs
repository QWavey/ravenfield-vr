using System.Collections.Generic;
using RavenfieldVR.Core;
using RavenfieldVR.Rig;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// Look at the top of a magazine to see how full it is, the way you would glance at the follower: a small round count
    /// appears just above the feed lips of the magazine you look at, if it is in a hand or within arm's reach and its top
    /// faces you. It fades in and out, faces your eyes, and never shows while you aim down the sights.
    /// </summary>
    internal sealed class MagReadout
    {
        private const float Reach = 0.8f;
        private const float LookCone = 14f;

        private static Font _font;
        private readonly WeaponSystem _sys;
        private GameObject _go;
        private TextMesh _text;
        private MagazineProp _target;
        private float _alpha;
        private string _shown;
        private static readonly Dictionary<int, string> Labels = new Dictionary<int, string>();

        /// <summary>Autotest pictures: shows this magazine's readout whatever the head looks at.</summary>
        internal static MagazineProp TestTarget;

        public MagReadout(WeaponSystem sys)
        {
            _sys = sys;
        }

        private void Build()
        {
            if (_font == null)
            {
                _font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            }
            _go = new GameObject("RavenfieldVR Magazine Readout") { layer = 18 };
            Object.DontDestroyOnLoad(_go);
            _text = _go.AddComponent<TextMesh>();
            _text.font = _font;
            _text.fontSize = 64;
            _text.characterSize = 0.0011f;
            _text.anchor = TextAnchor.LowerCenter;
            _text.alignment = TextAlignment.Center;
            var mr = _go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = _font.material;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }

        public void BeforeRender()
        {
            Transform head = _sys.Rig.Head;
            HeldWeapon held = _sys.Held;
            bool allowed = VRConfig.MagazineReadout.Value && Util.GameContext.PlayerOnFoot && !Util.GameContext.GameMenuOpen && (held == null || !held.Aiming);
            MagazineProp best = TestTarget != null && TestTarget.Go != null ? TestTarget : allowed ? Find(head) : null;
            if (best != null)
            {
                _target = best;
            }
            _alpha = Mathf.MoveTowards(_alpha, best != null ? 1f : 0f, Time.unscaledDeltaTime * (best != null ? 6f : 4f));
            if (_alpha <= 0.001f || _target == null || _target.Go == null)
            {
                if (_go != null && _go.activeSelf)
                {
                    _go.SetActive(false);
                }
                if (_alpha <= 0.001f)
                {
                    _target = null;
                }
                return;
            }
            if (_go == null)
            {
                Build();
            }
            if (!_go.activeSelf)
            {
                _go.SetActive(true);
            }
            Vector3 tip = _target.TipWorld;
            Vector3 up = _target.InsertDirWorld;
            Vector3 at = tip + up * 0.012f;
            Vector3 toEye = head.position - at;
            _go.transform.SetPositionAndRotation(at, Quaternion.LookRotation(-toEye.normalized, head.up));
            string label = Label(_target.Rounds);
            if (!ReferenceEquals(label, _shown))
            {
                _text.text = label;
                _shown = label;
            }
            Color c = _target.Rounds > 0 ? new Color(0.88f, 0.9f, 0.85f) : new Color(0.92f, 0.32f, 0.26f);
            c.a = _alpha * 0.95f;
            _text.color = c;
        }

        /// <summary>The magazine you are looking at: near, in the middle of the view, its top turned toward you.</summary>
        private static MagazineProp Find(Transform head)
        {
            MagazineProp best = null;
            float bestAngle = LookCone;
            IReadOnlyList<MagazineProp> all = MagazineProp.All;
            for (int i = 0; i < all.Count; i++)
            {
                MagazineProp p = all[i];
                if (p.Go == null || !p.Go.activeInHierarchy || p.Kind == PropKind.Rocket || p.OnRail)
                {
                    continue;
                }
                Vector3 tip = p.TipWorld;
                Vector3 toTip = tip - head.position;
                float dist = toTip.magnitude;
                if (dist > Reach || dist < 0.05f)
                {
                    continue;
                }
                // The top (feed lips) has to face the eye, as when you tilt a magazine to look at its rounds.
                if (Vector3.Dot(p.InsertDirWorld, -toTip / dist) < 0.25f)
                {
                    continue;
                }
                float angle = Vector3.Angle(head.forward, toTip);
                if (angle < bestAngle)
                {
                    bestAngle = angle;
                    best = p;
                }
            }
            return best;
        }

        private static string Label(int rounds)
        {
            if (!Labels.TryGetValue(rounds, out string s))
            {
                s = rounds > 0 ? rounds.ToString() : "EMPTY";
                Labels[rounds] = s;
            }
            return s;
        }

        public void Destroy()
        {
            if (_go != null)
            {
                Object.Destroy(_go);
            }
        }
    }
}
