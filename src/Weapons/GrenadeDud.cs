using System.Collections.Generic;
using System.Text.RegularExpressions;
using RavenfieldVR.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// A grenade thrown with its pin still in: a copy of its model that bounces and lies there, inert. A free hand
    /// that grabs it puts the grenade back in the pouch; if it is left lying until the clean-up it is refunded too,
    /// so a fumbled throw never costs a grenade.
    /// </summary>
    internal sealed class GrenadeDud
    {
        private static readonly List<GrenadeDud> Alive = new List<GrenadeDud>();
        private static readonly Regex ArmBone = new Regex(@"hand|arm(?!ature|or)|wrist|finger|palm|thumb|elbow|shoulder|clavicle", RegexOptions.IgnoreCase);

        private readonly GameObject _go;
        private readonly List<Collider> _colliders;
        private readonly Weapon _source;
        private readonly string _weaponName;
        private readonly float _born;

        public static int Count
        {
            get
            {
                Prune();
                return Alive.Count;
            }
        }

        private GrenadeDud(GameObject go, List<Collider> colliders, Weapon source)
        {
            _go = go;
            _colliders = colliders;
            _source = source;
            _weaponName = source != null ? source.name : "";
            _born = Time.time;
            Alive.Add(this);
        }

        public static GrenadeDud Spawn(WeaponRig rig, Weapon weapon, Vector3 position, Quaternion rotation, Vector3 velocity, Vector3 angularVelocity)
        {
            var root = new GameObject("RavenfieldVR Dud " + weapon.name);
            Transform gun = rig != null && rig.GunBody != null ? rig.GunBody : weapon.transform;
            root.transform.SetPositionAndRotation(gun.position, gun.rotation);
            Bounds bounds = CopyModel(rig, weapon, root.transform, out bool any);
            if (!any)
            {
                bounds = Procedural(root.transform);
            }
            // Re-centre on the hand: the dud leaves from where the grenade was held.
            Vector3 offset = position - root.transform.TransformPoint(bounds.center);
            root.transform.position += offset;

            int layer = PropPhysics.Layer;
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                t.gameObject.layer = layer >= 0 ? layer : 0;
            }
            // The grenade's own shape: its body, lever and ring as convex pieces of its mesh.
            ShapeFit.Piece[] shape = any
                ? ShapeFit.Fit("dud:" + weapon.name, root.GetComponentsInChildren<Renderer>(true), root.transform.worldToLocalMatrix, 0.012f, 6)
                : new ShapeFit.Piece[0];
            if (shape.Length == 0)
            {
                shape = new[] { ShapeFit.BoxPiece(bounds.center, Vector3.Max(bounds.size, new Vector3(0.03f, 0.03f, 0.03f))) };
            }
            List<Collider> colliders = ShapeFit.AddColliders(root, shape);
            var body = root.AddComponent<Rigidbody>();
            body.mass = 0.45f;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            body.velocity = velocity;
            body.angularVelocity = angularVelocity;
            foreach (Collider c in colliders)
            {
                PropPhysics.IgnorePlayer(c);
                GunCollider.Current?.OnPropDropped(c);
            }
            Cull();
            return new GrenadeDud(root, colliders, weapon);
        }

        /// <summary>Copies the grenade's visible meshes (arms left out) under <paramref name="root"/>; bounds in root space.</summary>
        private static Bounds CopyModel(WeaponRig rig, Weapon weapon, Transform root, out bool any)
        {
            any = false;
            var skip = rig != null ? new HashSet<Renderer>(rig.ArmRenderers) : new HashSet<Renderer>();
            Bounds bounds = default;
            foreach (Renderer r in weapon.GetComponentsInChildren<Renderer>(false))
            {
                if (!r.enabled || skip.Contains(r))
                {
                    continue;
                }
                Mesh mesh;
                Vector3 scale;
                if (r is MeshRenderer)
                {
                    MeshFilter mf = r.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null)
                    {
                        continue;
                    }
                    mesh = mf.sharedMesh;
                    scale = r.transform.lossyScale;
                }
                else if (r is SkinnedMeshRenderer smr && smr.sharedMesh != null && !SkinnedToArms(smr))
                {
                    mesh = new Mesh { name = smr.sharedMesh.name + " (dud)" };
                    smr.BakeMesh(mesh);
                    scale = Vector3.one; // BakeMesh already applies the renderer's scale
                }
                else
                {
                    continue;
                }
                var part = new GameObject(r.name);
                part.transform.SetParent(root, false);
                part.transform.localPosition = root.InverseTransformPoint(r.transform.position);
                part.transform.localRotation = Quaternion.Inverse(root.rotation) * r.transform.rotation;
                part.transform.localScale = scale;
                part.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = part.AddComponent<MeshRenderer>();
                mr.sharedMaterials = Util.MaterialFix.Fix(r.sharedMaterials);
                mr.shadowCastingMode = ShadowCastingMode.On;
                Bounds mb = mesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    Vector3 p = root.InverseTransformPoint(part.transform.TransformPoint(corner));
                    if (!any)
                    {
                        bounds = new Bounds(p, Vector3.zero);
                        any = true;
                    }
                    else
                    {
                        bounds.Encapsulate(p);
                    }
                }
            }
            if (any && bounds.size.magnitude > 0.5f)
            {
                // Something long came along (a hand-held launcher arm, a stray prop): not a grenade-sized copy.
                foreach (Transform child in root)
                {
                    Object.Destroy(child.gameObject);
                }
                any = false;
            }
            return bounds;
        }

        private static bool SkinnedToArms(SkinnedMeshRenderer smr)
        {
            Transform[] bones = smr.bones;
            if (bones == null || bones.Length == 0)
            {
                return false;
            }
            int arms = 0;
            foreach (Transform b in bones)
            {
                if (b != null && ArmBone.IsMatch(b.name))
                {
                    arms++;
                }
            }
            return arms * 3 >= bones.Length; // a third or more of the bones are arm bones: the arms share this mesh
        }

        private static Bounds Procedural(Transform root)
        {
            GameObject body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            Object.DestroyImmediate(body.GetComponent<Collider>());
            body.transform.SetParent(root, false);
            body.transform.localScale = new Vector3(0.055f, 0.04f, 0.055f);
            Shader s = Shader.Find("Standard") ?? Shader.Find("Legacy Shaders/Diffuse");
            body.GetComponent<Renderer>().sharedMaterial = new Material(s) { color = new Color(0.27f, 0.3f, 0.21f) };
            return new Bounds(Vector3.zero, new Vector3(0.055f, 0.08f, 0.055f));
        }

        private float DistanceTo(Vector3 point)
        {
            float best = float.MaxValue;
            foreach (Collider c in _colliders)
            {
                if (c != null && c.enabled)
                {
                    best = Mathf.Min(best, Vector3.Distance(c.ClosestPoint(point), point));
                }
            }
            return best;
        }

        public static GrenadeDud Nearest(Vector3 point, float radius)
        {
            Prune();
            GrenadeDud best = null;
            float bestDist = radius;
            foreach (GrenadeDud d in Alive)
            {
                float dist = d.DistanceTo(point);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = d;
                }
            }
            return best;
        }

        public float Distance(Vector3 point) => DistanceTo(point);

        /// <summary>Puts the grenade back with <paramref name="actor"/>'s matching weapon; false when there is no room.</summary>
        public bool Refund(Actor actor)
        {
            Weapon target = _source != null && _source.user == actor ? _source : null;
            if (target == null && actor != null && actor.weapons != null)
            {
                foreach (Weapon w in actor.weapons)
                {
                    if (w != null && w.name == _weaponName)
                    {
                        target = w;
                        break;
                    }
                }
            }
            if (target == null)
            {
                return false;
            }
            if (!target.HasInfiniteSpareAmmo())
            {
                if (target.ammo < target.configuration.ammo)
                {
                    target.ammo++;
                }
                else if (target.spareAmmo < target.configuration.spareAmmo)
                {
                    target.spareAmmo++;
                }
                else
                {
                    return false;
                }
                target.OnAmmoChanged();
            }
            Destroy();
            return true;
        }

        /// <summary>Autotest helper: picks the newest dud back up for <paramref name="actor"/>.</summary>
        internal static bool RefundLast(Actor actor)
        {
            Prune();
            return Alive.Count > 0 && Alive[Alive.Count - 1].Refund(actor);
        }

        public void Destroy()
        {
            Alive.Remove(this);
            if (_go != null)
            {
                Object.Destroy(_go);
            }
        }

        private static void Prune()
        {
            for (int i = Alive.Count - 1; i >= 0; i--)
            {
                if (Alive[i]._go == null)
                {
                    Alive.RemoveAt(i);
                }
            }
        }

        /// <summary>Same lifetime and cap as dropped magazines; a dud that expires goes back to the pouch.</summary>
        public static void Cull()
        {
            Prune();
            float lifetime = VRConfig.MagPropLifetime.Value;
            int max = Mathf.Max(1, VRConfig.MaxMagProps.Value);
            for (int i = Alive.Count - 1; i >= 0; i--)
            {
                GrenadeDud d = Alive[i];
                if (Time.time - d._born > lifetime || d._go.transform.position.y < -500f)
                {
                    d.Expire();
                }
            }
            while (Alive.Count > max)
            {
                Alive[0].Expire(); // oldest first
            }
        }

        private void Expire()
        {
            if (!Refund(LocalPlayer.actor))
            {
                Destroy();
            }
        }

        public static void DestroyAll()
        {
            for (int i = Alive.Count - 1; i >= 0; i--)
            {
                Alive[i].Destroy();
            }
            Alive.Clear();
        }
    }

    /// <summary>The split ring of a grenade pin: sits in the fuse, comes away in the hand, then drops.</summary>
    internal static class PinRing
    {
        private static Mesh _mesh;
        private static Material _material;

        public static GameObject Create()
        {
            var go = new GameObject("RavenfieldVR Pin");
            go.layer = MagazineProp.DisplayLayer;
            go.AddComponent<MeshFilter>().sharedMesh = RingMesh();
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = RingMaterial();
            mr.shadowCastingMode = ShadowCastingMode.Off;
            return go;
        }

        /// <summary>A copy of the grenade's own pin part (its meshes and materials), centred on it; the plain ring when it has no mesh.</summary>
        public static GameObject CreateFrom(Transform pin, Vector3 center)
        {
            var go = new GameObject("RavenfieldVR Pin");
            go.layer = MagazineProp.DisplayLayer;
            go.transform.SetPositionAndRotation(center, pin.rotation);
            bool any = false;
            foreach (MeshRenderer r in pin.GetComponentsInChildren<MeshRenderer>(true))
            {
                MeshFilter mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null)
                {
                    continue;
                }
                var part = new GameObject(r.name) { layer = go.layer };
                part.transform.SetParent(go.transform, false);
                part.transform.localPosition = go.transform.InverseTransformPoint(r.transform.position);
                part.transform.localRotation = Quaternion.Inverse(go.transform.rotation) * r.transform.rotation;
                part.transform.localScale = r.transform.lossyScale;
                part.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
                var mr = part.AddComponent<MeshRenderer>();
                mr.sharedMaterials = Util.MaterialFix.Fix(r.sharedMaterials);
                mr.shadowCastingMode = ShadowCastingMode.Off;
                any = true;
            }
            if (!any)
            {
                go.AddComponent<MeshFilter>().sharedMesh = RingMesh();
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = RingMaterial();
                mr.shadowCastingMode = ShadowCastingMode.Off;
            }
            return go;
        }

        public static void Drop(GameObject ring, Vector3 velocity)
        {
            int layer = PropPhysics.Layer;
            ring.layer = layer >= 0 ? layer : 0;
            var c = ring.AddComponent<SphereCollider>();
            c.radius = 0.012f;
            var body = ring.AddComponent<Rigidbody>();
            body.mass = 0.01f;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            body.velocity = velocity;
            body.angularVelocity = Random.insideUnitSphere * 8f;
            PropPhysics.IgnorePlayer(c);
            Object.Destroy(ring, 8f);
        }

        private static Material RingMaterial()
        {
            if (_material == null)
            {
                Shader s = Shader.Find("Standard") ?? Shader.Find("Legacy Shaders/Diffuse");
                _material = new Material(s) { name = "RavenfieldVR pin", color = new Color(0.62f, 0.6f, 0.55f) };
                if (_material.HasProperty("_Metallic"))
                {
                    _material.SetFloat("_Metallic", 0.85f);
                    _material.SetFloat("_Glossiness", 0.6f);
                }
            }
            return _material;
        }

        /// <summary>A torus in the XY plane: 11 mm ring radius, 1.6 mm wire.</summary>
        private static Mesh RingMesh()
        {
            if (_mesh != null)
            {
                return _mesh;
            }
            const int major = 20, minor = 8;
            const float r1 = 0.011f, r2 = 0.0016f;
            var verts = new Vector3[(major + 1) * (minor + 1)];
            var normals = new Vector3[verts.Length];
            var tris = new int[major * minor * 6];
            for (int i = 0; i <= major; i++)
            {
                float a = i * Mathf.PI * 2f / major;
                var center = new Vector3(Mathf.Cos(a) * r1, Mathf.Sin(a) * r1, 0f);
                Vector3 outward = center.normalized;
                for (int j = 0; j <= minor; j++)
                {
                    float b = j * Mathf.PI * 2f / minor;
                    Vector3 n = outward * Mathf.Cos(b) + Vector3.forward * Mathf.Sin(b);
                    verts[i * (minor + 1) + j] = center + n * r2;
                    normals[i * (minor + 1) + j] = n;
                }
            }
            int t = 0;
            for (int i = 0; i < major; i++)
            {
                for (int j = 0; j < minor; j++)
                {
                    int a = i * (minor + 1) + j, b = (i + 1) * (minor + 1) + j;
                    tris[t++] = a;
                    tris[t++] = b;
                    tris[t++] = a + 1;
                    tris[t++] = b;
                    tris[t++] = b + 1;
                    tris[t++] = a + 1;
                }
            }
            _mesh = new Mesh { name = "RavenfieldVR pin ring", vertices = verts, normals = normals, triangles = tris };
            _mesh.RecalculateBounds();
            return _mesh;
        }
    }
}
