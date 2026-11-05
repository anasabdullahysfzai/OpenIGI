using System.Numerics;
using OpenIGI.Core.Numerics;
using OpenIGI.Formats.Animation;
using OpenIGI.Game.Igi1.Physics;
using OpenIGI.Game.Physics;

namespace OpenIGI.Presentation.Igi1;

/// <summary>
/// The modern character as the engine's ragdoll sees it (<c>g_modern_ragdoll</c>): the Mixamo bones made a skeleton in the old rig's
/// terms -- engine units, x his right, y ahead, z up, every bone's rest frame those axes -- so the human rig's joint table holds as it
/// stands. A pose goes in as each bone's turn away from rest, and comes back the same way. <b>Not retail.</b>
/// </summary>
internal sealed class ModernRagdoll
{
    // Which Mixamo bone plays each part of the human rig. The bones between (Spine1, Neck, fingers, toes) ride on these.
    private static readonly (int Human, string Name)[] Roles =
    [
        (HumanRagdollProfile.Pelvis, "Hips"), (HumanRagdollProfile.Spine, "Spine"), (HumanRagdollProfile.Chest, "Spine2"), (HumanRagdollProfile.Head, "Head"),
        (HumanRagdollProfile.LeftUpperArm, "LeftArm"), (HumanRagdollProfile.LeftForearm, "LeftForeArm"), (HumanRagdollProfile.LeftHand, "LeftHand"),
        (HumanRagdollProfile.RightUpperArm, "RightArm"), (HumanRagdollProfile.RightForearm, "RightForeArm"), (HumanRagdollProfile.RightHand, "RightHand"),
        (HumanRagdollProfile.LeftThigh, "LeftUpLeg"), (HumanRagdollProfile.LeftShin, "LeftLeg"), (HumanRagdollProfile.LeftFoot, "LeftFoot"),
        (HumanRagdollProfile.RightThigh, "RightUpLeg"), (HumanRagdollProfile.RightShin, "RightLeg"), (HumanRagdollProfile.RightFoot, "RightFoot"),
    ];

    private readonly int[] _nodeOfBone;
    private readonly int[] _parents;
    private readonly Quaternion[] _restTurn;
    private readonly Vector3[] _restScale;
    private readonly double _unitsPerMetre;

    private ModernRagdoll(int[] nodeOfBone, int[] parents, Quaternion[] restTurn, Vector3[] restScale, RagdollSkeleton rig, double unitsPerMetre)
    {
        (_nodeOfBone, _parents, _restTurn, _restScale, Rig, _unitsPerMetre) = (nodeOfBone, parents, restTurn, restScale, rig, unitsPerMetre);
    }

    /// <summary>Gets the rig the solver runs.</summary>
    public RagdollSkeleton Rig { get; }

    /// <summary>Makes the rig from a character.</summary>
    /// <param name="asset">The character.</param>
    /// <param name="unitsPerMetre">The engine's units to a metre.</param>
    /// <returns>The rig, or null for a character without the Mixamo bones.</returns>
    public static ModernRagdoll? Build(ModernBodyAsset asset, double unitsPerMetre)
    {
        int Node(string name) => Array.IndexOf(asset.Names, "mixamorig:" + name);
        int hips = Node("Hips");
        if (hips < 0 || Roles.Any(role => Node(role.Name) < 0))
        {
            return null;
        }

        // The rest skeleton, in glTF's world, in metres.
        Matrix4x4[] rest = new Matrix4x4[asset.Rest.Length];
        bool[] done = new bool[rest.Length];
        Matrix4x4 RestOf(int node)
        {
            if (!done[node])
            {
                (Vector3 t, Quaternion r, Vector3 s) = asset.Rest[node];
                Matrix4x4 local = Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(r) * Matrix4x4.CreateTranslation(t);
                rest[node] = asset.Parents[node] >= 0 ? local * RestOf(asset.Parents[node]) : local;
                done[node] = true;
            }

            return rest[node];
        }

        // The hips and all below them, each after its parent. Not the rifle's bone: a fallen body's rifle is not drawn, the one he dropped is.
        List<int> nodes = [];
        void Walk(int node)
        {
            nodes.Add(node);
            for (int child = 0; child < asset.Parents.Length; child++)
            {
                if (asset.Parents[child] == node && child != asset.Weapon)
                {
                    Walk(child);
                }
            }
        }

        Walk(hips);
        int[] boneOfNode = new int[rest.Length];
        Array.Fill(boneOfNode, -1);
        for (int bone = 0; bone < nodes.Count; bone++)
        {
            boneOfNode[nodes[bone]] = bone;
        }

        int count = nodes.Count;
        (int[] parents, Vector3f[] offsets, Quaternion[] turns, Vector3[] scales) = (new int[count], new Vector3f[count], new Quaternion[count], new Vector3[count]);
        for (int bone = 0; bone < count; bone++)
        {
            int node = nodes[bone];
            parents[bone] = bone == 0 ? BoneSkeleton.NoParent : boneOfNode[asset.Parents[node]];
            Matrix4x4.Decompose(RestOf(node), out scales[bone], out turns[bone], out Vector3 at);
            Vector3 from = bone == 0 ? Vector3.Zero : RestOf(asset.Parents[node]).Translation;
            Vector3 offset = ModernBodyAsset.ToBody(at - from, 0f) * (float)unitsPerMetre;
            offsets[bone] = new Vector3f(offset.X, offset.Y, offset.Z);
        }

        // His skin in the rest pose, each point with the bone that has most of it, to measure the bodies from. Not the rifle's.
        HashSet<int> gun = [.. asset.GunVertices];
        List<(int Bone, Vector3f At)> skin = [];
        for (int v = 0; v < asset.Vertices.Length; v++)
        {
            (Vector4 joints, Vector4 weights) = asset.Weights[v];
            int most = 0;
            for (int k = 1; k < 4; k++)
            {
                most = weights[k] > weights[most] ? k : most;
            }

            (int node, Matrix4x4 inverseBind) = asset.Joints[(int)joints[most]];
            if (!gun.Contains(v) && boneOfNode[node] >= 0)
            {
                Vector3 at = ModernBodyAsset.ToBody(Vector3.Transform(asset.Vertices[v].Position, inverseBind * RestOf(node)), 0f) * (float)unitsPerMetre;
                skin.Add((boneOfNode[node], new Vector3f(at.X, at.Y, at.Z)));
            }
        }

        Dictionary<int, int> boneOfRole = Roles.ToDictionary(role => role.Human, role => boneOfNode[Node(role.Name)]);
        RagdollProfile profile = HumanRagdollProfile.Remapped(boneOfRole, [.. nodes.Select(n => asset.Names[n])]);
        RagdollSkeleton rig = RagdollSkeleton.Build(BoneSkeleton.Create(parents, offsets), profile: profile, measured: skin);
        return new ModernRagdoll([.. nodes], parents, turns, scales, rig, unitsPerMetre);
    }

    /// <summary>Starts a corpse from the last two ticks of a body's skeleton, going as fast as they were: no jump, and no sudden stop.</summary>
    /// <param name="world">The skeleton now, in glTF's world.</param>
    /// <param name="before">The skeleton a tick ago.</param>
    /// <param name="lift">How far the body is raised now, in metres.</param>
    /// <param name="liftBefore">How far a tick ago.</param>
    /// <param name="where">Where the soldier is now, in engine units.</param>
    /// <param name="whereBefore">Where a tick ago.</param>
    /// <param name="orientation">How he is turned.</param>
    /// <returns>The corpse.</returns>
    public RagdollBody Seed(Matrix4x4[] world, Matrix4x4[] before, float lift, float liftBefore, Vector3d where, Vector3d whereBefore, Matrix3f orientation)
    {
        BonePose now = PoseOf(world, lift), was = PoseOf(before, liftBefore);
        RagdollBody corpse = RagdollBody.Seed(Rig, now, where, orientation);
        Vector3d moved = where - whereBefore;
        for (int body = 0; body < Rig.BodyCount; body++)
        {
            int bone = Rig.BoneOf(body);
            Vector3f step = RagdollSolver.Rotate(orientation, now.Positions[bone] - was.Positions[bone]);
            corpse.AddVelocityOf(body, moved + new Vector3d(step.X, step.Y, step.Z));
        }

        return corpse;
    }

    /// <summary>A skeleton as the solver wants a pose: each bone's turn away from rest, and its place, in the soldier's frame.</summary>
    /// <param name="world">The skeleton, in glTF's world.</param>
    /// <param name="lift">How far the body is raised, in metres.</param>
    /// <returns>The pose.</returns>
    internal BonePose PoseOf(Matrix4x4[] world, float lift)
    {
        int count = _nodeOfBone.Length;
        (Matrix3f[] turns, Vector3f[] places) = (new Matrix3f[count], new Vector3f[count]);
        for (int bone = 0; bone < count; bone++)
        {
            Matrix4x4.Decompose(world[_nodeOfBone[bone]], out _, out Quaternion turn, out Vector3 at);
            Quaternion away = Quaternion.Normalize(turn * Quaternion.Inverse(_restTurn[bone]));
            turns[bone] = BonePose.ToMatrix(Swap(away));
            Vector3 place = (ModernBodyAsset.ToBody(at, 0f) + new Vector3(0f, 0f, lift)) * (float)_unitsPerMetre;
            places[bone] = new Vector3f(place.X, place.Y, place.Z);
        }

        return BonePose.FromBones(turns, places, _parents);
    }

    /// <summary>Puts a corpse's bones on a body's skeleton, relative to where the soldier is drawn, with no lift.</summary>
    /// <param name="corpse">The corpse.</param>
    /// <param name="where">Where the soldier is now, in engine units.</param>
    /// <param name="orientation">How he is turned.</param>
    /// <param name="world">The skeleton, in glTF's world.</param>
    public void Write(RagdollBody corpse, Vector3d where, Matrix3f orientation, Matrix4x4[] world)
    {
        Matrix3f back = orientation.Transposed();
        Quaternion32 backTurn = RagdollPose.QuaternionOf(back).Normalized();
        for (int bone = 0; bone < _nodeOfBone.Length; bone++)
        {
            Quaternion32 turn = (backTurn * corpse.OrientationOf(bone)).Normalized();
            Quaternion turned = Quaternion.Normalize(Swap(turn) * _restTurn[bone]);
            Vector3d at = RagdollSolver.Rotate(back, corpse.PositionOf(bone) - where) / _unitsPerMetre;
            world[_nodeOfBone[bone]] = Matrix4x4.CreateScale(_restScale[bone]) * Matrix4x4.CreateFromQuaternion(turned)
                * Matrix4x4.CreateTranslation(ModernBodyAsset.FromBody(new Vector3((float)at.X, (float)at.Y, (float)at.Z)));
        }
    }

    // A turn between glTF's axes (x his left, y up, z ahead) and the soldier's (x right, y ahead, z up): the swap is its own inverse.
    private static Quaternion32 Swap(Quaternion q) => new(-q.X, q.Z, q.Y, q.W);

    private static Quaternion Swap(Quaternion32 q) => new(-q.X, q.Z, q.Y, q.W);
}
