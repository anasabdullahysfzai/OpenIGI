using OpenIGI.Core.Numerics;

namespace OpenIGI.Game.Igi1.Ai;

/// <summary>
/// The modern AI's squad (<see cref="GameplayCVars.ModernAi"/>): every soldier of a level fighting the player shares one. They
/// tell each other where the player was last seen, keep apart, and one of them at a time goes round his side while the rest
/// keep him busy. <b>Not retail</b>, whose soldiers share nothing but the alert ping.
/// </summary>
public sealed class AiSquad
{
    /// <summary>How long a soldier stays a member after he last fought, in ticks.</summary>
    public const int StaleTicks = 60;

    /// <summary>How long one soldier keeps the flanking role before it passes on, in ticks.</summary>
    public const int FlankTicks = 20 * 30;

    private readonly Dictionary<Igi1HumanAi, (Vector3d Position, int Tick)> _members = [];
    private Igi1HumanAi? _flanker;
    private int _flankUntil;

    /// <summary>Gets where the player was last seen by any member, and when; null before anyone has seen him.</summary>
    public (Vector3d Position, int Tick)? LastSeen { get; private set; }

    /// <summary>Notes that a soldier fights this tick, where he stands or is going.</summary>
    /// <param name="soldier">The soldier.</param>
    /// <param name="position">Where he stands, or the place he is making for.</param>
    /// <param name="tick">The tick.</param>
    public void Fight(Igi1HumanAi soldier, Vector3d position, int tick) => _members[soldier] = (position, tick);

    /// <summary>Notes that a member sees the player.</summary>
    public void Saw(Vector3d feet, int tick) => LastSeen = (feet, tick);

    /// <summary>Gets the places of the other members fighting now.</summary>
    /// <param name="soldier">The soldier asking.</param>
    /// <param name="tick">The tick.</param>
    public IEnumerable<Vector3d> Others(Igi1HumanAi soldier, int tick) =>
        _members.Where(m => m.Key != soldier && tick - m.Value.Tick <= StaleTicks).Select(m => m.Value.Position);

    /// <summary>Whether this soldier is the one going round the player's side: with two or more fighting, one of them at a time.</summary>
    /// <param name="soldier">The soldier asking.</param>
    /// <param name="tick">The tick.</param>
    public bool IsFlanker(Igi1HumanAi soldier, int tick)
    {
        List<Igi1HumanAi> fighting = [.. _members.Where(m => tick - m.Value.Tick <= StaleTicks).Select(m => m.Key)];
        if (fighting.Count < 2)
        {
            _flanker = null;
            return false;
        }

        if (_flanker is null || tick >= _flankUntil || !fighting.Contains(_flanker))
        {
            // The role passes to the next of them, so each takes his turn.
            int next = _flanker is null ? 0 : (fighting.IndexOf(_flanker) + 1) % fighting.Count;
            _flanker = fighting[Math.Max(0, next)];
            _flankUntil = tick + FlankTicks;
        }

        return _flanker == soldier;
    }
}
