using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using RavenfieldVR.Core;
using UnityEngine;

namespace RavenfieldVR.Net
{
    /// <summary>
    /// The multiplayer checked in a running match with one game only (-rfvr-netcheck with the autotest; tools/vrshots.py --netcheck):
    /// the game hosts on a UDP port, and a second player made here (a session of its own on another port of this PC, sending what
    /// a real one sends) joins it. Measured: the join, his soldier standing in the match and following what he tells, a hit on
    /// him passed to his PC and not taken here, a hit from him taken by the player, his death and return, the host's soldiers told
    /// to him, his leaving. A failure is worded in capitals (NOT ...), the autotest counts those.
    /// </summary>
    internal static class NetGameCheck
    {
        private const int Port = 7141;

        internal static IEnumerator Run(Action<string> report)
        {
            Actor player = LocalPlayer.actor;
            if (player == null || player.dead || ActorManager.instance == null)
            {
                report("multiplayer: skipped (no player on the field)");
                yield break;
            }
            if (Time.timeScale == 0f)
            {
                report("the game's time stood still as the check began (" + Core.AutoTest.WhyPaused() + "): set running again, NOT A RESULT OF THE CHECK");
                GameManager.UnpauseGame();
            }
            NetGame.Init();
            Multiplayer.HostLan(Port);
            if (!Multiplayer.Hosting)
            {
                report("multiplayer host on UDP: NOT HOSTING (" + Multiplayer.Status + ")");
                yield break;
            }

            UdpTransport line = null;
            NetSession ghost = null;
            int hitsGot = 0, statesGot = 0, othersHits = 0, knocksGot = 0, posesGot = 0, poseFlags = 0;
            Vector3 poseMuzzle = Vector3.zero, poseLeft = Vector3.zero, poseRight = Vector3.zero;
            Quaternion poseMuzzleRot = Quaternion.identity;
            float hitDamage = 0f;
            var told = new HashSet<ushort>();
            string failed = null;
            try
            {
                line = new UdpTransport(0);
                ghost = NetSession.Join(line, UdpTransport.Parse("127.0.0.1", Port), new NetPlayerInfo
                {
                    Name = "Ghost", GameBuild = Multiplayer.GameBuild, ModVersion = PluginInfo.Version, Vr = false
                });
                ghost.Message += (byte kind, byte sender, ref NetReader r) =>
                {
                    if (kind == NetGame.Hit)
                    {
                        // (The host's own soldiers shoot the stand-in too, and each of their hits is passed on as well: 6 messages
                        // in the first run with the match running. The player's is the one from soldier 0 with the check's damage.)
                        r.UShort();
                        ushort shooter = r.UShort();
                        float damage = r.Float();
                        othersHits++;
                        if (shooter == 0 && Mathf.Abs(damage - 30f) < 0.01f)
                        {
                            hitDamage = damage;
                            hitsGot++;
                            othersHits--;
                        }
                    }
                    else if (kind == NetGame.VrPose)
                    {
                        posesGot++;
                        poseFlags = r.Byte();
                        r.SmallVector();
                        poseLeft = r.SmallVector();
                        r.Rotation();
                        poseRight = r.SmallVector();
                        r.Rotation();
                        poseMuzzle = r.SmallVector();
                        poseMuzzleRot = r.Rotation();
                    }
                    else if (kind == NetGame.Knock)
                    {
                        knocksGot++;
                    }
                    else if (kind == NetGame.ActorStates)
                    {
                        int n = r.Byte();
                        for (int i = 0; i < n; i++)
                        {
                            told.Add(r.UShort());
                            r.Byte(); // flags
                            r.Byte(); // flags, the second byte (ladder, off the ground, swimming)
                            r.Vector();
                            r.Byte(); // velocity across, along
                            r.Byte();
                            r.Byte(); // and up
                            r.Byte(); // yaw, pitch
                            r.Byte();
                        }
                        statesGot++;
                    }
                };
            }
            catch (Exception e)
            {
                failed = e.Message;
            }
            if (failed != null)
            {
                report("multiplayer second player: NOT MADE (" + failed + ")");
                Multiplayer.Leave();
                yield break;
            }

            var w = new NetWriter(256);
            // What the ghost tells of himself (the same bytes a real player's game writes).
            Vector3 forward = Vector3.ProjectOnPlane(player.transform.forward, Vector3.up).normalized;
            Vector3 side = Vector3.Cross(Vector3.up, forward);
            Vector3 start = player.Position() + forward * 7f;
            // (From chest height, not from above: seven metres ahead stood a wall of barriers, and the second player was put on top of it, walked off its edge and fell, run after run.)
            if (Physics.Raycast(start + Vector3.up * 1.2f, Vector3.down, out RaycastHit ground, 12f, ~Hitbox.ALL_HITBOX_LAYER_MASK & ~(1 << 2), QueryTriggerInteraction.Ignore))
            {
                start = ground.point;
            }
            int enemyTeam = 1 - player.team;
            string weapon = player.activeWeapon != null && player.activeWeapon.weaponEntry != null ? player.activeWeapon.weaponEntry.name
                : WeaponManager.instance.allWeapons.First(e => e != null && e.prefab != null && !e.hidden && e.slot == WeaponManager.WeaponSlot.Primary).name;
            string sidearm = WeaponManager.instance.allWeapons.First(e => e != null && e.prefab != null && !e.hidden && e.slot == WeaponManager.WeaponSlot.Secondary).name;
            WeaponManager.WeaponEntry grenadeEntry = WeaponManager.instance.allWeapons.FirstOrDefault(e => e != null && e.prefab != null && !e.hidden
                && e.prefab.GetComponent<ThrowableWeapon>() != null && e.prefab.GetComponent<Weapon>().configuration.projectilePrefab != null
                && e.prefab.GetComponent<Weapon>().configuration.projectilePrefab.GetComponent<GrenadeProjectile>() != null);
            string grenade = grenadeEntry != null ? grenadeEntry.name : "";
            int ghostSlot = 0;
            bool ghostAlive = true;
            Vector3 ghostAt = start, ghostVelocity = Vector3.zero;
            float nextSend = 0f;
            bool infoSent = false;
            void Pump()
            {
                ghost.Update(Time.realtimeSinceStartupAsDouble);
                if (ghost.State != NetState.Connected || Time.unscaledTime < nextSend)
                {
                    return;
                }
                nextSend = Time.unscaledTime + 1f / 15f;
                if (!infoSent)
                {
                    infoSent = true;
                    w.Reset();
                    w.UShort(0);
                    w.String("Ghost");
                    w.String(ghostSlot == 0 ? weapon : sidearm);
                    w.Byte((byte)ghostSlot);
                    w.Byte(5);
                    w.String(weapon);
                    w.String(sidearm);
                    w.String(grenade);
                    w.String("");
                    w.String("");
                    ghost.Send(NetGame.ActorInfo, w, true);
                }
                w.Reset();
                w.Byte(1);
                NetGame.PutState(w, 0, (ghostAlive ? NetGame.Alive : 0) | (enemyTeam == 1 ? NetGame.TeamOne : 0), ghostAt, ghostVelocity, -forward);
                ghost.Send(NetGame.ActorStates, w, false);
            }
            IEnumerator For(float seconds, Action each = null)
            {
                float until = Time.unscaledTime + seconds;
                while (Time.unscaledTime < until)
                {
                    if (Time.timeScale == 0f && !Multiplayer.Active) // (in a session the match itself keeps time running)
                    {
                        report("the game's time stood still under the check (" + Core.AutoTest.WhyPaused() + "): set running again, NOT A RESULT OF THE CHECK");
                        GameManager.UnpauseGame();
                    }
                    each?.Invoke();
                    Pump();
                    yield return null;
                }
            }
            NetGame.Remote Standin() => NetGame.AllRemotes.FirstOrDefault(r => r.Id == 0);

            // 1. The join.
            float joinBy = Time.unscaledTime + 5f;
            while (ghost.State == NetState.Connecting && Time.unscaledTime < joinBy)
            {
                Pump();
                yield return null;
            }
            report($"multiplayer join over UDP: {(ghost.State == NetState.Connected ? $"joined as player {ghost.LocalId}, {Multiplayer.Session.PlayerCount} in the game" : "NOT JOINED (" + (ghost.CloseReason ?? "no answer") + ")")}");
            if (ghost.State != NetState.Connected)
            {
                ghost.Dispose();
                line.Dispose();
                Multiplayer.Leave();
                yield break;
            }

            // (A picture of the panel with the two players in it, to look its layout over: reports/shots/netcheck.)
            UI.MultiplayerPanel panel = UI.UISystem.Instance != null ? UI.UISystem.Instance.MultiplayerPanel : null;
            if (panel != null)
            {
                panel.Open();
                yield return For(0.5f);
                yield return new WaitForEndOfFrame();
                Core.Shots.Capture("multiplayer-panel-hosting");
                panel.Close();
            }

            // 2. His soldier here: made, of his side, with his weapon; then walking where he says (2 m/s sideways for 4 s).
            yield return For(1.5f);
            NetGame.Remote remote = Standin();
            Actor body = remote != null ? remote.Actor : null;
            // (Until his gun is in his hands: animated like a soldier in view, he draws it in the gun's own time, not at once.)
            float drawnBy = Time.unscaledTime + 4f;
            while (body != null && body.activeWeapon == null && Time.unscaledTime < drawnBy)
            {
                Pump();
                yield return null;
            }
            string carried = body != null && body.activeWeapon != null && body.activeWeapon.weaponEntry != null ? body.activeWeapon.weaponEntry.name : "nothing";
            report($"multiplayer stand-in: {(body != null && !body.dead ? $"on the field, team {body.team} (told {enemyTeam}), carrying {carried} (told {weapon})" : "NOT ON THE FIELD")}"
                   + (body != null && body.team != enemyTeam ? " NOT HIS SIDE" : "") + (body != null && !body.dead && carried != weapon ? " NOT HIS WEAPON" : "")
                   + (remote != null && remote.Tag != null && remote.Tag.gameObject.activeSelf && remote.Tag.text == "Ghost" ? ", his name over him" : " HIS NAME NOT SHOWN"));
            if (body != null)
            {
                yield return new WaitForEndOfFrame();
                Core.Shots.Capture("multiplayer-other-player");
            }
            if (body == null)
            {
                ghost.Dispose();
                line.Dispose();
                Multiplayer.Leave();
                yield break;
            }
            ghostVelocity = side * 2f;
            float worst = 0f;
            bool knocked = false;
            int askedBefore = NetGame.VelocityAsked;
            float fastest = 0f;
            float walkStart = Time.unscaledTime;
            yield return For(4f, () =>
            {
                ghostAt = start + side * (2f * (Time.unscaledTime - walkStart));
                knocked |= body.fallenOver;
                if (Time.unscaledTime - walkStart > 1f && !knocked)
                {
                    Vector3 off = body.Position() - ghostAt;
                    off.y = 0f;
                    worst = Mathf.Max(worst, off.magnitude);
                    fastest = Mathf.Max(fastest, body.Velocity().magnitude);
                }
            });
            ghostVelocity = Vector3.zero;
            // (Knocked over on the way by the match's own soldiers, a blast or a blow: he lies 2.5 s, then stands where told.)
            yield return For(knocked ? 5f : 1f);
            Vector3 rest = body.Position() - ghostAt;
            rest.y = 0f;
            report($"multiplayer stand-in walking 8 m as told: {(knocked ? "knocked over on the way by the match (lies, then stands where told), before that " : "")}at most {worst:0.00} m behind{(worst > 1.5f ? " NOT FOLLOWING" : "")}, {rest.magnitude:0.00} m off at rest{(rest.magnitude > 0.5f ? " NOT THERE" : "")}"
                   + $" (his speed asked {NetGame.VelocityAsked - askedBefore} time(s), answered up to {fastest:0.0} m/s, last {NetGame.LastVelocity.magnitude:0.0}; moving {body.moving}, fallen {body.fallenOver}, frozen {body.isFrozen}, auto-move {body.autoMoveActor})");

            // 2b. All he carries is on his soldier, and the weapon in his hands follows his: he draws his sidearm.
            // (What he does not hold is seen on him: the sidearm at his thigh now, the long gun on his back once he draws the sidearm.)
            yield return For(0.8f);
            int carriedWithLong = NetAvatar.CarriedShown;
            string CarriedBy(Actor a) => string.Join(", ", a.weapons.Where(x => x != null && x.weaponEntry != null).Select(x => x.weaponEntry.name).ToArray());
            string carriedAll = CarriedBy(body);
            ghostSlot = 1;
            infoSent = false;
            yield return For(2.5f);
            string inHands = body.activeWeapon != null && body.activeWeapon.weaponEntry != null ? body.activeWeapon.weaponEntry.name : "nothing";
            report($"multiplayer stand-in's loadout: carries [{carriedAll}] (told {weapon}, {sidearm}){(carriedAll.Contains(weapon) && carriedAll.Contains(sidearm) ? "" : " NOT ALL HE CARRIES")}; "
                   + $"he drew his sidearm: in the stand-in's hands {inHands}{(inHands == sidearm ? "" : " NOT THE SIDEARM")}; "
                   + $"seen on him: {carriedWithLong} weapon(s) with the long gun in his hands, {NetAvatar.CarriedShown} with the sidearm{(carriedWithLong != 1 || NetAvatar.CarriedShown != 1 ? " NOT ONE EACH TIME" : "")}");
            {
                Vector3 chestNow = body.CenterPosition() + Vector3.up * 0.2f, back = -body.transform.forward, rightOf = body.transform.right;
                (Vector3? position, Quaternion? rotation) Behind(Vector3 from) => (chestNow + from, Quaternion.LookRotation(-from, Vector3.up));
                yield return new WaitForEndOfFrame();
                Core.Shots.CaptureFirstClear("multiplayer-carried", new[]
                {
                    Behind((back + rightOf * 0.6f).normalized * 2f + Vector3.up * 0.2f), Behind((back - rightOf * 0.6f).normalized * 2f + Vector3.up * 0.2f),
                    Behind(rightOf * 2f + Vector3.up * 0.2f), Behind(back * 1.6f + Vector3.up * 1.2f)
                }, 60f);
            }
            ghostSlot = 0;
            infoSent = false;
            yield return For(1.5f);

            // (This game's own player draws a gun first: what he tells of himself is read below, and he spawns empty-handed.)
            if (player.activeWeapon == null && player.weapons != null && player.weapons[0] != null)
            {
                player.SwitchWeapon(0);
                yield return For(1.5f);
            }
            // (And his soldier has the long gun back in his hands: with the sidearm still out, a muzzle told 85 cm in front put the
            // grip beyond the arm's reach and the hand hung 11 cm short of it.)
            float longBy = Time.unscaledTime + 4f;
            while (Time.unscaledTime < longBy && (body.activeWeapon == null || body.activeWeapon != body.weapons[0]))
            {
                Pump();
                yield return null;
            }
            yield return For(0.5f);
            // (What this game's own player tells of his gun, measured against what he tells of his hands: the muzzle must be near
            // the hand that holds the gun and at a height a held gun has.)
            string GunTold()
            {
                Vector3 gunHand = (poseFlags & NetAvatar.GunInLeft) != 0 ? poseLeft : poseRight;
                float fromHand = Vector3.Distance(poseMuzzle, gunHand);
                bool sane = fromHand < 1.3f && poseMuzzle.y > 0.2f && poseMuzzle.y < 2.4f;
                Vector3 aim = poseMuzzleRot * Vector3.forward;
                return $"his gun's muzzle {fromHand * 100f:0} cm from his gun hand, {poseMuzzle.y:0.00} m above his feet, pointing {(Mathf.Asin(Mathf.Clamp(aim.y, -1f, 1f)) * Mathf.Rad2Deg):0} degrees up"
                       + (sane ? "" : " NOT WHERE A HELD GUN IS");
            }

            // 2c. He is a VR player: his hands and his gun are where he tells them (30 times a second, for two seconds).
            {
                Vector3 up = Vector3.up, toMe = -forward;
                // (He faces the player, so his right is the player's left: -side. With the gun on his left, as first written, the grip was
                // 62 cm from his right shoulder and his arm is 55 cm long.)
                Vector3 handR = up * 1.35f + toMe * 0.4f - side * 0.18f, handL = up * 1.75f + side * 0.45f; // gun hand out front, the other raised
                Vector3 muzzleAt = up * 1.4f + toMe * 0.85f - side * 0.18f;
                Quaternion muzzleLook = Quaternion.LookRotation((toMe + up * 0.35f).normalized, up); // the gun pointed up a little, at the player
                var vr = new NetWriter(128);
                int armsBefore = NetAvatar.ArmsSet;
                float until = Time.unscaledTime + 2f, nextPose = 0f;
                while (Time.unscaledTime < until)
                {
                    if (Time.unscaledTime >= nextPose)
                    {
                        nextPose = Time.unscaledTime + 1f / 30f;
                        vr.Reset();
                        NetAvatar.Put(vr, NetAvatar.HasGun | NetAvatar.LeftHand | NetAvatar.RightHand | NetAvatar.Magazine, toMe, handL, Quaternion.identity, handR, Quaternion.identity, muzzleAt, muzzleLook);
                        ghost.Send(NetGame.VrPose, vr, false);
                    }
                    Pump();
                    yield return null;
                }
                yield return new WaitForEndOfFrame();
                // (From beside him, two metres off: from the player's own eyes he stood behind a wall of barriers.)
                Vector3 chest = body.CenterPosition() + Vector3.up * 0.25f;
                (Vector3? position, Quaternion? rotation) View(Vector3 from) => (chest + from, Quaternion.LookRotation(-from, Vector3.up));
                Core.Shots.CaptureFirstClear("multiplayer-vr-player", new[]
                {
                    View((toMe + side * 0.7f).normalized * 2.2f + Vector3.up * 0.3f), View((toMe - side * 0.7f).normalized * 2.2f + Vector3.up * 0.3f),
                    View(side * 2.2f + Vector3.up * 0.3f), View(-side * 2.2f + Vector3.up * 0.3f), View(Vector3.up * 2.5f + toMe * 0.5f)
                }, 60f);
                Animator anim = body.animator;
                Transform boneL = anim != null ? anim.GetBoneTransform(HumanBodyBones.LeftHand) : null, boneR = anim != null ? anim.GetBoneTransform(HumanBodyBones.RightHand) : null;
                Vector3 at = body.Position();
                float offL = boneL != null ? Vector3.Distance(boneL.position, at + handL) : -1f, offR = boneR != null ? Vector3.Distance(boneR.position, at + handR) : -1f;
                Transform mz = body.activeWeapon != null ? NetAvatar.FirstMuzzle(body.activeWeapon) : null;
                // (The gun hand is on the gun's grip as this soldier holds this gun, wherever the told palm is: measured against the
                // hand's place on the gun at rest, carried to where the gun is now.)
                float gripOff = -1f;
                string reach = "";
                NetAvatar.Pose pose = remote.Pose;
                if (pose != null && pose.Model != null && boneR != null && pose.TargetR != null)
                {
                    gripOff = Vector3.Distance(boneR.position, pose.TargetR.position);
                    Transform upper = anim.GetBoneTransform(HumanBodyBones.RightUpperArm), lower = anim.GetBoneTransform(HumanBodyBones.RightLowerArm);
                    if (upper != null && lower != null)
                    {
                        float arm = Vector3.Distance(upper.position, lower.position) + Vector3.Distance(lower.position, boneR.position);
                        Vector3 inBody = body.transform.InverseTransformPoint(pose.TargetR.position);
                        reach = $"; the grip is {Vector3.Distance(upper.position, pose.TargetR.position) * 100f:0} cm from his shoulder, his arm is {arm * 100f:0} cm long; in his body's frame the grip is right {inBody.x * 100f:0}, up {inBody.y * 100f:0}, ahead {inBody.z * 100f:0} cm";
                    }
                }
                float gunOff = mz != null ? Vector3.Distance(mz.position, at + muzzleAt) : -1f, gunTurn = mz != null ? Quaternion.Angle(mz.rotation, muzzleLook) : -1f;
                report($"multiplayer VR player's arms and gun: his soldier's arms set {NetAvatar.ArmsSet - armsBefore} time(s){(NetAvatar.ArmsSet == armsBefore ? " NOT SET" : "")}; "
                       + $"left hand {offL * 100f:0} cm from where he holds it{(offL < 0f || offL > 0.2f ? " NOT THERE" : "")}, gun hand {gripOff * 100f:0} cm from where his soldier grips this gun ({(pose != null && pose.OwnRight != null ? "the game's own grip point on the gun" : "the hand's place on the gun at rest")}){(gripOff < 0f || gripOff > 0.08f ? " NOT ON THE GRIP" : "")}; "
                       + reach.TrimStart(';', ' ') + (reach.Length > 0 ? "; " : "") + $"his gun's muzzle {gunOff * 100f:0} cm off and {gunTurn:0} degrees off his{(gunOff < 0f || gunOff > 0.05f || gunTurn > 5f ? " NOT WHERE HE HOLDS IT" : "")}");
                GameObject box = pose != null ? pose.MagazineBox : null;
                float boxOff = box != null && box.activeSelf ? Vector3.Distance(box.transform.position, at + handL) : -1f;
                report($"multiplayer VR player's magazine in his free hand: {(boxOff >= 0f && boxOff < 0.05f ? $"shown, {boxOff * 100f:0} cm from the hand" : "NOT SHOWN")}; "
                       + $"this game's own player told to him {posesGot} time(s) in the check so far{(posesGot == 0 ? " NOT TOLD" : "")}"
                       + (posesGot > 0 ? $": hands {((poseFlags & NetAvatar.LeftHand) != 0 ? "left " : "")}{((poseFlags & NetAvatar.RightHand) != 0 ? "right " : "")}tracked, "
                                         + $"{((poseFlags & NetAvatar.HasGun) != 0 ? GunTold() : "no gun in hand")}" : ""));
                yield return For(1f);
            }

            // 2d. He throws a grenade with his hand: it flies from his soldier here.
            if (grenade.Length > 0)
            {
                int shownBefore = NetGame.ThrowsShown, flyingBefore = UnityEngine.Object.FindObjectsOfType<GrenadeProjectile>().Length;
                w.Reset();
                w.Byte(2);
                w.Vector(body.CenterPosition() + Vector3.up * 0.5f);
                w.SmallVector(side * 4f + Vector3.up * 4f); // (off to the side, away from everyone)
                ghost.Send(NetGame.Thrown, w, true);
                yield return For(0.4f);
                int flying = UnityEngine.Object.FindObjectsOfType<GrenadeProjectile>().Length;
                report($"multiplayer thrown grenade ({grenade}): {(NetGame.ThrowsShown > shownBefore && flying > flyingBefore ? "flies from his soldier here" : $"NOT SHOWN ({NetGame.ThrowsShown - shownBefore} shown, {flying - flyingBefore} more in the air)")}");
                yield return For(4f); // (it goes off: nobody of this PC's is hurt by it, its damage is his PC's to tell)
            }

            // 3. A hit on him by the player: passed to his PC, not taken here.
            float bodyHealth = body.health;
            body.Damage(new DamageInfo(DamageInfo.DamageSourceType.Projectile, player, player.activeWeapon) { healthDamage = 30f, point = body.CenterPosition(), direction = forward });
            yield return For(0.6f);
            report($"multiplayer hit on the stand-in: {(hitsGot == 1 ? $"passed to his PC ({hitDamage:0} damage)" : $"NOT PASSED ({hitsGot} message(s))")}, {othersHits} more by the host's soldiers, health here {bodyHealth:0} -> {body.health:0}{(body.health < bodyHealth - 0.1f ? " NOT LEFT TO HIS PC" : "")}");

            // 3a. Knocked over by the player (a blow, a thrown body): his PC is told; here he lies, then stands where told.
            body.KnockOver(forward * 150f);
            yield return For(0.5f);
            bool down = body.fallenOver;
            yield return For(4f);
            report($"multiplayer stand-in knocked over: {(down ? "down here" : "NOT DOWN")}, his PC {(knocksGot >= 1 ? "told" : "NOT TOLD")}, "
                   + $"back on his feet after his time: {(!body.fallenOver && !body.dead ? "yes" : $"NOT UP (fallen {body.fallenOver}, dead {body.dead}, {Vector3.Distance(body.Position(), ghostAt):0.0} m from where told, told alive {ghostAlive})")}");

            // 3b. No slow motion and no pause in a shared match.
            GameManager.SetTimeScale(0.2f);
            yield return For(0.3f);
            float afterSlow = Time.timeScale;
            GameManager.PauseGame();
            yield return For(0.3f);
            float afterPause = Time.timeScale;
            report($"multiplayer one speed for everyone: slow motion asked for: time scale {afterSlow:0.##}{(Mathf.Abs(afterSlow - 1f) > 0.01f ? " NOT REFUSED" : " (refused)")}; "
                   + $"a pause asked for: {afterPause:0.##}{(Mathf.Abs(afterPause - 1f) > 0.01f ? " NOT REFUSED" : " (refused)")}");

            // 4. A hit by him on the player: taken.
            bool wasInvulnerable = player.isInvulnerable;
            player.isInvulnerable = false;
            Core.AutoTest.PlayerCanBeHurt = true; // (the autotest's own shield off for this one hit)
            float before = player.health;
            w.Reset();
            w.UShort(0);
            w.UShort(0);
            w.Float(25f);
            w.Float(0f);
            w.Byte(0);
            w.Byte((byte)DamageInfo.DamageSourceType.Projectile);
            w.Vector(player.CenterPosition());
            w.SmallVector(-forward);
            w.SmallVector(Vector3.zero);
            ghost.SendTo(0, NetGame.Hit, w, true);
            yield return For(0.6f);
            report($"multiplayer hit by the second player: the player's health {before:0} -> {player.health:0}{(player.health > before - 20f ? " NOT TAKEN" : "")}");
            player.isInvulnerable = wasInvulnerable;
            Core.AutoTest.PlayerCanBeHurt = false;

            // 5. He dies, and comes back.
            ghostAlive = false;
            yield return For(1f);
            bool died = body.dead;
            ghostAlive = true;
            ghostAt = start;
            yield return For(1.5f);
            report($"multiplayer stand-in: told dead: {(died ? "dead" : "NOT DEAD")}; told alive again: {(!body.dead ? "back" : "NOT BACK")}");

            // 6. The host's soldiers told to him.
            int mine = ActorManager.instance.actors.Count(a => a != null && a.aiControlled && !NetGame.IsRemote(a));
            report($"multiplayer host's soldiers told to the second player: {told.Count} of {mine + 1} (the player and {mine} soldier(s)), {statesGot} message(s){(told.Count < mine + 1 ? " NOT ALL TOLD" : "")}");

            report($"multiplayer telling: {NetGame.LastTold} of {NetGame.LastOwned} soldiers' places went out in the last telling (the dead and the far ones go out less often)");

            int vehicles = ActorManager.instance.vehicles.Count(v => v != null && !v.isTurret);
            report($"multiplayer vehicles in the shared match: {vehicles} on the field ({NetGame.VehiclesRemoved} taken off){(vehicles > 0 ? " NOT TAKEN OFF" : "")}");

            // 7. He leaves: his soldier goes with him.
            ghost.Leave();
            line.Dispose();
            float goneBy = Time.unscaledTime + 3f;
            while (NetGame.RemoteCount > 0 && Time.unscaledTime < goneBy)
            {
                yield return null;
            }
            report($"multiplayer second player left: {(NetGame.RemoteCount == 0 && (body == null || body.dead) ? "his soldier went with him" : "HIS SOLDIER NOT REMOVED")}, {Multiplayer.Session.PlayerCount} in the game");
            Multiplayer.Leave();
            report($"multiplayer closed: {(Multiplayer.Active ? "NOT CLOSED" : "closed")}");
            // 8. Steam itself, as far as one account goes: a lobby nobody can see made, its data read back from Steam, the list
            // asked for, the lobby left. (Two accounts are needed for the rest: a join and the relayed messages.)
            if (SteamLobbies.Available)
            {
                Multiplayer.HostSteam(true, 4, hidden: true);
                float steamBy = Time.unscaledTime + 12f;
                while (Multiplayer.Busy && Time.unscaledTime < steamBy)
                {
                    yield return null;
                }
                bool hosting = Multiplayer.Hosting && Multiplayer.OverSteam && Multiplayer.Lobby != 0;
                yield return new WaitForSecondsRealtime(1.5f); // (the lobby's lines are written at the next update, Steam takes them at once)
                string proto = Multiplayer.LobbyData("proto"), build = Multiplayer.LobbyData("build"), mod = Multiplayer.LobbyData("mod"), name = Multiplayer.LobbyData("name");
                bool data = proto == NetSession.ProtocolVersion.ToString() && build == Multiplayer.GameBuild.ToString() && mod == PluginInfo.Version && name.Length > 0;
                report($"multiplayer over Steam: a hidden lobby {(hosting ? "made" : "NOT MADE (" + Multiplayer.Status + ")")}"
                       + (hosting ? $", its data read back from Steam: protocol {proto}, game build {build}, mod {mod}{(data ? "" : " NOT AS WRITTEN")}" : ""));
                Multiplayer.RefreshLobbies();
                steamBy = Time.unscaledTime + 12f;
                while (Multiplayer.Listing && Time.unscaledTime < steamBy)
                {
                    yield return null;
                }
                report($"multiplayer over Steam: the list of open games: {(Multiplayer.Listing ? "NOT ANSWERED in 12 s" : Multiplayer.Lobbies != null ? "answered (" + Multiplayer.ListStatus + ")" : "NOT GIVEN (" + Multiplayer.ListStatus + ")")}");
                Multiplayer.Leave();
                report($"multiplayer over Steam: the lobby {(Multiplayer.Lobby == 0 && !Multiplayer.Active ? "left" : "NOT LEFT")}");
                // Steam's own sending and receiving, with the one account there is: a reliable and an unreliable message to this
                // account itself, through the same calls a game between two players uses.
                SteamTransport steam = null;
                string steamFailed = null;
                int gotReliable = 0, gotUnreliable = 0;
                bool intact = true;
                try
                {
                    steam = new SteamTransport { Accept = id => true, AllowSelf = true };
                    var payload = new byte[300];
                    for (int i = 0; i < payload.Length; i++)
                    {
                        payload[i] = (byte)(i * 7 + 3);
                    }
                    payload[0] = 1;
                    steam.Send(steam.LocalId, payload, payload.Length, true);
                    Steamworks.EResult sentReliable = steam.LastSend;
                    payload[0] = 2;
                    steam.Send(steam.LocalId, payload, 40, false);
                    steamFailed = sentReliable != Steamworks.EResult.k_EResultOK ? "Steam answered " + sentReliable : null;
                }
                catch (Exception e)
                {
                    steamFailed = e.GetType().Name + ": " + e.Message;
                }
                if (steam != null && steamFailed == null)
                {
                    float until = Time.unscaledTime + 5f;
                    ulong self = steam.LocalId;
                    while (Time.unscaledTime < until && (gotReliable == 0 || gotUnreliable == 0))
                    {
                        steam.Poll(Time.realtimeSinceStartupAsDouble, (from, data, length) =>
                        {
                            bool good = from == self && (data[0] == 1 ? length == 300 : length == 40);
                            for (int i = 1; i < length && good; i++)
                            {
                                good = data[i] == (byte)(i * 7 + 3);
                            }
                            intact &= good;
                            if (data[0] == 1)
                            {
                                gotReliable++;
                            }
                            else
                            {
                                gotUnreliable++;
                            }
                        }, lost => { });
                        yield return null;
                    }
                }
                steam?.Dispose();
                report("multiplayer over Steam: a message to this account itself through Steam's networking: "
                       + (steamFailed != null ? "NOT SENT (" + steamFailed + ")"
                           : gotReliable == 1 && gotUnreliable <= 1 && intact ? $"the reliable one came back whole (300 bytes){(gotUnreliable == 1 ? ", the unreliable one too (40 bytes)" : "; the unreliable one did not come (it may be lost)")}"
                           : $"NOT RECEIVED RIGHT (reliable {gotReliable}, unreliable {gotUnreliable}, {(intact ? "whole" : "bytes changed")})"));
            }
            else
            {
                report("multiplayer over Steam: not checked (Steam is not running under the game)");
            }
            if (panel != null)
            {
                panel.Open(); // (not in a game: host, the list, the address)
                yield return new WaitForSecondsRealtime(1.5f);
                yield return new WaitForEndOfFrame();
                Core.Shots.Capture("multiplayer-panel-list");
                panel.Close();
            }
        }
    }
}
