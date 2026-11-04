using OpenIGI.Core.Numerics;
using OpenIGI.Game.Ai;

namespace OpenIGI.Game.Igi1.Ai;

/// <content>
/// The modern AI's patrol between fights (<see cref="GameplayCVars.ModernAi"/>): the same paths the mission's scripts give, walked
/// by a man. He has a pace of his own, stops now and then where a leg of the walk ends, and looks about him; and where his head
/// looks, he sees. <b>Not retail.</b>
/// </content>
public sealed partial class Igi1HumanAi
{
    /// <summary>How likely he is to stop where a leg of a walk ends, once he has walked long enough since the last stop.</summary>
    public const double PatrolStopChance = 0.4;

    /// <summary>How long he walks before he may stop again, in ticks.</summary>
    public const int PatrolStopWalkTicks = 8 * 30;

    /// <summary>How long a stop to shift his weight lasts at the least, and how much longer at most, in ticks.</summary>
    public const int PatrolStopTicks = 3 * 30, PatrolStopExtraTicks = 3 * 30;

    /// <summary>How far his head turns from his facing through the looking-around clip (<c>stop_look</c>), in degrees to his right,
    /// every half second: measured from the clip, so that his sight follows the head the player watches.</summary>
    private static readonly float[] LookAroundDegrees =
        [6, 21, 87, 111, 112, 112, 114, 117, 107, 57, -7, -46, -96, -127, -133, -136, -136, -131, -101, -31, -3, 4];

    /// <summary>How long a stop to look around lasts, in ticks: the clip's whole sweep.</summary>
    public const int LookAroundTicks = 21 * 15;

    /// <summary>How fast his sight turns to a glance, in radians a tick: about as his head does.</summary>
    public const float GazeTurnPerTick = 0.15f;

    private int _patrolStopUntil = int.MinValue;
    private int _patrolWalkedTicks;
    private int _glanceUntil = int.MinValue, _glanceNext = int.MinValue;
    private float? _glance;
    private float _gaze;
    private int _gazeTick = int.MinValue;
    private int _lookAroundFrom = int.MinValue;
    private double _ownWalkPace;

    /// <summary>Gets or sets his task's id in the mission, which the test settings name him by.</summary>
    public int GuardId { get; set; } = -1;

    /// <summary>Gets how many stops he has made on his patrols, which tells a body when a new one begins.</summary>
    public int PatrolStops { get; private set; }

    /// <summary>Gets a number chosen for the latest stop, which picks what he does in it.</summary>
    public int PatrolStopPick { get; private set; }

    /// <summary>Gets which way he glances from his facing on patrol, in radians to his right; null when he looks ahead.</summary>
    public float? PatrolGlance => Gazing ? _glance : null;

    /// <summary>Gets a value indicating whether he is on his patrol this tick, not called away from it.</summary>
    public bool OnPatrol => Gazing;

    // Whether he is on his patrol this tick, looking where he pleases.
    private bool Gazing => _lastTick - _gazeTick <= 1 && !HasCombatRecord;

    // This man's walking pace, in metres a second: no two walk a patrol quite alike.
    private double OwnWalkPace => _ownWalkPace > 0.0
        ? _ownWalkPace
        : _ownWalkPace = ModernWalkMetresPerSecond * (0.9 + (0.2 * _random.NextDouble()));

    // Stands out a stop, looking about him; true while it lasts.
    private bool HoldPatrolStop(int tick)
    {
        if (!GameplayCVars.ModernAi.Value)
        {
            return false;
        }

        // Called away from a stop -- to look at something, or to fight -- he does not take it up again on his return.
        if (tick - _gazeTick > 1)
        {
            _patrolStopUntil = int.MinValue;
        }

        bool stopped = tick < _patrolStopUntil;
        Glance(tick, wide: stopped);
        _gazeTick = tick;
        TurnGaze(tick, stopped);
        if (stopped)
        {
            Activity = AiSoldierActivity.Idle;
            RequestedAnimation = -1;
        }

        return stopped;
    }

    // At the end of a leg of a walk he may stop, unless the script stops him there itself or has him hurry.
    private void MayStopOnPatrol(int tick, AiPatrolRoute route, AiPatrolCommand finished)
    {
        if (!GameplayCVars.ModernAi.Value || IsOnEdge || finished.Kind != AiPatrolCommandKind.WalkTo || !route.IsWalking
            || route.Next is not { Kind: AiPatrolCommandKind.WalkTo } || _patrolWalkedTicks < PatrolStopWalkTicks
            || _random.NextDouble() >= PatrolStopChance)
        {
            return;
        }

        _patrolWalkedTicks = 0;
        PatrolStopPick = (int)(_random.NextDouble() * 1024.0);
        bool lookAround = PatrolStopPick % 2 == 0; // the character's first stop clip (ModernBodyAsset.StopClips)
        _lookAroundFrom = lookAround ? tick : int.MinValue;
        _patrolStopUntil = tick + (lookAround ? LookAroundTicks : PatrolStopTicks + (int)(_random.NextDouble() * PatrolStopExtraTicks));
        PatrolStops++;
    }

    // Where he looks now: along the looking-around clip's sweep through such a stop, else easing to his glance.
    private void TurnGaze(int tick, bool stopped)
    {
        if (stopped && _lookAroundFrom != int.MinValue)
        {
            _glance = null;
            float at = Math.Min((tick - _lookAroundFrom) / 15f, LookAroundDegrees.Length - 1f);
            int before = (int)at, after = Math.Min(before + 1, LookAroundDegrees.Length - 1);
            float degrees = LookAroundDegrees[before] + ((LookAroundDegrees[after] - LookAroundDegrees[before]) * (at - before));
            _gaze = degrees * MathF.PI / 180f;
            return;
        }

        _gaze += Math.Clamp((_glance ?? 0f) - _gaze, -GazeTurnPerTick, GazeTurnPerTick);
    }

    // An eye turned as his gaze is: the head looks there, so the cone does.
    private Matrix3f TurnedByGaze(Matrix3f eye)
    {
        if (!GameplayCVars.ModernAi.Value || !Gazing || _gaze == 0f)
        {
            return eye;
        }

        (float sin, float cos) = MathF.SinCos(_gaze);
        Vector3f r = eye.Column1, f = eye.Column2, u = eye.Column3;
        Vector3f right = new((r.X * cos) - (f.X * sin), (r.Y * cos) - (f.Y * sin), (r.Z * cos) - (f.Z * sin));
        Vector3f ahead = new((f.X * cos) + (r.X * sin), (f.Y * cos) + (r.Y * sin), (f.Z * cos) + (r.Z * sin));
        return new Matrix3f(right.X, ahead.X, u.X, right.Y, ahead.Y, u.Y, right.Z, ahead.Z, u.Z);
    }

    // A look to one side and back every few seconds, wider and oftener when he has stopped.
    private void Glance(int tick, bool wide)
    {
        if (tick < _glanceUntil)
        {
            return;
        }

        _glance = null;
        if (tick >= _glanceNext)
        {
            double side = _random.NextDouble() < 0.5 ? -1.0 : 1.0;
            _glance = (float)(side * (wide ? 0.6 + (0.5 * _random.NextDouble()) : 0.3 + (0.5 * _random.NextDouble())));
            _glanceUntil = tick + 30 + (int)(_random.NextDouble() * 45.0);
            _glanceNext = _glanceUntil + (wide ? 15 : 90) + (int)(_random.NextDouble() * (wide ? 45.0 : 150.0));
        }
    }
}
