using System.Collections.Generic;
using RavenfieldVR.Core;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    /// <summary>What to play for a sound: a game clip, the part of it (s), and the level and pitch to play it at.</summary>
    internal struct GameSlice
    {
        public AudioClip Clip;
        public float Start, Length, Gain, Pitch;
    }

    /// <summary>
    /// The game's own sounds for everything the mod plays: a weapon's own sound for an action (its reload's magazine in or out, its
    /// slide back or forward), else a vanilla weapon's of its kind (SoundDonors), else one of the game's clips named in the table below
    /// and cut where it was measured, played through the mod's 3D voices at a level and pitch. Never a synthesised sound (user,
    /// 2026-11-03: "Youre not allowed to make sounds on your own. Use game sounds"; 2026-11-04: all of them): with no game sound for it,
    /// nothing is played.
    /// </summary>
    internal static class GameSounds
    {
        /// <summary>A game clip by name and the part of it for a sound: From to To (s; To 0: to its end), its level and pitch.</summary>
        private sealed class Cut
        {
            public readonly string Name;
            public readonly float From, To, Gain, Pitch;

            public Cut(string name, float from = 0f, float to = 0f, float gain = 1f, float pitch = 1f)
            {
                Name = name;
                From = from;
                To = to;
                Gain = gain;
                Pitch = pitch;
            }
        }

        // The cuts, read off the clips' waveforms in the game's files (each starts about 20 ms ahead of its hit, the fade-in's room);
        // the levels bring each to about what it should be beside the others, with the volume its callers give it.
        private static readonly Cut[] RifleMagOut = { new Cut("SA80_Reload_Mag_Out_01") };
        private static readonly Cut[] PistolMagOut = { new Cut("S-IND7_Reload_MagOut_02"), new Cut("Walther_PPK_Reload_Mag_Out_01") };
        private static readonly Cut[] RifleMagIn = { new Cut("SA80_Reload_Mag_In_01") };
        private static readonly Cut[] PistolMagIn = { new Cut("S-IND7_Reload_MagIn_02"), new Cut("Walther_PPK_Reload_Mag_In_01", 0f, 0.3f) };
        private static readonly Cut[] SlideBack = { new Cut("S-IND7_Reload_ChargingHandle_01", 0f, 0.12f) };
        private static readonly Cut[] SlideForward = { new Cut("S-IND7_Reload_ChargingHandle_01", 0.16f, 0.33f) };
        // Into the well, up to before the seating clack (rifle's at 0.16 s, pistol's second hit at 0.17 s).
        private static readonly Cut[] RifleLip = { new Cut("SA80_Reload_Mag_In_01", 0f, 0.13f, 2f) };
        private static readonly Cut[] PistolLip = { new Cut("S-IND7_Reload_MagIn_02", 0.03f, 0.14f) };
        // The seating clack alone and a little lower: metal on metal.
        private static readonly Cut[] RifleKnock = { new Cut("SA80_Reload_Mag_In_01", 0.135f, 0f, 1f, 0.9f) };
        private static readonly Cut[] PistolKnock = { new Cut("S-IND7_Reload_MagIn_02", 0.085f, 0.25f, 1f, 0.9f) };
        // The game's own empty click (IngameUI.outOfAmmoSound; it starts 40 ms in).
        private static readonly Cut[] DryFire = { new Cut("Out_Of_Ammo_Delayed_Long_02", 0.02f, 0f, 0.7f) };
        private static readonly Cut[] ShellIn = { new Cut("ShotgunSounds-ShotgunShell") };
        // The grenade launcher's selector on the Patriot.
        private static readonly Cut[] Selector = { new Cut("Patriot_GL_Toggle_01", 0f, 0.14f, 2f) };
        // What the game plays when ammunition comes to the player (IngameUI.resupplySoundBank), its first rustle.
        private static readonly Cut[] Pouch =
        {
            new Cut("resupply_1", 0f, 0.28f, 0.6f), new Cut("resupply_2", 0f, 0.2f, 0.9f), new Cut("resupply_3", 0f, 0.3f, 0.6f), new Cut("resupply_4", 0f, 0.3f, 0.6f)
        };
        // The player's clothing (FpsActorController.clothFoleySounds), the loudest of the nine: a draw's metal is the game's own
        // Weapon.unholsterAudio, which an equip clip here would only have doubled.
        private static readonly Cut[] Holster =
        {
            new Cut("cloth2", 0.06f, 0.38f, 1.5f), new Cut("cloth5", 0.04f, 0.42f, 1.5f), new Cut("cloth7", 0.38f, 0.62f, 1.5f), new Cut("cloth9", 0.17f, 0.56f, 1.5f)
        };
        // A short rising pull that ends in a click, higher and quieter: the ring out of the fuse.
        private static readonly Cut[] PinPull = { new Cut("PP90_Equip_02c", 0.01f, 0f, 0.8f, 1.2f) };
        // One metal tick and its ring.
        private static readonly Cut[] Spoon = { new Cut("Katana_DeflectBulletImpact_04", 0.04f, 0f, 0.3f, 1.2f) };
        // Small metal on metal with a second hit some 85 ms on: a case bouncing, once higher and quieter.
        private static readonly Cut[] Casing =
        {
            new Cut("Katana_DeflectBulletImpact_01", 0f, 0f, 0.45f, 1.45f), new Cut("Katana_DeflectBulletImpact_02", 0f, 0f, 0.45f, 1.5f),
            new Cut("Katana_DeflectBulletImpact_03", 0f, 0f, 0.45f, 1.55f), new Cut("Katana_DeflectBulletImpact_05", 0f, 0f, 0.45f, 1.45f)
        };
        // The dullest, shortest indoor footsteps (about 2 kHz, gone in 50 ms), raised: a case tapping on earth.
        private static readonly Cut[] CasingSoft =
        {
            new Cut("Step1", 0f, 0f, 1f, 1.2f), new Cut("Step9", 0f, 0f, 1f, 1.15f), new Cut("Step10", 0f, 0f, 1f, 1.25f)
        };

        private static Cut[] CutsFor(MechSound kind, bool pistol)
        {
            switch (kind)
            {
                case MechSound.MagOut: return pistol ? PistolMagOut : RifleMagOut;
                case MechSound.MagIn: return pistol ? PistolMagIn : RifleMagIn;
                case MechSound.SlideBack: return SlideBack;
                case MechSound.SlideForward: return SlideForward;
                case MechSound.MagLip: return pistol ? PistolLip : RifleLip;
                case MechSound.MagKnock: return pistol ? PistolKnock : RifleKnock;
                case MechSound.DryFire: return DryFire;
                case MechSound.ShellIn: return ShellIn;
                case MechSound.Selector: return Selector;
                case MechSound.Pouch: return Pouch;
                case MechSound.Holster: return Holster;
                case MechSound.PinPull: return PinPull;
                case MechSound.Spoon: return Spoon;
                case MechSound.Casing: return Casing;
                case MechSound.CasingSoft: return CasingSoft;
                default: return null;
            }
        }

        /// <summary>
        /// What to play for a sound: the weapon's own (or a vanilla weapon's of its kind) for the four actions it has one for, else a
        /// loaded clip of the table, one at random. False with none (nothing is played; the hand's haptic still tells it).
        /// </summary>
        internal static bool Pick(MechSound kind, WeaponRigData d, out GameSlice slice)
        {
            slice = default(GameSlice);
            if (Find(d, kind, out AudioClip own, out float start, out float length, out float level))
            {
                slice = new GameSlice { Clip = own, Start = start, Length = length, Gain = level, Pitch = 1f };
                return true;
            }
            Cut[] cuts = CutsFor(kind, d != null && d.IsPistol);
            if (cuts == null)
            {
                return false;
            }
            int found = 0;
            foreach (Cut cut in cuts)
            {
                AudioClip clip = Named(cut.Name);
                if (clip == null || Random.Range(0, ++found) != 0)
                {
                    continue; // (each loaded one as likely as the others)
                }
                float end = cut.To > 0f ? Mathf.Min(cut.To, clip.length) : clip.length;
                slice = new GameSlice { Clip = clip, Start = cut.From, Length = end - cut.From, Gain = cut.Gain, Pitch = cut.Pitch };
            }
            if (found == 0)
            {
                Log.WarnOnce("nosound:" + kind, $"No game sound loaded for {kind}: it stays silent.");
            }
            return found > 0;
        }

        /// <summary>Every loaded clip by its name; a name it lacks is looked for again after <see cref="ScanEvery"/> s (a scene's come in).</summary>
        private static readonly Dictionary<string, AudioClip> Loaded = new Dictionary<string, AudioClip>();
        private const float ScanEvery = 30f;
        private static float _nextScan;

        /// <summary>A game clip by name, if it is loaded (the weapons' sounds are from the start, with their prefabs).</summary>
        private static AudioClip Named(string name)
        {
            if (Loaded.TryGetValue(name, out AudioClip clip) && clip != null)
            {
                return clip;
            }
            if (Time.unscaledTime < _nextScan)
            {
                return null;
            }
            _nextScan = Time.unscaledTime + ScanEvery;
            foreach (AudioClip c in Resources.FindObjectsOfTypeAll<AudioClip>())
            {
                if (c == null)
                {
                    continue;
                }
                if (!Loaded.TryGetValue(c.name, out AudioClip known) || known == null)
                {
                    Loaded[c.name] = c;
                }
            }
            Preload();
            return Loaded.TryGetValue(name, out clip) ? clip : null;
        }

        /// <summary>Has the table's clips decoded now: one played for the first time starts late, or not at its cut.</summary>
        private static void Preload()
        {
            foreach (MechSound kind in System.Enum.GetValues(typeof(MechSound)))
            {
                for (int pistol = 0; pistol < 2; pistol++)
                {
                    foreach (Cut cut in CutsFor(kind, pistol == 1))
                    {
                        if (Loaded.TryGetValue(cut.Name, out AudioClip clip) && clip != null && clip.loadState == AudioDataLoadState.Unloaded)
                        {
                            clip.LoadAudioData();
                        }
                    }
                }
            }
        }

        /// <summary>How many of the table's clips are loaded and the names of those that are not (the self-test's report).</summary>
        internal static int CountLoaded(List<string> missing)
        {
            int loaded = 0;
            foreach (MechSound kind in System.Enum.GetValues(typeof(MechSound)))
            {
                for (int pistol = 0; pistol < 2; pistol++)
                {
                    foreach (Cut cut in CutsFor(kind, pistol == 1))
                    {
                        if (Named(cut.Name) != null)
                        {
                            loaded++;
                        }
                        else if (!missing.Contains(cut.Name))
                        {
                            missing.Add(cut.Name);
                        }
                    }
                }
            }
            return loaded;
        }

        /// <summary>The game's audio for an action of this weapon, as the clip and the part of it (s) that belongs to the action.</summary>
        private static bool Find(WeaponRigData d, MechSound kind, out AudioClip clip, out float start, out float length, out float level)
        {
            clip = null;
            start = length = 0f;
            level = 1f;
            if (d == null)
            {
                return false;
            }
            // (Its own only with the mod's own sounds on, as WeaponSoundPlayer; a vanilla weapon's always, the game's own audio.)
            if (VRConfig.ModWeaponSounds.Value && d.Cues.TryGetValue(kind, out List<SoundCue> cues))
            {
                foreach (SoundCue cue in cues)
                {
                    if (Take(cue, out clip, out start, out length, out level))
                    {
                        return true;
                    }
                }
            }
            SoundCue donor = SoundDonors.Find(d, kind);
            return donor != null && Take(donor, out clip, out start, out length, out level);
        }

        private static bool Take(SoundCue cue, out AudioClip clip, out float start, out float length, out float level)
        {
            clip = null;
            start = length = 0f;
            level = 1f;
            if (cue == null)
            {
                return false;
            }
            if (cue.Kind == CueKind.ClipSlice && cue.Clip != null && cue.Length > 0.01f)
            {
                clip = cue.Clip;
                start = cue.Start;
                length = cue.Length;
                return true;
            }
            if (cue.Clips != null && cue.Clips.Length > 0)
            {
                clip = cue.Clips[cue.Clips.Length == 1 ? 0 : Random.Range(0, cue.Clips.Length)];
                if (clip != null && clip.length > 0.01f)
                {
                    length = clip.length;
                    level = cue.Volume; // (its sound bank's level, as WeaponSoundPlayer plays it)
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Plays part of the game's sound for <paramref name="kind"/> at <paramref name="at"/>: from <paramref name="from"/> for
        /// <paramref name="share"/> of it (shares of the action's part, 0..1), at <paramref name="volume"/> and
        /// <paramref name="pitch"/>. False when the game has no sound for it (nothing is played then).
        /// </summary>
        internal static bool PlayPart(WeaponRigData d, MechSound kind, Vector3 at, float from, float share, float volume, float pitch = 1f)
        {
            WeaponSystem sys = WeaponSystem.Instance;
            if (sys == null || sys.Sounds == null || volume <= 0f || !Pick(kind, d, out GameSlice s))
            {
                return false;
            }
            from = Mathf.Clamp01(from);
            share = Mathf.Clamp(share, 0.02f, 1f - from);
            return sys.Sounds.PlayGameClip(s.Clip, s.Start + s.Length * from, Mathf.Max(0.02f, s.Length * share), at, volume * s.Gain, pitch * s.Pitch);
        }
    }

    /// <summary>
    /// A dropped magazine landing (user, 2026-11-03, idea 5: they hit the ground without a sound, the cases with one): the end of its
    /// gun's own magazine-in sound, the metal knock of it seating, lower and as loud as it hit, duller and quieter on earth or grass.
    /// Game audio only (GameSounds): a gun with none of its own lends a vanilla weapon's.
    /// </summary>
    internal sealed class MagazineClatter : MonoBehaviour
    {
        internal MagazineProp Prop;
        private float _last = -1f;

        private void OnCollisionEnter(Collision c)
        {
            if (Prop == null || Prop.HeldBy != null || Prop.Data == null || Prop.OnRail)
            {
                return;
            }
            float speed = c.relativeVelocity.magnitude;
            float now = Time.unscaledTime;
            if (speed < 0.8f || now - _last < 0.12f)
            {
                return;
            }
            _last = now;
            bool soft = CaseSound.IsSoft(c.collider);
            float loud = Mathf.Clamp01((speed - 0.8f) / 4f);
            float volume = soft ? 0.1f + 0.15f * loud : 0.18f + 0.37f * loud;
            float pitch = (soft ? 0.66f : 0.8f) * Random.Range(0.95f, 1.05f);
            Vector3 at = c.contactCount > 0 ? c.GetContact(0).point : transform.position;
            GameSounds.PlayPart(Prop.Data, MechSound.MagIn, at, 0.6f, 0.4f, volume, pitch);
        }
    }
}
