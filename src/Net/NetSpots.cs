using System.Collections.Generic;
using RavenfieldVR.Core;
using UnityEngine;

namespace RavenfieldVR.Net
{
    /// <summary>
    /// Spots shared between the players (invention 2 of 2026-11-14: the squad's pointing hand, already a ray in the world, made into a
    /// mark the friend sees): an order given by pointing, or a point made with no squad to tell, shows on the other PCs as the
    /// player's name above the spot and how far it is, for <see cref="ShowFor"/> seconds; one mark per player, a new one moves it.
    /// The mark is a small message (kind <see cref="NetGame.Spot"/>: the point and whether an enemy was pointed at).
    /// </summary>
    internal static class NetSpots
    {
        private const float ShowFor = 10f, TellEvery = 0.4f;

        private sealed class Mark
        {
            public GameObject Go;
            public TextMesh Text;
            public UI.TextPlate Plate;
            public Vector3 At;
            public float Until;
            public bool Enemy;
            public string Name = "";
            public int ShownMetres = -1;
        }

        private static readonly Dictionary<byte, Mark> Marks = new Dictionary<byte, Mark>();
        private static readonly NetWriter Out = new NetWriter(32);
        private static float _toldAt = -10f;
        private static Font _font;

        /// <summary>This player pointed somewhere (an order, or a spot with no squad): the others are shown it.</summary>
        internal static void Tell(Vector3 at, bool enemy)
        {
            NetSession session = NetGame.SessionNow;
            if (session == null || session.State != NetState.Connected || session.PlayerCount < 2 || !NetGame.InMatchNow
                || Time.unscaledTime - _toldAt < TellEvery || !Finite(at))
            {
                return;
            }
            _toldAt = Time.unscaledTime;
            Out.Reset();
            Out.Vector(at);
            Out.Bool(enemy);
            session.Send(NetGame.Spot, Out, true);
        }

        internal static void Take(byte sender, ref NetReader r)
        {
            Vector3 at = r.Vector();
            bool enemy = r.Bool();
            NetSession session = NetGame.SessionNow;
            if (session == null || !NetGame.InMatchNow || !Finite(at) || at.sqrMagnitude > 1e10f)
            {
                return;
            }
            NetPlayer who = session.Player(sender);
            string name = who != null && who.Info != null && !string.IsNullOrEmpty(who.Info.Name) ? who.Info.Name : "Player " + sender;
            if (!Marks.TryGetValue(sender, out Mark m) || m.Go == null)
            {
                m = Make();
                Marks[sender] = m;
            }
            m.At = at;
            m.Until = Time.unscaledTime + ShowFor;
            m.Enemy = enemy;
            m.Name = name.Length > 24 ? name.Substring(0, 24) : name;
            m.ShownMetres = -1; // (its text again: another spot, perhaps another kind)
            m.Go.SetActive(true);
            // (Felt as well as seen: a short tick in both hands in VR.)
            if (VRManager.Active && Rig.VRRig.Instance != null)
            {
                foreach (Rig.VRHand hand in Rig.VRRig.Instance.Hands)
                {
                    if (hand != null && hand.Tracked)
                    {
                        hand.Haptic(0.03f, 140f, 0.35f);
                    }
                }
            }
        }

        /// <summary>Once a frame: the marks face the eye, keep their size with distance, and go when their time is up.</summary>
        internal static void Update()
        {
            if (Marks.Count == 0)
            {
                return;
            }
            Camera eye = Camera.main;
            foreach (Mark m in Marks.Values)
            {
                if (m.Go == null || !m.Go.activeSelf)
                {
                    continue;
                }
                if (Time.unscaledTime > m.Until || eye == null)
                {
                    m.Go.SetActive(false);
                    continue;
                }
                Vector3 eyeAt = eye.transform.position;
                float distance = Vector3.Distance(eyeAt, m.At);
                int metres = Mathf.RoundToInt(distance);
                if (metres != m.ShownMetres)
                {
                    // (Its text only when the distance changed by a metre: a TextMesh rebuilt every frame is a string and a mesh each.)
                    m.ShownMetres = metres;
                    m.Text.text = (m.Enemy ? "ENEMY  " : "") + m.Name + "\n" + metres + " m\n▼";
                    m.Text.color = m.Enemy ? new Color(1f, 0.45f, 0.35f) : new Color(0.55f, 0.85f, 1f);
                    m.Plate.Fit();
                }
                // About the same size at any distance (a degree and a half of view per line), a little above the spot.
                float scale = Mathf.Clamp(distance, 2f, 400f) * 0.045f;
                Transform t = m.Go.transform;
                t.position = m.At + Vector3.up * (0.6f + scale * 0.35f);
                t.rotation = Quaternion.LookRotation(t.position - eyeAt, Vector3.up);
                t.localScale = Vector3.one * scale;
            }
        }

        private static Mark Make()
        {
            if (_font == null)
            {
                _font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            }
            var go = new GameObject("RavenfieldVR Spot") { layer = 5 };
            Object.DontDestroyOnLoad(go);
            var text = go.AddComponent<TextMesh>();
            text.font = _font;
            text.fontSize = 64;
            text.characterSize = 0.012f;
            text.anchor = TextAnchor.LowerCenter;
            text.alignment = TextAlignment.Center;
            var mr = go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = _font.material;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return new Mark { Go = go, Text = text, Plate = new UI.TextPlate(text, _font.material, 0.6f, 0.08f) };
        }

        /// <summary>A player left, or the match or session ended: his mark (or all) goes.</summary>
        internal static void Forget(byte player)
        {
            if (Marks.TryGetValue(player, out Mark m))
            {
                Destroy(m);
                Marks.Remove(player);
            }
        }

        internal static void Clear()
        {
            foreach (Mark m in Marks.Values)
            {
                Destroy(m);
            }
            Marks.Clear();
        }

        private static void Destroy(Mark m)
        {
            m.Plate?.Destroy();
            if (m.Go != null)
            {
                Object.Destroy(m.Go);
            }
        }

        private static bool Finite(Vector3 v) =>
            !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z) && !float.IsInfinity(v.x) && !float.IsInfinity(v.y) && !float.IsInfinity(v.z);
    }
}
