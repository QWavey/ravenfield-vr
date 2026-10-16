using RavenfieldVR.Core;
using RavenfieldVR.Rig;
using UnityEngine;
using UnityEngine.Rendering;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// Picture-in-picture lens for scoped weapons that only had a 2D scope overlay in flat mode. A camera
    /// on the sight line renders the magnified view into a texture shown on a disc at the ocular lens, with a
    /// procedural duplex reticle. It only renders while your eye is near the sight. An eye off the sight line sees a dark
    /// crescent come in from its own side (the scope's shadow, as in a real scope), and the picture dims further out.
    /// </summary>
    public sealed class VRScope
    {
        private const float LensRadius = 0.017f;
        private const float EyeRelief = 0.09f;
        private const int IgnoreLayers = (1 << 5) | (1 << 18);

        private readonly VRRig _vrRig;
        private readonly WeaponRig _rig;
        private readonly float _radius;
        private readonly Vector3 _eyePoint;
        private readonly Vector3 _eyeDirection;
        /// <summary>Field of view of this sight (a magnifier's mode); 0 = whatever the weapon aims with now.</summary>
        private readonly float _fov;
        private bool _shown = true;
        private readonly Camera _camera;
        private readonly RenderTexture _texture;
        private readonly GameObject _root;
        private readonly Material _lensMaterial, _reticleMaterial, _shadowMaterial;
        private static Texture2D _reticle, _shadow;
        private static Mesh _disc;
        /// <summary>The disc's corners in the lens's own frame (x right, y up, in lens radii), read once.</summary>
        private static Vector2[] _discLocal;
        // The shadow disc: the lens's shape, its texture coordinates moved with the eye (it cannot spill past the lens).
        private readonly Mesh _shadowMesh;
        private readonly Vector2[] _shadowUv;
        private Vector2 _shadowShift = new Vector2(float.NaN, 0f);
        // Last values copied from the VR camera onto the scope camera: they are the same on nearly every frame (the player
        // does not change graphics settings mid-aim), so a live scope stops rewriting six native camera properties per frame.
        private float _lastFov = -1f;
        private int _lastCullingMask = int.MinValue;
        private CameraClearFlags _lastClearFlags = (CameraClearFlags)(-1);
        private Color _lastBackground = new Color(float.NaN, 0f, 0f, 0f);
        private float _lastFarClip = -1f;
        private DepthTextureMode _lastDepthMode = (DepthTextureMode)(-1);
        private int _lastHdr = -1;

        /// <summary>Looked up once: three searches by name on every draw of a scoped weapon.</summary>
        private static Shader _spriteShader;
        private static Shader SpriteShader => _spriteShader != null ? _spriteShader : _spriteShader = Shader.Find("Sprites/Default");

        public static bool Wanted(Weapon w, WeaponRig rig)
        {
            if (!rig.Data.HasEyePoint || rig.Data.EyeFromLine || w.renderTextureCamera != null || w.configuration.scopeAimObject == null)
            {
                return false;
            }
            return w.GetAimFov() < 40f;
        }

        public VRScope(VRRig vrRig, WeaponRig rig) : this(vrRig, rig, rig.Data.EyePoint, rig.Data.EyeDirection,
            rig.Data.HasScopeModel ? rig.Data.ScopeOcular - rig.Data.EyeDirection * 0.002f : rig.Data.EyePoint + rig.Data.EyeDirection * EyeRelief,
            rig.Data.HasScopeModel ? rig.Data.ScopeRadius : LensRadius, 0f)
        {
        }

        /// <summary>
        /// A lens for one sight mode (a magnifier flipped in behind a red dot): on that mode's sight line, at eye relief in
        /// front of its eye point, magnifying by that mode's field of view.
        /// </summary>
        public VRScope(VRRig vrRig, WeaponRig rig, SightLine line) : this(vrRig, rig, line.EyePoint, line.EyeDirection,
            line.EyePoint + line.EyeDirection * EyeRelief, LensRadius * 0.85f, line.Fov)
        {
        }

        private VRScope(VRRig vrRig, WeaponRig rig, Vector3 eyePoint, Vector3 eyeDirection, Vector3 lensPos, float radius, float fov)
        {
            _vrRig = vrRig;
            _rig = rig;
            _eyePoint = eyePoint;
            _eyeDirection = eyeDirection;
            _fov = fov;
            int res = VRConfig.ScopeResolution.Value;
            _texture = new RenderTexture(res, res, 24, RenderTextureFormat.Default) { name = "RavenfieldVR Scope", antiAliasing = 2 };
            _texture.Create();

            _root = new GameObject("RavenfieldVR Scope");
            // On the scope's own ocular glass when its model was measured; else eye relief ahead of the eye point.
            // An ocular is 1-2.5 cm across the glass: a measured "lens" much bigger (a K13 ACOG's took the whole housing) was
            // a dark disc as wide as the receiver beside the sight.
            _radius = Mathf.Clamp(radius, 0.008f, 0.025f);
            rig.AttachInGunSpace(_root.transform, lensPos, Quaternion.LookRotation(eyeDirection, rig.Data.Up));

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
            _lensMaterial = new Material(SpriteShader) { mainTexture = _texture, renderQueue = 3000 };
            CreateDisc("Lens", _lensMaterial, 0f);
            _reticleMaterial = new Material(SpriteShader) { mainTexture = _reticle, renderQueue = 3001 };
            CreateDisc("Reticle", _reticleMaterial, -0.0008f);
            _shadowMesh = Object.Instantiate(_disc);
            _shadowMesh.name = "ScopeShadow";
            _shadowMesh.MarkDynamic();
            _shadowUv = _shadowMesh.uv;
            _shadowMaterial = new Material(SpriteShader) { mainTexture = _shadow, renderQueue = 3002 };
            CreateDisc("Shadow", _shadowMaterial, -0.0012f, _shadowMesh);
            SetShadow(Vector2.zero);
        }

        /// <summary>
        /// Puts the clear part of the view <paramref name="shift"/> lens radii off the middle (opposite the eye's offset,
        /// as through a tube): the dark rest comes in from the eye's side.
        /// </summary>
        private void SetShadow(Vector2 shift)
        {
            if ((shift - _shadowShift).sqrMagnitude < 1e-6f)
            {
                return;
            }
            _shadowShift = shift;
            for (int i = 0; i < _shadowUv.Length; i++)
            {
                _shadowUv[i] = new Vector2(0.5f, 0.5f) + 0.5f * (_discLocal[i] - shift);
            }
            _shadowMesh.uv = _shadowUv;
        }

        private void CreateDisc(string name, Material mat, float z, Mesh mesh = null)
        {
            var go = new GameObject(name);
            go.layer = 18;
            go.transform.SetParent(_root.transform, false);
            go.transform.localPosition = new Vector3(0f, 0f, z);
            // The disc faces back toward the eye (-Z of the sight line).
            go.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            go.transform.localScale = Vector3.one * _radius;
            go.AddComponent<MeshFilter>().sharedMesh = mesh ?? _disc;
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
                // The disc is turned to face the eye: its local x runs the other way to the lens's.
                _discLocal = new Vector2[verts.Length];
                for (int i = 0; i < verts.Length; i++)
                {
                    _discLocal[i] = new Vector2(-verts[i].x, verts[i].y);
                }
            }
            if (_reticle == null)
            {
                // Duplex: thick posts from the edge, a thin cross in the middle, a dark ring at the rim. Drawn with soft
                // (coverage) edges and mipmapped: the lens shows it about 1.5x smaller than drawn, and hard one-texel lines
                // without mipmaps flickered between faint and dark as the gun moved (simulated: 0.65 of full brightness
                // at 1.5x; now 0.07).
                const int size = 256;
                _reticle = new Texture2D(size, size, TextureFormat.RGBA32, true)
                {
                    wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, name = "Reticle"
                };
                var px = new Color32[size * size];
                float c = size * 0.5f;
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        float ax = Mathf.Abs(x + 0.5f - c), ay = Mathf.Abs(y + 0.5f - c);
                        float r = Mathf.Sqrt(ax * ax + ay * ay);
                        float inner = 48f;
                        float posts = Mathf.Max(Cover(ax, 3.5f) * Mathf.Clamp01(ay - inner + 0.5f), Cover(ay, 3.5f) * Mathf.Clamp01(ax - inner + 0.5f));
                        float cross = Mathf.Max(Cover(ax, 0.8f) * Mathf.Clamp01(inner + 0.5f - ay), Cover(ay, 0.8f) * Mathf.Clamp01(inner + 0.5f - ax));
                        float ring = Mathf.Clamp01(r - 0.96f * c + 0.5f);
                        float a = Mathf.Max(Mathf.Max(posts, cross) * 0.92f, ring);
                        px[y * size + x] = new Color32(8, 8, 8, (byte)(255f * a));
                    }
                }
                _reticle.SetPixels32(px);
                _reticle.Apply(true, true);
            }
            if (_shadow == null)
            {
                // Clear in the middle, a soft dark rim (the outer twentieth of the ocular), dark all round outside: moved off the
                // middle, the rim becomes a crescent, and past the texture's edge it stays dark.
                const int size = 128;
                _shadow = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, name = "ScopeShadow" };
                var px = new Color32[size * size];
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        float u = (x + 0.5f) / size - 0.5f, v = (y + 0.5f) / size - 0.5f;
                        float a = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.455f, 0.49f, Mathf.Sqrt(u * u + v * v)));
                        px[y * size + x] = new Color32(0, 0, 0, (byte)(255f * a));
                    }
                }
                _shadow.SetPixels32(px);
                _shadow.Apply(false, true);
            }
        }

        /// <summary>How much of a texel a line of half-width <paramref name="half"/> covers at <paramref name="dist"/> texels from its middle.</summary>
        private static float Cover(float dist, float half) => Mathf.Clamp01(half + 0.5f - dist);

        /// <summary>Shows or hides the lens (a magnifier's lens only while its sight mode is selected).</summary>
        public void SetShown(bool shown)
        {
            if (_shown == shown)
            {
                return;
            }
            _shown = shown;
            _root.SetActive(shown);
            if (!shown)
            {
                _camera.enabled = false;
            }
        }

        public void BeforeRender(bool aiming)
        {
            if (!_shown)
            {
                return;
            }
            Transform head = _vrRig.Head;
            Vector3 eye = _rig.ToWorld(_eyePoint);
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
            float magnification = Mathf.Max(1.5f, baseFov / Mathf.Max(1f, _fov > 0.5f ? _fov : _rig.Weapon.GetAimFov()));
            float lensAngle = 2f * Mathf.Atan(_radius / EyeRelief) * Mathf.Rad2Deg;
            float fov = Mathf.Clamp(lensAngle / magnification, 0.5f, 60f);
            if (!Mathf.Approximately(fov, _lastFov))
            {
                _lastFov = fov;
                _camera.fieldOfView = fov;
            }
            int mask = vr.cullingMask & ~IgnoreLayers;
            if (mask != _lastCullingMask)
            {
                _lastCullingMask = mask;
                _camera.cullingMask = mask;
            }
            if (vr.clearFlags != _lastClearFlags)
            {
                _lastClearFlags = vr.clearFlags;
                _camera.clearFlags = vr.clearFlags;
            }
            if (vr.backgroundColor != _lastBackground)
            {
                _lastBackground = vr.backgroundColor;
                _camera.backgroundColor = vr.backgroundColor;
            }
            if (!Mathf.Approximately(vr.farClipPlane, _lastFarClip))
            {
                _lastFarClip = vr.farClipPlane;
                _camera.farClipPlane = vr.farClipPlane;
            }
            int hdr = vr.allowHDR ? 1 : 0;
            if (hdr != _lastHdr)
            {
                _lastHdr = hdr;
                _camera.allowHDR = vr.allowHDR;
            }
            if (vr.depthTextureMode != _lastDepthMode)
            {
                _lastDepthMode = vr.depthTextureMode;
                _camera.depthTextureMode = vr.depthTextureMode;
            }
            // Eye box. The eye is the aiming one, the one nearer the line: measured from the middle of the head, a scope you
            // looked through perfectly with one eye was already 3.2 cm "off" and showed only 57% bright. Off the line, the
            // scope's shadow comes in from the eye's side (a sliver from about 1.5 cm, half the view at about 4 cm, all of it by about 6.5 cm), and the
            // picture dims as well past 3 cm.
            Vector3 axis = _rig.DirToWorld(_eyeDirection);
            Vector3 lateral = Vector3.ProjectOnPlane(AimingEye(head, eye, axis, vr) - eye, axis);
            float offAxis = lateral.magnitude;
            Transform lens = _root.transform;
            var side = new Vector2(Vector3.Dot(lateral, lens.right), Vector3.Dot(lateral, lens.up));
            float push = Mathf.Max(0f, offAxis - 0.008f) / 0.03f;
            SetShadow(offAxis > 1e-5f ? -side / offAxis * push : Vector2.zero);
            float brightness = Mathf.Clamp01(1f - (offAxis - 0.03f) / 0.04f);
            // Black through the lens (the eye 7 cm off its line): no second picture of the whole scene for it.
            _camera.enabled = brightness > 0.004f;
            _lensMaterial.color = new Color(brightness, brightness, brightness, 1f);
        }

        /// <summary>The eye nearer the sight line through <paramref name="eyePoint"/> (the headset's own eye distance, 64 mm if unknown).</summary>
        internal static Vector3 AimingEye(Transform head, Vector3 eyePoint, Vector3 axis, Camera vr)
        {
            float ipd = vr != null && vr.stereoSeparation > 0.04f && vr.stereoSeparation < 0.09f ? vr.stereoSeparation : 0.064f;
            Vector3 side = head.right * (ipd * 0.5f);
            Vector3 left = head.position - side, right = head.position + side;
            return Vector3.ProjectOnPlane(left - eyePoint, axis).sqrMagnitude <= Vector3.ProjectOnPlane(right - eyePoint, axis).sqrMagnitude ? left : right;
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
            // Made for this lens: they went on living after it (two per draw of a scoped gun, and per magnifier lens).
            if (_lensMaterial != null)
            {
                Object.Destroy(_lensMaterial);
            }
            if (_reticleMaterial != null)
            {
                Object.Destroy(_reticleMaterial);
            }
            if (_shadowMaterial != null)
            {
                Object.Destroy(_shadowMaterial);
            }
            if (_shadowMesh != null)
            {
                Object.Destroy(_shadowMesh);
            }
        }
    }
}
