using System.Collections.Generic;
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
    /// <summary>How forgiving a magazine well is (other VR shooters' "easy magazine loading").</summary>
    public enum MagazineHelp { Exact, Normal, Easy }
    public enum SlowMotionHands { Normal, Slowed }
    public enum PouchLocation { OffHandHip, DominantHip, Chest, OffHandChest }
    public enum GunAngleMode { Auto, Manual }
    public enum TurretAimMode { Head, Hand }

    /// <summary>
    /// Every user-tunable setting, bound to BepInEx/config/com.qwavey.ravenfieldvr.cfg.
    /// Most values are read live each frame, so edits from the in-VR settings panel or a
    /// config manager apply immediately.
    /// </summary>
    public enum HandsMode { VRHands, WeaponArms }
    public enum GloveStyle { Tactical, Simple }
    /// <summary>Where weapons are carried on the body.</summary>
    public enum HolsterLayout { Boneworks, Classic }

    public static class VRConfig
    {
        /// <summary>The laser's colour by default: the UI's blue accent (UiKit.Accent).</summary>
        private const string DefaultLaserColor = "#4E96E6";

        // General
        public static ConfigEntry<bool> EnableVR;
        public static ConfigEntry<bool> AutoStartSteamVR;
        public static ConfigEntry<bool> NoSteamVRTheater;
        public static ConfigEntry<Handedness> DominantHand;

        // Rendering
        public static ConfigEntry<StereoMode> Stereo;
        public static ConfigEntry<float> RenderScale;
        public static ConfigEntry<GraphicsPreset> Graphics;
        public static ConfigEntry<MirrorMode> Mirror;
        public static ConfigEntry<float> NearClip;
        public static ConfigEntry<bool> PostProcessing;
        public static ConfigEntry<float> GogglesTube;
        public static ConfigEntry<bool> BodyShadow;
        public static ConfigEntry<bool> WristFrameRate;
        public static ConfigEntry<float> MinFarClip;

        // Comfort
        public static ConfigEntry<TurnMode> Turn;
        public static ConfigEntry<float> SnapTurnAngle;
        public static ConfigEntry<float> SmoothTurnSpeed;
        public static ConfigEntry<MoveReference> MoveDirection;
        public static ConfigEntry<float> Vignette;
        public static ConfigEntry<bool> VehicleVignette;
        public static ConfigEntry<bool> StickHoldsTrim;
        public static ConfigEntry<bool> VoiceChat;
        public static ConfigEntry<string> VoiceDevice;
        public static ConfigEntry<float> VoiceVolume, VoiceMicGain, VoiceGate;
        public static ConfigEntry<bool> PhysicalCrouch;
        public static ConfigEntry<float> PhysicalCrouchRatio;
        public static ConfigEntry<bool> RoomscaleMovement;
        public static ConfigEntry<float> EyeHeight;
        public static ConfigEntry<bool> SeatedMode;
        public static ConfigEntry<bool> SmoothVehicleHorizon;
        public static ConfigEntry<bool> FallInFirstPerson;

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
        public static ConfigEntry<SlowMotionHands> SlowHands;
        public static ConfigEntry<bool> ChamberPlusOne;
        public static ConfigEntry<bool> ReturnRoundsOnMagDrop;
        public static ConfigEntry<bool> LoadMagazinesByHand;
        public static ConfigEntry<bool> ChamberByHand;
        public static ConfigEntry<bool> MagazineDetent;
        public static ConfigEntry<MagazineHelp> MagazineInsertHelp;
        public static ConfigEntry<int> ShellsPerReach;
        public static ConfigEntry<bool> UnderSeatedMagazineFalls;
        public static ConfigEntry<bool> AkMagazinesStayIn;
        public static ConfigEntry<bool> LoseRoundOnRack;
        public static ConfigEntry<PouchLocation> Pouch;
        public static ConfigEntry<float> MagPropLifetime;
        public static ConfigEntry<float> MagazineReach;
        public static ConfigEntry<float> GripLetGoBelow;
        public static ConfigEntry<int> MaxMagProps;
        public static ConfigEntry<float> RecoilStrength;
        public static ConfigEntry<float> TwoHandRecoilReduction;
        public static ConfigEntry<float> ScopeSteadying;
        public static ConfigEntry<float> AimSteadying;
        public static ConfigEntry<bool> WeaponWeight;
        public static ConfigEntry<bool> SkipDrawAnimation;
        public static ConfigEntry<HandsMode> Hands;
        public static ConfigEntry<GloveStyle> Glove;
        public static ConfigEntry<float> GloveSize;
        public static ConfigEntry<bool> TwoHandedAiming;
        public static ConfigEntry<bool> VRScopes;
        public static ConfigEntry<int> ScopeResolution;
        public static ConfigEntry<bool> ScopeShadows;
        public static ConfigEntry<bool> ScopeHalfRate;
        public static ConfigEntry<bool> HoldFrameRate;
        public static ConfigEntry<bool> GameCamerasOnePixel;
        public static ConfigEntry<float> GcSliceMs;
        public static ConfigEntry<bool> SmallObjectsNoShadows;
        public static ConfigEntry<bool> HideTinyDistantObjects;
        public static ConfigEntry<bool> InstanceRepeatedObjects;
        public static ConfigEntry<bool> HolsterSwitching;
        public static ConfigEntry<bool> GripToHoldWeapon;
        public static ConfigEntry<bool> SpawnEmptyHanded;
        public static ConfigEntry<bool> TriggerReleasesSlide;
        public static ConfigEntry<bool> MagSlapReleasesSlide;
        public static ConfigEntry<bool> MagazinePalming;
        public static ConfigEntry<bool> ReleaseDropsWeapon;
        public static ConfigEntry<float> WeaponReturnSeconds;
        public static ConfigEntry<bool> ShowHolsters;
        public static ConfigEntry<HolsterLayout> Holsters;
        public static ConfigEntry<bool> MagazineReadout;
        public static ConfigEntry<float> ThrowVelocityMultiplier;
        public static ConfigEntry<bool> GrenadePins;
        public static ConfigEntry<bool> BeltByHand;
        public static ConfigEntry<bool> HideGameWeaponHud;
        public static ConfigEntry<float> MeleeSwingSpeed;
        public static ConfigEntry<float> GrabRadius;
        public static ConfigEntry<bool> ModWeaponSounds;
        public static ConfigEntry<bool> FireSelector;
        public static ConfigEntry<bool> ShowPouch;
        public static ConfigEntry<bool> WeaponsWhileDriving;
        public static ConfigEntry<bool> EnemyWeaponDrops;
        public static ConfigEntry<float> DroppedWeaponLifetime;
        public static ConfigEntry<int> MaxDroppedWeapons;
        public static ConfigEntry<float> DroppedWeaponAmmo;

        // UI
        public static ConfigEntry<float> HudDistance;
        public static ConfigEntry<float> HudHorizontalFov;
        public static ConfigEntry<float> MenuDistance;
        public static ConfigEntry<float> MenuWidth;
        public static ConfigEntry<bool> WristHud;
        public static ConfigEntry<bool> GunAmmoCounter;
        public static ConfigEntry<string> LaserColor;
        /// <summary>The settings panel's less important tabs (read when the game starts).</summary>
        public static ConfigEntry<bool> ShowAdvanced;
        public static ConfigEntry<bool> ExplosionDirt;
        public static ConfigEntry<bool> LensFlash;
        public static ConfigEntry<bool> WaterDrops;
        public static ConfigEntry<float> ViewShake;
        public static ConfigEntry<float> LensFlashStrength;
        public static ConfigEntry<bool> ReduceFlashing;
        public static ConfigEntry<bool> ShotViewFlash;
        public static ConfigEntry<bool> WorldCollision;

        // Debug
        public static ConfigEntry<bool> DebugLogging;
        public static ConfigEntry<bool> ShowGrabZones;
        public static ConfigEntry<bool> DumpWeaponRigs;
        public static ConfigEntry<bool> UseSandboxPoses;
        public static ConfigEntry<bool> WeaponSelfTest;

        public static void Bind(ConfigFile cfg)
        {
            EnableVR = cfg.Bind("General", "EnableVR", true,
                "Start the game in VR. Turn off to play flat with the mod installed.");
            AutoStartSteamVR = cfg.Bind("General", "AutoStartSteamVR", false,
                "Start SteamVR yourself when the game launches and a headset driver reports a headset. Off: VR starts only if SteamVR is already running (or with -vr), so flat play never opens SteamVR.");
            NoSteamVRTheater = cfg.Bind("General", "NoSteamVRTheater", true,
                "Keep SteamVR from opening its dashboard theater when the game starts (SteamVR takes Ravenfield, started by Steam as a flat game, for one "
                + "to show on its theater screen, and the dashboard had to be closed by hand each time). Sets SteamVR's own setting "
                + "dashboard/autoShowGameTheater to off, from the next start of the game on; turn this off here and that setting on again in "
                + "SteamVR to have the theater back.");
            DominantHand = cfg.Bind("General", "DominantHand", Handedness.Right,
                "Hand that holds the weapon. The other hand handles foregrips, magazines and slides.");
            // The bindings are mirrored once, at startup (VRInput.ExtractManifests): the setting's "(restart)" applies there
            // and nowhere else. Read live, the gun hand flipped in the menu while the bindings stayed mirrored until restart,
            // so no trigger fired the gun (2026-10-19).
            _leftHandedAtStart = DominantHand.Value == Handedness.Left;

            Stereo = cfg.Bind("Rendering", "StereoMode", StereoMode.MultiPass,
                "Leave at MultiPass: the game's own shaders have no single-pass variants (no STEREO_INSTANCING_ON in its assets), so with SinglePassInstanced the world renders in one eye only. Restart required.");
            Graphics = cfg.Bind("Rendering", "GraphicsPreset", GraphicsPreset.Medium,
                "VR graphics preset (Low, Medium, High, Ultra) layered on the game's own options; Game leaves graphics to the game's menu. " +
                "Picking a preset also sets RenderScale, which you can still adjust.");
            RenderScale = cfg.Bind("Rendering", "RenderScale", 1.0f,
                new ConfigDescription("Eye texture resolution multiplier on top of SteamVR's own resolution.", new AcceptableValueRange<float>(0.5f, 2.0f)));
            Mirror = cfg.Bind("Rendering", "MirrorView", MirrorMode.Right,
                "What the desktop window shows (applies at once)."); // (applied live: OpenVRRuntime.ApplyLiveSettings; the text said a restart)
            NearClip = cfg.Bind("Rendering", "NearClip", 0.02f,
                new ConfigDescription("Camera near plane in metres. Lower lets you bring scopes closer to your eye.", new AcceptableValueRange<float>(0.005f, 0.2f)));
            PostProcessing = cfg.Bind("Rendering", "PostProcessing", false,
                "Copy the game's post-processing stack (colour grading, bloom, AO) onto the VR camera. Costs performance.");
            GogglesTube = cfg.Bind("Rendering", "GogglesTube", 0.5f,
                new ConfigDescription("How dark the edge of the view is through night vision goggles, as the game's tube picture is on the flat screen. 0: none.", new AcceptableValueRange<float>(0f, 1f)));
            BodyShadow = cfg.Bind("Rendering", "BodyShadow", false,
                "Your own soldier's shadow on foot and in first-person seats. Off: the body is the flat game's, its head at your eyes and its arms and gun in the game's " +
                "own animation, so with shadows on its shadow fell across your hands and gun and moved on its own in front of you.");
            MinFarClip = cfg.Bind("Rendering", "MinFarClip", 1500f,
                new ConfigDescription("Smallest far plane used for the VR camera, in metres. The game's draw distance is used when larger; the graphics preset's view distance still caps it (Low 1200, Medium 2000, High 3000, Ultra 4000).",
                    new AcceptableValueRange<float>(100f, 10000f))); // (a range: a hand-typed NaN made the camera's far plane NaN)

            Turn = cfg.Bind("Comfort", "TurnMode", TurnMode.Snap, "Right stick turning style.");
            SnapTurnAngle = cfg.Bind("Comfort", "SnapTurnAngle", 30f,
                new ConfigDescription("Degrees per snap turn.", new AcceptableValueRange<float>(10f, 90f)));
            SmoothTurnSpeed = cfg.Bind("Comfort", "SmoothTurnSpeed", 120f,
                new ConfigDescription("Degrees per second for smooth turning.", new AcceptableValueRange<float>(30f, 360f)));
            MoveDirection = cfg.Bind("Comfort", "MoveDirection", MoveReference.Head,
                "Whether the movement stick follows where you look or where your off hand points.");
            Vignette = cfg.Bind("Comfort", "Vignette", 0.35f,
                new ConfigDescription("Tunnel vignette strength while moving or smooth turning (0 = off).", new AcceptableValueRange<float>(0f, 1f)));
            VoiceChat = cfg.Bind("Multiplayer", "VoiceChat", false,
                "Voice between the players of a shared match: you hear the others from their soldiers' heads (louder near, quieter far), " +
                "they hear you while you speak. Off by default (for players on Discord or another voice app); off means neither sent nor heard.");
            VoiceDevice = cfg.Bind("Multiplayer", "VoiceDevice", "",
                "The microphone for the voice chat, by its Windows name (empty: the default device). Chosen in the settings' Display tab.");
            VoiceVolume = cfg.Bind("Multiplayer", "VoiceVolume", 1f,
                new ConfigDescription("How loud the other players' voices are played.", new AcceptableValueRange<float>(0f, 2f)));
            VoiceMicGain = cfg.Bind("Multiplayer", "VoiceMicGain", 1f,
                new ConfigDescription("Your microphone's level, as sent.", new AcceptableValueRange<float>(0.25f, 4f)));
            VoiceGate = cfg.Bind("Multiplayer", "VoiceGate", 0.015f,
                new ConfigDescription("How loud you must speak before you are sent (lower: quieter speech goes, more background too).", new AcceptableValueRange<float>(0.002f, 0.2f)));
            StickHoldsTrim = cfg.Bind("Vehicles", "StickHoldsTrim", true,
                "A plane's grabbed flight stick let go stays where it was (it holds the attitude, as a trimmed stick does) instead of " +
                "springing back to the middle. Grab it again to go on from there; push the right thumbstick to take over.");
            VehicleVignette = cfg.Bind("Comfort", "VehicleVignette", true,
                "The tunnel vignette also in a vehicle while it turns fast (a plane's roll, a car's skid): the turn the body does not feel " +
                "is what makes riders sick (the advice of headset makers and racing games). At the Vignette strength; off with it at 0.");
            PhysicalCrouch = cfg.Bind("Comfort", "PhysicalCrouch", true,
                "Crouching in real life makes your soldier crouch.");
            PhysicalCrouchRatio = cfg.Bind("Comfort", "PhysicalCrouchRatio", 0.75f,
                new ConfigDescription("Head height (fraction of calibrated eye height) below which you count as crouching.", new AcceptableValueRange<float>(0.5f, 0.9f)));
            RoomscaleMovement = cfg.Bind("Comfort", "RoomscaleMovement", true,
                "Walking around your play space moves your soldier (with collision). Off keeps the soldier where the stick puts it.");
            EyeHeight = cfg.Bind("Comfort", "EyeHeight", 0f,
                new ConfigDescription("Your standing (or seated) eye height in metres. 0 = calibrate automatically on first spawn. Re-calibrate from the VR settings panel.",
                    new AcceptableValueRange<float>(0f, 2.5f)));
            SeatedMode = cfg.Bind("Comfort", "SeatedMode", false,
                "Play seated: disables physical crouch, and takes the calibrated height as a seated one (the body's holsters and pouch are placed for its standing height).");
            SmoothVehicleHorizon = cfg.Bind("Comfort", "LevelHorizonInVehicles", false,
                "Keep the horizon level in aircraft/vehicles instead of rolling with the cockpit.");
            FallInFirstPerson = cfg.Bind("Comfort", "FallInFirstPerson", true,
                "Knocked over, the view stays at your soldier's head as he falls and lies (the horizon kept level), not the game's camera behind him.");

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
            SlowHands = cfg.Bind("Weapons", "SlowMotionHands", SlowMotionHands.Normal,
                "In the game's slow motion. Normal: your hands, and what they hold, move at your real speed, and what you let go of flies "
                + "as in real time until it touches something. Slowed: your hands are slowed with the world, as in Boneworks: they trail a "
                + "fast move and catch up when you stop, and what you hold or throw moves at the world's pace. Your head is never slowed.");
            ChamberPlusOne = cfg.Bind("Weapons", "ChamberPlusOne", true,
                "A chambered round counts on top of a full magazine (30+1).");
            ReturnRoundsOnMagDrop = cfg.Bind("Weapons", "ReturnRoundsOnMagDrop", true,
                "Rounds left in a dropped magazine go back to your reserve (Ravenfield's normal behaviour). Off = they are lost.");
            MagazineInsertHelp = cfg.Bind("Weapons", "MagazineInsertHelp", MagazineHelp.Normal,
                "How forgiving the magazine well is when a magazine is brought to it by hand. Exact: the magazine has to be close to the well's "
                + "line and turned nearly straight (a quarter less room than Normal). Normal: as it always was (3.5 cm beside the line, 35 degrees "
                + "turned; a pistol's grip a little more). Easy: 60% more room beside the line and half as much again in the turn, and on every "
                + "gun the magazine is drawn home by itself once it is a quarter of the way in, as a pistol's always is (no last push, no detent).");
            ShellsPerReach = cfg.Bind("Weapons", "ShellsPerReach", 2,
                new ConfigDescription("How many shells one reach into the pouch takes for a tube-fed shotgun (as in Half-Life: Alyx, two a reach). "
                                      + "They still go in one at a time: push one in, draw the hand back a little, push the next. Let go, the rest go back "
                                      + "to your reserve. 1 = one shell a reach, as before. Break actions and revolvers always take one.",
                                      new AcceptableValueRange<int>(1, 4)));
            MagazineDetent = cfg.Bind("Weapons", "MagazineDetent", true,
                "A magazine's last few millimetres into its well take a firm push, or a tap on its base, and it clicks as it latches. Pushed in "
                + "gently it stops just short; let go there, it waits in the well until its base is tapped home (or it is taken again, or the "
                + "magazine release drops it). Not for pistols (their magazine is drawn home in the grip), machine gun boxes or clips.");
            UnderSeatedMagazineFalls = cfg.Bind("Weapons", "UnderSeatedMagazineFalls", false,
                "Optional, with the magazine detent: a magazine let go before it clicks home goes in unlatched; it feeds, but drops out at the "
                + "next shot unless its base is tapped home first.");
            AkMagazinesStayIn = cfg.Bind("Weapons", "AkMagazinesStayIn", false,
                "Optional: on rifles with a curved magazine (AK style; not pistols or machine guns) the magazine release only unlatches the "
                + "magazine, it does not drop free: your hand rocks it out, as on the real thing.");
            LoadMagazinesByHand = cfg.Bind("Weapons", "LoadMagazinesByHand", false,
                "Experimental: rounds without end, loaded by hand. Magazines come from your reserve as usual; once it is out, the pouch hands "
                + "out empty ones, as many as you like, to fill yourself. Put the gun away and squeeze at the pouch for a magazine; holding it in "
                + "one hand, squeeze at the pouch with the other for a round, then touch it to the magazine's top. A magazine in a hand that is "
                + "not the gun's gives its rounds out one by one on that hand's trigger. Cases and rounds on the ground can be picked up and are "
                + "pushed by bullets. Not for machine guns, clips or guns loaded through the action, nor with the assisted reload.");
            ChamberByHand = cfg.Bind("Weapons", "ChamberByHand", true,
                "A single round into the open chamber by hand. With the gun drawn, hold your free hand's trigger and squeeze its grip at the "
                + "pouch: one round out of the reserve into your fingers. With the slide caught back or the bolt open and nothing in the "
                + "chamber, hold it to the ejection port and it goes in; let the slide go or push the bolt home and the gun is loaded with "
                + "that one round. A live round racked out and lying on the ground can be picked up and put back the same way. Manual "
                + "reloading only.");
            LoseRoundOnRack = cfg.Bind("Weapons", "LoseRoundOnRack", true,
                "Racking with a round already chambered ejects (and loses) that round.");
            Pouch = cfg.Bind("Weapons", "MagazinePouch", PouchLocation.OffHandHip,
                "Where fresh magazines/shells/rockets are grabbed from.");
            // (Ranges: a hand-typed huge lifetime left nothing but the fall-out test to remove props; prop physics hunt, 2026-10-21.)
            MagazineReach = cfg.Bind("Weapons", "MagazineReach", 3f,
                new ConfigDescription("A dropped magazine of the gun in your hand comes to your empty hand from this far (metres): point the hand at it "
                                      + "and squeeze the grip, with nothing else in reach of that hand (as the gravity gloves of Half-Life: Alyx do). "
                                      + "Only one with rounds in it. 0 = off: a magazine is picked up where it lies.",
                                      new AcceptableValueRange<float>(0f, 6f)));
            GripLetGoBelow = cfg.Bind("Input", "GripLetGoBelow", 0.3f,
                new ConfigDescription("On controllers with an analog grip (Touch, Index, Reverb): once a squeeze has taken hold, a grip that eases off for a moment "
                                      + "(up to 0.15 s) keeps its hold while it stays squeezed above this (0 to 0.9). SteamVR's own click lets go a little under "
                                      + "where it took hold, so a grip that eased off while pushing a magazine in or racking a slide dropped it and took it "
                                      + "again. 0 = SteamVR's click alone, as before.", new AcceptableValueRange<float>(0f, 0.9f)));
            MagPropLifetime = cfg.Bind("Weapons", "DroppedMagLifetime", 60f,
                new ConfigDescription("Seconds dropped magazines stay in the world (they keep their rounds and can be picked up again).",
                    new AcceptableValueRange<float>(1f, 3600f)));
            MaxMagProps = cfg.Bind("Weapons", "MaxDroppedMags", 12,
                new ConfigDescription("Maximum dropped magazines kept at once.", new AcceptableValueRange<int>(1, 100)));
            RecoilStrength = cfg.Bind("Weapons", "RecoilStrength", 1f,
                new ConfigDescription("Visual/aim recoil applied to the gun in your hand.", new AcceptableValueRange<float>(0f, 3f)));
            Hands = cfg.Bind("Weapons", "Hands", HandsMode.VRHands,
                "VRHands: the mod's own hands (fingers posed like each weapon's rig). WeaponArms: the weapon's own first-person arms, " +
                "gloves and sleeves, moved by IK to your controllers; with no weapon in the hand, the arms of the last one you held.");
            Glove = cfg.Bind("Weapons", "Glove", GloveStyle.Tactical,
                "Tactical: a textured tactical glove (Valve's SteamVR glove) whose fingers move like SteamVR's own hand poses. " +
                "Simple: the mod's earlier plain glove. Applies when the game starts.");
            GloveSize = cfg.Bind("Weapons", "GloveSize", 0.88f,
                new ConfigDescription("Size of the tactical glove against its modelled size (the weapons' own hands are smaller than real ones; 0.8 is a little " +
                                      "smaller than the default). Applies when the game starts.", new AcceptableValueRange<float>(0.7f, 1.1f)));
            SkipDrawAnimation = cfg.Bind("Weapons", "SkipDrawAnimation", true,
                "Skip the weapon's draw animation in VR (you draw it with your hand); the game's draw delay before firing stays.");
            WeaponWeight = cfg.Bind("Weapons", "WeaponWeight", true,
                "Long and heavy guns held in one hand droop a little and trail the wrist; supported by the other hand they follow it exactly.");
            TwoHandRecoilReduction = cfg.Bind("Weapons", "TwoHandRecoilReduction", 0.4f,
                new ConfigDescription("Fraction of recoil removed while holding the foregrip.", new AcceptableValueRange<float>(0f, 1f)));
            TwoHandedAiming = cfg.Bind("Weapons", "TwoHandedAiming", true, "Gripping the foregrip steers the barrel with both hands.");
            ScopeSteadying = cfg.Bind("Weapons", "ScopeSteadying", 1f,
                new ConfigDescription("Aiming through a magnifying scope (snipers, ACOGs, scoped launchers), the small shake of the hands is steadied, more the stronger the zoom; a deliberate swing follows at once. 0 = off.",
                    new AcceptableValueRange<float>(0f, 1f)));
            AimSteadying = cfg.Bind("Weapons", "AimSteadying", 0.5f,
                new ConfigDescription("Aiming down iron sights or a red dot (no magnification), the small shake of the hands is steadied this much; a deliberate swing follows at once, recoil is not smoothed. 0 = off.",
                    new AcceptableValueRange<float>(0f, 1f)));
            VRScopes = cfg.Bind("Weapons", "VRScopes", true,
                "Magnified picture-in-picture scope lens for scoped weapons that only had a 2D scope overlay.");
            ScopeResolution = cfg.Bind("Weapons", "ScopeResolution", 768,
                new ConfigDescription("Scope lens render texture size.", new AcceptableValueRange<int>(256, 2048)));
            SmallObjectsNoShadows = cfg.Bind("Rendering", "SmallObjectsNoShadows", false,
                "To try: the level's small objects (under 40 cm corner to corner) cast no shadow, so the shadow passes, drawn once per eye, have fewer "
                + "objects to draw (with shadows on: Medium, High, Ultra). Applies at once; soldiers keep theirs.");
            HideTinyDistantObjects = cfg.Bind("Rendering", "HideTinyDistantObjects", false,
                "To try: the level's objects that would be only about three pixels of an eye across (1 m corner to corner past about 250 m on Medium) are not drawn, so each eye's passes "
                + "have fewer objects to draw. Standing props only (soldiers, weapons, vehicles and anything moving stay); everything is drawn "
                + "while a magnified scope is. Applies at once; the frame rate line says how many are hidden.");
            InstanceRepeatedObjects = cfg.Bind("Rendering", "InstanceRepeatedObjects", false,
                "To try: the level's materials drawn by several objects are drawn instanced, so copies of one mesh go in one draw instead of "
                + "one each (in each eye's passes). Not for static-batched or lightmapped objects (their look stays as it is). Applies at once; "
                + "the frame rate line says how many materials it changed.");
            GcSliceMs = cfg.Bind("Rendering", "GarbageCollectionSliceMs", 3f,
                new ConfigDescription("To try: how much of a frame (ms) the game's incremental garbage collector may take while a collection runs "
                                      + "(Unity's own is 3, of the 11.1 ms a 90 Hz frame has). Lower spreads the same work over more frames, so a frame near "
                                      + "the budget is less likely to miss; too low and a collection may not keep up. Applies at once; the frame rate line "
                                      + "says the slice.", new AcceptableValueRange<float>(0.5f, 3f)));
            GameCamerasOnePixel = cfg.Bind("Rendering", "GameCamerasOnePixel", false,
                "To try: the game's own screen cameras, which draw nothing in VR but are kept on for the game's logic, render into one pixel "
                + "instead of the whole screen (their clears, image effects and post-processing cost about 0.7 ms of main thread a frame on a "
                + "test PC). Applies at once; off until it is measured in a headset. The desktop mirror and aiming are not meant to change.");
            HoldFrameRate = cfg.Bind("Rendering", "HoldFrameRate", false,
                "While frames keep missing the headset's rate, lower the preset's costliest settings one step at a time (shadow distance, "
                + "shadows, LOD bias, grass, pixel lights, last 15% of the render scale, only when the GPU is the cause) and bring them back when there is room again. Off: the preset stays as set. "
                + "Not with the Game preset (the game's own options).");
            ScopeShadows = cfg.Bind("Weapons", "ScopeShadows", false,
                "Shadows in the scope lens's magnified picture. Off by default: the lens draws the shadow cascades a second time, about 2.8 ms of the 3.4 ms the lens costs each frame while aiming through it (measured on the RECON LRR, 2026-10-17).");
            ScopeHalfRate = cfg.Bind("Weapons", "ScopeHalfRate", false,
                "To try: the magnified scope lens draws its picture every other frame and keeps the last one in between, about half of the "
                + "0.5 ms a frame it costs while aiming through it. Off until checked in a headset: the lens's picture may lag a fast pan.");
            HolsterSwitching = cfg.Bind("Weapons", "HolsterSwitching", true,
                "Reach over your shoulder / to your hip / chest and grip to draw primary, sidearm or gear. Gripping at the holster of the weapon you hold puts it away and frees your hand.");
            GripToHoldWeapon = cfg.Bind("Weapons", "GripToHoldWeapon", true,
                "Hold the grip button to keep holding your weapon; letting go puts it in its holster and leaves your hand empty and free. Off: the weapon stays in your hand (either hand: a second weapon in the free hand too) until you put it back at its holster with a grip press there.");
            SpawnEmptyHanded = cfg.Bind("Weapons", "SpawnEmptyHanded", true,
                "You spawn with empty hands, every weapon in its holster: reach for one to draw it (needs HolsterSwitching). Off: you spawn holding your first weapon, as in the flat game.");
            TriggerReleasesSlide = cfg.Bind("Weapons", "TriggerReleasesSlide", true,
                "Pulling the trigger with the slide or bolt locked open sends it forward, like the slide release button (it chambers a round from a loaded magazine; with none to chamber, it stays back).");
            MagSlapReleasesSlide = cfg.Bind("Weapons", "MagSlapReleasesSlide", true,
                "Slamming a loaded magazine home into a gun whose slide or bolt is locked open sends it forward and chambers a round, as in Boneworks (a gentle push leaves it locked).");
            MagazineReadout = cfg.Bind("UI", "MagazineReadout", true,
                "Look at the top of a magazine in your hand (or one lying within reach) to see how many rounds it holds.");
            ShowHolsters = cfg.Bind("Weapons", "ShowHolsters", true,
                "Show the weapons you carry where they hang on your body (see HolsterLayout).");
            Holsters = cfg.Bind("Weapons", "HolsterLayout", HolsterLayout.Boneworks,
                "Boneworks: long guns behind the shoulders (grip up, muzzle down the back), pistols under the arms, gear at the small " +
                "of the back and on the belt, the ammo pouch on the side of the hip. Classic: long guns muzzle up behind the shoulders, " +
                "pistols on the hips, gear on the chest, the pouch at the front of the belt.");
            ReleaseDropsWeapon = cfg.Bind("Weapons", "ReleaseDropsWeapon", true,
                "With GripToHoldWeapon: letting go of a weapon away from your body drops it where you are (pick it up again with either hand); letting go at your body or a holster puts it away.");
            WeaponReturnSeconds = cfg.Bind("Weapons", "WeaponReturnSeconds", 8f,
                new ConfigDescription("Seconds a weapon you dropped lies there before it goes back to its holster (it also goes back when you walk 20 m away).",
                    new AcceptableValueRange<float>(1f, 600f))); // (a range: a hand-typed NaN passed Mathf.Max and the weapon never came back)
            MagazinePalming = cfg.Bind("Weapons", "MagazinePalming", true,
                "Tactical reloads like H3VR: pressing the magazine release while your other hand holds a fresh magazine at the well puts the old magazine into that hand too. Seat the fresh one and the old one stays in your hand; let go to drop both.");
            ThrowVelocityMultiplier = cfg.Bind("Weapons", "ThrowVelocityMultiplier", 1.4f,
                new ConfigDescription("Multiplier on hand speed when throwing.", new AcceptableValueRange<float>(0.5f, 4f)));
            GrenadePins = cfg.Bind("Weapons", "GrenadePins", true,
                "Fused grenades have a pin: pull it with the other hand (or press A) before throwing. Thrown with the pin in, a grenade is a dud you can pick up again. Off = every throw is live.");
            BeltByHand = cfg.Bind("Weapons", "BeltByHand", true,
                "Belt-fed machine guns: after seating a new box, take the belt end from the box mouth and lay it on the feed tray before closing the cover. Off = closing the cover lays it for you.");
            HideGameWeaponHud = cfg.Bind("Weapons", "HideGameWeaponHud", true,
                "Hide the game's weapon panel (name, ammo, slots) in VR: the gun's own counter, the pouch and your wrist show the ammo, and weapons come from your holsters.");
            MeleeSwingSpeed = cfg.Bind("Weapons", "MeleeSwingSpeed", 2.6f,
                new ConfigDescription("Blade speed (m/s) needed for a melee hit.", new AcceptableValueRange<float>(1f, 8f)));
            WeaponsWhileDriving = cfg.Bind("Weapons", "WeaponsWhileDriving", true,
                "Driving a car or Humvee: reach to a holster to draw that weapon and shoot while steering with the other hand (put it back for the horn).");
            ShowPouch = cfg.Bind("Weapons", "ShowPouch", true,
                "Show what the held weapon feeds from (its magazine, a shell, a rocket) and the spare count at the ammo pouch on your body; with the gun put away, what it fed from.");
            FireSelector = cfg.Bind("Weapons", "FireSelector", true,
                "Fire-mode button switches automatic weapons between semi and auto (only weapons authored as automatic; " +
                "weapons with their own sub-weapon modes keep those).");
            EnemyWeaponDrops = cfg.Bind("Weapons", "EnemyWeaponDrops", true,
                "Fallen soldiers drop their weapon; grip it to pick it up (your weapon from that slot is dropped in exchange).");
            DroppedWeaponLifetime = cfg.Bind("Weapons", "DroppedWeaponLifetime", 90f,
                new ConfigDescription("Seconds a dropped weapon stays on the ground.", new AcceptableValueRange<float>(1f, 3600f)));
            MaxDroppedWeapons = cfg.Bind("Weapons", "MaxDroppedWeapons", 16,
                new ConfigDescription("Most dropped weapons kept at once (oldest disappear first).", new AcceptableValueRange<int>(0, 100)));
            DroppedWeaponAmmo = cfg.Bind("Weapons", "DroppedWeaponAmmo", 0.5f,
                new ConfigDescription("Share of a weapon's full reserve ammo that comes with a weapon picked up from a soldier.", new AcceptableValueRange<float>(0f, 1f)));
            ModWeaponSounds = cfg.Bind("Weapons", "ModWeaponSounds", true,
                "Play each weapon's own reload sounds for each manual action (mag out, mag in, slide). Off uses a vanilla weapon's of its kind.");
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
            WristFrameRate = cfg.Bind("UI", "WristFrameRate", true,
                "The wrist display also shows how many frames of the last few seconds kept the headset's rate: under about 90%, " +
                "SteamVR fills in frames and moving things ghost; pick a lower graphics preset.");
            LaserColor = cfg.Bind("UI", "LaserColor", DefaultLaserColor, "Menu laser colour (hex).");
            // The amber default a config file kept from before the UI turned blue (user, 2026-11-04); a colour picked by hand stays.
            if (string.Equals(LaserColor.Value, "#E8B44A", StringComparison.OrdinalIgnoreCase))
            {
                LaserColor.Value = DefaultLaserColor;
            }
            ShowAdvanced = cfg.Bind("UI", "ShowAdvanced", false,
                "Show the settings panel's advanced tabs (controls, gear, view, speed) when the game starts. Off: only the settings that matter most, the rest stay here in this file.");
            WorldCollision = cfg.Bind("Weapons", "WeaponWorldCollision", true,
                "The gun in your hand is stopped by the world (walls, crates, sandbags, the ground) instead of passing through it, and can be rested " +
                "on cover; it comes back to your hand when you pull it free. Off: the gun always follows your hand.");
            ExplosionDirt = cfg.Bind("UI", "ExplosionDirt", true,
                "A grenade or shell going off close by throws dirt on your view (water for a blast in water); it runs down and fades in a few seconds.");
            ViewShake = cfg.Bind("Comfort", "ViewShake", 0.3f,
                new ConfigDescription("A small jolt of the view on each shot (0 = off; it is short and capped, but view motion can make some people sick).",
                    new AcceptableValueRange<float>(0f, 1f)));
            WaterDrops = cfg.Bind("UI", "WaterDrops", false,
                "Coming up out of the water leaves drops on your view that run down and dry in a few seconds.");
            LensFlash = cfg.Bind("UI", "MuzzleLensFlash", true,
                "Your muzzle flash flares on the eyes for a moment (see-through, only on shots where the weapon's own flash shows).");
            LensFlashStrength = cfg.Bind("UI", "MuzzleLensFlashStrength", 1.5f,
                new ConfigDescription("How strong the muzzle lens flash is (1 is the approved simulation's own strength, seen on black; over a lit scene it is faint, so 1.5 by default). The flash limiter's caps hold at any strength.",
                    new AcceptableValueRange<float>(0f, 3f)));
            ReduceFlashing = cfg.Bind("UI", "ReduceFlashing", true,
                "Photosensitivity safety: never more than three visible flashes a second (a steady glow instead on full auto).");
            ShotViewFlash = cfg.Bind("UI", "ShotViewFlash", true,
                "Now and then a shot flashes the whole view for a moment (the muzzle flash lighting everything up). Only on shots the flash limiter lets through.");

            DebugLogging = cfg.Bind("Debug", "VerboseLogging", false, "Log extra diagnostics.");
            ShowGrabZones = cfg.Bind("Debug", "ShowGrabZones", false, "Draw spheres at mag wells, slides, foregrips, pouches and holsters.");
            DumpWeaponRigs = cfg.Bind("Debug", "DumpWeaponRigs", false, "Log the detected parts of every weapon you equip.");
            UseSandboxPoses = cfg.Bind("Weapons", "UseSandboxPoses", true,
                "Hold weapons with the hand poses saved in the sandbox (tools/sandbox, Save pose): BepInEx/config/RavenfieldVR/poses.");
            WeaponSelfTest = cfg.Bind("Debug", "WeaponSelfTest", false,
                "After content loads, analyse every installed weapon (vanilla and mods) and write BepInEx/RavenfieldVR-weapon-report.txt. Same as launching with -rfvr-selftest.");

            Migrate(cfg);
        }

        /// <summary>
        /// Moves settings to a new default once: BepInEx keeps whatever is in the file, so a changed default would
        /// otherwise never reach an existing install. A number still at its old default is moved (a value the player chose
        /// is kept); an on/off setting cannot tell the old default from a choice and is set once (revision 2: grip to hold).
        /// </summary>
        private static void Migrate(ConfigFile cfg)
        {
            ConfigEntry<int> revision = cfg.Bind("Debug", "ConfigRevision", 0, "Internal: which setting defaults this file has been brought up to. Do not edit.");
            if (revision.Value < 1)
            {
                // Dropped magazines became worth picking up again (they keep their rounds): 20 s was too short.
                if (Mathf.Approximately(MagPropLifetime.Value, 20f))
                {
                    MagPropLifetime.Value = 60f;
                }
                revision.Value = 1;
            }
            if (revision.Value < 2)
            {
                // Hands that hold only while you grip (letting go frees the hand) became the default.
                GripToHoldWeapon.Value = true;
                revision.Value = 2;
            }
            if (revision.Value < 3)
            {
                // The lens flash reached the headset for the first time (it had been drawn black in both eyes): at the
                // simulation's strength it was hard to see over a sunlit scene.
                if (Mathf.Approximately(LensFlashStrength.Value, 1f))
                {
                    LensFlashStrength.Value = 2f;
                }
                revision.Value = 3;
            }
            if (revision.Value < 4)
            {
                // 2 was too much on rifles (half the view washed out): a little smaller for every gun.
                if (Mathf.Approximately(LensFlashStrength.Value, 2f))
                {
                    LensFlashStrength.Value = 1.5f;
                }
                revision.Value = 4;
            }
            if (revision.Value < 5)
            {
                // Recoil was hardly felt in the headset: two hands take 40% of it (60% before).
                if (Mathf.Approximately(TwoHandRecoilReduction.Value, 0.6f))
                {
                    TwoHandRecoilReduction.Value = 0.4f;
                }
                revision.Value = 5;
            }
            if (revision.Value < 6)
            {
                // The presets were tuned for multi-pass VR (2026-10-24: the old ones dropped into reprojection even on Low): a
                // render scale still at its preset's old value takes the new one (Medium 1 -> 0.9, High 1.2 -> 1, Ultra 1.4 -> 1.2).
                float scale = RenderScale.Value;
                GraphicsPreset preset = Graphics.Value;
                if (preset == GraphicsPreset.Medium && Mathf.Approximately(scale, 1f))
                {
                    RenderScale.Value = 0.9f;
                }
                else if (preset == GraphicsPreset.High && Mathf.Approximately(scale, 1.2f))
                {
                    RenderScale.Value = 1f;
                }
                else if (preset == GraphicsPreset.Ultra && Mathf.Approximately(scale, 1.4f))
                {
                    RenderScale.Value = 1.2f;
                }
                revision.Value = 6;
            }
        }

        /// <summary>Handedness as it was at startup: the setting takes effect on restart (the bindings are mirrored then).</summary>
        private static bool _leftHandedAtStart;

        public static bool LeftHanded => TestLeftHanded ?? _leftHandedAtStart;

        /// <summary>The hands to show now: the setting, or the autotest's choice for a moment (never saved).</summary>
        public static HandsMode HandsNow => TestHands ?? Hands.Value;

        /// <summary>Autotest: the weapon's own arms for a moment without touching the saved setting.</summary>
        internal static HandsMode? TestHands;

        /// <summary>The reloading mode now: the setting, or the autotest's choice for a moment (never saved).</summary>
        public static ReloadMode ReloadNow => TestReload ?? Reload.Value;

        /// <summary>Autotest: a reloading mode for a moment without touching the saved setting.</summary>
        internal static ReloadMode? TestReload;

        /// <summary>Whether the hands are slowed with the world in slow motion now: the setting, or the autotest's choice (never saved).</summary>
        public static bool SlowedHands => (TestSlowHands ?? SlowHands.Value) == SlowMotionHands.Slowed;

        /// <summary>Autotest: the slow-motion hands mode for a moment without touching the saved setting.</summary>
        internal static SlowMotionHands? TestSlowHands;

        /// <summary>Whether magazines are loaded by hand now: the setting, or the autotest's choice (never saved).</summary>
        public static bool LoadsMagazinesByHand => TestLoadByHand ?? LoadMagazinesByHand.Value;

        /// <summary>Autotest: magazines loaded by hand for a moment without touching the saved setting.</summary>
        internal static bool? TestLoadByHand;

        /// <summary>Autotest: handedness for a moment without touching the saved setting.</summary>
        internal static bool? TestLeftHanded;

        private static readonly Dictionary<ConfigEntry<string>, KeyValuePair<string, Vector3>> ParsedVectors =
            new Dictionary<ConfigEntry<string>, KeyValuePair<string, Vector3>>();

        /// <summary>The entry as a vector, parsed once per value (it is read every frame for hand and gun offsets).</summary>
        public static Vector3 ParseVector(ConfigEntry<string> entry, Vector3 fallback)
        {
            string text = entry.Value;
            if (ParsedVectors.TryGetValue(entry, out KeyValuePair<string, Vector3> cached) && ReferenceEquals(cached.Key, text))
            {
                return cached.Value;
            }
            if (!TryParseVector(text, out Vector3 v))
            {
                Log.WarnOnce("vec:" + entry.Definition.Key, $"Config {entry.Definition.Section}.{entry.Definition.Key} = '{text}' is not 'x, y, z'; using {fallback}.");
                v = fallback;
            }
            ParsedVectors[entry] = new KeyValuePair<string, Vector3>(text, v);
            return v;
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
                // Written with a decimal comma, as a German keyboard has it ("0,5; 0; 0", "0,5 0 0", "0,5, 0, 0,25"): split by
                // ';', by blanks or by ", " alone, three parts whose commas are the decimal's. Read as four and more parts it was
                // refused, the offset left out with a line in the log only (scout, 2026-11-11).
                string[] wide = text.IndexOf(';') >= 0 ? text.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                    : text.IndexOf(", ", StringComparison.Ordinal) >= 0 ? text.Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries)
                    : text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (wide.Length != 3)
                {
                    return false;
                }
                parts = wide;
                for (int i = 0; i < 3; i++)
                {
                    parts[i] = parts[i].Trim();
                    if (parts[i].IndexOf(',') != parts[i].LastIndexOf(','))
                    {
                        return false; // (two commas in one number)
                    }
                    parts[i] = parts[i].Replace(',', '.');
                }
            }
            float[] f = new float[3];
            for (int i = 0; i < 3; i++)
            {
                // (Not "NaN" or "Infinity", which parse: a NaN offset hid the hand and the gun, every frame.)
                if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out f[i]) || float.IsNaN(f[i]) || float.IsInfinity(f[i]))
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
            if (ColorUtility.TryParseHtmlString(entry.Value, out Color c))
            {
                return c;
            }
            // Said once, as a rejected vector is (ParseVector): a mistyped colour showed the default with no word why (sweep).
            Log.WarnOnce("color:" + entry.Definition.Key, $"Config {entry.Definition.Section}.{entry.Definition.Key} = '{entry.Value}' is not a colour (#RRGGBB); using the default.");
            return fallback;
        }
    }
}
