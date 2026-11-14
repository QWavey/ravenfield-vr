using System;
using System.Collections;
using System.Linq;
using Steamworks;
using UnityEngine;

namespace RavenfieldVR.Core
{
    /// <summary>
    /// Test host only: -rfvr-subscribe=ID[,ID...] has the game subscribe to those workshop items through its own Steam login, waits
    /// at the main menu until Steam reports each installed (or 3 minutes pass), says so in the log and quits. It is how a new
    /// workshop gun is fetched for a check (tools/vrshots.py --subscribe) without a browser signed in to Steam. Nothing runs
    /// without the argument.
    /// </summary>
    internal sealed class WorkshopFetch : MonoBehaviour
    {
        private const string Flag = "-rfvr-subscribe=";

        internal static void InstallIfAsked(GameObject host)
        {
            string arg = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith(Flag, StringComparison.OrdinalIgnoreCase));
            if (arg != null)
            {
                host.AddComponent<WorkshopFetch>()._ids = arg.Substring(Flag.Length).Split(',')
                    .Select(s => ulong.TryParse(s.Trim(), out ulong id) ? id : 0UL).Where(id => id != 0UL).ToArray();
            }
        }

        private ulong[] _ids = new ulong[0];

        private IEnumerator Start()
        {
            float until = Time.realtimeSinceStartup + 120f;
            while ((GameManager.instance == null || GameManager.instance.steamworks == null) && Time.realtimeSinceStartup < until)
            {
                yield return new WaitForSecondsRealtime(0.5f);
            }
            if (GameManager.instance == null || GameManager.instance.steamworks == null)
            {
                Log.Warn("Workshop fetch: the game's Steam connection never came up: NOTHING SUBSCRIBED.");
                Application.Quit();
                yield break;
            }
            foreach (ulong id in _ids)
            {
                try
                {
                    GameManager.instance.steamworks.SubscribeToItem(new PublishedFileId_t(id));
                    Log.Info($"Workshop fetch: asked Steam to subscribe to {id}.");
                }
                catch (Exception e)
                {
                    Log.Warn($"Workshop fetch: Steam refused {id}: {e.Message}");
                }
            }
            until = Time.realtimeSinceStartup + 180f;
            bool all = false;
            while (!all && Time.realtimeSinceStartup < until)
            {
                yield return new WaitForSecondsRealtime(2f);
                all = _ids.All(Installed);
            }
            foreach (ulong id in _ids)
            {
                Log.Info($"Workshop fetch: {id} {(Installed(id) ? "installed" : "NOT INSTALLED")}.");
            }
            Application.Quit();
        }

        private static bool Installed(ulong id)
        {
            try
            {
                return GameManager.instance.steamworks.IsSubscribedItemInstalled(new PublishedFileId_t(id));
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
