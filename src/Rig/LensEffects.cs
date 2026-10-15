using System;
using System.Collections.Generic;
using System.Threading;
using RavenfieldVR.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace RavenfieldVR.Rig
{
    /// <summary>
    /// The on-screen effects the user picked and approved (Tasks2 209), built the way the approved pictures were built:
    /// each eye's picture is computed from the approved code (branch effects-source: muzzle-flash-sim/flash_sim.py
    /// <c>lens_flash</c>, <c>tone</c> and the lens mask; screen-effects/screen_effects.py F, G, <c>drop_field</c> and
    /// <c>shade_drops</c>) at the tested eye view, 300 x 250 pixels over 104 degrees, and added (see-through) to that eye on
    /// a quad a hand's width in front of it, lens-locked:
    /// A-C the muzzle flash on the lens (only on a shot where the gun's own flash plays: <see cref="Weapons.LensFlash"/>
    /// decides that and each eye's strength), F a red glow on the side a hit came from, G the red edge of low health
    /// beating with the heart, I water drops after coming up out of the water. The pictures are computed on a worker
    /// thread and shown a frame later.
    /// </summary>
    public sealed class LensEffects
    {
        public static LensEffects Instance { get; private set; }

        /// <summary>The tested eye view (flash_sim.py W, H, HFOV).</summary>
        internal const int W = 300, H = 250;
        internal static readonly float Focal = W / 2f / Mathf.Tan(52f * Mathf.Deg2Rad);
        private const float CX = W / 2f, CY = H / 2f;
        /// <summary>
        /// The picture drawn reaches past the tested view (a headset shows up to about 110 degrees across, and an Index
        /// about as much up and down): 40 and 45 pixels more on each side (to 58 and 55 degrees out), fading out over its
        /// last 24 pixels, so no frame of the effect shows in the headset. The view's rounded frame
        /// (flash_sim.py lens_mask) belongs to the approved sheets and is used for <see cref="SavePictures"/> only.
        /// </summary>
        private const int MX = 40, MY = 45, CW = W + 2 * MX, CH = H + 2 * MY;
        private const float Distance = 0.26f;
        /// <summary>flash_sim.py: x0.5 a frame at 90 fps, gone after 60 ms.</summary>
        internal const float Decay = 0.016f, Life = 0.06f;

        private static readonly float[] Hot = { 1f, 0.93f, 0.78f }, Fire = { 1f, 0.62f, 0.22f }, Ember = { 0.95f, 0.38f, 0.12f };
        // (t, radius, colour, strength) of the lens ghosts on the line from the flash through the lens centre.
        private static readonly float[,] Ghosts =
        {
            { 0.55f, 9f, 1f, 0.7f, 0.35f, 0.10f }, { 1.15f, 18f, 0.45f, 0.85f, 0.7f, 0.06f },
            { 1.55f, 6f, 0.9f, 0.5f, 0.9f, 0.09f }, { 1.9f, 30f, 1f, 0.75f, 0.45f, 0.035f }
        };

        internal struct Flash
        {
            public float X, Y, Power, Phase, Born;
            /// <summary>
            /// When its first picture was shown (-1 before): its 60 ms run from there, so the first picture shown is the
            /// flash at full strength and the next ones fade from it, however late the worker was. Counted from the shot, a
            /// worker that took 80 ms (it can, while the game's own threads are busy) showed nothing at all.
            /// </summary>
            public float Drawn;
            /// <summary>Its age in the picture being made (set when handed to the worker).</summary>
            public float Age;
            public int Serial;
        }

        /// <summary>One frame's inputs, copied for the worker (only while it is idle).</summary>
        private sealed class Job
        {
            public float Now;
            public readonly List<Flash>[] Flashes = { new List<Flash>(), new List<Flash>() };
            public readonly float[] Glow = new float[2], GlowX = new float[2], GlowY = new float[2], Side = { 1f, 1f };
            public float Hit, HitDirX, HitDirY;
            public bool HitBalance;
            public readonly float[] HitEye = { 1f, 1f };
            public float LowHealth, Drops;
            public int DropsSeed;
        }

        private readonly VRRig _rig;
        private readonly Texture2D[] _tex = new Texture2D[2];
        private readonly Color32[][] _out = { new Color32[CW * CH], new Color32[CW * CH] };
        private readonly float[][] _acc = { new float[CW * CH * 3], new float[CW * CH * 3] };
        private readonly GameObject[] _quads = new GameObject[3];
        private readonly bool _ok;

        // Inputs, main thread.
        private readonly List<Flash>[] _flashes = { new List<Flash>(), new List<Flash>() };
        private readonly float[] _glow = new float[2], _glowX = new float[2], _glowY = new float[2], _side = { 1f, 1f };
        private float _hitAt = -10f, _hitAngle;
        private bool _hitBalance, _hitNew;
        private float _lowHealth;
        private float _dropsAt = -10f;
        private int _dropsSeed;

        // The worker.
        // Four bands drawn at once (each eye's top and bottom half): band 0 on the worker itself, 1-3 on threads of their own.
        private const int Bands = 4;
        private readonly Thread _thread;
        private readonly Thread[] _bandThreads = new Thread[Bands];
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private readonly AutoResetEvent[] _bandWake = new AutoResetEvent[Bands], _bandDone = new AutoResetEvent[Bands];
        private readonly Exception[] _bandError = new Exception[Bands];
        // Flashes are numbered: the ones up to _jobSerial were in the picture being made, and are shown from when it is.
        private int _nextSerial, _jobSerial;
        private float _frameDt = 1f / 72f;
        private readonly Job _job = new Job();
        private volatile bool _busy, _done, _stop, _failed;
        private bool _resultAny, _shownAny, _resultFlash, _resultHit;
        // The flash and glow strength in each eye's finished picture, and in the one shown now (the film's frame log).
        private readonly float[] _resultPower = new float[2], _resultGlow = new float[2], _shownPower = new float[2], _shownGlow = new float[2];
        private volatile float _workMs, _workMsMax;

        /// <summary>Autotest film: this eye's shown flash and glow strength and the worker's time for a frame.</summary>
        internal string FilmState(int eye) =>
            $"flash {_shownPower[eye]:0.00} glow {_shownGlow[eye]:0.00} worker {_workMs:0.0} ms (most {_workMsMax:0.0})";

        /// <summary>The worker's time for the last frame and the most so far (ms): it must stay under a frame.</summary>
        internal float WorkMs => _workMs;
        internal float WorkMsMax => _workMsMax;

        /// <summary>Autotest: the pictures shown now hold a muzzle flash (they come a frame after the shot).</summary>
        internal bool ShowsFlash { get; private set; }

        /// <summary>Autotest: what the effects are doing (when a check fails).</summary>
        internal string DebugState =>
            $"set up {_ok}, hit {Time.unscaledTime - _hitAt:0.00} s ago, worker busy {_busy} / done {_done}, shown {_shownAny}, "
            + $"player {(Util.GameContext.PlayerActor == null ? "none" : Util.GameContext.PlayerActor.dead ? "dead" : "alive")}, thread {(_thread != null ? _thread.IsAlive.ToString() : "none")}";

        /// <summary>Autotest: the pictures shown now hold a hit's glow.</summary>
        internal bool ShowsHit { get; private set; }
        private Exception _workerError;

        // Made once.
        private static readonly float[] Mask = new float[W * H], LowEdge = new float[CW * CH], EdgeFade = new float[CW * CH];
        private static readonly float[] Petals = new float[AngleSteps], Star = new float[AngleSteps];
        private static readonly float[] ExpTable = new float[ExpSteps + 2];
        private const int AngleSteps = 4096, ExpSteps = 8192;
        private const float ExpMax = 24f;
        private static bool _tables;
        // The drops' picture per eye (toned), made on the worker when asked for.
        private readonly float[][] _drops = { new float[CW * CH * 3], new float[CW * CH * 3] };
        // The drops' supersampled height field and picture, made once and reused (15 MB: a new pair each time came up out
        // of the water made the garbage collector pause the game).
        private float[] _dropField, _dropShade;
        private int _dropsMadeFor;
        private readonly float[][] _washX = { new float[CW], new float[CW], new float[CW], new float[CW] }, _washY = { new float[CH], new float[CH], new float[CH], new float[CH] };

        public LensEffects(VRRig rig)
        {
            Instance = this;
            _rig = rig;
            MakeTables();
            Shader additive = FindAdditive();
            if (additive == null)
            {
                Log.WarnOnce("lenseffects-shader", "No additive particle shader found: the on-screen effects are off.");
                return;
            }
            Mesh quad = QuadMesh();
            for (int e = 0; e < 2; e++)
            {
                _tex[e] = new Texture2D(CW, CH, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "RavenfieldVR lens " + e };
                _tex[e].SetPixels32(_out[e]);
                _tex[e].Apply(false, false);
            }
            for (int q = 0; q < 3; q++)
            {
                var go = new GameObject("RavenfieldVR Lens Effects " + (q == 0 ? "L" : q == 1 ? "R" : "mono")) { layer = 5 };
                go.transform.SetParent(rig.Camera.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = quad;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = new Material(additive) { mainTexture = _tex[q == 0 ? 0 : 1], renderQueue = 4003, name = "RavenfieldVR lens effects" };
                r.shadowCastingMode = ShadowCastingMode.Off;
                r.receiveShadows = false;
                r.lightProbeUsage = LightProbeUsage.Off;
                r.reflectionProbeUsage = ReflectionProbeUsage.Off;
                if (r.sharedMaterial.HasProperty("_TintColor"))
                {
                    r.sharedMaterial.SetColor("_TintColor", new Color(0.5f, 0.5f, 0.5f, 0.5f)); // the legacy particle shaders double it: x1
                }
                var eye = go.AddComponent<LensEye>();
                eye.Renderer = r;
                eye.Eye = q;
                eye.Head = rig.Camera;
                eye.Picture = _tex[q == 0 ? 0 : 1];
                go.SetActive(false);
                _quads[q] = go;
            }
            Log.Info($"On-screen effects drawn with '{additive.name}' (colour space {QualitySettings.activeColorSpace}).");
            // Above the game's own threads, and in four bands at once: a flash's picture took 30-80 ms on one thread in the
            // game's runtime, and came after the flash was over.
            _thread = new Thread(WorkerLoop) { IsBackground = true, Name = "RavenfieldVR lens effects", Priority = System.Threading.ThreadPriority.AboveNormal };
            for (int b = 1; b < Bands; b++)
            {
                int band = b;
                _bandWake[b] = new AutoResetEvent(false);
                _bandDone[b] = new AutoResetEvent(false);
                _bandThreads[b] = new Thread(() => BandLoop(band)) { IsBackground = true, Name = "RavenfieldVR lens effects " + b, Priority = System.Threading.ThreadPriority.AboveNormal };
                _bandThreads[b].Start();
            }
            _thread.Start();
            _ok = true;
        }

        private static Shader FindAdditive()
        {
            foreach (string n in new[] { "Legacy Shaders/Particles/Additive", "Particles/Additive", "Mobile/Particles/Additive" })
            {
                Shader s = Shader.Find(n);
                if (s != null)
                {
                    return s;
                }
            }
            return null;
        }

        private static Mesh QuadMesh()
        {
            var m = new Mesh { name = "RavenfieldVR lens quad" };
            m.vertices = new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f) };
            m.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f) };
            m.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            m.RecalculateBounds();
            m.bounds = new Bounds(Vector3.zero, Vector3.one * 2f);
            return m;
        }

        // ---- inputs (main thread) ---------------------------------------------------------------------------------

        /// <summary>The eye's centre, sideways from the head (metres; left negative).</summary>
        /// <remarks>Single-pass stereo shows one picture to both eyes, made for the point between them.</remarks>
        internal float EyeX(int eye) => LensEye.SinglePass ? 0f : (eye == 0 ? -0.5f : 0.5f) * Ipd;

        internal float Ipd
        {
            get
            {
                float s = _rig != null && _rig.Camera != null ? _rig.Camera.stereoSeparation : 0f;
                return s > 0.04f && s < 0.09f ? s : 0.064f;
            }
        }

        /// <summary>A head-space point as flash_sim.py projects it for an eye: its pixel in that eye's view (y down).</summary>
        internal static Vector2 Project(Vector3 headLocal, float eyeX) =>
            new Vector2(CX - 0.5f + Focal * (headLocal.x - eyeX) / headLocal.z, CY - 0.5f - Focal * headLocal.y / headLocal.z);

        internal void AddFlash(int eye, Flash f)
        {
            f.Drawn = -1f;
            f.Serial = ++_nextSerial;
            _flashes[eye].Add(f);
        }

        /// <summary>The steady glow of sustained fire (flash_sim.py Limiter.step) at the muzzle's pixel now, and the gun's side.</summary>
        internal void SetGlow(int eye, float glow, Vector2 at, float side)
        {
            _glow[eye] = glow;
            _glowX[eye] = at.x;
            _glowY[eye] = at.y;
            _side[eye] = side;
        }

        /// <summary>A hit from <paramref name="angle"/> (degrees in the view: 0 in front, 90 left, as the game's arrow).</summary>
        internal void Hit(float angle, bool balanceOnly)
        {
            if (balanceOnly && !_hitBalance && (_hitNew || Time.unscaledTime - _hitAt < 0.5f))
            {
                return; // a red hit's glow is still showing: a small knock does not turn it amber
            }
            _hitNew = true; // its half second starts when it is first drawn: a slow frame after the hit did not eat it
            _hitAngle = angle;
            _hitBalance = balanceOnly;
        }

        /// <summary>How strongly the low-health edge shows now (0..1, the heartbeat included).</summary>
        internal void SetLowHealth(float amount)
        {
            _lowHealth = Mathf.Clamp01(amount);
        }

        /// <summary>Fresh water drops on the view (coming up out of the water); they dry within about 3 s.</summary>
        internal void WaterDrops()
        {
            _dropsAt = Time.unscaledTime;
            _dropsSeed = _dropsSeed % 100000 + 1 + UnityEngine.Random.Range(0, 1000) * 7;
        }

        /// <summary>Autotest: drops shown with the option off (the player's saved setting is not touched).</summary>
        internal static bool TestDrops;

        /// <summary>Autotest: the drops' strength now (0 when there are none).</summary>
        internal float DropsShown => DropsFade(Time.unscaledTime);

        /// <summary>The drops stay a second, then dry over two.</summary>
        private float DropsFade(float now)
        {
            float age = now - _dropsAt;
            return age < 0f || age > 3f ? 0f : age < 1f ? 1f : 1f - (age - 1f) / 2f;
        }

        /// <summary>Each frame, before drawing: shows the last finished pictures and hands this frame's inputs to the worker.</summary>
        public void Present()
        {
            if (!_ok)
            {
                return;
            }
            if (_workerError != null)
            {
                Log.ExceptionOnce("LensEffects worker", _workerError);
                _workerError = null;
            }
            float now = Time.unscaledTime;
            _frameDt = Mathf.Lerp(_frameDt, Mathf.Clamp(Time.unscaledDeltaTime, 0.005f, 0.05f), 0.1f);
            bool dead = Util.GameContext.PlayerActor == null || Util.GameContext.PlayerActor.dead;
            for (int e = 0; e < 2; e++)
            {
                List<Flash> list = _flashes[e];
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    // Gone 60 ms after its first picture was shown (one never shown, a quarter second after the shot).
                    float since = list[i].Drawn >= 0f ? now - list[i].Drawn : now - list[i].Born - 0.25f;
                    if (since > Life)
                    {
                        list.RemoveAt(i);
                    }
                }
            }
            if (_busy && _done)
            {
                if (!_failed)
                {
                    for (int e = 0; e < 2; e++)
                    {
                        _tex[e].SetPixels32(_out[e]);
                        _tex[e].Apply(false, false);
                        _shownPower[e] = _resultPower[e];
                        _shownGlow[e] = _resultGlow[e];
                        // The flashes in this picture are shown from now: their fade runs from this frame.
                        List<Flash> list = _flashes[e];
                        for (int i = 0; i < list.Count; i++)
                        {
                            if (list[i].Drawn < 0f && list[i].Serial <= _jobSerial)
                            {
                                Flash f = list[i];
                                f.Drawn = now;
                                list[i] = f;
                            }
                        }
                    }
                    _shownAny = _resultAny;
                    ShowsFlash = _resultFlash;
                    ShowsHit = _resultHit;
                }
                _failed = false;
                _busy = false;
            }
            if (!_busy)
            {
                Job j = _job;
                j.Now = now;
                bool any = false;
                for (int e = 0; e < 2; e++)
                {
                    j.Flashes[e].Clear();
                    if (!dead)
                    {
                        // Each flash as it will be when this picture is shown, a frame from now: one not shown yet at full
                        // strength (its first picture), the others faded by the time since their first.
                        List<Flash> list = _flashes[e];
                        for (int i = 0; i < list.Count; i++)
                        {
                            Flash f = list[i];
                            f.Age = f.Drawn < 0f ? 0f : now + _frameDt - f.Drawn;
                            j.Flashes[e].Add(f);
                        }
                    }
                    j.Glow[e] = dead ? 0f : _glow[e];
                    j.GlowX[e] = _glowX[e];
                    j.GlowY[e] = _glowY[e];
                    j.Side[e] = _side[e];
                    any |= j.Flashes[e].Count > 0 || j.Glow[e] > 1e-3f;
                }
                // F: half a second, in at once and fading (screen_effects.py F is its first frame).
                if (_hitNew)
                {
                    _hitNew = false;
                    _hitAt = now;
                }
                float hitAge = now - _hitAt;
                j.Hit = dead || hitAge > 0.5f ? 0f : 1f - hitAge / 0.5f;
                float rad = _hitAngle * Mathf.Deg2Rad;
                j.HitDirX = -Mathf.Sin(rad);
                j.HitDirY = -Mathf.Cos(rad);
                j.HitBalance = _hitBalance;
                // Both eyes, the one on the hit's side at full strength, the other down to 0.55.
                float side = j.HitDirX;
                j.HitEye[0] = side > 0f ? 1f - 0.45f * side : 1f;
                j.HitEye[1] = side < 0f ? 1f + 0.45f * side : 1f;
                j.LowHealth = dead ? 0f : _lowHealth;
                j.Drops = dead || !(VRConfig.WaterDrops.Value || TestDrops) ? 0f : DropsFade(now);
                j.DropsSeed = _dropsSeed;
                any |= j.Hit > 0.001f || j.LowHealth > 0.001f || j.Drops > 0.001f;
                if (any || _shownAny)
                {
                    _jobSerial = _nextSerial;
                    _resultAny = any;
                    _resultFlash = j.Flashes[0].Count + j.Flashes[1].Count > 0;
                    _resultHit = j.Hit > 0.001f;
                    _done = false;
                    _busy = true;
                    _wake.Set();
                }
            }
            for (int q = 0; q < 3; q++)
            {
                if (_quads[q].activeSelf != _shownAny)
                {
                    _quads[q].SetActive(_shownAny);
                }
            }
            if (_shownAny)
            {
                float ipd = Ipd;
                for (int q = 0; q < 3; q++)
                {
                    // Single-pass stereo draws the one centred quad for both eyes: far off (20 m, the same size in view), so
                    // each eye sees it straight ahead of itself. At a hand's width it sat about 7 degrees off in each eye.
                    float d = q == 2 && LensEye.SinglePass ? 20f : Distance;
                    Transform t = _quads[q].transform;
                    t.localPosition = new Vector3(q == 0 ? -ipd / 2f : q == 1 ? ipd / 2f : 0f, 0f, d);
                    t.localRotation = Quaternion.identity;
                    t.localScale = new Vector3(CW / Focal * d, CH / Focal * d, 1f);
                }
            }
        }

        /// <summary>
        /// Autotest: both eyes' pictures as shown now, side by side on black (left, right), as the approved sheets show
        /// them, into the shots folder.
        /// </summary>
        internal void SavePictures(string name)
        {
            if (!_ok)
            {
                return;
            }
            var sheet = new Texture2D(W * 2 + 6, H, TextureFormat.RGB24, false);
            try
            {
                var black = new Color32[sheet.width * sheet.height];
                for (int i = 0; i < black.Length; i++)
                {
                    black[i] = new Color32(0, 0, 0, 255);
                }
                sheet.SetPixels32(black);
                for (int e = 0; e < 2; e++)
                {
                    // The tested view out of the larger picture, in the view's rounded frame.
                    Color32[] view = _tex[e].GetPixels32();
                    var framed = new Color32[W * H];
                    for (int row = 0; row < H; row++)
                    {
                        for (int x = 0; x < W; x++)
                        {
                            Color32 c = view[(MY + row) * CW + MX + x];
                            float m = Mask[(H - 1 - row) * W + x];
                            framed[row * W + x] = new Color32((byte)(c.r * m), (byte)(c.g * m), (byte)(c.b * m), 255);
                        }
                    }
                    sheet.SetPixels32(e * (W + 6), 0, W, H, framed);
                }
                sheet.Apply(false, false);
                System.IO.Directory.CreateDirectory(Shots.Dir);
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(Shots.Dir, name + ".png"), sheet.EncodeToPNG());
            }
            catch (Exception e)
            {
                Log.Warn($"Lens pictures '{name}' failed: {e.Message}");
            }
            finally
            {
                UnityEngine.Object.Destroy(sheet);
            }
        }

        public void Destroy()
        {
            _stop = true;
            _wake.Set();
            for (int b = 1; b < Bands; b++)
            {
                _bandWake[b]?.Set();
            }
            foreach (GameObject q in _quads)
            {
                if (q != null)
                {
                    MeshRenderer r = q.GetComponent<MeshRenderer>();
                    if (r != null && r.sharedMaterial != null)
                    {
                        UnityEngine.Object.Destroy(r.sharedMaterial);
                    }
                    MeshFilter f = q.GetComponent<MeshFilter>();
                    if (f != null && f.sharedMesh != null)
                    {
                        UnityEngine.Object.Destroy(f.sharedMesh); // one mesh shared by the three: destroyed once, then null
                    }
                    UnityEngine.Object.Destroy(q);
                }
            }
            foreach (Texture2D t in _tex)
            {
                if (t != null)
                {
                    UnityEngine.Object.Destroy(t);
                }
            }
            if (Instance == this)
            {
                Instance = null;
            }
        }

        // ---- the worker ------------------------------------------------------------------------------------------

        private void WorkerLoop()
        {
            while (true)
            {
                _wake.WaitOne();
                if (_stop)
                {
                    return;
                }
                var watch = System.Diagnostics.Stopwatch.StartNew();
                bool madeDrops = false;
                try
                {
                    if (_job.Drops > 0f && _job.DropsSeed != _dropsMadeFor)
                    {
                        madeDrops = true; // once, on surfacing: not counted in the frame's time
                        MakeDrops(_job.DropsSeed);
                        _dropsMadeFor = _job.DropsSeed;
                    }
                    for (int b = 1; b < Bands; b++)
                    {
                        _bandError[b] = null;
                        _bandWake[b].Set();
                    }
                    try
                    {
                        RenderBand(0, _job);
                    }
                    finally
                    {
                        for (int b = 1; b < Bands; b++)
                        {
                            _bandDone[b].WaitOne(); // the other bands, made at the same time
                        }
                    }
                    for (int b = 1; b < Bands; b++)
                    {
                        if (_bandError[b] != null)
                        {
                            throw _bandError[b];
                        }
                    }
                }
                catch (Exception ex)
                {
                    _workerError = ex;
                    _failed = true; // half drawn: not shown
                }
                float ms = (float)watch.Elapsed.TotalMilliseconds;
                _workMs = ms;
                if (ms > _workMsMax && !madeDrops)
                {
                    _workMsMax = ms;
                }
                _done = true;
            }
        }

        private void BandLoop(int band)
        {
            while (true)
            {
                _bandWake[band].WaitOne();
                if (_stop)
                {
                    return;
                }
                try
                {
                    RenderBand(band, _job);
                }
                catch (Exception ex)
                {
                    _bandError[band] = ex;
                }
                _bandDone[band].Set();
            }
        }

        /// <summary>One band of one eye's picture: band 0 / 1 the left eye's top / bottom half, 2 / 3 the right eye's.</summary>
        private void RenderBand(int band, Job j)
        {
            int eye = band >> 1;
            int y0 = (band & 1) == 0 ? 0 : CH / 2, y1 = (band & 1) == 0 ? CH / 2 : CH;
            float[] acc = _acc[eye];
            Array.Clear(acc, y0 * CW * 3, (y1 - y0) * CW * 3);
            bool flash = false;
            float side = j.Side[eye], strongest = 0f;
            if (j.Glow[eye] > 1e-3f)
            {
                LensFlash(band, y0, y1, acc, j.GlowX[eye], j.GlowY[eye], j.Glow[eye], 0f, side, false);
                flash = true;
            }
            foreach (Flash f in j.Flashes[eye])
            {
                float age = f.Age;
                if (age >= 0f && age <= Life)
                {
                    float p = f.Power * Mathf.Exp(-age / Decay);
                    LensFlash(band, y0, y1, acc, f.X, f.Y, p, f.Phase, side, true);
                    strongest = Mathf.Max(strongest, p);
                    flash = true;
                }
            }
            _resultPower[eye] = strongest;
            _resultGlow[eye] = j.Glow[eye];
            float hit = j.Hit * j.HitEye[eye];
            float cx = 0f, cy = 0f, dx = j.HitDirX, dy = j.HitDirY;
            if (hit > 0.001f)
            {
                // screen_effects.py F, for a hit from the right: two glows centred 25 px past the view's right edge,
                // narrow across the edge (x0.8) and long along it (x1.9). Turned to the side the hit came from.
                float tx = Mathf.Abs(dx) > 1e-4f ? CX / Mathf.Abs(dx) : float.MaxValue, ty = Mathf.Abs(dy) > 1e-4f ? CY / Mathf.Abs(dy) : float.MaxValue;
                float t = Mathf.Min(tx, ty) + 25f;
                cx = CX + dx * t;
                cy = CY + dy * t;
            }
            float low = j.LowHealth, drops = j.Drops;
            float[] dropImg = _drops[eye];
            Color32[] o = _out[eye];
            float c1r = 0.95f * 1.1f, c1g = 0.06f * 1.1f, c1b = 0.03f * 1.1f, c2r = 0.5f, c2g = 0.25f * 0.5f, c2b = 0.15f * 0.5f;
            if (j.HitBalance)
            {
                // A blow to the balance only: the same glow in amber (the game's balance colour).
                c1r = 1.05f; c1g = 0.6f; c1b = 0.05f; c2r = 0.5f; c2g = 0.4f; c2b = 0.1f;
            }
            const float inv70 = 1f / (2f * 70f * 70f), inv25 = 1f / (2f * 25f * 25f);
            for (int y = y0; y < y1; y++)
            {
                int rowOut = (CH - 1 - y) * CW; // the texture's first row is the picture's bottom
                int vy = y - MY;
                for (int x = 0; x < CW; x++)
                {
                    int i = y * CW + x, k = i * 3, vx = x - MX;
                    float m = EdgeFade[i];
                    if (m <= 0f)
                    {
                        o[rowOut + x] = new Color32(0, 0, 0, 255);
                        continue;
                    }
                    float r = 0f, g = 0f, b = 0f;
                    if (flash)
                    {
                        r = ToneFlash(acc[k]);
                        g = ToneFlash(acc[k + 1]);
                        b = ToneFlash(acc[k + 2]);
                    }
                    if (hit > 0.001f)
                    {
                        float px = vx - cx, py = vy - cy;
                        float u = px * dx + py * dy, v = -px * dy + py * dx;
                        float q = (u / 0.8f) * (u / 0.8f) + (v / 1.9f) * (v / 1.9f);
                        float g70 = ExpNeg(q * inv70) * hit, g25 = ExpNeg(q * inv25) * hit;
                        r += ToneSe(g70 * c1r + g25 * c2r);
                        g += ToneSe(g70 * c1g + g25 * c2g);
                        b += ToneSe(g70 * c1b + g25 * c2b);
                    }
                    if (low > 0.001f)
                    {
                        r += LowEdge[i] * low; // screen_effects.py G: [0.55, 0, 0] shown as it is
                    }
                    if (drops > 0.001f)
                    {
                        r += dropImg[k] * drops;
                        g += dropImg[k + 1] * drops;
                        b += dropImg[k + 2] * drops;
                    }
                    o[rowOut + x] = new Color32(To8(r * m), To8(g * m), To8(b * m), 255);
                }
            }
        }

        /// <summary>flash_sim.py lens_flash: one flash added at (mx, my) with strength p (0..1.6).</summary>
        private void LensFlash(int band, int y0, int y1, float[] acc, float mx, float my, float p, float phase, float side, bool extras)
        {
            float[] washX = _washX[band], washY = _washY[band];
            if (p <= 1e-4f)
            {
                return;
            }
            float sCore = 2.5f + 3.5f * p, sFire = 8f + 15f * p, sEmber = 16f + 26f * p, sTail = 35f + 45f * p, sStar = 9f + 22f * p;
            float ringR = 55f + 30f * p;
            float invCore = 1f / (sCore * sCore), invFire = 1f / (sFire * sFire), invEmber = 1f / (sEmber * sEmber), invTail = 1f / (sTail * sTail);
            // The starburst out to 4.5 of its widths (6.5 before): further out it adds under one step in 255, and it was
            // most of the area drawn.
            float bloom2 = 2.7f * sEmber * 2.7f * sEmber, star2 = extras ? 4.5f * sStar * 4.5f * sStar : 0f;
            float near2 = Mathf.Max(bloom2, star2);
            float ringLo = Mathf.Max(0f, ringR - 13f), ringHi = ringR + 13f;
            float ringLo2 = extras ? ringLo * ringLo : 0f, ringHi2 = extras ? ringHi * ringHi : -1f;
            float tailA = p * 0.10f;
            float veil = extras && p > 1f ? 0.05f * (p - 1f) / 0.6f : 0f;
            float washA = extras ? 0.42f * p : 0f;
            if (extras)
            {
                // The see-through wash from the gun's side (lower left or right), shaped like a blast's but much lighter.
                float wcx = W * 0.5f + side * W * 0.62f, wcy = H * 0.5f + H * 0.42f;
                const float invWash = 1f / (2f * 100f * 100f);
                for (int x = 0; x < CW; x++)
                {
                    float u = (x - MX - wcx) / 1.05f;
                    washX[x] = ExpNeg(u * u * invWash);
                }
                for (int y = 0; y < CH; y++)
                {
                    float v = (y - MY - wcy) / 1.25f;
                    washY[y] = ExpNeg(v * v * invWash);
                }
            }
            float phaseSteps = phase / (2f * Mathf.PI) * AngleSteps;
            for (int y = y0; y < y1; y++)
            {
                float dy = y - MY - my;
                float dy2 = dy * dy;
                float wy = washA * (extras ? washY[y] : 0f);
                int row = y * CW * 3;
                for (int x = 0; x < CW; x++)
                {
                    float dx = x - MX - mx;
                    float r2 = dx * dx + dy2;
                    int k = row + x * 3;
                    float tail = tailA / (1f + r2 * invTail);
                    float ar = tail * Ember[0], ag = tail * Ember[1], ab = tail * Ember[2];
                    if (extras)
                    {
                        float wsh = wy * washX[x];
                        ar += wsh + veil;
                        ag += wsh * 0.7f + veil * 0.75f;
                        ab += wsh * 0.38f + veil * 0.45f;
                    }
                    if (r2 < near2 || r2 > ringLo2 && r2 < ringHi2)
                    {
                        float r = Mathf.Sqrt(r2) + 1e-3f;
                        if (r2 < near2)
                        {
                            int ai = AngleIndex(dy, dx, phaseSteps);
                            float pet = Petals[ai];
                            float ip2 = 1f / (pet * pet);
                            float core = ExpNeg(r2 * invCore) * 2.4f * p;
                            float fire = ExpNeg(r2 * invFire * ip2) * p;
                            float ember = ExpNeg(r2 * invEmber * ip2) * 0.35f * p;
                            ar += core * Hot[0] + fire * Fire[0] + ember * Ember[0];
                            ag += core * Hot[1] + fire * Fire[1] + ember * Ember[1];
                            ab += core * Hot[2] + fire * Fire[2] + ember * Ember[2];
                            if (extras && r2 < star2)
                            {
                                // A faint, short starburst: the eye's own (lashes, the pupil), not a camera's star filter.
                                float st = Star[ai] * ExpNeg(r / sStar) * 0.35f * p;
                                ar += st * Hot[0];
                                ag += st * Hot[1];
                                ab += st * Hot[2];
                            }
                        }
                        if (extras && r2 > ringLo2 && r2 < ringHi2)
                        {
                            float d = (r - ringR) / 4f;
                            float ring = ExpNeg(d * d) * 0.05f * p;
                            ar += ring;
                            ag += ring * 0.8f;
                            ab += ring * 0.55f;
                        }
                    }
                    acc[k] += ar;
                    acc[k + 1] += ag;
                    acc[k + 2] += ab;
                }
            }
            if (!extras)
            {
                return;
            }
            // Ghosts: reflections inside the lens on the line from the flash through the lens centre.
            float vx = CX - mx, vy = CY - my;
            for (int gi = 0; gi < 4; gi++)
            {
                float t = Ghosts[gi, 0], rad = Ghosts[gi, 1], a = Ghosts[gi, 5] * p * 3f;
                float gx = mx + vx * t + MX, gy = my + vy * t + MY; // in the picture drawn
                int x0 = Mathf.Max(0, Mathf.FloorToInt(gx - rad - 1f)), x1 = Mathf.Min(CW - 1, Mathf.CeilToInt(gx + rad + 1f));
                int gy0 = Mathf.Max(y0, Mathf.FloorToInt(gy - rad - 1f)), gy1 = Mathf.Min(y1 - 1, Mathf.CeilToInt(gy + rad + 1f));
                for (int y = gy0; y <= gy1; y++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        float ddx = x - gx, ddy = y - gy;
                        float d = Mathf.Sqrt(ddx * ddx + ddy * ddy);
                        if (d >= rad)
                        {
                            continue;
                        }
                        // A soft disc, brighter at its edge.
                        float disc = Mathf.Clamp01((rad - d) / 2.5f) * (0.6f + 0.4f * (d / rad)) * a;
                        int k = (y * CW + x) * 3;
                        acc[k] += disc * Ghosts[gi, 2];
                        acc[k + 1] += disc * Ghosts[gi, 3];
                        acc[k + 2] += disc * Ghosts[gi, 4];
                    }
                }
            }
        }

        // ---- I: water drops (screen_effects.py make_drops, drop_field, shade_drops) ----------------------------

        private const int S = 3; // supersampled, for clean edges and highlights

        private void MakeDrops(int seed)
        {
            var rng = new System.Random(seed);
            float U(float a, float b) => a + (b - a) * (float)rng.NextDouble();
            var drops = new List<float[]>(); // cx, cy, r, e, trail, wobble (eye-view pixels)
            for (int n = 0; n < 13; n++)
            {
                float cx, cy;
                do
                {
                    cx = U(12f, W - 12f);
                    cy = U(12f, H - 12f);
                }
                while ((cx - CX) / W * ((cx - CX) / W) + (cy - CY) / H * ((cy - CY) / H) <= 0.04f); // toward the edges
                float r = U(4.5f, 11f);
                bool running = rng.NextDouble() < 0.4;
                float trail = running ? U(35f, 100f) : 0f;
                drops.Add(new[] { cx, cy, r, running ? 1.1f : U(0.88f, 1.02f), trail, U(0.5f, 2f) });
                if (running)
                {
                    for (int m = 0; m < (int)(trail / 16f); m++)
                    {
                        drops.Add(new[] { cx + U(-1.5f, 1.5f), cy - r * 2.2f - U(4f, trail), U(1f, 2f), 1f, 0f, 0f });
                    }
                }
            }
            for (int n = 0; n < 70; n++)
            {
                drops.Add(new[] { U(0f, W), U(0f, H), U(0.8f, 2.2f), U(0.9f, 1.1f), 0f, 0f });
            }
            // The same drops in both eyes, shifted as if at one fixed depth, so the eyes never disagree.
            _dropField = _dropField ?? new float[CW * S * CH * S];
            _dropShade = _dropShade ?? new float[CW * S * CH * S * 3];
            for (int e = 0; e < 2; e++)
            {
                float shift = e == 0 ? 2.5f : -2.5f;
                DropField(drops, shift, _dropField);
                ShadeDrops(_dropField, _dropShade, _drops[e]);
            }
        }

        /// <summary>The drops' height field over the whole picture drawn (placed in the view; ones over its edge go on past it).</summary>
        private static void DropField(List<float[]> drops, float shift, float[] hf)
        {
            int w = CW * S, h = CH * S;
            Array.Clear(hf, 0, hf.Length);
            foreach (float[] d in drops)
            {
                float cx = (d[0] + shift + MX) * S, cy = (d[1] + MY) * S, r = d[2] * S, e = d[3], trail = d[4] * S, wob = d[5];
                // The drop: a dome.
                for (int y = Mathf.Max(0, (int)(cy - r * e) - 1); y <= Mathf.Min(h - 1, (int)(cy + r * e) + 1); y++)
                {
                    for (int x = Mathf.Max(0, (int)(cx - r) - 1); x <= Mathf.Min(w - 1, (int)(cx + r) + 1); x++)
                    {
                        float u = (x - cx) / r, v = (y - cy) / (r * e);
                        float body = Mathf.Sqrt(Mathf.Clamp01(1f - u * u - v * v)) * r * 0.6f;
                        int i = y * w + x;
                        if (body > hf[i])
                        {
                            hf[i] = body;
                        }
                    }
                }
                if (trail <= 0f)
                {
                    continue;
                }
                // A runner: a teardrop tail above it, then the thin, slightly uneven wet trail it leaves.
                float tail = r * 2.2f;
                int yTop = Mathf.Max(0, (int)(cy - tail - trail) - 1), yBottom = Mathf.Min(h - 1, (int)cy);
                float reach = r + wob * S + 0.32f * r * 3f + 2f;
                for (int y = yTop; y <= yBottom; y++)
                {
                    float off = wob * S * Mathf.Sin((cy - y) / (9f * S));
                    bool inTail = y < cy && y > cy - tail;
                    bool inFilm = y < cy - tail && y > cy - tail - trail;
                    if (!inTail && !inFilm)
                    {
                        continue;
                    }
                    float t = Mathf.Clamp01((cy - y) / tail);
                    float hw = r * Mathf.Pow(1f - t, 1.25f) + 1e-3f;
                    float tt = Mathf.Clamp01((cy - tail - y) / trail);
                    float filmK = (1f - tt) * 0.9f * S * (0.75f + 0.25f * Mathf.Sin(y / (3f * S)));
                    for (int x = Mathf.Max(0, (int)(cx + off - reach)); x <= Mathf.Min(w - 1, (int)(cx + off + reach)); x++)
                    {
                        int i = y * w + x;
                        float value = 0f;
                        if (inTail)
                        {
                            float q = (x - cx - off) / hw;
                            value = Mathf.Sqrt(Mathf.Clamp01(1f - q * q)) * r * 0.5f * Mathf.Pow(1f - t, 0.7f);
                        }
                        else
                        {
                            float z = (x - cx - off) / (0.32f * r);
                            value = Mathf.Exp(-z * z) * filmK;
                        }
                        if (value > hf[i])
                        {
                            hf[i] = value;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Rain on a window at night: only the light the water catches. Fresnel reflection of a soft light above-left (the
        /// highlight and the bright rim), light refracted through each drop gathering at its lower inside edge (the
        /// caustic), and a faint body; toned, then brought down to the eye view.
        /// </summary>
        private static void ShadeDrops(float[] hf, float[] big, float[] outRgb)
        {
            int w = CW * S, h = CH * S;
            Array.Clear(big, 0, big.Length);
            var ld = new Vector3(-0.42f, -0.72f, 0.55f).normalized;
            float wetMin = 0.02f * S;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (hf[i] <= wetMin)
                    {
                        continue;
                    }
                    // np.gradient: central differences, one-sided at the edges.
                    float gx = x == 0 ? hf[i + 1] - hf[i] : x == w - 1 ? hf[i] - hf[i - 1] : (hf[i + 1] - hf[i - 1]) * 0.5f;
                    float gy = y == 0 ? hf[i + w] - hf[i] : y == h - 1 ? hf[i] - hf[i - w] : (hf[i + w] - hf[i - w]) * 0.5f;
                    float len = Mathf.Sqrt(gx * gx + gy * gy + 1f);
                    float nx = -gx / len, ny = -gy / len, nz = 1f / len;
                    float wet = Mathf.Clamp01(hf[i] / (0.6f * S));
                    float fres = 0.02f + 0.98f * Mathf.Pow(1f - nz, 5f);
                    float rx = 2f * nz * nx, ry = 2f * nz * ny, rz = 2f * nz * nz - 1f;
                    float c = rx * ld.x + ry * ld.y + rz * ld.z;
                    float env = 45f * Mathf.Exp(-(1f - c) / 0.003f) + 7f * Mathf.Exp(-(1f - c) / 0.02f) + 1.2f * Mathf.Clamp01(-ry);
                    float refl = fres * env;
                    float rim = Mathf.Pow(1f - nz, 2.5f) * (0.35f + 0.45f * Mathf.Clamp01(-ny));
                    float caus = Mathf.Pow(Mathf.Clamp01(ny), 1.6f) * Mathf.Pow(1f - nz, 0.4f) * 1.1f;
                    float lum = refl + rim + caus + 0.06f * wet;
                    int k = i * 3;
                    big[k] = ToneSe(lum * 0.92f + caus * 0.06f);
                    big[k + 1] = ToneSe(lum * 0.96f + caus * 0.03f);
                    big[k + 2] = ToneSe(lum * 1.0f);
                }
            }
            // Down to the picture drawn (3 x 3 into 1).
            for (int y = 0; y < CH; y++)
            {
                for (int x = 0; x < CW; x++)
                {
                    float r = 0f, g = 0f, b = 0f;
                    for (int sy = 0; sy < S; sy++)
                    {
                        int row = (y * S + sy) * w;
                        for (int sx = 0; sx < S; sx++)
                        {
                            int k = (row + x * S + sx) * 3;
                            r += big[k];
                            g += big[k + 1];
                            b += big[k + 2];
                        }
                    }
                    int o = (y * CW + x) * 3;
                    outRgb[o] = r / (S * S);
                    outRgb[o + 1] = g / (S * S);
                    outRgb[o + 2] = b / (S * S);
                }
            }
        }

        // ---- shared ------------------------------------------------------------------------------------------------

        /// <summary>flash_sim.py tone: a soft roll-off to 94% of white (the flash alone never goes past it).</summary>
        private static float ToneFlash(float v) => v <= 0f ? 0f : 0.94f * (1f - ExpNeg(v * 1.32f));

        /// <summary>screen_effects.py tone: 1 - exp(-1.25 x).</summary>
        private static float ToneSe(float v) => v <= 0f ? 0f : 1f - ExpNeg(v * 1.25f);

        private static byte To8(float v) => v <= 0f ? (byte)0 : v >= 1f ? (byte)255 : (byte)(v * 255f + 0.5f);

        /// <summary>exp(-q) for q >= 0, from a table.</summary>
        private static float ExpNeg(float q)
        {
            if (q >= ExpMax)
            {
                return 0f;
            }
            float f = q * (ExpSteps / ExpMax);
            int i = (int)f;
            float t = f - i;
            return ExpTable[i] + (ExpTable[i + 1] - ExpTable[i]) * t;
        }

        /// <summary>The table index of atan2(dy, dx) + the phase (as table steps).</summary>
        private static int AngleIndex(float dy, float dx, float phaseSteps)
        {
            float a = FastAtan2(dy, dx);
            int i = (int)(a * (AngleSteps / (2f * Mathf.PI)) + phaseSteps + AngleSteps * 4f + 0.5f);
            return i & (AngleSteps - 1);
        }

        private static float FastAtan2(float y, float x)
        {
            float ax = Mathf.Abs(x), ay = Mathf.Abs(y);
            float mx = Mathf.Max(ax, ay), mn = Mathf.Min(ax, ay);
            if (mx < 1e-12f)
            {
                return 0f;
            }
            float a = mn / mx, s = a * a;
            float r = ((-0.0464964749f * s + 0.15931422f) * s - 0.327622764f) * s * a + a;
            if (ay > ax)
            {
                r = 1.57079637f - r;
            }
            if (x < 0f)
            {
                r = 3.14159274f - r;
            }
            return y < 0f ? -r : r;
        }

        private static void MakeTables()
        {
            if (_tables)
            {
                return;
            }
            _tables = true;
            for (int i = 0; i < ExpTable.Length; i++)
            {
                ExpTable[i] = Mathf.Exp(-i * (ExpMax / ExpSteps));
            }
            for (int i = 0; i < AngleSteps; i++)
            {
                float a = i * 2f * Mathf.PI / AngleSteps;
                // The plume seen end-on: ragged petals round the fire body.
                Petals[i] = 0.72f + 0.18f * Mathf.Sin(5f * a + 0.7f) * Mathf.Sin(3f * a + 2.1f) + 0.1f * Mathf.Sin(11f * a + 0.3f);
                Star[i] = Mathf.Pow(Mathf.Abs(Mathf.Cos(3f * (a + 0.26f))), 90f);
            }
            // The lens: a rounded rectangle (flash_sim.py lens_mask: inset a quarter pixel, corners 46 px), anti-aliased.
            const float x0 = 1f, y0 = 1f, x1 = W - 1.25f, y1 = H - 1.25f, rad = 46f;
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    float px = x + 0.5f, py = y + 0.5f;
                    float qx = Mathf.Max(x0 + rad - px, px - (x1 - rad), 0f), qy = Mathf.Max(y0 + rad - py, py - (y1 - rad), 0f);
                    float outside = Mathf.Sqrt(qx * qx + qy * qy) - rad;
                    float edge = Mathf.Min(px - x0, x1 - px, Mathf.Min(py - y0, y1 - py));
                    float sd = qx > 0f && qy > 0f ? outside : -edge;
                    Mask[y * W + x] = Mathf.Clamp01(0.5f - sd);
                }
            }
            for (int y = 0; y < CH; y++)
            {
                for (int x = 0; x < CW; x++)
                {
                    float vx = x - MX, vy = y - MY;
                    // screen_effects.py G: the red edge, with the lens's fall-off.
                    float r = Mathf.Sqrt(Sq((vx - CX) / (W * 0.5f)) + Sq((vy - CY) / (H * 0.5f)));
                    float lensv = Mathf.Pow(Mathf.Clamp01(1.25f - Mathf.Sqrt(Sq((vx - CX) / (W * 0.56f)) + Sq((vy - CY) / (H * 0.6f)))), 0.6f);
                    LowEdge[y * CW + x] = Mathf.Clamp01(r - 0.55f) * lensv * 0.55f;
                    // Past the headset's view: faded out (smoothly) over the last 24 pixels, no edge.
                    float fade = Mathf.Clamp01(Mathf.Min(Mathf.Min(x, CW - 1 - x), Mathf.Min(y, CH - 1 - y)) / 24f);
                    EdgeFade[y * CW + x] = fade * fade * (3f - 2f * fade);
                }
            }
        }

        private static float Sq(float v) => v * v;
    }

    /// <summary>
    /// One eye's quad of <see cref="LensEffects"/>: drawn only into its own eye (the mono one only into the autotest's
    /// pictures from the head), never into a scope's camera or the spectator view.
    /// </summary>
    internal sealed class LensEye : MonoBehaviour
    {
        public MeshRenderer Renderer;
        public int Eye;
        public Camera Head;
        public Texture Picture;
        private MaterialPropertyBlock _block;
        internal static readonly bool SinglePass = UnityEngine.XR.XRSettings.stereoRenderingMode != UnityEngine.XR.XRSettings.StereoRenderingMode.MultiPass;

        private void OnWillRenderObject()
        {
            Camera cam = Camera.current;
            if (Renderer == null || cam == null)
            {
                return;
            }
            bool show;
            Camera.MonoOrStereoscopicEye e = cam.stereoActiveEye;
            // The headset's cameras: the head's, and the UI camera that draws layer 5 (where these quads are) over it in
            // both eyes. Only the head's was recognised, and the head's skips layer 5: every eye got black, and nothing of
            // the effects (flash, hit, low health, drops) ever reached the headset.
            bool headset = cam == Head || cam.stereoEnabled && cam.stereoTargetEye != StereoTargetEyeMask.None;
            if (headset && SinglePass)
            {
                // Single-pass stereo draws each quad once for both eyes: the centred one only (one picture for both).
                show = Eye == 2;
            }
            else if (headset)
            {
                show = Eye == 0 ? e == Camera.MonoOrStereoscopicEye.Left : Eye == 1 ? e == Camera.MonoOrStereoscopicEye.Right : e == Camera.MonoOrStereoscopicEye.Mono;
            }
            else
            {
                show = Eye == 2 && e == Camera.MonoOrStereoscopicEye.Mono && Head != null && (cam.transform.position - Head.transform.position).sqrMagnitude < 1e-4f;
            }
            // The other eyes get black, which adds nothing (whatever tint the additive shader has, or lacks).
            _block = _block ?? new MaterialPropertyBlock();
            _block.SetTexture("_MainTex", show ? Picture : Texture2D.blackTexture);
            Renderer.SetPropertyBlock(_block);
        }
    }
}
