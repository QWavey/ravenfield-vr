using System;
using System.Collections.Generic;
using System.Linq;
using RavenfieldVR.Core;
using RavenfieldVR.Rig;
using RavenfieldVR.UI;
using RavenfieldVR.Util;
using UnityEngine;
using UnityEngine.UI;

namespace RavenfieldVR.Sandbox
{
    /// <summary>
    /// The sandbox's panel (opened from the VR settings' footer): what the spawn tool makes (a soldier's side, behaviour, weapons,
    /// health and number; a vehicle and its side), every weapon of the game and its mods to take in hand, and the field's own
    /// switches (a quiet battlefield, no damage, heal, clear). "Take the tool" closes the panel with the tool in the free hand.
    /// </summary>
    internal sealed class SandboxPanel : UI.ModPanel
    {
        private enum Tab
        {
            Soldiers,
            Weapons,
            Vehicles,
            Field,
            Me
        }

        private static readonly string[] TabNames = { "Soldiers", "Weapons", "Vehicles", "World", "Me" };
        private static readonly string[] SideNames = { "Enemy", "Friendly" };
        private static readonly string[] BehaviourNames = { "Normal", "Neutral", "Stand still", "Frozen", "Follow me" };
        private static readonly string[] WeaponNames = { "Their own", "Random", "None", "Mine" };
        private static readonly string[] HealthNames = { "Normal", "No damage", "One hit" };
        private static readonly int[] Counts = { 1, 2, 4, 8 };
        private static readonly string[] SlotNames = { "All", "Primary", "Secondary", "Gear" };
        private static readonly string[] BodyNames = { "5 s", "10 s", "1 min", "5 min" };
        private static readonly float[] BodyTimes = { 5f, 10f, 60f, 300f };
        private const int WeaponsPerPage = 12, WeaponsPerRow = 4;

        private Tab _tab;
        private int _weaponPage, _weaponSlot;
        private string _status = "";
        private float _statusUntil;

        protected override float CanvasWidth => 1000f;
        protected override float WorldWidth => 1.0f;
        protected override float RefreshSeconds => 1f; // (the count of what stands on the field, the status line going out)

        public SandboxPanel(VRRig rig, Camera eventCamera) : base(rig, eventCamera, "RavenfieldVR Sandbox")
        {
        }

        /// <summary>The self-check's: the panel opened on one page; how many buttons it built.</summary>
        internal int BuildForCheck(int tab)
        {
            _tab = (Tab)tab;
            if (IsOpen)
            {
                Rebuild();
            }
            else
            {
                Open();
            }
            return Canvas.GetComponentsInChildren<Button>(true).Count(b => b != null && b.gameObject.activeInHierarchy);
        }

        protected override int Shown()
        {
            // One number of all that is shown, each part mixed in after the other. (In bits of their own first, then two more
            // parts with ^ behind the |: ^ binds tighter, so their masks were OR-ed in, half the bits stood at 1 with the dead
            // kept, and a change of the counts under them went unseen until the next change elsewhere; scout, 2026-11-11.)
            unchecked
            {
                int h = (int)Sandbox.Armed;
                h = h * 31 + (Sandbox.Quiet ? 1 : 0);
                h = h * 31 + (Sandbox.PlayerInvulnerable ? 1 : 0);
                h = h * 31 + (GameContext.InGame ? 1 : 0);
                h = h * 31 + (Time.unscaledTime < _statusUntil && _status.Length > 0 ? 1 : 0);
                h = h * 31 + Sandbox.SpawnedCount;
                h = h * 31 + Sandbox.VehiclePictureCount; // (pictures taken after the page was built: shown; scout)
                h = h * 31 + Sandbox.PlacedCount;
                h = h * 31 + (Sandbox.Paused ? 1 : 0);
                h = h * 31 + (Sandbox.Fly ? 1 : 0);
                h = h * 31 + (Sandbox.RemoveDead ? 1 : 0);
                h = h * 31 + (Net.Multiplayer.Active ? Net.Multiplayer.Hosting ? 1 : 2 : 0);
                return h;
            }
        }

        private void Say(string status)
        {
            _status = status ?? "";
            _statusUntil = Time.unscaledTime + 6f;
        }

        protected override void Build()
        {
            Title("Sandbox", GameContext.InGame
                ? "Soldiers, vehicles and weapons made on the spot. The tool is in your empty hand: point, pull its trigger."
                : "Sandbox is a game mode: choose it where the game's modes are. It starts with no soldier on either side.");
            Choice("", TabNames, (int)_tab, i =>
            {
                _tab = (Tab)i;
                // The tool in the hand follows the page: on Vehicles it makes the chosen vehicle, on Soldiers the soldier (it had
                // to be put away and taken again on the other page: "not very handy", user 2026-11-10).
                if (Sandbox.Armed != Sandbox.ToolKind.None)
                {
                    if (_tab == Tab.Soldiers)
                    {
                        Sandbox.Armed = Sandbox.ToolKind.Soldier;
                    }
                    else if (_tab == Tab.Vehicles)
                    {
                        Sandbox.Armed = Sandbox.ToolKind.Vehicle;
                    }
                }
            });
            // In a shared match the field is the host's: on a joined PC what changes it is shown greyed out; and no PC makes
            // vehicles there (the user, 2026-11-10: "in multiplayer some things need to be greyed out").
            bool shared = Net.Multiplayer.Active, joined = shared && !Net.Multiplayer.Hosting;
            if (joined && _tab != Tab.Me && _tab != Tab.Weapons)
            {
                Label("A shared match: the field is the host's to change. What only he can do is greyed out.", 19, FontStyle.Italic, UiKit.TextColor, 34f);
            }
            Greyed = joined && _tab == Tab.Field; // (vehicles and soldiers: a joined player asks the host to make them, 2026-11-14)
            switch (_tab)
            {
                case Tab.Soldiers:
                    Soldiers();
                    break;
                case Tab.Weapons:
                    Weapons();
                    break;
                case Tab.Vehicles:
                    Vehicles();
                    break;
                case Tab.Me:
                    Me();
                    break;
                default:
                    Field();
                    break;
            }
            Greyed = false;
            Gap(8f);
            bool live = Time.unscaledTime < _statusUntil && _status.Length > 0;
            Label(live ? _status : $"On the field from the sandbox: {Sandbox.SpawnedCount}" + (Sandbox.Quiet ? "   |   quiet battlefield" : ""),
                  20, FontStyle.Normal, live ? UiKit.TextColor : UiKit.MutedText);
        }

        private void Soldiers()
        {
            SoldierSpec s = Sandbox.Soldier;
            Section("The soldier the tool makes");
            Choice("Side", SideNames, (int)s.Side, i => s.Side = (SoldierSide)i);
            Choice("Behaviour", BehaviourNames, (int)s.Behaviour, i => s.Behaviour = (SoldierBehaviour)i);
            Label(BehaviourHelp(s), 18, FontStyle.Italic, UiKit.MutedText, 34f);
            Choice("Weapons", WeaponNames, (int)s.Weapons, i => s.Weapons = (SoldierWeapons)i);
            Choice("Health", HealthNames, (int)s.Health, i => s.Health = (SoldierHealth)i);
            Choice("How many", Counts.Select(c => c.ToString()).ToArray(), Array.IndexOf(Counts, s.Count), i => s.Count = Counts[i]);
            Gap(6f);
            ActionRow("On a soldier, the tool sets his behaviour.", Sandbox.Armed == Sandbox.ToolKind.Soldier ? "Put away" : "Take the tool", () => TakeOrPutAway(Sandbox.ToolKind.Soldier),
                      "Here", () => SpawnInFront(false), 190f);
        }

        private static string BehaviourHelp(SoldierSpec s)
        {
            switch (s.Behaviour)
            {
                case SoldierBehaviour.Neutral: return "Walks his rounds, never shoots.";
                case SoldierBehaviour.StandStill: return "Stays where he is put and fights from there.";
                case SoldierBehaviour.Frozen: return "A statue: no move, no shot. To test hits, throws and holds.";
                case SoldierBehaviour.Follow: return s.Side == SoldierSide.Friendly ? "Joins your squad and takes your orders." : "Only a friendly soldier follows; an enemy fights as normal.";
                default: return "Fights as any soldier of the match: he runs off to its flags at once.";
            }
        }

        private List<WeaponManager.WeaponEntry> WeaponList()
        {
            var list = new List<WeaponManager.WeaponEntry>();
            if (WeaponManager.instance == null || WeaponManager.instance.allWeapons == null)
            {
                return list;
            }
            foreach (WeaponManager.WeaponEntry e in WeaponManager.instance.allWeapons)
            {
                if (e == null || e.prefab == null || e.hidden)
                {
                    continue;
                }
                bool fits = _weaponSlot == 0
                            || _weaponSlot == 1 && e.slot == WeaponManager.WeaponSlot.Primary
                            || _weaponSlot == 2 && e.slot == WeaponManager.WeaponSlot.Secondary
                            || _weaponSlot == 3 && e.slot != WeaponManager.WeaponSlot.Primary && e.slot != WeaponManager.WeaponSlot.Secondary;
                if (fits)
                {
                    list.Add(e);
                }
            }
            return list;
        }

        private void Weapons()
        {
            Section("A weapon into your hands");
            Choice("Kind", SlotNames, _weaponSlot, i =>
            {
                _weaponSlot = i;
                _weaponPage = 0;
            });
            List<WeaponManager.WeaponEntry> list = WeaponList();
            int pages = Mathf.Max(1, (list.Count + WeaponsPerPage - 1) / WeaponsPerPage);
            _weaponPage = Mathf.Clamp(_weaponPage, 0, pages - 1);
            // Tiles with the weapon's own picture (the game's loadout sprite), four to a row (the user, 2026-11-10: "there are no
            // images of the guns ... we need images its important so we see what we spawn"). A press gives it.
            const float tileHeight = 150f, gap = 8f;
            float tileWidth = Mathf.Floor((CanvasWidth - 68f - gap * (WeaponsPerRow - 1)) / WeaponsPerRow);
            int first = _weaponPage * WeaponsPerPage, last = Mathf.Min(list.Count, first + WeaponsPerPage);
            for (int from = first; from < last; from += WeaponsPerRow)
            {
                RectTransform row = Row(tileHeight + 10f);
                for (int c = 0; c < WeaponsPerRow && from + c < last; c++)
                {
                    WeaponManager.WeaponEntry entry = list[from + c];
                    Button tile = ButtonIn(row, entry.name, 34f + (WeaponsPerRow - 1 - c) * (tileWidth + gap), tileWidth, () =>
                    {
                        string name = Sandbox.Give(entry, out string problem);
                        Say(name != null ? name + " is yours." : "No weapon: " + problem + ".");
                    });
                    var tileRect = (RectTransform)tile.transform;
                    tileRect.sizeDelta = new Vector2(tileWidth, tileHeight);
                    Text name = tile.GetComponentInChildren<Text>();
                    if (name != null)
                    {
                        name.fontSize = 17;
                        name.alignment = entry.uiSprite != null ? TextAnchor.LowerCenter : TextAnchor.MiddleCenter;
                        name.horizontalOverflow = HorizontalWrapMode.Wrap;
                        name.rectTransform.offsetMin = new Vector2(6f, 5f);
                    }
                    if (entry.uiSprite != null)
                    {
                        var picture = new GameObject("Picture", typeof(RectTransform));
                        picture.layer = 5;
                        var pictureRect = (RectTransform)picture.transform;
                        pictureRect.SetParent(tileRect, false);
                        pictureRect.anchorMin = Vector2.zero;
                        pictureRect.anchorMax = Vector2.one;
                        pictureRect.offsetMin = new Vector2(10f, 46f);
                        pictureRect.offsetMax = new Vector2(-10f, -8f);
                        Image image = picture.AddComponent<Image>();
                        image.sprite = entry.uiSprite;
                        image.preserveAspect = true;
                        image.raycastTarget = false;
                    }
                }
            }
            if (list.Count == 0)
            {
                Label(GameContext.InGame ? "No weapon of that kind." : "The weapons are listed in a match.");
            }
            ActionRow($"Page {_weaponPage + 1} of {pages}   ({list.Count} weapons)", "Back", () => _weaponPage = (_weaponPage + pages - 1) % pages,
                      "Next", () => _weaponPage = (_weaponPage + 1) % pages, 150f);
        }

        private void Vehicles()
        {
            Section("The vehicle the tool makes");
            Choice("Side", SideNames, Sandbox.VehicleFriendly ? 1 : 0, i => Sandbox.VehicleFriendly = i == 1);
            var types = (VehicleSpawner.VehicleSpawnType[])Enum.GetValues(typeof(VehicleSpawner.VehicleSpawnType));
            // Tiles, four to a row, each with the picture the game's own preview camera took of it in the main menu (none taken:
            // the name alone); the chosen one in the accent colour.
            const float tileHeight = 130f, gap = 8f;
            float tileWidth = Mathf.Floor((CanvasWidth - 68f - gap * 3f) / 4f);
            for (int from = 0; from < types.Length; from += 4)
            {
                RectTransform row = Row(tileHeight + 8f);
                for (int c = 0; c < 4 && from + c < types.Length; c++)
                {
                    VehicleSpawner.VehicleSpawnType type = types[from + c];
                    Texture picture = Sandbox.VehiclePicture(type);
                    Button tile = ButtonIn(row, VehicleName(type), 34f + (3 - c) * (tileWidth + gap), tileWidth, () => Sandbox.Vehicle = type, type == Sandbox.Vehicle);
                    var tileRect = (RectTransform)tile.transform;
                    tileRect.sizeDelta = new Vector2(tileWidth, tileHeight);
                    Text name = tile.GetComponentInChildren<Text>();
                    if (name != null && picture != null)
                    {
                        name.fontSize = 17;
                        name.alignment = TextAnchor.LowerCenter;
                        name.rectTransform.offsetMin = new Vector2(6f, 4f);
                    }
                    if (picture != null)
                    {
                        var shown = new GameObject("Picture", typeof(RectTransform));
                        shown.layer = 5;
                        var shownRect = (RectTransform)shown.transform;
                        shownRect.SetParent(tileRect, false);
                        // (A square in the tile's upper part: the preview is square.)
                        shownRect.anchorMin = shownRect.anchorMax = new Vector2(0.5f, 1f);
                        shownRect.pivot = new Vector2(0.5f, 1f);
                        shownRect.anchoredPosition = new Vector2(0f, -4f);
                        shownRect.sizeDelta = new Vector2(tileHeight - 32f, tileHeight - 32f);
                        RawImage image = shown.AddComponent<RawImage>();
                        image.texture = picture;
                        image.raycastTarget = false;
                    }
                }
            }
            Gap(6f);
            ActionRow("Empty, for that side: its soldiers get in on their own.", Sandbox.Armed == Sandbox.ToolKind.Vehicle ? "Put away" : "Take the tool", () => TakeOrPutAway(Sandbox.ToolKind.Vehicle),
                      "Here", () => SpawnInFront(true), 190f);
        }

        private static string VehicleName(VehicleSpawner.VehicleSpawnType t)
        {
            switch (t)
            {
                case VehicleSpawner.VehicleSpawnType.JeepMachineGun: return "Jeep MG";
                case VehicleSpawner.VehicleSpawnType.AttackHelicopter: return "Attack heli";
                case VehicleSpawner.VehicleSpawnType.TransportHelicopter: return "Transport heli";
                case VehicleSpawner.VehicleSpawnType.AttackPlane: return "Attack plane";
                case VehicleSpawner.VehicleSpawnType.BomberPlane: return "Bomber";
                case VehicleSpawner.VehicleSpawnType.AttackBoat: return "Attack boat";
                case VehicleSpawner.VehicleSpawnType.Rhib: return "RHIB";
                case VehicleSpawner.VehicleSpawnType.Apc: return "APC";
                default: return t.ToString();
            }
        }

        private void Field()
        {
            Section("The soldiers");
            ActionRow($"Every soldier: {(Sandbox.Paused ? "PAUSED (no step, no shot)" : "running")}.", Sandbox.Paused ? "Play" : "Pause", () => Say(Sandbox.SetPaused(!Sandbox.Paused)));
            ActionRow($"Quiet battlefield (none of the match's own soldiers): {(Sandbox.Quiet ? "on" : "off")}.", Sandbox.Quiet ? "Turn off" : "Turn on", () => Say(Sandbox.SetQuiet(!Sandbox.Quiet)));
            ActionRow("Every enemy soldier.", "Kill", () => Say(Sandbox.HostsField ? $"{Sandbox.KillEnemies()} enemy soldier(s) killed." : "In a shared match the field is the host's to change."));
            Section("What the sandbox made");
            ActionRow($"A dead one is taken off the field and stays away: {(Sandbox.RemoveDead ? "on" : "off (the match brings him back at its spawn points)")}.", Sandbox.RemoveDead ? "Turn off" : "Turn on", () => Say(Sandbox.SetRemoveDead(!Sandbox.RemoveDead)));
            if (Sandbox.RemoveDead)
            {
                Choice("His body lies", BodyNames, Array.IndexOf(BodyTimes, Sandbox.BodySeconds), i => Sandbox.BodySeconds = BodyTimes[i]);
            }
            ActionRow("Take it off the field again.", "The last", () => Say(Sandbox.RemoveLast() ? "Removed." : "Nothing of the sandbox's is left."),
                      "All", () => Say($"{Sandbox.RemoveAll()} removed."), 150f);
            ActionRow($"This map's set: {Sandbox.PlacedCount} placed{(Sandbox.HasSavedSet ? ", one saved" : "")}.", "Save", () => Say(Sandbox.SaveSet()),
                      "Load", () => Say(Sandbox.LoadSet()), 150f);
            ActionRow("The remover: point at a soldier or a vehicle, pull.", Sandbox.Armed == Sandbox.ToolKind.Remove ? "Put away" : "Take it", () => TakeOrPutAway(Sandbox.ToolKind.Remove));
        }

        /// <summary>What is the player's own: no damage, flying, health and ammunition, the tool, the way to the VR settings.</summary>
        private void Me()
        {
            Section("You");
            ActionRow($"You take no damage: {(Sandbox.PlayerInvulnerable ? "on" : "off")}.", Sandbox.PlayerInvulnerable ? "Turn off" : "Turn on", () => Sandbox.SetPlayerInvulnerable(!Sandbox.PlayerInvulnerable));
            ActionRow($"Flying: {(Sandbox.Fly ? "on (the walking stick moves you where you look)" : "off")}.", Sandbox.Fly ? "Land" : "Fly", () => Say(Sandbox.SetFly(!Sandbox.Fly)));
            ActionRow("Health and ammunition.", "Fill up", () => Say(Sandbox.Refill() ? "Filled up." : "Not on your feet."));
            Section("The tool");
            Label("It is in your gun hand while that hand holds nothing, else in the other hand.", 18, FontStyle.Italic, UiKit.MutedText, 34f);
            ActionRow(Sandbox.Armed == Sandbox.ToolKind.None ? "The tool is put away." : "The tool is in your hand.", Sandbox.Armed == Sandbox.ToolKind.None ? "Take it" : "Put away", () =>
            {
                if (Sandbox.Armed == Sandbox.ToolKind.None)
                {
                    Take(Sandbox.ToolKind.Soldier);
                }
                else
                {
                    Sandbox.Armed = Sandbox.ToolKind.None;
                }
            });
            ActionRow("The mod's own settings.", "VR settings", () =>
            {
                Close();
                UISystem.Instance?.Settings?.Open();
            });
        }

        private void TakeOrPutAway(Sandbox.ToolKind kind)
        {
            if (Sandbox.Armed == kind)
            {
                Sandbox.Armed = Sandbox.ToolKind.None; // (and the menu button opens the VR settings again)
                Say("The spawn gun is put away.");
                return;
            }
            Take(kind);
        }

        private void Take(Sandbox.ToolKind kind)
        {
            if (!GameContext.InGame)
            {
                Say("The tool is for a match.");
                return;
            }
            Sandbox.Armed = kind;
            Close();
            UISystem.Instance?.Notice?.Show("Sandbox tool: point with the empty hand, pull its trigger. Menu button: the panel.", 5f);
        }

        /// <summary>The panel's own spawn, without the tool: three (a vehicle: eight) metres in front of the player, on the ground there.</summary>
        private void SpawnInFront(bool vehicle)
        {
            Vector3 forward = Vector3.ProjectOnPlane(Rig.Head.forward, Vector3.up);
            forward = forward.sqrMagnitude > 1e-4f ? forward.normalized : Rig.Root.forward;
            Vector3 at = Rig.Head.position + forward * (vehicle ? 8f : 3f);
            const int mask = ~Hitbox.ALL_HITBOX_LAYER_MASK & ~(1 << 2);
            if (Physics.Raycast(at + Vector3.up * 3f, Vector3.down, out RaycastHit ground, 60f, mask, QueryTriggerInteraction.Ignore))
            {
                at = ground.point;
            }
            Say(SandboxTool.Make(vehicle ? Sandbox.ToolKind.Vehicle : Sandbox.ToolKind.Soldier, at, -forward));
        }
    }
}
