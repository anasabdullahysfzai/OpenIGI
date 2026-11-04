using OpenIGI.Core.Numerics;
using OpenIGI.Game.Ai;
using OpenIGI.Game.Igi1.Ai;
using OpenIGI.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace OpenIGI.Game.Tests;

/// <summary>Hunting for a player lost, and a team-mate found dead, in the test room (<see cref="ModernArena"/>). <b>Not retail.</b></summary>
[Collection(ModernAiTests.Name)]
public sealed class ModernSearchArenaTests(ITestOutputHelper output)
{
    /// <summary>
    /// A squad in a fight that loses the player hunts for him where he was last seen, each from nodes of his own, one down on a knee at
    /// the last; and goes back to his patrol still on edge.
    /// </summary>
    [RequiresGameDataFact]
    public void ASquadThatLosesThePlayerHuntsForHimAndStaysOnEdge()
    {
        With(() =>
        {
            ModernArena arena = new ModernArena(60.0).Box(0, 40, 0, 60, 41, 4.0);
            arena.Build();
            List<string> story = [];
            int tick = 0;
            List<Igi1HumanAi> guards = [];
            foreach ((double x, int id) in new[] { (22.0, 1), (30.0, 2), (38.0, 3) })
            {
                int n = id;
                guards.Add(arena.Patrol(ModernArena.At(x, 12), 0f, text => story.Add($"{tick / 30.0:00.0}s guard {n}: {text}"), seed: id + 10));
            }

            Vector3d player = ModernArena.At(30, 30);
            bool scanned = false;
            HashSet<int>[] places = [[], [], []];
            for (tick = 1; tick <= 90 * 30; tick++)
            {
                // In the open until they are all fighting, then right up to the wall, and round behind it.
                if (tick == 10 * 30)
                {
                    player = ModernArena.At(30, 38);
                }
                else if (tick == 11 * 30)
                {
                    player = ModernArena.At(30, 43);
                }

                for (int g = 0; g < guards.Count; g++)
                {
                    arena.Tick(guards[g], tick, player);
                    scanned |= guards[g].KneelScanning;
                    if (guards[g].IsSearching && guards[g].DestinationNode >= 0)
                    {
                        places[g].Add(guards[g].DestinationNode);
                    }
                }
            }

            output.WriteLine(string.Join("\n", story.Where(line => !line.Contains("sight", StringComparison.Ordinal) && !line.Contains("sees the", StringComparison.Ordinal))));
            output.WriteLine($"places: {string.Join(" | ", places.Select(spots => string.Join(",", spots)))}");
            Assert.Equal(3, story.Count(line => line.Contains("lost him; hunts", StringComparison.Ordinal)));
            Assert.Contains(story, line => line.Contains("says: lost", StringComparison.Ordinal));
            Assert.Contains(story, line => line.Contains("says: clear", StringComparison.Ordinal));
            Assert.True(scanned, "nobody knelt to look about");
            Assert.All(places, spots => Assert.True(spots.Count >= 3, "he did not walk his search"));
            Assert.True(places[0].Intersect(places[1]).Count() < places[0].Count, "they searched the same places");
            Assert.Equal(3, story.Count(line => line.Contains("search over", StringComparison.Ordinal)));
            Assert.All(guards, guard => Assert.True(guard.IsOnEdge, "he calmed down at once"));
        });
    }

    /// <summary>
    /// A guard who sees a team-mate down goes and checks him and waves the squad on; another nearby, who never saw him, answers. A body
    /// behind a guard is not noticed.
    /// </summary>
    [RequiresGameDataFact]
    public void AGuardFindsABodyChecksItAndCallsTheSquad()
    {
        With(() =>
        {
            ModernArena arena = new ModernArena(60.0).Box(0, 40, 0, 60, 41, 4.0);
            arena.Build();
            List<string> story = [];
            int tick = 0;
            Igi1HumanAi finder = arena.Patrol(ModernArena.At(30, 10), 0f, text => story.Add($"{tick / 30.0:00.0}s finder: {text}"), seed: 21);
            Igi1HumanAi other = arena.Patrol(ModernArena.At(44, 10), 0f, text => story.Add($"{tick / 30.0:00.0}s other: {text}"), seed: 22);
            Vector3d player = ModernArena.At(30, 50), body = ModernArena.At(30, 18);
            for (tick = 1; tick <= 60 * 30; tick++)
            {
                arena.Tick(finder, tick, player, body, other.Position);
                arena.Tick(other, tick, player);
            }

            output.WriteLine(string.Join("\n", story));
            Assert.Equal((1, 1), finder.SearchGestures);
            int saw = story.FindIndex(line => line.Contains("finder: sees a team-mate down", StringComparison.Ordinal));
            int check = story.FindIndex(line => line.Contains("finder: checks the body", StringComparison.Ordinal));
            int wave = story.FindIndex(line => line.Contains("finder: waves the squad on", StringComparison.Ordinal));
            Assert.Contains(story, line => line.Contains("finder: says: mandown", StringComparison.Ordinal));
            Assert.Contains(story, line => line.Contains("finder: says: advance", StringComparison.Ordinal));
            Assert.InRange(saw, 0, check);
            Assert.InRange(check, 0, wave);
            Assert.Contains(story, line => line.Contains("other: lost him; hunts", StringComparison.Ordinal));

            // With nobody about he checks the body and searches, and waves to no one.
            ModernArena empty = new ModernArena(60.0).Box(0, 40, 0, 60, 41, 4.0);
            empty.Build();
            List<string> alone = [];
            Igi1HumanAi lone = empty.Patrol(ModernArena.At(30, 10), 0f, alone.Add, seed: 24);
            for (tick = 1; tick <= 40 * 30; tick++)
            {
                empty.Tick(lone, tick, player, body);
            }

            output.WriteLine(string.Join("\n", alone));
            Assert.Equal((1, 0), lone.SearchGestures);
            Assert.Contains("nobody to wave to", alone);
            Assert.Contains(alone, line => line.StartsWith("search over", StringComparison.Ordinal));

            // A mate behind a wall does not see him wave either.
            ModernArena walled = new ModernArena(60.0).Box(0, 40, 0, 60, 41, 4.0).Box(25, 24, 0, 35, 25, 4.0);
            walled.Build();
            Igi1HumanAi behind = walled.Patrol(ModernArena.At(30, 12), 0f, _ => { }, seed: 24);
            for (tick = 1; tick <= 40 * 30; tick++)
            {
                walled.Tick(behind, tick, player, ModernArena.At(30, 20), ModernArena.At(30, 30));
            }

            Assert.Equal((1, 0), behind.SearchGestures);

            // The same body behind a lone guard.
            ModernArena second = new ModernArena(60.0).Box(0, 40, 0, 60, 41, 4.0);
            second.Build();
            Igi1HumanAi blind = second.Patrol(ModernArena.At(30, 20), 0f, _ => { }, seed: 23);
            for (tick = 1; tick <= 15 * 30; tick++)
            {
                second.Tick(blind, tick, player, ModernArena.At(30, 12));
            }

            Assert.Equal((0, 0), blind.SearchGestures);
            Assert.False(blind.IsOnEdge, "he noticed a body behind him");
        });
    }

    /// <summary>
    /// A squad with cover in reach fights from it: few positions in the open, none far from the player when nearer ones will do.
    /// </summary>
    [RequiresGameDataFact]
    public void ASquadFightsFromCoverAndAtAUsefulRange()
    {
        With(() =>
        {
            ModernArena arena = new ModernArena(60.0)
                .Box(14, 20, 0, 18, 20.6, 1.0).Box(28, 20, 0, 32, 20.6, 1.0).Box(42, 20, 0, 46, 20.6, 1.0)
                .Box(21, 24, 0, 22.5, 25.5, 3.0).Box(37, 24, 0, 38.5, 25.5, 3.0)
                .Box(8, 30, 0, 12, 30.6, 1.0).Box(48, 30, 0, 52, 30.6, 1.0);
            arena.Build();
            List<string> story = [];
            int tick = 0;
            List<Igi1HumanAi> guards = [];
            foreach ((double x, int id) in new[] { (20.0, 1), (30.0, 2), (40.0, 3) })
            {
                guards.Add(arena.Patrol(ModernArena.At(x, 10), 0f, story.Add, seed: id + 30));
            }

            Vector3d player = ModernArena.At(30, 40);
            for (tick = 1; tick <= 45 * 30; tick++)
            {
                player = ModernArena.At(30 + (10 * Math.Sin(tick / 120.0)), 40);
                foreach (Igi1HumanAi guard in guards)
                {
                    arena.Tick(guard, tick, player);
                }
            }

            List<string> positions = [.. story.Where(line => line.Contains(" a position ", StringComparison.Ordinal))];
            int open = positions.Count(line => line.Contains("no cover", StringComparison.Ordinal));
            double furthest = positions.Select(line =>
                double.Parse(line.Split(", ")[1].Split(" m from")[0], System.Globalization.CultureInfo.InvariantCulture)).DefaultIfEmpty().Max();
            output.WriteLine($"POSITIONS {positions.Count}, in the open {open}, furthest {furthest:0.0} m");
            output.WriteLine(string.Join("\n", positions));
            Assert.True(positions.Count >= 3, "they took no positions");
            Assert.True(open * 4 <= positions.Count, $"{open} of {positions.Count} positions in the open");
            Assert.True(furthest <= 32.0, $"a position {furthest:0.0} m from him");
        });
    }

    /// <summary>Every callout a guard can say is on disk for each voice, and reads as the sound the game plays.</summary>
    [Fact]
    public void EveryCalloutFileReadsAsPlayableSound()
    {
        string folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../voices/callouts"));
        if (!Directory.Exists(folder))
        {
            return; // the voices are generated beside the checkout, not kept in it
        }

        foreach (string key in new[] { "flank", "cover", "grenade", "pinned", "reload", "lost", "mandown", "advance", "clear" })
        {
            foreach ((int voice, int wording) in new[] { (0, 1), (0, 2), (1, 1), (1, 2), (2, 1), (2, 2) })
            {
                (OpenIGI.Audio.AudioFormat Format, byte[] Pcm)? read =
                    OpenIGI.Game.Igi1.World.Igi1MissionAudio.ReadWave(File.ReadAllBytes(Path.Combine(folder, $"callout_{voice}_{key}_{wording}.wav")));
                Assert.NotNull(read);
                Assert.Equal(new OpenIGI.Audio.AudioFormat(22050, 1, 16), read.Value.Format);
                Assert.InRange(read.Value.Pcm.Length, 22050 / 4, 22050 * 2 * 4);
            }
        }
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
