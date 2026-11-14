using System;
using System.Collections;
using System.Linq;
using RavenfieldVR.Core;
using RavenfieldVR.Net;
using UnityEngine;

namespace RavenfieldVR.Local
{
    /// <summary>
    /// The local play checked in a running match, flat (-rfvr-splitcheck with -novr; tools/vrshots.py --splitcheck): the Steam status, a LAN
    /// game heard on this PC, and splitscreen with four and with two players driven by simulated controllers (moving, turning, firing,
    /// reloading), their parts of the window, and pictures of each layout.
    /// </summary>
    internal static class SplitCheck
    {
        public static IEnumerator Run(Action<string> report)
        {
            // 1. The Steam status.
            yield return Wait(4f);
            report($"local: Steam status \"{SteamStatus.Build()}\" (sent: {(SteamStatus.Last != null ? "yes" : "NOT SENT (Steam not running?)")})");

            // 2. A LAN game on this PC, heard on the LAN page.
            const int port = 7199;
            Multiplayer.HostLan(port, 4);
            bool heard = false;
            for (float until = Time.unscaledTime + 4f; Time.unscaledTime < until && !heard;)
            {
                LanDiscovery.Listen();
                heard = LanDiscovery.Games.Any(g => g.Address == "127.0.0.1:" + port);
                yield return null;
            }
            LanGame found = LanDiscovery.Games.FirstOrDefault(g => g.Address == "127.0.0.1:" + port);
            report(heard ? $"local: LAN game heard: \"{found.Name}\" {found.Players}/{found.MaxPlayers}, version {found.Version}" : "local: LAN game NOT HEARD within 4 s");
            Multiplayer.Leave();
            yield return Wait(1f);

            // 3. Splitscreen, four players, then two side by side and above and below.
            foreach ((int players, SplitLayout layout, string name) in new[]
                     {
                         (4, SplitLayout.SideBySide, "quarters"), (2, SplitLayout.SideBySide, "side-by-side"), (2, SplitLayout.AboveBelow, "above-below"),
                     })
            {
                Splitscreen.Players = players;
                Splitscreen.Layout = layout;
                Splitscreen.On = true;
                yield return Wait(3f);
                var all = SplitPlayers.All.ToList();
                if (all.Count != players - 1)
                {
                    report($"local: splitscreen with {players}: {all.Count} extra player(s) made, NOT {players - 1}");
                    continue;
                }
                if (name == "quarters")
                {
                    // Keyboards told apart: before the simulated controllers the players took keyboards, so Raw Input is on. A key sent
                    // through Windows (F13, which nothing uses; no mouse moved) is heard on the game's window.
                    const ushort f13 = 0x7C;
                    bool started = RawInput.Started;
                    bool sent = started && RawInput.SendKey(f13, true);
                    bool keyHeard = false;
                    for (int f = 0; f < 30 && sent && !keyHeard; f++)
                    {
                        yield return null;
                        keyHeard = RawInput.AnyKeyHeld(f13);
                    }
                    if (sent)
                    {
                        RawInput.SendKey(f13, false);
                    }
                    report(!started ? "local: keyboards and mice NOT TOLD APART (Raw Input did not start: see the log)"
                        : !sent ? "local: Raw Input on (window " + RawInput.Window.ToInt64().ToString("X") + "), the check's key could not be sent"
                        : keyHeard ? "local: Raw Input on the game's window " + RawInput.Window.ToInt64().ToString("X") + ", a key sent through Windows heard"
                        : !RawInput.InFront ? "local: Raw Input on; the key test not checked (another window is in front, and Windows gives it the keys)"
                        : "local: Raw Input on, a key sent through Windows NOT HEARD within 30 frames");
                    report($"local: Raw Input: {RawInput.Messages} message(s) on the window, {RawInput.Errors} not read{(RawInput.LastError != null ? " (" + RawInput.LastError + ")" : "")}, "
                           + $"{RawInput.Count(false)} keyboard(s) and {RawInput.Count(true)} mouse/mice heard; messages on thread {RawInput.ProcThread}, Unity's {RawInput.MainThread}");
                }
                foreach (SplitPlayer p in all)
                {
                    if (p.Actor == null || p.Actor.dead)
                    {
                        report($"local: splitscreen player {p.Index + 1}: NOT ON THE FIELD ({(p.Actor == null ? "no soldier" : "dead")})");
                    }
                }
                // Walk forward and turn right for a second and a half.
                var from = all.ToDictionary(p => p, p => p.Actor != null ? p.Actor.Position() : Vector3.zero);
                var yaw0 = all.ToDictionary(p => p, p => p.Yaw);
                foreach (SplitPlayer p in all)
                {
                    p.Simulated = new PadState { Connected = true, LeftStick = new Vector2(0f, 1f), RightStick = new Vector2(0.6f, 0f) };
                }
                yield return Wait(1.5f);
                // Fire for a second.
                var shots = all.ToDictionary(p => p, p => 0);
                var last = all.ToDictionary(p => p, p => p.Actor != null && p.Actor.activeWeapon != null ? p.Actor.activeWeapon.ammo : -1);
                foreach (SplitPlayer p in all)
                {
                    p.Simulated = new PadState { Connected = true, RightTrigger = 1f };
                }
                // Shots counted as the magazine goes down, frame by frame (a reload in between is not a shot back).
                for (float until = Time.unscaledTime + 1.5f; Time.unscaledTime < until;)
                {
                    foreach (SplitPlayer p in all)
                    {
                        int now = p.Actor != null && p.Actor.activeWeapon != null ? p.Actor.activeWeapon.ammo : -1;
                        if (now >= 0 && last[p] >= 0 && now < last[p])
                        {
                            shots[p] += last[p] - now;
                        }
                        last[p] = now;
                    }
                    yield return null;
                }
                foreach (SplitPlayer p in all)
                {
                    Actor a = p.Actor;
                    if (a == null)
                    {
                        continue;
                    }
                    float moved = Vector3.ProjectOnPlane(a.Position() - from[p], Vector3.up).magnitude;
                    float turned = Mathf.Abs(Mathf.DeltaAngle(yaw0[p], p.Yaw));
                    int fired = shots[p];
                    Rect v = p.Viewport;
                    string fp = p.View?.ShownWeapon;
                    report($"local: splitscreen {name}, player {p.Index + 1}: first-person view {(fp != null ? "shows " + fp : "NONE (no weapon in front of its camera)")}"
                           + $" (holding {(a.activeWeapon != null ? a.activeWeapon.name : "nothing")}{(a.IsSeated() ? ", seated" : "")})");
                    report($"local: splitscreen {name}, player {p.Index + 1}: walked {moved:0.0} m{(moved < 1f ? " (DID NOT WALK)" : "")}, turned {turned:0} deg"
                           + $"{(turned < 30f ? " (DID NOT TURN)" : "")}, fired {fired} round(s){(fired <= 0 ? " (DID NOT FIRE)" : "")}{(a.fallenOver ? " (FALLEN OVER)" : "")}; its part of the window "
                           + $"x {v.x:0.00} y {v.y:0.00} w {v.width:0.00} h {v.height:0.00}, camera {(p.Cam != null && p.Cam.enabled ? "on" : "OFF")}");
                }
                // Reload by its button.
                foreach (SplitPlayer p in all)
                {
                    p.Simulated = new PadState { Connected = true, Buttons = PadState.X };
                }
                yield return null;
                yield return null;
                int reloading = all.Count(p => p.Actor != null && p.Actor.activeWeapon != null && (p.Actor.activeWeapon.reloading || p.Actor.activeWeapon.ammo == p.Actor.activeWeapon.configuration.ammo));
                report($"local: splitscreen {name}: {reloading} of {all.Count} reloaded on their button");
                foreach (SplitPlayer p in all)
                {
                    p.Simulated = new PadState { Connected = true };
                }
                yield return Wait(0.5f);
                string file = Shots.FileFor("split-" + name);
                System.IO.Directory.CreateDirectory(Shots.Dir); // (ScreenCapture writes no folder of its own)
                ScreenCapture.CaptureScreenshot(file);
                yield return Wait(1f);
                Log.Info("Shot: " + file);
                Splitscreen.On = false;
                yield return Wait(1.5f);
                report($"local: splitscreen {name} off: {SplitPlayers.All.Count} extra player(s) left{(SplitPlayers.All.Count > 0 ? " (NOT TAKEN AWAY)" : "")}");
            }
        }

        private static IEnumerator Wait(float seconds)
        {
            for (float until = Time.unscaledTime + seconds; Time.unscaledTime < until;)
            {
                yield return null;
            }
        }
    }
}
