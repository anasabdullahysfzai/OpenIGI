using OpenIGI.Core.Numerics;
using OpenIGI.Formats.Ai;
using OpenIGI.Game.Ai;

namespace OpenIGI.Game.Igi1.Ai;

/// <content>
/// The modern AI's fight (<see cref="GameplayCVars.ModernAi"/>), in place of the original patrol arm's: a soldier picks a
/// fighting position -- a place he can fire on the player from, with somewhere to get down beside it or behind it -- runs to
/// it, fires a burst or two, gets down, comes up again, and moves on when the player has moved round him, has gone from view,
/// or he has been there long enough. With others fighting, they keep apart, share where the player was last seen, and one at
/// a time goes round the player's side. The firing itself -- aim, bursts, reloads, the difficulty's accuracy -- is the
/// original's (<see cref="RunSharedSubAction"/>). <b>Not retail.</b>
/// </content>
public sealed partial class Igi1HumanAi
{
    /// <summary>The furthest a fighting position may be from him, in metres.</summary>
    public const double PositionReachMetres = 16.0;

    /// <summary>The range he likes to fight at, in metres; nearer or further costs a little.</summary>
    public const double PreferredRangeMetres = 14.0;

    /// <summary>The nearest he will choose to stand to the player, in metres.</summary>
    public const double ClosestRangeMetres = 5.0;

    /// <summary>How far from his firing spot the place he gets down in may be, in metres.</summary>
    public const double HideReachMetres = 3.0;

    /// <summary>How far the player may step to either side, in metres, and the place he gets down in still hide him.</summary>
    public const double CoverFlankMetres = 3.0;

    /// <summary>How near a squad mate may be before a place counts as crowded, in metres.</summary>
    public const double SpacingMetres = 4.0;

    /// <summary>How long he holds a position, in ticks, at least and at most more, before he moves on anyway.</summary>
    public const int HoldTicks = 8 * 30, HoldMoreTicks = 8 * 30;

    /// <summary>How long he stays down, in ticks, at least and at most more.</summary>
    public const int DownTicks = 45, DownMoreTicks = 45;

    /// <summary>How long without the player in view from his firing spot before he moves to find him again, in ticks.</summary>
    public const int UnseenTicks = 2 * 30;

    /// <summary>How long a place he was seen in while down stays off his list, in ticks.</summary>
    public const int SeenDownTicks = 10 * 30;

    /// <summary>How long without anyone seeing the player before the fight is left to end as the original's does, in ticks.</summary>
    public const int GiveUpTicks = 12 * 30;

    // Heights above a node his body is tested at, in metres: chest and head, standing and kneeling; and the shooter's eye.
    private const double CoverChestMetres = 1.1, CoverHeadMetres = 1.6, KneelChestMetres = 0.6, KneelHeadMetres = 1.0;
    private const double ShooterEyeMetres = 1.6;

    /// <summary>How fast he crouch-runs and strafes, against his run (the original's run clip, about four metres a second).</summary>
    public const double CrouchStride = 0.55, StrafeStride = 0.7;

    /// <summary>The furthest he strafes to a position he can see the player from, in metres; further, he runs.</summary>
    public const double StrafeReachMetres = 8.0;

    private enum MoveStyle
    {
        Run,
        Crouch,
        Strafe,
    }

    private MoveStyle _moveStyle;

    /// <summary>Gets whether he is moving crouched, ducking back into cover.</summary>
    public bool CrouchMoving => _tactic == TacticPhase.Moving && _moveStyle == MoveStyle.Crouch && _route.Count > 0;

    /// <summary>Gets whether he is moving facing the player, sideways or backwards as the way lies.</summary>
    public bool Strafing => _tactic == TacticPhase.Moving && _moveStyle == MoveStyle.Strafe && _route.Count > 0;

    private enum TacticPhase
    {
        None,
        Moving,
        Firing,
        Down,
        Advancing,
    }

    private TacticPhase _tactic;
    private int _fireNode = -1;
    private int _downNode = -1;
    private bool _downLow;
    private int _phaseSince;
    private int _holdUntil;
    private int _downUntil;
    private int _lastSeenTick;
    private int _burstsLeft;
    private AiCombatPhase _lastFirePhase;
    private Vector3d? _lastKnown;
    private AiSquad? _squad;
    private readonly Dictionary<int, int> _seenDownUntil = [];

    /// <summary>Gives the soldier the squad he fights the player with (the modern AI).</summary>
    /// <param name="squad">The level's squad, or null to fight alone.</param>
    public void SetSquad(AiSquad? squad) => _squad = squad;

    /// <summary>Gets what the soldier is doing in his fight, for testing and the log: none, moving, firing, down or advancing.</summary>
    public string CoverState => _tactic.ToString();

    /// <summary>Gets the node he gets down at, else the one he fires from, or -1.</summary>
    public int CoverNode => _downNode >= 0 ? _downNode : _fireNode;

    /// <summary>Gets whether he is down behind low cover, kneeling out of sight, which his body shows with the rifle lowered.</summary>
    public bool KeepsDown => _tactic == TacticPhase.Down && _downLow;

    /// <summary>Gets whether his position's cover is low, which he kneels behind, for testing.</summary>
    internal bool CoverLowForTesting => _downLow;

    /// <summary>Runs the modern fight for this tick, in place of the original's patrol arm: true while it owns the tick.</summary>
    /// <param name="tick">The tick.</param>
    /// <param name="enemy">Where the target is.</param>
    /// <returns>Whether the modern fight owns the tick.</returns>
    private bool FightModern(int tick, Vector3d enemy)
    {
        if (!GameplayCVars.ModernAi.Value || Arm != AiCombatArm.Patrol || _graph is null || _graph.InParentFrame || _sight is null)
        {
            return false;
        }

        bool handled = FightModernTick(tick, enemy);
        string handing = handled ? string.Empty
            : $"hands the fight to the original (prone {_patrolProne}, child {_patrolOneShotChild is not null || _patrolRollChild is not null}, {_tactic})";
        if (handing != _handing)
        {
            _handing = handing;
            if (handing.Length > 0)
            {
                ModernLog?.Invoke(handing);
            }
        }

        return handled;
    }

    private string _handing = string.Empty;

    private bool FightModernTick(int tick, Vector3d enemy)
    {
        if (_patrolProne || _patrolOneShotChild is not null || _patrolRollChild is not null)
        {
            return false;
        }

        // What he knows: the player where he sees him, else where any of them last did.
        Vector3d feet = LiveTargetFeet(enemy);
        bool seen = TargetSlot.IsVisible;
        if (seen)
        {
            (_lastKnown, _lastSeenTick) = (feet, tick);
            _squad?.Saw(feet, tick);
        }
        else if (_squad?.LastSeen is { } shared && shared.Tick > _lastSeenTick)
        {
            (_lastKnown, _lastSeenTick) = (shared.Position, shared.Tick);
        }

        Vector3d target = _lastKnown ?? feet;
        bool underFire = Events.DrainedCount(AiEvent.TakingDamage) > 0 || Events.DrainedCount(AiEvent.GunshotMiss) > 0;

        // Nobody has seen him for a long while: the fight is left to end as the original's does, and the search to follow it.
        if (tick - _lastSeenTick > GiveUpTicks && _lastSeenTick > 0)
        {
            ForgetTactics();
            return false;
        }

        _squad?.Fight(this, _fireNode >= 0 ? _graph!.NodeAt(_fireNode)!.Position : Position, tick);
        switch (_tactic)
        {
            case TacticPhase.None:
                return TakePosition(tick, target, "engages") || Advance(tick, target) || Hold(tick, target);

            case TacticPhase.Moving:
            case TacticPhase.Advancing:
                // The run, as the original's own walks in a fight go: no shooting on the way.
                _channelWrites.Clear();
                Combat = default;
                _tenseRunThisTick = true;
                SetGait(walking: false);
                AdvanceInCombat();
                if (_route.Count > 0)
                {
                    // Crouched, slower along his way; strafing, slower still on his way but facing the player all along.
                    if (_tactic == TacticPhase.Moving && _moveStyle != MoveStyle.Run)
                    {
                        _stepStyle = (_moveStyle == MoveStyle.Crouch ? CrouchStride : StrafeStride, _moveStyle == MoveStyle.Strafe);
                        if (_moveStyle == MoveStyle.Strafe)
                        {
                            TurnTowards(AimPointOf(target, TargetCrouching), 0f, level: true);
                        }
                    }

                    return true;
                }

                _movingInCombat = false;
                if (_tactic == TacticPhase.Advancing)
                {
                    // At the place he was making for: look again from here.
                    _tactic = TacticPhase.None;
                    return TakePosition(tick, target, "looks for him from here") || Hold(tick, target);
                }

                // At his position: down if he came to get down, or rounds are about and he can; else straight to firing.
                if (_pendingDown)
                {
                    _pendingDown = false;
                    GetDown(tick, keepUntil: true);
                }
                else if (underFire && (_downLow || CurrentNode == _downNode))
                {
                    GetDown(tick);
                }
                else
                {
                    StartFiring(tick);
                }

                return true;

            case TacticPhase.Down:
                _channelWrites.Clear();
                Combat = default;
                _movingInCombat = false;
                if (_downLow)
                {
                    WriteChannel(AiCombatChannel.Kneel, 1f);
                }

                TurnTowards(AimPointOf(target, TargetCrouching), 0.034906585f);
                if (underFire)
                {
                    _downUntil = Math.Max(_downUntil, tick + (DownTicks / 2));
                }

                // Seen where he is down: the place does not hide him from where the player has got to.
                if (tick - _phaseSince > 10 && tick % 6 == 0 && Exposed(feet, _downLow))
                {
                    _seenDownUntil[CurrentNode] = tick + SeenDownTicks;
                    if (TakePosition(tick, target, "is seen where he is down: moves"))
                    {
                        return true;
                    }

                    _downUntil = tick;
                }

                if (tick >= _downUntil)
                {
                    // Up over low cover where he is, or a step out to his firing spot.
                    if (_downLow || _fireNode == CurrentNode)
                    {
                        StartFiring(tick);
                    }
                    else if (GoTo(_fireNode))
                    {
                        // Out to fire, facing where he will fire.
                        _moveStyle = MoveStyle.Strafe;
                        _tactic = TacticPhase.Moving;
                        _phaseSince = tick;
                    }
                    else
                    {
                        StartFiring(tick);
                    }
                }

                return true;

            case TacticPhase.Firing:
                return Fire(tick, enemy, target, seen, underFire);
        }

        return false;
    }

    // At his firing spot: the original's own fire, a burst or two, then down; on when the player has moved or gone.
    private bool Fire(int tick, Vector3d enemy, Vector3d target, bool seen, bool underFire)
    {
        _channelWrites.Clear();
        HoldPosture();
        RunSharedSubAction(enemy, _weapon!());
        if (!_combatInstalled)
        {
            return true;
        }

        AiCombatPhase phase = _combat!.Phase;
        bool burstEnded = _lastFirePhase == AiCombatPhase.Fire && phase != AiCombatPhase.Fire;
        _lastFirePhase = phase;
        if (burstEnded)
        {
            _burstsLeft--;
        }

        bool between = phase != AiCombatPhase.Fire;
        bool canGetDown = _downLow || _downNode >= 0;

        // Down after his bursts, or sooner when rounds come close, if there is anywhere to get down.
        if (between && canGetDown && (_burstsLeft <= 0 || (underFire && tick - _phaseSince > 15)))
        {
            CancelFire();
            if (_downLow)
            {
                GetDown(tick);
            }
            else if (GoTo(_downNode))
            {
                // Back into cover, low.
                _moveStyle = MoveStyle.Crouch;
                _tactic = TacticPhase.Moving;
                _phaseSince = tick;
                ModernLog?.Invoke("gets back into cover");
                _downUntil = tick + DownTicks + NextBounded(DownMoreTicks);
                _pendingDown = true;
            }

            return true;
        }

        // The player out of view from here a while -- moved round, or gone: somewhere he can be seen from, or after him.
        if (between && tick - Math.Max(_lastSeenTick, _phaseSince) > UnseenTicks)
        {
            CancelFire();
            // Nowhere better: he watches where the player went from here a while longer.
            if (!TakePosition(tick, target, "has lost his line of fire: moves") && !Advance(tick, target))
            {
                _phaseSince = tick;
            }

            return true;
        }

        // Long enough here: on to another place, as a man in a fight keeps moving.
        if (between && tick >= _holdUntil)
        {
            CancelFire();
            if (!TakePosition(tick, target, "moves on"))
            {
                _holdUntil = tick + (HoldTicks / 2);
            }

            return true;
        }

        return true;
    }

    private bool _pendingDown;

    private void StartFiring(int tick)
    {
        _tactic = TacticPhase.Firing;
        _phaseSince = tick;
        _burstsLeft = 2 + NextBounded(2);
        _lastFirePhase = _combat?.Phase ?? AiCombatPhase.Stand;
    }

    private void GetDown(int tick, bool keepUntil = false)
    {
        _tactic = TacticPhase.Down;
        _phaseSince = tick;
        if (!keepUntil)
        {
            _downUntil = tick + DownTicks + NextBounded(DownMoreTicks);
        }
    }

    // Stays where he is, firing when he can: nothing better to go to.
    private bool Hold(int tick, Vector3d target)
    {
        _fireNode = CurrentNode;
        (_downNode, _downLow) = (-1, false);
        _holdUntil = tick + (HoldTicks / 2);
        StartFiring(tick);
        return true;
    }

    private void CancelFire()
    {
        if (_combat is not null && _combat.Phase != AiCombatPhase.Stand)
        {
            _combat.CancelChild();
        }
    }

    // Toward where the player was last seen, to find him again.
    private bool Advance(int tick, Vector3d target)
    {
        if (FindAdvance(target) is not int node || !GoTo(node))
        {
            return false;
        }

        _combat!.CancelChild();
        _repositionLatched = false;
        _lookForTargetLatched = false;
        (_fireNode, _downNode, _downLow) = (-1, -1, false);
        _moveStyle = MoveStyle.Run;
        _tactic = TacticPhase.Advancing;
        _phaseSince = tick;
        ModernLog?.Invoke(FormattableString.Invariant(
            $"advances to find him: {Vector3d.Distance(Position, _graph!.NodeAt(node)!.Position) / GraphNode.MetreInUnits:0.0} m (node {node})"));
        return true;
    }

    // Chooses a fighting position and starts for it; false where there is none.
    private bool TakePosition(int tick, Vector3d target, string why)
    {
        bool flanker = _squad?.IsFlanker(this, tick) ?? false;
        // Moving on means somewhere else: never the place he fires from now, nor the place he is down in.
        if (FindPosition(target, flanker, _tactic == TacticPhase.None ? -1 : _fireNode, CurrentNode) is not var (fire, down, low))
        {
            return false;
        }

        // Where he goes first: his down when the place has one beside it and rounds are about, else his firing spot.
        int first = fire;
        if (!GoTo(first, claim: true))
        {
            return false;
        }

        _combat!.CancelChild();
        _repositionLatched = false;
        _lookForTargetLatched = false;
        (_fireNode, _downNode, _downLow) = (fire, down, low);

        // A short way with the player in view is strafed, facing him; anything else is run.
        bool inView = tick - _lastSeenTick < 30;
        _moveStyle = inView && Vector3d.Distance(Position, _graph!.NodeAt(fire)!.Position) <= StrafeReachMetres * GraphNode.MetreInUnits
            ? MoveStyle.Strafe : MoveStyle.Run;
        _tactic = TacticPhase.Moving;
        _phaseSince = tick;
        _holdUntil = tick + HoldTicks + NextBounded(HoldMoreTicks);
        _pendingDown = false;
        GraphNode at = _graph!.NodeAt(fire)!;
        string cover = low ? "low cover" : down >= 0 ? "cover beside it" : "no cover";
        ModernLog?.Invoke(FormattableString.Invariant(
            $"{why}: {(flanker ? "flanks to" : "takes")} a position {Vector3d.Distance(Position, at.Position) / GraphNode.MetreInUnits:0.0} m away, {Flat(at.Position, target) / GraphNode.MetreInUnits:0.0} m from him, {cover} (node {fire})"));
        return true;
    }

    /// <summary>
    /// The best place to fight from: one he can fire on the player from standing, and get down in (low cover) or beside
    /// (a hidden node a step or two away), hidden from a step or two either side of the player too; at the range he likes,
    /// near, apart from his squad, off places he was just seen down in, and for the flanker, round the player's side.
    /// </summary>
    private (int Fire, int Down, bool Low)? FindPosition(Vector3d target, bool flanker, int notHere, int standing)
    {
        double reach = PositionReachMetres * GraphNode.MetreInUnits;
        double metre = GraphNode.MetreInUnits;
        Vector3d eye = new(target.X, target.Y, target.Z + (ShooterEyeMetres * metre));
        Vector3d chest = new(target.X, target.Y, target.Z + (1.2 * metre));
        List<Vector3d> others = _squad?.Others(this, _lastTick).ToList() ?? [];

        // The direction the rest of the squad bears on him from: the flanker wants another.
        Vector3d? squadBearing = null;
        if (flanker && others.Count > 0)
        {
            double sx = others.Sum(o => o.X - target.X), sy = others.Sum(o => o.Y - target.Y);
            double length = Math.Sqrt((sx * sx) + (sy * sy));
            squadBearing = length > 1.0 ? new Vector3d(sx / length, sy / length, 0) : null;
        }

        (int, int, bool)? best = null;
        double bestScore = double.MaxValue;
        foreach (GraphNode node in _graph!.Nodes)
        {
            double away = Vector3d.Distance(Position, node.Position);
            double range = Flat(node.Position, target);
            if (away > reach || range < ClosestRangeMetres * metre || node.Id == notHere || (notHere >= 0 && node.Id == standing) || (node.Criteria & GraphNodeCriteria.View) != 0
                || _seenDownUntil.GetValueOrDefault(node.Id, -1) > _lastTick
                || (_occupancy is not null && !_occupancy.IsClaimedBy(_graph, node.Id, this) && _occupancy.IsFull(_graph, node.Id)))
            {
                continue;
            }

            // Cheap first: what the place would cost him, before any sight line is cast.
            double crowd = others.Count(o => Flat(o, node.Position) < SpacingMetres * metre);
            double score = (away / metre * 0.6) + (Math.Abs(range / metre - PreferredRangeMetres) * 0.4) + (crowd * 15.0);
            if (squadBearing is { } bearing)
            {
                double nx = node.Position.X - target.X, ny = node.Position.Y - target.Y, nl = Math.Sqrt((nx * nx) + (ny * ny));
                double cos = nl > 1.0 ? ((nx * bearing.X) + (ny * bearing.Y)) / nl : 1.0;
                score += (cos + 1.0) * 6.0;
            }

            if (score >= bestScore || !_sight!(new Vector3d(node.Position.X, node.Position.Y, node.Position.Z + (CoverHeadMetres * metre)), chest, null))
            {
                continue;
            }

            // Somewhere to get down: kneeling where he fires, or a hidden place a step or two away.
            Vector3d across = Across(node.Position, target, CoverFlankMetres * metre);
            Vector3d[] eyes = [eye, eye + across, eye - across];
            int down = -1;
            bool low = eyes.All(from => Hidden(from, node, KneelChestMetres) && Hidden(from, node, KneelHeadMetres));
            if (!low)
            {
                down = FindDown(node, eyes);
            }

            score += low ? 0.0 : down >= 0 ? 2.0 : 10.0;
            if (score < bestScore)
            {
                (best, bestScore) = ((node.Id, down, low), score);
            }
        }

        return best;
    }

    // Beside a firing spot, the nearest node hidden standing from the player and a step or two either side of him.
    private int FindDown(GraphNode fire, Vector3d[] eyes)
    {
        double reach = HideReachMetres * GraphNode.MetreInUnits;
        int best = -1;
        double bestAway = double.MaxValue;
        foreach (GraphNode node in _graph!.Nodes)
        {
            double away = Vector3d.Distance(fire.Position, node.Position);
            if (node.Id == fire.Id || away > reach || away >= bestAway || _seenDownUntil.GetValueOrDefault(node.Id, -1) > _lastTick)
            {
                continue;
            }

            if (eyes.All(from => Hidden(from, node, CoverChestMetres) && Hidden(from, node, CoverHeadMetres)))
            {
                (best, bestAway) = (node.Id, away);
            }
        }

        return best;
    }

    // Toward the player's last known place: the node nearest it he can reach, not on top of it.
    private int? FindAdvance(Vector3d target)
    {
        double metre = GraphNode.MetreInUnits;
        int? best = null;
        double bestScore = double.MaxValue;
        foreach (GraphNode node in _graph!.Nodes)
        {
            double range = Flat(node.Position, target);
            double away = Vector3d.Distance(Position, node.Position);
            if (node.Id == CurrentNode || away < 2.5 * metre || away > PositionReachMetres * metre || (node.Criteria & GraphNodeCriteria.View) != 0)
            {
                continue;
            }

            double score = Math.Abs(range - (ClosestRangeMetres * metre * 1.5));
            if (score < bestScore && range < Flat(Position, target))
            {
                (best, bestScore) = (node.Id, score);
            }
        }

        return best;
    }

    // The fight's state, gone with the fight.
    private void ForgetTactics()
    {
        _tactic = TacticPhase.None;
        (_fireNode, _downNode, _downLow) = (-1, -1, false);
        _lastKnown = null;
        _lastSeenTick = 0;
        _pendingDown = false;
        _seenDownUntil.Clear();
    }

    /// <summary>Gets how many waypoints are left on the soldier's route, and whether he is writing forward, for testing.</summary>
    internal (int Waypoints, bool Forward, int Destination, int Current) RouteForTesting => (_route.Count, WalksForward, DestinationNode, CurrentNode);

    /// <summary>Gets the fire routine's phase and whether the target is seen, for testing.</summary>
    internal string FireForTesting => $"{_combat?.Phase} seen={TargetSlot.IsVisible} installed={_combatInstalled}";

    /// <summary>Gets whether one point can be seen from another through the level, for testing.</summary>
    internal bool CanSeeForTesting(Vector3d from, Vector3d to) => _sight is null || _sight(from, to, seen: null);

    /// <summary>Gets a node's place on the soldier's graph, for testing.</summary>
    internal Vector3d? NodePositionForTesting(int node) => _graph?.NodeAt(node)?.Position;

    // Whether the player's eye can see his chest or his head where he stands now: the line itself, not the vision's last word.
    private bool Exposed(Vector3d shooter, bool kneeling)
    {
        double metre = GraphNode.MetreInUnits;
        Vector3d eye = new(shooter.X, shooter.Y, shooter.Z + (ShooterEyeMetres * metre));
        (double chest, double head) = kneeling ? (KneelChestMetres, KneelHeadMetres) : (CoverChestMetres, CoverHeadMetres);
        return _sight!(eye, new Vector3d(Position.X, Position.Y, Position.Z + (chest * metre)), null)
            || _sight(eye, new Vector3d(Position.X, Position.Y, Position.Z + (head * metre)), null);
    }

    // Whether an eye cannot see a point the height given above a node.
    private bool Hidden(Vector3d eye, GraphNode node, double metres) =>
        !_sight!(eye, new Vector3d(node.Position.X, node.Position.Y, node.Position.Z + (metres * GraphNode.MetreInUnits)), null);

    // Square to the line from the player to a place, along the ground, the length given.
    private static Vector3d Across(Vector3d place, Vector3d shooter, double length)
    {
        Vector3d across = new(-(place.Y - shooter.Y), place.X - shooter.X, 0.0);
        return across * (length / Math.Max(1.0, Math.Sqrt((across.X * across.X) + (across.Y * across.Y))));
    }

    private static double Flat(Vector3d a, Vector3d b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }
}
