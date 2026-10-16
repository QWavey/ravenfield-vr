namespace RavenfieldVR.Rig
{
    /// <summary>What kind of weapon a hand holds or supports, for choosing its pose.</summary>
    public enum WeaponClass { None, Pistol, Rifle, MachineGun, Launcher, Knife, LongMelee, Throwable }

    /// <summary>Which moving part a hand works (set while racking).</summary>
    public enum WorkedPart { Slide, Bolt, ChargingHandle, Pump }

    /// <summary>The hand poses the mod has, one per kind of thing a hand holds (Boneworks has about thirty).</summary>
    public enum HandPoseId
    {
        Relaxed, Fist, LaserPoint,
        PistolGrip, RifleGrip, MachineGunGrip, LauncherGrip, KnifeGrip, ToolGrip, Grenade,
        PistolCup, Handguard, LauncherTube, TwoHandedMelee,
        PistolSlide, BoltHandle, ChargingHandle, Pump,
        RifleMagazine, PistolMagazine, BoxMagazine, TwoMagazines, Shell, Rocket,
        CoverLift, PinPull, BeltPinch, OpticFlip,
        SteeringWheel, FlightStick,
        PalmPush
    }

    /// <summary>
    /// A hand pose: how far each finger closes (thumb, index, middle, ring, little; 0 open .. 1 fist) and where the thumb's
    /// base turns (0 its relaxed rest .. 1 the fist). The fingers still follow the tracked fingers past these values, and a
    /// hand holding a measured model closes on its real shape from here; weapons with their own authored hand pose use
    /// that instead for the gun hand and the support hand.
    /// </summary>
    public sealed class GripPose
    {
        public readonly HandPoseId Id;
        public readonly float[] Curls;
        public readonly float Thumb;
        /// <summary>The pose is taken as it is (the tracked fingers do not open it further): a firm hold.</summary>
        public readonly bool Exact;

        public GripPose(HandPoseId id, float thumbBase, bool exact, params float[] curls)
        {
            Id = id;
            Thumb = thumbBase;
            Exact = exact;
            Curls = curls;
        }
    }

    /// <summary>The pose library and how a pose is chosen from what the hand does and holds.</summary>
    public static class HandPoses
    {
        // Thumb base 0.35 is the thumb along a pistol frame or a handguard's side (the earlier grip thumb).
        private static readonly GripPose[] All =
        {
            new GripPose(HandPoseId.Relaxed, 0f, false, 0.1f, 0.12f, 0.16f, 0.2f, 0.24f),
            new GripPose(HandPoseId.Fist, 0.8f, false, 0.6f, 0.95f, 1f, 1f, 1f),
            new GripPose(HandPoseId.LaserPoint, 0.55f, true, 0.45f, 0.05f, 0.82f, 0.84f, 0.86f),

            new GripPose(HandPoseId.PistolGrip, 0.35f, false, 0.06f, 0.3f, 0.7f, 0.72f, 0.74f),
            new GripPose(HandPoseId.RifleGrip, 0.35f, false, 0.25f, 0.3f, 0.7f, 0.72f, 0.74f),
            new GripPose(HandPoseId.MachineGunGrip, 0.45f, false, 0.4f, 0.32f, 0.76f, 0.78f, 0.8f),
            new GripPose(HandPoseId.LauncherGrip, 0.5f, false, 0.35f, 0.35f, 0.75f, 0.78f, 0.8f),
            new GripPose(HandPoseId.KnifeGrip, 0.7f, false, 0.65f, 0.85f, 0.88f, 0.9f, 0.9f),
            new GripPose(HandPoseId.ToolGrip, 0.55f, false, 0.5f, 0.75f, 0.8f, 0.82f, 0.84f),
            new GripPose(HandPoseId.Grenade, 0.55f, false, 0.5f, 0.6f, 0.62f, 0.64f, 0.66f),

            new GripPose(HandPoseId.PistolCup, 0.35f, false, 0.3f, 0.55f, 0.6f, 0.62f, 0.65f),
            new GripPose(HandPoseId.Handguard, 0.35f, false, 0.3f, 0.6f, 0.65f, 0.68f, 0.7f),
            new GripPose(HandPoseId.LauncherTube, 0.3f, false, 0.25f, 0.45f, 0.5f, 0.52f, 0.55f),
            new GripPose(HandPoseId.TwoHandedMelee, 0.6f, false, 0.55f, 0.8f, 0.84f, 0.86f, 0.88f),

            new GripPose(HandPoseId.PistolSlide, 0.5f, false, 0.45f, 0.65f, 0.7f, 0.7f, 0.7f),
            new GripPose(HandPoseId.BoltHandle, 0.45f, false, 0.5f, 0.6f, 0.8f, 0.85f, 0.85f),
            new GripPose(HandPoseId.ChargingHandle, 0.2f, false, 0.15f, 0.7f, 0.72f, 0.45f, 0.4f),
            new GripPose(HandPoseId.Pump, 0.55f, false, 0.5f, 0.75f, 0.78f, 0.8f, 0.82f),

            // The thumb's base less turned in (0.55 went through the magazine's back edge): along the magazine's side.
            new GripPose(HandPoseId.RifleMagazine, 0.3f, false, 0.55f, 0.7f, 0.75f, 0.78f, 0.8f),
            // A pistol magazine by its base, in the fist, the thumb along its side and the magazine out between thumb and
            // index (the user's picture; MagazineProp.HoldPose). Seating it, the hand opens into a push (PalmPush).
            new GripPose(HandPoseId.PistolMagazine, 0.3f, false, 0.5f, 0.68f, 0.74f, 0.78f, 0.8f),
            new GripPose(HandPoseId.BoxMagazine, 0.35f, false, 0.35f, 0.5f, 0.55f, 0.58f, 0.6f),
            new GripPose(HandPoseId.TwoMagazines, 0.5f, false, 0.5f, 0.65f, 0.7f, 0.72f, 0.75f),
            // Pushing a pistol magazine's base plate home with the palm's heel: the thumb away, the fingers loose under the
            // gun hand's (blended in as the magazine slides up the grip; HandModel.PushBlend).
            new GripPose(HandPoseId.PalmPush, 0f, false, 0.08f, 0.32f, 0.4f, 0.46f, 0.52f),
            new GripPose(HandPoseId.Shell, 0.55f, false, 0.55f, 0.55f, 0.85f, 0.9f, 0.9f),
            new GripPose(HandPoseId.Rocket, 0.3f, false, 0.3f, 0.45f, 0.5f, 0.52f, 0.55f),

            new GripPose(HandPoseId.CoverLift, 0.3f, false, 0.3f, 0.45f, 0.5f, 0.55f, 0.6f),
            new GripPose(HandPoseId.PinPull, 0.4f, false, 0.4f, 0.85f, 0.55f, 0.5f, 0.45f),
            new GripPose(HandPoseId.BeltPinch, 0.45f, false, 0.5f, 0.6f, 0.85f, 0.88f, 0.9f),
            new GripPose(HandPoseId.OpticFlip, 0.25f, false, 0.1f, 0.35f, 0.7f, 0.75f, 0.78f),

            new GripPose(HandPoseId.SteeringWheel, 0.45f, false, 0.45f, 0.7f, 0.72f, 0.74f, 0.76f),
            new GripPose(HandPoseId.FlightStick, 0.5f, false, 0.5f, 0.35f, 0.85f, 0.86f, 0.88f)
        };

        public static int Count => All.Length;

        private static readonly GripPose[] ById = BuildIndex();

        private static GripPose[] BuildIndex()
        {
            var byId = new GripPose[All.Length];
            foreach (GripPose p in All)
            {
                byId[(int)p.Id] = p;
            }
            return byId;
        }

        public static GripPose Get(HandPoseId id) => ById[(int)id] ?? All[0];

        /// <summary>Every pose (self-test pictures).</summary>
        public static GripPose[] Library => All;

        /// <summary>The pose for a hand doing <paramref name="role"/> with these details of what it holds.</summary>
        public static HandPoseId For(HandRole role, WeaponClass weapon, WorkedPart part, Weapons.MagazineProp magazine, bool palming, bool stick)
        {
            switch (role)
            {
                case HandRole.Weapon:
                    switch (weapon)
                    {
                        case WeaponClass.Pistol: return HandPoseId.PistolGrip;
                        case WeaponClass.MachineGun: return HandPoseId.MachineGunGrip;
                        case WeaponClass.Launcher: return HandPoseId.LauncherGrip;
                        case WeaponClass.Knife: return HandPoseId.KnifeGrip;
                        case WeaponClass.LongMelee: return HandPoseId.ToolGrip;
                        case WeaponClass.Throwable: return HandPoseId.Grenade;
                        default: return HandPoseId.RifleGrip;
                    }
                case HandRole.Throwable:
                    return HandPoseId.Grenade;
                case HandRole.Foregrip:
                    switch (weapon)
                    {
                        case WeaponClass.Pistol: return HandPoseId.PistolCup;
                        case WeaponClass.Launcher: return HandPoseId.LauncherTube;
                        case WeaponClass.Knife:
                        case WeaponClass.LongMelee: return HandPoseId.TwoHandedMelee;
                        default: return HandPoseId.Handguard;
                    }
                case HandRole.Slide:
                    switch (part)
                    {
                        case WorkedPart.Pump: return HandPoseId.Pump;
                        case WorkedPart.ChargingHandle: return HandPoseId.ChargingHandle;
                        case WorkedPart.Bolt: return HandPoseId.BoltHandle;
                        default: return HandPoseId.PistolSlide;
                    }
                case HandRole.Magazine:
                    if (palming)
                    {
                        return HandPoseId.TwoMagazines;
                    }
                    if (magazine != null)
                    {
                        if (magazine.Kind == Weapons.PropKind.Shell)
                        {
                            return HandPoseId.Shell;
                        }
                        if (magazine.Kind == Weapons.PropKind.Rocket)
                        {
                            return HandPoseId.Rocket;
                        }
                        // By its size across: a pistol magazine is slim, a machine gun's box or drum is wide.
                        if (magazine.BoxSized)
                        {
                            return HandPoseId.BoxMagazine;
                        }
                        if (magazine.PistolSized)
                        {
                            return HandPoseId.PistolMagazine;
                        }
                    }
                    return HandPoseId.RifleMagazine;
                case HandRole.Hinge:
                    return HandPoseId.CoverLift;
                case HandRole.Pin:
                    return HandPoseId.PinPull;
                case HandRole.Belt:
                    return HandPoseId.BeltPinch;
                case HandRole.Optic:
                    return HandPoseId.OpticFlip;
                case HandRole.Cockpit:
                    return stick ? HandPoseId.FlightStick : HandPoseId.SteeringWheel;
                case HandRole.Pointer:
                    return HandPoseId.LaserPoint;
                default:
                    return HandPoseId.Relaxed;
            }
        }
    }
}
