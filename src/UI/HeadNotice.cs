using System.Text;
using UnityEngine;

namespace RavenfieldVR.UI
{
    /// <summary>
    /// A few lines of text held in front of the eyes for a while, on a dark plate: what went wrong at start-up, said where the
    /// player is looking. With the controllers' input dead the headset showed the game and nothing else, the reason only in the
    /// log, and the monitor's notice is not in the headset (round-4 scout).
    /// </summary>
    internal sealed class HeadNotice
    {
        private const float Distance = 1.4f, Drop = 0.12f;
        private readonly GameObject _go;
        private readonly TextMesh _text;
        private readonly TextPlate _plate;
        private float _until = -1f;
        private int _refitFrames;

        public HeadNotice()
        {
            Font font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            _go = new GameObject("RavenfieldVR Head Notice");
            Object.DontDestroyOnLoad(_go);
            _go.layer = 5; // (the UI camera's)
            _text = _go.AddComponent<TextMesh>();
            _text.font = font;
            _text.fontSize = 64;
            _text.characterSize = 0.0016f;
            _text.anchor = TextAnchor.MiddleCenter;
            _text.alignment = TextAlignment.Center;
            _text.lineSpacing = 0.95f;
            _text.color = new Color(0.62f, 0.82f, 1f);
            var mr = _go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = font.material;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            _plate = new TextPlate(_text, font.material, 0.85f, 0.03f);
            _go.SetActive(false);
        }

        /// <summary>Shows <paramref name="message"/> (wrapped to short lines) for <paramref name="seconds"/>.</summary>
        public void Show(string message, float seconds)
        {
            _text.text = "Ravenfield VR\n" + Wrap(message, 48);
            _until = Time.unscaledTime + seconds;
            _refitFrames = 2; // (laid out only once drawn: fitted this frame and the next)
            _go.SetActive(true);
        }

        public void BeforeRender(Transform head)
        {
            if (_until < 0f)
            {
                return;
            }
            if (Time.unscaledTime > _until || head == null)
            {
                _until = -1f;
                _go.SetActive(false);
                return;
            }
            // Ahead and a little below the eyes, level, turning with the head's heading (not its tilt: read without chasing it).
            Vector3 forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            forward = forward.sqrMagnitude > 1e-4f ? forward.normalized : Vector3.forward;
            _go.transform.SetPositionAndRotation(head.position + forward * Distance - Vector3.up * Drop, Quaternion.LookRotation(forward, Vector3.up));
            if (_refitFrames > 0)
            {
                _refitFrames--;
                _plate.Fit();
            }
        }

        public void Destroy()
        {
            _plate.Destroy();
            if (_go != null)
            {
                Object.Destroy(_go);
            }
        }

        /// <summary>The words broken into lines of at most <paramref name="width"/> characters (a TextMesh does not wrap).</summary>
        internal static string Wrap(string message, int width)
        {
            var sb = new StringBuilder(message.Length + 8);
            int line = 0;
            foreach (string word in message.Split(' '))
            {
                if (word.Length == 0)
                {
                    continue;
                }
                int firstBreak = word.IndexOf('\n');
                int lead = firstBreak >= 0 ? firstBreak : word.Length; // (what of it goes on this line)
                if (line > 0 && line + 1 + lead > width)
                {
                    sb.Append('\n');
                    line = 0;
                }
                else if (line > 0)
                {
                    sb.Append(' ');
                    line++;
                }
                sb.Append(word);
                line += word.Length;
                int nl = word.LastIndexOf('\n');
                if (nl >= 0)
                {
                    line = word.Length - nl - 1; // (a break of its own in the message starts the count again)
                }
            }
            return sb.ToString();
        }
    }
}
