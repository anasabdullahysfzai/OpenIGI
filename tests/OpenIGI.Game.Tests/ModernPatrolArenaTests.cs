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

        (int Stops, int Longest, int Glances, int Legs, int Walking) first = One(true, 11), second = One(true, 23), original = One(false, 11);
        output.WriteLine($"guard 11 {first}\nguard 23 {second}\noriginal {original}");

        Assert.InRange(first.Stops, 3, 20);
        Assert.InRange(first.Longest, Igi1HumanAi.PatrolStopTicks, Igi1HumanAi.PatrolStopTicks + Igi1HumanAi.PatrolStopExtraTicks + 30);
        Assert.True(first.Glances >= 10, "he hardly looks about him");
        Assert.True(first.Legs >= 8, "the stops kept him from walking his path");
        Assert.NotEqual(first.Walking, second.Walking);
        Assert.Equal(0, original.Stops);
        Assert.Equal(0, original.Glances);
    }
}
