using System.Collections.Generic;
using RavenfieldVR.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// Spent cases you can see land, as in Boneworks: a self-loading gun throws a brass case out of its ejection port with
    /// every shot, a bolt or pump throws the case (or the live round) out when it is worked, a revolver or a break-action
    /// drops its fired cases when opened. They tumble, bounce with a tink and lie on the ground for a while (40 at most,
    /// the oldest go first). Only for weapons that eject cases in the flat game (they have casing particles, which the
    /// held weapon no longer shows): an energy weapon stays clean.
    /// </summary>
    internal static class Casings
    {
        private const int Max = 40;
        private const float Lifetime = 25f;

        private enum Kind { Pistol, Rifle, Heavy, Shell }

        private sealed class Case
        {
            public GameObject Go;
            public float Born;
        }

        private static readonly List<Case> Alive = new List<Case>();
        private static Mesh[] _meshes, _liveMeshes;
        private static Material _brass, _copper, _hull;
        private static PhysicMaterial _bounce;

        /// <summary>The weapon throws cases in the flat game (and so in VR).</summary>
        public static bool Ejects(HeldWeapon held)
        {
            Weapon w = held?.ActiveWeapon;
            ParticleSystem[] p = w != null ? w.configuration.casingParticles : null;
            return p != null && p.Length > 0 && p[0] != null;
        }

        /// <summary>Throws one spent case (or a live round) out of the held weapon's ejection port.</summary>
        public static void Eject(HeldWeapon held, bool live = false)
        {
            if (held == null || !Ejects(held) || held.ActiveRig.GunBody == null)
            {
                return;
            }
            WeaponRig rig = held.ActiveRig;
            WeaponRigData d = rig.Data;
            Transform port = held.ActiveWeapon.configuration.casingParticles[0].transform;
            Vector3 right = rig.DirToWorld(Vector3.Cross(d.Up, d.AimAxis).normalized);
            Vector3 up = rig.UpWorld, back = -rig.AimWorld;
            Vector3 hand = Rig.VRRig.Instance != null ? Rig.VRRig.Instance.Dominant.Velocity : Vector3.zero;
            Vector3 velocity = hand + right * Random.Range(1.8f, 2.6f) + up * Random.Range(0.8f, 1.5f) + back * Random.Range(0.1f, 0.5f);
            Spawn(KindOf(held), live, port.position + right * 0.01f, Quaternion.LookRotation(right, up), velocity,
                Random.insideUnitSphere * 25f + rig.DirToWorld(d.Up) * 10f);
        }

        /// <summary>Fired cases falling out of an opened cylinder or break-action, straight down out of the chambers.</summary>
        public static void Drop(HeldWeapon held, int count, Vector3 chambers)
        {
            if (held == null || !Ejects(held))
            {
                return;
            }
            Kind kind = KindOf(held);
            Vector3 aim = held.ActiveRig.AimWorld;
            for (int i = 0; i < Mathf.Min(count, 8); i++)
            {
                Vector3 at = chambers + Random.insideUnitSphere * 0.012f;
                Spawn(kind, false, at, Quaternion.LookRotation(-aim, Vector3.up), Vector3.down * Random.Range(0.3f, 0.9f) - aim * 0.4f, Random.insideUnitSphere * 8f);
            }
        }

        private static Kind KindOf(HeldWeapon held)
        {
            WeaponRigData d = held.ActiveRig.Data;
            Weapon.Configuration c = held.ActiveWeapon.configuration;
            if (c.projectilesPerShot > 1 && (d.Action == ActionType.ShellFed || d.Action == ActionType.BreakOpen || d.Action == ActionType.Manual))
            {
                return Kind.Shell;
            }
            if (d.IsPistol)
            {
                return Kind.Pistol;
            }
            float length = Mathf.Abs(Vector3.Dot(d.MuzzlePoint - d.GripPoint, d.AimAxis));
            return d.IsMachineGun || d.Action == ActionType.Manual && length > 0.55f ? Kind.Heavy : Kind.Rifle;
        }

        private static void Spawn(Kind kind, bool live, Vector3 position, Quaternion rotation, Vector3 velocity, Vector3 spin)
        {
            EnsureAssets();
            Cull();
            if (Alive.Count >= Max)
            {
                Object.Destroy(Alive[0].Go);
                Alive.RemoveAt(0);
            }
            var go = new GameObject(live ? "RavenfieldVR Round" : "RavenfieldVR Case");
            int layer = PropPhysics.Layer;
            go.layer = layer >= 0 ? layer : 0; // bullets pass through it
            go.transform.SetPositionAndRotation(position, rotation);
            Mesh mesh = live ? _liveMeshes[(int)kind] : _meshes[(int)kind];
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterials = kind == Kind.Shell ? new[] { _hull, _brass } : live ? new[] { _brass, _copper } : new[] { _brass };
            mr.shadowCastingMode = ShadowCastingMode.Off;
            var col = go.AddComponent<CapsuleCollider>();
            col.direction = 2;
            col.center = mesh.bounds.center;
            col.radius = Mathf.Max(mesh.bounds.extents.x, 0.003f);
            col.height = mesh.bounds.size.z;
            col.sharedMaterial = _bounce;
            var body = go.AddComponent<Rigidbody>();
            body.mass = kind == Kind.Shell ? 0.03f : 0.012f;
            body.drag = 0.1f;
            body.angularDrag = 0.2f;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.velocity = velocity;
            body.angularVelocity = spin;
            PropPhysics.IgnorePlayer(col);
            GunCollider.Current?.OnPropDropped(col); // out of the port it passes through the gun
            go.AddComponent<CaseSound>();
            Alive.Add(new Case { Go = go, Born = Time.time });
            Spawned++;
        }

        /// <summary>Clears cases past their time (also called per frame by the weapon system).</summary>
        public static void Cull()
        {
            float now = Time.time;
            for (int i = Alive.Count - 1; i >= 0; i--)
            {
                Case c = Alive[i];
                if (c.Go == null || now - c.Born > Lifetime || c.Go.transform.position.y < -500f)
                {
                    if (c.Go != null)
                    {
                        Object.Destroy(c.Go);
                    }
                    Alive.RemoveAt(i);
                }
            }
        }

        private static GameObject _chamber;
        private static bool _chamberLive;

        /// <summary>
        /// Press check: with the slide or bolt eased back part way, the round in the chamber (or a bolt gun's fired case)
        /// shows in the ejection port. Hidden otherwise.
        /// </summary>
        public static void ShowChamber(HeldWeapon held, bool show, bool live)
        {
            show &= held != null && Ejects(held) && held.ActiveRig.GunBody != null;
            if (!show)
            {
                if (_chamber != null && _chamber.activeSelf)
                {
                    _chamber.SetActive(false);
                }
                return;
            }
            EnsureAssets();
            Kind kind = KindOf(held);
            if (_chamber == null)
            {
                _chamber = new GameObject("RavenfieldVR Chambered Round") { layer = MagazineProp.DisplayLayer };
                Object.DontDestroyOnLoad(_chamber);
                _chamber.AddComponent<MeshFilter>();
                var mr = _chamber.AddComponent<MeshRenderer>();
                mr.shadowCastingMode = ShadowCastingMode.Off;
            }
            MeshFilter mf = _chamber.GetComponent<MeshFilter>();
            Mesh mesh = live ? _liveMeshes[(int)kind] : _meshes[(int)kind];
            if (mf.sharedMesh != mesh || _chamberLive != live)
            {
                mf.sharedMesh = mesh;
                _chamberLive = live;
                _chamber.GetComponent<MeshRenderer>().sharedMaterials = kind == Kind.Shell ? new[] { _hull, _brass } : live ? new[] { _brass, _copper } : new[] { _brass };
            }
            WeaponRig rig = held.ActiveRig;
            Transform port = held.ActiveWeapon.configuration.casingParticles[0].transform;
            Vector3 aim = rig.AimWorld;
            // In the port, head toward the shooter, the length of a case back from its mouth.
            Vector3 right = rig.DirToWorld(Vector3.Cross(rig.Data.Up, rig.Data.AimAxis).normalized);
            _chamber.transform.SetPositionAndRotation(port.position - right * 0.004f - aim * mesh.bounds.size.z * 0.5f, Quaternion.LookRotation(aim, rig.UpWorld));
            if (!_chamber.activeSelf)
            {
                _chamber.SetActive(true);
            }
        }

        /// <summary>Cases and rounds thrown since start (self-test).</summary>
        internal static int Spawned { get; private set; }

        /// <summary>How many cases lie around (self-test).</summary>
        internal static int Count => Alive.Count;

        public static void DestroyAll()
        {
            if (_chamber != null)
            {
                _chamber.SetActive(false);
            }
            foreach (Case c in Alive)
            {
                if (c.Go != null)
                {
                    Object.Destroy(c.Go);
                }
            }
            Alive.Clear();
        }

        private static void EnsureAssets()
        {
            if (_meshes != null)
            {
                return;
            }
            Shader s = Shader.Find("Standard") ?? Shader.Find("Legacy Shaders/Diffuse");
            _brass = new Material(s) { name = "RavenfieldVR case brass", color = new Color(0.78f, 0.6f, 0.27f) };
            _copper = new Material(s) { name = "RavenfieldVR bullet", color = new Color(0.66f, 0.38f, 0.22f) };
            _hull = new Material(s) { name = "RavenfieldVR shell hull", color = new Color(0.55f, 0.08f, 0.06f) };
            if (_brass.HasProperty("_Metallic"))
            {
                _brass.SetFloat("_Metallic", 0.9f);
                _brass.SetFloat("_Glossiness", 0.6f);
                _copper.SetFloat("_Metallic", 0.85f);
                _copper.SetFloat("_Glossiness", 0.5f);
                _hull.SetFloat("_Glossiness", 0.35f);
            }
            _bounce = new PhysicMaterial("RavenfieldVR case") { bounciness = 0.35f, dynamicFriction = 0.5f, staticFriction = 0.6f, bounceCombine = PhysicMaterialCombine.Maximum };
            // Profiles along +Z (z, radius), case head at 0: 9 mm, 5.56 mm, 7.62 mm, 12 gauge.
            float[][] z =
            {
                new[] { 0f, 0f, 0.019f, 0.019f },
                new[] { 0f, 0f, 0.036f, 0.039f, 0.045f, 0.045f },
                new[] { 0f, 0f, 0.041f, 0.044f, 0.051f, 0.051f },
                new[] { 0f, 0f, 0.07f, 0.07f }
            };
            float[][] r =
            {
                new[] { 0f, 0.0049f, 0.0048f, 0.0042f },
                new[] { 0f, 0.0048f, 0.0047f, 0.0032f, 0.0029f, 0.0025f },
                new[] { 0f, 0.0060f, 0.0058f, 0.0040f, 0.0036f, 0.0031f },
                new[] { 0f, 0.0105f, 0.0102f, 0.0094f }
            };
            float[] bullet = { 0.008f, 0.019f, 0.022f, 0f };
            _meshes = new Mesh[4];
            _liveMeshes = new Mesh[4];
            for (int k = 0; k < 4; k++)
            {
                _meshes[k] = Lathe(z[k], r[k], k == 3 ? 0.016f : 0f, 0f);
                _liveMeshes[k] = k == 3 ? _meshes[k] : Lathe(z[k], r[k], 0f, bullet[k]);
            }
        }

        /// <summary>
        /// A turned case along +Z. Shells: the brass head (submesh 1) up to <paramref name="head"/>, the hull above (submesh
        /// 0). Live rounds: a bullet of <paramref name="bullet"/> length on the mouth (submesh 1).
        /// </summary>
        private static Mesh Lathe(float[] z, float[] r, float head, float bullet)
        {
            const int sides = 10;
            var verts = new List<Vector3>();
            var normals = new List<Vector3>();
            var main = new List<int>();
            var second = new List<int>();
            var zs = new List<float>(z);
            var rs = new List<float>(r);
            if (bullet > 0f)
            {
                float mouth = zs[zs.Count - 1], mr = rs[rs.Count - 1];
                zs.Add(mouth + bullet * 0.55f); rs.Add(mr * 0.95f);
                zs.Add(mouth + bullet); rs.Add(0f);
            }
            for (int i = 0; i < zs.Count; i++)
            {
                for (int k = 0; k <= sides; k++)
                {
                    float a = k * Mathf.PI * 2f / sides;
                    var dir = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
                    verts.Add(new Vector3(dir.x * rs[i], dir.y * rs[i], zs[i]));
                    normals.Add(rs[i] > 0f ? dir : new Vector3(0f, 0f, i == 0 ? -1f : 1f));
                }
            }
            int caseRings = z.Length;
            for (int i = 0; i < zs.Count - 1; i++)
            {
                bool other = bullet > 0f ? i >= caseRings - 1 : head > 0f && zs[i + 1] <= head + 1e-4f;
                List<int> tris = other ? second : main;
                for (int k = 0; k < sides; k++)
                {
                    int a = i * (sides + 1) + k, b = (i + 1) * (sides + 1) + k;
                    tris.Add(a); tris.Add(b); tris.Add(a + 1);
                    tris.Add(b); tris.Add(b + 1); tris.Add(a + 1);
                }
            }
            if (head > 0f)
            {
                // The shell's brass head band, a little proud of the hull.
                int start = verts.Count;
                for (int k = 0; k <= sides; k++)
                {
                    float a = k * Mathf.PI * 2f / sides;
                    var dir = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
                    verts.Add(dir * (r[1] + 0.0004f));
                    normals.Add(dir);
                    verts.Add(dir * (r[1] + 0.0004f) + new Vector3(0f, 0f, head));
                    normals.Add(dir);
                }
                for (int k = 0; k < sides; k++)
                {
                    int a = start + k * 2;
                    second.Add(a); second.Add(a + 1); second.Add(a + 2);
                    second.Add(a + 1); second.Add(a + 3); second.Add(a + 2);
                }
            }
            var mesh = new Mesh { name = "RavenfieldVR case" };
            mesh.SetVertices(verts);
            mesh.SetNormals(normals);
            mesh.subMeshCount = second.Count > 0 ? 2 : 1;
            mesh.SetTriangles(main, 0);
            if (second.Count > 0)
            {
                mesh.SetTriangles(second, 1);
            }
            mesh.RecalculateBounds();
            return mesh;
        }
    }

    /// <summary>A case's tink when it lands (twice at most, softer for a slow touch).</summary>
    internal sealed class CaseSound : MonoBehaviour
    {
        private int _tinks;

        private void OnCollisionEnter(Collision c)
        {
            float speed = c.relativeVelocity.magnitude;
            if (_tinks >= 2 || speed < 0.6f || WeaponSystem.Instance == null)
            {
                return;
            }
            _tinks++;
            WeaponSystem.Instance.Sounds.Play(MechSound.Casing, transform.position, Mathf.Clamp01(speed / 4f) * (_tinks == 1 ? 0.55f : 0.3f));
        }
    }
}
