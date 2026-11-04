using OpenIGI.Game.Ai;

namespace OpenIGI.Game.Igi1.Ai;

/// <content>
/// The modern AI's patrol between fights (<see cref="GameplayCVars.ModernAi"/>): the same paths the mission's scripts give, walked
/// by a man. He has a pace of his own, stops now and then where a leg of the walk ends, and looks about him. <b>Not retail.</b>
/// </content>
public sealed partial class Igi1HumanAi
{
    /// <summary>How likely he is to stop where a leg of a walk ends, once he has walked long enough since the last stop.</summary>
    public const double PatrolStopChance = 0.4;

    /// <summary>How long he walks before he may stop again, in ticks.</summary>
    public const int PatrolStopWalkTicks = 8 * 30;

    /// <summary>How long a stop lasts at the least, and how much longer at most, in ticks.</summary>
    public const int PatrolStopTicks = 3 * 30, PatrolStopExtraTicks = 5 * 30;

    private int _patrolStopUntil = int.MinValue;
    private int _patrolWalkedTicks;
    private int _glanceUntil = int.MinValue, _glanceNext = int.MinValue;
    private float? _glance;
    private double _ownWalkPace;

    /// <summary>Gets or sets his task's id in the mission, which the test settings name him by.</summary>
    public int GuardId { get; set; } = -1;

    /// <summary>Gets how many stops he has made on his patrols, which tells a body when a new one begins.</summary>
    public int PatrolStops { get; private set; }

    /// <summary>Gets a number chosen for the latest stop, which picks what he does in it.</summary>
    public int PatrolStopPick { get; private set; }

    /// <summary>Gets which way he glances from his facing on patrol, in radians to his right; null when he looks ahead.</summary>
    public float? PatrolGlance => Activity is AiSoldierActivity.Idle or AiSoldierActivity.Patrolling && !HasCombatRecord ? _glance : null;

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

        bool stopped = tick < _patrolStopUntil;
        Glance(tick, wide: stopped);
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
        if (!GameplayCVars.ModernAi.Value || finished.Kind != AiPatrolCommandKind.WalkTo || !route.IsWalking
            || route.Next is not { Kind: AiPatrolCommandKind.WalkTo } || _patrolWalkedTicks < PatrolStopWalkTicks
            || _random.NextDouble() >= PatrolStopChance)
        {
            return;
        }

        _patrolWalkedTicks = 0;
        _patrolStopUntil = tick + PatrolStopTicks + (int)(_random.NextDouble() * PatrolStopExtraTicks);
        PatrolStopPick = (int)(_random.NextDouble() * 1024.0);
        PatrolStops++;
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
