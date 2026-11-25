using System;
using UnityEngine;

namespace RavenfieldVR.Util
{
    public enum VehicleKind { None, Ground, Boat, Helicopter, Airplane, Turret, Passenger }

    /// <summary>Null-safe accessors for the game state the VR systems branch on.</summary>
    public static class GameContext
    {
        public static FpsActorController Player
        {
            get
            {
                FpsActorController c = LocalPlayer.controller;
                return c != null && c.isActiveAndEnabled ? c : null;
            }
        }

        public static Actor PlayerActor
        {
            get
            {
                FpsActorController c = Player;
                return c != null ? c.actor : null;
            }
        }

        public static bool InGame => Player != null && !GameManager.IsSpectating();

        public static bool PlayerAlive
        {
            get
            {
                Actor a = PlayerActor;
                return a != null && !a.dead;
            }
        }

        public static bool PlayerSeated
        {
            get
            {
                Actor a = PlayerActor;
                return a != null && !a.dead && a.IsSeated();
            }
        }

        public static bool PlayerOnFoot
        {
            get
            {
                Actor a = PlayerActor;
                return a != null && !a.dead && !a.IsSeated() && !a.IsOnLadder() && !a.fallenOver;
            }
        }

        /// <summary>True whenever the game shows its cursor (pause, loadout, squad map, scoreboard focus, config flags).</summary>
        public static bool GameMenuOpen
        {
            get
            {
                try
                {
                    FpsActorController c = Player;
                    if (c != null)
                    {
                        return c.IsCursorFree();
                    }
                    return IngameMenuUi.instance != null && IngameMenuUi.IsOpen();
                }
                catch (Exception e)
                {
                    Core.Log.ExceptionOnce("GameContext.GameMenuOpen", e); // (said once: it was silent every frame it threw; util scout)
                    return false;
                }
            }
        }

        public static Weapon ActiveWeapon
        {
            get
            {
                Actor a = PlayerActor;
                if (a == null || a.dead || a.activeWeapon == null)
                {
                    return null;
                }
                return a.activeWeapon;
            }
        }

        /// <summary>The player's carried (not seat-mounted) weapon that is drawn and visible in first person.</summary>
        public static Weapon HeldPersonalWeapon
        {
            get
            {
                Actor a = PlayerActor;
                if (a == null || a.dead || a.fallenOver || a.IsOnLadder())
                {
                    return null;
                }
                Weapon w = a.activeWeapon;
                if (w == null || !w.gameObject.activeInHierarchy || w.IsMountedWeapon())
                {
                    return null;
                }
                if (a.IsSeated() && !a.seat.CanUsePersonalWeapons())
                {
                    return null;
                }
                return w;
            }
        }

        public static VehicleKind SeatKind
        {
            get
            {
                Actor a = PlayerActor;
                if (a == null || a.dead || !a.IsSeated())
                {
                    return VehicleKind.None;
                }
                Seat seat = a.seat;
                Vehicle v = seat.vehicle;
                if (!seat.IsDriverSeat())
                {
                    return seat.HasAnyMountedWeapons() ? VehicleKind.Turret : VehicleKind.Passenger;
                }
                if (v is Helicopter)
                {
                    return VehicleKind.Helicopter;
                }
                if (v is Airplane || v is Plane)
                {
                    return VehicleKind.Airplane;
                }
                if (v is Boat)
                {
                    return VehicleKind.Boat;
                }
                if (v is AnimationDrivenVehicle adv)
                {
                    // IsAircraft is false for every one of these (only Airplane and Helicopter override it); one that reads the
                    // airplane input (planeInput) flies on the stick, so the right stick must not turn the view too.
                    return adv.planeInput || adv.IsAircraft() ? VehicleKind.Airplane : VehicleKind.Ground;
                }
                if (v is Car || v is ArcadeCar || v is Tank)
                {
                    return VehicleKind.Ground;
                }
                // Stationary emplacements are plain Vehicles whose driver seat only carries weapons.
                return seat.HasAnyMountedWeapons() ? VehicleKind.Turret : VehicleKind.Ground;
            }
        }

        public static Camera GameMainCamera
        {
            get
            {
                try
                {
                    // After "Quit to menu" the game still points at the destroyed player, and its getter (a ?. test, which a
                    // destroyed object passes) threw every frame the rig asked: Unity's own test first, with the same fallbacks.
                    if (LocalPlayer.controller == null && !GameManager.IsSpectating())
                    {
                        SpectatorCamera s = SpectatorCamera.instance;
                        return s != null && s.camera != null && s.camera.enabled ? s.camera : Camera.main;
                    }
                    return GameManager.GetMainCamera();
                }
                catch (Exception e)
                {
                    Core.Log.ExceptionOnce("GameContext.GameMainCamera", e);
                    return Camera.main;
                }
            }
        }
    }
}
