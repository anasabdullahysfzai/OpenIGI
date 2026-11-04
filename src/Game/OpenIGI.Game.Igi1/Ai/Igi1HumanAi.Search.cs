using OpenIGI.Core.Numerics;
using OpenIGI.Formats.Ai;

namespace OpenIGI.Game.Igi1.Ai;

/// <content>
/// The modern AI's search (<see cref="GameplayCVars.ModernAi"/>): a soldier who walked over to something he heard or glimpsed,
/// and found nothing there, searches the place as a modern stealth game's soldier does. He looks left, right and behind him;
/// he walks to a couple of the graph's nodes nearby and looks about at each; then he goes back to his patrol, on edge for a
/// while. Anything he sees or hears on the way ends the search. A squad that has lost the player hunts for him the same way, each
/// to his own nodes about where he was last seen; and one who finds a team-mate down checks the body, waves the others on, and
/// they search there. <b>Not retail.</b>
/// </content>
public sealed partial class Igi1HumanAi
{
    /// <summary>How far to either side the soldier turns to look, in radians.</summary>
    public const double SweepRadians = 70.0 * Math.PI / 180.0;

    /// <summary>How long he holds each look of a search once turned, in seconds.</summary>
    public const float SweepSeconds = 1.0f;

    /// <summary>How far from the searched place a node may be for him to go and look from it, in world units.</summary>
    public const double SearchRadiusInUnits = 12.0 * 4096.0;

    /// <summary>How many nodes about the place he goes and looks from.</summary>
    public const int SearchNodes = 2;

    /// <summary>How many nodes about the place he goes and looks from, hunting with the squad.</summary>
    public const int HuntNodes = 3;

    // Left, right, then behind, from his facing.
    private static readonly double[] LooksAbout = [SweepRadians, -SweepRadians, Math.PI];

    /// <summary>How long after a search he stays on edge, in ticks: suspicion fills twice as fast.</summary>
    public const int OnEdgeTicks = 45 * 30;

    // How far ahead of himself he looks, in world units: far enough to be level.
    private const double LookAheadInUnits = 5.0 * 4096.0;

    /// <summary>How far from the place called a soldier answers a search called by the squad, in world units.</summary>
    public const double SearchCallInUnits = 30.0 * 4096.0;

    /// <summary>How long the kneeling look about lasts, how long checking a body, and waving the squad on, in seconds: their clips'.</summary>
    public const float ScanSeconds = 6.33f, CheckBodySeconds = 7.03f, SignalSeconds = 2.5f;

    private enum SearchAct
    {
        None,
        KneelScan,
        CheckBody,
        Signal,
    }

    // The search still to run, in order: a point to look at or walk to, for a walk what to look at on arriving, and for a look what
    // he does meanwhile.
    private readonly Queue<(Vector3d Point, bool Walk, Vector3d? LookAt, SearchAct Act)> _searchSteps = new();
    private int _actUntil = int.MinValue, _scanFrom = int.MinValue;
    private Vector3d? _huntPlace, _bodyPlace;
    private int _searchCallSeen = int.MinValue;

    private bool _searching;

    // Whether the last investigation walked over to its point; a search follows it once it ends.
    private bool _investigationWalked;

    private int _onEdgeUntilTick = -1;

    /// <summary>Gets how many bodies he has knelt to check, and how many times waved the squad on, which tell a body to play them.</summary>
    public (int Checks, int Signals) SearchGestures { get; private set; }

    /// <summary>Gets a value indicating whether he is in the middle of checking a body or waving the squad on.</summary>
    public bool HoldsGesture => _lastTick < _actUntil;

    /// <summary>Gets a value indicating whether he kneels and looks about him.</summary>
    public bool KneelScanning => _lastTick < _scanFrom + (int)(ScanSeconds * 30f);

    /// <summary>Gets a value indicating whether he goes with his rifle up: hunting, or on edge after a fight or a search.</summary>
    public bool RifleReady => GameplayCVars.ModernAi.Value && IsOnEdge && !HoldsGesture;

    /// <summary>Gets whether the soldier is searching a place for the player.</summary>
    public bool IsSearching => _searching;

    /// <summary>Gets whether the soldier is searching, or has lately searched: his suspicion fills twice as fast.</summary>
    public bool IsOnEdge => _searching || _lastTick < _onEdgeUntilTick;

    /// <summary>
    /// Starts a search once a walk-over investigation has ended with nothing found, and installs its next step once the last
    /// has run; once a tick, before the script, so a step between two others is not taken for idleness.
    /// </summary>
    private void ContinueSearch()
    {
        if (_combatInstalled || Activity == AiSoldierActivity.Dead)
        {
            CancelSearch();
            return;
        }

        if (KneelScanning)
        {
            // Down on a knee, looking as the clip's head does: to his left and then his right.
            WriteChannel(AiCombatChannel.Kneel, 1f);
            _gazeTick = _lastTick;
            _gaze = -1.0f * MathF.Sin(2f * MathF.PI * (_lastTick - _scanFrom) / (ScanSeconds * 30f));
        }

        AnswerSearchCall();

        // A look or a walk -- of the investigation, or of the search -- is still running.
        if (_lookAtPoint is not null || IsActionActive("AIAction_MoveToEvent"))
        {
            return;
        }

        if (!_searching && (_bodyPlace ?? _huntPlace) is { } hunted)
        {
            bool body = _bodyPlace is not null;
            (_bodyPlace, _huntPlace, _investigationWalked) = (null, null, false);
            PlanSearch(hunted, hunting: true, checkBody: body);
            ModernLog?.Invoke($"{(body ? "goes to check a body" : "lost him; hunts where he was last seen")} ({_searchSteps.Count} steps)");
        }

        if (!_searching)
        {
            // Nobody searches for a dead man: the walk that would have led to it is forgotten.
            _tracked.TryGetValue(AiDetectionCandidate.PlayerId, out Igi1AiTrackedHuman? player);
            if (player is { Candidate.Alive: false })
            {
                _investigationWalked = false;
            }

            // Nor for a man he is looking at: the glimpse keeps his eyes on him (FollowGlimpse).
            if (!_investigationWalked || InvestigatingAt is not { } place || player is { SeesRaw: true })
            {
                return;
            }

            _investigationWalked = false;
            PlanSearch(place);
            ModernLog?.Invoke($"found nothing; searching ({_searchSteps.Count} steps)");
        }

        if (_searchSteps.TryDequeue(out (Vector3d Point, bool Walk, Vector3d? LookAt, SearchAct Act) step))
        {
            float seconds = step.Act switch
            {
                SearchAct.KneelScan => ScanSeconds,
                SearchAct.CheckBody => CheckBodySeconds,
                SearchAct.Signal => SignalSeconds,
                _ => SweepSeconds,
            };
            Install(step.Point, step.Walk, seconds, step.LookAt);
            BeginAct(step.Act, step.Point, seconds);
            return;
        }

        _searching = false;
        _onEdgeUntilTick = _lastTick + OnEdgeTicks;
        ModernLog?.Invoke("search over; back to patrol, on edge");
    }

    /// <summary>Ends a search where it stands, without the time on edge a finished one leaves.</summary>
    private void CancelSearch()
    {
        if (_searching)
        {
            ModernLog?.Invoke("search dropped");
        }

        _searchSteps.Clear();
        _searching = false;
        (_bodyPlace, _huntPlace, _actUntil, _scanFrom) = (null, null, int.MinValue, int.MinValue);
    }

    // What a step does while it looks: kneeling to scan, or the clip a body plays; a checked body then waves the squad on.
    private void BeginAct(SearchAct act, Vector3d point, float seconds)
    {
        int ticks = (int)(seconds * AiCombatParameters.TicksPerSecond);
        switch (act)
        {
            case SearchAct.KneelScan:
                _scanFrom = _lastTick;
                break;
            case SearchAct.CheckBody:
                _actUntil = _lastTick + ticks;
                SearchGestures = (SearchGestures.Checks + 1, SearchGestures.Signals);
                ModernLog?.Invoke("checks the body");
                break;
            case SearchAct.Signal:
                _actUntil = _lastTick + ticks;
                SearchGestures = (SearchGestures.Checks, SearchGestures.Signals + 1);
                _squad?.CallSearch(point, _lastTick);
                ModernLog?.Invoke("waves the squad on");
                break;
        }
    }

    // A team-mate found dead: the first to see him goes and checks him, unless he is fighting; anyone else who sees him is on edge.
    private void NoticeFallenFriend(Igi1AiTrackedHuman friend)
    {
        bool first = _squad?.NoticeBody(friend.Candidate.Id) ?? true;
        if (_combatInstalled)
        {
            return;
        }

        _onEdgeUntilTick = Math.Max(_onEdgeUntilTick, _lastTick + OnEdgeTicks);
        if (first && Arm == AiCombatArm.Patrol && !_searching)
        {
            ModernLog?.Invoke("sees a team-mate down");
            _bodyPlace = friend.Candidate.Feet;
        }
    }

    // A search the squad was called to, near enough to answer, once.
    private void AnswerSearchCall()
    {
        if (_squad?.SearchCall is not { } call || call.Tick == _searchCallSeen)
        {
            return;
        }

        _searchCallSeen = call.Tick;
        _onEdgeUntilTick = Math.Max(_onEdgeUntilTick, _lastTick + OnEdgeTicks);
        if (Arm == AiCombatArm.Patrol && !_searching && _bodyPlace is null && Vector3d.Distance(Position, call.Place) <= SearchCallInUnits)
        {
            _huntPlace = call.Place;
        }
    }

    /// <summary>Lays out the search of a place: the looks about where he stands, then the nodes nearby.</summary>
    /// <param name="place">The place.</param>
    /// <param name="hunting">Whether he hunts with the squad: straight to his own nodes, down on a knee at one of them.</param>
    /// <param name="checkBody">Whether a body lies at the place, to go to and check first.</param>
    private void PlanSearch(Vector3d place, bool hunting = false, bool checkBody = false)
    {
        _searching = true;
        _searchSteps.Clear();
        Vector3d here = Position;
        double facing = Facing;
        if (checkBody)
        {
            // To the node nearest him, looking at him; down to check him; and the squad waved on.
            if (_graph is { InParentFrame: false } && _graph.NodeAt(NearestNode(place)) is { } beside && NearestNode(here) != beside.Id)
            {
                _searchSteps.Enqueue((beside.Position, true, place, SearchAct.None));
            }

            _searchSteps.Enqueue((place, false, null, SearchAct.CheckBody));
            _searchSteps.Enqueue((Ahead(place, facing), false, null, SearchAct.Signal));
        }

        // Left, right, then behind -- each a look at a point ahead, at his own height.
        foreach (double turn in hunting ? [] : LooksAbout)
        {
            Vector3d ahead = Ahead(here, facing + turn);
            _searchSteps.Enqueue((ahead, false, null, SearchAct.None));
        }

        // A couple of nodes about the place, not the one he stands on, in an order of the level's own dice; at each he looks out
        // away from the place, then to either side of that.
        int count = hunting ? HuntNodes : SearchNodes;
        if (_graph is null || _graph.InParentFrame)
        {
            count = 0;
        }

        // Only a node he can walk to: one behind a wall would end his walk where it began.
        int standing = NearestNode(here);
        GraphRouteTable? routes = _graph?.RouteTable;
        List<GraphNode> near = count == 0 || routes is null ? [] : [.. _graph!.Nodes.Where(node =>
            node.Id != standing && Vector3d.Distance(node.Position, place) <= SearchRadiusInUnits
            && routes.TryGetStep(standing, node.Id, out _, out _))];

        // A hunt goes first to as near where he was seen as can be reached, if no one else has it; then wherever the dice say.
        near = hunting ? [.. near.OrderBy(node => Vector3d.Distance(node.Position, place))] : near;
        bool nearest = hunting;
        int planned = _searchSteps.Count;
        for (int i = 0; i < count && near.Count > 0; i++)
        {
            GraphNode node = nearest ? near[0] : near[(int)(_random.NextDouble() * near.Count) % near.Count];
            near.Remove(node);
            if (hunting && _squad is { } squad && !squad.TakeSearchNode(node.Id, _lastTick))
            {
                i--;
                continue;
            }

            nearest = false;
            double outward = Math.Atan2(-(node.Position.X - place.X), node.Position.Y - place.Y);
            _searchSteps.Enqueue((node.Position, true, Ahead(node.Position, outward), SearchAct.None));
            if (hunting && i == count - 1)
            {
                _searchSteps.Enqueue((Ahead(node.Position, outward), false, null, SearchAct.KneelScan));
                continue;
            }

            _searchSteps.Enqueue((Ahead(node.Position, outward + (SweepRadians * 0.7)), false, null, SearchAct.None));
            _searchSteps.Enqueue((Ahead(node.Position, outward - (SweepRadians * 0.7)), false, null, SearchAct.None));
        }

        // Nowhere to go from here: he looks about where he stands instead.
        if (hunting && _searchSteps.Count == planned)
        {
            foreach (double turn in LooksAbout)
            {
                _searchSteps.Enqueue((Ahead(here, facing + turn), false, null, SearchAct.None));
            }
        }
    }

    // A point ahead of a place along a heading -- the facing's convention, atan2(-dx, dy) -- at the place's own height.
    private static Vector3d Ahead(Vector3d from, double heading) =>
        new(from.X - (Math.Sin(heading) * LookAheadInUnits), from.Y + (Math.Cos(heading) * LookAheadInUnits), from.Z);
}
