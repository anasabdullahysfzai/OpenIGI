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
    private ModernMix _before;
    private float _fade = 1f;
    private bool _running;
    private int _shotsSeen;
    private float _firingFor;
    private ModernClip? _fall;
    private float _fallTime;
    private float _fallRaise = float.NaN;
    private float _floor;
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
        _before = new ModernMix(asset.Idle, 0f, asset.Walk, 0f, 0f);
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
    public void Fall(ModernClip fall)
    {
        _fall = fall;
        _fallTime = 0f;
    }

    /// <summary>Poses and skins the body for one tick of the soldier's movement.</summary>
    /// <param name="moved">How far the soldier moved this tick along the ground, in metres.</param>
    /// <param name="yaw">The character's extra turn about up, in radians.</param>
    /// <param name="raise">How far to raise the body, in metres: what the soldier's own clip has lowered where he is drawn.</param>
    /// <param name="aiming">Whether he is in a fight, with his rifle up.</param>
    /// <param name="shots">How many shots he has fired, which a firing clip follows.</param>
    /// <param name="kneeling">Whether he kneels: down out of sight, or with his rifle up when he is also aiming.</param>
    /// <param name="keepsDown">Whether he kneels to hide, his rifle lowered whatever the fight.</param>
    /// <param name="turning">How fast he turns about up, in radians a second, left positive.</param>
    /// <param name="crouched">Whether he moves crouched.</param>
    /// <param name="heading">Which way he moves against the way he faces, forward and right, when he moves facing elsewhere (strafing); zero otherwise.</param>
    public void Tick(float moved, float yaw, float raise = 0f, bool aiming = false, int shots = 0, bool kneeling = false, bool keepsDown = false, float turning = 0f,
        bool crouched = false, Vector2 heading = default)
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
        _running = moving && (_running ? Speed > RunBelow : Speed > RunAbove);
        ModernClip stand = kneeling ? (!keepsDown && (aiming || _firingFor > 0f) ? _asset.KneelAim : _asset.KneelIdle)
            : _firingFor > 0f ? _asset.Fire : aiming ? _asset.AimIdle : _asset.Idle;

        // A reload or a throw over whatever he stands in, played once; set off moving, he leaves it.
        if (_act is { } act)
        {
            _actTime += TickSeconds;
            if (_actTime >= act.Duration || (moving && !kneeling))
            {
                _act = null;
            }
            else
            {
                stand = act;
            }
        }

        // Turning where he stands: the steps of a turn clip as big as the turn is fast -- 45, 135 or 180 degrees -- each played on
        // by the angle he turns, so his feet keep to his body whatever the speed. A turn once started is stepped a third of a
        // second at least (a rate over the line for a tick or two flickered the steps in and out), on the biggest clip it has
        // needed, so it does not change clip at every wobble of the rate.
        bool canTurn = _act is null && !kneeling && !moving && !ReferenceEquals(_asset.TurnLeft, ModernClip.None);
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
        ModernClip move = crouched ? _asset.CrouchWalk
            : heading != Vector2.Zero ? (Math.Abs(heading.X) >= Math.Abs(heading.Y)
                ? (heading.X >= 0f ? _asset.AimWalk : _asset.WalkBack)
                : (heading.Y > 0f ? _asset.StrafeRight : _asset.StrafeLeft))
            : _running ? _asset.Run : aiming ? _asset.AimWalk : _asset.Walk;
        if (!ReferenceEquals(stand, _stand) || !ReferenceEquals(move, _move))
        {
            // Faded from what was showing, held where it was; mid-fade, from whichever of the two showed more.
            _before = _fade < 0.5f ? _before : Mix(_stand, _move);
            (_stand, _move, _fade) = (stand, move, 0f);
        }

        _fade = Math.Min(1f, _fade + (TickSeconds / BlendSeconds));

        // The moving clip is played at the soldier's pace, so its feet keep to the ground he covers; a change of gait keeps the
        // stride where it was.
        float pace = moving && move.MetresPerSecond > 0f
            ? Math.Clamp(Speed / move.MetresPerSecond, SlowestPace, FastestPace)
            : 1f;
        _stride = (_stride + (TickSeconds * pace / move.Duration)) % 1f;
        Showing = FormattableString.Invariant(
            $"speed {Speed:0.00} m/s, stand {Name(stand)}, move {Name(move)} x{Smooth(_moveWeight):0.00} at pace {pace:0.00}, fade {_fade:0.00}, legs {(_turnClip is { } legs && _turnWeight > 0f ? Name(legs) : "-")} x{Smooth(_turnWeight):0.00}");
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
        _asset.Pose(Mix(_stand, _move), _before, Smooth(_fade), _world, _skin, _hit, _hitTime, hitWeight, _kick.Length > 0 ? _kick : null);
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

        _asset.Skin(_skin, yaw, (Lift ?? 0f) + raise, _vertices);
        _device.UpdateAnimatedMesh(Mesh, _vertices);
    }

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

            Vector3f m = _vertices[muzzle].Position, s = _vertices[stock].Position;
            return (new Vector3(m.X, m.Y, m.Z), new Vector3(s.X, s.Y, s.Z));
        }
    }

    // A pair of clips where the body is in them now.
    private ModernMix Mix(ModernClip stand, ModernClip move) =>
        new(stand, ReferenceEquals(stand, _act) ? _actTime : _standTime, move, _stride * move.Duration, Smooth(_moveWeight),
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
            (_act, _actTime) = (clip, 0f);
        }
    }

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
