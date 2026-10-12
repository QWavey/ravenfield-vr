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
        public bool SelfLoading => Rig.Action == ActionType.SelfLoading;
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
                if (Rig.Action == ActionType.SingleShot)
                {
                    return MagRounds > 0;
                }
                return Chambered && !SlideLocked;
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
                    // Refilled by the game (resupply crate, ravenscript, infinite-ammo mutator…).
                    Log.Debug($"{Weapon.name}: ammo changed externally {_expectedAmmo} -> {Weapon.ammo}, adopting.");
                    AdoptWeaponAmmo();
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
            if (Rig.Action == ActionType.SingleShot)
            {
                MagRounds = Mathf.Max(0, MagRounds - 1);
                Sync();
                return;
            }
            Chambered = false;
            if (SelfLoading)
            {
                if (MagInserted && MagRounds > 0)
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
            Sync();
            return rounds;
        }

        public void InsertMagazine(int rounds)
        {
            MagInserted = true;
            MagRounds = Mathf.Clamp(rounds, 0, Capacity);
            Sync();
        }

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
            bool hasRound = (MagInserted || !UsesDetachableMag) && MagRounds > 0;
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

        public string CounterText()
        {
            if (!Participates)
            {
                return "";
            }
            if (Rig.Action == ActionType.SingleShot)
            {
                return MagRounds > 0 ? "LOADED" : "EMPTY";
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
