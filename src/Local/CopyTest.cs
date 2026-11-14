using System;
using RavenfieldVR.Core;
using UnityEngine;

namespace RavenfieldVR.Local
{
    /// <summary>
    /// A splitscreen copy of the game played by a script, through the same binds the host sends it (-rfvr-splittest, started by the
    /// check's host): deploy, walk, look down with the mouse, fire, reload, switch weapons, switch team and deploy on the other side. Each
    /// result is a line in the copy's own log, which the host's check reads (the user, 2026-11-14: "cant walk ... my controls are inverted
    /// ... can also not shoot or reload or switch weapons").
    /// </summary>
    internal static class CopyTest
    {
        public static bool Running => _step >= 0 && _step < Done;

        private const int Done = 9;
        private static int _step = -1;
        private static float _stepAt, _ammoLast;
        private static Vector3 _from;
        private static float _lookFrom;
        private static int _shots, _slotFrom, _teamFrom, _deployTries;

        private static void Next(int step)
        {
            _step = step;
            _stepAt = Time.unscaledTime;
        }

        private static void Clear(FlatPlayerInput.Binds b)
        {
            Array.Clear(b.Axis, 0, b.Axis.Length);
            Array.Clear(b.Held, 0, b.Held.Length);
        }

        public static void Update(FlatPlayerInput.Binds b, Action<string> note)
        {
            Actor me = LocalPlayer.actor;
            if (me == null)
            {
                return;
            }
            float t = Time.unscaledTime - _stepAt;
            Clear(b);
            switch (_step)
            {
                case -1:
                    note("copy test: started");
                    Next(0);
                    break;
                case 0: // deploy: fire pressed every 2 s until alive
                case 6: // deploy again on the other side
                    if (!me.dead)
                    {
                        note(_step == 0 ? $"copy test: spawned (team {me.team}) after {t:0.0} s" : $"copy test: spawned on the other side (team {me.team}, player team {GameManager.PlayerTeam()}) after {t:0.0} s");
                        if (_step == 6)
                        {
                            note(me.team != _teamFrom ? "copy test: team switch worked" : "copy test: team switch DID NOT CHANGE THE SOLDIER'S TEAM");
                            Next(Done);
                            note("copy test: done");
                            return;
                        }
                        Next(1);
                        _from = me.Position();
                        return;
                    }
                    if (t % 2f < 0.15f)
                    {
                        b.Held[(int)SteelInput.KeyBinds.Fire] = true;
                    }
                    if (t > 25f)
                    {
                        note($"copy test: NOT SPAWNED in 25 s ({(_step == 0 ? "first deploy" : "after the team switch")})");
                        Next(Done);
                        note("copy test: done");
                    }
                    break;
                case 1: // walk forward 2 s
                    b.Axis[(int)SteelInput.KeyBinds.Vertical] = 1f;
                    if (t > 2f)
                    {
                        float moved = Vector3.ProjectOnPlane(me.Position() - _from, Vector3.up).magnitude;
                        note($"copy test: walked {moved:0.0} m in 2 s{(moved < 1f ? " (DID NOT WALK)" : "")}");
                        Camera cam = FpsActorController.instance != null ? FpsActorController.instance.fpCamera : null;
                        _lookFrom = cam != null ? cam.transform.forward.y : 0f;
                        Next(2);
                    }
                    break;
                case 2: // the mouse moved down (raw y grows toward the player) for half a second: the view must go down
                    if (t < 0.5f)
                    {
                        b.MouseY += 6f;
                    }
                    else
                    {
                        Camera cam = FpsActorController.instance != null ? FpsActorController.instance.fpCamera : null;
                        float now = cam != null ? cam.transform.forward.y : 0f;
                        note($"copy test: mouse moved down: the view went {(now < _lookFrom - 0.02f ? "down" : now > _lookFrom + 0.02f ? "UP (INVERTED)" : "NOWHERE (NO LOOK)")} ({_lookFrom:0.00} -> {now:0.00})");
                        b.MouseY -= 0f;
                        _ammoLast = me.activeWeapon != null ? me.activeWeapon.ammo : -1;
                        _shots = 0;
                        Next(3);
                    }
                    break;
                case 3: // fire 1.5 s
                    b.Held[(int)SteelInput.KeyBinds.Fire] = t % 0.2f < 0.1f; // (pulled again and again: a semi-automatic fires too)
                    if (me.activeWeapon != null)
                    {
                        if (me.activeWeapon.ammo < _ammoLast)
                        {
                            _shots += (int)(_ammoLast - me.activeWeapon.ammo);
                        }
                        _ammoLast = me.activeWeapon.ammo;
                    }
                    if (t > 1.5f)
                    {
                        note($"copy test: fired {_shots} round(s){(_shots <= 0 ? " (DID NOT FIRE)" : "")} with {(me.activeWeapon != null ? me.activeWeapon.name : "nothing")}");
                        Next(4);
                    }
                    break;
                case 4: // reload
                    b.Held[(int)SteelInput.KeyBinds.Reload] = t < 0.15f;
                    if (t > 0.6f)
                    {
                        Weapon w = me.activeWeapon;
                        bool ok = w != null && (w.reloading || w.ammo == w.configuration.ammo);
                        note($"copy test: reload {(ok ? "started" : "DID NOT START")}");
                        _slotFrom = me.activeWeaponSlot;
                        Next(5);
                    }
                    break;
                case 5: // next weapon (after the reload)
                    b.Held[(int)SteelInput.KeyBinds.NextWeapon] = t > 2.5f && t < 2.65f;
                    if (t > 4f)
                    {
                        note($"copy test: next weapon: slot {_slotFrom + 1} -> {me.activeWeaponSlot + 1}{(me.activeWeaponSlot == _slotFrom ? " (DID NOT SWITCH)" : "")}");
                        _teamFrom = me.team;
                        Next(7);
                    }
                    break;
                case 7: // switch team
                    b.Held[FlatPlayerInput.SwitchTeam] = t < 0.15f;
                    if (t > 1f)
                    {
                        note($"copy test: switch team pressed: player team {GameManager.PlayerTeam()}, soldier {(me.dead ? "down" : "STILL UP")}");
                        _deployTries = 0;
                        Next(6);
                    }
                    break;
            }
        }
    }
}
