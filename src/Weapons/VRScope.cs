using System.Collections.Generic;
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
        private RenderTexture _texture;
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
            return w.GetAimFov() < 40f && !HasOwnReticle(w, rig);
        }

        /// <summary>
        /// The flat game's 2D scope picture as words, for the log: each image in it (its name, its sprite or texture and size, its share of the
        /// screen, whether its texture is readable). A gun's own reticle is drawn there; the mod's lens draws a duplex of its own instead (user,
        /// 2026-11-08: "some guns still get a vanilla scope over tehir real one"). Read to see whether those pictures can be put on the lens.
        /// </summary>
        internal static string DescribeOverlay(Weapon w)
        {
            GameObject o = w != null && w.configuration != null ? w.configuration.scopeAimObject : null;
            if (o == null)
            {
                return "none";
            }
            var parts = new List<string>();
            foreach (UnityEngine.UI.Graphic g in o.GetComponentsInChildren<UnityEngine.UI.Graphic>(true))
            {
                Texture tex = g.mainTexture;
                string what = g is UnityEngine.UI.Image img && img.sprite != null ? $"sprite '{img.sprite.name}' {img.sprite.rect.width:0}x{img.sprite.rect.height:0}"
                    : tex != null ? $"texture '{tex.name}' {tex.width}x{tex.height}" : "no texture";
                Rect r = g.rectTransform.rect;
                bool readable = tex is Texture2D t2 && t2.isReadable;
                parts.Add($"{g.GetType().Name} '{g.name}' {what}, {r.width:0}x{r.height:0} units, alpha {g.color.a:0.00}{(readable ? ", readable" : "")}");
                if (parts.Count >= 6)
                {
                    break;
                }
            }
            // (The vanilla sniper's and RFB's picture is no UI image but a mesh before the camera, its texture the scope as drawn: named too.)
            foreach (Renderer r in o.GetComponentsInChildren<Renderer>(true))
            {
                Material m = r.sharedMaterial;
                Texture tex = m != null ? m.mainTexture : null;
                Vector3 size = r is MeshRenderer && r.GetComponent<MeshFilter>()?.sharedMesh is Mesh mesh ? Vector3.Scale(mesh.bounds.size, r.transform.lossyScale) : r.bounds.size;
                parts.Add($"{r.GetType().Name} '{r.name}' material '{(m != null ? m.name : "none")}' shader '{(m != null && m.shader != null ? m.shader.name : "none")}' "
                          + $"texture '{(tex != null ? tex.name : "none")}' {(tex != null ? $"{tex.width}x{tex.height}" : "")}{(tex is Texture2D t2r && t2r.isReadable ? " readable" : "")}, "
                          + $"{size.x:0.###} x {size.y:0.###} x {size.z:0.###} m, {(r.transform.localPosition.magnitude):0.###} m from its holder");
                if (parts.Count >= 8)
                {
                    break;
                }
            }
            return parts.Count == 0 ? "no image in it" : string.Join("; ", parts.ToArray());
        }

        /// <summary>A gun's own reticle found in its 2D scope picture: the texture, drawn added (glowing) or blended; null when it has none.</summary>
        private sealed class Picture
        {
            public Texture Texture;
            public bool Additive;
            public Vector2 Scale = Vector2.one, Offset;
        }

        private static readonly Dictionary<string, Picture> Pictures = new Dictionary<string, Picture>();
        private static Shader _additiveShader;

        /// <summary>
        /// The lens's reticle from the gun's own flat 2D scope picture, where that picture is a reticle drawn on a clear middle (a texture
        /// such as the game's acog_sight or dmr_sight: lines on a transparent texture; mods' pictures). The lens drew the mod's duplex on every
        /// gun; the flat game shows each its own. Not a picture with no texture (the vanilla SL-DEFENDER's and RECON LRR's are dark meshes,
        /// 'Dark Scope'), a vignette, or one that covers the middle of the view (read once on the GPU: a picture of a whole scope would cover
        /// the lens's view): those keep the duplex.
        /// </summary>
        private static Material OwnPictureMaterial(WeaponRig rig)
        {
            Weapon w = rig != null ? rig.Weapon : null;
            Picture pic = PictureFor(w, rig != null && rig.Data.Key != null ? rig.Data.Key : w != null ? w.name : null);
            if (pic == null || pic.Texture == null)
            {
                return null;
            }
            Shader shader = SpriteShader;
            if (pic.Additive)
            {
                if (_additiveShader == null)
                {
                    _additiveShader = Shader.Find("Legacy Shaders/Particles/Additive");
                }
                shader = _additiveShader != null ? _additiveShader : SpriteShader;
            }
            var m = new Material(shader) { mainTexture = pic.Texture, renderQueue = 3001 };
            m.mainTextureScale = pic.Scale;
            m.mainTextureOffset = pic.Offset;
            return m;
        }

        /// <summary>
        /// The weapon warm-up's share: a scoped gun's picture looked at in the menu, so its GPU read (a few ms, once) is not paid on its first
        /// scoped draw in a match.
        /// </summary>
        internal static void WarmPicture(Weapon w, WeaponRigData data)
        {
            if (w != null && data != null && w.configuration != null && w.configuration.scopeAimObject != null && w.GetAimFov() < 40f)
            {
                PictureFor(w, data.Key ?? w.name);
            }
        }

        /// <summary>The picture for a gun, looked for once and kept by its key (null: it has none to use).</summary>
        private static Picture PictureFor(Weapon w, string key)
        {
            if (key == null)
            {
                return null;
            }
            if (!Pictures.TryGetValue(key, out Picture pic))
            {
                string why;
                try
                {
                    pic = FindPicture(w, out why);
                }
                catch (System.Exception e)
                {
                    pic = null;
                    why = $"its picture could not be read ({e.GetType().Name}: {e.Message})";
                }
                Pictures[key] = pic;
                Log.Info(pic != null
                    ? $"{key}: the lens draws the gun's own reticle, '{pic.Texture.name}' from its 2D scope picture{(pic.Additive ? " (glowing)" : "")}."
                    : $"{key}: the lens draws the mod's duplex reticle ({why}).");
            }
            return pic;
        }

        private static readonly System.Text.RegularExpressions.Regex NotAReticle =
            new System.Text.RegularExpressions.Regex(@"vignette|unitywhite|font|white|black|noise|lock|target", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>The first reticle texture among the picture's renderers and images, vetted by <see cref="ClearInTheMiddle"/>; why not, else.</summary>
        private static Picture FindPicture(Weapon w, out string why)
        {
            GameObject o = w != null && w.configuration != null ? w.configuration.scopeAimObject : null;
            why = "no 2D scope picture";
            if (o == null)
            {
                return null;
            }
            why = "no texture in its 2D scope picture";
            var found = new List<Picture>();
            foreach (Renderer r in o.GetComponentsInChildren<Renderer>(true))
            {
                Material m = r is ParticleSystemRenderer ? null : r.sharedMaterial;
                Texture t = m != null && m.HasProperty("_MainTex") ? m.mainTexture : null;
                if (t != null)
                {
                    string shader = m.shader != null ? m.shader.name : "";
                    found.Add(new Picture
                    {
                        Texture = t, Additive = shader.IndexOf("Additive", System.StringComparison.OrdinalIgnoreCase) >= 0,
                        Scale = m.mainTextureScale, Offset = m.mainTextureOffset
                    });
                }
            }
            foreach (UnityEngine.UI.Graphic g in o.GetComponentsInChildren<UnityEngine.UI.Graphic>(true))
            {
                if (g is UnityEngine.UI.RawImage raw && raw.texture != null)
                {
                    found.Add(new Picture { Texture = raw.texture, Scale = raw.uvRect.size, Offset = raw.uvRect.position });
                }
                else if (g is UnityEngine.UI.Image img && img.sprite != null && img.sprite.texture != null)
                {
                    Rect tr = img.sprite.textureRect;
                    Texture2D st = img.sprite.texture;
                    found.Add(new Picture
                    {
                        Texture = st, Scale = new Vector2(tr.width / st.width, tr.height / st.height), Offset = new Vector2(tr.x / st.width, tr.y / st.height)
                    });
                }
            }
            foreach (Picture p in found)
            {
                if (NotAReticle.IsMatch(p.Texture.name))
                {
                    why = $"'{p.Texture.name}' is no reticle";
                    continue;
                }
                if (p.Texture is RenderTexture)
                {
                    // (A mod's own scope camera shown in its 2D picture: read back, it is whatever that camera last drew, not a reticle.)
                    why = $"'{p.Texture.name}' is a camera's picture";
                    continue;
                }
                if (!ClearInTheMiddle(p, out why))
                {
                    continue;
                }
                return p;
            }
            return null;
        }

        /// <summary>
        /// The picture drawn small on the GPU and read back once: a reticle covers little of the middle of the view (its lines), a picture of
        /// a whole scope or a coloured screen covers it all. Blended pictures by their alpha, glowing (added) ones by their brightness.
        /// </summary>
        private static bool ClearInTheMiddle(Picture p, out string why)
        {
            const int n = 32;
            RenderTexture rt = RenderTexture.GetTemporary(n, n, 0, RenderTextureFormat.ARGB32);
            RenderTexture before = RenderTexture.active;
            var read = new Texture2D(n, n, TextureFormat.RGBA32, false);
            try
            {
                Graphics.Blit(p.Texture, rt, p.Scale, p.Offset);
                RenderTexture.active = rt;
                read.ReadPixels(new Rect(0, 0, n, n), 0, 0, false);
                read.Apply(false);
                Color32[] px = read.GetPixels32();
                float inner = 0f, all = 0f;
                int innerCount = 0;
                for (int y = 0; y < n; y++)
                {
                    for (int x = 0; x < n; x++)
                    {
                        Color32 c = px[y * n + x];
                        float cover = p.Additive ? (c.r + c.g + c.b) / (3f * 255f) : c.a / 255f;
                        all += cover;
                        float u = (x + 0.5f) / n - 0.5f, v = (y + 0.5f) / n - 0.5f;
                        if (u * u + v * v < 0.35f * 0.35f)
                        {
                            inner += cover;
                            innerCount++;
                        }
                    }
                }
                inner /= Mathf.Max(1, innerCount);
                all /= n * n;
                if (inner > 0.35f)
                {
                    why = $"'{p.Texture.name}' covers {inner:P0} of the middle: a picture, not a reticle";
                    return false;
                }
                if (all < 0.002f)
                {
                    why = $"'{p.Texture.name}' is empty";
                    return false;
                }
                why = null;
                return true;
            }
            finally
            {
                RenderTexture.active = before;
                RenderTexture.ReleaseTemporary(rt);
                Object.Destroy(read);
            }
        }

        /// <summary>A reticle the model draws itself: a material named as one (the mods' "reticle_ui_stencil" on a scope's glass).</summary>
        private static readonly System.Text.RegularExpressions.Regex ReticleName =
            new System.Text.RegularExpressions.Regex(@"retic|recticle|crosshair|cross_hair", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        private static readonly Dictionary<string, bool> OwnReticle = new Dictionary<string, bool>();

        /// <summary>
        /// The model's scope draws a reticle of its own on its glass: the mod's lens is not put over it. It was, with its duplex crosshair, the
        /// flat game's 2D scope as it looks, in front of the real one (user, 2026-11-08: "some guns still get a vanilla scope over tehir real one";
        /// the mk18 ACOG, mg3-acog, MRAD and AK-105 have such a material in the export). Without the lens the scope is seen as the model is, its
        /// reticle on the glass, unmagnified.
        /// </summary>
        internal static bool HasOwnReticle(Weapon w, WeaponRig rig)
        {
            string key = rig.Data.Key ?? w.name;
            if (OwnReticle.TryGetValue(key, out bool known))
            {
                return known;
            }
            string found = null;
            foreach (Renderer r in w.GetComponentsInChildren<Renderer>(true))
            {
                if (r is ParticleSystemRenderer)
                {
                    continue;
                }
                foreach (Material m in r.sharedMaterials)
                {
                    if (m != null && ReticleName.IsMatch(m.name))
                    {
                        found = r.name + " (" + m.name + ")";
                        break;
                    }
                }
                if (found != null)
                {
                    break;
                }
            }
            OwnReticle[key] = found != null;
            if (found != null)
            {
                Log.Info($"{key}: its scope draws its own reticle ({found}): no lens of the mod's over it.");
            }
            return found != null;
        }

        public VRScope(VRRig vrRig, WeaponRig rig) : this(vrRig, rig, rig.Data.EyePoint, rig.Data.EyeDirection,
            rig.Data.HasScopeModel ? rig.Data.ScopeOcular - rig.Data.EyeDirection * 0.002f : rig.Data.EyePoint + rig.Data.EyeDirection * EyeRelief,
            rig.Data.HasScopeModel ? rig.Data.ScopeRadius : LensRadius, 0f)
        {
        }

        /// <summary>
        /// A lens for one sight mode (a magnifier flipped in behind a red dot): on that mode's sight line, at eye relief in
        /// front of its eye point, magnifying by that mode's field of view. When that line runs through the model's own scope
        /// (a scope measured in the model, its ocular on the line), on that scope's ocular glass as the scope's own lens is: at eye
        /// relief it floated in front of the scope, a second sight before the one on the gun (user, 2026-11-03: "On the M417A2
        /// theres a Vanilla scope placed infront the original scope").
        /// </summary>
        public VRScope(VRRig vrRig, WeaponRig rig, SightLine line) : this(vrRig, rig, line.EyePoint, line.EyeDirection,
            OnOcular(rig, line) ? rig.Data.ScopeOcular - line.EyeDirection * 0.002f : line.EyePoint + line.EyeDirection * EyeRelief,
            OnOcular(rig, line) ? rig.Data.ScopeRadius : LensRadius * 0.85f, line.Fov)
        {
        }

        /// <summary>Whether a sight mode's line runs through the model's own scope: its ocular ahead of the eye, within its glass of the line.</summary>
        private static bool OnOcular(WeaponRig rig, SightLine line)
        {
            WeaponRigData d = rig.Data;
            if (!d.HasScopeModel || line == null)
            {
                return false;
            }
            Vector3 off = d.ScopeOcular - line.EyePoint;
            return Vector3.Dot(off, line.EyeDirection) > 0f
                   && Vector3.ProjectOnPlane(off, line.EyeDirection).magnitude < Mathf.Max(0.015f, d.ScopeRadius);
        }

        private VRScope(VRRig vrRig, WeaponRig rig, Vector3 eyePoint, Vector3 eyeDirection, Vector3 lensPos, float radius, float fov)
        {
            _vrRig = vrRig;
            _rig = rig;
            _eyePoint = eyePoint;
            _eyeDirection = eyeDirection;
            _fov = fov;
            // Whatever it made given back if a step throws part way (the draw goes on without a lens: its picture, its object and
            // its materials were left behind, a render texture each time).
            try
            {
                _texture = TakeTexture(VRConfig.ScopeResolution.Value);

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
                _noShadows = camGo.AddComponent<ScopeNoShadows>();
                _noShadows.enabled = false;
                _camera.stereoTargetEye = StereoTargetEyeMask.None;
                _camera.nearClipPlane = 0.05f;
                _camera.enabled = false;
                vrRig.Suppressor.Ignore(_camera);

                EnsureAssets();
                _lensMaterial = new Material(SpriteShader) { mainTexture = _texture, renderQueue = 3000 };
                CreateDisc("Lens", _lensMaterial, 0f);
                _reticleMaterial = (fov <= 0f ? OwnPictureMaterial(rig) : null) ?? new Material(SpriteShader) { mainTexture = _reticle, renderQueue = 3001 };
                CreateDisc("Reticle", _reticleMaterial, -0.0008f);
                _shadowMesh = Object.Instantiate(_disc);
                _shadowMesh.name = "ScopeShadow";
                _shadowMesh.MarkDynamic();
                _shadowUv = _shadowMesh.uv;
                _shadowMaterial = new Material(SpriteShader) { mainTexture = _shadow, renderQueue = 3002 };
                CreateDisc("Shadow", _shadowMaterial, -0.0012f, _shadowMesh);
                SetShadow(Vector2.zero);
            }
            catch
            {
                Destroy();
                throw;
            }
        }

        /// <summary>
        /// Puts the clear part of the view <paramref name="shift"/> lens radii off the middle (BeforeRender puts it opposite the
        /// eye's offset): the dark rest comes in from the eye's side.
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

        /// <summary>
        /// The lens's disc, reticle and shadow made ahead by the weapon warm-up: drawn texel by texel on the first scoped draw
        /// in a match, they came with its one-off hitch ("draw: scope" 37 ms at most against 0.5 ms on average, 2026-10-25).
        /// </summary>
        internal static void WarmAssets() => EnsureAssets();

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
            if (_shown == shown || _root == null) // (destroyed: nothing to show; a destroyed lens drawn threw every frame)
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
            if (!_shown || _root == null)
            {
                return;
            }
            Transform head = _vrRig.Head;
            Vector3 eye = _rig.ToWorld(_eyePoint);
            float dist = Vector3.Distance(head.position, eye);
            // (Near the head and pointing along the look: a gun held close at a reload rendered the whole scene a second time for nobody.)
            bool active = aiming || dist < 0.22f && Vector3.Dot(head.forward, _rig.AimWorld) > 0.6f;
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
            // The clear part opposite the eye's offset, the shadow coming in from the eye's own side. Behind the exit pupil (the
            // usual place in a headset) each field angle's bundle has crossed the axis: rays from the right of the view travel
            // leftward, so an eye moved left catches the right of the field and loses its left. The shadow then says to move away
            // from it. (21ea236 drew it the other way round on a reversed reasoning; that is an eye in front of the exit pupil.
            // Re-derived in the rig hunt, 2026-10-20 night.)
            SetShadow(offAxis > 1e-5f ? -side / offAxis * push : Vector2.zero);
            float brightness = Mathf.Clamp01(1f - (offAxis - 0.03f) / 0.04f);
            // Black through the lens (the eye 7 cm off its line): no second picture of the whole scene for it.
            bool render = brightness > 0.004f;
            if (BenchMode == 1)
            {
                render = false;
            }
            else if (BenchMode == 2 || BenchMode < 0 && VRConfig.ScopeHalfRate.Value)
            {
                render &= (Time.frameCount & 1) == 0; // the last picture stays on the lens in between
            }
            _camera.enabled = render;
            if (render)
            {
                LensDrawnAt = Time.unscaledTime;
            }
            // Without shadows unless asked for (the second set of shadow cascades was most of the lens's cost: 14.2 ms frames
            // aimed through the RECON LRR's lens, 11.3 without its shadows, 10.8 with no lens at all; 255.P1.2). The bench
            // measures its own modes.
            bool noShadows = BenchMode == 3 ? false : !VRConfig.ScopeShadows.Value;
            if (_noShadows != null && _noShadows.enabled != noShadows)
            {
                _noShadows.enabled = noShadows;
            }
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

        /// <summary>
        /// When a lens last drew its picture, the mod's or the game's own scope camera (Time.unscaledTime): a magnified view is on
        /// (TinyObjects draws everything then).
        /// </summary>
        internal static float LensDrawnAt = -10f;

        /// <summary>
        /// Autotest bench of the lens's cost (255.P1.2): 0 as set (shadows as ScopeShadows says, every frame whatever ScopeHalfRate
        /// says), 1 its camera off, 2 rendered every other frame, 3 with shadows (a camera of its own draws the shadow cascades again);
        /// -1 not benching (the config's).
        /// </summary>
        internal static int BenchMode = -1;

        private ScopeNoShadows _noShadows;

        /// <summary>
        /// On the lens camera and the mod's UI camera: no shadows while it renders (the shadow distance is global; put back after).
        /// Taken away before the camera culls, not only before it draws: from OnPreRender the shadow maps were left out but the
        /// level's shadow casters were still culled for it, a whole set per render (per eye for the UI camera) with a preset's
        /// shadows on; the suppressed game cameras' come off at their pre-cull too (CameraSuppressor).
        /// </summary>
        internal sealed class ScopeNoShadows : MonoBehaviour
        {
            private float _distance;
            private bool _held;

            private void OnPreCull() => Hold();

            // And before each render: a stereo camera's second eye, after the first eye's post-render gave the distance back, drew
            // with shadows if its cull was the first eye's alone (review).
            private void OnPreRender() => Hold();

            private void Hold()
            {
                if (!_held)
                {
                    _distance = QualitySettings.shadowDistance; // (once: a render whose post-render never came would save its own 0)
                    _held = true;
                }
                QualitySettings.shadowDistance = 0f;
            }

            private void OnPostRender()
            {
                if (_held)
                {
                    QualitySettings.shadowDistance = _distance;
                    _held = false;
                }
            }

            private void OnDisable() => OnPostRender(); // (switched off between its cull and its post-render: the distance given back)
        }

        /// <summary>
        /// Lens pictures kept for the next scope (a few: a scope, a flipped-in magnifier, a second weapon's): one was made and
        /// released on every draw of a scoped gun, a 768 x 768 picture with its depth and two samples (255.P1.4).
        /// </summary>
        private static readonly List<RenderTexture> SpareTextures = new List<RenderTexture>();

        private static RenderTexture TakeTexture(int res)
        {
            for (int i = SpareTextures.Count - 1; i >= 0; i--)
            {
                RenderTexture t = SpareTextures[i];
                SpareTextures.RemoveAt(i);
                if (t != null && t.width == res && t.height == res)
                {
                    if (!t.IsCreated())
                    {
                        t.Create();
                    }
                    return t;
                }
                if (t != null)
                {
                    t.Release();
                    Object.Destroy(t);
                }
            }
            var made = new RenderTexture(res, res, 24, RenderTextureFormat.Default) { name = "RavenfieldVR Scope", antiAliasing = 2 };
            made.Create();
            return made;
        }

        /// <summary>
        /// VR stopped: the kept lens pictures go too (up to three of about 9 MB at the default size, 200 MB at the largest, kept
        /// for the rest of the game; render-effects scout, 2026-10-23).
        /// </summary>
        internal static void ReleaseSpares()
        {
            foreach (RenderTexture t in SpareTextures)
            {
                if (t != null)
                {
                    t.Release();
                    Object.Destroy(t);
                }
            }
            SpareTextures.Clear();
        }

        private static void GiveBackTexture(RenderTexture t)
        {
            if (SpareTextures.Count < 3)
            {
                SpareTextures.Add(t);
                return;
            }
            t.Release();
            Object.Destroy(t);
        }

        public void Destroy()
        {
            if (_root != null)
            {
                Object.Destroy(_root);
            }
            if (_texture != null)
            {
                // The camera goes with its object only at the end of the frame: off this picture first, which the next
                // scope may take this frame.
                if (_camera != null)
                {
                    _camera.enabled = false;
                    _camera.targetTexture = null;
                }
                GiveBackTexture(_texture);
                _texture = null;
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
