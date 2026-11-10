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
            Field
        }

        private static readonly string[] TabNames = { "Soldiers", "Weapons", "Vehicles", "Field" };
        private static readonly string[] SideNames = { "Enemy", "Friendly" };
        private static readonly string[] BehaviourNames = { "Normal", "Neutral", "Stand still", "Frozen", "Follow me" };
        private static readonly string[] WeaponNames = { "Their own", "Random", "None", "Mine" };
        private static readonly string[] HealthNames = { "Normal", "No damage", "One hit" };
        private static readonly int[] Counts = { 1, 2, 4, 8 };
        private static readonly string[] SlotNames = { "All", "Primary", "Secondary", "Gear" };
        private static readonly string[] BodyNames = { "5 s", "20 s", "1 min", "5 min" };
        private static readonly float[] BodyTimes = { 5f, 20f, 60f, 300f };
        private const int WeaponsPerPage = 8;

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

        protected override int Shown() =>
            // (Each in bits of its own: added up, two states could give one number and a change went unseen.)
            (Sandbox.Quiet ? 1 : 0) | (Sandbox.PlayerInvulnerable ? 2 : 0) | ((int)Sandbox.Armed << 2) | (GameContext.InGame ? 16 : 0)
            | (Time.unscaledTime < _statusUntil && _status.Length > 0 ? 32 : 0) | ((Sandbox.SpawnedCount & 0xFFF) << 6) | ((Sandbox.PlacedCount & 0xFFF) << 18);

        private void Say(string status)
        {
            _status = status ?? "";
            _statusUntil = Time.unscaledTime + 6f;
        }

        protected override void Build()
        {
            Title("Sandbox", GameContext.InGame
                ? "Soldiers, vehicles and weapons made on the spot. Take the tool, point with the free hand, pull its trigger."
                : "For a match: start any game mode, then open this panel from the VR settings.");
            Choice("", TabNames, (int)_tab, i => _tab = (Tab)i);
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
                default:
                    Field();
                    break;
            }
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
                default: return "Fights as any soldier of the match.";
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
            for (int i = _weaponPage * WeaponsPerPage; i < Mathf.Min(list.Count, (_weaponPage + 1) * WeaponsPerPage); i++)
            {
                WeaponManager.WeaponEntry entry = list[i];
                ActionRow(entry.name, "Give me", () =>
                {
                    string name = Sandbox.Give(entry, out string problem);
                    Say(name != null ? name + " is yours." : "No weapon: " + problem + ".");
                }, null, null, 170f);
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
            // (Four to a row: eleven kinds in three rows.)
            for (int from = 0; from < types.Length; from += 4)
            {
                VehicleSpawner.VehicleSpawnType[] row = types.Skip(from).Take(4).ToArray();
                int chosen = Array.IndexOf(row, Sandbox.Vehicle);
                Choice("", row.Select(VehicleName).ToArray(), chosen, i => Sandbox.Vehicle = row[i], 180f);
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
            Section("The field");
            ActionRow($"Quiet battlefield (none of the match's own soldiers): {(Sandbox.Quiet ? "on" : "off")}.", Sandbox.Quiet ? "Turn off" : "Turn on", () => Say(Sandbox.SetQuiet(!Sandbox.Quiet)));
            ActionRow($"You take no damage: {(Sandbox.PlayerInvulnerable ? "on" : "off")}.", Sandbox.PlayerInvulnerable ? "Turn off" : "Turn on", () => Sandbox.SetPlayerInvulnerable(!Sandbox.PlayerInvulnerable));
            ActionRow("Health and ammunition.", "Fill up", () => Say(Sandbox.Refill() ? "Filled up." : "Not on your feet."));
            ActionRow("Every enemy soldier.", "Kill", () => Say($"{Sandbox.KillEnemies()} enemy soldier(s) killed."));
            Section("What the sandbox made");
            Choice("A dead one's body lies", BodyNames, Array.IndexOf(BodyTimes, Sandbox.BodySeconds), i => Sandbox.BodySeconds = BodyTimes[i]);
            Label("Then he is taken off the field with it. A sandbox soldier never comes back by himself.", 18, FontStyle.Italic, UiKit.MutedText, 34f);
            ActionRow("Take it off the field again.", "The last", () => Say(Sandbox.RemoveLast() ? "Removed." : "Nothing of the sandbox's is left."),
                      "All", () => Say($"{Sandbox.RemoveAll()} removed."), 150f);
            ActionRow($"This map's set: {Sandbox.PlacedCount} placed{(Sandbox.HasSavedSet ? ", one saved" : "")}.", "Save", () => Say(Sandbox.SaveSet()),
                      "Load", () => Say(Sandbox.LoadSet()), 150f);
            ActionRow(Sandbox.Armed == Sandbox.ToolKind.None ? "The spawn tool is put away (take it on the Soldiers or Vehicles page)." : "The spawn tool is in your hand.", Sandbox.Armed == Sandbox.ToolKind.None ? "Away" : "Put away", () =>
            {
                Sandbox.Armed = Sandbox.ToolKind.None;
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
            UISystem.Instance?.Notice?.Show("Sandbox tool: point with the free hand, pull its trigger. Menu button: the panel.", 5f);
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
