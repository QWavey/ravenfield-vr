using System.Collections.Generic;
using RavenfieldVR.Core;
using RavenfieldVR.Net;
using UnityEngine;

namespace RavenfieldVR.UI
{
    /// <summary>
    /// The multiplayer window for a player without a headset (F8): the panel is in the VR UI, and a flat player could only join
    /// through a Steam invitation, never host, list the open games or join by address (Tasks 383.8). Plain Unity GUI, drawn only
    /// while open and only in flat play; its buttons in the menus (the cursor is free there), the game's state alone in a match.
    /// </summary>
    internal sealed class FlatMultiplayerWindow : MonoBehaviour
    {
        private bool _open;
        private string _address = "";
        private Vector2 _scroll;
        private Rect _rect;
        private GUIStyle _hint;
        private string _modsSaid;
        private int _kickArmed = -1;
        private float _kickArmedAt;
        private NetPlayer _kickArmedFor;

        private void Awake()
        {
            useGUILayout = false; // (no layout pass while closed: OnGUI returns at once)
        }

        private void Update()
        {
            if (VRManager.Active)
            {
                if (_open)
                {
                    _open = false;
                    useGUILayout = false;
                }
                return;
            }
            if (!_open && useGUILayout)
            {
                useGUILayout = false;
            }
            if (UnityEngine.Input.GetKeyDown(KeyCode.F8))
            {
                _open = !_open;
                useGUILayout = _open;
                if (_open && SteamLobbies.Available && !Multiplayer.Active)
                {
                    Multiplayer.RefreshLobbies();
                }
            }
        }

        private void OnGUI()
        {
            if (VRManager.Active)
            {
                return;
            }
            bool menus = Cursor.lockState != CursorLockMode.Locked;
            if (!_open)
            {
                if (menus && !Multiplayer.InMatch && Event.current.type == EventType.Repaint)
                {
                    // (A hint in the menus that the window is there; not in a match.)
                    if (_hint == null)
                    {
                        _hint = new GUIStyle(GUI.skin.label) { fontSize = 14, alignment = TextAnchor.LowerRight };
                        _hint.normal.textColor = new Color(1f, 1f, 1f, 0.7f);
                    }
                    GUI.Label(new Rect(Screen.width - 420f, Screen.height - 34f, 400f, 24f),
                              Multiplayer.Active ? "Ravenfield VR multiplayer: in a game (F8)" : "Ravenfield VR multiplayer: F8", _hint);
                }
                return;
            }
            float w = Mathf.Min(620f, Screen.width - 40f), h = Mathf.Min(560f, Screen.height - 40f);
            if (_rect.width != w || _rect.height != h)
            {
                _rect = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);
            }
            _rect = GUILayout.Window(0x52465652, _rect, Draw, "Ravenfield VR multiplayer  (F8 closes)");
        }

        private void Draw(int id)
        {
            bool menus = Cursor.lockState != CursorLockMode.Locked;
            _scroll = GUILayout.BeginScrollView(_scroll);
            GUILayout.Label(Multiplayer.Status);
            NetSession session = Multiplayer.Session;
            if (Multiplayer.Active && session != null)
            {
                InGame(session, menus);
            }
            else if (!menus)
            {
                GUILayout.Label("Open the game's menu (Esc) to host or join.");
            }
            else
            {
                Host();
                Lobbies();
                Address();
            }
            GUILayout.EndScrollView();
            if (GUILayout.Button("Close"))
            {
                _open = false; // (useGUILayout let go in Update: changed inside the window's layout it mismatched the groups)
            }
            GUI.DragWindow();
        }

        private static void Host()
        {
            GUILayout.Space(8f);
            GUILayout.Label(SteamLobbies.Available ? "Host a game (over Steam: no port to open)" : "Host a game (Steam is not running: the LAN only)");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("LAN"))
            {
                Multiplayer.HostLan();
            }
            if (SteamLobbies.Available && GUILayout.Button("Friends"))
            {
                Multiplayer.HostSteam(true);
            }
            if (SteamLobbies.Available && GUILayout.Button("Public"))
            {
                Multiplayer.HostSteam(false);
            }
            GUILayout.EndHorizontal();
        }

        private static void Lobbies()
        {
            if (!SteamLobbies.Available)
            {
                return;
            }
            GUILayout.Space(8f);
            GUILayout.Label("Open games");
            List<LobbyInfo> list = Multiplayer.Lobbies ?? new List<LobbyInfo>();
            int build = Multiplayer.GameBuild;
            for (int i = 0; i < list.Count && i < 30; i++)
            {
                LobbyInfo lobby = list[i];
                string what = string.IsNullOrEmpty(lobby.Map) ? "in the menu" : lobby.Map + (string.IsNullOrEmpty(lobby.Mode) ? "" : ", " + lobby.Mode);
                string line = $"{(string.IsNullOrEmpty(lobby.Name) ? "A game" : lobby.Name)}   {lobby.Players}/{lobby.MaxPlayers}   {what}";
                string mismatch = lobby.Mismatch(build);
                GUILayout.BeginHorizontal();
                GUILayout.Label(mismatch != null ? line + "   (" + mismatch + ")" : line);
                if (mismatch == null && GUILayout.Button("Join", GUILayout.Width(80f)))
                {
                    Multiplayer.JoinLobby(lobby.Id);
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.BeginHorizontal();
            GUILayout.Label(Multiplayer.ListStatus);
            if (GUILayout.Button("Refresh", GUILayout.Width(80f)))
            {
                Multiplayer.RefreshLobbies();
            }
            GUILayout.EndHorizontal();
        }

        private void Address()
        {
            GUILayout.Space(8f);
            GUILayout.Label("Join by address (LAN), e.g. 192.168.1.20");
            GUILayout.BeginHorizontal();
            _address = GUILayout.TextField(_address ?? "", 64);
            if (GUILayout.Button("Join", GUILayout.Width(80f)) && _address.Trim().Length > 0)
            {
                Multiplayer.JoinAddress(_address.Trim());
            }
            GUILayout.EndHorizontal();
        }

        private void InGame(NetSession session, bool menus)
        {
            GUILayout.Space(8f);
            GUILayout.Label(session.IsHost ? "Your game. In it:" : "In it:");
            NetPlayer kick = null;
            foreach (NetPlayer p in session.Players)
            {
                string name = p.Info != null && !string.IsNullOrEmpty(p.Info.Name) ? p.Info.Name : "Player " + p.Id;
                string ping = p.Id != session.LocalId && p.Ping >= 0 ? $"   {p.Ping * 1000:0} ms" : "";
                GUILayout.BeginHorizontal();
                GUILayout.Label("   " + name + (p.Id == session.LocalId ? "  (you)" : p.Id == 0 ? "  (host)" : "") + ping);
                if (menus && session.IsHost && p.Id != session.LocalId)
                {
                    // (Pressed twice, as on the VR panel.)
                    // (Armed for this player, not his number: one who left and a newcomer with his number was kicked on one press.)
                    bool armed = _kickArmed == p.Id && ReferenceEquals(_kickArmedFor, p) && Time.unscaledTime - _kickArmedAt < 4f;
                    if (GUILayout.Button(armed ? "Sure?" : "Kick", GUILayout.Width(70f)))
                    {
                        if (armed)
                        {
                            _kickArmed = -1;
                            _kickArmedFor = null;
                            kick = p;
                        }
                        else
                        {
                            _kickArmed = p.Id;
                            _kickArmedFor = p;
                            _kickArmedAt = Time.unscaledTime;
                        }
                    }
                }
                GUILayout.EndHorizontal();
            }
            if (kick != null)
            {
                // (After the list: kicked inside it, the players' table changed under the loop and the window's layout broke; settings scout.)
                session.Kick(kick.Id);
            }
            if (!string.IsNullOrEmpty(Multiplayer.Map))
            {
                GUILayout.Label("Playing " + Multiplayer.Map + (string.IsNullOrEmpty(Multiplayer.Mode) ? "" : ", " + Multiplayer.Mode));
            }
            if (!session.IsHost && NetMods.HostCount > 0)
            {
                int missing = NetMods.Missing(out int off);
                if (missing > 0 && !NetMods.Asked)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"{missing} of the host's {NetMods.HostCount} workshop mods are not on this PC.");
                    if (menus && GUILayout.Button("Get them", GUILayout.Width(100f)))
                    {
                        _modsSaid = NetMods.GetThem();
                    }
                    GUILayout.EndHorizontal();
                }
                else if (missing > 0)
                {
                    GUILayout.Label($"Steam is fetching the host's mods: {missing} still to come. Start the game again once they are there.");
                }
                if (off > 0)
                {
                    GUILayout.Label($"{off} of them are switched off on this PC (the game's Mods page turns them on).");
                }
                if (!string.IsNullOrEmpty(_modsSaid))
                {
                    GUILayout.Label(_modsSaid);
                }
            }
            if (session.IsHost)
            {
                GUILayout.Label("Start a match from the game's own menu: everyone in your game comes along.");
            }
            else
            {
                GUILayout.Label("When the host starts the match, it starts here by itself.");
                GUILayout.BeginHorizontal();
                GUILayout.Label(Multiplayer.OtherSide ? "You play AGAINST the host (from his next match)." : "You play on the host's side.");
                if (menus && GUILayout.Button(Multiplayer.OtherSide ? "Join his side" : "Play against", GUILayout.Width(110f)))
                {
                    Multiplayer.OtherSide = !Multiplayer.OtherSide;
                }
                GUILayout.EndHorizontal();
            }
            if (!menus)
            {
                GUILayout.Label("Open the game's menu (Esc) for the buttons.");
                return;
            }
            GUILayout.BeginHorizontal();
            if (session.IsHost && Multiplayer.OverSteam && GUILayout.Button("Invite friends"))
            {
                Multiplayer.InviteFriends();
            }
            if (GUILayout.Button(session.IsHost ? "Close your game (everyone leaves)" : "Leave this game"))
            {
                Multiplayer.Leave();
            }
            GUILayout.EndHorizontal();
        }
    }
}
