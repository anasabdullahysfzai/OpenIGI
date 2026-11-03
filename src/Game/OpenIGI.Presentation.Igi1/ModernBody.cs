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

    private const float TickSeconds = 1f / 30f;

    private readonly ModernBodyAsset _asset;
    private readonly IGraphicsDevice _device;
    private readonly Matrix4x4[] _world;
    private readonly Matrix4x4[] _skin;
    private readonly Vertex3D[] _vertices;
    private float _idleTime;
    private float _walkTime;
    private float _walkWeight;
    private ModernClip? _fall;
    private float _fallTime;
    private float _fallRaise = float.NaN;
    private float _floor;
    private ModernClip? _hit;
    private float _yaw;
    private float _lift;
    private float _hitTime;

    /// <summary>Initializes a new instance of the <see cref="ModernBody"/> class, its mesh in the bind pose.</summary>
    /// <param name="asset">The character.</param>
    /// <param name="device">The device its mesh lives on.</param>
    public ModernBody(ModernBodyAsset asset, IGraphicsDevice device)
    {
        _asset = asset;
        _device = device;
        _world = new Matrix4x4[asset.Rest.Length];
        _skin = new Matrix4x4[asset.Joints.Length];
        _vertices = new Vertex3D[asset.Vertices.Length];
        asset.Pose(0f, 0f, 0f, _world, _skin);
        asset.Skin(_skin, 0f, 0f, _vertices);
        Mesh = device.CreateAnimatedMesh(_vertices, asset.Indices);
        ReadHands(0f, 0f);
        Reference = (Grip.Position, Hand(asset.LeftHand, 0f, 0f), Grip.Rotation);
    }

    /// <summary>Gets where the right hand is and how it is turned, in the soldier's frame, metres, as last posed; the gun rides it.</summary>
    public (Vector3 Position, Matrix3f Rotation) Grip { get; private set; }

    /// <summary>Gets both hands and the right one's turn in the idle's first frame, which a gun is fitted to (<see cref="FitGun"/>).</summary>
    public (Vector3 Right, Vector3 Left, Matrix3f Rotation) Reference { get; }

    /// <summary>Gets where the left hand is, in the soldier's frame, metres, as last posed.</summary>
    public Vector3 Left => Hand(_asset.LeftHand, _yaw, _lift);

    /// <summary>Gets how the last gun was fitted (<see cref="FitGun"/>): the model's forward and up in the gun's frame, and the axes taken.</summary>
    public string LastFit { get; private set; } = string.Empty;

    /// <summary>Gets a value indicating whether the character has hands to hold a gun in.</summary>
    public bool HasHands => _asset.RightHand >= 0 && _asset.LeftHand >= 0;

    /// <summary>
    /// Works out how a gun sits in the right hand, from how the soldier's own model holds it: its barrel along from the right hand
    /// to the left in the idle, its top up. The gun's own barrel and top are its axes nearest the model's forward and up.
    /// </summary>
    /// <param name="modelHand">The soldier's own model's gun hand, which the gun is drawn turned by, in the soldier's frame.</param>
    /// <returns>The turn from the right hand to the gun, to be put after <see cref="Grip"/>'s.</returns>
    public Matrix3f FitGun(Matrix3f modelHand)
    {
        // Matrix3f is M . v here, as the drawer applies it: a column is where a local axis goes.
        Vector3 barrelLocal = Snap(Times(modelHand.Transposed(), Vector3.UnitY), Vector3.Zero);
        Vector3 topLocal = Snap(Times(modelHand.Transposed(), Vector3.UnitZ), barrelLocal);
        Vector3 sideLocal = Vector3.Cross(barrelLocal, topLocal);
        LastFit = FormattableString.Invariant(
            $"{Times(modelHand.Transposed(), Vector3.UnitY):0.00} -> {barrelLocal}, top {Times(modelHand.Transposed(), Vector3.UnitZ):0.00} -> {topLocal}");

        Vector3 barrel = Vector3.Normalize(Reference.Left - Reference.Right);
        Vector3 top = Vector3.Normalize(Vector3.UnitZ - (Vector3.Dot(Vector3.UnitZ, barrel) * barrel));
        Vector3 side = Vector3.Cross(barrel, top);

        Matrix3f gun = Columns(barrel, top, side) * Columns(barrelLocal, topLocal, sideLocal).Transposed();
        return Reference.Rotation.Transposed() * gun;
    }

    /// <summary>Gets the mesh the body is drawn from.</summary>
    public MeshHandle Mesh { get; private set; }

    /// <summary>Gets the character's runs of triangles and their textures.</summary>
    public (int IndexOffset, int IndexCount, TextureHandle Diffuse)[] Subsets => _asset.Subsets;

    /// <summary>Gets or sets how far to raise the body, in metres, so its lowest point meets the soldier's; null until measured.</summary>
    public float? Lift { get; set; }

    /// <summary>Gets the ground speed the body was last posed for, in metres a second.</summary>
    public float Speed { get; private set; }

    /// <summary>How long a flinch takes to come in, and to go out at its end, in seconds.</summary>
    public const float HitInSeconds = 0.1f, HitOutSeconds = 0.3f;

    /// <summary>Starts a flinch over whatever the body is doing; a new one replaces one still playing.</summary>
    /// <param name="hit">The flinch (<see cref="ModernBodyAsset.Hit"/>).</param>
    public void Flinch(ModernClip hit)
    {
        _hit = hit;
        _hitTime = 0f;
    }

    /// <summary>Gets a value indicating whether the body is falling, or lying where it fell.</summary>
    public bool Fallen => _fall is not null;

    /// <summary>Starts the body's fall, which every tick after plays once and then holds.</summary>
    /// <param name="fall">The fall (<see cref="ModernBodyAsset.Death"/>).</param>
    public void Fall(ModernClip fall)
    {
        _fall = fall;
        _fallTime = 0f;
    }

    /// <summary>Poses and skins the body for one tick of the soldier's movement.</summary>
    /// <param name="moved">How far the soldier moved this tick along the ground, in metres.</param>
    /// <param name="yaw">The character's extra turn about up, in radians.</param>
    /// <param name="raise">How far to raise the body, in metres: what the soldier's own clip has lowered where he is drawn.</param>
    public void Tick(float moved, float yaw, float raise = 0f)
    {
        if (_fall is { } fall)
        {
            // Once it has lain a tick on its last frame, and he is drawn no lower, the mesh already holds it.
            if (_fallTime > fall.Duration + TickSeconds && raise == _fallRaise)
            {
                return;
            }

            _fallTime += TickSeconds;
            _fallRaise = raise;
            _asset.PoseFall(fall, _fallTime, _world, _skin);
            _asset.Skin(_skin, yaw, (Lift ?? 0f) + raise, _vertices);

            // Nothing of him below the floor his feet stood on: a fall swings a hand or a knee lower than the feet were.
            float under = _floor + raise - _vertices.Min(v => v.Position.Z);
            if (under > 0f)
            {
                _asset.Skin(_skin, yaw, (Lift ?? 0f) + raise + under, _vertices);
            }

            _device.UpdateAnimatedMesh(Mesh, _vertices);
            return;
        }

        Speed = moved / TickSeconds;
        bool walking = Speed >= StandingBelow;
        float step = TickSeconds / BlendSeconds;
        _walkWeight = Math.Clamp(_walkWeight + (walking ? step : -step), 0f, 1f);

        // The walk is played at the soldier's pace, so its feet keep to the ground he covers.
        float pace = walking && _asset.WalkMetresPerSecond > 0f
            ? Math.Clamp(Speed / _asset.WalkMetresPerSecond, SlowestPace, FastestPace)
            : 1f;
        _walkTime += TickSeconds * pace;
        _idleTime += TickSeconds;

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

        _asset.Pose(_idleTime, _walkTime, Smooth(_walkWeight), _world, _skin, _hit, _hitTime, hitWeight);
        _asset.Skin(_skin, yaw, (Lift ?? 0f) + raise, _vertices);
        ReadHands(yaw, (Lift ?? 0f) + raise);
        _device.UpdateAnimatedMesh(Mesh, _vertices);
    }

    /// <summary>Works out how fast a character's walk clip covers ground, in metres a second, as its skeleton scales it.</summary>
    /// <remarks>Run once, as the character loads (<see cref="ModernBodyAsset.WalkMetresPerSecond"/>).</remarks>
    /// <param name="asset">The character.</param>
    /// <returns>The pace.</returns>
    public static float WalkSpeed(ModernBodyAsset asset)
    {
        ModernClip walk = asset.Walk;
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

    private static Vector3 Times(Matrix3f m, Vector3 v) =>
        new((m.M11 * v.X) + (m.M12 * v.Y) + (m.M13 * v.Z), (m.M21 * v.X) + (m.M22 * v.Y) + (m.M23 * v.Z), (m.M31 * v.X) + (m.M32 * v.Y) + (m.M33 * v.Z));

    private static Matrix3f Columns(Vector3 a, Vector3 b, Vector3 c) => new(a.X, b.X, c.X, a.Y, b.Y, c.Y, a.Z, b.Z, c.Z);

    // The unit axis, signed, nearest a direction, leaving out one already taken.
    private static Vector3 Snap(Vector3 v, Vector3 taken)
    {
        Vector3[] axes = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];
        Vector3 best = Vector3.UnitX;
        float most = -1f;
        foreach (Vector3 axis in axes)
        {
            float along = Vector3.Dot(v, axis);
            if (Math.Abs(Vector3.Dot(axis, taken)) < 0.5f && Math.Abs(along) > most)
            {
                most = Math.Abs(along);
                best = along < 0f ? -axis : axis;
            }
        }

        return best;
    }

    // A node's place in the soldier's frame, metres.
    private Vector3 Hand(int node, float yaw, float lift) =>
        node < 0 ? Vector3.Zero : ModernBodyAsset.ToBody(_world[node].Translation, yaw) + new Vector3(0f, 0f, lift);

    // The right hand as posed: its place, and its axes carried into the soldier's frame.
    private void ReadHands(float yaw, float lift)
    {
        int node = _asset.RightHand;
        if (node < 0)
        {
            return;
        }

        Matrix4x4 m = _world[node];
        Vector3 x = Vector3.Normalize(ModernBodyAsset.ToBody(new Vector3(m.M11, m.M12, m.M13), yaw));
        Vector3 y = Vector3.Normalize(ModernBodyAsset.ToBody(new Vector3(m.M21, m.M22, m.M23), yaw));
        Vector3 z = Vector3.Normalize(ModernBodyAsset.ToBody(new Vector3(m.M31, m.M32, m.M33), yaw));
        Grip = (Hand(node, yaw, lift), Columns(x, y, z));
        (_yaw, _lift) = (yaw, lift);
    }

    // Eased, so the change of gait starts and ends gently.
    private static float Smooth(float t) => t * t * (3f - (2f * t));
}
