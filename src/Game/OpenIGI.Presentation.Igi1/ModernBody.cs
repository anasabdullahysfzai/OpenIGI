using System.Numerics;
using OpenIGI.Core.Graphics;
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
    }

    /// <summary>Gets the mesh the body is drawn from.</summary>
    public MeshHandle Mesh { get; private set; }

    /// <summary>Gets the character's runs of triangles and their textures.</summary>
    public (int IndexOffset, int IndexCount, TextureHandle Diffuse)[] Subsets => _asset.Subsets;

    /// <summary>Gets or sets how far to raise the body, in metres, so its lowest point meets the soldier's; null until measured.</summary>
    public float? Lift { get; set; }

    /// <summary>Gets the ground speed the body was last posed for, in metres a second.</summary>
    public float Speed { get; private set; }

    /// <summary>Poses and skins the body for one tick of the soldier's movement.</summary>
    /// <param name="moved">How far the soldier moved this tick along the ground, in metres.</param>
    /// <param name="yaw">The character's extra turn about up, in radians.</param>
    public void Tick(float moved, float yaw)
    {
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

        _asset.Pose(_idleTime, _walkTime, Smooth(_walkWeight), _world, _skin);
        _asset.Skin(_skin, yaw, Lift ?? 0f, _vertices);
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
