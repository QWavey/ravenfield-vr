using System;
using System.Collections.Generic;
using RavenfieldVR.Core;
using UnityEngine;

namespace RavenfieldVR.Net
{
    /// <summary>
    /// Proximity voice between the players (the user, 2026-11-14: "Yes ofcourse. but default its off bc we use dc. also the user
    /// should be able to select his device"): the chosen microphone recorded at 16 kHz, sent only while the player speaks (a level
    /// gate with a short hold), 20 ms at a time packed 4:1 (IMA ADPCM, about 8 kB a second), and played at the other player's
    /// soldier's head, louder near and quieter far (in the menu, plainly). Off unless <see cref="VRConfig.VoiceChat"/> is on: then
    /// both sent and heard; off, neither.
    /// </summary>
    internal static class NetVoice
    {
        internal const int Rate = 16000, FrameSamples = 320; // 20 ms
        private const float HoldOpen = 0.35f; // seconds the gate stays open after the voice drops under it

        private static string _device;
        private static AudioClip _mic;
        private static int _readAt;
        private static float[] _micBuffer = new float[4096];
        private static readonly List<float> Pending = new List<float>(FrameSamples * 4);
        private static int _micRate = Rate;
        private static float _resampleAt;
        private static float _openUntil;
        private static byte _seq;
        private static readonly NetWriter Out = new NetWriter(256);
        private static readonly short[] FrameIn = new short[FrameSamples];
        private static readonly byte[] Packed = new byte[FrameSamples / 2];

        /// <summary>The microphones Windows has (for the settings' choice), the default one first as "".</summary>
        internal static string[] Devices()
        {
            try
            {
                return Microphone.devices ?? new string[0];
            }
            catch (Exception)
            {
                return new string[0];
            }
        }

        /// <summary>Whether this player is speaking now (the gate open): for the wrist or a sign.</summary>
        internal static bool Speaking => _mic != null && Time.unscaledTime < _openUntil;

        /// <summary>Once a frame with a session connected: the microphone read and sent, and the voices heard kept at their heads.</summary>
        internal static void Update(NetSession session)
        {
            bool on = VRConfig.VoiceChat.Value && session != null && session.State == NetState.Connected && session.PlayerCount > 1;
            if (!on)
            {
                StopMic();
                if (Voices.Count > 0)
                {
                    Clear();
                }
                return;
            }
            KeepMic();
            Capture(session);
            foreach (Voice v in Voices.Values)
            {
                v.Follow();
            }
        }

        // ---- the microphone ------------------------------------------------------------------------------------------------

        private static void KeepMic()
        {
            string want = VRConfig.VoiceDevice.Value ?? "";
            string[] devices = Devices();
            if (want.Length > 0 && Array.IndexOf(devices, want) < 0)
            {
                want = ""; // (gone since it was chosen: the default one)
            }
            if (_mic != null && want == _device && Microphone.IsRecording(Name(want)))
            {
                return;
            }
            StopMic();
            if (devices.Length == 0)
            {
                return; // (no microphone: heard, not heard from)
            }
            try
            {
                string name = Name(want);
                Microphone.GetDeviceCaps(name, out int min, out int max);
                // (16 kHz where the device does it; a device of one fixed rate is recorded at that and brought to 16 kHz here.)
                _micRate = min == 0 && max == 0 ? Rate : Mathf.Clamp(Rate, min, max);
                _mic = Microphone.Start(name, true, 1, _micRate);
                _device = want;
                _readAt = 0;
                _resampleAt = 0f;
                Pending.Clear();
                Log.Info($"Voice: recording from {(want.Length > 0 ? want : "the default microphone")} at {_micRate} Hz.");
            }
            catch (Exception e)
            {
                Log.WarnOnce("voice-mic", "Voice: the microphone could not be opened: " + e.Message);
                _mic = null;
            }
        }

        private static string Name(string device) => device.Length > 0 ? device : null;

        private static void StopMic()
        {
            if (_mic == null)
            {
                return;
            }
            try
            {
                Microphone.End(Name(_device ?? ""));
            }
            catch (Exception)
            {
            }
            UnityEngine.Object.Destroy(_mic);
            _mic = null;
            _device = null;
            Pending.Clear();
        }

        private static void Capture(NetSession session)
        {
            if (_mic == null)
            {
                return;
            }
            int at = Microphone.GetPosition(Name(_device ?? ""));
            int length = _mic.samples;
            int count = at - _readAt;
            if (count < 0)
            {
                count += length;
            }
            if (count <= 0)
            {
                return;
            }
            if (_whole == null || _whole.Length != length)
            {
                _whole = new float[length]; // (the whole looping second, once: read each frame into the same array)
            }
            if (_micBuffer.Length < count)
            {
                _micBuffer = new float[Mathf.NextPowerOfTwo(count)];
            }
            _mic.GetData(_whole, 0);
            for (int i = 0; i < count; i++)
            {
                _micBuffer[i] = _whole[(_readAt + i) % length];
            }
            _readAt = at;
            float step = _micRate / (float)Rate;
            for (; _resampleAt < count; _resampleAt += step)
            {
                Pending.Add(_micBuffer[(int)_resampleAt]);
            }
            _resampleAt -= count;
            while (Pending.Count >= FrameSamples)
            {
                SendFrame(session);
                Pending.RemoveRange(0, FrameSamples);
            }
        }

        private static float[] _whole;

        private static void SendFrame(NetSession session)
        {
            double sum = 0;
            for (int i = 0; i < FrameSamples; i++)
            {
                float s = Mathf.Clamp(Pending[i] * VRConfig.VoiceMicGain.Value, -1f, 1f);
                sum += s * s;
                FrameIn[i] = (short)(s * 32767f);
            }
            float level = Mathf.Sqrt((float)(sum / FrameSamples));
            if (level >= VRConfig.VoiceGate.Value)
            {
                _openUntil = Time.unscaledTime + HoldOpen;
            }
            if (Time.unscaledTime >= _openUntil)
            {
                return; // (quiet: nothing sent)
            }
            Adpcm.Encode(FrameIn, Packed, out short predictor, out byte index);
            Out.Reset();
            Out.Byte(_seq++);
            Out.UShort((ushort)predictor);
            Out.Byte(index);
            Out.Bytes(Packed, 0, Packed.Length);
            session.Send(NetGame.Voice, Out, false);
        }

        // ---- the voices heard ----------------------------------------------------------------------------------------------

        private static readonly Dictionary<byte, Voice> Voices = new Dictionary<byte, Voice>();

        internal static void Take(byte sender, ref NetReader r)
        {
            if (!VRConfig.VoiceChat.Value)
            {
                return;
            }
            byte seq = r.Byte();
            short predictor = (short)r.UShort();
            byte index = r.Byte();
            if (r.Remaining < FrameSamples / 2 || index > 88)
            {
                return;
            }
            if (!Voices.TryGetValue(sender, out Voice v))
            {
                v = new Voice(sender);
                Voices[sender] = v;
            }
            v.Add(seq, predictor, index, ref r);
        }

        internal static void Forget(byte player)
        {
            if (Voices.TryGetValue(player, out Voice v))
            {
                v.Destroy();
                Voices.Remove(player);
            }
        }

        /// <summary>The session ended: the microphone closed and the voices gone.</summary>
        internal static void Shutdown()
        {
            StopMic();
            Clear();
        }

        internal static void Clear()
        {
            foreach (Voice v in Voices.Values)
            {
                v.Destroy();
            }
            Voices.Clear();
        }

        /// <summary>
        /// One player's voice: a streamed clip that reads from a buffer the frames are decoded into, on a source at his soldier's head.
        /// Playing starts once 60 ms are in (a little cushion for the line's jitter); a gap plays silence and fills again.
        /// </summary>
        private sealed class Voice
        {
            private readonly byte _player;
            private readonly GameObject _go;
            private readonly AudioSource _source;
            private readonly float[] _ring = new float[Rate]; // one second
            private int _write, _read, _count;
            private bool _primed;
            private readonly object _lock = new object();
            private readonly short[] _decoded = new short[FrameSamples];
            private readonly byte[] _packed = new byte[FrameSamples / 2];
            private int _lastSeq = -1;

            public Voice(byte player)
            {
                _player = player;
                _go = new GameObject("RavenfieldVR Voice " + player);
                UnityEngine.Object.DontDestroyOnLoad(_go);
                _source = _go.AddComponent<AudioSource>();
                AudioClip clip = AudioClip.Create("RavenfieldVR Voice", Rate, 1, Rate, true, Read);
                _source.clip = clip;
                _source.loop = true;
                _source.spatialBlend = 1f;
                _source.minDistance = 2f;
                _source.maxDistance = 70f;
                _source.rolloffMode = AudioRolloffMode.Linear;
                _source.dopplerLevel = 0f;
                _source.volume = Mathf.Clamp(VRConfig.VoiceVolume.Value, 0f, 2f);
                _source.Play();
            }

            public void Add(byte seq, short predictor, byte index, ref NetReader r)
            {
                for (int i = 0; i < _packed.Length; i++)
                {
                    _packed[i] = r.Byte();
                }
                // (A frame from before the last one, come late over the unreliable line: dropped, not played out of order.)
                if (_lastSeq >= 0 && (byte)(seq - _lastSeq) > 128)
                {
                    return;
                }
                _lastSeq = seq;
                Adpcm.Decode(_packed, predictor, index, _decoded);
                lock (_lock)
                {
                    for (int i = 0; i < FrameSamples; i++)
                    {
                        if (_count == _ring.Length)
                        {
                            _read = (_read + 1) % _ring.Length; // (more than a second behind: the oldest goes)
                            _count--;
                        }
                        _ring[_write] = _decoded[i] / 32768f;
                        _write = (_write + 1) % _ring.Length;
                        _count++;
                    }
                }
            }

            /// <summary>The audio thread's read: from the buffer once primed, silence when it runs dry.</summary>
            private void Read(float[] data)
            {
                lock (_lock)
                {
                    if (!_primed && _count >= FrameSamples * 3)
                    {
                        _primed = true;
                    }
                    for (int i = 0; i < data.Length; i++)
                    {
                        if (_primed && _count > 0)
                        {
                            data[i] = _ring[_read];
                            _read = (_read + 1) % _ring.Length;
                            _count--;
                        }
                        else
                        {
                            data[i] = 0f;
                            _primed = false;
                        }
                    }
                }
            }

            /// <summary>At his soldier's head in a match, plainly (not placed) where he has none here.</summary>
            public void Follow()
            {
                if (_go == null)
                {
                    return;
                }
                _source.volume = Mathf.Clamp(VRConfig.VoiceVolume.Value, 0f, 2f);
                NetGame.Remote remote = NetGame.RemoteOf(_player, 0);
                Actor a = remote != null ? remote.Actor : null;
                if (a != null && !a.dead)
                {
                    Transform head = a.animator != null ? a.animator.GetBoneTransform(HumanBodyBones.Head) : null;
                    _go.transform.position = head != null ? head.position : a.Position() + Vector3.up * 1.6f;
                    _source.spatialBlend = 1f;
                }
                else
                {
                    _source.spatialBlend = 0f;
                }
            }

            public void Destroy()
            {
                if (_go != null)
                {
                    UnityEngine.Object.Destroy(_go);
                }
            }
        }
    }
}
