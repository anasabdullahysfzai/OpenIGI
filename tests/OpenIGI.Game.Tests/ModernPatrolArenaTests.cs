using OpenIGI.Core.Numerics;
using OpenIGI.Formats.Ai;
using OpenIGI.Game.Ai;
using OpenIGI.Game.Igi1.Ai;
using OpenIGI.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace OpenIGI.Game.Tests;

/// <summary>A patrol walked between fights in the test room (<see cref="ModernArena"/>). <b>Not retail.</b></summary>
[Collection(ModernAiTests.Name)]
public sealed class ModernPatrolArenaTests(ITestOutputHelper output)
{
    /// <summary>
    /// A guard sent up and down a room stops now and then for a few seconds, looks about him, and still walks his whole path; two
    /// guards do not keep step. With the modern AI off he walks it without a stop, as the original does.
    /// </summary>
    [RequiresGameDataFact]
    public void AGuardOnPatrolStopsLooksAboutAndStillWalksHisPath()
    {
        (int Stops, int Longest, int Glances, int Legs, int Walking) One(bool modern, int seed)
        {
            bool previous = GameplayCVars.ModernAi.Value;
            GameplayCVars.ModernAi.Value = modern;
            try
            {
                ModernArena arena = new ModernArena().Box(0, 22, 0, 30, 23, 4.0); // the player is out of sight behind a wall
                GraphFile graph = arena.Build();
                int Near(double x, double y) => graph.Nodes.OrderBy(n => Vector3d.Distance(n.Position, ModernArena.At(x, y))).First().Id;
                int a = Near(4, 4), b = Near(26, 4), c = Near(26, 16), d = Near(4, 16);
                AiPatrolCommand[] commands = [.. Enumerable.Range(0, 12)
                    .SelectMany(_ => new[] { a, b, c, d }).Select(n => new AiPatrolCommand(AiPatrolCommandKind.WalkTo, n))];
                AiPatrolPathRegistry paths = new();
                paths.Add(new AiPatrolPath(1, commands));
                Igi1HumanAi guard = arena.Patrol(ModernArena.At(4, 4), 0f, _ => { }, seed: seed);
                guard.SetPatrolPaths(paths);
                guard.Tick(0, ModernArena.At(15, 28) + new Vector3d(0, 0, 1.6 * ModernArena.Metre), rootMotionThisTick: 0.0, enemyFeet: ModernArena.At(15, 28));
                guard.ApplyActions([new AiProducedAction("AIAction_Patrol", [1, 0, 0], 0)]);

                int longest = 0, still = 0, glances = 0, walking = 0;
                bool glancing = false;
                for (int tick = 1; tick <= 180 * 30; tick++)
                {
                    Vector3d before = guard.Position;
                    arena.Tick(guard, tick, ModernArena.At(15, 28));
                    bool moved = Vector3d.Distance(before, guard.Position) > 1.0;
                    walking += moved ? 1 : 0;
                    still = moved ? 0 : still + 1;
                    longest = Math.Max(longest, still);
                    glances += guard.PatrolGlance is not null && !glancing ? 1 : 0;
                    glancing = guard.PatrolGlance is not null;
                }

                return (guard.PatrolStops, longest, glances, guard.Route!.CurrentIndex, walking);
            }
            finally
            {
                GameplayCVars.ModernAi.Value = previous;
            }
        }

        (int Stops, int Longest, int Glances, int Legs, int Walking) first = One(true, 14), second = One(true, 36), original = One(false, 14);
        output.WriteLine($"guard 14 {first}\nguard 36 {second}\noriginal {original}");

        Assert.InRange(first.Stops, 3, 20);
        Assert.InRange(first.Longest, Igi1HumanAi.PatrolStopTicks, Igi1HumanAi.LookAroundTicks + 30);
        Assert.True(first.Glances >= 10, "he hardly looks about him");
        Assert.True(first.Legs >= 8, "the stops kept him from walking his path");
        Assert.NotEqual(first.Walking, second.Walking);
        Assert.Equal(0, original.Stops);
        Assert.Equal(0, original.Glances);
    }

    /// <summary>
    /// A guard walking round a room keeps his turn to a man's and still arrives at every leg's end, and sways a little on the long
    /// straights.
    /// </summary>
    [RequiresGameDataFact]
    public void AGuardCurvesThroughHisCornersAndStillGetsThere()
    {
        bool previous = GameplayCVars.ModernAi.Value;
        GameplayCVars.ModernAi.Value = true;
        try
        {
            {
                ModernArena arena = new ModernArena().Box(0, 22, 0, 30, 23, 4.0);
                GraphFile graph = arena.Build(spacing: 10.0);
                int Near(double x, double y) => graph.Nodes.OrderBy(n => Vector3d.Distance(n.Position, ModernArena.At(x, y))).First().Id;
                int a = Near(4, 4), b = Near(26, 4), c = Near(26, 16), d = Near(4, 16);
                AiPatrolCommand[] commands = [.. Enumerable.Range(0, 6)
                    .SelectMany(_ => new[] { a, b, c, d }).Select(n => new AiPatrolCommand(AiPatrolCommandKind.WalkTo, n))];
                AiPatrolPathRegistry paths = new();
                paths.Add(new AiPatrolPath(1, commands));
                Igi1HumanAi guard = arena.Patrol(ModernArena.At(4, 4), 0f, _ => { }, seed: 11);
                guard.SetPatrolPaths(paths);
                guard.Tick(0, ModernArena.At(15, 28) + new Vector3d(0, 0, 1.6 * ModernArena.Metre), rootMotionThisTick: 0.0, enemyFeet: ModernArena.At(15, 28));
                guard.ApplyActions([new AiProducedAction("AIAction_Patrol", [1, 0, 0], 0)]);

                double sharpest = 0.0, least = double.MaxValue, most = double.MinValue;
                for (int tick = 1; tick <= 120 * 30; tick++)
                {
                    Vector3d before = guard.Position;
                    float facing = guard.Facing;
                    arena.Tick(guard, tick, ModernArena.At(15, 28));
                    if (Vector3d.Distance(before, guard.Position) > 1.0 && guard.PatrolGlance is null)
                    {
                        sharpest = Math.Max(sharpest, Math.Abs(Igi1AiGeometry.Wrap(guard.Facing - facing)));
                    }

                    // The middle of the long leg along y = 4: how far his line strays from straight.
                    if (guard.Position.X is > 10 * ModernArena.Metre and < 20 * ModernArena.Metre && guard.Position.Y < 8 * ModernArena.Metre)
                    {
                        least = Math.Min(least, guard.Position.Y);
                        most = Math.Max(most, guard.Position.Y);
                    }
                }

                double sway = (most - least) / ModernArena.Metre;
                output.WriteLine($"sharpest {sharpest}, sway {sway} m, legs {guard.Route!.CurrentIndex}");
                Assert.True(sharpest <= Igi1HumanAi.ModernTurnPerTick + 1e-3, $"he snapped round {sharpest} radians in a tick");
                Assert.True(guard.Route!.CurrentIndex >= 8, $"he walked only {guard.Route!.CurrentIndex} legs in two minutes");
                Assert.InRange(sway, 0.02, 0.5);
            }
        }
        finally
        {
            GameplayCVars.ModernAi.Value = previous;
        }
    }

    /// <summary>A round in the leg slows him for good, and tells which leg; a round elsewhere does not, nor one with the modern AI off.</summary>
    [RequiresGameDataFact]
    public void AGuardShotInTheLegLimpsForGood()
    {
        bool previous = GameplayCVars.ModernAi.Value;
        GameplayCVars.ModernAi.Value = true;
        try
        {
            (ModernArena arena, Igi1HumanAi guard) = Walker(seed: 14);
            double Usual(int from, int to)
            {
                List<double> paces = [];
                for (int tick = from; tick < to; tick++)
                {
                    Vector3d before = guard.Position;
                    arena.Tick(guard, tick, ModernArena.At(15, 28));
                    double moved = Vector3d.Distance(before, guard.Position);
                    if (moved > 1.0)
                    {
                        paces.Add(moved * 30.0 / ModernArena.Metre);
                    }
                }

                paces.Sort();
                return paces[paces.Count / 2];
            }

            double whole = Usual(1, 300);
            guard.WoundLeg(12);
            Assert.Equal(0, guard.LimpLeg);
            guard.WoundLeg(8);
            Assert.Equal(-1, guard.LimpLeg);
            guard.WoundLeg(0);
            Assert.Equal(1, guard.LimpLeg);
            double hurt = Usual(300, 900);
            output.WriteLine($"usually {whole} m/s whole, {hurt} m/s wounded");
            Assert.True(whole > Igi1HumanAi.LimpWalkMetresPerSecond + 0.1, $"he walked only {whole} m/s before");
            Assert.InRange(hurt, 0.5, Igi1HumanAi.LimpWalkMetresPerSecond + 0.01);

            GameplayCVars.ModernAi.Value = false;
            (_, Igi1HumanAi original) = Walker(seed: 14);
            original.WoundLeg(8);
            Assert.Equal(0, original.LimpLeg);
        }
        finally
        {
            GameplayCVars.ModernAi.Value = previous;
        }
    }

    /// <summary>On patrol his sight turns with his head: the eye a sweep tests against leaves his facing, both ways.</summary>
    [RequiresGameDataFact]
    public void HisSightTurnsWithHisGaze()
    {
        bool previous = GameplayCVars.ModernAi.Value;
        GameplayCVars.ModernAi.Value = true;
        try
        {
            (ModernArena arena, Igi1HumanAi guard) = Walker(seed: 14);
            double wide = 0.0;
            bool left = false, right = false;
            for (int tick = 1; tick <= 400 * 30; tick++)
            {
                arena.Tick(guard, tick, ModernArena.At(15, 28));
                Vector3f eye = guard.CurrentEyeOrientation.Column2, body = guard.Orientation.Column2;
                double aside = Math.Atan2((body.X * eye.Y) - (body.Y * eye.X), (body.X * eye.X) + (body.Y * eye.Y));
                wide = Math.Max(wide, Math.Abs(aside));
                left |= aside > 0.3;
                right |= aside < -0.3;
            }

            output.WriteLine($"widest {wide * 180.0 / Math.PI:0} degrees");
            Assert.True(left && right, "his sight never turned both ways");
            Assert.InRange(wide, 1.9, 2.5); // a look around reaches 136 degrees
        }
        finally
        {
            GameplayCVars.ModernAi.Value = previous;
        }
    }

    /// <summary>
    /// Part way through a looking-around stop his head has swung to his right: a man standing square on his right is noticed, and
    /// one square on his left, though no further off, is not.
    /// </summary>
    [RequiresGameDataFact]
    public void WhereHisHeadSwingsHeNotices()
    {
        bool previous = GameplayCVars.ModernAi.Value;
        GameplayCVars.ModernAi.Value = true;
        try
        {
            static double Stand(double side)
            {
                (ModernArena arena, Igi1HumanAi guard) = Walker(seed: 14, size: 60.0, wall: 45.0);
                Vector3d hidden = ModernArena.At(15, 50);
                int stops = 0, began = -1;
                Vector3d player = hidden;
                for (int tick = 1; tick <= 400 * 30; tick++)
                {
                    arena.Tick(guard, tick, player);
                    if (began < 0 && guard.PatrolStops != stops)
                    {
                        stops = guard.PatrolStops;
                        if (guard.PatrolStopPick % 2 == 0)
                        {
                            began = tick;
                            Vector3f right = guard.Orientation.Column1;
                            player = guard.Position + (new Vector3d(right.X, right.Y, 0.0) * (side * 6.0 * ModernArena.Metre));
                        }
                    }

                    if (began >= 0 && tick == began + 90)
                    {
                        return guard.PlayerSuspicion;
                    }
                }

                throw new InvalidOperationException("he never stopped to look around");
            }

            double right = Stand(1.0), left = Stand(-1.0);
            output.WriteLine($"man on his right {right}, on his left {left}");
            Assert.True(right > 0.05, $"his head swung right and he did not notice: {right}");
            Assert.Equal(0.0, left);
        }
        finally
        {
            GameplayCVars.ModernAi.Value = previous;
        }
    }

    /// <summary>
    /// A man his sweep finds, well off to the side: he breaks off the stop to look, walks over, and is sure within seconds. The stop
    /// does not take up again afterwards.
    /// </summary>
    [RequiresGameDataFact]
    public void HisSweepFindingAManBreaksOffTheStop()
    {
        bool previous = GameplayCVars.ModernAi.Value;
        GameplayCVars.ModernAi.Value = true;
        try
        {
            (ModernArena arena, Igi1HumanAi guard) = Walker(seed: 14, size: 60.0, wall: 45.0);
            List<string> story = [];
            guard.ModernLog = story.Add;
            int began = -1, stops = 0;
            Vector3d player = ModernArena.At(15, 50);
            for (int tick = 1; tick <= 400 * 30 && (began < 0 || tick - began <= 8 * 30); tick++)
            {
                arena.Tick(guard, tick, player);
                if (began < 0 && guard.PatrolStops != stops)
                {
                    stops = guard.PatrolStops;
                    if (guard.PatrolStopPick % 2 == 0)
                    {
                        began = tick;
                        Vector3f right = guard.Orientation.Column1;
                        player = guard.Position + (new Vector3d(right.X, right.Y, 0.0) * (14.0 * ModernArena.Metre));
                    }
                }
            }

            output.WriteLine(string.Join("\n", story));
            Assert.Contains(story, line => line.StartsWith("looks at something", StringComparison.Ordinal));
            Assert.Contains(story, line => line.StartsWith("walks over", StringComparison.Ordinal));
            Assert.Contains(story, line => line.StartsWith("is sure", StringComparison.Ordinal));
            Assert.False(guard.OnPatrol, "he went back to his stop");
        }
        finally
        {
            GameplayCVars.ModernAi.Value = previous;
        }
    }

    /// <summary>
    /// A man right behind him is sensed over a few seconds, though outside the cone; a man further back, or behind a wall, is not.
    /// </summary>
    [RequiresGameDataFact]
    public void HeSensesAManRightBesideHimAndOnlyThen()
    {
        bool previous = GameplayCVars.ModernAi.Value;
        GameplayCVars.ModernAi.Value = true;
        try
        {
            static (double Suspicion, int SureAt) Stand(Vector3d player, bool wall)
            {
                ModernArena arena = new();
                if (wall)
                {
                    arena.Box(10, 13.5, 0, 20, 14.5, 4.0);
                }

                arena.Build();
                Igi1HumanAi guard = arena.Patrol(ModernArena.At(15, 15), 0f, _ => { }); // he faces +Y; the player is at lower Y
                int sure = -1;
                for (int tick = 1; tick <= 12 * 30; tick++)
                {
                    arena.Tick(guard, tick, player);
                    sure = sure < 0 && guard.PlayerSuspicion >= 1.0 ? tick : sure;
                }

                return (guard.PlayerSuspicion, sure);
            }

            (double Suspicion, int SureAt) behind = Stand(ModernArena.At(15, 13.5), false), far = Stand(ModernArena.At(15, 10), false), walled = Stand(ModernArena.At(15, 13.2), true);
            output.WriteLine($"behind {behind}, far {far}, walled {walled}");
            Assert.InRange(behind.SureAt, 2 * 30, 12 * 30);
            Assert.Equal(0.0, far.Suspicion);
            Assert.Equal(0.0, walled.Suspicion);
        }
        finally
        {
            GameplayCVars.ModernAi.Value = previous;
        }
    }

    // A guard sent round a room, the player out of sight behind a wall.
    private static (ModernArena Arena, Igi1HumanAi Guard) Walker(int seed, double size = 30.0, double wall = 22.0)
    {
        ModernArena arena = new ModernArena(size).Box(0, wall, 0, size, wall + 1.0, 4.0);
        GraphFile graph = arena.Build();
        int Near(double x, double y) => graph.Nodes.OrderBy(n => Vector3d.Distance(n.Position, ModernArena.At(x, y))).First().Id;
        int a = Near(4, 4), b = Near(26, 4), c = Near(26, 16), d = Near(4, 16);
        AiPatrolPathRegistry paths = new();
        paths.Add(new AiPatrolPath(1, [.. Enumerable.Range(0, 12)
            .SelectMany(_ => new[] { a, b, c, d }).Select(n => new AiPatrolCommand(AiPatrolCommandKind.WalkTo, n))]));
        Igi1HumanAi guard = arena.Patrol(ModernArena.At(4, 4), 0f, _ => { }, seed: seed);
        guard.SetPatrolPaths(paths);
        guard.Tick(0, ModernArena.At(15, 28) + new Vector3d(0, 0, 1.6 * ModernArena.Metre), rootMotionThisTick: 0.0, enemyFeet: ModernArena.At(15, 28));
        guard.ApplyActions([new AiProducedAction("AIAction_Patrol", [1, 0, 0], 0)]);
        return (arena, guard);
    }
}
