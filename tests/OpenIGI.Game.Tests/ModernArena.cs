using System.Buffers.Binary;
using OpenIGI.Core.Numerics;
using OpenIGI.Formats.Ai;
using OpenIGI.Formats.Tests;
using OpenIGI.Game.Ai;
using OpenIGI.Game.Igi1.Ai;
using OpenIGI.Game.Weapons;
using OpenIGI.Scripting.Qvm;
using OpenIGI.Scripting.Runtime;
using OpenIGI.TestSupport;

namespace OpenIGI.Game.Tests;

/// <summary>
/// A closed test room for the modern AI, built in code: a flat floor, walls as boxes (tall ones and waist-high ones), a nav
/// graph laid over the floor on a two-metre grid with its route table, and sight lines the boxes block. A real soldier brain
/// runs in it -- the shipped archetype script, a patrol's fire routine -- against a scripted player, tick by tick, so a fight
/// can be replayed and read back in a second. <b>Not retail.</b>
/// </summary>
internal sealed class ModernArena
{
    public const double Metre = GraphNode.MetreInUnits;

    private readonly List<(Vector3d Min, Vector3d Max)> _boxes = [];
    private readonly Dictionary<Igi1HumanAi, int[]> _clips = [];

    /// <summary>Gets the squad every soldier stood in the room fights with.</summary>
    public AiSquad Squad { get; } = new();

    /// <summary>Initializes a new instance of the <see cref="ModernArena"/> class: a square room, walled round.</summary>
    /// <param name="size">The room's side, in metres.</param>
    public ModernArena(double size = 30.0)
    {
        Size = size;
        Box(-1, -1, 0, size + 1, 0, 3);
        Box(-1, size, 0, size + 1, size + 1, 3);
        Box(-1, 0, 0, 0, size, 3);
        Box(size, 0, 0, size + 1, size, 3);
    }

    /// <summary>Gets the room's side, in metres.</summary>
    public double Size { get; }

    /// <summary>Gets the nav graph, once <see cref="Build"/> has laid it.</summary>
    public GraphFile? Graph { get; private set; }

    /// <summary>Adds a wall: a box between two corners, in metres.</summary>
    public ModernArena Box(double x0, double y0, double z0, double x1, double y1, double z1)
    {
        _boxes.Add((new Vector3d(x0 * Metre, y0 * Metre, z0 * Metre), new Vector3d(x1 * Metre, y1 * Metre, z1 * Metre)));
        return this;
    }

    /// <summary>A point in the room, in metres, as engine units.</summary>
    public static Vector3d At(double x, double y, double z = 0.0) => new(x * Metre, y * Metre, z * Metre);

    /// <summary>Whether the straight line between two points passes no wall.</summary>
    public bool Clear(Vector3d from, Vector3d to) => _boxes.All(b => !Crosses(from, to, b.Min, b.Max));

    /// <summary>Lays the nav graph: a node every two metres clear of the walls, linked to its neighbours it can walk to.</summary>
    public GraphFile Build(double spacing = 2.0)
    {
        List<Vector3d> nodes = [];
        for (double x = 1.0; x < Size; x += spacing)
        {
            for (double y = 1.0; y < Size; y += spacing)
            {
                Vector3d p = At(x, y);
                if (_boxes.All(b => !Near(p, b.Min, b.Max, 0.6 * Metre)))
                {
                    nodes.Add(p);
                }
            }
        }

        int n = nodes.Count;
        float[,] cost = new float[n, n];
        int[,] next = new int[n, n];
        List<(int, int)> links = [];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                (cost[i, j], next[i, j]) = i == j ? (0f, i) : (float.PositiveInfinity, -1);
            }
        }

        double reach = spacing * 1.5 * Metre;
        Vector3d knee = new(0, 0, 0.3 * Metre);
        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                double d = Vector3d.Distance(nodes[i], nodes[j]);
                if (d <= reach && Clear(nodes[i] + knee, nodes[j] + knee))
                {
                    links.Add((i, j));
                    (cost[i, j], next[i, j]) = ((float)d, j);
                    (cost[j, i], next[j, i]) = ((float)d, i);
                }
            }
        }

        // Every route: the next step from one node toward another and what is left to walk.
        for (int k = 0; k < n; k++)
        {
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    if (cost[i, k] + cost[k, j] < cost[i, j])
                    {
                        cost[i, j] = cost[i, k] + cost[k, j];
                        next[i, j] = next[i, k];
                    }
                }
            }
        }

        byte[] table = new byte[GraphRouteTable.EntryStride * n * n];
        for (int from = 0; from < n; from++)
        {
            for (int to = 0; to < n; to++)
            {
                Span<byte> entry = table.AsSpan(GraphRouteTable.EntryStride * (from + (to * n)), GraphRouteTable.EntryStride);
                bool routed = next[from, to] >= 0;
                BinaryPrimitives.WriteInt32LittleEndian(entry, routed ? next[from, to] : GraphRouteTable.NoRoute);
                BinaryPrimitives.WriteSingleLittleEndian(entry[4..], routed ? cost[from, to] : -1f);
            }
        }

        GraphStreamWriter w = new();
        w.RawUInt32(GraphFile.Magic);
        w.Field("nMaxNodes", GraphValueType.Int32, x => x.Int32(n));
        for (int i = 0; i < n; i++)
        {
            Vector3d p = nodes[i];
            w.Field("nNodeID", GraphValueType.Int32, x => x.Int32(i));
            w.Field("tNodePosition", GraphValueType.Real64x3, x => x.Vector3d(p));
            w.Field("vNodeGamma", GraphValueType.Real32, x => x.Single(0));
            w.Field("vNodeRadius", GraphValueType.Real32, x => x.Single((float)(spacing * 0.6 * Metre)));
            w.Field("nNodeMaterial", GraphValueType.Int32, x => x.Int32(-1));
            w.Field("nNodeCriteria", GraphValueType.String8, x => x.String(string.Empty));
        }

        foreach ((int a, int b) in links)
        {
            w.Field("nLinkID1", GraphValueType.Int32, x => x.Int32(a));
            w.Field("nLinkID2", GraphValueType.Int32, x => x.Int32(b));
            w.Field("eLinkType", GraphValueType.Int32, x => x.Int32(1));
        }

        w.Field("RouteTable", GraphValueType.Blob, x => x.Blob(GraphValueType.Blob, table));
        Graph = GraphFile.Read(w.ToArray());
        return Graph;
    }

    /// <summary>
    /// Stands a patrol soldier in the room: the shipped patrol script, the patrol arm with an AK at the given difficulty's
    /// figures (mission one's Hard row by default), sight that the walls block, and his decisions to the log given.
    /// </summary>
    /// <param name="missionTask">A mission-one soldier's own AI script to run first (its task id), or null for the archetype's alone.</param>
    public Igi1HumanAi Patrol(Vector3d feet, float facing, Action<string> log, int? missionTask = null, int seed = 1)
    {
        GraphFile graph = Graph ?? Build();
        Igi1HumanAi soldier = new(graph, feet, facing, phase: seed & 3, seed: seed) { StepsBodyAfterClip = true, RuntimeId = seed };
        int[] clip = [30];
        _clips[soldier] = clip;
        soldier.SetSquad(Squad);
        soldier.SetGrenadeAvailable(true);
        soldier.SetLineOfSight((from, to, _) => Clear(from, to));
        soldier.SetNodeOccupancy(new AiNodeOccupancy());
        soldier.ModernLog = log;
        soldier.SetCombat(
            AiType.PatrolAk,
            new AiCombatParameters(new HumanAiConfigItemTask
            {
                AiTypeName = "AITYPE_PATROL_AK",
                DifficultyName = "GD_3",
                TargetingTimeoutSeconds = 15f,
                TrackingDistanceMetres = 100f,
                MinimumClipFraction = 0.2f,
                RandomClipFraction = 0.3f,
                MaximumRoundsPerSequence = 30,
                HitProbabilityAtMinimumRange = 1f,
                HitProbabilityAtMaximumRange = 0.5f,
                AimPrecision = 0.8f,
            }),
            () => new AiCombatWeaponState(clip[0], 30, 30, WeaponCategory.Gun));

        NativeRegistry natives = new();
        natives.RegisterConstant("TRUE", 1);
        natives.RegisterConstant("FALSE", 0);
        natives.RegisterConstant("GAME_FREQUENCY", 30);
        natives.RegisterRealConstant("WORLD_METER", Metre);
        natives.RegisterRealConstant("MATH_PI", Math.PI);
        QvmProgram patrol = Archetype("patrol");
        if (missionTask is int task)
        {
            soldier.SetScript(new AiScriptHost(natives), Read(Path.Combine(GameData.RequireRoot(), "missions", "location0", "level1", "ai", $"{task}.qvm")), patrol);
        }
        else
        {
            soldier.SetScript(new AiScriptHost(natives), patrol);
        }

        return soldier;
    }

    /// <summary>
    /// Ticks a soldier as the game does: his brain against the player, his body stepped by the gait's stride, and his clip
    /// spent as his trigger is pulled and filled when he reloads (the burst counts rounds by watching the clip change).
    /// </summary>
    /// <param name="soldier">The soldier.</param>
    /// <param name="tick">The tick.</param>
    /// <param name="playerFeet">Where the player stands.</param>
    /// <param name="bodyFeet">Where a dead team-mate lies, if one is on offer.</param>
    public void Tick(Igi1HumanAi soldier, int tick, Vector3d playerFeet, Vector3d? bodyFeet = null)
    {
        double stride = soldier.WalksForward ? (soldier.Activity == AiSoldierActivity.Engaging ? 0.11 : 0.04) * Metre : 0.0;
        if (bodyFeet is { } lying)
        {
            static Vector3d Mark(Vector3d feet) => feet + new Vector3d(0, 0, Igi1HumanAi.DetectionMarkAboveFeet(false));
            soldier.TickAmong(
                tick,
                [new AiDetectionCandidate(AiDetectionCandidate.PlayerId, true, true, Mark(playerFeet), playerFeet, false, null),
                 new AiDetectionCandidate(99, false, false, Mark(lying), lying, false, null)],
                stride);
        }
        else
        {
            soldier.Tick(tick, playerFeet + new Vector3d(0, 0, 1.6 * Metre), rootMotionThisTick: stride, enemyFeet: playerFeet);
        }

        soldier.StepBodyAfterClip(stride, 0.0);
        int[] clip = _clips[soldier];
        if (soldier.Combat.PullTrigger && clip[0] > 0)
        {
            clip[0]--;
        }
        else if (soldier.Combat.PressReload || soldier.Combat.ReloadWeaponDirectly)
        {
            clip[0] = 30;
        }
    }

    /// <summary>The room from above, a character a metre: walls #, low walls =, the soldiers by number, the player P.</summary>
    public string Map(IReadOnlyList<Igi1HumanAi> soldiers, Vector3d player)
    {
        int size = (int)Size;
        char[,] map = new char[size, size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                Vector3d at = At(x + 0.5, y + 0.5);
                map[y, x] = '.';
                foreach ((Vector3d min, Vector3d max) in _boxes)
                {
                    if (at.X >= min.X && at.X <= max.X && at.Y >= min.Y && at.Y <= max.Y)
                    {
                        map[y, x] = max.Z > 1.5 * Metre ? '#' : '=';
                    }
                }
            }
        }

        void Put(Vector3d p, char c)
        {
            int x = (int)(p.X / Metre), y = (int)(p.Y / Metre);
            if (x >= 0 && x < size && y >= 0 && y < size)
            {
                map[y, x] = c;
            }
        }

        for (int i = 0; i < soldiers.Count; i++)
        {
            Put(soldiers[i].Position, soldiers[i].Activity == AiSoldierActivity.Dead ? 'x' : (char)('1' + i));
        }

        Put(player, 'P');
        System.Text.StringBuilder text = new();
        for (int y = size - 1; y >= 0; y--)
        {
            for (int x = 0; x < size; x++)
            {
                text.Append(map[y, x]);
            }

            text.AppendLine();
        }

        return text.ToString();
    }

    /// <summary>A round from the player: the gunshot every soldier in earshot hears, and its whizz past any it goes near.</summary>
    /// <param name="soldier">The soldier.</param>
    /// <param name="muzzle">Where the player fired from.</param>
    /// <param name="aim">What the round was aimed at; it flies on past it to the first wall.</param>
    /// <returns>Whether it struck him (it is aimed at his chest and nothing is in the way).</returns>
    /// <param name="heard">How far the gunshot itself carries, in metres (the weapon's AI detection range); 0 for none.</param>
    public bool Shoot(Igi1HumanAi soldier, Vector3d muzzle, Vector3d aim, double heard = 60.0)
    {
        if (heard > 0.0)
        {
            soldier.HearGunshot(muzzle, heard * Metre, fromSameSide: false, shooter: AiDetectionCandidate.PlayerId);
        }

        Vector3d along = aim - muzzle;
        Vector3d end = muzzle + (along * (40.0 * Metre / along.Length));
        Vector3d chest = soldier.Position + new Vector3d(0, 0, 1.2 * Metre);
        Vector3d closest = Vector3d.ClosestOnSegment(muzzle, end, chest);
        if (Vector3d.Distance(closest, chest) < 0.3 * Metre && Clear(muzzle, chest))
        {
            soldier.ReceiveDamage(muzzle);
            return true;
        }

        // As the game delivers a round going by (SoldierGunShotRounds): its nearest point to him, if it comes within two metres.
        soldier.ReceiveRoundPassing(Vector3d.ClosestOnSegment(muzzle, end, soldier.Position), fromOtherSide: true, AiDetectionCandidate.PlayerId, muzzle);
        return false;
    }

    private static QvmProgram Archetype(string name) => Read(Path.Combine(GameData.RequireRoot(), "common", "ai", $"{name}.qvm"));

    private static QvmProgram Read(string path) => QvmProgram.Read(File.ReadAllBytes(path));

    // Whether a point is inside a box grown by a margin along the ground.
    private static bool Near(Vector3d p, Vector3d min, Vector3d max, double margin) =>
        p.X > min.X - margin && p.X < max.X + margin && p.Y > min.Y - margin && p.Y < max.Y + margin && p.Z < max.Z;

    // Whether the segment passes through the box (the slab test).
    private static bool Crosses(Vector3d from, Vector3d to, Vector3d min, Vector3d max)
    {
        double t0 = 0.0, t1 = 1.0;
        double[] o = [from.X, from.Y, from.Z], d = [to.X - from.X, to.Y - from.Y, to.Z - from.Z];
        double[] lo = [min.X, min.Y, min.Z], hi = [max.X, max.Y, max.Z];
        for (int axis = 0; axis < 3; axis++)
        {
            if (Math.Abs(d[axis]) < 1e-9)
            {
                if (o[axis] < lo[axis] || o[axis] > hi[axis])
                {
                    return false;
                }

                continue;
            }

            double a = (lo[axis] - o[axis]) / d[axis], b = (hi[axis] - o[axis]) / d[axis];
            t0 = Math.Max(t0, Math.Min(a, b));
            t1 = Math.Min(t1, Math.Max(a, b));
            if (t0 > t1)
            {
                return false;
            }
        }

        return true;
    }
}
