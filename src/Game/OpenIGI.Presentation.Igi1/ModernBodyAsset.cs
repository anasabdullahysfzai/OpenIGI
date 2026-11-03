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

    /// <summary>Gets each primitive's run of <see cref="Indices"/> and its colour texture.</summary>
    public (int IndexOffset, int IndexCount, TextureHandle Diffuse)[] Subsets { get; private set; } = [];

    /// <summary>Gets the lowest point of the bind pose, along glTF's up, which the soldier's lowest point is matched to.</summary>
    public float LowestY { get; private set; }

    /// <summary>Gets the right hand's node, which a gun is held in, or -1.</summary>
    public int RightHand => Array.IndexOf(Names, "mixamorig:RightHand");

    /// <summary>Gets the left hand's node, or -1.</summary>
    public int LeftHand => Array.IndexOf(Names, "mixamorig:LeftHand");

    /// <summary>Gets the standing clip.</summary>
    public ModernClip Idle { get; private set; } = ModernClip.None;

    /// <summary>Gets the walking clip, travel taken out.</summary>
    public ModernClip Walk { get; private set; } = ModernClip.None;

    /// <summary>Gets the falls, by shot from behind then headshot, each null where the file has none.</summary>
    /// <remarks>Named <c>death_front</c>, <c>death_back</c>, <c>death_front_head</c> and <c>death_back_head</c>; played once and held.</remarks>
    public ModernClip?[,] Deaths { get; private set; } = new ModernClip?[2, 2];

    /// <summary>Gets the flinches, each null where the file has none: <c>hit_front</c>, <c>hit_back</c>, <c>hit_left</c>, <c>hit_right</c>.</summary>
    public (ModernClip? Front, ModernClip? Back, ModernClip? Left, ModernClip? Right) Hits { get; private set; }

    /// <summary>Gets the flinch for a round, by where its shooter was: behind, to one side, else ahead.</summary>
    /// <param name="ahead">How far ahead the shooter was, along the ground, unit length with <paramref name="right"/>.</param>
    /// <param name="right">How far to the right.</param>
    /// <returns>The flinch, or null where the file has none.</returns>
    public ModernClip? Hit(float ahead, float right)
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
        asset.WalkMetresPerSecond = ModernBody.WalkSpeed(asset);

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
        float idleTime, float walkTime, float walkWeight, Matrix4x4[] world, Matrix4x4[] skin, ModernClip? hit = null, float hitTime = 0f, float hitWeight = 0f) =>
        Blend(Idle, idleTime, Walk, walkTime, walkWeight, world, skin, hit, hitTime, hitWeight);

    /// <summary>Poses the skeleton in one fall, held on its last frame once it has run.</summary>
    /// <param name="fall">The fall (<see cref="Death"/>).</param>
    /// <param name="time">How long since the soldier died, in seconds.</param>
    /// <param name="world">Each node's world matrix, written.</param>
    /// <param name="skin">Each joint's skinning matrix, written.</param>
    public void PoseFall(ModernClip fall, float time, Matrix4x4[] world, Matrix4x4[] skin) =>
        Blend(fall, time, fall, time, 0f, world, skin);

    private void Blend(
        ModernClip first,
        float firstTime,
        ModernClip second,
        float secondTime,
        float weight,
        Matrix4x4[] world,
        Matrix4x4[] skin,
        ModernClip? over = null,
        float overTime = 0f,
        float overWeight = 0f)
    {
        for (int node = 0; node < Rest.Length; node++)
        {
            (Vector3 T, Quaternion R, Vector3 S) a = first.Sample(node, firstTime, Rest[node]);
            (Vector3 T, Quaternion R, Vector3 S) b = weight > 0f ? second.Sample(node, secondTime, Rest[node]) : a;
            Vector3 t = Vector3.Lerp(a.T, b.T, weight);
            Quaternion r = Quaternion.Slerp(a.R, b.R, weight);
            Vector3 s = Vector3.Lerp(a.S, b.S, weight);

            // A clip's travel comes off its root, along the ground: the soldier's own move carries the body.
            if (node == first.TravelNode)
            {
                t -= first.TravelAt(firstTime) * (1f - weight);
            }

            if (node == second.TravelNode && weight > 0f)
            {
                t -= second.TravelAt(secondTime) * weight;
            }

            // A flinch over whatever he is doing, its own slide taken out like a fall's.
            if (over is not null && overWeight > 0f)
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
        }

        for (int j = 0; j < Joints.Length; j++)
        {
            skin[j] = Joints[j].InverseBind * world[Joints[j].Node];
        }
    }

    /// <summary>Skins the mesh into the soldier's frame, its lowest point at the height given.</summary>
    /// <param name="skin">The joints' skinning matrices (<see cref="Pose"/>).</param>
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
                uv.Y);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach ((_, _, TextureHandle diffuse) in Subsets)
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
        List<(int, int, TextureHandle)> subsets = [];
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
                }
            }

            subsets.Add((offset, indices.Count - offset, diffuse));
        }

        Vertices = [.. vertices];
        Weights = [.. skin];
        Indices = [.. indices];
        Subsets = [.. subsets];
        LowestY = Vertices.Min(v => v.Position.Y);
    }

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
