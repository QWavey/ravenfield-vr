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

namespace RavenfieldVR.Core
{
    /// <summary>
    /// Autotest: films what each eye of the headset gets, frame by frame (each eye's finished picture, read after the last
    /// camera drew into it: the UI camera, which draws the on-screen effects), into a folder of PNGs (L-0000.png, R-0000.png) with a frame log (frames.txt). Read back on the
    /// GPU without stalling and written on a thread of its own, so the game keeps its frame rate while it films.
    /// tools/flashfilm.py makes the videos and the per-frame strips.
    /// </summary>
    internal sealed class EyeFilm : MonoBehaviour
    {
        private const int Width = 480;

        private Camera _cam;
        private string _dir;
        private bool _recording;
        private int _pending;
        private readonly int[] _count = new int[2];
        private readonly StringBuilder _frames = new StringBuilder();
        private string _note = "";

        private Thread _writer;
        private readonly Queue<(string path, byte[] rgba, int w, int h)> _queue = new Queue<(string, byte[], int, int)>();
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private volatile bool _stop;

        /// <summary>Starts filming the eyes <paramref name="last"/> (the last camera to draw them) finishes, into <paramref name="dir"/> (emptied first).</summary>
        internal static EyeFilm Begin(Camera last, string dir) => Begin(last, dir, false);

        /// <summary>
        /// <paramref name="mono"/>: a close-up camera of its own (rendered by the caller with Camera.Render), filmed as one
        /// picture a frame (C-0000.png).
        /// </summary>
        internal static EyeFilm Begin(Camera last, string dir, bool mono)
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
            Directory.CreateDirectory(dir);
            EyeFilm f = last.gameObject.AddComponent<EyeFilm>();
            f._cam = last;
            f._mono = mono;
            f._dir = dir;
            f._recording = true;
            // Below the game's threads: the PNGs take a core of their own, and the frames filmed must not be slowed by them.
            Interlocked.Increment(ref Writing);
            f._writer = new Thread(f.WriterLoop) { IsBackground = true, Name = "RavenfieldVR eye film", Priority = System.Threading.ThreadPriority.BelowNormal };
            f._writer.Start();
            f._frames.AppendLine("frame\teye\ttime\tnote\tlens");
            return f;
        }

        /// <summary>Marks what happens on the frames that follow (a shot, the sight mode), into the frame log.</summary>
        internal void Note(string note)
        {
            _note = note;
        }

        /// <summary>No more frames (Finish still waits for the ones being written).</summary>
        internal void StopRecording()
        {
            _recording = false;
        }

        /// <summary>Stops filming, waits for the last frames to be read back and written, and writes the frame log.</summary>
        internal System.Collections.IEnumerator Finish()
        {
            _recording = false;
            // Every read-back back first (a slow one at the end was lost after 3 s and enqueued after the writer stopped).
            for (float until = Time.realtimeSinceStartup + 120f; _pending > 0 && Time.realtimeSinceStartup < until;)
            {
                yield return null;
            }
            // Every frame written before the writer stops (up to 2 minutes; a whole machine-gun magazine is 1400 pictures):
            // waited only 10 s, the game quit while the writer was still in Unity's PNG encoder, and crashed.
            for (float until = Time.realtimeSinceStartup + 120f; Queued > 0 && Time.realtimeSinceStartup < until;)
            {
                yield return null;
            }
            _stop = true;
            _wake.Set();
            if (!_writer.Join(15000))
            {
                Log.Warn("Eye film: the frame writer did not finish; the game waits for it before quitting.");
            }
            File.WriteAllText(Path.Combine(_dir, "frames.txt"), _frames.ToString());
            _copy?.Release();
            Destroy(this);
        }

        internal int Frames => _mono ? _count[0] : Mathf.Min(_count[0], _count[1]);

        private int Queued
        {
            get
            {
                lock (_queue)
                {
                    return _queue.Count;
                }
            }
        }

        /// <summary>Films still writing frames (the autotest waits for them before it quits the game).</summary>
        internal static int Writing;

        private CommandBuffer _copy;
        /// <summary>
        /// Picture buffers the writer is done with, for the next readbacks (under the queue's lock): each frame of each eye made a
        /// new one, about a megabyte, so the collector stopped the frames the film was measuring (round-5 scout). As many as can be
        /// in flight (the 48 of the writer's backlog, both eyes' pending, the one it is writing).
        /// </summary>
        private readonly Stack<byte[]> _spare = new Stack<byte[]>();
        private const int SpareMax = 56;
        private bool _mono;
        private int _skipFrame = -1;

        private void OnPostRender()
        {
            if (!_recording)
            {
                return;
            }
            Camera.MonoOrStereoscopicEye e = _cam.stereoActiveEye;
            if (e == Camera.MonoOrStereoscopicEye.Mono != _mono)
            {
                return;
            }
            int eye = _mono || e == Camera.MonoOrStereoscopicEye.Left ? 0 : 1;
            // The writer behind (half a megabyte a picture): this frame is left out, both eyes of it, instead of the queue
            // growing by tens of megabytes a second (85-agent review).
            if (_skipFrame == Time.frameCount)
            {
                return;
            }
            if (eye == 0 && Queued + _pending > 48)
            {
                _skipFrame = Time.frameCount;
                _frames.Append(Time.frameCount).Append('\t').AppendLine("skipped (the writer is behind)");
                return;
            }
            int sw = Mathf.Max(1, _cam.pixelWidth), sh = Mathf.Max(1, _cam.pixelHeight);
            int w = Width, h = Mathf.Max(2, Mathf.RoundToInt(Width * (float)sh / sw));
            RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            // The eye's picture as it is now (everything drawn), scaled down into ours.
            _copy = _copy ?? new CommandBuffer { name = "RavenfieldVR eye film" };
            _copy.Clear();
            _copy.Blit(BuiltinRenderTextureType.CurrentActive, rt);
            Graphics.ExecuteCommandBuffer(_copy);
            int n = _count[eye]++;
            string path = Path.Combine(_dir, (_mono ? "C-" : eye == 0 ? "L-" : "R-") + n.ToString("0000") + ".png");
            Rig.LensEffects lens = Rig.LensEffects.Instance;
            // Invariant numbers: on a comma-decimal Windows (German) the time read 12,3456 and tools/flashfilm.py failed on frames.txt.
            _frames.Append(Time.frameCount).Append('\t').Append(_mono ? 'C' : eye == 0 ? 'L' : 'R').Append('\t').Append(Time.unscaledTime.ToString("0.0000", CultureInfo.InvariantCulture))
                   .Append('\t').Append(_note).Append('\t').AppendLine(lens != null ? lens.FilmState(eye) : "-");
            _pending++;
            AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32, req =>
            {
                _pending--;
                if (!req.hasError)
                {
                    NativeArray<byte> data = req.GetData<byte>();
                    byte[] rgba = null;
                    lock (_queue)
                    {
                        // (exactly the picture's size: the encoder writes the whole array; one of another size is let go)
                        while (_spare.Count > 0 && rgba == null)
                        {
                            byte[] b = _spare.Pop();
                            rgba = b.Length == w * h * 4 ? b : null;
                        }
                    }
                    rgba = rgba ?? new byte[w * h * 4];
                    NativeArray<byte>.Copy(data, rgba, rgba.Length); // read back top row first (Direct3D): as the PNG wants it
                    lock (_queue)
                    {
                        _queue.Enqueue((path, rgba, w, h));
                    }
                    _wake.Set();
                }
                RenderTexture.ReleaseTemporary(rt);
            });
        }

        private void WriterLoop()
        {
            while (true)
            {
                (string path, byte[] rgba, int w, int h) item;
                bool have;
                lock (_queue)
                {
                    have = _queue.Count > 0;
                    item = have ? _queue.Dequeue() : default;
                }
                if (!have)
                {
                    if (_stop)
                    {
                        Interlocked.Decrement(ref Writing);
                        return;
                    }
                    _wake.WaitOne(200);
                    continue;
                }
                try
                {
                    byte[] png = ImageConversion.EncodeArrayToPNG(item.rgba, GraphicsFormat.R8G8B8A8_UNorm, (uint)item.w, (uint)item.h);
                    lock (_queue)
                    {
                        if (_spare.Count < SpareMax)
                        {
                            _spare.Push(item.rgba); // (encoded: free for the next readback)
                        }
                    }
                    File.WriteAllBytes(item.path, png);
                }
                catch (Exception ex)
                {
                    Log.WarnOnce("eyefilm-write", "Eye film frame not written: " + ex.Message);
                }
            }
        }

        private void OnDestroy()
        {
            _stop = true;
            _wake.Set();
        }
    }
}
