using System.Numerics;
using OpenIGI.Core.Graphics;
using OpenIGI.Core.Numerics;
using OpenIGI.Formats.Textures;
using OpenIGI.Rendering;
using SharpGLTF.Animations;
using SharpGLTF.Schema2;
using StbImageSharp;
using GltfAnimation = SharpGLTF.Schema2.Animation;

namespace OpenIGI.Presentation.Igi1;

/// <summary>
/// A glTF character drawn in a soldier's place (<see cref="Game.GameplayCVars.ModernBodies"/>): one skinned mesh, its colour
/// textures, its skeleton, and two clips named <c>idle</c> and <c>walk</c>. Loaded once and shared by every soldier wearing it;
/// each soldier poses and skins it into its own mesh (<see cref="ModernBody"/>). <b>Not retail.</b>
/// </summary>
/// <remarks>
/// <para>
/// Only what is drawn changes. The soldier's own skeleton, clips, hit zones, eye and ragdoll go on as they do, so nothing the
/// simulation reads moves; the character is chosen idle or walking, and the walk's pace, from how the soldier is moving.
/// </para>
/// <para>
/// glTF stands up along +Y in metres and its characters face +Z; a soldier's mesh stands up along +Z in metres and faces +Y.
/// <see cref="ToBody"/> turns one into the other.
/// </para>
/// </remarks>
internal sealed class ModernBodyAsset : IDisposable
{
    private readonly IGraphicsDevice _device;
    private readonly List<TextureHandle> _details = [];

    private ModernBodyAsset(IGraphicsDevice device) => _device = device;

    /// <summary>Gets each node's parent, or -1, in an order where a parent always comes before its children.</summary>
    public int[] Parents { get; private set; } = [];

    /// <summary>Gets each node's name, in the same order.</summary>
    public string[] Names { get; private set; } = [];

    /// <summary>Gets each node's rest translation, rotation and scale.</summary>
    public (Vector3 T, Quaternion R, Vector3 S)[] Rest { get; private set; } = [];

    /// <summary>Gets the node each joint of the skin is, and its inverse bind matrix.</summary>
    public (int Node, Matrix4x4 InverseBind)[] Joints { get; private set; } = [];

    /// <summary>Gets the bind-pose positions, normals and texture coordinates, glTF's frame.</summary>
    public (Vector3 Position, Vector3 Normal, Vector2 Uv)[] Vertices { get; private set; } = [];

    /// <summary>Gets each vertex's four joints and weights.</summary>
    public (Vector4 Joints, Vector4 Weights)[] Weights { get; private set; } = [];

    /// <summary>Gets the triangles, every primitive's back to back.</summary>
    public ushort[] Indices { get; private set; } = [];

    /// <summary>Gets each primitive's run of <see cref="Indices"/>, its colour texture, and whether it is the gun he holds (a material named <c>Rifle_...</c>).</summary>
    public (int IndexOffset, int IndexCount, TextureHandle Diffuse, bool Gun)[] Subsets { get; private set; } = [];

    /// <summary>Gets a value indicating whether the character's own normal, roughness and metalness came with it, to be drawn with
    /// (<see cref="SurfaceMaterial.AuthoredDetail"/>).</summary>
    public bool HasAuthoredDetail => _details.Count > 0;

    /// <summary>Gets the lowest point of the bind pose, along glTF's up, which the soldier's lowest point is matched to.</summary>
    public float LowestY { get; private set; }

    /// <summary>
    /// Where a round in each of the original's hit zones (<c>HumanDamage.ZoneName</c>) kicks the character: the bones it turns
    /// and how far at most, in degrees, the struck bone most and the ones above it less.
    /// </summary>
    private static readonly Dictionary<int, (string Bone, float Degrees)[]> KickBones = new()
    {
        [0] = [("RightFoot", 18f), ("RightLeg", 8f)],
        [1] = [("RightLeg", 22f), ("RightUpLeg", 8f), ("Hips", 3f)],
        [2] = [("RightUpLeg", 18f), ("Hips", 5f), ("Spine", 3f)],
        [3] = [("RightHand", 30f), ("RightForeArm", 12f)],
        [4] = [("RightForeArm", 22f), ("RightArm", 10f), ("Spine2", 3f)],
        [5] = [("RightArm", 22f), ("RightShoulder", 10f), ("Spine2", 5f)],
        [6] = [("LeftFoot", 18f), ("LeftLeg", 8f)],
        [7] = [("LeftLeg", 22f), ("LeftUpLeg", 8f), ("Hips", 3f)],
        [8] = [("LeftUpLeg", 18f), ("Hips", 5f), ("Spine", 3f)],
        [9] = [("LeftHand", 30f), ("LeftForeArm", 12f)],
        [10] = [("LeftForeArm", 22f), ("LeftArm", 10f), ("Spine2", 3f)],
        [11] = [("LeftArm", 22f), ("LeftShoulder", 10f), ("Spine2", 5f)],
        [12] = [("Spine2", 10f), ("Spine1", 8f), ("Spine", 5f), ("Head", 6f)],
        [13] = [("Spine2", 10f), ("Spine1", 8f), ("Spine", 5f), ("Head", 6f)],
        [14] = [("Spine2", 10f), ("Spine1", 8f), ("Spine", 5f), ("Head", 6f)],
        [15] = [("Head", 28f), ("Neck", 10f), ("Spine2", 4f)],
        [16] = [("Neck", 20f), ("Head", 12f), ("Spine2", 4f)],
    };

    /// <summary>Gets the nodes a round in a zone kicks and how far each turns at most, in radians; none for a zone it has none for.</summary>
    /// <param name="zone">The original's hit zone.</param>
    /// <returns>The nodes and their reach.</returns>
    public IEnumerable<(int Node, float Radians)> KickOf(int zone) =>
        (KickBones.TryGetValue(zone, out (string Bone, float Degrees)[]? bones) ? bones : [])
            .Select(b => (Node: Array.IndexOf(Names, "mixamorig:" + b.Bone), Radians: b.Degrees * MathF.PI / 180f))
            .Where(b => b.Node >= 0);

    /// <summary>Gets a node's first child, which the bone runs to, or -1.</summary>
    /// <param name="node">The node.</param>
    /// <returns>The child.</returns>
    public int ChildOf(int node) => Array.IndexOf(Parents, node);

    /// <summary>Turns a direction of the soldier's frame into glTF's, the inverse of <see cref="ToBody"/> without its yaw.</summary>
    /// <param name="v">The direction.</param>
    /// <returns>It in glTF's frame.</returns>
    public static Vector3 FromBody(Vector3 v) => new(-v.X, v.Z, v.Y);

    /// <summary>Gets the rifle's vertices, which the hands place (<see cref="ModernBody"/>) rather than the skin; none without one.</summary>
    public int[] GunVertices { get; private set; } = [];

    /// <summary>Gets the nodes either palm is measured between: each hand's wrist and middle knuckle, or -1.</summary>
    public (int Wrist, int Knuckle) RightPalm => (Array.IndexOf(Names, "mixamorig:RightHand"), Array.IndexOf(Names, "mixamorig:RightHandMiddle1"));

    /// <summary>Gets the left arm's three joints, shoulder to wrist, or -1 each: what the left hand is reached with.</summary>
    public (int Shoulder, int Elbow, int Wrist) LeftArm =>
        (Array.IndexOf(Names, "mixamorig:LeftArm"), Array.IndexOf(Names, "mixamorig:LeftForeArm"), Array.IndexOf(Names, "mixamorig:LeftHand"));

    /// <summary>Gets a node and every node below it.</summary>
    /// <param name="node">The node.</param>
    /// <returns>The nodes, parents before children.</returns>
    public int[] Below(int node)
    {
        if (_below.TryGetValue(node, out int[]? known))
        {
            return known;
        }

        List<int> nodes = [node];
        for (int n = node + 1; n < Parents.Length; n++)
        {
            if (nodes.Contains(Parents[n]))
            {
                nodes.Add(n);
            }
        }

        return _below[node] = [.. nodes];
    }

    private readonly Dictionary<int, int[]> _below = [];

    /// <summary>Gets the skin's joint each node is, or -1 for a node the skin does not use.</summary>
    public int[] JointOfNode => _jointOfNode ??= BuildJointOfNode();

    private int[]? _jointOfNode;

    private int[] BuildJointOfNode()
    {
        int[] joints = [.. Enumerable.Repeat(-1, Rest.Length)];
        for (int j = 0; j < Joints.Length; j++)
        {
            joints[Joints[j].Node] = j;
        }

        return joints;
    }

    /// <summary>Gets the left palm's nodes, as <see cref="RightPalm"/>.</summary>
    public (int Wrist, int Knuckle) LeftPalm => (Array.IndexOf(Names, "mixamorig:LeftHand"), Array.IndexOf(Names, "mixamorig:LeftHandMiddle1"));

    /// <summary>Gets the vertices at the rifle's muzzle and at the end of its stock, or -1 each where it has no rifle.</summary>
    public (int Muzzle, int Stock) GunEnds { get; private set; } = (-1, -1);

    // The rifle's two ends: its longest run, the thin end the muzzle and the tall one the stock.
    private (int Muzzle, int Stock) FindGunEnds()
    {
        int[] gun = [.. Subsets.Where(s => s.Gun).SelectMany(s => Indices.Skip(s.IndexOffset).Take(s.IndexCount)).Select(i => (int)i).Distinct()];
        if (gun.Length < 8)
        {
            return (-1, -1);
        }

        Vector3 low = new(float.MaxValue), high = new(float.MinValue);
        foreach (int i in gun)
        {
            (low, high) = (Vector3.Min(low, Vertices[i].Position), Vector3.Max(high, Vertices[i].Position));
        }

        Vector3 size = high - low;
        Vector3 along = size.X >= size.Y && size.X >= size.Z ? Vector3.UnitX : size.Y >= size.Z ? Vector3.UnitY : Vector3.UnitZ;
        float span = Vector3.Dot(size, along);
        int first = gun.MinBy(i => Vector3.Dot(Vertices[i].Position, along));
        int last = gun.MaxBy(i => Vector3.Dot(Vertices[i].Position, along));

        // How thick the rifle is within a tenth of its length of each end.
        float Thickness(int end)
        {
            float at = Vector3.Dot(Vertices[end].Position, along);
            Vector3[] near = [.. gun.Select(i => Vertices[i].Position).Where(p => Math.Abs(Vector3.Dot(p, along) - at) < span * 0.1f)];
            Vector3 extent = near.Aggregate(new Vector3(float.MinValue), Vector3.Max) - near.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
            return (extent - (along * Vector3.Dot(extent, along))).Length();
        }

        return Thickness(first) < Thickness(last) ? (first, last) : (last, first);
    }

    /// <summary>Gets the standing clip.</summary>
    public ModernClip Idle { get; private set; } = ModernClip.None;

    /// <summary>Gets the walking clip, travel taken out.</summary>
    public ModernClip Walk { get; private set; } = ModernClip.None;

    /// <summary>Gets the standing clip with the rifle up, for a fight (<c>aim_idle</c>; the idle where the file has none).</summary>
    public ModernClip AimIdle { get; private set; } = ModernClip.None;

    /// <summary>Gets the standing clip firing (<c>fire</c>; the aim where the file has none).</summary>
    public ModernClip Fire { get; private set; } = ModernClip.None;

    /// <summary>Gets the walk with the rifle up (<c>aim_walk</c>; the walk where the file has none).</summary>
    public ModernClip AimWalk { get; private set; } = ModernClip.None;

    /// <summary>Gets the run (<c>run</c>; the aimed walk where the file has none).</summary>
    public ModernClip Run { get; private set; } = ModernClip.None;

    /// <summary>Gets the falls, by shot from behind then headshot, each null where the file has none.</summary>
    /// <remarks>Named <c>death_front</c>, <c>death_back</c>, <c>death_front_head</c> and <c>death_back_head</c>; played once and held.</remarks>
    public ModernClip?[,] Deaths { get; private set; } = new ModernClip?[2, 2];

    /// <summary>Gets the flinches, each null where the file has none: <c>hit_front</c>, <c>hit_back</c>, <c>hit_left</c>, <c>hit_right</c>.</summary>
    public (ModernClip? Front, ModernClip? Back, ModernClip? Left, ModernClip? Right) Hits { get; private set; }

    /// <summary>Gets the flinch for a round, by where its shooter was: behind, to one side, else ahead.</summary>
    /// <param name="ahead">How far ahead the shooter was, along the ground, unit length with <paramref name="right"/>.</param>
    /// <param name="right">How far to the right.</param>
    /// <returns>The flinch, or null where the file has none.</returns>
    public ModernClip? Hit(float ahead, float right) => Hit(ahead, right, zone: -1, heavy: false);

    /// <summary>Gets the reactions read from the file (<c>react_...</c>), by name.</summary>
    public Dictionary<string, ModernClip> Reacts { get; } = [];

    /// <summary>Gets, for each node, whether it is an arm's (a shoulder or below), which a layered reaction leaves to the rifle.</summary>
    public bool[] ArmNodes { get; private set; } = [];

    /// <summary>
    /// Gets the reaction for a round: a headshot's for the head or neck; the gut's for a heavy one in the body from ahead;
    /// otherwise the one from the side the shooter was on, the big one when the round was heavy. Falls back to the rifle
    /// flinches (<see cref="Hits"/>) where the file has no reactions.
    /// </summary>
    /// <param name="ahead">How far ahead the shooter was, along the ground, unit length with <paramref name="right"/>.</param>
    /// <param name="right">How far to the right.</param>
    /// <param name="zone">The original's hit zone, or -1 for any.</param>
    /// <param name="heavy">Whether the round was a heavy one (it came hard on the heels of another).</param>
    /// <returns>The reaction, or null where the file has none.</returns>
    public ModernClip? Hit(float ahead, float right, int zone, bool heavy)
    {
        string side = ahead < -0.5f ? "back" : Math.Abs(right) > 0.7f ? (right > 0f ? "right" : "left") : "front";
        string? name = zone is 15 or 16 ? "react_head"
            : zone is >= 12 and <= 14 && heavy && side == "front" ? "react_gut"
            : zone >= 0 ? (heavy ? $"react_{side}_big" : $"react_{side}") : null;
        if (name is not null && (Reacts.GetValueOrDefault(name) ?? Reacts.GetValueOrDefault($"react_{side}")) is { } react)
        {
            return react;
        }

        return HitOld(ahead, right);
    }

    // A node under either shoulder.
    private bool IsArm(int node)
    {
        for (int at = node; at >= 0; at = Parents[at])
        {
            if (Names[at] is "mixamorig:LeftShoulder" or "mixamorig:RightShoulder")
            {
                return true;
            }
        }

        return false;
    }

    private ModernClip? HitOld(float ahead, float right)
    {
        ModernClip? side = right > 0f ? Hits.Right : Hits.Left;
        return ahead < -0.5f && Hits.Back is { } back ? back
            : Math.Abs(right) > 0.7f && side is not null ? side
            : Hits.Front;
    }

    /// <summary>Gets the fall for a killing shot: its own if the file has it, else the plain one from that side, else any.</summary>
    /// <param name="fromBehind">Whether the round came from behind.</param>
    /// <param name="headshot">Whether it struck the head or neck.</param>
    /// <returns>The fall, or null where the file has none.</returns>
    public ModernClip? Death(bool fromBehind, bool headshot)
    {
        int side = fromBehind ? 1 : 0, head = headshot ? 1 : 0;
        return Deaths[side, head] ?? Deaths[side, 0] ?? Deaths[1 - side, 0];
    }

    /// <summary>Reads a character, uploading its textures.</summary>
    /// <param name="device">The device its textures live on.</param>
    /// <param name="path">The <c>.glb</c> file.</param>
    /// <returns>The character.</returns>
    /// <exception cref="InvalidDataException">The file holds no skinned mesh, or no <c>idle</c> and <c>walk</c> clips.</exception>
    public static ModernBodyAsset Load(IGraphicsDevice device, string path)
    {
        ArgumentNullException.ThrowIfNull(device);
        ModelRoot model = ModelRoot.Load(path);
        Node skinned = model.LogicalNodes.FirstOrDefault(n => n.Skin is not null && n.Mesh is not null)
            ?? throw new InvalidDataException($"{path} holds no skinned mesh.");

        ModernBodyAsset asset = new(device);
        Dictionary<Node, int> index = asset.ReadNodes(model);
        asset.Joints = [.. Enumerable.Range(0, skinned.Skin.JointsCount)
            .Select(i => skinned.Skin.GetJoint(i))
            .Select(j => (index[j.Joint], j.InverseBindMatrix))];
        asset.ReadMesh(skinned.Mesh);
        asset.Idle = ModernClip.Read(Find(model, "idle", path), index, asset.Rest.Length, travelling: false);
        asset.Walk = ModernClip.Read(Find(model, "walk", path), index, asset.Rest.Length, travelling: true);
        ModernClip Optional(string name, bool travelling, ModernClip otherwise) =>
            model.LogicalAnimations.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) is { } clip
                ? ModernClip.Read(clip, index, asset.Rest.Length, travelling)
                : otherwise;
        asset.AimIdle = Optional("aim_idle", travelling: false, asset.Idle);
        asset.Fire = Optional("fire", travelling: false, asset.AimIdle);
        asset.AimWalk = Optional("aim_walk", travelling: true, asset.Walk);
        asset.Run = Optional("run", travelling: true, asset.AimWalk);
        foreach (ModernClip moving in new[] { asset.Walk, asset.AimWalk, asset.Run })
        {
            moving.MetresPerSecond = ModernBody.WalkSpeed(asset, moving);
        }

        asset.WalkMetresPerSecond = asset.Walk.MetresPerSecond;

        // The fall's root keeps its drop but not its slide: the soldier's own death clip already moves him along the ground.
        Vector3 up = asset.UpOf(asset.Walk.TravelNode);
        foreach ((int side, int head, string name) in new[] { (0, 0, "death_front"), (1, 0, "death_back"), (0, 1, "death_front_head"), (1, 1, "death_back_head") })
        {
            if (model.LogicalAnimations.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) is { } fall)
            {
                asset.Deaths[side, head] = ModernClip.ReadFall(fall, index, asset.Rest.Length, up);
            }
        }

        ModernClip? Flinch(string name) =>
            model.LogicalAnimations.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) is { } clip
                ? ModernClip.ReadFall(clip, index, asset.Rest.Length, up)
                : null;
        asset.Hits = (Flinch("hit_front"), Flinch("hit_back"), Flinch("hit_left"), Flinch("hit_right"));

        // The unarmed reactions, played on everything but the arms, which stay on the rifle.
        foreach (string name in new[] { "react_front", "react_back", "react_left", "react_right", "react_front_big", "react_back_big", "react_left_big", "react_right_big", "react_head", "react_gut" })
        {
            if (Flinch(name) is { } react)
            {
                react.Layered = true;
                asset.Reacts[name] = react;
            }
        }

        asset.ArmNodes = [.. Enumerable.Range(0, asset.Names.Length).Select(asset.IsArm)];

        return asset;
    }

    /// <summary>Gets how fast the walk clip covers ground, in metres a second, which a walking soldier's pace is measured against.</summary>
    public float WalkMetresPerSecond { get; private set; }

    /// <summary>Turns a point of glTF's frame into the soldier's mesh frame: up +Z, facing +Y, then turned about Z.</summary>
    /// <param name="v">The point.</param>
    /// <param name="yaw">The extra turn about up, in radians, for a character authored facing another way.</param>
    /// <returns>The point in the soldier's frame.</returns>
    public static Vector3 ToBody(Vector3 v, float yaw)
    {
        Vector3 turned = new(-v.X, v.Z, v.Y);
        if (yaw == 0f)
        {
            return turned;
        }

        float c = MathF.Cos(yaw), s = MathF.Sin(yaw);
        return new Vector3((turned.X * c) - (turned.Y * s), (turned.X * s) + (turned.Y * c), turned.Z);
    }

    /// <summary>Poses the skeleton: each node's local transform blended between two clips, then carried down from the root.</summary>
    /// <param name="idleTime">Where in the idle clip.</param>
    /// <param name="walkTime">Where in the walk clip.</param>
    /// <param name="walkWeight">How much of the walk, nought (all idle) to one (all walk).</param>
    /// <param name="world">Each node's world matrix, written.</param>
    /// <param name="skin">Each joint's skinning matrix, written.</param>
    /// <param name="hit">A flinch played over the rest, or null.</param>
    /// <param name="hitTime">Where in the flinch.</param>
    /// <param name="hitWeight">How much of the flinch.</param>
    public void Pose(
        float idleTime, float walkTime, float walkWeight, Matrix4x4[] world, Matrix4x4[] skin, ModernClip? hit = null, float hitTime = 0f, float hitWeight = 0f)
    {
        ModernMix mix = new(Idle, idleTime, Walk, walkTime, walkWeight);
        Blend(mix, mix, 1f, world, skin, hit, hitTime, hitWeight);
    }

    /// <summary>Poses the skeleton in one mix of a standing and a moving clip, faded in from the mix before it.</summary>
    /// <param name="now">What he is doing.</param>
    /// <param name="before">What he was doing.</param>
    /// <param name="fade">How far from the one before to now, nought to one.</param>
    /// <param name="world">Each node's world matrix, written.</param>
    /// <param name="skin">Each joint's skinning matrix, written.</param>
    /// <param name="hit">A flinch played over the rest, or null.</param>
    /// <param name="hitTime">Where in the flinch.</param>
    /// <param name="hitWeight">How much of the flinch.</param>
    /// <param name="kicks">Each node's kick (<see cref="ModernBody.Kick"/>), a turn about its joint in glTF's world: the axis times the angle in radians.</param>
    public void Pose(
        in ModernMix now, in ModernMix before, float fade, Matrix4x4[] world, Matrix4x4[] skin, ModernClip? hit, float hitTime, float hitWeight, Vector3[]? kicks = null) =>
        Blend(now, before, fade, world, skin, hit, hitTime, hitWeight, kicks);

    /// <summary>Poses the skeleton in one fall, held on its last frame once it has run.</summary>
    /// <param name="fall">The fall (<see cref="Death"/>).</param>
    /// <param name="time">How long since the soldier died, in seconds.</param>
    /// <param name="world">Each node's world matrix, written.</param>
    /// <param name="skin">Each joint's skinning matrix, written.</param>
    public void PoseFall(ModernClip fall, float time, Matrix4x4[] world, Matrix4x4[] skin)
    {
        ModernMix mix = new(fall, time, fall, time, 0f);
        Blend(mix, mix, 1f, world, skin);
    }

    private void Blend(
        in ModernMix now,
        in ModernMix before,
        float fade,
        Matrix4x4[] world,
        Matrix4x4[] skin,
        ModernClip? over = null,
        float overTime = 0f,
        float overWeight = 0f,
        Vector3[]? kicks = null)
    {
        for (int node = 0; node < Rest.Length; node++)
        {
            (Vector3 t, Quaternion r, Vector3 s) = Mix(now, node);
            if (fade < 1f)
            {
                (Vector3 T, Quaternion R, Vector3 S) was = Mix(before, node);
                t = Vector3.Lerp(was.T, t, fade);
                r = Quaternion.Slerp(was.R, r, fade);
                s = Vector3.Lerp(was.S, s, fade);
            }

            // A flinch over whatever he is doing, its own slide taken out like a fall's.
            if (over is not null && overWeight > 0f && over.Layered && !ArmNodes[node])
            {
                // A layered reaction is added, not swapped in: only how it moves away from its own first frame, on top of
                // whatever stance he is in, so the rifle stance keeps its facing and the jolt, twist and stumble are added.
                (Vector3 T, Quaternion R, Vector3 S) from = over.Sample(node, 0f, Rest[node]);
                (Vector3 T, Quaternion R, Vector3 S) to = over.Sample(node, overTime, Rest[node]);
                Quaternion turn = Quaternion.Normalize(Quaternion.Inverse(from.R) * to.R);
                r = Quaternion.Normalize(r * Quaternion.Slerp(Quaternion.Identity, turn, overWeight));
                Vector3 shift = to.T - from.T;
                if (node == over.TravelNode)
                {
                    shift -= over.TravelAt(overTime);
                }

                t += shift * overWeight;
            }
            else if (over is not null && overWeight > 0f && !over.Layered)
            {
                (Vector3 T, Quaternion R, Vector3 S) h = over.Sample(node, overTime, Rest[node]);
                if (node == over.TravelNode)
                {
                    h.T -= over.TravelAt(overTime);
                }

                t = Vector3.Lerp(t, h.T, overWeight);
                r = Quaternion.Slerp(r, h.R, overWeight);
                s = Vector3.Lerp(s, h.S, overWeight);
            }

            Matrix4x4 local = Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(r) * Matrix4x4.CreateTranslation(t);
            world[node] = Parents[node] < 0 ? local : local * world[Parents[node]];

            // A round's kick turns the node about its own joint, after the clips have posed it; its children follow.
            if (kicks is not null && kicks[node].LengthSquared() > 1e-8f)
            {
                float angle = kicks[node].Length();
                Vector3 pivot = world[node].Translation;
                world[node] = world[node] * Matrix4x4.CreateTranslation(-pivot)
                    * Matrix4x4.CreateFromAxisAngle(kicks[node] / angle, angle) * Matrix4x4.CreateTranslation(pivot);
            }
        }

        for (int j = 0; j < Joints.Length; j++)
        {
            skin[j] = Joints[j].InverseBind * world[Joints[j].Node];
        }
    }

    /// <summary>Gets or sets the colour every vertex carries, which the albedo is multiplied by: how far the character is toned
    /// down to sit in a level lit for the original's darker art (<see cref="Game.GameplayCVars.ModernBodyTone"/>).</summary>
    public Bgra32 Tone { get; set; } = Bgra32.White;

    /// <summary>Skins the mesh into the soldier's frame, its lowest point at the height given.</summary>
    /// <param name="skin">The joints' skinning matrices (<see cref="Blend"/>).</param>
    /// <param name="yaw">The extra turn about up (<see cref="ToBody"/>).</param>
    /// <param name="lift">How far to raise every point, in metres, along the soldier's up.</param>
    /// <param name="into">The vertices, written.</param>
    public void Skin(Matrix4x4[] skin, float yaw, float lift, Vertex3D[] into)
    {
        for (int v = 0; v < Vertices.Length; v++)
        {
            (Vector3 position, Vector3 normal, Vector2 uv) = Vertices[v];
            (Vector4 joints, Vector4 weights) = Weights[v];
            Vector3 p = Vector3.Zero, n = Vector3.Zero;
            for (int k = 0; k < 4; k++)
            {
                float w = weights[k];
                if (w > 0f)
                {
                    Matrix4x4 m = skin[(int)joints[k]];
                    p += Vector3.Transform(position, m) * w;
                    n += Vector3.TransformNormal(normal, m) * w;
                }
            }

            Vector3 at = ToBody(p, yaw);
            Vector3 facing = Vector3.Normalize(ToBody(n, yaw));
            into[v] = new Vertex3D(
                new Vector3f(at.X, at.Y, at.Z + lift),
                new Vector3f(facing.X, facing.Y, facing.Z),
                uv.X,
                uv.Y,
                colour: Tone);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (TextureHandle detail in _details)
        {
            _device.DestroyTexture(detail);
        }

        _details.Clear();
        foreach ((_, _, TextureHandle diffuse, _) in Subsets)
        {
            if (diffuse.IsValid)
            {
                _device.DestroyTexture(diffuse);
            }
        }

        Subsets = [];
    }

    private static GltfAnimation Find(ModelRoot model, string name, string path) =>
        model.LogicalAnimations.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidDataException($"{path} holds no clip named '{name}'.");

    // One node in a mix of a standing and a moving clip, each clip's travel taken off its root along the ground: the soldier's own
    // move carries the body.
    private (Vector3 T, Quaternion R, Vector3 S) Mix(in ModernMix mix, int node)
    {
        float weight = mix.MoveWeight;
        (Vector3 T, Quaternion R, Vector3 S) a = mix.Stand.Sample(node, mix.StandTime, Rest[node]);
        (Vector3 T, Quaternion R, Vector3 S) b = weight > 0f ? mix.Move.Sample(node, mix.MoveTime, Rest[node]) : a;
        Vector3 t = Vector3.Lerp(a.T, b.T, weight);
        if (node == mix.Stand.TravelNode)
        {
            t -= mix.Stand.TravelAt(mix.StandTime) * (1f - weight);
        }

        if (node == mix.Move.TravelNode && weight > 0f)
        {
            t -= mix.Move.TravelAt(mix.MoveTime) * weight;
        }

        return (t, Quaternion.Slerp(a.R, b.R, weight), Vector3.Lerp(a.S, b.S, weight));
    }

    // The up direction in a node's parent's frame, at rest: what a fall's slide along the ground is measured against.
    private Vector3 UpOf(int node)
    {
        int parent = node >= 0 ? Parents[node] : -1;
        if (parent < 0)
        {
            return Vector3.UnitY;
        }

        Matrix4x4[] world = new Matrix4x4[Rest.Length];
        Matrix4x4[] skin = new Matrix4x4[Joints.Length];
        Pose(0f, 0f, 0f, world, skin);
        return Matrix4x4.Invert(world[parent], out Matrix4x4 inverse)
            ? Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, inverse))
            : Vector3.UnitY;
    }

    // Every node of the default scene, parents first.
    private Dictionary<Node, int> ReadNodes(ModelRoot model)
    {
        List<Node> order = [];
        Stack<Node> pending = new(model.DefaultScene.VisualChildren.Reverse());
        while (pending.TryPop(out Node? node))
        {
            order.Add(node);
            foreach (Node child in node.VisualChildren.Reverse())
            {
                pending.Push(child);
            }
        }

        Dictionary<Node, int> index = order.Select((n, i) => (n, i)).ToDictionary(p => p.n, p => p.i);
        Parents = [.. order.Select(n => n.VisualParent is { } parent ? index[parent] : -1)];
        Rest = [.. order.Select(n => (n.LocalTransform.Translation, n.LocalTransform.Rotation, n.LocalTransform.Scale))];
        Names = [.. order.Select(n => n.Name ?? string.Empty)];
        return index;
    }

    private void ReadMesh(Mesh mesh)
    {
        List<(Vector3, Vector3, Vector2)> vertices = [];
        List<(Vector4, Vector4)> skin = [];
        List<ushort> indices = [];
        List<(int, int, TextureHandle, bool)> subsets = [];
        Dictionary<SharpGLTF.Schema2.Image, TextureHandle> uploaded = [];

        foreach (MeshPrimitive primitive in mesh.Primitives)
        {
            int first = vertices.Count;
            IList<Vector3> positions = primitive.GetVertexAccessor("POSITION").AsVector3Array();
            IList<Vector3> normals = primitive.GetVertexAccessor("NORMAL").AsVector3Array();
            IList<Vector2> uvs = primitive.GetVertexAccessor("TEXCOORD_0").AsVector2Array();
            IList<Vector4> joints = primitive.GetVertexAccessor("JOINTS_0").AsVector4Array();
            IList<Vector4> weights = primitive.GetVertexAccessor("WEIGHTS_0").AsVector4Array();
            for (int i = 0; i < positions.Count; i++)
            {
                vertices.Add((positions[i], normals[i], uvs[i]));
                skin.Add((joints[i], weights[i]));
            }

            if (vertices.Count > ushort.MaxValue)
            {
                throw new InvalidDataException($"The character has {vertices.Count} vertices; a soldier's mesh holds at most {ushort.MaxValue}.");
            }

            int offset = indices.Count;
            indices.AddRange(primitive.GetIndices().Select(i => (ushort)(i + first)));

            TextureHandle diffuse = TextureHandle.None;
            if (primitive.Material?.FindChannel("BaseColor")?.Texture?.PrimaryImage is { } image)
            {
                if (!uploaded.TryGetValue(image, out diffuse))
                {
                    diffuse = Upload(image.Content.Content.ToArray());
                    uploaded[image] = diffuse;
                    AttachDetail(diffuse, primitive.Material!);
                }
            }

            bool gun = primitive.Material?.Name?.StartsWith("Rifle_", StringComparison.Ordinal) ?? false;
            subsets.Add((offset, indices.Count - offset, diffuse, gun));
        }

        Vertices = [.. vertices];
        Weights = [.. skin];
        Indices = [.. indices];
        Subsets = [.. subsets];
        GunVertices = [.. Subsets.Where(s => s.Gun).SelectMany(s => Indices.Skip(s.IndexOffset).Take(s.IndexCount)).Select(i => (int)i).Distinct()];
        GunEnds = FindGunEnds();
        LowestY = Vertices.Min(v => v.Position.Y);
    }

    // The material's own normal map and metal-roughness, packed as the renderer reads an authored detail map -- the normal's x
    // and y (y flipped: glTF's points up the image, the renderer's tangent frame down it), the roughness, the metalness -- and
    // attached to its colour texture. Nothing attached for a material with neither.
    private void AttachDetail(TextureHandle diffuse, Material material)
    {
        MaterialChannel? normalChannel = material.FindChannel("Normal");
        MaterialChannel? surfaceChannel = material.FindChannel("MetallicRoughness");
        ImageResult? normal = Decode(normalChannel?.Texture?.PrimaryImage);
        ImageResult? surface = Decode(surfaceChannel?.Texture?.PrimaryImage);
        if (normal is null && surface is null)
        {
            return;
        }

        // glTF's metallic and roughness factors scale the map, and stand for it where there is none.
        float Factor(string name, float otherwise) =>
            surfaceChannel?.Parameters.FirstOrDefault(p => p.Name.Contains(name, StringComparison.OrdinalIgnoreCase))?.Value is float value
                ? value
                : otherwise;
        Vector2 factors = new(Factor("Metallic", 0f), Factor("Roughness", 1f));
        ImageResult size = normal ?? surface!;
        int width = size.Width, height = size.Height;
        Bgra32[] packed = new Bgra32[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                (byte nx, byte ny) = normal is null ? ((byte)128, (byte)128) : (Texel(normal, x, y, width, height, 0), Texel(normal, x, y, width, height, 1));
                float roughness = factors.Y * (surface is null ? 1f : Texel(surface, x, y, width, height, 1) / 255f);
                float metalness = factors.X * (surface is null ? 1f : Texel(surface, x, y, width, height, 2) / 255f);
                packed[(y * width) + x] = Bgra32.FromRgba(nx, (byte)(255 - ny), (byte)Math.Clamp(roughness * 255f, 0f, 255f), (byte)Math.Clamp(metalness * 255f, 0f, 255f));
            }
        }

        (Bgra32[] chain, int levels) = LinearMipChain.Build(packed, width, height);
        TextureHandle map = _device.CreateTexture(
            new TextureDescription(width, height, levels, TextureFilter.Linear, TextureAddressMode.Repeat) { HoldsData = true },
            chain);
        _details.Add(map);
        _device.SetTextureDetail(diffuse, map);
    }

    private static ImageResult? Decode(SharpGLTF.Schema2.Image? image) =>
        image is null ? null : ImageResult.FromMemory(image.Content.Content.ToArray(), ColorComponents.RedGreenBlueAlpha);

    // One channel of an image at the place a texel of a map the size given falls on it.
    private static byte Texel(ImageResult image, int x, int y, int width, int height, int channel) =>
        image.Data[((((y * image.Height / height) * image.Width) + (x * image.Width / width)) * 4) + channel];

    // A JPEG or PNG, decoded and uploaded with its whole mip chain.
    private TextureHandle Upload(byte[] encoded)
    {
        ImageResult image = ImageResult.FromMemory(encoded, ColorComponents.RedGreenBlueAlpha);
        Bgra32[] level = new Bgra32[image.Width * image.Height];
        for (int i = 0; i < level.Length; i++)
        {
            int at = i * 4;
            level[i] = Bgra32.FromRgba(image.Data[at], image.Data[at + 1], image.Data[at + 2], image.Data[at + 3]);
        }

        (Bgra32[] chain, _) = MipChainBuilder.Build(level, image.Width, image.Height);
        return _device.CreateTexture(
            new TextureDescription(
                image.Width,
                image.Height,
                MipChainBuilder.LevelCount(image.Width, image.Height),
                TextureFilter.Linear,
                TextureAddressMode.Repeat),
            chain);
    }
}

/// <summary>A standing clip and a moving one, where each is, and how much of the moving one.</summary>
/// <param name="Stand">The standing clip.</param>
/// <param name="StandTime">Where in it, in seconds.</param>
/// <param name="Move">The moving clip.</param>
/// <param name="MoveTime">Where in it, in seconds.</param>
/// <param name="MoveWeight">How much of it, nought to one.</param>
internal readonly record struct ModernMix(ModernClip Stand, float StandTime, ModernClip Move, float MoveTime, float MoveWeight);

/// <summary>One clip of a <see cref="ModernBodyAsset"/>: a translation, rotation and scale curve for each node it moves.</summary>
internal sealed class ModernClip
{
    /// <summary>A clip that moves nothing.</summary>
    public static readonly ModernClip None = new() { Duration = 1f };

    private ICurveSampler<Vector3>?[] _translations = [];
    private ICurveSampler<Quaternion>?[] _rotations = [];
    private ICurveSampler<Vector3>?[] _scales = [];
    private Vector3 _travel;
    private Vector3 _up;
    private bool _falls;

    /// <summary>Gets or sets a value indicating whether the clip is played over everything but the arms, which keep the rifle.</summary>
    public bool Layered { get; set; }

    /// <summary>Gets how fast a moving clip covers ground, in metres a second, as the character's skeleton scales it.</summary>
    public float MetresPerSecond { get; internal set; }

    /// <summary>Gets how long the clip runs, in seconds.</summary>
    public float Duration { get; private init; }

    /// <summary>Gets the node whose translation carries the clip's travel -- the first it translates, its root -- or -1.</summary>
    public int TravelNode { get; private set; } = -1;

    /// <summary>Gets how far the clip's root goes in one run, along the ground, in its parent's frame.</summary>
    public Vector3 Travel => _travel;

    /// <summary>Reads a clip.</summary>
    /// <param name="animation">The glTF animation.</param>
    /// <param name="index">Each node's index in the asset.</param>
    /// <param name="nodes">How many nodes the asset has.</param>
    /// <param name="travelling">Whether it walks somewhere, and its travel is to be taken out.</param>
    /// <returns>The clip.</returns>
    public static ModernClip Read(GltfAnimation animation, Dictionary<Node, int> index, int nodes, bool travelling)
    {
        ModernClip clip = new()
        {
            Duration = Math.Max(animation.Duration, 1e-3f),
            _translations = new ICurveSampler<Vector3>?[nodes],
            _rotations = new ICurveSampler<Quaternion>?[nodes],
            _scales = new ICurveSampler<Vector3>?[nodes],
        };

        foreach (AnimationChannel channel in animation.Channels)
        {
            if (!index.TryGetValue(channel.TargetNode, out int node))
            {
                continue;
            }

            switch (channel.TargetNodePath)
            {
                case PropertyPath.translation:
                    clip._translations[node] = channel.GetTranslationSampler().CreateCurveSampler();
                    if (clip.TravelNode < 0 || node < clip.TravelNode)
                    {
                        clip.TravelNode = node;
                    }

                    break;
                case PropertyPath.rotation:
                    clip._rotations[node] = channel.GetRotationSampler().CreateCurveSampler();
                    break;
                case PropertyPath.scale:
                    clip._scales[node] = channel.GetScaleSampler().CreateCurveSampler();
                    break;
            }
        }

        // The root's start-to-end move. A looping walk ends at the height it began, so the move is all along the ground,
        // and taking it out at an even pace leaves the stride's bob and sway where they are.
        if (travelling && clip.TravelNode >= 0 && clip._translations[clip.TravelNode] is { } root)
        {
            clip._travel = root.GetPoint(clip.Duration) - root.GetPoint(0f);
        }

        return clip;
    }

    /// <summary>Reads a fall: played once and held, its root's slide along the ground taken out and its drop kept.</summary>
    /// <param name="animation">The glTF animation.</param>
    /// <param name="index">Each node's index in the asset.</param>
    /// <param name="nodes">How many nodes the asset has.</param>
    /// <param name="up">Up, in the root's parent's frame.</param>
    /// <returns>The clip.</returns>
    public static ModernClip ReadFall(GltfAnimation animation, Dictionary<Node, int> index, int nodes, Vector3 up)
    {
        ModernClip clip = Read(animation, index, nodes, travelling: false);
        clip._falls = true;
        clip._up = up;
        return clip;
    }

    /// <summary>Gets a node's local transform at a time, the clip wrapping round, or its rest for what the clip does not move.</summary>
    /// <param name="node">The node.</param>
    /// <param name="time">The time, in seconds.</param>
    /// <param name="rest">The node's rest transform.</param>
    /// <returns>The transform.</returns>
    public (Vector3 T, Quaternion R, Vector3 S) Sample(int node, float time, (Vector3 T, Quaternion R, Vector3 S) rest)
    {
        if (node >= _rotations.Length)
        {
            return rest;
        }

        float t = Wrap(time);
        return (
            _translations[node]?.GetPoint(t) ?? rest.T,
            _rotations[node]?.GetPoint(t) ?? rest.R,
            _scales[node]?.GetPoint(t) ?? rest.S);
    }

    /// <summary>Gets how much of the clip's travel has been made by a time within a run.</summary>
    /// <param name="time">The time, in seconds.</param>
    /// <returns>The travel so far.</returns>
    public Vector3 TravelAt(float time)
    {
        if (!_falls)
        {
            return _travel * (Wrap(time) / Duration);
        }

        if (TravelNode < 0 || _translations[TravelNode] is not { } root)
        {
            return Vector3.Zero;
        }

        Vector3 moved = root.GetPoint(Wrap(time)) - root.GetPoint(0f);
        return moved - (Vector3.Dot(moved, _up) * _up);
    }

    // A fall holds its last frame; anything else loops.
    private float Wrap(float time) => _falls
        ? Math.Clamp(time, 0f, Duration)
        : time - (MathF.Floor(time / Duration) * Duration);
}
