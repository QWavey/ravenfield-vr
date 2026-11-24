using UnityEngine;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// A shell pushed home finishes going into the gun: a copy of what the hand held slides on along its way in, past the port, and
    /// goes. The held shell was taken away where its short way in ended, with a third of it still out of the port, so it seemed to
    /// vanish in the air beside the gun instead of going in as the reload's animation has it (the user, 2026-11-24: "shotgun shells
    /// are also in the air sometimes and not actually getting inserted ... make sure its cleanly and actually there like its in the
    /// animation"). Drawn only: no collider, no body, carried with the gun.
    /// </summary>
    internal sealed class ShellSlideIn : MonoBehaviour
    {
        private const float Seconds = 0.09f;
        private Vector3 _from, _to;
        private float _t;

        /// <summary>A drawn copy of <paramref name="shell"/> slid <paramref name="distance"/> on along <paramref name="insertWorld"/> into <paramref name="gun"/>.</summary>
        internal static void Start(GameObject shell, Transform gun, Vector3 insertWorld, float distance)
        {
            if (shell == null || gun == null || distance <= 0.002f)
            {
                return;
            }
            GameObject copy = Object.Instantiate(shell, shell.transform.position, shell.transform.rotation);
            copy.name = "RavenfieldVR Shell Going In";
            foreach (Collider c in copy.GetComponentsInChildren<Collider>(true))
            {
                Object.Destroy(c);
            }
            foreach (Rigidbody b in copy.GetComponentsInChildren<Rigidbody>(true))
            {
                Object.Destroy(b);
            }
            foreach (Transform t in copy.GetComponentsInChildren<Transform>(true))
            {
                if (t != copy.transform && t.name == "RavenfieldVR Shells Beside")
                {
                    Object.Destroy(t.gameObject); // (the ones still in the hand stay there)
                }
            }
            foreach (MonoBehaviour m in copy.GetComponentsInChildren<MonoBehaviour>(true))
            {
                Object.Destroy(m); // (only drawn: nothing of the prop's own runs on the copy)
            }
            copy.transform.SetParent(gun, true);
            var slide = copy.AddComponent<ShellSlideIn>();
            slide._from = copy.transform.localPosition;
            slide._to = slide._from + gun.InverseTransformVector(insertWorld.normalized * distance);
        }

        private void LateUpdate()
        {
            _t += Mathf.Min(Time.unscaledDeltaTime, 0.05f);
            float u = Mathf.Clamp01(_t / Seconds);
            transform.localPosition = Vector3.Lerp(_from, _to, u * u); // (sped up as it goes in, as the thumb pushes it home)
            if (u >= 1f)
            {
                Destroy(gameObject);
            }
        }
    }
}
