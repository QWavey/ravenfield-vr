using RavenfieldVR.Core;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// Physical ammunition state of one weapon under manual reloading: rounds in the inserted magazine (or
    /// internal tube), a round in the chamber, whether a magazine is seated and whether the slide/bolt is locked
    /// back. <c>weapon.ammo</c> is kept equal to <see cref="Loaded"/> so the HUD and game logic stay correct.
    /// </summary>
    public sealed class AmmoState
    {
        public readonly Weapon Weapon;
        public readonly WeaponRigData Rig;

        /// <summary>Rounds in the magazine (a fire-mode variant on its parent's model reads and moves the parent's; 255.B6-3).</summary>
        public int MagRounds
        {
            get => Shared ? MagazineOf.MagRounds : _magRounds;
            set
            {
                if (Shared)
                {
                    MagazineOf.MagRounds = value;
                }
                else
                {
                    _magRounds = value;
                }
            }
        }
        private int _magRounds;

        /// <summary>This is a fire-mode variant that shares its parent's magazine, cover and belt.</summary>
        private bool Shared => MagazineOf != null && !ReferenceEquals(MagazineOf, this);
        /// <summary>
        /// A round in the chamber. Shared with a fire-mode variant as the magazine is (one gun, one chamber): its own, set from its
        /// weapon's full load when it was made, gave the first switch to it with the magazine empty a round to fire it did not
        /// have (round-2 scout, 2026-10-27). The slide and bolt as well.
        /// </summary>
        public bool Chambered
        {
            get => Shared ? MagazineOf.Chambered : _chambered;
            set
            {
                if (Shared)
                {
                    MagazineOf.Chambered = value;
                }
                else
                {
                    _chambered = value;
                }
            }
        }
        private bool _chambered;
        private bool _magInserted = true;

        /// <summary>
        /// A fire-mode variant drawn on its parent's model has no magazine of its own: it reads and moves the parent's. With
        /// the magazine out, switching to the variant drew the magazine again and fired without one (bug hunt 249).
        /// </summary>
        public AmmoState MagazineOf;

        public bool MagInserted
        {
            get => MagazineOf != null && !ReferenceEquals(MagazineOf, this) ? MagazineOf.MagInserted : _magInserted;
            set
            {
                if (MagazineOf != null && !ReferenceEquals(MagazineOf, this))
                {
                    MagazineOf.MagInserted = value;
                }
                else
                {
                    _magInserted = value;
                    if (!value)
                    {
                        _magLoose = false; // (out of the gun: a next one latches or not on its own)
                    }
                }
            }
        }

        /// <summary>
        /// The seated magazine is in but not latched (let go short of its click with UnderSeatedMagazineFalls on): it drops out at the
        /// next shot unless its base is tapped home.
        /// </summary>
        public bool MagLoose
        {
            get => Shared ? MagazineOf.MagLoose : _magLoose;
            set
            {
                if (Shared)
                {
                    MagazineOf.MagLoose = value;
                }
                else
                {
                    _magLoose = value;
                }
            }
        }
        private bool _magLoose;
        public bool SlideLocked
        {
            get => Shared ? MagazineOf.SlideLocked : _slideLocked;
            set
            {
                if (Shared)
                {
                    MagazineOf.SlideLocked = value;
                }
                else
                {
                    _slideLocked = value;
                }
            }
        }
        private bool _slideLocked;
        /// <summary>Break-action / revolver: open for loading (cannot fire).</summary>
        public bool Open;
        /// <summary>Bolt / pump / lever left open by hand (manual actions do not spring shut).</summary>
        public bool BoltOpen
        {
            get => Shared ? MagazineOf.BoltOpen : _boltOpen;
            set
            {
                if (Shared)
                {
                    MagazineOf.BoltOpen = value;
                }
                else
                {
                    _boltOpen = value;
                }
            }
        }
        private bool _boltOpen;
        /// <summary>Machine gun feed cover lifted (cannot fire). Shared with a fire-mode variant on the same model (255.B6-3).</summary>
        public bool CoverOpen
        {
            get => Shared ? MagazineOf.CoverOpen : _coverOpen;
            set
            {
                if (Shared)
                {
                    MagazineOf.CoverOpen = value;
                }
                else
                {
                    _coverOpen = value;
                }
            }
        }
        private bool _coverOpen;
        /// <summary>Machine gun: the belt of the seated box lies on the feed tray (the cover was closed over it). Shared as the cover.</summary>
        public bool BeltLaid
        {
            get => Shared ? MagazineOf.BeltLaid : _beltLaid;
            set
            {
                if (Shared)
                {
                    MagazineOf.BeltLaid = value;
                }
                else
                {
                    _beltLaid = value;
                }
            }
        }
        private bool _beltLaid = true;
        /// <summary>The cover was lifted off a belt laid on the tray, and its box has stayed in since. Shared as the cover.</summary>
        private bool BeltLifted
        {
            get => Shared ? MagazineOf.BeltLifted : _beltLifted;
            set
            {
                if (Shared)
                {
                    MagazineOf.BeltLifted = value;
                }
                else
                {
                    _beltLifted = value;
                }
            }
        }
        private bool _beltLifted;
        /// <summary>A bolt or pump gun fired: the case stays in the chamber until the action is worked.</summary>
        public bool SpentInChamber
        {
            get => Shared ? MagazineOf.SpentInChamber : _spentInChamber;
            set
            {
                if (Shared)
                {
                    MagazineOf.SpentInChamber = value;
                }
                else
                {
                    _spentInChamber = value;
                }
            }
        }
        private bool _spentInChamber;
        /// <summary>Break-action / revolver: fired cases still in the chambers (they fall out when it is opened).</summary>
        public int SpentCases;

        private int _expectedAmmo;

        public AmmoState(Weapon weapon, WeaponRigData rig)
        {
            Weapon = weapon;
            Rig = rig;
            AdoptWeaponAmmo();
        }

        public int Capacity => Mathf.Max(1, Weapon.configuration.ammo);

        /// <summary>
        /// Rounds the magazine or tube still takes. Without chamber +1 the chambered round counts in the capacity: shells and
        /// tube loading filled to capacity on top of it (9 in an 8-round gun; bug hunt 251.4).
        /// </summary>
        public int TubeRoom => Mathf.Max(0, Capacity - MagRounds - (!VRConfig.ChamberPlusOne.Value && Chambered ? 1 : 0));
        public int Loaded => (MagInserted || !UsesDetachableMag ? MagRounds : 0) + (Chambered ? 1 : 0);

        /// <summary>A fire-mode variant has no magazine path of its own, so it takes the parent's answer (task 249).</summary>
        public bool UsesDetachableMag => Shared ? MagazineOf.UsesDetachableMag : (Rig.Action == ActionType.SelfLoading || (Rig.Action == ActionType.Manual && Rig.MagazinePath != null));
        public bool UsesChamber => Rig.Action == ActionType.SelfLoading || Rig.Action == ActionType.Manual || Rig.Action == ActionType.ShellFed;
        /// <summary>The action cycles itself after a shot (feeds the next round).</summary>
        public bool SelfLoading => (Rig.Action == ActionType.SelfLoading || Rig.Action == ActionType.ShellFed) && !Rig.CyclesByHand;
        /// <summary>
        /// The next round is chambered without the hand after a shot: a self-loading action, or any action with assisted
        /// reloading (the game's own fire animation works a bolt or pump then; nothing racks it by hand).
        /// </summary>
        public bool CyclesItself => SelfLoading || VRConfig.ReloadNow != ReloadMode.Manual;
        public bool HasCover => Shared ? MagazineOf.HasCover : Rig.Hinge == HingeKind.FeedCover;
        /// <summary>Rounds can reach the chamber: a magazine is seated (or internal) and, on a belt-fed gun, the belt is laid.</summary>
        public bool FeedReady => (MagInserted || !UsesDetachableMag) && (!HasCover || BeltLaid && !CoverOpen);
        private bool LoadsDirectly => Rig.Action == ActionType.SingleShot || Rig.Action == ActionType.BreakOpen;
        public bool Participates => Rig.Action != ActionType.NoAmmo && Rig.Action != ActionType.Throwable && Rig.Action != ActionType.Melee;

        public int SpareAmmo
        {
            get => Weapon.spareAmmo;
            set => Weapon.spareAmmo = Mathf.Max(0, value);
        }

        public bool InfiniteSpare => Weapon.HasInfiniteSpareAmmo();

        /// <summary>The trigger pull that sent the slide forward does not also fire: held until the trigger is let go.</summary>
        public bool HoldFireUntilTriggerUp;

        /// <summary>Whether the trigger can drop the hammer on a round right now.</summary>
        public bool CanFire
        {
            get
            {
                if (!Participates)
                {
                    return true;
                }
                if (HoldFireUntilTriggerUp)
                {
                    return false;
                }
                if (LoadsDirectly)
                {
                    return MagRounds > 0 && !Open;
                }
                // A belt-fed gun shut over no belt does not fire either (the counter already says OPEN COVER).
                return Chambered && !SlideLocked && !BoltOpen && !(HasCover && (CoverOpen || MagInserted && !BeltLaid));
            }
        }

        /// <summary>Takes over whatever the game currently thinks is loaded (spawn, resupply, scripts).</summary>
        public void AdoptWeaponAmmo()
        {
            int ammo = Mathf.Max(0, Weapon.ammo);
            if (Rig.Action == ActionType.SingleShot || !UsesChamber)
            {
                MagRounds = ammo;
                Chambered = false;
            }
            else
            {
                Chambered = ammo > 0;
                MagRounds = Mathf.Max(0, ammo - 1);
            }
            MagInserted = true;
            SlideLocked = false;
            SpentInChamber = false;
            SpentCases = 0;
            Open = false;
            CoverOpen = false;
            BeltLaid = true;
            BoltOpen = false;
            // Empty on a gun that locks open on empty (a dropped one picked up, a spawn): its slide starts caught back, as after
            // its last round, not free to go home without a round (264). Only a gun with a chamber: an override's lockOnEmpty on
            // a launcher or break action must not report its loaded rounds as none.
            if (!Chambered && Rig.LockOnEmpty && UsesChamber && !Rig.CyclesByHand)
            {
                SlideLocked = true;
            }
            Sync();
        }

        /// <summary>Called every frame: adopts outside changes to weapon.ammo, then re-asserts our value.</summary>
        public void Reconcile()
        {
            if (!Participates)
            {
                return;
            }
            if (Weapon.ammo != _expectedAmmo)
            {
                if (Weapon.ammo > _expectedAmmo)
                {
                    // Refilled from outside (an infinite-ammo mutator, a script, a resupply): only the rounds change.
                    // The mechanics stay as the player left them: a refill must never put an ejected magazine back
                    // into the gun or close an open cover or bolt. With no magazine in, or the gun open, it cannot
                    // take rounds at all right now, and the value below is simply re-asserted.
                    bool canTakeRounds = !(UsesDetachableMag && !MagInserted) && !Open && !(HasCover && CoverOpen);
                    if (canTakeRounds)
                    {
                        if (SlideLocked && !Chambered)
                        {
                            // Locked back on an empty chamber the game is told 0 (Sync) while the magazine holds rounds: the refill's
                            // rounds are added to them (set to the refill alone, rounds vanished or appeared; round 11).
                            MagRounds = Mathf.Clamp(MagRounds + (Weapon.ammo - _expectedAmmo), 0, Capacity);
                        }
                        else
                        {
                            int chamber = UsesChamber && Chambered && Rig.Action != ActionType.SingleShot ? 1 : 0;
                            MagRounds = Mathf.Clamp(Weapon.ammo - chamber, 0, Capacity);
                        }
                        Log.Debug($"{Weapon.name}: refilled externally {_expectedAmmo} -> {Weapon.ammo}.");
                    }
                }
                else
                {
                    int lost = _expectedAmmo - Weapon.ammo;
                    MagRounds = Mathf.Max(0, MagRounds - lost);
                    if (MagRounds == 0 && Weapon.ammo == 0)
                    {
                        Chambered = false;
                    }
                }
            }
            Sync();
        }

        public void Sync()
        {
            if (!Participates)
            {
                // Not the mod's to count (no ammo of its own, thrown, melee): an infinite magazine's -1 was written as 0,
                // and a charge-time weapon with one never charged (the game starts the charge only with ammo -1 or above 0).
                return;
            }
            // Locked open nothing is chambered: the game is told no round is loaded, a fresh magazine in or not. Told the
            // magazine's rounds, many pistols' animators or scripts ran the slide forward on their own as the magazine went
            // in (the mwii-g17 family; user, 2026-10-16), while the gun still had to be released or racked.
            int loaded = SlideLocked && !Chambered ? 0 : Loaded;
            if (Weapon.ammo != loaded)
            {
                Weapon.ammo = loaded;
                Weapon.OnAmmoChanged();
            }
            _expectedAmmo = loaded;
        }

        /// <summary>Called after the game fired a round (Weapon.Shoot already decremented ammo).</summary>
        public void OnShot()
        {
            if (!Participates)
            {
                return;
            }
            if (LoadsDirectly)
            {
                if (MagRounds > 0)
                {
                    SpentCases = Mathf.Min(SpentCases + 1, Capacity);
                }
                MagRounds = Mathf.Max(0, MagRounds - 1);
                Sync();
                return;
            }
            Chambered = false;
            // A bolt or pump left for the hand to work keeps its case. With assisted reloading a bolt gun is cycled by its
            // fire animation: it was never chambered again here, so from the second shot on every shot's round was put
            // back (endless ammo).
            SpentInChamber = !CyclesItself;
            if (CyclesItself)
            {
                if (FeedReady && MagRounds > 0)
                {
                    MagRounds--;
                    Chambered = true;
                }
                else if (Rig.LockOnEmpty)
                {
                    SlideLocked = true;
                }
            }
            Sync();
        }

        /// <summary>Opened: the fired cases fall out of the chambers (how many there were; none the next time).</summary>
        public int TakeSpentCases()
        {
            int spent = SpentCases;
            SpentCases = 0;
            return spent;
        }

        /// <summary>
        /// A break-action shotgun opened: its live shells come out with the fired ones (user, 2026-10-22, the DOUBLE-BARREL:
        /// "the ammo wont even go out"), back into the reserve as a live round racked out of a chamber is.
        /// </summary>
        /// <returns>The live shells that came out.</returns>
        public int TakeLiveShells()
        {
            int live = MagRounds;
            if (live <= 0)
            {
                return 0;
            }
            MagRounds = 0;
            if (!VRConfig.LoseRoundOnRack.Value)
            {
                ReturnSpareCapped(live);
            }
            Sync();
            return live;
        }

        /// <returns>Rounds that were in the magazine.</returns>
        public int EjectMagazine()
        {
            if (!MagInserted || !UsesDetachableMag)
            {
                return 0;
            }
            int rounds = MagRounds;
            MagInserted = false;
            MagRounds = 0;
            BeltLaid = !HasCover;
            BeltLifted = false;
            Sync();
            return rounds;
        }

        public void InsertMagazine(int rounds)
        {
            if (MagInserted && MagRounds > 0)
            {
                // A magazine in while one is still in (two seat paths in one frame): the one it replaces keeps its rounds,
                // back to the reserve, or they were gone from the count (252 ae, RECON LRR's 29 -> 15).
                Log.Info($"A magazine went in over one still in ({MagRounds} rounds): those back to the reserve.");
                ReturnSpare(MagRounds);
                MagRounds = 0;
            }
            MagInserted = true;
            MagRounds = Mathf.Clamp(rounds, 0, Mathf.Max(0, Capacity - (!VRConfig.ChamberPlusOne.Value && Chambered ? 1 : 0)));
            ReturnSpare(rounds - MagRounds); // more than this weapon's magazine holds: the rest goes to the reserve
            // A new box's belt still has to be laid on the feed tray under the cover. Without belt laying by hand, a box
            // seated under a cover already shut counts as laid, as shutting the cover over it would (nothing else would
            // ever lay it: the gun stayed dead, the counter asking to close a closed cover).
            BeltLaid = !HasCover || !VRConfig.BeltByHand.Value && !CoverOpen;
            BeltLifted = false;
            Sync();
        }

        public void SetOpen(bool open)
        {
            Open = open;
            Sync();
        }

        public void SetCoverOpen(bool open)
        {
            // (Or over the belt it was lifted off, the box never out: a look under the cover on a loaded gun left it dead, the counter
            // asking to open it again and lay a belt that had never gone anywhere; belt scout, 2026-11-05.)
            if (CoverOpen && !open && MagInserted && (!VRConfig.BeltByHand.Value || BeltLifted))
            {
                BeltLaid = true; // closing the cover over the belt
            }
            // Lifting the cover takes a belt laid by hand off the tray with its box: the strip is hidden with the cover open, so the
            // state has to say so too, or LAY BELT was never offered and the gun counted a belt it did not show (2026-10-19).
            if (!CoverOpen && open && HasCover && MagInserted && VRConfig.BeltByHand.Value)
            {
                BeltLifted = BeltLaid;
                BeltLaid = false;
            }
            CoverOpen = open;
            Sync();
        }

        /// <summary>The belt end laid on the feed tray (cover open, a box seated).</summary>
        public bool LayBelt()
        {
            if (!HasCover || !CoverOpen || !MagInserted || BeltLaid)
            {
                return false;
            }
            BeltLaid = true;
            Sync();
            return true;
        }

        /// <summary>A belt-fed gun's box stays put while the belt runs through the closed cover.</summary>
        // Shut over a box without its belt laid too: the box came out through the closed cover (255.B6-2). Firing stays gated
        // by CanFire and FeedReady.
        public bool BoxLocked => HasCover && MagInserted && !CoverOpen;

        /// <summary>Pushes individual rounds into an internal magazine/tube.</summary>
        /// <returns>How many were accepted.</returns>
        public int LoadRounds(int count)
        {
            int room = TubeRoom;
            int accepted = Mathf.Clamp(count, 0, room);
            MagRounds += accepted;
            Sync();
            return accepted;
        }

        /// <summary>Slide/bolt pulled fully back: the chambered round (if any) is ejected.</summary>
        /// <returns>True if a live round was ejected.</returns>
        public bool RackBack()
        {
            SpentInChamber = false;
            bool ejected = Chambered;
            if (ejected)
            {
                Chambered = false;
                if (!VRConfig.LoseRoundOnRack.Value && !InfiniteSpare)
                {
                    ReturnSpareCapped(1); // capped like every other return: a full reserve grew by one per rack
                }
            }
            Sync();
            return ejected;
        }

        /// <summary>
        /// Slide/bolt goes forward: strips a round from the magazine if one is available. A slide that goes home chambers a
        /// round from the magazine (264): with none to chamber, a gun that locks open on empty stays caught back. Released
        /// with nothing chambered it went home unlocked; the empty animator held it back, and the next magazine (with rounds)
        /// sent it home without a round, so it went forward on its own and still needed a pull (user, 2026-10-18).
        /// A launcher, break action or revolver has no chamber apart from its rounds: one taken into the chamber left the
        /// rounds (which it fires from) one short, so a launcher reloaded in the free hand never fired again (scout, 2026-10-23).
        /// </summary>
        /// <returns>True if a round was chambered.</returns>
        public bool RackForward()
        {
            SlideLocked = false;
            bool hasRound = UsesChamber && FeedReady && MagRounds > 0;
            if (hasRound && !Chambered)
            {
                MagRounds--;
                Chambered = true;
            }
            if (!Chambered && Rig.LockOnEmpty && UsesChamber && !Rig.CyclesByHand)
            {
                SlideLocked = true;
                Log.InfoOnce($"caught:{Rig.Key}", $"{Rig.Key}: the slide went back with no round to chamber (no magazine it can feed, or an empty one); it stays caught back until one can be chambered.");
            }
            Sync();
            return Chambered;
        }

        /// <summary>How many rounds a fresh magazine taken from the pouch holds.</summary>
        public int RoundsForFreshMagazine()
        {
            int cap = Capacity;
            if (VRConfig.ChamberPlusOne.Value == false && Chambered)
            {
                cap = Mathf.Max(0, cap - 1);
            }
            return InfiniteSpare ? cap : Mathf.Min(cap, SpareAmmo);
        }

        /// <summary>
        /// Shells a shell-fed or break-action gun hands out of the pouch at a time: one, as they go in one by one, whatever
        /// chunks its flat reload loads. The largest allowed chunk that fit gave a double barrel authored to reload both at once
        /// nothing with one barrel empty ("FULL"), and two shells in the hand went in as one (user, 2026-10-22: "i cant put it
        /// back in one by one").
        /// </summary>
        public int ShellChunk()
        {
            int room = TubeRoom;
            int available = InfiniteSpare ? room : Mathf.Min(room, SpareAmmo);
            return Mathf.Min(1, available);
        }

        public void TakeSpare(int rounds)
        {
            if (!InfiniteSpare)
            {
                SpareAmmo -= rounds;
            }
        }

        /// <summary>Rounds back into the reserve, never past the game's reserve limit (where resupply stops too).</summary>
        public void ReturnSpare(int rounds) => ReturnSpareCapped(rounds);

        /// <summary>
        /// Rounds coming back from a magazine left lying around, up to the weapon's reserve limit (where the game caps
        /// resupply): an ammo bag may have refilled the reserve in the meantime.
        /// </summary>
        public void ReturnSpareCapped(int rounds)
        {
            if (InfiniteSpare || rounds <= 0)
            {
                return;
            }
            int cap = Weapon != null && Weapon.configuration.spareAmmo > 0 ? Weapon.configuration.spareAmmo : int.MaxValue;
            // (From 0 on a gun with no reserve: its -1 is the game's no-resupply mark, and a returned magazine came back a round short.)
            SpareAmmo = Mathf.Max(SpareAmmo, (int)System.Math.Min(cap, (long)Mathf.Max(0, SpareAmmo) + rounds));
        }

        private long _counterKey = long.MinValue;
        /// <summary>
        /// Each text made once per state: a full-auto gun changed its count every 6-9 frames and built a new string each time,
        /// garbage collected in the middle of a fire fight (allocation hunt, 2026-10-24). A few dozen states a gun; capped.
        /// </summary>
        private readonly System.Collections.Generic.Dictionary<long, string> _counterTexts = new System.Collections.Generic.Dictionary<long, string>();
        private string _counterText = "";

        /// <summary>
        /// Everything the counter text depends on, exactly (rounds, capacity and flags each in their own bits: a mixed hash of
        /// them could name two states alike, and the text cached for one would show for the other).
        /// </summary>
        private long CounterKey()
        {
            long flags = (Chambered ? 1 : 0) | (MagInserted ? 2 : 0) | (SlideLocked ? 4 : 0) | (Open ? 8 : 0) | (CoverOpen ? 16 : 0)
                         | (BeltLaid ? 32 : 0) | (BoltOpen ? 64 : 0) | (Participates ? 128 : 0) | (VRConfig.BeltByHand.Value ? 256 : 0)
                         | (VRConfig.ReloadNow == ReloadMode.Manual ? 512 : 0) | (BeltLifted ? 1024 : 0);
            return ((long)(uint)MagRounds << 32) | ((long)(Capacity & 0xFFFFF) << 12) | flags;
        }

        public string CounterText()
        {
            long key = CounterKey();
            if (key != _counterKey)
            {
                _counterKey = key;
                if (!_counterTexts.TryGetValue(key, out _counterText))
                {
                    _counterText = BuildCounterText();
                    if (_counterTexts.Count < 256)
                    {
                        _counterTexts[key] = _counterText;
                    }
                }
            }
            return _counterText;
        }

        /// <summary>
        /// The number an animator's "loaded ammo" is given (HeldWeapon.HoldSlideLockPose): a belt gun's rounds in the box, the belt being what
        /// it draws by it; any other gun's count, or 0 with its slide locked back. Pure: the offline tests check it.
        /// </summary>
        internal static int AnimatorRounds(bool beltIn, int boxRounds, bool chambered, bool slideLocked, int gunAmmo) =>
            beltIn ? boxRounds + (chambered ? 1 : 0) : slideLocked ? 0 : gunAmmo;

        private string BuildCounterText()
        {
            if (!Participates)
            {
                return "";
            }
            if (Rig.Action == ActionType.SingleShot)
            {
                return Open ? "OPEN" : MagRounds > 0 ? "LOADED" : "EMPTY";
            }
            if (Rig.Action == ActionType.BreakOpen)
            {
                return Open ? $"OPEN {MagRounds}/{Capacity}" : MagRounds.ToString();
            }
            // (The cover's steps in manual reloading only: switched to assisted with the cover left open, the gun fired, as the game
            // reloads it there, under a counter saying LAY BELT; machine gun scout, 2026-11-03.)
            bool byHand = VRConfig.ReloadNow == ReloadMode.Manual;
            if (byHand && HasCover && CoverOpen)
            {
                // Without belt laying by hand, shutting the cover lays it: the hint is to shut it. As over a belt only lifted off. A box
                // fired empty has no belt to lay: it said LAY BELT with nothing to take (belt scout, 2026-11-05).
                if (MagInserted && MagRounds <= 0)
                {
                    return "BOX EMPTY";
                }
                return MagInserted && !BeltLaid && VRConfig.BeltByHand.Value && !BeltLifted ? "LAY BELT" : MagInserted ? "CLOSE COVER" : "COVER OPEN";
            }
            if (byHand && HasCover && MagInserted && !BeltLaid)
            {
                return "OPEN COVER"; // shut over no belt: it has to be opened (and the belt laid, or the cover shut again)
            }
            if (UsesDetachableMag && !MagInserted)
            {
                return Chambered ? "1 / NO MAG" : "NO MAG";
            }
            string text = Chambered ? $"{MagRounds}+1" : MagRounds.ToString();
            if (SlideLocked)
            {
                text += " OPEN";
            }
            else if (!Chambered && MagRounds > 0)
            {
                text += " RACK";
            }
            return text;
        }
    }
}
