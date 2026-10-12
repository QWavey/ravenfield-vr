using RavenfieldVR.Core;
using RavenfieldVR.Rig;
using UnityEngine;
using UnityEngine.Rendering;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// Picture-in-picture lens for scoped weapons that only had a 2D scope overlay in flat mode. A camera
    /// on the sight line renders the magnified view into a texture shown on a disc at the ocular lens, with a
    /// procedural duplex reticle. It only renders while your eye is near the sight.
    /// </summary>
    public sealed class VRScope
    {
        private const float LensRadius = 0.017f;
        private const float EyeRelief = 0.09f;
        private const int IgnoreLayers = (1 << 5) | (1 << 18);

        private readonly VRRig _vrRig;
        private readonly WeaponRig _rig;
        private readonly Camera _camera;
        private readonly RenderTexture _texture;
        private readonly GameObject _root;
        private readonly Material _lensMaterial;
        private static Texture2D _reticle;
        private static Mesh _disc;

        public static bool Wanted(Weapon w, WeaponRig rig)
        {
            if (!rig.Data.HasEyePoint || w.renderTextureCamera != null || w.configuration.scopeAimObject == null)
            {
                return false;
            }
            return w.GetAimFov() < 40f;
        }

        public VRScope(VRRig vrRig, WeaponRig rig)
        {
            _vrRig = vrRig;
            _rig = rig;
            int res = VRConfig.ScopeResolution.Value;
            _texture = new RenderTexture(res, res, 24, RenderTextureFormat.Default) { name = "RavenfieldVR Scope", antiAliasing = 2 };
            _texture.Create();

            _root = new GameObject("RavenfieldVR Scope");
            _root.transform.SetParent(rig.GunBody, false);
            Vector3 lensPos = rig.Data.EyePoint + rig.Data.EyeDirection * EyeRelief;
            _root.transform.localPosition = lensPos;
            _root.transform.localRotation = Quaternion.LookRotation(rig.Data.EyeDirection, rig.Data.Up);

            var camGo = new GameObject("Scope Camera");
            camGo.transform.SetParent(_root.transform, false);
            camGo.transform.localPosition = new Vector3(0f, 0f, 0.02f);
            _camera = camGo.AddComponent<Camera>();
            _camera.targetTexture = _texture;
            _camera.stereoTargetEye = StereoTargetEyeMask.None;
            _camera.nearClipPlane = 0.05f;
            _camera.enabled = false;
            vrRig.Suppressor.Ignore(_camera);

            EnsureAssets();
            _lensMaterial = new Material(Shader.Find("Sprites/Default")) { mainTexture = _texture, renderQueue = 3000 };
            CreateDisc("Lens", _lensMaterial, 0f);
            CreateDisc("Reticle", new Material(Shader.Find("Sprites/Default")) { mainTexture = _reticle, renderQueue = 3001 }, -0.0008f);
        }

        private void CreateDisc(string name, Material mat, float z)
        {
            var go = new GameObject(name);
            go.layer = 18;
            go.transform.SetParent(_root.transform, false);
            go.transform.localPosition = new Vector3(0f, 0f, z);
            // The disc faces back toward the eye (-Z of the sight line).
            go.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            go.transform.localScale = Vector3.one * LensRadius;
            go.AddComponent<MeshFilter>().sharedMesh = _disc;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }

        private static void EnsureAssets()
        {
            if (_disc == null)
            {
                const int segments = 40;
                var verts = new Vector3[segments + 1];
                var uv = new Vector2[segments + 1];
                var tris = new int[segments * 3];
                verts[0] = Vector3.zero;
                uv[0] = new Vector2(0.5f, 0.5f);
                for (int i = 0; i < segments; i++)
                {
                    float a = i / (float)segments * Mathf.PI * 2f;
                    verts[i + 1] = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
                    // Mirror U: the disc is turned around to face the eye.
                    uv[i + 1] = new Vector2(0.5f - 0.5f * Mathf.Cos(a), 0.5f + 0.5f * Mathf.Sin(a));
                    tris[i * 3] = 0;
                    tris[i * 3 + 1] = 1 + (i + 1) % segments;
                    tris[i * 3 + 2] = 1 + i;
                }
                _disc = new Mesh { name = "ScopeDisc", vertices = verts, uv = uv, triangles = tris };
                _disc.RecalculateBounds();
            }
            if (_reticle == null)
            {
                const int size = 256;
                _reticle = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, name = "Reticle" };
                var px = new Color32[size * size];
                int c = size / 2;
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        int dx = Mathf.Abs(x - c), dy = Mathf.Abs(y - c);
                        float r = Mathf.Sqrt(dx * dx + dy * dy) / c;
                        bool thick = (dx <= 3 && dy > 48) || (dy <= 3 && dx > 48);
                        bool thin = (dx <= 0 && dy <= 48) || (dy <= 0 && dx <= 48);
                        bool ring = r > 0.96f;
                        byte a = (byte)(thick || thin ? 235 : ring ? 255 : 0);
                        px[y * size + x] = new Color32(8, 8, 8, a);
                    }
                }
                _reticle.SetPixels32(px);
                _reticle.Apply(false, true);
            }
        }

        public void BeforeRender(bool aiming)
        {
            Transform head = _vrRig.Head;
            Vector3 eye = _rig.ToWorld(_rig.Data.EyePoint);
            float dist = Vector3.Distance(head.position, eye);
            bool active = aiming || dist < 0.22f;
            if (!active)
            {
                _camera.enabled = false;
                _lensMaterial.color = new Color(0.05f, 0.06f, 0.07f, 1f);
                return;
            }
            Camera vr = _vrRig.Camera;
            float baseFov = 60f;
            float magnification = Mathf.Max(1.5f, baseFov / Mathf.Max(1f, _rig.Weapon.GetAimFov()));
            float lensAngle = 2f * Mathf.Atan(LensRadius / EyeRelief) * Mathf.Rad2Deg;
            _camera.fieldOfView = Mathf.Clamp(lensAngle / magnification, 0.5f, 60f);
            _camera.cullingMask = vr.cullingMask & ~IgnoreLayers;
            _camera.clearFlags = vr.clearFlags;
            _camera.backgroundColor = vr.backgroundColor;
            _camera.farClipPlane = vr.farClipPlane;
            _camera.allowHDR = vr.allowHDR;
            _camera.depthTextureMode = vr.depthTextureMode;
            _camera.enabled = true;
            // Eye box: the image darkens as your eye drifts off the sight line.
            float offAxis = Vector3.ProjectOnPlane(head.position - eye, _rig.DirToWorld(_rig.Data.EyeDirection)).magnitude;
            float brightness = Mathf.Clamp01(1f - (offAxis - 0.015f) / 0.04f);
            _lensMaterial.color = new Color(brightness, brightness, brightness, 1f);
        }

        public void Destroy()
        {
            if (_root != null)
            {
                Object.Destroy(_root);
            }
            if (_texture != null)
            {
                _texture.Release();
                Object.Destroy(_texture);
            }
        }
    }
}
