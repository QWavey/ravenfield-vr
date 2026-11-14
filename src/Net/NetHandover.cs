using RavenfieldVR.Core;
using UnityEngine;

namespace RavenfieldVR.Net
{
    /// <summary>
    /// A magazine handed to the friend (invention 3 of 2026-11-14: ammo from the partner, not the pouch): let go of a loaded magazine at
    /// another PC's VR player's hand (where his hands are is known here, from his pose) while he holds a gun it fits, and it is his: it
    /// goes from this hand, and his PC adds its rounds to that gun's reserve (kind <see cref="NetGame.GiveRounds"/>).
    /// </summary>
    internal static class NetHandover
    {
        /// <summary>How near his palm the magazine must be let go (m).</summary>
        private const float Reach = 0.2f;

        private static readonly NetWriter Out = new NetWriter(160);

        /// <summary>
        /// A loaded magazine let go by this player's hand: given to the friend whose hand it is at, if one is there with a gun it fits.
        /// True: it is his now (the caller takes it out of the world).
        /// </summary>
        internal static bool TryGive(Weapons.MagazineProp mag)
        {
            NetSession session = NetGame.SessionNow;
            if (mag == null || mag.Go == null || mag.Rounds <= 0 || session == null || session.State != NetState.Connected || session.PlayerCount < 2
                || !NetGame.InMatchNow)
            {
                return false;
            }
            string entry = EntryOf(mag.WeaponKey, '#');
            if (entry.Length == 0)
            {
                return false;
            }
            Vector3 at = mag.Go.transform.position;
            foreach (NetGame.Remote r in NetGame.RemoteValues)
            {
                Actor a = r.Actor;
                NetAvatar.Pose p = r.Pose;
                if (r.Id != 0 || a == null || a.dead || p == null || !p.Fresh || EntryOf(r.Weapon, '\u001F') != entry)
                {
                    continue; // (a player, alive, with his hands told and the same gun in them)
                }
                Vector3 origin = a.Position();
                if ((origin + p.LeftNow - at).sqrMagnitude > Reach * Reach && (origin + p.RightNow - at).sqrMagnitude > Reach * Reach)
                {
                    continue;
                }
                Out.Reset();
                Out.String(entry, 120);
                Out.VarInt(mag.Rounds);
                session.SendTo(r.Owner, NetGame.GiveRounds, Out, true);
                Log.Info($"Multiplayer: a magazine of {entry} ({mag.Rounds} rounds) handed to {r.Name}.");
                UI.UISystem.Instance?.Notice?.Show($"Magazine handed to {r.Name}.", 2f);
                return true;
            }
            return false;
        }

        /// <summary>Another player handed this player a magazine: its rounds into the reserve of the gun it fits.</summary>
        internal static void Take(byte sender, ref NetReader r)
        {
            string entry = r.String(120);
            int rounds = r.VarInt();
            Actor me = LocalPlayer.actor;
            if (me == null || me.dead || me.weapons == null || rounds <= 0 || rounds > 1000)
            {
                return;
            }
            Weapon fits = null;
            foreach (Weapon w in me.weapons)
            {
                if (w != null && w.weaponEntry != null && w.weaponEntry.name == entry && (fits == null || w == me.activeWeapon))
                {
                    fits = w;
                }
            }
            if (fits == null || fits.HasInfiniteSpareAmmo())
            {
                return;
            }
            int cap = fits.configuration != null && fits.configuration.spareAmmo > 0 ? fits.configuration.spareAmmo : int.MaxValue;
            int before = Mathf.Max(0, fits.spareAmmo);
            fits.spareAmmo = (int)Mathf.Min((long)before + rounds, cap);
            NetPlayer from = NetGame.SessionNow?.Player(sender);
            string name = from != null && from.Info != null && !string.IsNullOrEmpty(from.Info.Name) ? from.Info.Name : "Your friend";
            UI.UISystem.Instance?.Notice?.Show($"{name} handed you a magazine (+{fits.spareAmmo - before} rounds).", 3f);
            if (VRManager.Active && Rig.VRRig.Instance != null)
            {
                foreach (Rig.VRHand hand in Rig.VRRig.Instance.Hands)
                {
                    if (hand != null && hand.Tracked)
                    {
                        hand.Haptic(0.04f, 110f, 0.5f); // (felt in the hand it reached)
                    }
                }
            }
        }

        /// <summary>The weapon entry's name in a key ("name#prefab" here, "name&lt;sep&gt;workshop id" over the line).</summary>
        private static string EntryOf(string key, char separator)
        {
            if (string.IsNullOrEmpty(key))
            {
                return "";
            }
            int cut = key.IndexOf(separator);
            return cut >= 0 ? key.Substring(0, cut) : key;
        }
    }
}
