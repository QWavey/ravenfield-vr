using System;
using System.Collections.Generic;
using System.Linq;
using RavenfieldVR.Net;
using RavenfieldVR.Rig;
using UnityEngine;
using UnityEngine.UI;

namespace RavenfieldVR.UI
{
    /// <summary>
    /// The multiplayer panel (opened from the VR settings' footer): host a game (Steam, public or for friends; LAN), Steam's list
    /// of open games with a Join on each, a LAN game joined by its address (typed on the panel's own keys), and, in a game, who is
    /// in it with his ping, the invitation of friends and the way out.
    /// </summary>
    internal sealed class MultiplayerPanel : ModPanel
    {
        private const int LobbiesPerPage = 6;
        private static readonly string[] Keys = { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0", ".", ":" };

        private string _address = "";
        private bool _typing;
        private int _page;

        protected override float CanvasWidth => 1000f;
        protected override float WorldWidth => 1.0f;
        protected override float RefreshSeconds => 1f; // (pings, players coming and going)

        public MultiplayerPanel(VRRig rig, Camera eventCamera) : base(rig, eventCamera, "RavenfieldVR Multiplayer")
        {
            _onChanged = () => Changed = true;
            Multiplayer.Changed += _onChanged;
        }

        /// <summary>The session's "changed" as this panel hears it: taken off again with the panel (every panel made anew, F6's
        /// reload, left another one listening for good; scout, 2026-11-11).</summary>
        private readonly System.Action _onChanged;

        public override void Dispose()
        {
            Multiplayer.Changed -= _onChanged;
            base.Dispose();
        }

        /// <summary>A joined player outside a match follows the host: his panel stays (its Close only made it jump back).</summary>
        internal static bool Following => Multiplayer.Active && !Multiplayer.Hosting && !RavenfieldVR.Util.GameContext.InGame
                                          && UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex <= 1;

        protected override bool HasClose => !Following;

        protected override int Shown()
        {
            // (Pings to the nearest 10 ms: a ping moving by a millisecond is no reason to build the panel again.)
            int shown = (Multiplayer.Active ? 1 : 0) | (Following ? 2 : 0);
            if (Multiplayer.Active)
            {
                foreach (NetPlayer p in Multiplayer.Session.Players)
                {
                    shown = shown * 31 + p.Id * 7 + (p.Ping >= 0 ? (int)(p.Ping * 100) : -1);
                }
                shown = shown * 31 + (int)Multiplayer.Session.State;
                foreach (string line in Multiplayer.HostLines)
                {
                    shown = shown * 31 + line.GetHashCode();
                }
                if (NetMods.HostCount > 0)
                {
                    shown = shown * 31 + NetMods.Missing(out int off) * 5 + off * 3 + (NetMods.Asked ? 1 : 0) + NetMods.HostCount * 7;
                }
            }
            return shown;
        }

        public override void Open()
        {
            base.Open();
            if (!Multiplayer.Active && Multiplayer.Lobbies == null)
            {
                Multiplayer.RefreshLobbies();
            }
        }

        protected override void Build()
        {
            Title("Multiplayer", Multiplayer.Status);
            if (Multiplayer.Active)
            {
                InGame();
            }
            else
            {
                Host();
                Lobbies();
                Address();
            }
        }

        private void InGame()
        {
            NetSession session = Multiplayer.Session;
            Section(session.State == NetState.Connecting ? "Joining" : session.IsHost ? "Your game" : "The game you are in");
            foreach (NetPlayer p in session.Players.OrderBy(p => p.Id))
            {
                string ping = p.Id == session.LocalId ? "you" : p.Ping >= 0 ? $"{p.Ping * 1000:0} ms" : "...";
                Label($"{(p.Id == 0 ? "Host   " : "Player ")}{p.Info.Name}{(p.Info.Vr ? "   VR" : "")}   ({ping})", 22, FontStyle.Normal, UiKit.TextColor);
            }
            if (session.State == NetState.Connecting)
            {
                Label("Waiting for the host's answer ...");
            }
            else if (!session.IsHost)
            {
                Section("The host chooses, you follow");
                string[] lines = Multiplayer.HostLines;
                if (lines.Length == 0)
                {
                    Label("Waiting for word from the host ...");
                }
                foreach (string line in lines)
                {
                    Label(line, 24, FontStyle.Normal, UiKit.TextColor);
                }
                Label("When the host starts the match, it starts here by itself.", 19, FontStyle.Italic);
                if (NetMods.HostCount > 0)
                {
                    // The host's workshop mods this PC lacks: asked for through Steam on the player's word.
                    Section("The host's mods");
                    int missing = NetMods.Missing(out int off);
                    if (missing > 0 && !NetMods.Asked)
                    {
                        ActionRow($"{missing} of the host's {NetMods.HostCount} workshop mods are not on this PC.", "Get them", () => UISystem.Instance?.Notice?.Show(NetMods.GetThem(), 6f));
                        Label("Steam downloads them from the workshop; start the game again once they are there. Without them his weapons and maps are missing here.", 18, FontStyle.Italic, UiKit.MutedText, 50f);
                    }
                    else if (missing > 0)
                    {
                        Label($"Steam is fetching the host's mods: {missing} still to come.", 22, FontStyle.Normal, UiKit.TextColor);
                        Label("Once they are downloaded, start the game again: the game loads mods only when it starts.", 18, FontStyle.Italic, UiKit.MutedText, 34f);
                    }
                    else
                    {
                        Label($"You have all {NetMods.HostCount} of the host's workshop mods.", 22, FontStyle.Normal, UiKit.TextColor);
                    }
                    if (off > 0)
                    {
                        Label($"{off} of them are switched off on this PC (the game's Mods page turns them on).", 18, FontStyle.Italic, UiKit.MutedText, 34f);
                    }
                }
            }
            Label("No slow motion and no pause in a shared match: it runs at one speed for everyone.", 19, FontStyle.Italic);
            Label("Vehicles are shared: whoever sits in the driver's seat drives it for everyone. Turrets are each PC's own.", 19, FontStyle.Italic);
            Gap(8f);
            if (session.IsHost && Multiplayer.OverSteam)
            {
                ActionRow("Friends, through Steam's overlay.", "Invite", Multiplayer.InviteFriends);
            }
            if (session.IsHost)
            {
                Label($"Who joins is told your {NetMods.Mine().Count} workshop mods and asked whether to fetch the ones he lacks. Everyone needs the VR mod itself.", 19, FontStyle.Italic, UiKit.MutedText, 50f);
                Label("Start a match from the game's own menu: everyone in your game comes along.", 19, FontStyle.Italic);
            }
            ActionRow(session.IsHost ? "Close your game (everyone leaves)." : "Leave this game.", session.IsHost ? "Close" : "Leave", Multiplayer.Leave);
        }

        private void Host()
        {
            Section("Host a game");
            RectTransform row = Row(UiKit.RowHeight);
            Text t = UiKit.MakeText(row, SteamLobbies.Available ? "Over Steam: no port to open." : "Steam is not running: the LAN only.", 22, FontStyle.Normal,
                                    UiKit.TextColor, TextAnchor.MiddleLeft);
            UiKit.Stretch(t.rectTransform, 34f, 34f + 3f * 198f);
            ButtonIn(row, "LAN", 34f, 190f, () => Multiplayer.HostLan());
            if (SteamLobbies.Available)
            {
                ButtonIn(row, "Friends", 34f + 198f, 190f, () => Multiplayer.HostSteam(true));
                ButtonIn(row, "Public", 34f + 2f * 198f, 190f, () => Multiplayer.HostSteam(false));
            }
        }

        private void Lobbies()
        {
            Section("Open games");
            List<LobbyInfo> list = Multiplayer.Lobbies ?? new List<LobbyInfo>();
            int pages = Mathf.Max(1, (list.Count + LobbiesPerPage - 1) / LobbiesPerPage);
            _page = Mathf.Clamp(_page, 0, pages - 1);
            int build = Multiplayer.GameBuild;
            for (int i = _page * LobbiesPerPage; i < Mathf.Min(list.Count, (_page + 1) * LobbiesPerPage); i++)
            {
                LobbyInfo lobby = list[i];
                string what = string.IsNullOrEmpty(lobby.Map) ? "in the menu" : lobby.Map + (string.IsNullOrEmpty(lobby.Mode) ? "" : ", " + lobby.Mode);
                string mismatch = lobby.Mismatch(build);
                string line = $"{(string.IsNullOrEmpty(lobby.Name) ? "A game" : lobby.Name)}   {lobby.Players}/{lobby.MaxPlayers}   {what}";
                if (mismatch != null)
                {
                    Label(line + "   (" + mismatch + ")", 22);
                }
                else
                {
                    ActionRow(line, "Join", () => Multiplayer.JoinLobby(lobby.Id), null, null, 150f);
                }
            }
            string under = Multiplayer.ListStatus + (pages > 1 ? $"   Page {_page + 1} of {pages}." : "");
            if (pages > 1)
            {
                ActionRow(under, "Refresh", Multiplayer.RefreshLobbies, "Next", () => _page = (_page + 1) % pages, 150f);
            }
            else
            {
                ActionRow(under, "Refresh", Multiplayer.RefreshLobbies, null, null, 150f);
            }
        }

        private void Address()
        {
            Section("Join by address (LAN)");
            ActionRow(_address.Length > 0 ? _address : "No address typed.  Example: 192.168.1.20", _typing ? "Done" : "Type", () => _typing = !_typing,
                      "Join", () =>
                      {
                          if (_address.Length > 0)
                          {
                              _typing = false;
                              Multiplayer.JoinAddress(_address);
                          }
                      }, 150f);
            if (!_typing)
            {
                return;
            }
            // The address's own keys: figures, the dot, the colon before a port, and one back.
            RectTransform row = Row(UiKit.RowHeight);
            const float width = 64f, gap = 6f;
            ButtonIn(row, "<", 34f, 90f, () => _address = _address.Length > 0 ? _address.Substring(0, _address.Length - 1) : "");
            for (int i = 0; i < Keys.Length; i++)
            {
                string key = Keys[i];
                ButtonIn(row, key, 34f + 90f + gap + (Keys.Length - 1 - i) * (width + gap), width, () =>
                {
                    if (_address.Length < 21)
                    {
                        _address += key;
                    }
                });
            }
        }
    }
}
