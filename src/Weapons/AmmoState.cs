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
        public bool Chambered;
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
                }
            }
        }
        public bool SlideLocked;
        /// <summary>Break-action / revolver: open for loading (cannot fire).</summary>
        public bool Open;
        /// <summary>Bolt / pump / lever left open by hand (manual actions do not spring shut).</summary>
        public bool BoltOpen;
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
        /// <summary>A bolt or pump gun fired: the case stays in the chamber until the action is worked.</summary>
        public bool SpentInChamber;
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

        public bool UsesDetachableMag => Rig.Action == ActionType.SelfLoading || (Rig.Action == ActionType.Manual && Rig.MagazinePath != null);
        public bool UsesChamber => Rig.Action == ActionType.SelfLoading || Rig.Action == ActionType.Manual || Rig.Action == ActionType.ShellFed;
        /// <summary>The action cycles itself after a shot (feeds the next round).</summary>
        public bool SelfLoading => (Rig.Action == ActionType.SelfLoading || Rig.Action == ActionType.ShellFed) && !Rig.CyclesByHand;
        /// <summary>
        /// The next round is chambered without the hand after a shot: a self-loading action, or any action with assisted
        /// reloading (the game's own fire animation works a bolt or pump then; nothing racks it by hand).
        /// </summary>
        public bool CyclesItself => SelfLoading || VRConfig.ReloadNow != ReloadMode.Manual;
        public bool HasCover => Rig.Hinge == HingeKind.FeedCover;
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

        /// <summary>Whether the trigger can drop the hammer on a round right now.</summary>
        /// <summary>The trigger pull that sent the slide forward does not also fire: held until the trigger is let go.</summary>
        public bool HoldFireUntilTriggerUp;

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
                        int chamber = UsesChamber && Chambered && Rig.Action != ActionType.SingleShot ? 1 : 0;
                        MagRounds = Mathf.Clamp(Weapon.ammo - chamber, 0, Capacity);
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
            Sync();
            return rounds;
        }

        public void InsertMagazine(int rounds)
        {
            MagInserted = true;
            MagRounds = Mathf.Clamp(rounds, 0, Mathf.Max(0, Capacity - (!VRConfig.ChamberPlusOne.Value && Chambered ? 1 : 0)));
            ReturnSpare(rounds - MagRounds); // more than this weapon's magazine holds: the rest goes to the reserve
            // A new box's belt still has to be laid on the feed tray under the cover. Without belt laying by hand, a box
            // seated under a cover already shut counts as laid, as shutting the cover over it would (nothing else would
            // ever lay it: the gun stayed dead, the counter asking to close a closed cover).
            BeltLaid = !HasCover || !VRConfig.BeltByHand.Value && !CoverOpen;
            Sync();
        }

        public void SetOpen(bool open)
        {
            Open = open;
            Sync();
        }

        public void SetCoverOpen(bool open)
        {
            if (CoverOpen && !open && MagInserted && !VRConfig.BeltByHand.Value)
            {
                BeltLaid = true; // closing the cover over the belt
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

        /// <summary>Slide/bolt goes forward: strips a round from the magazine if one is available.</summary>
        /// <returns>True if a round was chambered.</returns>
        public bool RackForward()
        {
            SlideLocked = false;
            bool hasRound = FeedReady && MagRounds > 0;
            if (hasRound && !Chambered)
            {
                MagRounds--;
                Chambered = true;
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

        /// <summary>Size of one loading motion for shell-fed weapons (largest allowed chunk that fits).</summary>
        public int ShellChunk()
        {
            int room = TubeRoom;
            int available = InfiniteSpare ? room : Mathf.Min(room, SpareAmmo);
            int best = 0;
            int[] allowed = Weapon.configuration.allowedReloads;
            if (allowed == null || allowed.Length == 0)
            {
                return Mathf.Min(1, available);
            }
            foreach (int a in allowed)
            {
                if (a <= available && a > best)
                {
                    best = a;
                }
            }
            return best;
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
            SpareAmmo = Mathf.Max(SpareAmmo, (int)System.Math.Min(cap, (long)SpareAmmo + rounds));
        }

        private int _counterKey = int.MinValue;
        private string _counterText = "";

        /// <summary>Everything the counter text depends on, so the string is only rebuilt when something changed.</summary>
        private int CounterKey()
        {
            int flags = (Chambered ? 1 : 0) | (MagInserted ? 2 : 0) | (SlideLocked ? 4 : 0) | (Open ? 8 : 0) | (CoverOpen ? 16 : 0)
                        | (BeltLaid ? 32 : 0) | (BoltOpen ? 64 : 0) | (Participates ? 128 : 0) | (VRConfig.BeltByHand.Value ? 256 : 0);
            return (MagRounds * 397) ^ (Capacity << 20) ^ (flags << 12);
        }

        public string CounterText()
        {
            int key = CounterKey();
            if (key != _counterKey)
            {
                _counterKey = key;
                _counterText = BuildCounterText();
            }
            return _counterText;
        }

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
            if (HasCover && CoverOpen)
            {
                // Without belt laying by hand, shutting the cover lays it: the hint is to shut it.
                return MagInserted && !BeltLaid && VRConfig.BeltByHand.Value ? "LAY BELT" : MagInserted ? "CLOSE COVER" : "COVER OPEN";
            }
            if (HasCover && MagInserted && !BeltLaid)
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
