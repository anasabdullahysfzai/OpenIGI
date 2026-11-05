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

    /// <summary>Down behind tall cover, a man who can see round it leans out and fires from where he is, his eye out with him, and comes back in; with no clips to show it, he steps out as before.</summary>
    [RequiresGameDataTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void AManBehindTallCoverLeansOutToFire(bool canLean)
    {
        With(() =>
        {
            ModernArena arena = new ModernArena(60.0).Box(26, 14, 0, 34, 15, 3.0);
            arena.Build();
            List<string> story = [];
            int tick = 0;
            Igi1HumanAi guard = arena.Patrol(ModernArena.At(23, 11), 0f, text => story.Add($"{tick / 30.0:00.0}s {text}"), seed: 11);
            guard.CanLean = canLean;
            Vector3d player = ModernArena.At(30, 30);
            int leaning = 0, fired = 0, clearEye = 0;
            Vector3d? from = null;
            double furthest = 0.0, worst = 0.0;
            for (tick = 1; tick <= 60 * 30; tick++)
            {
                if (tick % 40 == 0 && guard.CoverState == "Firing")
                {
                    arena.Shoot(guard, player, guard.Position + new Vector3d(1.0 * Metre, 0, 1.2 * Metre));
                }

                arena.Tick(guard, tick, player);
                if (guard.LeanSide != 0 && guard.CoverState == "Firing")
                {
                    leaning++;
                    from ??= guard.Position;
                    worst = Math.Max(worst, Vector3d.Distance(from.Value, guard.Position) / Metre);
                    fired += guard.Combat.PullTrigger ? 1 : 0;
                    clearEye += guard.CanSeeForTesting(guard.EyePosition(), player + new Vector3d(0, 0, 1.2 * Metre)) ? 1 : 0;
                    furthest = Math.Max(furthest, Vector3d.Distance(guard.EyePosition(), guard.EyePosition() - guard.LeanOffset) / Metre);
                }
                else
                {
                    from = null;
                }
            }

            int outs = story.Count(line => line.Contains("leans out", StringComparison.Ordinal));
            output.WriteLine(string.Join("\n", story.Where(s => !s.Contains("says", StringComparison.Ordinal))));
            output.WriteLine($"{outs} leans, {leaning} ticks, {fired} firing, eye clear {clearEye}, drifted {worst:0.00} m, furthest {furthest:0.00} m");
            if (!canLean)
            {
                Assert.Equal(0, outs);
                return;
            }

            Assert.True(outs > 0, "he never leaned out");
            Assert.True(fired > 0, "he never fired from his lean");
            Assert.True(clearEye > leaning / 2, $"his eye cleared the wall for {clearEye} of {leaning} ticks");
            Assert.True(worst < 0.2, $"he drifted {worst:0.00} m while leaning");
            Assert.InRange(furthest, Igi1HumanAi.LeanRightMetres, Igi1HumanAi.LeanLeftMetres + 0.1);
        });
    }

    /// <summary>A flashbang has him blinded for as long as his script's clip for it runs, so that a modern body can show it, and then he is not.</summary>
    [RequiresGameDataFact]
    public void AFlashbangBlindsHimUntilHisClipEnds()
    {
        With(() =>
        {
            ModernArena arena = new(70.0);
            arena.Build();
            int tick = 0;
            Igi1HumanAi guard = arena.Patrol(ModernArena.At(35, 10), 0f, _ => { }, seed: 11, type: AiType.GuardAk, archetype: "guard");
            Vector3d away = ModernArena.At(35, 60);
            for (tick = 1; tick <= 30; tick++)
            {
                arena.Tick(guard, tick, away);
                Assert.False(guard.Blinded);
            }

            Assert.True(guard.ReceiveFlashbang(guard.Position + new Vector3d(0.0, 3.0 * Metre, 1.0 * Metre)));
            int blinded = 0;
            for (; tick <= 60; tick++)
            {
                arena.Tick(guard, tick, away);
                blinded += guard.Blinded ? 1 : 0;
            }

            Assert.True(blinded > 0, "the flashbang did not blind him");
            guard.ReportAnimationFinished();
            arena.Tick(guard, tick, away);
            Assert.False(guard.Blinded);
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
