// Rounds are never made or lost by the order things are done in. The mod's real src/Weapons/AmmoState.cs, compiled here against the stubs
// below (Weapon's static constructor calls into Unity, so the real one cannot load outside the game), driven by thousands of random
// actions on eight kinds of gun, in both reload modes, with each setting that changes the counts. After every action: everything in the
// gun, the reserve, loose and fired adds up to what it started with; nothing is negative; the game is told what the gun holds; a gun
// that can fire has a round. A fixed seed: a failure comes back the same. A new dependency of AmmoState stops this compiling: add it
// to the stubs. Run by tools/tests/run-tests.ps1.
using System;
using System.Collections.Generic;
using RavenfieldVR.Core;
using RavenfieldVR.Weapons;

public class Weapon
{
    public class Configuration
    {
        public int ammo, spareAmmo;
    }

    public int ammo, spareAmmo;
    public string name = "fuzz";
    public Configuration configuration = new Configuration();
    public bool HasInfiniteSpareAmmo() => configuration.spareAmmo == -2;
    public void OnAmmoChanged()
    {
    }
}

namespace UnityEngine
{
    public static class Mathf
    {
        public static int Max(int a, int b) => a > b ? a : b;
        public static int Min(int a, int b) => a < b ? a : b;
        public static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
    }
}

namespace RavenfieldVR.Core
{
    public enum ReloadMode { Manual, Assisted }

    public sealed class Setting
    {
        public bool Value;
    }

    public static class VRConfig
    {
        public static readonly Setting ChamberPlusOne = new Setting { Value = true }, LoseRoundOnRack = new Setting { Value = true }, BeltByHand = new Setting { Value = true };
        public static ReloadMode ReloadNow = ReloadMode.Manual;
    }

    public static class Log
    {
        public static void Info(string s)
        {
        }

        public static void Debug(string s)
        {
        }

        public static void InfoOnce(string key, string s)
        {
        }
    }
}

namespace RavenfieldVR.Weapons
{
    public enum ActionType { SelfLoading, Manual, ShellFed, SingleShot, BreakOpen, Throwable, Melee, NoAmmo }
    public enum HingeKind { None, BreakAction, Cylinder, FeedCover }

    public sealed class WeaponRigData
    {
        public string Key = "fuzz";
        public ActionType Action;
        public HingeKind Hinge;
        public bool LockOnEmpty, CyclesByHand;
        public int[] MagazinePath;
    }
}

namespace RavenfieldVR.Tests
{
    internal static class AmmoFuzz
    {
        private const int Steps = 40000;

        private sealed class Gun
        {
            public string Name;
            public ActionType Action;
            public HingeKind Hinge;
            public bool Lock, ByHand, Magazine;
        }

        private static readonly Gun[] Guns =
        {
            new Gun { Name = "rifle", Action = ActionType.SelfLoading, Lock = true, Magazine = true },
            new Gun { Name = "rifle with no lock", Action = ActionType.SelfLoading, Magazine = true },
            new Gun { Name = "bolt gun with a magazine", Action = ActionType.Manual, ByHand = true, Magazine = true },
            new Gun { Name = "pump with a tube", Action = ActionType.Manual, ByHand = true },
            new Gun { Name = "shell fed", Action = ActionType.ShellFed },
            new Gun { Name = "machine gun", Action = ActionType.SelfLoading, Hinge = HingeKind.FeedCover, Lock = true, Magazine = true },
            new Gun { Name = "break action", Action = ActionType.BreakOpen },
            new Gun { Name = "launcher", Action = ActionType.SingleShot }
        };

        private static int Main()
        {
            long actions = 0;
            int runs = 0, faults = 0;
            foreach (ReloadMode mode in new[] { ReloadMode.Manual, ReloadMode.Assisted })
            {
                foreach (bool lose in new[] { true, false })
                {
                    foreach (bool plusOne in new[] { true, false })
                    {
                        foreach (bool belt in new[] { true, false })
                        {
                            foreach (Gun gun in Guns)
                            {
                                VRConfig.ReloadNow = mode;
                                VRConfig.LoseRoundOnRack.Value = lose;
                                VRConfig.ChamberPlusOne.Value = plusOne;
                                VRConfig.BeltByHand.Value = belt;
                                string fault = Run(gun, 4711 + runs, ref actions);
                                runs++;
                                if (fault != null)
                                {
                                    faults++;
                                    Console.WriteLine($"FAIL {gun.Name}, {mode}, rounds lost on a rack {lose}, chamber plus one {plusOne}, belts by hand {belt}: {fault}");
                                }
                            }
                        }
                    }
                }
            }
            // The settings changed in the middle of it, as the panel can at any moment.
            foreach (Gun gun in Guns)
            {
                for (int seed = 0; seed < 8; seed++)
                {
                    string fault = Run(gun, 9000 + runs, ref actions, flipSettings: true);
                    runs++;
                    if (fault != null)
                    {
                        faults++;
                        Console.WriteLine($"FAIL {gun.Name}, settings changing: {fault}");
                    }
                }
            }
            // A reserve that runs dry in the run (7 rounds), and the game's own -1 (none, never resupplied; 2026-11-11: a shell was
            // handed out with -1 rounds).
            foreach (int reserve in new[] { 7, 0, -1 })
            {
                foreach (Gun gun in Guns)
                {
                    VRConfig.ReloadNow = ReloadMode.Manual;
                    string fault = Run(gun, 12000 + runs, ref actions, flipSettings: true, reserve: reserve);
                    runs++;
                    if (fault != null)
                    {
                        faults++;
                        Console.WriteLine($"FAIL {gun.Name}, a reserve of {reserve}: {fault}");
                    }
                }
            }
            string belt2 = BeltUnderCover();
            if (belt2 != null)
            {
                faults++;
                Console.WriteLine("FAIL belt under the cover: " + belt2);
            }
            string variant = VariantSharesMagazine();
            if (variant != null)
            {
                faults++;
                Console.WriteLine("FAIL a fire-mode variant: " + variant);
            }
            Console.WriteLine($"ammo fuzz: {actions} random actions in {runs} runs, {faults} failed");
            return faults == 0 ? 0 : 1;
        }

        private static readonly System.Reflection.MethodInfo BuildText =
            typeof(AmmoState).GetMethod("BuildCounterText", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        /// <summary>
        /// A fire-mode variant on the same model (a weapon of its own in the game) shares its parent's magazine in the mod's count,
        /// and the game's count of each is told when it becomes the one in use, as HeldWeapon does at the switch (2026-11-11:
        /// fired empty as one, reloaded as the other, switched back: a full counter and a trigger the game refused).
        /// </summary>
        private static string VariantSharesMagazine()
        {
            VRConfig.ReloadNow = ReloadMode.Manual;
            var rig = new WeaponRigData { Action = ActionType.SelfLoading, LockOnEmpty = false, MagazinePath = new[] { 0 } };
            var autoGun = new Weapon { ammo = 30, spareAmmo = 200 };
            var semiGun = new Weapon { ammo = 30, spareAmmo = 200 };
            foreach (Weapon w in new[] { autoGun, semiGun })
            {
                w.configuration.ammo = 30;
                w.configuration.spareAmmo = 100000;
            }
            var auto = new AmmoState(autoGun, rig);
            var semi = new AmmoState(semiGun, rig);
            semi.MagazineOf = auto;
            // Fired empty as the automatic one.
            for (int i = 0; i < 100 && auto.CanFire; i++)
            {
                autoGun.ammo--;
                auto.OnShot();
            }
            auto.Reconcile();
            if (auto.Loaded != 0 || autoGun.ammo != 0)
            {
                return $"fired empty, the gun holds {auto.Loaded} and the game was told {autoGun.ammo}";
            }
            // Switched to the other mode (its count told, as at the switch), a fresh magazine put in and racked there.
            semi.Sync();
            semi.EjectMagazine();
            int fresh = semi.RoundsForFreshMagazine();
            semi.TakeSpare(fresh);
            semi.InsertMagazine(fresh);
            semi.RackBack();
            semi.RackForward();
            semi.Reconcile();
            if (semi.Loaded <= 0 || semiGun.ammo != semi.Loaded)
            {
                return $"reloaded as the variant: the gun holds {semi.Loaded}, the game was told {semiGun.ammo}";
            }
            // Back to the first: the same rounds, and the game told so.
            auto.Sync();
            if (auto.Loaded != semi.Loaded)
            {
                return $"the two disagree on the magazine they share ({auto.Loaded} and {semi.Loaded})";
            }
            if (autoGun.ammo != auto.Loaded)
            {
                return $"switched back, the gun holds {auto.Loaded} and the game was told {autoGun.ammo} (it refuses the trigger at 0)";
            }
            return null;
        }

        /// <summary>A look under the cover keeps the belt; a box changed under it does not (Tasks 274, 312, belt scout 2026-11-05).</summary>
        private static string BeltUnderCover()
        {
            VRConfig.ReloadNow = ReloadMode.Manual;
            VRConfig.BeltByHand.Value = true;
            var weapon = new Weapon { ammo = 100, spareAmmo = 200 };
            weapon.configuration.ammo = 100;
            weapon.configuration.spareAmmo = 100000;
            var a = new AmmoState(weapon, new WeaponRigData { Action = ActionType.SelfLoading, Hinge = HingeKind.FeedCover, LockOnEmpty = true, MagazinePath = new[] { 0 } });
            if (!a.FeedReady)
            {
                return "a full gun does not feed";
            }
            a.SetCoverOpen(true);
            if (a.BeltLaid || a.FeedReady)
            {
                return "the belt stays on the tray with the cover up";
            }
            a.SetCoverOpen(false);
            if (!a.FeedReady)
            {
                return "opened and shut, the gun is dead";
            }
            a.SetCoverOpen(true);
            int rounds = a.EjectMagazine();
            a.InsertMagazine(rounds);
            a.SetCoverOpen(false);
            if (a.BeltLaid)
            {
                return "a box changed under the cover laid its own belt";
            }
            a.SetCoverOpen(true);
            if (!a.LayBelt())
            {
                return "the new box's belt could not be laid";
            }
            a.SetCoverOpen(false);
            return a.FeedReady ? null : "a belt laid by hand does not feed";
        }

        private static string Run(Gun gun, int seed, ref long actions, bool flipSettings = false, int reserve = 200)
        {
            var weapon = new Weapon { ammo = 30, spareAmmo = reserve };
            weapon.configuration.ammo = 30;
            // (No cap: a capped return loses rounds on purpose. A reserve of -1 is the game's "none, and never resupplied": it counts
            // as no rounds, never as one fewer than none.)
            weapon.configuration.spareAmmo = reserve < 0 ? -1 : 100000;
            var rig = new WeaponRigData { Action = gun.Action, Hinge = gun.Hinge, LockOnEmpty = gun.Lock, CyclesByHand = gun.ByHand, MagazinePath = gun.Magazine ? new int[0] : null };
            var a = new AmmoState(weapon, rig);
            var loose = new List<int>();
            int gone = 0, total = a.MagRounds + (a.Chambered ? 1 : 0) + a.SpareAmmo;
            var random = new Random(seed);
            var last = new Queue<string>();
            for (int step = 0; step < Steps; step++)
            {
                bool lose = VRConfig.LoseRoundOnRack.Value;
                string op = null;
                switch (random.Next(flipSettings ? 14 : 13))
                {
                    case 13:
                        switch (random.Next(4))
                        {
                            case 0:
                                VRConfig.ReloadNow = VRConfig.ReloadNow == ReloadMode.Manual ? ReloadMode.Assisted : ReloadMode.Manual;
                                op = "reloading " + VRConfig.ReloadNow;
                                break;
                            case 1:
                                VRConfig.BeltByHand.Value = !VRConfig.BeltByHand.Value;
                                op = "belts by hand " + VRConfig.BeltByHand.Value;
                                break;
                            case 2:
                                VRConfig.ChamberPlusOne.Value = !VRConfig.ChamberPlusOne.Value;
                                op = "chamber plus one " + VRConfig.ChamberPlusOne.Value;
                                break;
                            default:
                                VRConfig.LoseRoundOnRack.Value = !VRConfig.LoseRoundOnRack.Value;
                                op = "lose on rack " + VRConfig.LoseRoundOnRack.Value;
                                break;
                        }
                        break;
                    case 0:
                    case 1:
                    case 2:
                        if (a.CanFire)
                        {
                            op = "fire";
                            weapon.ammo--;
                            gone++;
                            a.OnShot();
                        }
                        break;
                    case 3:
                        if (a.UsesDetachableMag && a.MagInserted && !a.BoxLocked)
                        {
                            op = "eject";
                            loose.Add(a.EjectMagazine());
                        }
                        break;
                    case 4:
                        if (a.UsesDetachableMag && loose.Count > 0)
                        {
                            op = "insert";
                            int i = random.Next(loose.Count), rounds = loose[i];
                            loose.RemoveAt(i);
                            a.InsertMagazine(rounds);
                        }
                        break;
                    case 5:
                        if (a.UsesDetachableMag)
                        {
                            op = "fresh magazine";
                            int rounds = a.RoundsForFreshMagazine();
                            a.TakeSpare(rounds);
                            loose.Add(rounds);
                        }
                        break;
                    case 6:
                        if (loose.Count > 0)
                        {
                            op = "stow";
                            int i = random.Next(loose.Count);
                            a.ReturnSpareCapped(loose[i]);
                            loose.RemoveAt(i);
                        }
                        break;
                    case 7:
                        if (a.UsesChamber)
                        {
                            op = "rack back";
                            if (a.RackBack() && lose)
                            {
                                gone++; // (a live round on the ground, as the caller throws it out)
                            }
                        }
                        break;
                    case 8:
                        op = "rack forward";
                        a.RackForward();
                        break;
                    case 9:
                        if (a.HasCover)
                        {
                            op = a.CoverOpen ? "cover shut" : "cover open";
                            a.SetCoverOpen(!a.CoverOpen);
                        }
                        break;
                    case 10:
                        if (a.HasCover)
                        {
                            op = "lay belt";
                            a.LayBelt();
                        }
                        break;
                    case 11:
                        if (!a.UsesDetachableMag)
                        {
                            op = "load a shell";
                            int want = a.ShellChunk();
                            a.TakeSpare(want);
                            a.ReturnSpare(want - a.LoadRounds(want));
                        }
                        break;
                    default:
                        if (gun.Action == ActionType.BreakOpen || gun.Action == ActionType.SingleShot)
                        {
                            op = a.Open ? "shut" : "open";
                            bool open = !a.Open;
                            a.SetOpen(open);
                            if (open)
                            {
                                int live = a.TakeLiveShells();
                                gone += lose ? live : 0;
                                a.TakeSpentCases();
                            }
                        }
                        break;
                }
                a.Reconcile();
                actions++;
                if (op != null)
                {
                    last.Enqueue(op);
                    if (last.Count > 10)
                    {
                        last.Dequeue();
                    }
                }
                int held = 0;
                foreach (int r in loose)
                {
                    held += r;
                }
                int sum = a.MagRounds + (a.Chambered ? 1 : 0) + a.SpareAmmo + held + gone;
                string wrong = null;
                if (sum != total)
                {
                    wrong = $"{sum} rounds of {total} (magazine {a.MagRounds}, chamber {a.Chambered}, reserve {a.SpareAmmo}, loose {held}, gone {gone})";
                }
                else if (a.MagRounds < 0 || a.SpareAmmo < 0 || loose.Exists(r => r < 0) || a.RoundsForFreshMagazine() < 0)
                {
                    wrong = "a negative count";
                }
                else if (a.UsesDetachableMag && !a.MagInserted && a.MagRounds != 0)
                {
                    wrong = "rounds in a magazine that is out";
                }
                else if (weapon.ammo != (a.SlideLocked && !a.Chambered ? 0 : a.Loaded))
                {
                    wrong = $"the game told {weapon.ammo}, the gun holds {a.Loaded}";
                }
                else if (a.CanFire && a.Loaded <= 0)
                {
                    wrong = "able to fire with nothing loaded";
                }
                else if (step % 7 == 0 && a.CounterText() != (string)BuildText.Invoke(a, null))
                {
                    wrong = $"the counter says '{a.CounterText()}', its state says '{BuildText.Invoke(a, null)}'";
                }
                if (wrong != null)
                {
                    return $"step {step}: {wrong} after {string.Join(", ", last.ToArray())}";
                }
            }
            return null;
        }
    }
}
