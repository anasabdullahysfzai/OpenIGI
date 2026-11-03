using OpenIGI.Core.Numerics;
using OpenIGI.Game.Ai;
using OpenIGI.Game.Igi1.Ai;
using OpenIGI.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace OpenIGI.Game.Tests;

/// <summary>Squad fights in the test room (<see cref="ModernArena"/>), drawn from above every two seconds. <b>Not retail.</b></summary>
[Collection(ModernAiTests.Name)]
public sealed class ModernSquadArenaTests(ITestOutputHelper output)
{
    private const double Metre = ModernArena.Metre;

    /// <summary>
    /// Three guards against a player behind a low wall across a courtyard, who strafes along it firing at whoever he can see.
    /// They should each find somewhere to fight from, fire, get down, keep apart, and one of them go round his side.
    /// </summary>
    [RequiresGameDataFact]
    public void ThreeGuardsFightAPlayerAcrossACourtyard()
    {
        bool previous = GameplayCVars.ModernAi.Value;
        GameplayCVars.ModernAi.Value = true;
        try
        {
            ModernArena arena = new ModernArena()
                .Box(12, 23, 0, 18, 23.6, 1.0)      // the player's low wall
                .Box(7, 15, 0, 8.5, 16.5, 3.0)      // pillars
                .Box(21.5, 15, 0, 23, 16.5, 3.0)
                .Box(4, 10, 0, 9, 10.6, 1.0)        // low walls
                .Box(21, 10, 0, 26, 10.6, 1.0)
                .Box(13, 16, 0, 17, 16.6, 1.0)
                .Box(2, 20, 0, 3, 26, 3.0)          // a tall wall up the left
                .Box(27, 18, 0, 28, 24, 3.0);       // and the right
            arena.Build();
            List<string> story = [];
            int tick = 0;
            List<Igi1HumanAi> guards = [];
            foreach ((double x, double y, int id) in new[] { (10.0, 4.0, 1), (15.0, 3.0, 2), (20.0, 4.0, 3) })
            {
                int n = id;
                guards.Add(arena.Patrol(ModernArena.At(x, y), 0f, text => story.Add($"{tick / 30.0:00.00}s  guard {n}: {text}"), seed: id));
            }

            Dictionary<(int, string), int> ticksIn = [];
            int shots = 0;
            for (tick = 1; tick <= 40 * 30; tick++)
            {
                // Along his wall and back, at a walk.
                double leg = (tick * 1.5 / 30.0) % 28.0;
                double px = leg < 14.0 ? 15.0 + (leg < 7.0 ? leg : 14.0 - leg) : 15.0 - (leg < 21.0 ? leg - 14.0 : 28.0 - leg);
                Vector3d feet = ModernArena.At(px, 25);
                Vector3d eye = feet + new Vector3d(0, 0, 1.6 * Metre);
                if (tick % 15 == 0 && guards.Where(g => arena.Clear(eye, g.Position + new Vector3d(0, 0, 1.5 * Metre)))
                        .OrderBy(g => Vector3d.Distance(g.Position, feet)).FirstOrDefault() is { } mark)
                {
                    shots++;
                    double wide = shots % 4 == 0 ? 0.0 : 1.0;
                    bool hit = arena.Shoot(mark, eye, mark.Position + new Vector3d(wide * Metre, 0, 1.2 * Metre), heard: 30.0);
                    story.Add($"{tick / 30.0:00.00}s  -- player fires at guard {guards.IndexOf(mark) + 1}, {(hit ? "hits" : "misses")}");
                }

                foreach (Igi1HumanAi guard in guards)
                {
                    arena.Tick(guard, tick, feet);
                    (int, string) key = (guards.IndexOf(guard) + 1, guard.CoverState);
                    ticksIn[key] = ticksIn.GetValueOrDefault(key) + 1;
                    if (guard.Strafing || guard.CrouchMoving)
                    {
                        (int, string) how = (key.Item1, guard.Strafing ? "strafing" : "crouching");
                        ticksIn[how] = ticksIn.GetValueOrDefault(how) + 1;
                    }
                }

                if (tick % 60 == 0)
                {
                    story.Add($"{tick / 30.0:00.00}s map:" + Environment.NewLine + arena.Map(guards, feet));
                }
            }

            foreach (int g in new[] { 1, 2, 3 })
            {
                output.WriteLine($"guard {g}: " + string.Join(", ", ticksIn.Where(k => k.Key.Item1 == g).Select(k => $"{k.Key.Item2} {k.Value / 30.0:0.0}s")));
            }

            output.WriteLine(string.Join(Environment.NewLine, story));
            Assert.All(guards, g => Assert.True(ticksIn.GetValueOrDefault((guards.IndexOf(g) + 1, "Firing")) > 60, $"guard {guards.IndexOf(g) + 1} hardly fired"));
        }
        finally
        {
            GameplayCVars.ModernAi.Value = previous;
        }
    }
}
