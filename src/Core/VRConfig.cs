using System;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;

namespace RavenfieldVR.Core
{
    public enum Handedness { Right, Left }
    public enum StereoMode { MultiPass, SinglePassInstanced }
    public enum MirrorMode { None, Left, Right, OpenVR }
    public enum TurnMode { Snap, Smooth, Off }
    public enum MoveReference { Head, OffHand }
    public enum SprintMode { Toggle, Hold }
    public enum ReloadMode { Manual, Assisted }
    public enum PouchLocation { OffHandHip, DominantHip, Chest, OffHandChest }
    public enum GunAngleMode { Auto, Manual }
    public enum TurretAimMode { Head, Hand }

    /// <summary>
    /// Every user-tunable setting, bound to BepInEx/config/com.qwavey.ravenfieldvr.cfg.
    /// Most values are read live each frame, so edits from the in-VR settings panel or a
    /// config manager apply immediately.
    /// </summary>
    public static class VRConfig
    {
        // General
        public static ConfigEntry<bool> EnableVR;
        public static ConfigEntry<bool> AutoStartSteamVR;
        public static ConfigEntry<Handedness> DominantHand;

        // Rendering
        public static ConfigEntry<StereoMode> Stereo;
        public static ConfigEntry<float> RenderScale;
        public static ConfigEntry<MirrorMode> Mirror;
        public static ConfigEntry<float> NearClip;
        public static ConfigEntry<bool> PostProcessing;
        public static ConfigEntry<float> MinFarClip;

        // Comfort
        public static ConfigEntry<TurnMode> Turn;
        public static ConfigEntry<float> SnapTurnAngle;
        public static ConfigEntry<float> SmoothTurnSpeed;
        public static ConfigEntry<MoveReference> MoveDirection;
        public static ConfigEntry<float> Vignette;
        public static ConfigEntry<bool> PhysicalCrouch;
        public static ConfigEntry<float> PhysicalCrouchRatio;
        public static ConfigEntry<bool> RoomscaleMovement;
        public static ConfigEntry<float> EyeHeight;
        public static ConfigEntry<bool> SeatedMode;
        public static ConfigEntry<bool> SmoothVehicleHorizon;

        // Controls
        public static ConfigEntry<bool> StickJumpCrouch;
        public static ConfigEntry<float> StickDeadzone;
        public static ConfigEntry<SprintMode> Sprint;
        public static ConfigEntry<float> HapticStrength;
        public static ConfigEntry<GunAngleMode> GunAngle;
        public static ConfigEntry<string> GunRotationOffset;
        public static ConfigEntry<string> GunPositionOffset;
        public static ConfigEntry<float> GunPitchTrim;
        public static ConfigEntry<string> HandModelRotation;
        public static ConfigEntry<string> HandModelPosition;
        public static ConfigEntry<string> LaserRotationOffset;
        public static ConfigEntry<TurretAimMode> TurretAim;

        // Weapons
        public static ConfigEntry<ReloadMode> Reload;
        public static ConfigEntry<bool> ChamberPlusOne;
        public static ConfigEntry<bool> ReturnRoundsOnMagDrop;
        public static ConfigEntry<bool> LoseRoundOnRack;
        public static ConfigEntry<PouchLocation> Pouch;
        public static ConfigEntry<float> MagPropLifetime;
        public static ConfigEntry<int> MaxMagProps;
        public static ConfigEntry<float> RecoilStrength;
        public static ConfigEntry<float> TwoHandRecoilReduction;
        public static ConfigEntry<bool> TwoHandedAiming;
        public static ConfigEntry<bool> VRScopes;
        public static ConfigEntry<int> ScopeResolution;
        public static ConfigEntry<bool> HolsterSwitching;
        public static ConfigEntry<float> ThrowVelocityMultiplier;
        public static ConfigEntry<float> MeleeSwingSpeed;
        public static ConfigEntry<float> GrabRadius;

        // UI
        public static ConfigEntry<float> HudDistance;
        public static ConfigEntry<float> HudHorizontalFov;
        public static ConfigEntry<float> MenuDistance;
        public static ConfigEntry<float> MenuWidth;
        public static ConfigEntry<bool> WristHud;
        public static ConfigEntry<bool> GunAmmoCounter;
        public static ConfigEntry<string> LaserColor;

        // Debug
        public static ConfigEntry<bool> DebugLogging;
        public static ConfigEntry<bool> ShowGrabZones;
        public static ConfigEntry<bool> DumpWeaponRigs;

        public static void Bind(ConfigFile cfg)
        {
            EnableVR = cfg.Bind("General", "EnableVR", true,
                "Start the game in VR. Turn off to play flat with the mod installed.");
            AutoStartSteamVR = cfg.Bind("General", "AutoStartSteamVR", false,
                "Start SteamVR yourself when the game launches and a headset driver reports a headset. Off: VR starts only if SteamVR is already running (or with -vr), so flat play never opens SteamVR.");
            DominantHand = cfg.Bind("General", "DominantHand", Handedness.Right,
                "Hand that holds the weapon. The other hand handles foregrips, magazines and slides.");

            Stereo = cfg.Bind("Rendering", "StereoMode", StereoMode.MultiPass,
                "MultiPass works with every shader. SinglePassInstanced is faster but some map/mod shaders then render in one eye only. Restart required.");
            RenderScale = cfg.Bind("Rendering", "RenderScale", 1.0f,
                new ConfigDescription("Eye texture resolution multiplier on top of SteamVR's own resolution.", new AcceptableValueRange<float>(0.5f, 2.0f)));
            Mirror = cfg.Bind("Rendering", "MirrorView", MirrorMode.Right,
                "What the desktop window shows. Restart required.");
            NearClip = cfg.Bind("Rendering", "NearClip", 0.02f,
                new ConfigDescription("Camera near plane in metres. Lower lets you bring scopes closer to your eye.", new AcceptableValueRange<float>(0.005f, 0.2f)));
            PostProcessing = cfg.Bind("Rendering", "PostProcessing", false,
                "Copy the game's post-processing stack (colour grading, bloom, AO) onto the VR camera. Costs performance.");
            MinFarClip = cfg.Bind("Rendering", "MinFarClip", 1500f,
                "Smallest far plane used for the VR camera, in metres. The game's draw distance is used when larger.");

            Turn = cfg.Bind("Comfort", "TurnMode", TurnMode.Snap, "Right stick turning style.");
            SnapTurnAngle = cfg.Bind("Comfort", "SnapTurnAngle", 30f,
                new ConfigDescription("Degrees per snap turn.", new AcceptableValueRange<float>(10f, 90f)));
            SmoothTurnSpeed = cfg.Bind("Comfort", "SmoothTurnSpeed", 120f,
                new ConfigDescription("Degrees per second for smooth turning.", new AcceptableValueRange<float>(30f, 360f)));
            MoveDirection = cfg.Bind("Comfort", "MoveDirection", MoveReference.Head,
                "Whether the movement stick follows where you look or where your off hand points.");
            Vignette = cfg.Bind("Comfort", "Vignette", 0.35f,
                new ConfigDescription("Tunnel vignette strength while moving or smooth turning (0 = off).", new AcceptableValueRange<float>(0f, 1f)));
            PhysicalCrouch = cfg.Bind("Comfort", "PhysicalCrouch", true,
                "Crouching in real life makes your soldier crouch.");
            PhysicalCrouchRatio = cfg.Bind("Comfort", "PhysicalCrouchRatio", 0.75f,
                new ConfigDescription("Head height (fraction of calibrated eye height) below which you count as crouching.", new AcceptableValueRange<float>(0.5f, 0.9f)));
            RoomscaleMovement = cfg.Bind("Comfort", "RoomscaleMovement", true,
                "Walking around your play space moves your soldier (with collision). Off keeps the soldier where the stick puts it.");
            EyeHeight = cfg.Bind("Comfort", "EyeHeight", 0f,
                "Your standing (or seated) eye height in metres. 0 = calibrate automatically on first spawn. Re-calibrate from the VR settings panel.");
            SeatedMode = cfg.Bind("Comfort", "SeatedMode", false,
                "Play seated: disables physical crouch and treats the calibrated height as standing height.");
            SmoothVehicleHorizon = cfg.Bind("Comfort", "LevelHorizonInVehicles", false,
                "Keep the horizon level in aircraft/vehicles instead of rolling with the cockpit.");

            StickJumpCrouch = cfg.Bind("Controls", "TurnStickJumpCrouch", true,
                "Push the turn stick up to jump and down to toggle crouch.");
            StickDeadzone = cfg.Bind("Controls", "StickDeadzone", 0.15f,
                new ConfigDescription("Thumbstick dead zone.", new AcceptableValueRange<float>(0f, 0.6f)));
            Sprint = cfg.Bind("Controls", "SprintMode", SprintMode.Toggle, "Sprint button behaviour.");
            HapticStrength = cfg.Bind("Controls", "HapticStrength", 1f,
                new ConfigDescription("Controller vibration strength.", new AcceptableValueRange<float>(0f, 2f)));
            GunAngle = cfg.Bind("Controls", "GunAngleMode", GunAngleMode.Auto,
                "Auto aligns guns with the controller's pointing direction (SteamVR tip pose). Manual uses GunRotationOffset.");
            GunRotationOffset = cfg.Bind("Controls", "GunRotationOffset", "35, 0, 0",
                "Euler degrees (pitch, yaw, roll) from the controller grip pose to the gun barrel, used in Manual mode or when the tip pose is unavailable.");
            GunPositionOffset = cfg.Bind("Controls", "GunPositionOffset", "0, 0, 0",
                "Metres (right, up, forward) a held gun's grip is shifted from your palm centre.");
            GunPitchTrim = cfg.Bind("Controls", "GunPitchTrim", 0f,
                new ConfigDescription("Extra pitch (degrees) applied to held guns on top of the alignment above.", new AcceptableValueRange<float>(-45f, 45f)));
            HandModelRotation = cfg.Bind("Controls", "HandModelRotation", "0, 0, 0",
                "Euler degrees applied to the hand model on top of the palm frame (mirrored for the left hand).");
            HandModelPosition = cfg.Bind("Controls", "HandModelPosition", "0, 0, 0",
                "Metres (right, up, forward) the hand model is shifted from the palm centre (mirrored for the left hand).");
            LaserRotationOffset = cfg.Bind("Controls", "LaserRotationOffset", "40, 0, 0",
                "Euler degrees for the menu laser when the controller has no tip pose.");
            TurretAim = cfg.Bind("Controls", "TurretAim", TurretAimMode.Head,
                "Mounted guns and vehicle turrets follow your head or your weapon hand's pointing direction.");

            Reload = cfg.Bind("Weapons", "ReloadMode", ReloadMode.Manual,
                "Manual: eject, grab and insert magazines and rack slides yourself. Assisted: the magazine release button plays the game's normal reload.");
            ChamberPlusOne = cfg.Bind("Weapons", "ChamberPlusOne", true,
                "A chambered round counts on top of a full magazine (30+1).");
            ReturnRoundsOnMagDrop = cfg.Bind("Weapons", "ReturnRoundsOnMagDrop", true,
                "Rounds left in a dropped magazine go back to your reserve (Ravenfield's normal behaviour). Off = they are lost.");
            LoseRoundOnRack = cfg.Bind("Weapons", "LoseRoundOnRack", true,
                "Racking with a round already chambered ejects (and loses) that round.");
            Pouch = cfg.Bind("Weapons", "MagazinePouch", PouchLocation.OffHandHip,
                "Where fresh magazines/shells/rockets are grabbed from.");
            MagPropLifetime = cfg.Bind("Weapons", "DroppedMagLifetime", 20f, "Seconds dropped magazines stay in the world.");
            MaxMagProps = cfg.Bind("Weapons", "MaxDroppedMags", 12, "Maximum dropped magazines kept at once.");
            RecoilStrength = cfg.Bind("Weapons", "RecoilStrength", 1f,
                new ConfigDescription("Visual/aim recoil applied to the gun in your hand.", new AcceptableValueRange<float>(0f, 3f)));
            TwoHandRecoilReduction = cfg.Bind("Weapons", "TwoHandRecoilReduction", 0.6f,
                new ConfigDescription("Fraction of recoil removed while holding the foregrip.", new AcceptableValueRange<float>(0f, 1f)));
            TwoHandedAiming = cfg.Bind("Weapons", "TwoHandedAiming", true, "Gripping the foregrip steers the barrel with both hands.");
            VRScopes = cfg.Bind("Weapons", "VRScopes", true,
                "Magnified picture-in-picture scope lens for scoped weapons that only had a 2D scope overlay.");
            ScopeResolution = cfg.Bind("Weapons", "ScopeResolution", 768,
                new ConfigDescription("Scope lens render texture size.", new AcceptableValueRange<int>(256, 2048)));
            HolsterSwitching = cfg.Bind("Weapons", "HolsterSwitching", true,
                "Reach over your shoulder / to your hip / chest and grip to draw primary, sidearm or gear.");
            ThrowVelocityMultiplier = cfg.Bind("Weapons", "ThrowVelocityMultiplier", 1.4f,
                new ConfigDescription("Multiplier on hand speed when throwing.", new AcceptableValueRange<float>(0.5f, 4f)));
            MeleeSwingSpeed = cfg.Bind("Weapons", "MeleeSwingSpeed", 2.6f,
                new ConfigDescription("Blade speed (m/s) needed for a melee hit.", new AcceptableValueRange<float>(1f, 8f)));
            GrabRadius = cfg.Bind("Weapons", "GrabRadius", 0.09f,
                new ConfigDescription("How close (m) your hand must be to grab a slide, foregrip or magazine.", new AcceptableValueRange<float>(0.03f, 0.25f)));

            HudDistance = cfg.Bind("UI", "HudDistance", 4f,
                new ConfigDescription("Distance (m) of the head-locked HUD frame.", new AcceptableValueRange<float>(1f, 20f)));
            HudHorizontalFov = cfg.Bind("UI", "HudHorizontalFov", 72f,
                new ConfigDescription("Horizontal angle the HUD frame spans. Smaller keeps HUD elements closer to the centre.", new AcceptableValueRange<float>(40f, 110f)));
            MenuDistance = cfg.Bind("UI", "MenuDistance", 1.5f,
                new ConfigDescription("Distance (m) menus appear in front of you.", new AcceptableValueRange<float>(0.6f, 4f)));
            MenuWidth = cfg.Bind("UI", "MenuWidth", 1.7f,
                new ConfigDescription("Width (m) of menu panels.", new AcceptableValueRange<float>(0.6f, 5f)));
            WristHud = cfg.Bind("UI", "WristHud", true, "Health and reserve ammo on your off-hand wrist.");
            GunAmmoCounter = cfg.Bind("UI", "GunAmmoCounter", true, "Small round counter on the side of the held weapon.");
            LaserColor = cfg.Bind("UI", "LaserColor", "#E8B44A", "Menu laser colour (hex).");

            DebugLogging = cfg.Bind("Debug", "VerboseLogging", false, "Log extra diagnostics.");
            ShowGrabZones = cfg.Bind("Debug", "ShowGrabZones", false, "Draw spheres at mag wells, slides, foregrips, pouches and holsters.");
            DumpWeaponRigs = cfg.Bind("Debug", "DumpWeaponRigs", false, "Log the detected parts of every weapon you equip.");
        }

        public static bool LeftHanded => DominantHand.Value == Handedness.Left;

        public static Vector3 ParseVector(ConfigEntry<string> entry, Vector3 fallback)
        {
            if (TryParseVector(entry.Value, out Vector3 v))
            {
                return v;
            }
            Log.WarnOnce("vec:" + entry.Definition.Key, $"Config {entry.Definition.Section}.{entry.Definition.Key} = '{entry.Value}' is not 'x, y, z'; using {fallback}.");
            return fallback;
        }

        public static bool TryParseVector(string text, out Vector3 v)
        {
            v = Vector3.zero;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }
            string[] parts = text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3)
            {
                return false;
            }
            float[] f = new float[3];
            for (int i = 0; i < 3; i++)
            {
                if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out f[i]))
                {
                    return false;
                }
            }
            v = new Vector3(f[0], f[1], f[2]);
            return true;
        }

        public static string FormatVector(Vector3 v) =>
            string.Format(CultureInfo.InvariantCulture, "{0:0.###}, {1:0.###}, {2:0.###}", v.x, v.y, v.z);

        public static Color ParseColor(ConfigEntry<string> entry, Color fallback)
        {
            return ColorUtility.TryParseHtmlString(entry.Value, out Color c) ? c : fallback;
        }
    }
}
