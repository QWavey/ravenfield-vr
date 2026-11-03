using System.Collections.Generic;
using RavenfieldVR.Core;
using RavenfieldVR.Rig;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// The ammo pouch made visible: a copy of what the held weapon feeds from (its own magazine model, a shell, a
    /// rocket) sits at the pouch on your body with the number of spares next to it, and lifts a little when your
    /// free hand is close enough to take one. Whatever the magazine, its top (the end you grab) stands at the same
    /// spot just above the pouch point, where the grab is answered; longer magazines reach further down.
    /// </summary>
    internal sealed class PouchDisplay
    {
        private const float MaxSize = 0.26f;
        /// <summary>How far above the pouch point the top of the shown magazine stands.</summary>
        private const float TopAbovePouch = 0.025f;

        private readonly WeaponSystem _sys;
        private GameObject _root;
        private GameObject _model;
        private TextMesh _label;
        private WeaponRigData _builtFor;
        private PropKind _builtKind;
        private Vector3 _insertLocal, _frontLocal;
        private Bounds _bounds;
        private float _scale = 1f;
        private bool _reachingLast;
        private float _hover;

        public PouchDisplay(WeaponSystem sys)
        {
            _sys = sys;
        }

        public void BeforeRender(HeldWeapon held, Weapon away)
        {
            AmmoState ammo = held != null ? held.ActiveAmmo : null;
            WeaponRig rig = held != null ? held.ActiveRig : null;
            if (held == null && HandLoading.CarriedStill(away))
            {
                // Loading by hand the gun is put away: the pouch still shows where the magazines and rounds are, its last gun's.
                ammo = _sys.StateFor(away, create: false);
                rig = HandLoading.Applies(ammo) ? _sys.RigFor(away) : null;
            }
            bool show = VRConfig.ShowPouch.Value && VRConfig.ReloadNow == ReloadMode.Manual && rig != null && ammo != null && ammo.Participates
                        && rig.Data.Action != ActionType.Throwable && GameContextOnFoot() && !Util.GameContext.GameMenuOpen;
            if (!show)
            {
                SetVisible(false);
                return;
            }
            PropKind kind = ammo.UsesDetachableMag ? PropKind.Magazine : rig.Data.Action == ActionType.SingleShot ? PropKind.Rocket : PropKind.Shell;
            // Loaded by hand with the reserve out, the pouch hands out empty magazines: it shows an empty one (it showed a full one).
            bool empty = kind == PropKind.Magazine && HandLoading.Applies(ammo) && !ammo.InfiniteSpare && ammo.SpareAmmo <= 0;
            // Built again once the gun's magazine is copied as it looks (loaded, or empty for that): built at the draw, before the gun
            // has settled (HeldWeapon.SettledLoaded), it was a copy of the magazine as it showed then, rounds off on a mod that animates
            // them on.
            bool kept = kind == PropKind.Magazine && (empty ? MagazineProp.KeepsEmptyLook(rig.Data.Key) : MagazineProp.KeepsLoadedLook(rig.Data.Key));
            if (_builtFor != rig.Data || _builtKind != kind || _model == null || empty != _builtEmpty || kept && !_builtLoaded)
            {
                Build(rig, kind, kept, empty);
                _builtLoaded = kept;
                _builtEmpty = empty;
                _builtFor = rig.Data;
                _builtKind = kind;
            }
            SetVisible(true);

            BodyZones zones = _sys.Zones;
            VRHand off = _sys.Rig.Off;
            bool reaching = off.Tracked && off.Role == HandRole.Free && zones.InPouch(off.Palm.position);
            if (held == null && !reaching)
            {
                // (With the gun away the other hand reaches it as well.)
                off = _sys.Rig.Dominant;
                reaching = off.Tracked && off.Role == HandRole.Free && zones.InPouch(off.Palm.position);
            }
            if (reaching && !_reachingLast)
            {
                off.Haptic(0.012f, 220f, 0.3f); // a tick on reaching it, as a holster gives: the pouch is found by feel too (sweep)
            }
            _reachingLast = reaching;
            _hover = Mathf.MoveTowards(_hover, reaching ? 1f : 0f, Time.unscaledDeltaTime * 8f);

            // Standing up in the pouch, facing forward with the body.
            Quaternion propBasis = Quaternion.LookRotation(Vector3.ProjectOnPlane(_frontLocal, _insertLocal).sqrMagnitude > 1e-4f ? _frontLocal : Vector3.forward, _insertLocal);
            Quaternion worldBasis = Quaternion.LookRotation(zones.TorsoForward, Vector3.up);
            Quaternion rot = worldBasis * Runtime.RigidPose.Conjugate(propBasis);
            float scale = _scale * (1f + 0.06f * _hover);
            Vector3 lift = Vector3.up * (0.015f * _hover);
            Transform t = _model.transform;
            t.localScale = Vector3.one * scale;
            t.rotation = rot;
            float along = Mathf.Abs(Vector3.Dot(_bounds.extents, Abs(_insertLocal.normalized)));
            Vector3 topLocal = _bounds.center + _insertLocal.normalized * along;
            Vector3 top = zones.Pouch + lift + Vector3.up * TopAbovePouch;
            t.position = top - rot * (topLocal * scale);
            GrabPoint = top - Vector3.up * Mathf.Min(0.03f, along * scale);

            int spare = ammo.InfiniteSpare ? -1 : ammo.SpareAmmo;
            // Loaded by hand with the reserve out, the pouch still hands out empty magazines and rounds without end: said so.
            bool byHand = HandLoading.Applies(ammo);
            if (spare != _shownSpare || ammo.Capacity != _shownCapacity || kind != _shownKind || byHand != _shownByHand)
            {
                _shownSpare = spare;
                _shownCapacity = ammo.Capacity;
                _shownKind = kind; // a magazine at 30/30 reads "1", a shell gun at 30/30 "30"
                _shownByHand = byHand;
                _label.text = byHand && spare == 0 ? "0 \u00b7 \u221e rds" : PouchText(spare, ammo.Capacity, kind);
            }
            Color color = spare == 0 ? new Color(0.85f, 0.35f, 0.3f) : Color.Lerp(new Color(0.85f, 0.86f, 0.82f, 0.85f), new Color32(201, 160, 74, 255), _hover);
            if (color != _label.color)
            {
                _label.color = color; // only on a change: a text mesh is built again each time its colour is set
            }
            Vector3 side = Vector3.Cross(Vector3.up, zones.TorsoForward).normalized;
            float halfWidth = Mathf.Abs(Vector3.Dot(_bounds.extents, Abs(Runtime.RigidPose.Conjugate(rot) * side))) * scale;
            _label.transform.position = zones.Pouch + lift + side * (halfWidth + 0.018f) + Vector3.up * 0.01f;
            Transform head = _sys.Rig.Head;
            Vector3 toHead = _label.transform.position - head.position;
            _label.transform.rotation = toHead.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(toHead, Vector3.up) : Quaternion.identity;
        }

        private int _shownSpare = int.MinValue, _shownCapacity = -1;
        private bool _shownByHand;
        private PropKind _shownKind;

        /// <summary>Where a hand takes the shown magazine: just under its top (world, last frame drawn).</summary>
        internal Vector3 GrabPoint { get; private set; }

        internal bool Shown => _root != null && _root.activeSelf;

        private static string PouchText(int spare, int capacity, PropKind kind)
        {
            if (spare < 0)
            {
                return "\u221e";
            }
            if (kind != PropKind.Magazine)
            {
                return spare.ToString();
            }
            int mags = spare / Mathf.Max(1, capacity);
            int rest = spare - mags * capacity;
            if (mags == 0 && rest > 0)
            {
                return rest + " rds"; // no full magazine left: "0+" read as empty
            }
            return rest > 0 ? mags + "+" : mags.ToString();
        }

        private static bool GameContextOnFoot() => Util.GameContext.PlayerOnFoot || DrivingWeapons.Armed;

        private static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

        /// <summary>The shown model was made from the gun's magazine as it looks loaded (see <see cref="MagazineProp.KeepsLoadedLook"/>).</summary>
        private bool _builtLoaded;

        /// <summary>The shown model is an empty magazine (loaded by hand, the reserve out).</summary>
        private bool _builtEmpty;

        private void Build(WeaponRig rig, PropKind kind, bool loadedLook, bool empty)
        {
            if (_root == null)
            {
                _root = new GameObject("RavenfieldVR Pouch");
                Object.DontDestroyOnLoad(_root);
                var labelGo = new GameObject("Count");
                labelGo.layer = MagazineProp.DisplayLayer;
                labelGo.transform.SetParent(_root.transform, false);
                _label = labelGo.AddComponent<TextMesh>();
                Font font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                _label.font = font;
                labelGo.GetComponent<MeshRenderer>().sharedMaterial = font.material;
                _label.fontSize = 64;
                _label.characterSize = 0.0028f;
                _label.anchor = TextAnchor.MiddleLeft;
                _label.fontStyle = FontStyle.Bold;
                labelGo.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            if (_model != null)
            {
                _model.SetActive(false); // kept for the next time this weapon is drawn
            }
            var key = (rig.Data, kind);
            if (_kept.TryGetValue(key, out Kept k) && StillDrawable(k.Model) && k.Empty == empty && (k.Loaded || !loadedLook))
            {
                // Drawn before: the same model again (each switch made a new copy of the magazine's meshes; 255.P3-2).
                _model = k.Model;
                _bounds = k.Bounds;
                _insertLocal = k.Insert;
                _frontLocal = k.Front;
                _scale = k.Scale;
                _model.SetActive(true);
                _keptOrder.Remove(key);
                _keptOrder.Add(key);
                return;
            }
            if (k.Model != null)
            {
                if (k.Model == _model)
                {
                    _model = null;
                }
                Object.Destroy(k.Model);
            }
            _model = MagazineProp.BuildDisplay(rig, kind, out _bounds, out _insertLocal, out _frontLocal, loaded: !empty);
            _model.transform.SetParent(_root.transform, true);
            // A picture of what you can take, not a thing in the world: it casts no shadow on the ground under you.
            foreach (Renderer r in _model.GetComponentsInChildren<Renderer>(true))
            {
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            float size = _bounds.size.magnitude;
            _scale = size > MaxSize ? MaxSize / size : 1f; // a rocket shows smaller than life
            _kept[key] = new Kept { Model = _model, Bounds = _bounds, Insert = _insertLocal, Front = _frontLocal, Scale = _scale, Loaded = loadedLook, Empty = empty };
            _keptOrder.Remove(key);
            _keptOrder.Add(key);
            while (_keptOrder.Count > KeptModels)
            {
                (WeaponRigData, PropKind) oldest = _keptOrder[0];
                _keptOrder.RemoveAt(0);
                if (_kept.TryGetValue(oldest, out Kept o) && o.Model != null && o.Model != _model)
                {
                    Object.Destroy(o.Model);
                }
                _kept.Remove(oldest);
            }
        }

        /// <summary>The pouch models of the last few weapons drawn (per weapon type and what it feeds from).</summary>
        private struct Kept
        {
            public GameObject Model;
            public Bounds Bounds;
            public Vector3 Insert, Front;
            public float Scale;
            /// <summary>Made from the magazine's kept look (loaded, or empty when <see cref="Empty"/>).</summary>
            public bool Loaded;
            /// <summary>An empty magazine (loaded by hand, the reserve out).</summary>
            public bool Empty;
        }

        private const int KeptModels = 6;
        private readonly Dictionary<(WeaponRigData, PropKind), Kept> _kept = new Dictionary<(WeaponRigData, PropKind), Kept>();
        private readonly List<(WeaponRigData, PropKind)> _keptOrder = new List<(WeaponRigData, PropKind)>();

        /// <summary>A kept model whose meshes and materials are all still there (a weapon's own may go with its scene).</summary>
        private static bool StillDrawable(GameObject model)
        {
            if (model == null)
            {
                return false;
            }
            foreach (Renderer r in model.GetComponentsInChildren<Renderer>(true))
            {
                foreach (Material m in r.sharedMaterials)
                {
                    if (m == null)
                    {
                        return false;
                    }
                }
                if (r is MeshRenderer && (r.GetComponent<MeshFilter>() == null || r.GetComponent<MeshFilter>().sharedMesh == null)
                    || r is SkinnedMeshRenderer s && s.sharedMesh == null)
                {
                    return false;
                }
            }
            return true;
        }

        private void SetVisible(bool visible)
        {
            if (_root != null && _root.activeSelf != visible)
            {
                _root.SetActive(visible);
            }
        }

        public void Destroy()
        {
            if (_root != null)
            {
                Object.Destroy(_root);
            }
            _root = null;
            _model = null;
            _builtFor = null;
            _kept.Clear(); // their objects went with the root
            _keptOrder.Clear();
            _shownSpare = int.MinValue; // a rebuilt label must be written again
            _shownCapacity = -1;
        }
    }
}
