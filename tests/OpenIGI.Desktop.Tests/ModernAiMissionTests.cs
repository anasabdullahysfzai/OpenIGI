using OpenIGI.Core.Numerics;
using OpenIGI.Engine.Flow;
using OpenIGI.Engine.Level;
using OpenIGI.Engine.Resources;
using OpenIGI.Engine.Tasks;
using OpenIGI.Engine.Time;
using OpenIGI.Engine.Vfs;
using OpenIGI.Formats.Ai;
using OpenIGI.Game;
using OpenIGI.Game.Ai;
using OpenIGI.Game.Igi1.Ai;
using OpenIGI.Game.Player;
using OpenIGI.Game.World;
using OpenIGI.Presentation.Igi1;
using OpenIGI.Rendering.Headless;
using OpenIGI.TestSupport;
using Xunit;

namespace OpenIGI.Desktop.Tests;

/// <summary>The modern AI on a retail mission's own guards, scripts and all: what the soldier fixtures without scripts cannot show.</summary>
[Collection(ModernAiMissionTests.Name)]
public sealed class ModernAiMissionTests
{
    /// <summary>The collection: these turn the modern AI on, which every soldier reads, so they run alone.</summary>
    public const string Name = "Modern AI on a mission";

    private const double Metre = 4096.0;

    /// <summary>
    /// A mission-1 guard who sees a standing player ten metres ahead fights him: the original at once, the modern AI once
    /// its suspicion is full. Seeing him and then walking off is the bug a play-test found.
    /// </summary>
    /// <param name="modern">Whether the modern AI is on.</param>
    [RequiresGameDataTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void AGuardWhoIsSureOfThePlayerFightsHim(bool modern)
    {
        bool previous = GameplayCVars.ModernAi.Value;
        GameplayCVars.ModernAi.Value = modern;
        try
        {
            using Fixture fixture = Fixture.Load(1);
            fixture.Renderer.Simulation.ObserveAllForTesting();
            Igi1HumanAi soldier = fixture.Renderer.Simulation.BrainsForTesting.First(brain =>
                brain.PlayerIsEnemy && Math.Abs(brain.CurrentEyeOrientation.M32) < 0.5f);

            Matrix3f eye = soldier.CurrentEyeOrientation;
            double length = Math.Sqrt((eye.M12 * eye.M12) + (eye.M22 * eye.M22));
            Vector3d player = new(
                soldier.Position.X + (eye.M12 / length * 10.0 * Metre),
                soldier.Position.Y + (eye.M22 / length * 10.0 * Metre),
                soldier.Position.Z);

            int sure = -1, fighting = -1;
            List<string> seen = [];
            soldier.ModernLog = seen.Add;
            for (int tick = 1; tick <= 600 && fighting < 0; tick++)
            {
                soldier.Tick(tick, player, rootMotionThisTick: 0.0);
                if (sure < 0 && soldier.SeesEnemy)
                {
                    sure = tick;
                }

                if (soldier.Activity == AiSoldierActivity.Engaging)
                {
                    fighting = tick;
                }
            }

            string story = $"activity {soldier.Activity}; last actions [{string.Join(", ", soldier.LastActions.Select(a => a.Name))}]; "
                + $"log: {string.Join(" | ", seen)}";
            Assert.True(sure > 0, $"the guard was never sure of the player; {story}");
            Assert.True(fighting > 0, $"sure on tick {sure}, the guard never fought; {story}");
        }
        finally
        {
            GameplayCVars.ModernAi.Value = previous;
        }
    }

    /// <summary>
    /// How long a sure guard takes to fight, with what runs in between: the modern AI's walk over must not finish first. The
    /// timeline is printed, and the two are compared.
    /// </summary>
    [RequiresGameDataFact]
    public void ASureGuardFightsAsSoonModernAsOriginal()
    {
        (int original, string originalStory) = SureToFight(modern: false);
        (int modern, string modernStory) = SureToFight(modern: true);
        _output.WriteLine($"original: {original} ticks; {originalStory}");
        _output.WriteLine($"modern:   {modern} ticks; {modernStory}");

        Assert.True(original >= 0 && modern >= 0, $"a guard never fought. original: {originalStory} | modern: {modernStory}");
        Assert.True(modern <= original + 6, $"modern took {modern} ticks from sure to fighting, the original {original}. {modernStory}");
    }

    /// <summary>
    /// While a guard walks over to investigate, the sight of an enemy, a round or a fight still reaches his script: the
    /// investigation must not outrank them. A play-test caught a sure guard finishing his walk and look, five seconds, first.
    /// </summary>
    [RequiresGameDataFact]
    public void AnInvestigationLetsTheScriptHearWhatOutranksAStep()
    {
        bool previous = GameplayCVars.ModernAi.Value;
        GameplayCVars.ModernAi.Value = true;
        try
        {
            using Fixture fixture = Fixture.Load(1);
            fixture.Renderer.Simulation.ObserveAllForTesting();
            Igi1HumanAi soldier = fixture.Renderer.Simulation.BrainsForTesting.First(brain =>
                brain.PlayerIsEnemy && Math.Abs(brain.CurrentEyeOrientation.M32) < 0.5f);
            Vector3d player = soldier.Position + new Vector3d(14.0 * Metre, 0, 0);
            soldier.Tick(1, player, rootMotionThisTick: 0.0);

            // A step behind him sends him to look.
            Assert.True(soldier.ReceiveWalk(player, player, 60.0 * Metre), "the guard did not hear the step");
            soldier.Tick(2, player, rootMotionThisTick: 0.0);
            Assert.NotNull(soldier.InvestigatingAt);

            IReadOnlyList<AiEvent> order = soldier.Events.PriorityOrder;
            _output.WriteLine(string.Join(", ", order.Select((e, i) => $"{i}:{e}")));
            foreach (AiEvent urgent in new[] { AiEvent.EnemyDetection, AiEvent.Combat, AiEvent.TakingDamage, AiEvent.Gunshot, AiEvent.Alert })
            {
                int rank = order.ToList().IndexOf(urgent);
                Assert.True(rank < 0 || rank < soldier.PriorityCapForTesting, $"{urgent} (rank {rank}) cannot reach a guard investigating (cap {soldier.PriorityCapForTesting})");
            }
        }
        finally
        {
            GameplayCVars.ModernAi.Value = previous;
        }
    }

    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    /// <summary>Initializes a new instance of the <see cref="ModernAiMissionTests"/> class.</summary>
    /// <param name="output">Where the timelines are printed.</param>
    public ModernAiMissionTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    // Fourteen metres ahead of mission 1's first level-eyed hostile guard: the ticks from sure to fighting, and what ran. (The fixture arms nobody, so no round is fired.)
    private static (int Ticks, string Story) SureToFight(bool modern)
    {
        bool previous = GameplayCVars.ModernAi.Value;
        GameplayCVars.ModernAi.Value = modern;
        try
        {
            using Fixture fixture = Fixture.Load(1);
            fixture.Renderer.Simulation.ObserveAllForTesting();
            Igi1HumanAi soldier = fixture.Renderer.Simulation.BrainsForTesting.First(brain =>
                brain.PlayerIsEnemy && Math.Abs(brain.CurrentEyeOrientation.M32) < 0.5f);
            Matrix3f eye = soldier.CurrentEyeOrientation;
            double length = Math.Sqrt((eye.M12 * eye.M12) + (eye.M22 * eye.M22));
            Vector3d player = new(
                soldier.Position.X + (eye.M12 / length * 14.0 * Metre),
                soldier.Position.Y + (eye.M22 / length * 14.0 * Metre),
                soldier.Position.Z);

            List<string> story = [];
            soldier.ModernLog = text => story.Add(text);
            string? running = null;
            int sure = -1;
            for (int tick = 1; tick <= 900; tick++)
            {
                soldier.Tick(tick, player, rootMotionThisTick: 0.0);
                if (soldier.RunningAction != running)
                {
                    running = soldier.RunningAction;
                    story.Add($"t{tick} runs {running ?? "nothing"}");
                }

                if (sure < 0 && soldier.SeesEnemy)
                {
                    sure = tick;
                    story.Add($"t{tick} sure");
                }

                if (soldier.Activity == AiSoldierActivity.Engaging)
                {
                    story.Add($"t{tick} engaging");
                    return (tick - sure, string.Join(" | ", story));
                }
            }

            return (-1, string.Join(" | ", story));
        }
        finally
        {
            GameplayCVars.ModernAi.Value = previous;
        }
    }

    private sealed class Fixture(SoldierRenderer renderer) : IDisposable
    {
        public SoldierRenderer Renderer { get; } = renderer;

        public static Fixture Load(int level)
        {
            VirtualFileSystem fileSystem = new();
            fileSystem.RegisterDevice(new LocalFileDevice(GameData.RequireRoot()));
            ResourceRegistry resources = new(fileSystem);
            GameLoop loop = new(new ManualTimeSource());
            PlayerTaskTypes.RegisterAll(loop.Types);

            MissionRegistry registry = new();
            registry.Register(loop.Natives);
            registry.Scan(fileSystem, resources, loop.Natives);

            LevelTask root = (LevelTask)loop.Tasks.New(loop.Tasks.Root, loop.Types.Get(EngineTaskTypes.Level));
            LoadedMission mission = new MissionLoader(
                registry,
                fileSystem,
                resources,
                new LevelLoader(root, loop.TaskCommands, loop.Natives, resources))
                .Load(level);

            SoldierRenderer renderer = new(new RecordingGraphicsDevice(), resources);
            Assert.True(renderer.Load(mission, root) > 0, $"Level {level} loaded no soldiers");
            Assert.True(
                renderer.Simulation.Animate(new Dictionary<int, GraphFile>(), levelRoot: root) > 0,
                $"Level {level} created no AI brains");
            return new Fixture(renderer);
        }

        public void Dispose() => Renderer.Dispose();
    }
}

/// <summary>Runs <see cref="ModernAiMissionTests"/> alone.</summary>
[CollectionDefinition(ModernAiMissionTests.Name, DisableParallelization = true)]
public sealed class ModernAiMissionTestsRunAlone;
