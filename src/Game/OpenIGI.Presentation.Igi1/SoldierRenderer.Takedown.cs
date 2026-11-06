using OpenIGI.Core.Graphics;
using OpenIGI.Core.Numerics;
using OpenIGI.Game;
using OpenIGI.Game.Igi1.Ai;
using OpenIGI.Game.Igi1.Player;
using OpenIGI.Game.Player;
using OpenIGI.Game.World;
using Quaternion = System.Numerics.Quaternion;
using Matrix4x4 = System.Numerics.Matrix4x4;
using Vector3 = System.Numerics.Vector3;

namespace OpenIGI.Presentation.Igi1;

/// <content>
/// The knife takedown's bodies: the victim's own and one made for the attacker, each on its clip of a pair that share the victim's origin,
/// and the eye in the attacker's head. <b>Not retail.</b>
/// </content>
public sealed partial class SoldierRenderer
{
    // How long the view takes to go into the attacker's head and back to the player's own, in ticks.
    private const float TakedownEyeInTicks = 9f, TakedownEyeOutTicks = 9f;

    // The wound, in glTF's world at the clip's first frame, from the neck bone (takedown v3).
    private static readonly Vector3 TakedownWound = new(-0.07f, 0.03f, -0.03f);

    // The ticks the wound bleeds on: as the blade goes in, as it comes out, and fading as he goes limp.
    private static readonly int[] TakedownBleeds = [41, 44, 47, 48, 49, 50, 52, 54, 56, 58, 60, 62, 65];

    // The game's blood holds ten drops a body, too few for a cut throat: each burst throws several bodies' worth, each from one of these.
    private static readonly object[] TakedownBloodOwners = [new(), new(), new(), new(), new(), new()];

    // How far the wound's soak spreads, in metres: on him as he bleeds out, and on the attacker's hand and blade at once.
    private const float SoakStartMetres = 0.014f, SoakEndMetres = 0.11f, HandSoakMetres = 0.24f;
    private const int SoakFromTick = 41, SoakEndTick = 90, HandSoakTicks = 8;

    private void TickTakedown(SoldierGroup group, SoldierMember member, ModernBodyAsset asset, SoldierView view, TakedownRun run)
    {
        float yaw = GameplayCVars.ModernBodyYaw.Value * MathF.PI / 180f;
        member.Modern ??= NewTakedownBody(group, asset);
        ModernBody body = member.Modern;
        if (member.TakedownPartner is null)
        {
            _modernFallHeights[member] = _modernStandingHeights.GetValueOrDefault(member, member.TickWhere.Z);
            member.TakenDown = true;
            body.Fall(asset.TakedownVictim!);
            ModernBody partner = NewTakedownBody(group, asset);
            partner.HideHead();
            partner.ShowsKnife = true;
            partner.Fall(asset.TakedownAttacker!, blend: false);
            member.TakedownPartner = partner;
        }

        ModernBody attacker = member.TakedownPartner;
        float raise = (float)(_modernFallHeights[member] - member.TickWhere.Z);
        body.Tick(0f, yaw, raise);
        attacker.Tick(0f, yaw, raise);

        if (run.End is null)
        {
            (Vector3 hips, Vector3 ahead) = attacker.EndOf(asset.TakedownAttacker!, yaw);
            Vector3d feet = BodyToWorld(member, new Vector3(hips.X, hips.Y, 0f));
            Vector3d look = BodyDirection(member, ahead);
            float gamma = -MathF.Atan2((float)look.X, (float)look.Y);
            run.End = (new Vector3d(feet.X, feet.Y, _modernFallHeights[member]) * WorldScene.UnitsPerMetre, gamma);
            run.Eye = alpha => TakedownEye(member, attacker, run, alpha);
        }

        (Vector3 point, Vector3 way) = body.OnNeck(asset.TakedownVictim!, TakedownWound, yaw);
        if (run.Tick == SoakFromTick)
        {
            body.BeginSoak(point, runsDown: 0.55f);
            attacker.BeginSoak(attacker.PalmsForTesting.Right, runsDown: 0f);
        }

        if (run.Tick >= SoakFromTick)
        {
            float bled = Math.Clamp((run.Tick - SoakFromTick) / (float)(SoakEndTick - SoakFromTick), 0f, 1f);
            body.SoakMetres = SoakStartMetres + ((SoakEndMetres - SoakStartMetres) * MathF.Pow(bled, 0.7f));
            attacker.SoakMetres = HandSoakMetres * Math.Clamp((run.Tick - SoakFromTick) / (float)HandSoakTicks, 0f, 1f);
        }

        if (Array.IndexOf(TakedownBleeds, run.Tick) >= 0)
        {
            Vector3d going = BodyDirection(member, way), wound = BodyToWorld(member, point) * WorldScene.UnitsPerMetre;
            Vector3f along = new((float)going.X, (float)going.Y, (float)going.Z);
            int bursts = run.Tick is >= 47 and <= 50 ? TakedownBloodOwners.Length : run.Tick < 58 ? 3 : 1;
            for (int i = 0; i < bursts; i++)
            {
                _sim.BloodSpray?.Invoke(TakedownBloodOwners[(run.Tick + i) % TakedownBloodOwners.Length], wound, along);
            }
        }
    }

    private ModernBody NewTakedownBody(SoldierGroup group, ModernBodyAsset asset)
    {
        ModernBody made = new(asset, _device);
        made.MatchLowest(group.Body.LowestVertexZ);
        return made;
    }

    // A point of the soldier's frame in the world, metres: x his right, y ahead, z up, as the drawn turn has them.
    private static Vector3d BodyToWorld(SoldierMember member, Vector3 p) => member.DrawnWhere + BodyDirection(member, p);

    private static Vector3d BodyDirection(SoldierMember member, Vector3 v)
    {
        Matrix3f m = member.DrawnOrientation;
        return new Vector3d(
            (m.M11 * v.X) + (m.M12 * v.Y) + (m.M13 * v.Z),
            (m.M21 * v.X) + (m.M22 * v.Y) + (m.M23 * v.Z),
            (m.M31 * v.X) + (m.M32 * v.Y) + (m.M33 * v.Z));
    }

    private static Camera3D TakedownEye(SoldierMember member, ModernBody attacker, TakedownRun run, float alpha)
    {
        float watch = GameplayCVars.TakedownWatch.Value;
        if (watch != 0f)
        {
            Vector3d middle = BodyToWorld(member, new Vector3(0f, -0.2f, 1.0f));
            Vector3d from = BodyToWorld(member, new Vector3(watch, -0.2f, 1.25f));
            return new Camera3D(from, Rows(Vec(middle - from), new Vector3f(0f, 0f, 1f)));
        }

        (Vector3 at, Vector3 ahead, Vector3 up) = attacker.HeadViewAt(alpha);
        Vector3d f = BodyDirection(member, ahead), u = BodyDirection(member, up);
        Camera3D clip = new(BodyToWorld(member, at), Rows(Vec(f), Vec(u)));

        // In from the player's own eye, and back out to where he ends up, level.
        float t = run.Tick - 1f + alpha;
        Camera3D eye = Between(run.StartEye, clip, Ease(t / TakedownEyeInTicks));
        if (run.End is ({ } feet, float gamma))
        {
            Camera3D end = new(
                new Vector3d(feet.X, feet.Y, feet.Z + PlayerBody.StandingEyeHeight) / WorldScene.UnitsPerMetre,
                Camera3D.LookRotation(-gamma, TakedownRun.EndPitch));
            eye = Between(eye, end, Ease((t - (TakedownRun.Ticks - TakedownEyeOutTicks)) / TakedownEyeOutTicks));
        }

        return eye;
    }

    private static float Ease(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - (2f * t));
    }

    private static Vector3f Vec(Vector3d v) => new((float)v.X, (float)v.Y, (float)v.Z);

    // The world-to-view rotation looking along a direction, up as near the one given as it can be: rows right, up, forward.
    private static Matrix3f Rows(Vector3f forward, Vector3f up)
    {
        Vector3 f = Vector3.Normalize(new Vector3(forward.X, forward.Y, forward.Z)), u = new(up.X, up.Y, up.Z);
        Vector3 r = Vector3.Normalize(Vector3.Cross(f, u));
        u = Vector3.Cross(r, f);
        return Matrix3f.FromRows(new Vector3f(r.X, r.Y, r.Z), new Vector3f(u.X, u.Y, u.Z), new Vector3f(f.X, f.Y, f.Z));
    }

    private static Camera3D Between(in Camera3D a, in Camera3D b, float t)
    {
        if (t <= 0f)
        {
            return a;
        }

        if (t >= 1f)
        {
            return b;
        }

        // The view's rows (right, up, forward) are left-handed: with forward turned back they are a rotation, which is what a quaternion holds.
        static Quaternion Q(Matrix3f m) => Quaternion.CreateFromRotationMatrix(new Matrix4x4(m.M11, m.M12, m.M13, 0f, m.M21, m.M22, m.M23, 0f, -m.M31, -m.M32, -m.M33, 0f, 0f, 0f, 0f, 1f));
        Matrix4x4 r = Matrix4x4.CreateFromQuaternion(Quaternion.Slerp(Q(a.Orientation), Q(b.Orientation), t));
        return new Camera3D(
            a.Position + ((b.Position - a.Position) * t),
            Matrix3f.FromRows(new Vector3f(r.M11, r.M12, r.M13), new Vector3f(r.M21, r.M22, r.M23), new Vector3f(-r.M31, -r.M32, -r.M33)));
    }
}
