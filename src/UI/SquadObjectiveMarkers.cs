using System.Collections.Generic;
using UnityEngine;

namespace RavenfieldVR.UI
{
    /// <summary>
    /// The capture points as flags while squad pointing is open in VR (task 297). In the flat game the squad order screen
    /// draws each flag over the view and an order goes to the flag the aim is on. In VR that screen is kept off the view, so
    /// the flags could not be seen and could not be pointed at. Now, with the squad order screen open and a hand held out
    /// pointing, every capture point shows a flag in its owner's team colour with its name on top. The flags are drawn over
    /// the world by the UI camera, as the order label and the arrow are. The flag the pointing line is within a few degrees of
    /// is drawn brighter, and an order given then goes to that point (see <see cref="Aimed(Ray, Vector3, out float)"/>).
    /// Flags beyond <see cref="MaxDrawDistance"/> stand on the sightline at that distance, at the size they have there. The UI
    /// camera's far plane is 200 m and the capture points are spread farther apart than that.
    /// </summary>
    internal sealed class SquadObjectiveMarkers
    {
        /// <summary>How far off the pointing line a flag may be and still count as the one pointed at (degrees).</summary>
        internal const float PointedDegrees = 5f;

        // The flag is modelled 3 units tall. At 25 m it is 3 m tall, about 7 degrees high, and it keeps that angular size at
        // every distance. Nearer than 10 m it stops shrinking, so it stays readable.
        private const float MetresPerUnit = 25f;
        private const float MinUnits = 0.4f;
        private const float MaxDrawDistance = 150f;
        private const float HighlightScale = 1.25f;

        /// <summary>The middle of the cloth in units: where the pointing line is measured to.</summary>
        private const float CentreHeight = 2.6f;
        private const float PoleTop = 3f;
        private const float ClothLeft = 0.04f, ClothRight = 1.3f, ClothBottom = 2.2f;
        private const float LabelHeight = 3.5f;

        private static readonly Color LabelColour = new Color(0.92f, 0.95f, 1f, 0.9f);
        private static readonly Color PointedLabelColour = new Color(0.62f, 0.85f, 1f, 1f);
        private static readonly Color PoleColour = new Color(0.16f, 0.17f, 0.19f, 0.95f);

        private sealed class Flag
        {
            public SpawnPoint Spawn;
            public GameObject Root;
            public Material ClothMaterial;
            public Material PoleMaterial;
            public TextMesh Label;
            public TextPlate Plate;
            public int ShownOwner = int.MinValue;
            public bool ShownPointed;
            public int FitFrames;
        }

        private readonly List<Flag> _flags = new List<Flag>();
        private SpawnPoint[] _source;
        private readonly Font _font;
        private readonly Mesh _pole;
        private readonly Mesh _cloth;

        public SquadObjectiveMarkers()
        {
            _font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            _pole = PoleMesh();
            _cloth = ClothMesh();
        }

        /// <summary>
        /// The capture point the pointing line (from the eye) is within <see cref="PointedDegrees"/> of, the nearest if several,
        /// or null. It is measured to the middle of the flag that is drawn for the point, so what is pointed at is what is seen.
        /// </summary>
        internal static SpawnPoint Aimed(Ray line, Vector3 eye) => Aimed(line, eye, out _);

        /// <summary>The same, with how far off the line the flag is (degrees; past <see cref="PointedDegrees"/> when none).</summary>
        internal static SpawnPoint Aimed(Ray line, Vector3 eye, out float bestAngle)
        {
            bestAngle = PointedDegrees;
            ActorManager manager = ActorManager.instance;
            SpawnPoint[] spawns = manager != null ? manager.spawnPoints : null;
            if (spawns == null)
            {
                return null;
            }
            SpawnPoint best = null;
            foreach (SpawnPoint spawn in spawns)
            {
                if (spawn == null || spawn.isGhostSpawn || spawn.squadOrderPoint == null)
                {
                    continue; // a spawn point out of this match, or one the squad cannot be ordered to
                }
                Place(spawn.transform.position, eye, out Vector3 root, out float scale);
                float angle = Vector3.Angle(line.direction, root + Vector3.up * (CentreHeight * scale) - eye);
                if (angle < bestAngle)
                {
                    bestAngle = angle;
                    best = spawn;
                }
            }
            return best;
        }

        /// <summary>
        /// Where a flag at <paramref name="point"/> is drawn: on the sightline from the eye, at most <see cref="MaxDrawDistance"/>
        /// away, and how big it is there (<paramref name="scale"/> in units).
        /// </summary>
        internal static void Place(Vector3 point, Vector3 eye, out Vector3 root, out float scale)
        {
            Vector3 to = point - eye;
            float distance = to.magnitude;
            float drawn = Mathf.Min(distance, MaxDrawDistance);
            root = distance > 1e-3f ? eye + to * (drawn / distance) : point;
            scale = Mathf.Max(MinUnits, drawn / MetresPerUnit);
        }

        /// <summary>
        /// Each frame: while <paramref name="show"/> (the squad order screen is open and a hand points), each capture point's flag
        /// is placed and coloured by its owner, and the one <paramref name="pointed"/> is brighter. Hidden otherwise.
        /// </summary>
        internal void BeforeRender(bool show, Vector3 eye, SpawnPoint pointed)
        {
            ActorManager manager = ActorManager.instance;
            SpawnPoint[] spawns = manager != null ? manager.spawnPoints : null;
            if (show && spawns != null && !ReferenceEquals(spawns, _source))
            {
                Sync(spawns);
            }
            for (int i = 0; i < _flags.Count; i++)
            {
                Flag flag = _flags[i];
                if (flag.Root == null)
                {
                    continue; // its scene has gone
                }
                SpawnPoint spawn = flag.Spawn;
                bool on = show && spawn != null && !spawn.isGhostSpawn && spawn.squadOrderPoint != null;
                if (flag.Root.activeSelf != on)
                {
                    flag.Root.SetActive(on);
                    flag.FitFrames = 2; // laid out in the frame it shows, then fitted round its name
                }
                if (!on)
                {
                    continue;
                }
                Place(spawn.transform.position, eye, out Vector3 root, out float scale);
                bool pointedAt = ReferenceEquals(spawn, pointed);
                Transform t = flag.Root.transform;
                t.position = root;
                // Turned to face the eye (the flag and its name), upright.
                Vector3 away = root - eye;
                away.y = 0f;
                t.rotation = away.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(away) : Quaternion.identity;
                float size = pointedAt ? scale * HighlightScale : scale;
                t.localScale = new Vector3(size, size, size);
                if (spawn.owner != flag.ShownOwner || pointedAt != flag.ShownPointed)
                {
                    // Colours change only when the owner or the pointing does, not every frame.
                    flag.ShownOwner = spawn.owner;
                    flag.ShownPointed = pointedAt;
                    Color team = ColorScheme.TeamColor(spawn.owner);
                    flag.ClothMaterial.color = pointedAt ? Color.Lerp(team, Color.white, 0.45f) : team;
                    flag.Label.color = pointedAt ? PointedLabelColour : LabelColour;
                }
                if (flag.FitFrames > 0)
                {
                    flag.FitFrames--;
                    flag.Plate.Fit();
                }
            }
        }

        /// <summary>Removes every flag: the scene they belong to is going. The next pointing makes them again.</summary>
        internal void Clear()
        {
            foreach (Flag flag in _flags)
            {
                if (flag.Plate != null)
                {
                    flag.Plate.Destroy();
                }
                if (flag.PoleMaterial != null)
                {
                    Object.Destroy(flag.PoleMaterial);
                }
                if (flag.ClothMaterial != null)
                {
                    Object.Destroy(flag.ClothMaterial);
                }
                if (flag.Root != null)
                {
                    Object.Destroy(flag.Root);
                }
            }
            _flags.Clear();
            _source = null;
        }

        /// <summary>Removes the flags and the shared meshes (the mod is closing).</summary>
        internal void Destroy()
        {
            Clear();
            if (_pole != null)
            {
                Object.Destroy(_pole);
            }
            if (_cloth != null)
            {
                Object.Destroy(_cloth);
            }
        }

        /// <summary>Makes one flag for each spawn point the first time the list is seen (once per scene).</summary>
        private void Sync(SpawnPoint[] spawns)
        {
            _source = spawns;
            foreach (SpawnPoint spawn in spawns)
            {
                if (spawn != null && FlagOf(spawn) == null)
                {
                    _flags.Add(MakeFlag(spawn));
                }
            }
        }

        private Flag FlagOf(SpawnPoint spawn)
        {
            foreach (Flag flag in _flags)
            {
                if (ReferenceEquals(flag.Spawn, spawn))
                {
                    return flag;
                }
            }
            return null;
        }

        private static Shader _sprites;

        private Flag MakeFlag(SpawnPoint spawn)
        {
            var flag = new Flag { Spawn = spawn };
            flag.Root = new GameObject("RavenfieldVR Objective " + spawn.shortName) { layer = 5 };
            flag.Root.SetActive(false);
            Transform root = flag.Root.transform;

            // Sprites/Default, as the order arrow is: it blends, and the UI camera clears the depth before it draws layer 5, so
            // the world does not hide a flag.
            // (The shader found once: two lookups by name for every flag, all made in the first pointing's frame.)
            _sprites = _sprites != null ? _sprites : Shader.Find("Sprites/Default");
            flag.PoleMaterial = new Material(_sprites) { color = PoleColour, renderQueue = 4000 };
            AddPart(root, "pole", _pole, flag.PoleMaterial);
            flag.ClothMaterial = new Material(_sprites) { color = Color.white, renderQueue = 4000 };
            AddPart(root, "cloth", _cloth, flag.ClothMaterial);

            var labelGo = new GameObject("name") { layer = 5 };
            labelGo.transform.SetParent(root, false);
            labelGo.transform.localPosition = new Vector3((ClothLeft + ClothRight) / 2f, LabelHeight, 0f);
            TextMesh label = labelGo.AddComponent<TextMesh>();
            label.font = _font;
            label.fontSize = 64;
            // A line 64 x 0.1 x 0.1 = 0.64 units tall, capitals about 0.45 (half a metre at 25 m, about a degree). At 0.011 it was
            // 0.07 units, a few pixels in the headset, and the flags looked nameless (user, 2026-10-24: "the flags dont have names").
            label.characterSize = 0.1f;
            label.anchor = TextAnchor.MiddleCenter;
            label.alignment = TextAlignment.Center;
            label.text = string.IsNullOrEmpty(spawn.shortName) ? "?" : spawn.shortName;
            label.color = LabelColour;
            MeshRenderer mr = labelGo.GetComponent<MeshRenderer>();
            mr.sharedMaterial = _font.material;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            flag.Label = label;
            flag.Plate = new TextPlate(label, _font.material, 0.75f, 0.12f);
            return flag;
        }

        private static void AddPart(Transform parent, string name, Mesh mesh, Material material)
        {
            var go = new GameObject(name) { layer = 5 };
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
        }

        /// <summary>A thin pole, two strips crossed so it reads from every side, from the flag's base up to its top.</summary>
        private static Mesh PoleMesh()
        {
            const float w = 0.035f;
            var m = new Mesh { name = "RavenfieldVR Squad Flag Pole" };
            m.vertices = new[]
            {
                new Vector3(-w, 0f, 0f), new Vector3(w, 0f, 0f), new Vector3(w, PoleTop, 0f), new Vector3(-w, PoleTop, 0f),
                new Vector3(0f, 0f, -w), new Vector3(0f, 0f, w), new Vector3(0f, PoleTop, w), new Vector3(0f, PoleTop, -w)
            };
            m.triangles = new[] { 0, 2, 1, 0, 3, 2, 4, 6, 5, 4, 7, 6 };
            m.RecalculateBounds();
            return m;
        }

        /// <summary>The cloth: a flat panel beside the top of the pole (drawn from both sides: the sprite shader does not cull).</summary>
        private static Mesh ClothMesh()
        {
            var m = new Mesh { name = "RavenfieldVR Squad Flag Cloth" };
            m.vertices = new[]
            {
                new Vector3(ClothLeft, ClothBottom, 0f), new Vector3(ClothRight, ClothBottom, 0f),
                new Vector3(ClothRight, PoleTop, 0f), new Vector3(ClothLeft, PoleTop, 0f)
            };
            m.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            m.RecalculateBounds();
            return m;
        }
    }
}
