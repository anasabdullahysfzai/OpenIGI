using OpenIGI.Core.Numerics;
using OpenIGI.Game.Ai;
using OpenIGI.Game.Igi1.Ai;
using OpenIGI.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace OpenIGI.Game.Tests;

/// <summary>Guards at a post in the modern fight, in the test room (<see cref="ModernArena"/>). <b>Not retail.</b></summary>
[Collection(ModernAiTests.Name)]
public sealed class ModernGuardArenaTests(ITestOutputHelper output)
{
    private const double Metre = ModernArena.Metre;

    /// <summary>A guard at a post fights from positions near it, does not chase a man who leaves, and walks back when it is over.</summary>
    [RequiresGameDataFact]
    public void AGuardFightsNearHisPostAndWalksBackToIt()
    {
        With(() =>
        {
            ModernArena arena = new ModernArena(70.0).Box(0, 44, 0, 70, 45, 4.0);
            arena.Build();
            List<string> story = [];
            int tick = 0;
            Vector3d post = ModernArena.At(35, 10);
            Igi1HumanAi guard = arena.Patrol(post, 0f, text => story.Add($"{tick / 30.0:00.0}s {text}"), seed: 11, type: AiType.GuardAk, archetype: "guard");
            Vector3d player = ModernArena.At(35, 30), gone = ModernArena.At(35, 65);
            double furthest = 0.0;
            int fired = 0, moved = 0, leftAt = 40 * 30;
            for (tick = 1; tick <= 90 * 30; tick++)
            {
                arena.Tick(guard, tick, tick < leftAt ? player : gone);
                furthest = Math.Max(furthest, Vector3d.Distance(post, guard.Position) / Metre);
                fired += guard.Combat.PullTrigger ? 1 : 0;
                moved += guard.CoverState is "Moving" or "Advancing" ? 1 : 0;
            }

            double home = Vector3d.Distance(post, guard.Position) / Metre;
            output.WriteLine(string.Join("\n", story));
            output.WriteLine($"fired {fired} ticks, moving {moved} ticks, furthest {furthest:0.0} m, ends {home:0.0} m from his post, state {guard.CoverState}");
            Assert.True(moved > 0, "he stood where he was, the way the original's guard does");
            Assert.True(fired > 0, "he never fired");
            Assert.True(furthest <= Igi1HumanAi.PostLeashMetres + 2.0, $"he went {furthest:0.0} m from his post");
            Assert.DoesNotContain(story, line => line.Contains("hunts", StringComparison.Ordinal));
            Assert.Contains(story, line => line.Contains("walks back to his post", StringComparison.Ordinal));
            Assert.True(home < 2.0, $"he ended {home:0.0} m from his post");
        });
    }

    /// <summary>A sniper keeps the original's stand: he fires from where he is, and goes nowhere.</summary>
    [RequiresGameDataFact]
    public void ASniperStaysWhereHeIs()
    {
        With(() =>
        {
            ModernArena arena = new(70.0);
            arena.Build();
            List<string> story = [];
            int tick = 0;
            Vector3d post = ModernArena.At(35, 10);
            Igi1HumanAi sniper = arena.Patrol(post, 0f, text => story.Add($"{tick / 30.0:00.0}s {text}"), seed: 11, type: AiType.Sniper, archetype: "sniper");
            Vector3d player = ModernArena.At(35, 30);
            int fired = 0;
            Vector3d? fought = null;
            for (tick = 1; tick <= 30 * 30; tick++)
            {
                arena.Tick(sniper, tick, player);
                fired += sniper.Combat.PullTrigger ? 1 : 0;
                Assert.Equal("None", sniper.CoverState);
                fought ??= sniper.Activity == AiSoldierActivity.Engaging ? sniper.Position : null;
            }

            output.WriteLine(string.Join("\n", story));
            Assert.True(fired > 0, "he never fired");
            Assert.True(fought is { } place && Vector3d.Distance(place, sniper.Position) < 0.5 * Metre, "he moved in his fight");
        });
    }

    private static void With(Action run)
    {
        bool previous = GameplayCVars.ModernAi.Value;
        GameplayCVars.ModernAi.Value = true;
        try
        {
            run();
        }
        finally
        {
            GameplayCVars.ModernAi.Value = previous;
        }
    }
}
