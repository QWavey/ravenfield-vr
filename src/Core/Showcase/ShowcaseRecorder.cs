using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace RavenfieldVR.Core.Showcase
{
    /// <summary>
    /// The showcase's recorder (docs/Showcase-Plan.md), one of two passes of the same scene:
    /// <list type="bullet">
    /// <item>the picture pass: the game stepped frame by frame at a fixed rate (Time.captureFramerate), each frame drawn by the showcase
    /// camera into a picture of its own (1920x1080) and read back on the GPU, written as a JPEG by worker threads: smooth at its rate
    /// however slowly the PC draws it;</item>
    /// <item>the sound pass: the same scene played again in real time, what the listener hears (the game's whole mix, the music muted)
    /// written to audio.wav, each frame's mark timed from its start. Unity's own offline sound recording (AudioRenderer) stops this
    /// game's mixer altogether (its listener had no audio block during a take, and 93 in 2 s before one; 2026-11-08), so picture and
    /// sound are taken apart and put together by their marks (tools/showcase_take.py --mux), as a film's sound is.</item>
    /// </list>
    /// Both follow the scene's one schedule (<see cref="Planned"/>), so a moment is at the same second of either. Only with -rfvr-showcase.
    /// </summary>
    internal sealed class ShowcaseRecorder
    {
        internal const int Fps = 60;

        private readonly string _dir;
        private readonly int _w, _h;
        private readonly StringBuilder _log = new StringBuilder();
        private int _frame, _pending;
        private bool _recording;
        private float _startTime;

        /// <summary>The sound pass: no pictures, the listener recorded in real time.</summary>
        internal readonly bool Sound;

        // The picture pass's camera and writers: a JPEG of 1920x1080 is 15-30 ms of a core; three keep up with the read-backs.
        private readonly Camera _cam;
        private readonly RenderTexture _rt;
        private readonly Thread[] _writers;
        private readonly Queue<(string path, byte[] rgba)> _queue = new Queue<(string, byte[])>();
        private readonly Stack<byte[]> _spare = new Stack<byte[]>();
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private volatile bool _stop;
        private int _writing;

        // The sound pass: the listener's blocks, written by a thread of its own.
        private ShowcaseSoundTap _tap;
        private float _music = float.NaN, _sting = float.NaN;

        /// <summary>Where the camera is and what it sees this frame: set by the scene (<see cref="ShowcaseCamera"/>).</summary>
        internal Func<ShowcaseCamera.Shot> Shot;

        /// <summary>The scene's and shot's name for the frame log, so the edit finds each moment by name.</summary>
        internal string Mark = "";

        /// <summary>
        /// The scene's schedule: seconds of the take its filmed steps have planned so far. Each step ends at its planned second, not a
        /// frame after the last one's, so the real-time sound pass does not drift from the picture's whole frames.
        /// </summary>
        internal double Planned;

        internal int Frame => _frame;
        internal string Dir => _dir;

        /// <summary>Seconds of the take so far: the frames filmed at the fixed rate (picture), game time from its start (sound).</summary>
        internal float Elapsed => Sound ? Time.time - _startTime : _frame / (float)Fps;

        internal ShowcaseRecorder(string dir, bool sound, int width = 1920, int height = 1080)
        {
            _dir = dir;
            Sound = sound;
            _w = width;
            _h = height;
            if (sound)
            {
                Directory.CreateDirectory(dir);
                _log.AppendLine("frame\ttime\tmark");
                return;
            }
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
            Directory.CreateDirectory(dir);
            var go = new GameObject("RavenfieldVR Showcase Camera");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _cam = go.AddComponent<Camera>();
            _cam.enabled = false;
            _rt = new RenderTexture(_w, _h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 8, name = "RavenfieldVR Showcase" };
            _rt.Create();
            _writers = new Thread[3];
            for (int i = 0; i < _writers.Length; i++)
            {
                _writers[i] = new Thread(WriterLoop) { IsBackground = true, Name = "RavenfieldVR showcase writer " + i, Priority = System.Threading.ThreadPriority.BelowNormal };
                _writers[i].Start();
            }
            _log.AppendLine("frame\ttime\tmark\tcamera");
        }

        /// <summary>Starts the take: the picture pass at the fixed frame rate; the sound pass in real time into audio.wav. The music muted.</summary>
        internal void Start()
        {
            MuteMusic(true);
            AudioListener.pause = false;
            AudioListener.volume = 1f;
            _startTime = Time.time;
            Planned = 0;
            if (Sound)
            {
                Time.captureFramerate = 0;
                AudioListener listener = UnityEngine.Object.FindObjectsOfType<AudioListener>().FirstOrDefault(l => l != null && l.isActiveAndEnabled);
                if (listener == null)
                {
                    Log.Warn("Showcase: no live audio listener: the sound pass records nothing.");
                }
                else
                {
                    _tap = listener.gameObject.AddComponent<ShowcaseSoundTap>();
                    _tap.Begin(Path.Combine(_dir, "audio.wav"), AudioSettings.outputSampleRate);
                }
            }
            else
            {
                // One physics step a frame, every frame. The game steps physics at 60 Hz, the take's rate: with the clock on a step's
                // boundary the frames took 0 and 2 steps by turns, and the gun (a physics body) fell behind and caught up every other
                // frame while walking: the gun cam and the arms on it jumped 66 px a frame (take 8, the user: "theres shaking in the cam
                // when the player walks"). Near a boundary the clock is moved half a step by one frame at twice the rate.
                float phase = Time.fixedDeltaTime > 0f ? (Time.time - Time.fixedTime) / Time.fixedDeltaTime : 0.5f;
                _halfStep = phase < 0.25f || phase > 0.75f;
                _startFrame = Time.frameCount;
                Time.captureFramerate = _halfStep ? Fps * 2 : Fps;
                Log.Info($"Showcase: the physics phase at the take's start {phase:0.00} of a step{(_halfStep ? ": moved half a step" : "")}.");
                _lastFixed = -1f;
            }
            _recording = true;
        }

        private bool _halfStep;
        private int _startFrame, _uneven;
        private float _lastFixed;

        /// <summary>
        /// One frame of the take, after the frame's drawing (the scene calls it from a WaitForEndOfFrame): the picture pass draws and
        /// reads back the camera's picture (waiting, the game held, while the writers are far behind); both log the frame's mark and time.
        /// </summary>
        internal void TakeFrame()
        {
            if (!_recording)
            {
                return;
            }
            string camName = "-";
            float at = Elapsed;
            if (!Sound)
            {
                if (_halfStep && Time.frameCount > _startFrame)
                {
                    _halfStep = false;
                    Time.captureFramerate = Fps; // (the half step taken: the take's own rate from the next frame)
                }
                else if (_lastFixed >= 0f && Time.fixedDeltaTime > 0f && Mathf.RoundToInt((Time.fixedTime - _lastFixed) / Time.fixedDeltaTime) != 1)
                {
                    _uneven++; // (a frame with no physics step or more than one: the gun jumps)
                }
                _lastFixed = Time.fixedTime;
                for (int guard = 0; Backlog > 24 && guard < 4000; guard++)
                {
                    Thread.Sleep(5); // (its time is the capture's, not the clock's: nothing is lost but wall time)
                }
                ShowcaseCamera.Shot shot = Shot != null ? Shot() : ShowcaseCamera.Shot.None;
                if (shot.Valid)
                {
                    camName = shot.Name;
                    ShowcaseCamera.Render(_cam, shot, _rt);
                    string path = Path.Combine(_dir, "f" + _frame.ToString("00000") + ".jpg");
                    _pending++;
                    AsyncGPUReadback.Request(_rt, 0, TextureFormat.RGBA32, req => Readback(req, path));
                }
            }
            _log.Append(_frame).Append('\t').Append(at.ToString("0.0000", CultureInfo.InvariantCulture)).Append('\t').Append(Mark);
            if (!Sound)
            {
                _log.Append('\t').Append(camName).Append('\t').Append(Probe());
            }
            _log.AppendLine();
            _frame++;
        }

        /// <summary>
        /// Where things are this frame, for the frame log (the gun cam shook while walking, takes 8-9: the player's whole unit stepped
        /// against the world by turns): the head, the rig's root, the actor, the gun (world, cm), the clock's place in its physics step.
        /// </summary>
        private static string Probe()
        {
            var inv = CultureInfo.InvariantCulture;
            string V(Vector3 v) => (v.x * 100f).ToString("0.0", inv) + "," + (v.y * 100f).ToString("0.0", inv) + "," + (v.z * 100f).ToString("0.0", inv);
            Rig.VRRig rig = Rig.VRRig.Instance;
            Actor a = LocalPlayer.actor;
            Weapons.HeldWeapon held = Weapons.WeaponSystem.Instance != null ? Weapons.WeaponSystem.Instance.Held : null;
            float phase = Time.fixedDeltaTime > 0f ? (float)((Time.timeAsDouble - Time.fixedTimeAsDouble) / Time.fixedDeltaTime) : 0f;
            return "head " + (rig != null && rig.Head != null ? V(rig.Head.position) : "-")
                   + " root " + (rig != null ? V(rig.Root.position) : "-")
                   + " actor " + (a != null ? V(a.transform.position) : "-")
                   + " gun " + (held != null && held.Rig.GunBody != null ? V(held.Rig.GunBody.position) : "-")
                   + " phase " + phase.ToString("0.000", inv);
        }

        private void Readback(AsyncGPUReadbackRequest req, string path)
        {
            _pending--;
            if (req.hasError)
            {
                Log.WarnOnce("showcase-readback", "Showcase: a frame's read-back failed.");
                return;
            }
            NativeArray<byte> data = req.GetData<byte>();
            byte[] rgba = null;
            lock (_queue)
            {
                while (_spare.Count > 0 && rgba == null)
                {
                    byte[] b = _spare.Pop();
                    rgba = b.Length == data.Length ? b : null;
                }
            }
            rgba = rgba ?? new byte[data.Length];
            NativeArray<byte>.Copy(data, rgba, rgba.Length);
            lock (_queue)
            {
                _queue.Enqueue((path, rgba));
            }
            _wake.Set();
        }

        /// <summary>Ends the take: the frame rate and the music back, the sound file closed, every frame written (waited for), the logs.</summary>
        internal System.Collections.IEnumerator Finish()
        {
            _recording = false;
            Time.captureFramerate = 0;
            MuteMusic(false);
            if (Sound)
            {
                long samples = 0;
                int rate = AudioSettings.outputSampleRate;
                if (_tap != null)
                {
                    samples = _tap.End();
                    UnityEngine.Object.Destroy(_tap);
                }
                File.WriteAllText(Path.Combine(_dir, "sound.txt"), _log.ToString());
                Log.Info($"Showcase sound pass written: {_frame} frames over {Elapsed:0.0} s, {samples / (float)Mathf.Max(1, rate):0.0} s of sound"
                         + $" (peak {(_tap != null ? _tap.Peak : 0f):0.000}), in {_dir}.");
                yield break;
            }
            for (float until = Time.realtimeSinceStartup + 120f; (_pending > 0 || Backlog > 0) && Time.realtimeSinceStartup < until;)
            {
                yield return null;
            }
            _stop = true;
            _wake.Set();
            foreach (Thread t in _writers)
            {
                if (!t.Join(20000))
                {
                    Log.Warn("Showcase: a frame writer did not finish in time.");
                }
            }
            File.WriteAllText(Path.Combine(_dir, "shots.txt"), _log.ToString());
            File.WriteAllText(Path.Combine(_dir, "take.txt"), $"fps\t{Fps}\nframes\t{_frame}\nwidth\t{_w}\nheight\t{_h}\n");
            _rt.Release();
            UnityEngine.Object.Destroy(_rt);
            UnityEngine.Object.Destroy(_cam.gameObject);
            Log.Info($"Showcase picture pass written: {_frame} frames at {Fps} fps ({_frame / (float)Fps:0.0} s), in {_dir}; "
                     + (_uneven == 0 ? "one physics step every frame." : $"{_uneven} FRAME(S) WITHOUT EXACTLY ONE PHYSICS STEP (the gun jumps on them)."));
        }

        private int Backlog
        {
            get
            {
                lock (_queue)
                {
                    return _queue.Count + _writing;
                }
            }
        }

        private void WriterLoop()
        {
            while (true)
            {
                (string path, byte[] rgba) item;
                bool have;
                lock (_queue)
                {
                    have = _queue.Count > 0;
                    item = have ? _queue.Dequeue() : default;
                    if (have)
                    {
                        _writing++;
                    }
                }
                if (!have)
                {
                    if (_stop)
                    {
                        return;
                    }
                    _wake.WaitOne(100);
                    continue;
                }
                try
                {
                    byte[] jpg = ImageConversion.EncodeArrayToJPG(item.rgba, GraphicsFormat.R8G8B8A8_UNorm, (uint)_w, (uint)_h, 0, 95);
                    File.WriteAllBytes(item.path, jpg);
                }
                catch (Exception ex)
                {
                    Log.WarnOnce("showcase-write", "Showcase: a frame was not written: " + ex.Message);
                }
                finally
                {
                    lock (_queue)
                    {
                        _writing--;
                        if (_spare.Count < 32)
                        {
                            _spare.Push(item.rgba);
                        }
                    }
                }
            }
        }

        /// <summary>The game's music and its stings silent for the take (their own mixers), then as they were.</summary>
        private void MuteMusic(bool mute)
        {
            Options o = Options.instance;
            if (o == null)
            {
                return;
            }
            try
            {
                if (mute)
                {
                    if (o.musicAudioMixer != null && o.musicAudioMixer.GetFloat("volume", out float m))
                    {
                        _music = m;
                        o.musicAudioMixer.SetFloat("volume", -80f);
                    }
                    if (o.musicStingAudioMixer != null && o.musicStingAudioMixer.GetFloat("volume", out float s))
                    {
                        _sting = s;
                        o.musicStingAudioMixer.SetFloat("volume", -80f);
                    }
                }
                else
                {
                    if (!float.IsNaN(_music))
                    {
                        o.musicAudioMixer?.SetFloat("volume", _music);
                    }
                    if (!float.IsNaN(_sting))
                    {
                        o.musicStingAudioMixer?.SetFloat("volume", _sting);
                    }
                }
            }
            catch (Exception e)
            {
                Log.WarnOnce("showcase-music", "Showcase: the music could not be muted: " + e.Message);
            }
        }
    }

    /// <summary>
    /// On the live listener: each block of the final mix (the audio thread's OnAudioFilterRead) copied into a queue and written to a
    /// 32-bit float WAV by a thread of its own. Passes the sound through unchanged.
    /// </summary>
    internal sealed class ShowcaseSoundTap : MonoBehaviour
    {
        private readonly Queue<float[]> _blocks = new Queue<float[]>();
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private Thread _writer;
        private volatile bool _on, _stop;
        private volatile int _channels = 2;
        private FileStream _file;
        private BinaryWriter _out;
        private int _rate;
        private long _samples;
        private float _peak;

        internal float Peak => _peak;

        internal void Begin(string path, int rate)
        {
            _rate = rate;
            _file = new FileStream(path, FileMode.Create, FileAccess.Write);
            _out = new BinaryWriter(_file);
            WriteHeader(2, 0); // (rewritten at the end with the real channel count and length)
            _writer = new Thread(WriterLoop) { IsBackground = true, Name = "RavenfieldVR showcase sound" };
            _writer.Start();
            _on = true;
        }

        private void OnAudioFilterRead(float[] data, int channels)
        {
            if (!_on)
            {
                return;
            }
            _channels = channels;
            var copy = new float[data.Length];
            Array.Copy(data, copy, data.Length);
            lock (_blocks)
            {
                _blocks.Enqueue(copy);
            }
            _wake.Set();
        }

        /// <summary>Stops recording and closes the file: the samples (per channel) written.</summary>
        internal long End()
        {
            if (_out == null)
            {
                return _samples;
            }
            _on = false;
            _stop = true;
            _wake.Set();
            _writer?.Join(10000);
            _out.Flush();
            _file.Position = 0;
            WriteHeader(Mathf.Max(1, _channels), _samples);
            _out.Close();
            _out = null;
            return _samples;
        }

        private void WriterLoop()
        {
            while (true)
            {
                float[] block = null;
                lock (_blocks)
                {
                    if (_blocks.Count > 0)
                    {
                        block = _blocks.Dequeue();
                    }
                }
                if (block == null)
                {
                    if (_stop)
                    {
                        return;
                    }
                    _wake.WaitOne(50);
                    continue;
                }
                foreach (float f in block)
                {
                    _out.Write(f);
                    float a = f < 0f ? -f : f;
                    if (a > _peak)
                    {
                        _peak = a;
                    }
                }
                _samples += block.Length / Mathf.Max(1, _channels);
            }
        }

        private void WriteHeader(int channels, long samples)
        {
            const int bytesPerSample = 4;
            long data = samples * channels * bytesPerSample;
            _out.Write(Encoding.ASCII.GetBytes("RIFF"));
            _out.Write((int)Math.Min(int.MaxValue, 36 + data));
            _out.Write(Encoding.ASCII.GetBytes("WAVE"));
            _out.Write(Encoding.ASCII.GetBytes("fmt "));
            _out.Write(16);
            _out.Write((short)3); // IEEE float
            _out.Write((short)channels);
            _out.Write(_rate);
            _out.Write(_rate * channels * bytesPerSample);
            _out.Write((short)(channels * bytesPerSample));
            _out.Write((short)(bytesPerSample * 8));
            _out.Write(Encoding.ASCII.GetBytes("data"));
            _out.Write((int)Math.Min(int.MaxValue, data));
        }

        private void OnDestroy() => End();
    }
}
