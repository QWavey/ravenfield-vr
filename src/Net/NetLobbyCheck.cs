using System;
using System.Collections;
using System.Linq;
using RavenfieldVR.Core;
using RavenfieldVR.UI;
using UnityEngine;
using UnityEngine.UI;

namespace RavenfieldVR.Net
{
    /// <summary>
    /// The main menu's part of multiplayer checked in the game (-rfvr-lobbycheck, no map; tools/vrshots.py --lobbycheck): the
    /// choice of single player or multiplayer over the menu at the game's start, the multiplayer panel behind its button, and a
    /// player who joined a host (made here, on a UDP port of this PC) following him: the game's own menus hidden, the host's
    /// choices shown as he tells them, the menus back when he leaves. A failure is worded in capitals (NOT ...).
    /// </summary>
    internal static class NetLobbyCheck
    {
        private const int Port = 7143;

        internal static IEnumerator Run(Action<string> report)
        {
            float by = Time.unscaledTime + 90f;
            while (Time.unscaledTime < by && (UISystem.Instance == null || MainMenu.instance == null || !UISystem.Instance.Canvases.AnyMenuVisible))
            {
                yield return null;
            }
            UISystem ui = UISystem.Instance;
            if (ui == null || MainMenu.instance == null)
            {
                report("multiplayer lobby: the main menu NOT REACHED in VR");
                yield break;
            }

            // 1. The choice, over the menu.
            by = Time.unscaledTime + 10f;
            while (Time.unscaledTime < by && !ui.StartChoice.IsOpen)
            {
                yield return null;
            }
            yield return new WaitForSecondsRealtime(1f);
            yield return new WaitForEndOfFrame();
            Shots.Capture("lobby-1-choice");
            bool choice = ui.StartChoice.IsOpen;
            report($"multiplayer start choice: {(choice ? "shown over the main menu" : "NOT SHOWN")}");
            Button multi = ui.StartChoice.Canvas.GetComponentsInChildren<Button>(false).FirstOrDefault(b =>
            {
                Text t = b.GetComponentInChildren<Text>();
                return t != null && t.text == "Multiplayer";
            });
            if (multi != null)
            {
                multi.onClick.Invoke();
            }
            yield return new WaitForSecondsRealtime(1.2f);
            yield return new WaitForEndOfFrame();
            Shots.Capture("lobby-2-multiplayer");
            report($"multiplayer start choice, \"Multiplayer\" pressed: {(ui.MultiplayerPanel.IsOpen && !ui.StartChoice.IsOpen ? "the multiplayer panel is open" : "THE PANEL NOT OPEN")}");

            // 2. Joined to a host: his choices shown, the game's own menus hidden.
            UdpTransport line = null;
            NetSession host = null;
            string failed = null;
            try
            {
                line = new UdpTransport(Port);
                host = NetSession.Host(line, new NetPlayerInfo { Name = "Ghost host", GameBuild = Multiplayer.GameBuild, ModVersion = PluginInfo.Version });
            }
            catch (Exception e)
            {
                failed = e.Message;
            }
            if (failed != null)
            {
                report("multiplayer lobby: the second game NOT MADE (" + failed + ")");
                yield break;
            }
            IEnumerator For(float seconds)
            {
                float until = Time.unscaledTime + seconds;
                while (Time.unscaledTime < until)
                {
                    host.Update(Time.realtimeSinceStartupAsDouble);
                    yield return null;
                }
            }
            int menusBefore = ui.Canvases.HiddenMenuCount;
            Multiplayer.JoinAddress("127.0.0.1:" + Port);
            by = Time.unscaledTime + 5f;
            while (Time.unscaledTime < by && !(Multiplayer.Active && Multiplayer.Session.State == NetState.Connected))
            {
                host.Update(Time.realtimeSinceStartupAsDouble);
                yield return null;
            }
            bool joined = Multiplayer.Active && Multiplayer.Session.State == NetState.Connected;
            report($"multiplayer lobby joined from the menu: {(joined ? "in" : "NOT JOINED (" + Multiplayer.Status + ")")}");
            if (joined)
            {
                var w = new NetWriter(256);
                string[] told = { "Map: Island", "Mode: Battle", "Soldiers: 30 against 30", "Time of day: night" };
                w.Byte((byte)told.Length);
                foreach (string s in told)
                {
                    w.String(s);
                }
                host.Send(NetGame.LobbyState, w, true);
                yield return For(1.5f);
                yield return new WaitForEndOfFrame();
                Shots.Capture("lobby-3-following");
                bool lines = Multiplayer.HostLines.SequenceEqual(told);
                int hidden = ui.Canvases.HiddenMenuCount;
                report($"multiplayer lobby, following the host: his choices {(lines ? "shown as told" : "NOT AS TOLD (" + string.Join(" | ", Multiplayer.HostLines) + ")")}, "
                       + $"the game's own menus hidden: {hidden}{(hidden > menusBefore ? "" : " NOT HIDDEN")}, the panel {(ui.MultiplayerPanel.IsOpen ? "open" : "NOT OPEN")}");
                ui.MultiplayerPanel.Close(); // (a joined player's panel comes back by itself)
                yield return For(0.5f);
                report($"multiplayer lobby, the panel closed by the player: {(ui.MultiplayerPanel.IsOpen ? "back by itself" : "NOT BACK")}");

                // 3. The host leaves: the menus are the player's again.
                host.Leave();
                line.Dispose();
                by = Time.unscaledTime + 4f;
                while (Time.unscaledTime < by && (Multiplayer.Active || ui.Canvases.HiddenMenuCount > 0))
                {
                    yield return null;
                }
                yield return new WaitForSecondsRealtime(0.8f);
                yield return new WaitForEndOfFrame();
                Shots.Capture("lobby-4-host-left");
                report($"multiplayer lobby, the host left: {(Multiplayer.Active ? "NOT ENDED" : "ended (\"" + Multiplayer.Status + "\")")}, the game's menus {(ui.Canvases.HiddenMenuCount == 0 && ui.Canvases.AnyMenuVisible ? "back" : "NOT BACK")}");
            }
            else
            {
                host.Dispose();
                line.Dispose();
            }
            Multiplayer.Leave();
        }
    }
}
