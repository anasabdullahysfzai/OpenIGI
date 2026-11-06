using System.Numerics;
using OpenIGI.Core.Graphics;
using OpenIGI.Core.Numerics;
using OpenIGI.Rendering;

namespace OpenIGI.Presentation.Igi1;

/// <summary>
/// One soldier's <see cref="ModernBodyAsset"/>: its own skinned mesh, and where it is in the idle and walk clips. Each tick it
/// is posed by how fast the soldier moved -- idle standing, the walk at the soldier's own pace -- and skinned into the mesh the
/// drawer then draws in place of the soldier's own. <b>Not retail.</b>
/// </summary>
internal sealed class ModernBody : IDisposable
{
    /// <summary>Below this ground speed, in metres a second, the soldier counts as standing.</summary>
    public const float StandingBelow = 0.2f;

    /// <summary>How long the body takes to go between standing and walking, in seconds.</summary>
    public const float BlendSeconds = 0.25f;

    /// <summary>The slowest and fastest the walk is played, against its own pace, before it would look wrong.</summary>
    public const float SlowestPace = 0.5f, FastestPace = 2.0f;

    /// <summary>Above this ground speed, in metres a second, he runs; below the second, a runner walks again.</summary>
    public const float RunAbove = 2.2f, RunBelow = 1.8f;

    /// <summary>Above how many metres a second a limping body shows his hurried limp: between his walk (0.9) and jog (2.0).</summary>
    public const float LimpRunAbove = 1.5f;

    /// <summary>How long a shot keeps the firing clip playing, in seconds.</summary>
    public const float FireSeconds = 0.6f;

    private const float TickSeconds = 1f / 30f;

    private readonly ModernBodyAsset _asset;
    private readonly IGraphicsDevice _device;
    private readonly Matrix4x4[] _world;
    private readonly Matrix4x4[] _skin;
    private readonly Vertex3D[] _vertices;
    private float _standTime;
    private float _stride;
    private float _moveWeight;
    private ModernClip _stand;
    private ModernClip _move;

    private bool _running;
    private int _shotsSeen;
    private float _firingFor;
    private ModernClip? _fall;
    private float _fallTime;
    private float _fallRaise = float.NaN;
    private float _floor;
    private bool _gesture;
    private ModernClip? _hit;
    private Vector3[] _kick = [];
    private Vector3[] _kickSpeed = [];
    private float _hitTime;

    /// <summary>Initializes a new instance of the <see cref="ModernBody"/> class, its mesh in the bind pose.</summary>
    /// <param name="asset">The character.</param>
    /// <param name="device">The device its mesh lives on.</param>
    public ModernBody(ModernBodyAsset asset, IGraphicsDevice device)
    {
        _asset = asset;
        _device = device;
        (_stand, _move) = (asset.Idle, asset.Walk);
        _inertia = new ModernInertia(asset.Rest.Length);
        _world = new Matrix4x4[asset.Rest.Length];
        _skin = new Matrix4x4[asset.Joints.Length];
        _vertices = new Vertex3D[asset.Vertices.Length];
        asset.Pose(0f, 0f, 0f, _world, _skin);
        asset.Skin(_skin, 0f, 0f, _vertices);
        // Where each hand holds the rifle, in the weapon bone's frame, as the idle the rifle was fitted in holds it.
        if (_asset.Weapon >= 0 && _asset.LeftArm.Wrist >= 0 && _asset.RightArm.Wrist >= 0)
        {
            Matrix4x4.Invert(_world[_asset.Weapon], out Matrix4x4 into);
            _leftGrip = Vector3.Transform(_world[_asset.LeftArm.Wrist].Translation, into);
            _rightGrip = Vector3.Transform(_world[_asset.RightArm.Wrist].Translation, into);
        }
        Mesh = device.CreateAnimatedMesh(_vertices, asset.Indices);
    }

    /// <summary>Gets both palms in the soldier's frame as last posed, for testing.</summary>
    internal (Vector3 Right, Vector3 Left) PalmsForTesting =>
        ((Hand(_asset.RightPalm.Wrist, 0f, 0f) + Hand(_asset.RightPalm.Knuckle, 0f, 0f)) / 2f,
         (Hand(_asset.LeftPalm.Wrist, 0f, 0f) + Hand(_asset.LeftPalm.Knuckle, 0f, 0f)) / 2f);

    /// <summary>
    /// Brings a wrist to a point, in glTF's world, bending the elbow and turning the arm at the shoulder: the two-bone reach
    /// that keeps the hand on the rifle's handguard where a clip from another set, a flinch or a kick, has it off it.
    /// </summary>
    private void ReachHand((int Shoulder, int Elbow, int Wrist) arm, Vector3 target)
    {
        (int shoulder, int elbow, int wrist) = arm;
        if (shoulder < 0 || elbow < 0 || wrist < 0)
        {
            return;
        }

        Vector3 a = _world[shoulder].Translation, b = _world[elbow].Translation, c = _world[wrist].Translation;
        float upper = Vector3.Distance(a, b), lower = Vector3.Distance(b, c);
        float reach = Math.Clamp(Vector3.Distance(a, target), Math.Abs(upper - lower) + 1e-4f, upper + lower - 1e-4f);

        // The elbow first: open or close it until the shoulder-to-wrist span is the reach.
        Vector3 toShoulder = a - b, toWrist = c - b;
        Vector3 bend = Vector3.Cross(toShoulder, toWrist);
        if (bend.LengthSquared() > 1e-10f)
        {
            float now = MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(toShoulder), Vector3.Normalize(toWrist)), -1f, 1f));
            float wanted = MathF.Acos(Math.Clamp(((upper * upper) + (lower * lower) - (reach * reach)) / (2f * upper * lower), -1f, 1f));
            Turn(elbow, Quaternion.CreateFromAxisAngle(Vector3.Normalize(bend), wanted - now));
        }

        // Then the whole arm at the shoulder, so the wrist comes to the target.
        Vector3 from = _world[wrist].Translation - a, to = target - a;
        Vector3 axis = Vector3.Cross(from, to);
        if (axis.LengthSquared() > 1e-10f)
        {
            float angle = MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(from), Vector3.Normalize(to)), -1f, 1f));
            Turn(shoulder, Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), angle));
        }
    }

    // Turns a node and all below it about its joint, in glTF's world, and their skinning with them.
    private void Turn(int node, Quaternion turn)
    {
        Vector3 pivot = _world[node].Translation;
        Matrix4x4 about = Matrix4x4.CreateTranslation(-pivot) * Matrix4x4.CreateFromQuaternion(turn) * Matrix4x4.CreateTranslation(pivot);
        int[] joints = _asset.JointOfNode;
        foreach (int n in _asset.Below(node))
        {
            _world[n] *= about;
            if (joints[n] >= 0)
            {
                _skin[joints[n]] = _asset.Joints[joints[n]].InverseBind * _world[n];
            }
        }
    }

    private static Vector3 Vec(Vector3f v) => new(v.X, v.Y, v.Z);

    // A node's place in the soldier's frame, metres.
    private Vector3 Hand(int node, float yaw, float lift) =>
        node < 0 ? Vector3.Zero : ModernBodyAsset.ToBody(_world[node].Translation, yaw) + new Vector3(0f, 0f, lift);

    /// <summary>Gets the mesh the body is drawn from.</summary>
    public MeshHandle Mesh { get; private set; }

    /// <summary>Gets the character's runs of triangles and their textures.</summary>
    public (int IndexOffset, int IndexCount, TextureHandle Diffuse, bool Gun)[] Subsets => _asset.Subsets;

    /// <summary>Gets a value indicating whether the character came with its own normal, roughness and metalness.</summary>
    public bool HasAuthoredDetail => _asset.HasAuthoredDetail;

    /// <summary>Gets a value indicating whether the character holds a gun of its own.</summary>
    public bool HasGun => _asset.Subsets.Any(subset => subset.Gun);

    /// <summary>Gets or sets how far to raise the body, in metres, so its lowest point meets the soldier's; null until measured.</summary>
    public float? Lift { get; set; }

    /// <summary>Gets the ground speed the body was last posed for, in metres a second.</summary>
    public float Speed { get; private set; }

    /// <summary>How long a flinch takes to come in, and to go out at its end, in seconds.</summary>
    public const float HitInSeconds = 0.1f, HitOutSeconds = 0.3f;

    /// <summary>Starts a flinch over whatever the body is doing; a new one replaces one still playing.</summary>
    /// <param name="hit">The flinch (<see cref="ModernBodyAsset.Hit(float, float, int, bool)"/>).</param>
    public void Flinch(ModernClip hit)
    {
        _hit = hit;
        _hitTime = 0f;
    }

    /// <summary>How fast a kicked bone springs back, in radians a second, and how much it is damped (under one overshoots).</summary>
    public const float KickSpring = 9f, KickDamping = 0.5f;

    /// <summary>The furthest a bone may be kicked, however many rounds stack, in radians.</summary>
    public const float KickLimit = 0.8f;

    /// <summary>
    /// Kicks the bones a round struck: each turned towards where the round was going, the struck bone most, then sprung back
    /// over whatever the clips are doing. Rounds stack, so a burst kicks harder than one.
    /// </summary>
    /// <param name="zone">The original's hit zone.</param>
    /// <param name="ahead">How far ahead the shooter was, along the ground, unit length with <paramref name="right"/>.</param>
    /// <param name="right">How far to the right.</param>
    /// <param name="strength">How much of the zone's full kick, one being all of it.</param>
    public void Kick(int zone, float ahead, float right, float strength = 1f)
    {
        if (_kick.Length == 0)
        {
            (_kick, _kickSpeed) = (new Vector3[_world.Length], new Vector3[_world.Length]);
        }

        // Where the round was going, in glTF's world, along the ground.
        Vector3 going = ModernBodyAsset.FromBody(new Vector3(-right, -ahead, 0f));
        if (going.LengthSquared() < 1e-6f)
        {
            return;
        }

        going = Vector3.Normalize(going);
        Vector3 up = Vector3.UnitY;

        // An underdamped spring's first swing reaches about this share of speed over spring.
        float root = MathF.Sqrt(1f - (KickDamping * KickDamping));
        float swing = MathF.Exp(-KickDamping / root * MathF.Atan(root / KickDamping));
        foreach ((int node, float radians) in _asset.KickOf(zone))
        {
            // The bone turns about its joint so that its far end goes the round's way: about the axis across the bone and the push.
            int child = _asset.ChildOf(node);
            Vector3 bone = child >= 0 ? _world[child].Translation - _world[node].Translation : up;
            Vector3 axis = Vector3.Cross(bone, going);
            if (axis.LengthSquared() < 1e-8f)
            {
                axis = Vector3.Cross(up, going);
            }

            // Stepped at the tick, the spring's first swing falls short of the smooth one's by about this much.
            const float stepped = 1.35f;
            _kickSpeed[node] += Vector3.Normalize(axis) * (radians * strength * KickSpring / swing * stepped);
        }
    }

    /// <summary>Gets how far a node is kicked now, in radians, for testing.</summary>
    /// <param name="node">The node.</param>
    /// <returns>The angle.</returns>
    internal float KickAngleForTesting(int node) => node < _kick.Length ? _kick[node].Length() : 0f;

    /// <summary>How far the impact of a killing round turns his upper body at most, in radians: towards the side it landed on, in all, and leaning.</summary>
    public const float ImpactTwist = 1.3f, ImpactLean = 0.75f;

    /// <summary>
    /// The impact of a round from the front on the side of him it landed on: that shoulder goes back and his upper body bends that way,
    /// then springs back over the fall. Each spine bone takes its share, as a kick (<see cref="Kick"/>).
    /// </summary>
    /// <param name="side">The side it landed on: -1 his left, 1 his right.</param>
    /// <param name="strength">How much of the full impact, one being all of it.</param>
    public void Impact(int side, float strength)
    {
        if (_kick.Length == 0)
        {
            (_kick, _kickSpeed) = (new Vector3[_world.Length], new Vector3[_world.Length]);
        }

        // In glTF's world, about the up axis: going left brings his left shoulder back. About the axis ahead: going right tips him to his right.
        Vector3 turn = new(0f, -side * ImpactTwist, side * ImpactLean);
        const float stepped = 1.35f;
        float root = MathF.Sqrt(1f - (KickDamping * KickDamping));
        float swing = MathF.Exp(-KickDamping / root * MathF.Atan(root / KickDamping));
        (int spine, int spine1, int spine2, _, _) = _asset.Torso;
        foreach ((int node, float share) in new[] { (spine, 0.25f), (spine1, 0.3f), (spine2, 0.45f) })
        {
            if (node >= 0)
            {
                _kickSpeed[node] += turn * (share * strength * KickSpring / swing * stepped);
            }
        }
    }

    internal Vector3 BoneForTesting(int node) => _world[node].Translation;

    // One tick of every kicked bone's spring.
    private void SpringKicks()
    {
        for (int node = 0; node < _kick.Length; node++)
        {
            Vector3 pull = (-KickSpring * KickSpring * _kick[node]) - (2f * KickDamping * KickSpring * _kickSpeed[node]);
            _kickSpeed[node] += pull * TickSeconds;
            _kick[node] += _kickSpeed[node] * TickSeconds;
            if (_kick[node].Length() > KickLimit)
            {
                _kick[node] = Vector3.Normalize(_kick[node]) * KickLimit;
            }
        }
    }

    /// <summary>Gets a value indicating whether the body is falling, or lying where it fell.</summary>
    public bool Fallen => _fall is not null;

    /// <summary>Starts the body's fall, which every tick after plays once and then holds.</summary>
    /// <param name="fall">The fall (<see cref="ModernBodyAsset.Death"/>).</param>
    /// <param name="brief">Whether it is a short fall a ragdoll finishes, rather than one that ends on the ground.</param>
    /// <param name="blend">Whether it blends in from what he was doing; not for a body made to play it.</param>
    public void Fall(ModernClip fall, bool brief = false, bool blend = true)
    {
        FallIsBrief = brief;
        _fall = fall;
        _fallTime = 0f;
        _inertia.Begin = blend;
    }

    /// <summary>Gets or sets a value indicating whether the knife in his hand is drawn.</summary>
    public bool ShowsKnife { get; set; }

    /// <summary>Gets the knife's run of triangles and its texture.</summary>
    public (int IndexOffset, int IndexCount, TextureHandle Diffuse) KnifeSubset => _asset.Knife;

    private float[]? _soak;

    /// <summary>Gets or sets how far blood has spread from where <see cref="BeginSoak"/> set it, in metres.</summary>
    public float SoakMetres { get; set; }

    /// <summary>Starts blood soaking him from a point, by each vertex's distance from it as he stands now, unevenly, as cloth takes it.</summary>
    /// <param name="point">Where, in the soldier's frame.</param>
    /// <param name="runsDown">How much nearer a point below counts, within a hand of straight under it: blood runs down.</param>
    public void BeginSoak(Vector3 point, float runsDown)
    {
        _soak = new float[_vertices.Length];
        for (int v = 0; v < _vertices.Length; v++)
        {
            Vector3f p = _vertices[v].Position;
            Vector3 r = new Vector3(p.X, p.Y, p.Z) - point;
            float across = MathF.Sqrt((r.X * r.X) + (r.Y * r.Y));
            float down = across < 0.09f ? Math.Clamp(-r.Z, 0f, 0.3f) * runsDown : 0f;
            float uneven = (0.012f * MathF.Sin((p.X * 71f) + (p.Z * 43f))) + (0.01f * MathF.Sin((p.Y * 97f) - (p.Z * 61f))) + (0.006f * MathF.Sin((p.X * 190f) + (p.Y * 150f)));
            _soak[v] = r.Length() - down + uneven;
        }
    }

    // Over the albedo the vertex colour multiplies: what blood leaves of the cloth's colour.
    private static readonly Bgra32 Blood = Bgra32.FromRgba(112, 6, 6, 255);

    private void ApplySoak()
    {
        if (_soak is null)
        {
            return;
        }

        for (int v = 0; v < _soak.Length; v++)
        {
            if (_soak[v] < SoakMetres)
            {
                Vertex3D was = _vertices[v];
                _vertices[v] = new Vertex3D(was.Position, was.Normal, was.DiffuseU, was.DiffuseV, colour: Blood);
            }
        }
    }

    private int[] _hiddenJoints = [];
    private int _hiddenAt = -1;

    /// <summary>Takes his head and neck out of the mesh: every point skinned to them collapses at the root of the neck. For the eye inside it.</summary>
    public void HideHead()
    {
        int neck = _asset.Torso.Neck;
        _hiddenAt = neck;
        int[] joints = _asset.JointOfNode;
        _hiddenJoints = [.. _asset.Below(neck).Select(node => joints[node]).Where(joint => joint >= 0)];
    }

    private void CollapseHidden(Matrix4x4[] world)
    {
        if (_hiddenAt >= 0)
        {
            Matrix4x4 gone = Matrix4x4.CreateScale(1e-4f) * Matrix4x4.CreateTranslation(world[_hiddenAt].Translation);
            foreach (int joint in _hiddenJoints)
            {
                _skin[joint] = gone;
            }
        }
    }

    /// <summary>How far from the head bone the eye sits, in metres, up and ahead (the pair tool's viewer).</summary>
    private const float EyeUp = 0.08f, EyeAhead = 0.10f;

    /// <summary>How far below the head bone's axis the eye looks, as a slope: the pair tool's viewer sees about 4.6 degrees lower.</summary>
    private const float EyeLooksDown = 0.08f;

    /// <summary>Gets the eye in his head between the last two ticks, in the soldier's frame, metres.</summary>
    /// <param name="alpha">How far the frame is from the tick before the last to the last, nought to one.</param>
    /// <returns>The eye, and which way it looks, and up.</returns>
    public (Vector3 Eye, Vector3 Forward, Vector3 Up) HeadViewAt(float alpha)
    {
        int head = _asset.Torso.Head;
        Matrix4x4.Decompose(_worldBefore[head], out _, out Quaternion ra, out Vector3 ta);
        Matrix4x4.Decompose(_worldNow[head], out _, out Quaternion rb, out Vector3 tb);
        Quaternion r = Quaternion.Slerp(ra, rb, alpha);
        Vector3 up = Vector3.Transform(Vector3.UnitY, r), ahead = Vector3.Transform(Vector3.UnitZ, r);
        Vector3 eye = Vector3.Lerp(ta, tb, alpha) + (up * EyeUp) + (ahead * EyeAhead);
        Vector3 forward = Vector3.Normalize(ahead - (up * EyeLooksDown)), lookUp = Vector3.Normalize(up + (ahead * EyeLooksDown));
        float yaw = _keptYawBefore + ((_keptYaw - _keptYawBefore) * alpha), lift = _keptLiftBefore + ((_keptLift - _keptLiftBefore) * alpha);
        return (ModernBodyAsset.ToBody(eye, yaw) + new Vector3(0f, 0f, lift), ModernBodyAsset.ToBody(forward, yaw), ModernBodyAsset.ToBody(lookUp, yaw));
    }

    /// <summary>Gets a clip's last pose: where his hips stand and his head looks along the ground, in the soldier's frame.</summary>
    /// <param name="clip">The clip.</param>
    /// <param name="yaw">The character's extra turn about up, in radians.</param>
    /// <returns>The hips, and the head's forward.</returns>
    public (Vector3 Hips, Vector3 Forward) EndOf(ModernClip clip, float yaw)
    {
        Matrix4x4[] world = new Matrix4x4[_world.Length], skin = new Matrix4x4[_skin.Length];
        _asset.PoseFall(clip, clip.Duration, world, skin);
        Matrix4x4.Decompose(world[_asset.Torso.Head], out _, out Quaternion r, out _);
        int hips = Array.IndexOf(_asset.Names, "mixamorig:Hips");
        return (ModernBodyAsset.ToBody(world[hips].Translation, yaw), ModernBodyAsset.ToBody(Vector3.Transform(Vector3.UnitZ, r), yaw));
    }

    /// <summary>Gets a point on the neck, as he was last posed, in the soldier's frame: an offset, in glTF's world at the clip's first frame, carried with his neck.</summary>
    /// <param name="clip">The clip whose first frame the offset was measured on.</param>
    /// <param name="offset">The offset, metres.</param>
    /// <param name="yaw">The character's extra turn about up, in radians.</param>
    /// <returns>The point, and the way out from the neck through it.</returns>
    public (Vector3 Point, Vector3 Out) OnNeck(ModernClip clip, Vector3 offset, float yaw)
    {
        Matrix4x4[] world = new Matrix4x4[_world.Length], skin = new Matrix4x4[_skin.Length];
        int neck = _asset.Torso.Neck;
        _asset.PoseFall(clip, 0f, world, skin);
        Matrix4x4.Decompose(world[neck], out _, out Quaternion first, out _);
        Vector3 inNeck = Vector3.Transform(offset, Quaternion.Inverse(first));
        Matrix4x4.Decompose(_worldNow[neck], out _, out Quaternion now, out Vector3 at);
        Vector3 turned = Vector3.Transform(inNeck, now);
        return (ModernBodyAsset.ToBody(at + turned, yaw) + new Vector3(0f, 0f, _keptLift), Vector3.Normalize(ModernBodyAsset.ToBody(turned, yaw)));
    }

    /// <summary>Gets how long the fall has played, in seconds.</summary>
    public float FallTime => _fallTime;

    /// <summary>Gets how long the fall is, in seconds.</summary>
    public float FallDuration => _fall?.Duration ?? 0f;

    /// <summary>Gets a value indicating whether the fall is a short one a ragdoll finishes.</summary>
    public bool FallIsBrief { get; private set; }

    /// <summary>Gets the last two ticks' skeletons and how far each was raised, which a ragdoll takes over from.</summary>
    internal (Matrix4x4[] Now, Matrix4x4[] Before, float Lift, float LiftBefore) Kept => (_worldNow, _worldBefore, _keptLift, _keptLiftBefore);

    /// <summary>Takes one tick's skeleton from elsewhere (<see cref="ModernRagdoll"/>), its places already from where he is drawn.</summary>
    /// <param name="write">Writes the skeleton.</param>
    /// <param name="yaw">The character's extra turn about up, in radians.</param>
    internal void TickPosed(Action<Matrix4x4[]> write, float yaw)
    {
        write(_world);
        int[] joints = _asset.JointOfNode;
        for (int node = 0; node < _world.Length; node++)
        {
            if (joints[node] >= 0)
            {
                _skin[joints[node]] = _asset.Joints[joints[node]].InverseBind * _world[node];
            }
        }

        NeedsSkin = !_drawnSinceTick;
        _drawnSinceTick = false;
        Keep(yaw, 0f);
    }

    /// <summary>Gets the lowest of him as last skinned, not his rifle, along up, in the soldier's frame, metres, for the log.</summary>
    internal float LowestSkinned => _vertices.Where((_, v) => !_gunVertices.Contains(v)).Min(v => v.Position.Z);

    private HashSet<int> _gunVertices => _gunVerticesKept ??= [.. _asset.GunVertices];

    private HashSet<int>? _gunVerticesKept;

    /// <summary>Gets where the soldier's feet are in his frame, metres, standing.</summary>
    internal float Floor => _floor;

    // The blends from clip to clip (ModernInertia), and how long since the moving clip last changed.
    private readonly ModernInertia _inertia;
    private float _sinceMoveChange;

    /// <summary>The least time between one moving clip and the next, in seconds: a pace or a heading on a line flipped the
    /// legs between two clips tick after tick.</summary>
    public const float MoveHoldSeconds = 0.25f;

    /// <summary>Poses and skins the body for one tick of the soldier's movement.</summary>
    /// <param name="moved">How far the soldier moved this tick along the ground, in metres.</param>
    /// <param name="yaw">The character's extra turn about up, in radians.</param>
    /// <param name="raise">How far to raise the body, in metres: what the soldier's own clip has lowered where he is drawn.</param>
    /// <param name="aiming">Whether he is in a fight, with his rifle up.</param>
    /// <param name="shots">How many shots he has fired, which a firing clip follows.</param>
    /// <param name="searching">Whether he walks hunting for someone: the cautious walk.</param>
    /// <param name="scanning">Whether, kneeling, he looks about him, rifle up.</param>
    /// <param name="limp">The leg he limps on: -1 his left, 1 his right, 0 neither.</param>
    /// <param name="lean">Which way he leans out from cover: 1 to his right, -1 to his left, 0 not at all.</param>
    /// <param name="runs">Whether he is setting out at or keeping up a run.</param>
    /// <param name="wayLeft">How far is left of his walk, in metres.</param>
    /// <param name="kneeling">Whether he kneels: down out of sight, or with his rifle up when he is also aiming.</param>
    /// <param name="keepsDown">Whether he kneels to hide, his rifle lowered whatever the fight.</param>
    /// <param name="turning">How fast he turns about up, in radians a second, left positive.</param>
    /// <param name="crouched">Whether he moves crouched.</param>
    /// <param name="heading">Which way he moves against the way he faces, forward and right, when he moves facing elsewhere (strafing); zero otherwise.</param>
    /// <param name="travel">How far he moved this tick, in metres, ahead and to his right: what the planted feet are held against.</param>
    /// <param name="look">Where he looks, in the soldier's frame (x right, y ahead, z up), from his eyes; zero for nowhere in
    /// particular. Aiming, his upper body bends to it; always, his head turns to it.</param>
    public void Tick(float moved, float yaw, float raise = 0f, bool aiming = false, int shots = 0, bool kneeling = false, bool keepsDown = false, float turning = 0f,
        bool crouched = false, Vector2 heading = default, Vector2 travel = default, Vector3 look = default, bool searching = false, bool scanning = false, int limp = 0, int lean = 0, bool runs = false, float wayLeft = 0f)
    {
        if (_fall is { } fall)
        {
            // Once it has lain a tick on its last frame, and he is drawn no lower, the mesh already holds it.
            if (_fallTime > fall.Duration + TickSeconds && raise == _fallRaise)
            {
                Keep(yaw, _keptLift);
                return;
            }

            _fallTime += TickSeconds;
            _fallRaise = raise;
            _inertia.Step(TickSeconds);
            SpringKicks();
            _asset.PoseFall(fall, _fallTime, _world, _skin, _inertia, _kick.Length > 0 ? _kick : null);
            CollapseHidden(_world);
            _asset.Skin(_skin, yaw, (Lift ?? 0f) + raise, _vertices);

            // Nothing of him below the floor his feet stood on: a fall swings a hand or a knee lower than the feet were.
            float under = _floor + raise - _vertices.Min(v => v.Position.Z);
            if (under > 0f)
            {
                _asset.Skin(_skin, yaw, (Lift ?? 0f) + raise + under, _vertices);
            }

            ApplySoak();
            _device.UpdateAnimatedMesh(Mesh, _vertices);
            Keep(yaw, (Lift ?? 0f) + raise + Math.Max(0f, under));
            return;
        }

        Kneeling = kneeling;
        bool wasStill = Speed < StandingBelow;
        Speed = moved / TickSeconds;
        bool moving = Speed >= StandingBelow;
        // His legs come in as fast as he does: the soldier is at full speed the tick he sets off, and a quarter second of
        // legs fading in under him at a run is a quarter second of gliding.
        float step = TickSeconds / (moving && Speed > 1f ? BlendSeconds / 4f : BlendSeconds);
        _moveWeight = Math.Clamp(_moveWeight + (moving ? step : -step), 0f, 1f);

        // What he does: a fight brings the rifle up, a shot fires it, and fast enough is a run.
        if (shots != _shotsSeen)
        {
            _shotsSeen = shots;
            _firingFor = FireSeconds;
        }

        _firingFor = Math.Max(0f, _firingFor - TickSeconds);
        _running = moving && (limp != 0 ? Speed > LimpRunAbove : _running ? Speed > RunBelow : Speed > RunAbove);
        bool shot = _firingFor >= FireSeconds - TickSeconds;
        TickRunClip(moving, wasStill, runs, wayLeft, shot, kneeling || crouched || limp != 0 || lean != 0 || heading != Vector2.Zero);
        ModernClip stand = kneeling && scanning && _firingFor <= 0f ? _asset.KneelScan
            : kneeling ? (!keepsDown && (aiming || _firingFor > 0f) ? _asset.KneelAim : _asset.KneelIdle)
            : lean != 0 ? (lean > 0 ? _asset.LeanRight : _asset.LeanLeft)
            : _firingFor > 0f ? _asset.Fire : aiming ? _asset.AimIdle : _asset.Idle;

        // A reload or a throw over whatever he stands in, played once; set off moving, he leaves it.
        if (_act is { } act)
        {
            _actTime += TickSeconds;
            // (A gesture begins the tick he arrives, when he has still moved: only setting off again ends it.)
            if (_actTime >= act.Duration || (moving && !kneeling && !(_gesture && _actTime < GestureGraceSeconds)) || (_gesture && aiming))
            {
                _act = null;
            }
            else
            {
                stand = act;
            }
        }

        // Setting off into his run, or stopping from it.
        if (_runClip is { } running)
        {
            stand = running;
        }

        // Turning where he stands: the steps of a turn clip as big as the turn is fast -- 45, 135 or 180 degrees -- each played on
        // by the angle he turns, so his feet keep to his body whatever the speed. A turn once started is stepped a third of a
        // second at least (a rate over the line for a tick or two flickered the steps in and out), on the biggest clip it has
        // needed, so it does not change clip at every wobble of the rate.
        bool canTurn = _act is null && _runClip is null && !kneeling && !moving && !ReferenceEquals(_asset.TurnLeft, ModernClip.None);
        if (canTurn && Math.Abs(turning) > TurnAboveRadiansPerSecond)
        {
            if (_turnFor <= 0f || _turnLeft != turning > 0f)
            {
                (_turnClip, _turnRate) = (null, 0f);
            }

            (_turnFor, _turnLeft) = (TurnHoldSeconds, turning > 0f);
            _turnRate = Math.Max(_turnRate, Math.Abs(turning));
        }

        _turnFor = canTurn ? Math.Max(0f, _turnFor - TickSeconds) : 0f;
        if (_turnFor > 0f)
        {
            (ModernClip clip, float angle) = _asset.TurnFor(_turnLeft, _turnRate);
            if (!ReferenceEquals(clip, _turnClip))
            {
                // A bigger clip as the turn quickens, from the same point of its step.
                _turnTime = _turnClip is { } was ? _turnTime / was.Duration * clip.Duration : 0f;
                _turnClip = clip;
            }

            _turnTime += Math.Max(Math.Abs(turning), TurnAboveRadiansPerSecond) * TickSeconds * clip.Duration / angle;
        }

        // The steps come in and go out over a few ticks, under the stance he keeps.
        _turnWeight = Math.Clamp(_turnWeight + ((_turnFor > 0f ? 1f : -1f) * TickSeconds / TurnBlendSeconds), 0f, 1f);

        // Crouched, his crouched walk; facing elsewhere than he goes, the clip for the way he goes against his facing.
        ModernClip limping = limp == 0 || crouched || heading != Vector2.Zero ? ModernClip.None
            : (_running ? _asset.LimpRun : _asset.LimpWalk)[limp > 0 ? 1 : 0];
        ModernClip move = _runClip is not null ? _asset.Run
            : !ReferenceEquals(limping, ModernClip.None) ? limping
            : searching ? _asset.SearchWalk
            : crouched ? _asset.CrouchWalk
            : heading != Vector2.Zero ? (Math.Abs(heading.X) >= Math.Abs(heading.Y)
                ? (heading.X >= 0f ? _asset.AimWalk : _asset.WalkBack)
                : (heading.Y > 0f ? _asset.StrafeRight : _asset.StrafeLeft))
            : _running ? _asset.Run : aiming ? _asset.AimWalk : _asset.Walk;
        // The moving clip held a moment once changed; its weight is the walk's, so a standing body does not wait on it.
        _sinceMoveChange += TickSeconds;
        if (!ReferenceEquals(move, _move) && _sinceMoveChange < MoveHoldSeconds && _moveWeight > 0f)
        {
            move = _move;
        }

        if (!ReferenceEquals(stand, _stand) || !ReferenceEquals(move, _move))
        {
            // From what was showing, by inertialization: the body carries on from where it was (ModernInertia).
            _sinceMoveChange = ReferenceEquals(move, _move) ? _sinceMoveChange : 0f;
            (_stand, _move) = (stand, move);
            _inertia.Begin = true;
        }

        // The moving clip is played at the soldier's pace, so its feet keep to the ground he covers; a change of gait keeps the
        // stride where it was.
        float pace = moving && move.MetresPerSecond > 0f
            ? Math.Clamp(Speed / move.MetresPerSecond, SlowestPace, FastestPace)
            : 1f;
        _moveWeight = _runClip is not null ? 0f : _moveWeight;
        _stride = (_stride + (TickSeconds * pace / move.Duration)) % 1f;
        Showing = FormattableString.Invariant(
            $"speed {Speed:0.00} m/s, stand {Name(stand)}, move {Name(move)} x{Smooth(_moveWeight):0.00} at pace {pace:0.00}, legs {(_turnClip is { } legs && _turnWeight > 0f ? Name(legs) : "-")} x{Smooth(_turnWeight):0.00}");
        _standTime += TickSeconds;

        float hitWeight = 0f;
        if (_hit is { } hit)
        {
            _hitTime += TickSeconds;
            hitWeight = Smooth(Math.Clamp(Math.Min(_hitTime / HitInSeconds, (hit.Duration - _hitTime) / HitOutSeconds), 0f, 1f));
            if (_hitTime >= hit.Duration)
            {
                _hit = null;
            }
        }

        SpringKicks();
        ModernMix mix = Mix(_stand, _move);
        _inertia.Step(TickSeconds);
        _asset.Pose(mix, mix, 1f, _world, _skin, _hit, _hitTime, hitWeight, _kick.Length > 0 ? _kick : null, _inertia, keepLegs: kneeling);
        Look(look, aiming && _act is null);

        // The rifle rides its bone, where the clip's hands carried it with its stock seated in the shoulder (baked in the
        // build); the hands are brought back to it -- the right to its grip, the left to its handguard -- where they are near
        // enough to be meant to be on it, the left except while a reload or a throw has it away on purpose.
        if (_asset.Weapon >= 0 && _asset.LeftArm.Wrist >= 0 && _asset.RightArm.Wrist >= 0)
        {
            Matrix4x4 weapon = _world[_asset.Weapon];
            Vector3 right = Vector3.Transform(_rightGrip, weapon);
            GripsForTesting = (_world[_asset.RightArm.Wrist].Translation, right, _world[_asset.LeftArm.Wrist].Translation,
                Vector3.Transform(_leftGrip, weapon), weapon.Translation);
            if (Vector3.Distance(right, _world[_asset.RightArm.Wrist].Translation) < LeftReachMetres)
            {
                ReachHand(_asset.RightArm, right);
            }

            Vector3 left = Vector3.Transform(_leftGrip, weapon);
            if (_act is null && Vector3.Distance(left, _world[_asset.LeftArm.Wrist].Translation) < LeftReachMetres)
            {
                ReachHand(_asset.LeftArm, left);
            }
        }

        // Only turning on the spot: going along, the clip's own feet keep to the ground at his pace, to under half a centimetre a tick at a
        // walk, and the leg pulled to a held foot and let go again was a hitch in the knee and the foot at every step, twice what the clip has.
        LockFeet(travel, turning * TickSeconds, kneeling || moving || _act is not null || _runClip is not null);

        // Drawn every frame (DrawAt), the tick only poses: skinning here too was thrown away by the next frame, and landed on
        // the tick's frame all at once -- a frame in three twice as long as the others, a hitch thirty times a second. Where no
        // smoothed frame has come since the last tick he is marked to be skinned as he is drawn (SkinIfStale). Skinning him here
        // instead cost 0.4 ms a body on one core: at 99 bodies a tick outran its slot, the clock ran ten ticks to catch up, each
        // skinning them all, and a frame took 400 ms.
        NeedsSkin = !_drawnSinceTick;
        _drawnSinceTick = false;

        Keep(yaw, (Lift ?? 0f) + raise);
    }

    /// <summary>The most his upper body bends to aim up or down, and his head turns aside and up or down beyond it, in radians;
    /// and how long a look takes to come in or go, in seconds.</summary>
    public const float AimPitchMost = 0.9f, HeadYawMost = 1.2f, HeadPitchMost = 0.6f, LookBlendSeconds = 0.3f;

    // How far into a look he is, nought to one, and the way he looks, eased toward the one asked.
    private float _lookWeight;
    private Vector3 _lookWay = new(0f, 1f, 0f);

    /// <summary>Gets the angles his look last turned him by, in radians: the spine's pitch, and the head's yaw and pitch, for testing.</summary>
    internal (float SpinePitch, float HeadYaw, float HeadPitch) LookForTesting { get; private set; }

    /// <summary>
    /// Turns him to what he looks at: aiming, the spine bent over three bones so the rifle -- its bone on the chest -- comes up
    /// or down onto it with the arms, at a target above or below him; and the neck and head turned the rest of the way, aside
    /// and up or down, whatever he does. The clips aim level, so on stairs or a slope he fired level with the rifle pointed off
    /// the man he was shooting.
    /// </summary>
    private void Look(Vector3 look, bool aiming)
    {
        (int spine, int spine1, int spine2, int neck, int head) = _asset.Torso;
        if (spine < 0 || spine1 < 0 || spine2 < 0 || neck < 0 || head < 0)
        {
            return;
        }

        bool looking = look.LengthSquared() > 1e-6f;
        if (looking)
        {
            Vector3 way = Vector3.Normalize(look);
            _lookWay = _lookWeight <= 0f ? way : Vector3.Normalize(Vector3.Lerp(_lookWay, way, 0.35f));
        }

        _lookWeight = Math.Clamp(_lookWeight + ((looking ? 1f : -1f) * TickSeconds / LookBlendSeconds), 0f, 1f);
        float weight = Smooth(_lookWeight);
        if (weight <= 0f)
        {
            LookForTesting = default;
            return;
        }

        // In the soldier's frame: up-down from level, and aside from ahead (right positive).
        float level = MathF.Sqrt((_lookWay.X * _lookWay.X) + (_lookWay.Y * _lookWay.Y));
        float elevation = MathF.Atan2(_lookWay.Z, level);
        float aside = MathF.Atan2(_lookWay.X, _lookWay.Y);

        // glTF's world here: x his left, y up, z ahead. A pitch up is about his right (-x); a turn right is about -y.
        Vector3 right = new(-1f, 0f, 0f);
        float spinePitch = aiming ? Math.Clamp(elevation, -AimPitchMost, AimPitchMost) * weight : 0f;
        if (spinePitch != 0f)
        {
            Turn(spine, Quaternion.CreateFromAxisAngle(right, spinePitch * 0.3f));
            Turn(spine1, Quaternion.CreateFromAxisAngle(right, spinePitch * 0.3f));
            Turn(spine2, Quaternion.CreateFromAxisAngle(right, spinePitch * 0.4f));
        }

        float headYaw = Math.Clamp(aside, -HeadYawMost, HeadYawMost) * weight;
        float headPitch = Math.Clamp(elevation - spinePitch, -HeadPitchMost, HeadPitchMost) * weight;
        foreach ((int node, float share) in new[] { (neck, 0.4f), (head, 0.6f) })
        {
            Quaternion turn = Quaternion.CreateFromAxisAngle(new Vector3(0f, -1f, 0f), headYaw * share);
            Vector3 turnedRight = Vector3.Transform(right, turn);
            Turn(node, Quaternion.Concatenate(turn, Quaternion.CreateFromAxisAngle(turnedRight, headPitch * share)));
        }

        LookForTesting = (spinePitch, headYaw, headPitch);
    }

    /// <summary>How near its lowest an ankle is to count as on the ground, in metres; how far a held foot may get from where the
    /// clip puts it before it is let go, in metres; and how long a hold takes to come in or go, in seconds.</summary>
    public const float FootDownMetres = 0.03f, FootSlipMetres = 0.3f, FootBlendSeconds = 0.08f;

    // Each foot's place held on the ground, in glTF's world, how far it is held, and the ankle's lowest in the clips.
    private readonly Vector3[] _footHeld = new Vector3[2];
    private readonly float[] _footHold = new float[2];
    private readonly bool[] _footLocked = new bool[2];
    private float _ankleLowest = float.NaN;

    /// <summary>Gets an ankle's place in the body's glTF frame as last posed, for testing.</summary>
    internal float HeadHeightForTesting => _world[_asset.Torso.Head].Translation.Y;

    internal float HeadYawForTesting
    {
        get
        {
            int head = _asset.Torso.Head;
            int joint = Array.FindIndex(_asset.Joints, j => j.Node == head);
            Vector3 f = Vector3.TransformNormal(Vector3.UnitZ, _skin[joint]);
            return MathF.Atan2(-f.X, f.Z);
        }
    }

    internal Vector3 AnkleForTesting(int foot) => _world[_asset.Legs[foot].Ankle].Translation;

    /// <summary>Gets how far each foot is held to the ground now, left then right, nought to one, for testing.</summary>
    internal (float Left, float Right) FootHoldForTesting => (_footHold[0], _footHold[1]);

    /// <summary>
    /// Holds each foot where it came down while the clip has it down, the leg bent to keep it there, so it does not slide over
    /// the ground as the clip's stride and his own pace or turn disagree -- a strafe at two thirds of its pace, a crouched walk at
    /// twice its, a curve at a run. The held place moves against him as he moves and turns, as the ground does. Let go when the
    /// clip lifts the foot or it would stretch too far, eased in and out so the foot does not snap.
    /// </summary>
    private void LockFeet(Vector2 step, float turned, bool off)
    {
        (int Hip, int Knee, int Ankle)[] legs = _asset.Legs;
        if (legs.Any(l => l.Hip < 0 || l.Knee < 0 || l.Ankle < 0))
        {
            return;
        }

        if (float.IsNaN(_ankleLowest))
        {
            _ankleLowest = legs.Min(l => _world[l.Ankle].Translation.Y);
        }

        // The ground's move against him this tick, in glTF's world (x his left, y up, z ahead): back by his step, and round
        // the other way by his turn.
        Vector3 back = ModernBodyAsset.FromBody(new Vector3(-step.Y, -step.X, 0f));
        Matrix4x4 unturn = Matrix4x4.CreateRotationY(-turned);
        float blend = TickSeconds / FootBlendSeconds;
        for (int foot = 0; foot < 2; foot++)
        {
            (int hip, int knee, int ankle) = legs[foot];
            Vector3 clip = _world[ankle].Translation;
            bool down = !off && clip.Y < _ankleLowest + FootDownMetres && (step != Vector2.Zero || turned != 0f);
            if (_footLocked[foot])
            {
                _footHeld[foot] = Vector3.Transform(_footHeld[foot] + back, unturn);
                _footHeld[foot].Y = clip.Y;
                if (!down || Vector3.Distance(_footHeld[foot], clip) > FootSlipMetres)
                {
                    _footLocked[foot] = false;
                }
            }
            else if (down)
            {
                (_footLocked[foot], _footHeld[foot]) = (true, clip);
            }

            _footHold[foot] = Math.Clamp(_footHold[foot] + (_footLocked[foot] ? blend : -blend), 0f, 1f);
            if (_footHold[foot] > 0f)
            {
                ReachHand((hip, knee, ankle), Vector3.Lerp(clip, _footHeld[foot], Smooth(_footHold[foot])));
            }
        }
    }

    // Whether a frame has posed and skinned this body (DrawAt) since the last tick, so this one need not.
    private bool _drawnSinceTick;

    // The skeleton as the last two ticks posed it, and their turn and lift, which every drawn frame between them blends.
    private Matrix4x4[] _worldBefore = [], _worldNow = [], _worldDrawn = [];
    private float _keptYaw, _keptYawBefore, _keptLift, _keptLiftBefore;

    // This tick's finished skeleton, the last one's kept before it.
    private void Keep(float yaw, float lift)
    {
        if (_worldNow.Length != _world.Length)
        {
            (_worldBefore, _worldNow, _worldDrawn) = ((Matrix4x4[])_world.Clone(), (Matrix4x4[])_world.Clone(), new Matrix4x4[_world.Length]);
            (_keptYawBefore, _keptLiftBefore) = (yaw, lift);
        }
        else
        {
            (_worldBefore, _worldNow) = (_worldNow, _worldBefore);
            Array.Copy(_world, _worldNow, _world.Length);
            (_keptYawBefore, _keptLiftBefore) = (_keptYaw, _keptLift);
        }

        (_keptYaw, _keptLift) = (yaw, lift);
    }

    /// <summary>
    /// Poses and skins the body for a frame drawn between ticks: every joint blended between the last two ticks' skeletons by
    /// how far between them the frame is, as the soldier's place and facing are. Posed only each tick, his limbs stepped at
    /// thirty a second under a body that glided at the display's rate -- a run was the worst of it.
    /// </summary>
    /// <param name="alpha">How far the frame is from the tick before the last to the last, nought to one.</param>
    public void DrawAt(float alpha)
    {
        if (SkinAt(alpha))
        {
            Upload();
        }
    }

    /// <summary>Gets a value indicating whether a tick has posed him and no frame has skinned him since.</summary>
    public bool NeedsSkin { get; private set; }

    /// <summary>Skins him at his latest tick where no smoothed frame has, for the view about to draw him.</summary>
    public void SkinIfStale()
    {
        if (NeedsSkin)
        {
            DrawAt(1f);
        }
    }

    /// <summary>Leaves the body as it was for this frame: not wanted now, and no reason for the tick to skin him instead.</summary>
    public void SkipFrame() => _drawnSinceTick = _worldNow.Length > 0;

    /// <summary>The first half of <see cref="DrawAt"/>: the blend and the skinning, which touch only this body and so run
    /// beside other bodies' on other threads.</summary>
    /// <returns>Whether there was a pose to skin.</returns>
    public bool SkinAt(float alpha)
    {
        if (_worldNow.Length == 0)
        {
            return false;
        }

        _drawnSinceTick = true;
        NeedsSkin = false;

        for (int node = 0; node < _worldNow.Length; node++)
        {
            Matrix4x4 a = _worldBefore[node], b = _worldNow[node];
            if (Matrix4x4.Decompose(a, out Vector3 sa, out Quaternion ra, out Vector3 ta) && Matrix4x4.Decompose(b, out Vector3 sb, out Quaternion rb, out Vector3 tb))
            {
                _worldDrawn[node] = Matrix4x4.CreateScale(Vector3.Lerp(sa, sb, alpha)) * Matrix4x4.CreateFromQuaternion(Quaternion.Slerp(ra, rb, alpha))
                    * Matrix4x4.CreateTranslation(Vector3.Lerp(ta, tb, alpha));
            }
            else
            {
                _worldDrawn[node] = b;
            }
        }

        foreach ((int joint, (int node, Matrix4x4 inverseBind)) in _asset.Joints.Select((j, i) => (i, j)))
        {
            _skin[joint] = inverseBind * _worldDrawn[node];
        }

        CollapseHidden(_worldDrawn);
        float yaw = _keptYawBefore + ((_keptYaw - _keptYawBefore) * alpha);
        float lift = _keptLiftBefore + ((_keptLift - _keptLiftBefore) * alpha);
        _asset.Skin(_skin, yaw, lift, _vertices);
        ApplySoak();
        return true;
    }

    /// <summary>The second half of <see cref="DrawAt"/>: the skinned mesh to the device, on the thread that owns it.</summary>
    public void Upload() => _device.UpdateAnimatedMesh(Mesh, _vertices);

    /// <summary>Gets a value indicating whether he was kneeling when last posed alive.</summary>
    public bool Kneeling { get; private set; }

    /// <summary>Gets what the body last showed, for the body log (<c>dev_body_log</c>).</summary>
    public string Showing { get; private set; } = string.Empty;

    // A clip's name in the character file, for the log.
    private string Name(ModernClip clip) => _asset.NameOf(clip);

    /// <summary>Gets where the rifle's muzzle and the end of its stock are, in the soldier's frame, metres, as last posed; null without one.</summary>
    public (Vector3 Muzzle, Vector3 Stock)? GunEnds
    {
        get
        {
            (int muzzle, int stock) = _asset.GunEnds;
            if (muzzle < 0)
            {
                return null;
            }

            SkinIfStale();

            Vector3f m = _vertices[muzzle].Position, s = _vertices[stock].Position;
            return (new Vector3(m.X, m.Y, m.Z), new Vector3(s.X, s.Y, s.Z));
        }
    }

    /// <summary>
    /// Meets a line with the body as it was last skinned -- the triangles drawn -- in the soldier's frame, metres: the nearest one it
    /// crosses either way, not the rifle's. What the player sees is what a round meets.
    /// </summary>
    /// <param name="from">Where the line starts.</param>
    /// <param name="to">Where it ends.</param>
    /// <param name="along">How far along it the triangle is, nought to one.</param>
    /// <param name="part">The original rig's body part the triangle is a piece of (<see cref="ModernBodyAsset.TriangleParts"/>).</param>
    /// <param name="normal">The triangle's normal, turned against the line.</param>
    /// <returns>Whether it met one.</returns>
    public bool TryMeet(Vector3 from, Vector3 to, out float along, out int part, out Vector3 normal)
    {
        SkinIfStale();
        (along, part, normal) = (float.MaxValue, -1, Vector3.Zero);
        ushort[] indices = _asset.Indices;
        int[] parts = _asset.TriangleParts;
        Vector3 d = to - from;
        for (int t = 0; t < parts.Length; t++)
        {
            if (parts[t] < 0)
            {
                continue;
            }

            // Möller and Trumbore's: the line against the triangle's two edges.
            Vector3 a = Point(indices[t * 3]), e1 = Point(indices[(t * 3) + 1]) - a, e2 = Point(indices[(t * 3) + 2]) - a;
            Vector3 p = Vector3.Cross(d, e2);
            float det = Vector3.Dot(e1, p);
            if (MathF.Abs(det) < 1e-12f)
            {
                continue;
            }

            Vector3 s = (from - a) / det;
            float u = Vector3.Dot(s, p);
            if (u is < 0f or > 1f)
            {
                continue;
            }

            Vector3 q = Vector3.Cross(s, e1);
            float v = Vector3.Dot(d, q), at = Vector3.Dot(e2, q);
            if (v < 0f || u + v > 1f || at < 0f || at > 1f || at >= along)
            {
                continue;
            }

            Vector3 n = Vector3.Normalize(Vector3.Cross(e1, e2));
            (along, part, normal) = (at, parts[t], Vector3.Dot(n, d) > 0f ? -n : n);
        }

        return part >= 0;
    }

    private Vector3 Point(int vertex)
    {
        Vector3f p = _vertices[vertex].Position;
        return new Vector3(p.X, p.Y, p.Z);
    }

    // A pair of clips where the body is in them now.
    private ModernMix Mix(ModernClip stand, ModernClip move) =>
        new(stand, ReferenceEquals(stand, _runClip) ? _runClipTime : ReferenceEquals(stand, _act) ? _actTime : _standTime, move, _stride * move.Duration, Smooth(_moveWeight),
            _turnClip, _turnTime, Smooth(_turnWeight));

    /// <summary>Above this turn rate, in radians a second, a soldier standing still steps his turn.</summary>
    public const float TurnAboveRadiansPerSecond = 0.6f;

    /// <summary>The furthest a hand is brought back to the rifle from, in metres; further, the clip has it off it.</summary>
    public const float LeftReachMetres = 0.25f;

    /// <summary>Gets the wrists and where they were reached to, and the weapon bone, last tick, in glTF's world (testing).</summary>
    internal (Vector3 RightWrist, Vector3 RightTarget, Vector3 LeftWrist, Vector3 LeftTarget, Vector3 Weapon) GripsForTesting { get; private set; }

    // The wrists' places in the weapon bone's frame (see the constructor).
    private Vector3 _leftGrip, _rightGrip;

    private ModernClip? _act;
    private float _actTime;

    /// <summary>How much way he must have left to set off with a run start, in metres: enough for the start and a stop. How near the end of it a stop may begin
    /// when his stride comes to one, and how near a stop begins whatever his stride; further than the last from his end, a stop he is playing is called off.</summary>
    public const float RunStartNeedsMetres = 2.2f, RunStopFromMetres = 1.35f, RunStopByMetres = 0.55f, RunStopOffMetres = 2.5f;

    // The run start or stop he is playing, and how far through it.
    private ModernClip? _runClip;
    private float _runClipTime;

    // Starts, plays on and ends a run start or stop. It is played at his own pace, so its feet keep to the ground he covers, and at
    // its own once he has stopped; anything else he comes to do ends it, carried over by the inertial blend.
    private void TickRunClip(bool moving, bool wasStill, bool runs, float wayLeft, bool shot, bool otherGait)
    {
        ModernClip start = _asset.RunStart, left = _asset.RunStopLeft, right = _asset.RunStopRight;
        if (ReferenceEquals(start, ModernClip.None) || ReferenceEquals(left, ModernClip.None) || ReferenceEquals(right, ModernClip.None))
        {
            return;
        }

        if (_runClip is null)
        {
            if (otherGait || _act is not null)
            {
                return;
            }

            if (wasStill && moving && runs && wayLeft >= RunStartNeedsMetres)
            {
                BeginRunClip(start);
            }
            else if (_running && runs && wayLeft > 0f && wayLeft <= RunStopFromMetres)
            {
                // The foot his stride is coming to; by the last of his way, the nearer.
                float reach = TickSeconds * Speed / Math.Max(_asset.Run.MetresPerSecond, 0.1f) / _asset.Run.Duration;
                float toLeft = StrideTo(ModernBodyAsset.RunStopLeftFrom), toRight = StrideTo(ModernBodyAsset.RunStopRightFrom);
                if (Math.Min(toLeft, toRight) <= reach || wayLeft <= RunStopByMetres)
                {
                    BeginRunClip(toLeft <= toRight ? left : right);
                }
            }

            return;
        }

        bool isStart = ReferenceEquals(_runClip, start);
        bool off = otherGait || _act is not null || (isStart ? !moving || !runs : shot || (moving && wayLeft > RunStopOffMetres) || (wasStill && moving));
        if (off)
        {
            EndRunClip(moving, _stride);
            return;
        }

        // At his own pace going along, by how fast the clip's root goes here; at the clip's own otherwise.
        float rate = 1f;
        float clipSpeed = Vector3.Distance(_runClip.TravelAt(_runClipTime + TickSeconds), _runClip.TravelAt(_runClipTime)) * _asset.RunMetresPerTravel / TickSeconds;
        if (moving && clipSpeed > 0.3f)
        {
            rate = Math.Clamp(Speed / clipSpeed, 0.6f, 1.6f);
        }

        _runClipTime += TickSeconds * rate;
        if (_runClipTime >= _runClip.Duration)
        {
            EndRunClip(moving, isStart ? ModernBodyAsset.RunStartMeets : _stride);
        }
    }

    // How far on in the stride a place in it is, as a share of the stride.
    private float StrideTo(float place) => ((place - _stride) + 1f) % 1f;

    private void BeginRunClip(ModernClip clip)
    {
        (_runClip, _runClipTime, _moveWeight) = (clip, 0f, 0f);
        _inertia.Begin = true;
    }

    private void EndRunClip(bool moving, float stride)
    {
        (_runClip, _stride, _standTime) = (null, stride, 0f);
        _moveWeight = moving ? 1f : 0f;
        _inertia.Begin = true;
    }

    /// <summary>Gets the run start or stop he is playing and how far through it, for testing.</summary>
    internal (ModernClip? Clip, float Time) RunClipForTesting => (_runClip, _runClipTime);
    private float _turnTime;
    private float _turnFor;
    private bool _turnLeft;
    private float _turnRate;
    private ModernClip? _turnClip;
    private float _turnWeight;

    /// <summary>How long a turn's steps take to come in under his stance, and to go, in seconds.</summary>
    public const float TurnBlendSeconds = 0.12f;

    /// <summary>Gets how far through its length the turn clip is, as a fraction, for testing.</summary>
    internal float TurnPhaseForTesting => _turnClip is { } clip ? _turnTime / clip.Duration : 0f;

    /// <summary>How long a turn on the spot is stepped at least, in seconds.</summary>
    public const float TurnHoldSeconds = 0.35f;

    /// <summary>Plays a reload or a throw once over what he stands in, the rifle in his right hand meanwhile.</summary>
    /// <param name="clip">The clip; <see cref="ModernClip.None"/> plays nothing.</param>
    public void Act(ModernClip clip)
    {
        if (!ReferenceEquals(clip, ModernClip.None))
        {
            (_act, _actTime, _gesture) = (clip, 0f, false);
        }
    }

    /// <summary>Plays a patrol stop's clip once over what he stands in: unlike <see cref="Act"/>, his rifle coming up ends it.</summary>
    /// <param name="clip">The clip.</param>
    public void Gesture(ModernClip clip) => (_act, _actTime, _gesture) = (clip, 0f, true);

    /// <summary>Ends a patrol stop's clip, if one is playing.</summary>
    public void EndGesture()
    {
        if (_gesture)
        {
            (_act, _gesture) = (null, false);
        }
    }

    /// <summary>How long a gesture's first moments ignore his moving, in seconds.</summary>
    public const float GestureGraceSeconds = 0.5f;

    /// <summary>Works out how fast a character's walk clip covers ground, in metres a second, as its skeleton scales it.</summary>
    /// <remarks>Run once, as the character loads (<see cref="ModernBodyAsset.WalkMetresPerSecond"/>).</remarks>
    /// <param name="asset">The character.</param>
    /// <returns>The pace.</returns>
    /// <param name="walk">The moving clip.</param>
    public static float WalkSpeed(ModernBodyAsset asset, ModernClip walk)
    {
        int root = walk.TravelNode;
        int parent = root >= 0 ? asset.Parents[root] : -1;
        if (parent < 0)
        {
            return walk.Travel.Length() / walk.Duration;
        }

        // The travel is in the root's parent's frame -- the armature's, in its authoring units -- carried to metres by it.
        Matrix4x4[] world = new Matrix4x4[asset.Rest.Length];
        Matrix4x4[] skin = new Matrix4x4[asset.Joints.Length];
        asset.Pose(0f, 0f, 0f, world, skin);
        return Vector3.TransformNormal(walk.Travel, world[parent]).Length() / walk.Duration;
    }

    /// <summary>Measures the lift: the soldier's lowest point against the character's, both in the soldier's frame.</summary>
    /// <param name="soldierLowest">The soldier's own mesh's lowest point along up, in metres.</param>
    public void MatchLowest(float soldierLowest)
    {
        float lowest = float.MaxValue;
        foreach (Vertex3D vertex in _vertices)
        {
            lowest = Math.Min(lowest, vertex.Position.Z - (Lift ?? 0f));
        }

        Lift = soldierLowest - lowest;
        _floor = soldierLowest;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Mesh.IsValid)
        {
            _device.DestroyMesh(Mesh);
            Mesh = default;
        }
    }

    // Eased, so the change of gait starts and ends gently.
    private static float Smooth(float t) => t * t * (3f - (2f * t));
}
