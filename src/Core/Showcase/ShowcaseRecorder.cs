using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace RavenfieldVR.Core.Showcase
{
    /// <summary>
    /// The showcase's recorder (docs/Showcase-Plan.md): the game stepped frame by frame at a fixed rate (Time.captureFramerate), each
    /// frame drawn by the showcase camera into a picture of its own (1920x1080) and read back on the GPU, written as a JPEG by worker
    /// threads; the game's own sound rendered in the same lock-step (AudioRenderer, as Unity's recorder does it) into a WAV. So the
    /// clip runs smooth at its rate however slowly the PC draws it, and picture and sound stay together to the sample. The game's
    /// music is muted for the take (the user: "without music only ingame sounds"). Only with -rfvr-showcase.
    /// </summary>
    internal sealed class ShowcaseRecorder
    {
        internal const int Fps = 60;

        private readonly string _dir;
        private readonly int _w, _h;
        private readonly Camera _cam;
        private readonly RenderTexture _rt;
        private readonly StringBuilder _log = new StringBuilder();
        private int _frame, _pending;
        private bool _recording;

        // The writers: a JPEG of 1920x1080 is 15-30 ms of a core; three keep up with the read-backs.
        private readonly Thread[] _writers = new Thread[3];
        private readonly Queue<(string path, byte[] rgba)> _queue = new Queue<(string, byte[])>();
        private readonly Stack<byte[]> _spare = new Stack<byte[]>();
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private volatile bool _stop;
        private int _writing;

        // The sound: float samples of the game's mix, written as they come (a 32-bit float WAV, its sizes set at the end).
        private FileStream _wav;
        private BinaryWriter _wavOut;
        private long _samples;
        private int _channels, _rate;
        private float _music = float.NaN, _sting = float.NaN;

        /// <summary>Where the camera is and what it sees this frame: set by the scene each frame (<see cref="ShowcaseCamera"/>).</summary>
        internal Func<ShowcaseCamera.Shot> Shot;

        /// <summary>The scene's and shot's name for the frame log (shots.txt), so the edit finds each moment by name.</summary>
        internal string Mark = "";

        internal int Frame => _frame;
        internal string Dir => _dir;

        internal ShowcaseRecorder(string dir, int width = 1920, int height = 1080)
        {
            _dir = dir;
            _w = width;
            _h = height;
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
            for (int i = 0; i < _writers.Length; i++)
            {
                _writers[i] = new Thread(WriterLoop) { IsBackground = true, Name = "RavenfieldVR showcase writer " + i, Priority = System.Threading.ThreadPriority.BelowNormal };
                _writers[i].Start();
            }
            _log.AppendLine("frame\ttime\tmark\tcamera");
        }

        /// <summary>Starts the take: the fixed frame rate, the sound into audio.wav, the music muted.</summary>
        internal void Start()
        {
            Time.captureFramerate = Fps;
            MuteMusic(true);
            _channels = AudioSettings.speakerMode == AudioSpeakerMode.Mono ? 1 : 2;
            _rate = AudioSettings.outputSampleRate;
            _wav = new FileStream(Path.Combine(_dir, "audio.wav"), FileMode.Create, FileAccess.Write);
            _wavOut = new BinaryWriter(_wav);
            WriteWavHeader(0);
            bool started = AudioRenderer.Start();
            _recording = true;
            // The sound taken in a component's Update, as Unity's own example does (taken at the end of the frame it came back empty: 0
            // samples for a whole take, 2026-11-08); said once with what the audio system is.
            _tap = _cam.gameObject.AddComponent<ShowcaseAudioTap>();
            _tap.Rec = this;
            Log.Info($"Showcase: sound recording {(started ? "started" : "NOT STARTED")} ({_rate} Hz, {_channels} channel(s), speaker mode "
                     + $"{AudioSettings.speakerMode}, capture at {Time.captureFramerate} fps, DSP buffer {AudioSettings.GetConfiguration().dspBufferSize}).");
        }

        private ShowcaseAudioTap _tap;
        private int _soundCalls;

        /// <summary>
        /// One frame of the take, after the frame's own drawing (the scene calls it from a WaitForEndOfFrame): the camera drawn into the
        /// picture, its read-back started, and the frame's sound taken. Waits (real time, the game held) while the writers are far behind.
        /// </summary>
        internal void TakeFrame()
        {
            if (!_recording)
            {
                return;
            }
            // The writers behind: the game waits (its time is the capture's, not the clock's, so nothing is lost but wall time).
            for (int guard = 0; Backlog > 24 && guard < 4000; guard++)
            {
                Thread.Sleep(5);
            }
            ShowcaseCamera.Shot shot = Shot != null ? Shot() : ShowcaseCamera.Shot.None;
            if (shot.Valid)
            {
                ShowcaseCamera.Render(_cam, shot, _rt);
                int n = _frame;
                string path = Path.Combine(_dir, "f" + n.ToString("00000") + ".jpg");
                _pending++;
                AsyncGPUReadback.Request(_rt, 0, TextureFormat.RGBA32, req =>
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
                });
            }
            _log.Append(_frame).Append('\t').Append(Time.time.ToString("0.0000", CultureInfo.InvariantCulture)).Append('\t').Append(Mark).Append('\t')
                .AppendLine(shot.Valid ? shot.Name : "-");
            _frame++;
        }

        /// <summary>The frame's share of the game's sound (from <see cref="ShowcaseAudioTap"/>'s Update), appended to the WAV.</summary>
        internal void TakeSound()
        {
            if (!_recording || _wavOut == null)
            {
                return;
            }
            // The count is what came since the last Render: 0 until the first one, so a frame's worth is rendered when it says 0 (it
            // said 0 for a whole take while Render was only called on more; 2026-11-08).
            int counted = AudioRenderer.GetSampleCountForCaptureFrame();
            int n = counted > 0 ? counted : Mathf.RoundToInt(_rate / (float)Fps);
            var buffer = new NativeArray<float>(n * _channels, Allocator.Temp);
            try
            {
                bool ok = AudioRenderer.Render(buffer);
                if (_soundCalls++ < 5)
                {
                    float peak = 0f;
                    for (int i = 0; i < buffer.Length; i++)
                    {
                        peak = Mathf.Max(peak, Mathf.Abs(buffer[i]));
                    }
                    Log.Info($"Showcase: sound frame {_soundCalls}: {counted} counted, {n} taken, render {(ok ? "ok" : "FAILED")}, peak {peak:0.000}.");
                }
                if (ok)
                {
                    for (int i = 0; i < buffer.Length; i++)
                    {
                        _wavOut.Write(buffer[i]);
                    }
                    _samples += n;
                }
            }
            finally
            {
                buffer.Dispose();
            }
        }

        /// <summary>Ends the take: the frame rate and the music back, the sound file closed, every frame written (waited for), the frame log.</summary>
        internal System.Collections.IEnumerator Finish()
        {
            _recording = false;
            if (_tap != null)
            {
                UnityEngine.Object.Destroy(_tap);
            }
            try
            {
                AudioRenderer.Stop();
            }
            catch (Exception e)
            {
                Log.WarnOnce("showcase-audio-stop", "Showcase: the sound recording did not stop cleanly: " + e.Message);
            }
            Time.captureFramerate = 0;
            MuteMusic(false);
            if (_wavOut != null)
            {
                _wavOut.Flush();
                _wav.Position = 0;
                WriteWavHeader(_samples);
                _wavOut.Close();
                _wavOut = null;
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
            File.WriteAllText(Path.Combine(_dir, "take.txt"),
                $"fps\t{Fps}\nframes\t{_frame}\nwidth\t{_w}\nheight\t{_h}\naudio_rate\t{_rate}\naudio_channels\t{_channels}\naudio_samples\t{_samples}\n");
            _rt.Release();
            UnityEngine.Object.Destroy(_rt);
            UnityEngine.Object.Destroy(_cam.gameObject);
            Log.Info($"Showcase take written: {_frame} frames at {Fps} fps ({_frame / (float)Fps:0.0} s), sound {_samples / (float)Mathf.Max(1, _rate):0.0} s, in {_dir}.");
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

        private void WriteWavHeader(long samples)
        {
            int bytesPerSample = 4;
            long data = samples * _channels * bytesPerSample;
            _wavOut.Write(Encoding.ASCII.GetBytes("RIFF"));
            _wavOut.Write((int)Mathf.Min(int.MaxValue, 36 + data));
            _wavOut.Write(Encoding.ASCII.GetBytes("WAVE"));
            _wavOut.Write(Encoding.ASCII.GetBytes("fmt "));
            _wavOut.Write(16);
            _wavOut.Write((short)3); // IEEE float
            _wavOut.Write((short)_channels);
            _wavOut.Write(_rate);
            _wavOut.Write(_rate * _channels * bytesPerSample);
            _wavOut.Write((short)(_channels * bytesPerSample));
            _wavOut.Write((short)(bytesPerSample * 8));
            _wavOut.Write(Encoding.ASCII.GetBytes("data"));
            _wavOut.Write((int)Mathf.Min(int.MaxValue, data));
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

    /// <summary>Takes the recorder's sound each frame in Update (<see cref="ShowcaseRecorder.TakeSound"/>).</summary>
    internal sealed class ShowcaseAudioTap : MonoBehaviour
    {
        internal ShowcaseRecorder Rec;

        private void Update() => Rec?.TakeSound();
    }
}
