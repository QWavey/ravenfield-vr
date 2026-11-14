using System;
using RavenfieldVR.Core;
using UnityEngine;

namespace RavenfieldVR.Local
{
    /// <summary>
    /// A splitscreen copy of the game played by a script (-rfvr-splittest, started by the check's host): keys and mouse given to the copy as
    /// the host gives a player's, pressed by the game's own bindings (so whatever Options, Controls says): deploy, walk forward, strafe
    /// right, turn right and look down with the mouse, fire, reload, switch weapons, switch team and deploy on the other side. Each result is
    /// a line in the copy's own log, which the host's check reads (the user, 2026-11-14: "cant walk ... the other presses D and walks to the
    /// left ... controls are also inverted ... can also not shoot or reload or switch weapons").
    /// </summary>
    internal static class CopyTest
    {
        public static bool Running => _step >= 0 && _step < Done;

        private const int Done = 99;
        private static int _step = -1;
        private static float _stepAt, _ammoLast, _yawFrom, _lookFrom;
        private static Vector3 _from, _rightFrom;
        private static int _shots, _slotFrom, _teamFrom;
        private static bool _saidAim;
        private static string _primaryFrom = "";
        private static float _healthFrom;
        private static float _turned;

        private static void Next(int step)
        {
            _step = step;
            _stepAt = Time.unscaledTime;
        }

        private static void Clear(FlatPlayerInput.DeviceState s)
        {
            Array.Clear(s.Keys, 0, s.Keys.Length);
            s.Left = s.Right = s.Middle = s.X1 = s.X2 = false;
        }

        private static Camera Cam => FpsActorController.instance != null ? FpsActorController.instance.fpCamera : null;

        public static void Update(FlatPlayerInput.DeviceState s, Action<string> note)
        {
            Actor me = LocalPlayer.actor;
            if (me == null)
            {
                return;
            }
            float t = Time.unscaledTime - _stepAt;
            Clear(s);
            void Press(SteelInput.KeyBinds b, bool positive = true) => FlatPlayerInput.PressBind(s, b, positive);
            switch (_step)
            {
                case -1:
                    note($"copy test: started (the game's bindings: forward {FlatPlayerInput.KeyName(SteelInput.KeyBinds.Vertical)}, fire {FlatPlayerInput.KeyName(SteelInput.KeyBinds.Fire)}, reload {FlatPlayerInput.KeyName(SteelInput.KeyBinds.Reload)})");
                    Next(0);
                    break;
                case 0: // deploy: the fire key pressed every 2 s until alive
                case 20: // deploy again on the other side
                    if (!me.dead)
                    {
                        if (_step == 20)
                        {
                            note($"copy test: spawned on the other side (team {me.team}, player team {GameManager.PlayerTeam()}) after {t:0.0} s");
                            note(me.team != _teamFrom ? "copy test: team switch worked" : "copy test: team switch DID NOT CHANGE THE SOLDIER'S TEAM");
                            Next(40);
                            return;
                        }
                        note($"copy test: spawned (team {me.team}) after {t:0.0} s");
                        _from = me.Position();
                        Next(10);
                        return;
                    }
                    if (t % 2f < 0.15f)
                    {
                        Press(SteelInput.KeyBinds.Fire);
                    }
                    if (t > 25f)
                    {
                        note($"copy test: NOT SPAWNED in 25 s ({(_step == 0 ? "first deploy" : "after the team switch")})");
                        Next(Done);
                        note("copy test: done");
                    }
                    break;
                case 10: // stand still 3 s, looking ahead (the stand-in on the host must stand: no walking in place)
                    if (t > 3f)
                    {
                        Camera c = Cam;
                        note($"copy test: stood still 3 s, moved {Vector3.Distance(me.Position(), _from):0.00} m; looking yaw {(c != null ? c.transform.eulerAngles.y : 0f):0} pitch {(c != null ? -Mathf.DeltaAngle(0f, c.transform.eulerAngles.x) : 0f):0}");
                        _from = me.Position();
                        Next(1);
                    }
                    break;
                case 1: // forward 2 s
                    Press(SteelInput.KeyBinds.Vertical, true);
                    if (t > 2f)
                    {
                        Vector3 moved = Vector3.ProjectOnPlane(me.Position() - _from, Vector3.up);
                        Vector3 fwd = Cam != null ? Vector3.ProjectOnPlane(Cam.transform.forward, Vector3.up).normalized : Vector3.forward;
                        note($"copy test: forward key: moved {moved.magnitude:0.0} m, {Vector3.Dot(moved, fwd):0.0} m ahead{(Vector3.Dot(moved, fwd) < 1f ? " (DID NOT WALK FORWARD)" : "")}");
                        _from = me.Position();
                        _rightFrom = Cam != null ? Vector3.ProjectOnPlane(Cam.transform.right, Vector3.up).normalized : Vector3.right;
                        Next(2);
                    }
                    break;
                case 2: // strafe right 1.5 s: the key the game's binding puts on the right (its sideways bind is positive to the left)
                    Press(SteelInput.KeyBinds.Horizontal, false);
                    if (t > 1.5f)
                    {
                        float right = Vector3.Dot(me.Position() - _from, _rightFrom);
                        note($"copy test: right key ({NegName(SteelInput.KeyBinds.Horizontal)}): moved {right:0.0} m to the right{(right < 0.5f ? (right < -0.5f ? " (WENT LEFT: INVERTED)" : " (DID NOT MOVE RIGHT)") : "")}");
                        _yawFrom = Cam != null ? Cam.transform.eulerAngles.y : 0f;
                        Next(3);
                    }
                    break;
                case 3: // the mouse moved right for half a second: the view must turn right
                    if (t < 0.5f)
                    {
                        // (Gently, the turn added up frame by frame: 8 counts a frame turned past half a circle and the angle wrapped, so a
                        // right turn read as "left"; 2026-11-14 23:05.)
                        s.TotalX += 1.5f;
                        if (Cam != null)
                        {
                            float y = Cam.transform.eulerAngles.y;
                            _turned += Mathf.DeltaAngle(_yawFrom, y);
                            _yawFrom = y;
                        }
                        if (t > 0.2f && !_saidAim)
                        {
                            _saidAim = true;
                            SteelInput.Axis ax = SteelInput.GetInput(SteelInput.KeyBinds.AimX);
                            note($"copy test: mouse right: the game reads AimX {SteelInput.GetAxis(SteelInput.KeyBinds.AimX):0.00} (binding {ax.unityAxis}, inverted {ax.invertUnityAxis}); body yaw {me.transform.eulerAngles.y:0}, camera yaw {(Cam != null ? Cam.transform.eulerAngles.y : 0f):0}");
                        }
                    }
                    else
                    {
                        float turned = _turned + (Cam != null ? Mathf.DeltaAngle(_yawFrom, Cam.transform.eulerAngles.y) : 0f);
                        note($"copy test: after the mouse right: body yaw {me.transform.eulerAngles.y:0}, camera yaw {(Cam != null ? Cam.transform.eulerAngles.y : 0f):0} (from {_yawFrom:0})");
                        note($"copy test: mouse moved right: the view turned {turned:0} deg{(turned < 5f ? (turned < -5f ? " (TURNED LEFT: INVERTED)" : " (DID NOT TURN)") : " to the right")}");
                        _lookFrom = Cam != null ? Cam.transform.forward.y : 0f;
                        Next(4);
                    }
                    break;
                case 4: // the mouse moved down (Windows' counts grow toward the player): the view must go down
                    if (t < 0.4f)
                    {
                        s.TotalY += 6f;
                    }
                    else
                    {
                        float now = Cam != null ? Cam.transform.forward.y : 0f;
                        note($"copy test: mouse moved down: the view went {(now < _lookFrom - 0.02f ? "down" : now > _lookFrom + 0.02f ? "UP (INVERTED)" : "NOWHERE (NO LOOK)")} ({_lookFrom:0.00} -> {now:0.00})");
                        s.TotalY -= 0f;
                        _ammoLast = me.activeWeapon != null ? me.activeWeapon.ammo : -1;
                        _shots = 0;
                        Next(5);
                    }
                    break;
                case 5: // fire 1.5 s
                    if (t % 0.2f < 0.1f)
                    {
                        Press(SteelInput.KeyBinds.Fire); // (pulled again and again: a semi-automatic fires too)
                    }
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
                        Next(6);
                    }
                    break;
                case 6: // reload
                    if (t < 0.15f)
                    {
                        Press(SteelInput.KeyBinds.Reload);
                    }
                    if (t > 0.6f)
                    {
                        Weapon w = me.activeWeapon;
                        bool ok = w != null && (w.reloading || w.ammo == w.configuration.ammo);
                        note($"copy test: reload key: reload {(ok ? "started" : "DID NOT START")}");
                        _slotFrom = me.activeWeaponSlot;
                        Next(7);
                    }
                    break;
                case 7: // next weapon (after the reload)
                    if (t > 2.5f && t < 2.65f)
                    {
                        Press(SteelInput.KeyBinds.NextWeapon);
                    }
                    if (t > 4f)
                    {
                        note($"copy test: next weapon key: slot {_slotFrom + 1} -> {me.activeWeaponSlot + 1}{(me.activeWeaponSlot == _slotFrom ? " (DID NOT SWITCH)" : "")}");
                        _teamFrom = me.team;
                        Next(30);
                    }
                    break;
                case 30: // Esc: this player's own menu, the game not paused; down three rows to Primary, D changes it, Esc closes
                    {
                        string before = LoadoutUi.instance != null && LoadoutUi.instance.loadout != null && LoadoutUi.instance.loadout.primary != null ? LoadoutUi.instance.loadout.primary.name : "none";
                        if (t < 0.1f)
                        {
                            s.Keys[0x1B] = true;
                            _primaryFrom = before;
                        }
                        else if (t > 0.3f && t < 0.4f || t > 0.5f && t < 0.6f || t > 0.7f && t < 0.8f)
                        {
                            Press(SteelInput.KeyBinds.Vertical, false); // (down)
                        }
                        else if (t > 0.9f && t < 1.0f)
                        {
                            Press(SteelInput.KeyBinds.Horizontal, false); // (right: the next weapon)
                        }
                        else if (t > 1.2f && t < 1.25f)
                        {
                            note($"copy test: Esc menu {(SplitCopyMenu.Open ? "open" : "NOT OPEN")}, the game {(GameManager.IsPaused() ? "PAUSED" : "not paused")}; primary {_primaryFrom} -> {before}{(before == _primaryFrom ? " (NOT CHANGED)" : "")}");
                        }
                        else if (t > 1.4f && t < 1.5f)
                        {
                            s.Keys[0x1B] = true;
                        }
                        if (t > 1.8f)
                        {
                            note($"copy test: Esc again: menu {(SplitCopyMenu.Open ? "STILL OPEN" : "closed")}");
                            Next(8);
                        }
                    }
                    break;
                case 40: // damage both ways, on opposite teams now: this player hits player 1's soldier, then player 1 hits this one
                    if (t < 0.05f)
                    {
                        Actor host = null;
                        foreach (Net.NetGame.Remote r in Net.NetGame.AllRemotes)
                        {
                            if (r.Id == 0 && r.Actor != null && !r.Actor.dead)
                            {
                                host = r.Actor;
                            }
                        }
                        if (host == null)
                        {
                            note("copy test: NO SOLDIER OF PLAYER 1 to hit");
                        }
                        else
                        {
                            host.Damage(new DamageInfo(DamageInfo.DamageSourceType.Projectile, me, me.activeWeapon)
                            {
                                healthDamage = 25f, point = host.CenterPosition(), direction = (host.CenterPosition() - me.CenterPosition()).normalized,
                            });
                            note($"copy test: hit player 1's soldier for 25 (team {host.team}, this player team {me.team})");
                        }
                        _healthFrom = me.health;
                        note($"copy test: hit me now (health {me.health:0})");
                    }
                    if (t > 6f)
                    {
                        float took = _healthFrom - (me.dead ? 0f : me.health);
                        note($"copy test: player 1's and an enemy AI's hits (25 each): this player's health {_healthFrom:0} -> {(me.dead ? "down" : me.health.ToString("0"))}{(took < 1f ? " (TOOK NO DAMAGE)" : took < 40f ? " (ONE HIT LOST)" : "")}");
                        Next(Done);
                        note("copy test: done");
                    }
                    break;
                case 8: // switch team (T)
                    if (t < 0.15f)
                    {
                        s.Keys[0x54] = true;
                    }
                    if (t > 1f)
                    {
                        note($"copy test: T pressed: player team {GameManager.PlayerTeam()}, soldier {(me.dead ? "down" : "STILL UP")}");
                        Next(20);
                    }
                    break;
            }
        }

        private static string NegName(SteelInput.KeyBinds bind)
        {
            try
            {
                return SteelInput.GetInput(bind).negKeyCode.ToString();
            }
            catch (Exception)
            {
                return "?";
            }
        }
    }
}
