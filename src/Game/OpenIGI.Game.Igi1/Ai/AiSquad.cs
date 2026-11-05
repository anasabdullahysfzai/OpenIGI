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

    /// <summary>Gets or sets when a member last threw a grenade, so the squad does not throw them in a shower.</summary>
    public int GrenadeTick { get; set; } = int.MinValue / 2;

    /// <summary>Whether a member other than this one is going round the player's side now: the rest keep him pinned.</summary>
    /// <param name="soldier">The soldier asking.</param>
    /// <param name="tick">The tick.</param>
    public bool OtherFlanking(Igi1HumanAi soldier, int tick) =>
        _flanker is { } flanker && flanker != soldier && tick - _members.GetValueOrDefault(flanker).Tick <= StaleTicks;

    private readonly Dictionary<int, int> _searchedNodes = [];
    private readonly HashSet<int> _bodies = [];

    /// <summary>Gets a place the squad was told to search, and when; null before anyone has been.</summary>
    public (Vector3d Place, int Tick)? SearchCall { get; private set; }

    /// <summary>Tells the squad to search a place: a body found, say.</summary>
    /// <param name="place">The place.</param>
    /// <param name="tick">The tick.</param>
    public void CallSearch(Vector3d place, int tick) => SearchCall = (place, tick);

    /// <summary>Takes a node for a soldier to search from, so the squad spreads out: false if another took it within the last half minute.</summary>
    /// <param name="node">The node.</param>
    /// <param name="tick">The tick.</param>
    /// <returns>Whether it is his.</returns>
    public bool TakeSearchNode(int node, int tick)
    {
        if (_searchedNodes.TryGetValue(node, out int taken) && tick - taken < SearchNodeTicks)
        {
            return false;
        }

        _searchedNodes[node] = tick;
        return true;
    }

    /// <summary>Notes that a member has noticed a body.</summary>
    /// <param name="id">Who it is.</param>
    /// <returns>Whether he is the first to.</returns>
    public bool NoticeBody(int id) => _bodies.Add(id);

    private readonly Dictionary<string, int> _saidAt = [];
    private int _saidLast = int.MinValue / 2;

    /// <summary>How long after anyone in the squad speaks another may, and how long before the same thing is said again, in ticks.</summary>
    public const int SpeakGapTicks = 2 * 30, SameThingTicks = 8 * 30;

    /// <summary>Whether a member may say something now, so they talk in turn and do not repeat themselves; notes it if so.</summary>
    /// <param name="key">What he would say.</param>
    /// <param name="tick">The tick.</param>
    /// <returns>Whether he says it.</returns>
    public bool MaySay(string key, int tick)
    {
        if (tick - _saidLast < SpeakGapTicks || (_saidAt.TryGetValue(key, out int said) && tick - said < SameThingTicks))
        {
            return false;
        }

        _saidLast = tick;
        _saidAt[key] = tick;
        return true;
    }

    /// <summary>How long a node stays taken by a soldier searching from it, in ticks.</summary>
    public const int SearchNodeTicks = 30 * 30;

    /// <summary>Gets where the player was last seen by any member, when, and where that member stood; null before anyone has seen him.</summary>
    public (Vector3d Position, int Tick, Vector3d Seer)? LastSeen { get; private set; }

    private readonly Dictionary<Igi1HumanAi, (int Until, int RestUntil)> _firing = [];

    /// <summary>How many may fire at the player at once.</summary>
    public const int MostFiring = 3;

    /// <summary>How long a member keeps his turn to fire, and how long after it others come first, in ticks.</summary>
    public const int FireTurnTicks = 5 * 30, FireRestTicks = 1 * 30;

    /// <summary>Whether a member may fire now: on his turn, or there is one free and he has rested since his last.</summary>
    /// <param name="soldier">The soldier.</param>
    /// <param name="tick">The tick.</param>
    /// <returns>Whether he may.</returns>
    public bool MayFire(Igi1HumanAi soldier, int tick)
    {
        if (_firing.TryGetValue(soldier, out (int Until, int RestUntil) turn))
        {
            if (tick < turn.Until)
            {
                return true;
            }

            if (tick < turn.RestUntil)
            {
                return false;
            }
        }

        if (_firing.Values.Count(t => tick < t.Until) >= MostFiring)
        {
            return false;
        }

        _firing[soldier] = (tick + FireTurnTicks, tick + FireTurnTicks + FireRestTicks);
        return true;
    }

    /// <summary>Keeps a member's turn while his burst runs on past it, so a new one does not start beside him.</summary>
    /// <param name="soldier">The soldier.</param>
    /// <param name="tick">The tick.</param>
    /// <returns>Whether he may fire on: false for a burst begun without a turn, with none now free.</returns>
    public bool StillFiring(Igi1HumanAi soldier, int tick)
    {
        (int until, int rest) = _firing.GetValueOrDefault(soldier, (int.MinValue, int.MinValue));
        if (tick >= until)
        {
            return MayFire(soldier, tick);
        }

        _firing[soldier] = (Math.Max(until, tick + 1), Math.Max(rest, tick + 1 + FireRestTicks));
        return true;
    }

    /// <summary>Gets how many hold a turn to fire now, for testing.</summary>
    /// <param name="tick">The tick.</param>
    /// <returns>How many.</returns>
    public int FiringNow(int tick) => _firing.Values.Count(t => tick < t.Until);

    /// <summary>Notes that a soldier fights this tick, where he stands or is going.</summary>
    /// <param name="soldier">The soldier.</param>
    /// <param name="position">Where he stands, or the place he is making for.</param>
    /// <param name="tick">The tick.</param>
    public void Fight(Igi1HumanAi soldier, Vector3d position, int tick) => _members[soldier] = (position, tick);

    /// <summary>Notes that a member sees the player.</summary>
    /// <param name="feet">Where the player stands.</param>
    /// <param name="tick">The tick.</param>
    /// <param name="seer">Where the member stands.</param>
    public void Saw(Vector3d feet, int tick, Vector3d seer)
    {
        LastSeen = (feet, tick, seer);
        _words.Enqueue((feet, tick, seer));
    }

    private readonly Queue<(Vector3d Position, int Tick, Vector3d Seer)> _words = new();
    private (Vector3d Position, int Tick, Vector3d Seer)? _word;

    /// <summary>Gets the latest word of where the player was that has had time to be passed on; null before there is one.</summary>
    /// <param name="tick">The tick.</param>
    /// <param name="delay">How long a word takes to pass, in ticks.</param>
    /// <returns>Where he was, when, and where the one who saw him stood.</returns>
    public (Vector3d Position, int Tick, Vector3d Seer)? WordOf(int tick, int delay)
    {
        while (_words.TryPeek(out (Vector3d Position, int Tick, Vector3d Seer) word) && tick - word.Tick >= delay)
        {
            _word = _words.Dequeue();
        }

        return _word;
    }

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
