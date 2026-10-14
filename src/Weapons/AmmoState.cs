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

        public int MagRounds;
        public bool Chambered;
        public bool MagInserted = true;
        public bool SlideLocked;
        /// <summary>Break-action / revolver: open for loading (cannot fire).</summary>
        public bool Open;
        /// <summary>Bolt / pump / lever left open by hand (manual actions do not spring shut).</summary>
        public bool BoltOpen;
        /// <summary>Machine gun feed cover lifted (cannot fire).</summary>
        public bool CoverOpen;
        /// <summary>Machine gun: the belt of the seated box lies on the feed tray (the cover was closed over it).</summary>
        public bool BeltLaid = true;
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
        public int Loaded => (MagInserted || !UsesDetachableMag ? MagRounds : 0) + (Chambered ? 1 : 0);

        public bool UsesDetachableMag => Rig.Action == ActionType.SelfLoading || (Rig.Action == ActionType.Manual && Rig.MagazinePath != null);
        public bool UsesChamber => Rig.Action == ActionType.SelfLoading || Rig.Action == ActionType.Manual || Rig.Action == ActionType.ShellFed;
        /// <summary>The action cycles itself after a shot (feeds the next round).</summary>
        public bool SelfLoading => (Rig.Action == ActionType.SelfLoading || Rig.Action == ActionType.ShellFed) && !Rig.CyclesByHand;
        /// <summary>
        /// The next round is chambered without the hand after a shot: a self-loading action, or any action with assisted
        /// reloading (the game's own fire animation works a bolt or pump then; nothing racks it by hand).
        /// </summary>
        public bool CyclesItself => SelfLoading || VRConfig.Reload.Value != ReloadMode.Manual;
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
        public bool CanFire
        {
            get
            {
                if (!Participates)
                {
                    return true;
                }
                if (LoadsDirectly)
                {
                    return MagRounds > 0 && !Open;
                }
                return Chambered && !SlideLocked && !BoltOpen && !(HasCover && CoverOpen);
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
            int loaded = Loaded;
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
            MagRounds = Mathf.Clamp(rounds, 0, Capacity);
            ReturnSpare(rounds - MagRounds); // more than this weapon's magazine holds: the rest goes to the reserve
            // A new box's belt still has to be laid on the feed tray under the cover.
            BeltLaid = !HasCover;
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
        public bool BoxLocked => HasCover && MagInserted && BeltLaid && !CoverOpen;

        /// <summary>Pushes individual rounds into an internal magazine/tube.</summary>
        /// <returns>How many were accepted.</returns>
        public int LoadRounds(int count)
        {
            int room = Capacity - MagRounds;
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
                    SpareAmmo++;
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
            int room = Capacity - MagRounds;
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

        public void ReturnSpare(int rounds)
        {
            if (!InfiniteSpare && rounds > 0)
            {
                SpareAmmo += rounds;
            }
        }

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
            int cap = Weapon != null ? Weapon.configuration.spareAmmo : int.MaxValue;
            SpareAmmo = Mathf.Max(SpareAmmo, (int)System.Math.Min(cap, (long)SpareAmmo + rounds));
        }

        private int _counterKey = int.MinValue;
        private string _counterText = "";

        /// <summary>Everything the counter text depends on, so the string is only rebuilt when something changed.</summary>
        private int CounterKey()
        {
            int flags = (Chambered ? 1 : 0) | (MagInserted ? 2 : 0) | (SlideLocked ? 4 : 0) | (Open ? 8 : 0) | (CoverOpen ? 16 : 0)
                        | (BeltLaid ? 32 : 0) | (BoltOpen ? 64 : 0) | (Participates ? 128 : 0);
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
                return MagInserted && !BeltLaid ? "LAY BELT" : MagInserted ? "CLOSE COVER" : "COVER OPEN";
            }
            if (HasCover && MagInserted && !BeltLaid)
            {
                return VRConfig.BeltByHand.Value ? "OPEN COVER" : "CLOSE COVER";
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
