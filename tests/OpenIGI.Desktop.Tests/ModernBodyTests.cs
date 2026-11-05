using System.Numerics;
using OpenIGI.Core.Graphics;
using OpenIGI.Presentation.Igi1;
using OpenIGI.Rendering.Headless;
using Xunit;

namespace OpenIGI.Desktop.Tests;

/// <summary>
/// The modern bodies on a real character: read whole, stood upright at a man's height, facing the way a soldier faces, and
/// walking on the spot so the soldier's own move carries him. The character is the play-test's, outside the repository.
/// </summary>
public sealed class ModernBodyTests(Xunit.Abstractions.ITestOutputHelper output)
{
    /// <summary>A limping man is never fast enough to count as running when he is shot dead, or he would skip the deaths for a man on his feet.</summary>
    [Fact]
    public void ALimpingManDoesNotCountAsRunningWhenHeDies() =>
        Assert.True(OpenIGI.Game.Igi1.Ai.Igi1HumanAi.LimpRunMetresPerSecond < SoldierRenderer.RagdollRunningMetresPerSecond);

    /// <summary>Where the character is: <c>OPENIGI_MODERN_BODY</c>, else the play-test's file beside the checkout.</summary>
    public static readonly string File = Environment.GetEnvironmentVariable("OPENIGI_MODERN_BODY")
        ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../animations/gltf/swat_guy_game.glb"));

    /// <summary>A pose handed to the ragdoll comes back the same, or the takeover would jump; and the rig has every body it should.</summary>
    [RequiresModernBodyFact]
    public void APoseGoesThroughTheRagdollAndComesBackTheSame()
    {
        RecordingGraphicsDevice device = new();
        using ModernBodyAsset asset = ModernBodyAsset.Load(device, File);
        const double Units = 4096.0;
        ModernRagdoll ragdoll = ModernRagdoll.Build(asset, Units) ?? throw new InvalidOperationException("no rig");
        Assert.Equal(16, ragdoll.Rig.BodyCount);

        // Measured from his skin, not guessed from his bones: a trunk the thickness of a guess sinks into the floor.
        Assert.InRange(ragdoll.Rig.BodyAt(0).Radius / Units, 0.1, 0.25);
        Assert.InRange(ragdoll.Rig.BodyAt(2).Radius / Units, 0.1, 0.25);
        output.WriteLine($"bones {ragdoll.Rig.BoneCount}, mass {ragdoll.Rig.TotalMass:0.0} kg");
        foreach ((RagdollBoneView body, int index) in ragdoll.Rig.Bodies.Select((b, i) => (new RagdollBoneView(b.HalfLength / Units, b.Radius / Units, 1.0 / b.InverseMass), i)))
        {
            output.WriteLine($"  {ragdoll.Rig.Profile.Bodies[index].Name}: half {body.Half:0.00} m, radius {body.Radius:0.00} m, {body.Mass:0.0} kg");
        }

        System.Numerics.Matrix4x4[] world = new System.Numerics.Matrix4x4[asset.Rest.Length];
        System.Numerics.Matrix4x4[] skin = new System.Numerics.Matrix4x4[asset.Joints.Length];
        asset.Pose(0.4f, 0f, 0f, world, skin);

        OpenIGI.Core.Numerics.Vector3d where = new(100000.0, -50000.0, 7000.0);
        OpenIGI.Core.Numerics.Matrix3f facing = OpenIGI.Core.Numerics.Matrix3f.FromEngineEulerAngles(0f, 0f, 1.1f);
        OpenIGI.Game.Igi1.Physics.RagdollBody corpse = ragdoll.Seed(world, world, 0f, 0f, where, where, facing);
        System.Numerics.Matrix4x4[] back = (System.Numerics.Matrix4x4[])world.Clone();
        ragdoll.Write(corpse, where, facing, back);

        double worstPlace = 0.0, worstTurn = 0.0;
        for (int node = 0; node < world.Length; node++)
        {
            if (!asset.Names[node].StartsWith("mixamorig:", StringComparison.Ordinal))
            {
                continue;
            }

            worstPlace = Math.Max(worstPlace, System.Numerics.Vector3.Distance(world[node].Translation, back[node].Translation));
            System.Numerics.Matrix4x4.Decompose(world[node], out _, out System.Numerics.Quaternion a, out _);
            System.Numerics.Matrix4x4.Decompose(back[node], out _, out System.Numerics.Quaternion b, out _);
            worstTurn = Math.Max(worstTurn, 1.0 - Math.Abs(System.Numerics.Quaternion.Dot(a, b)));
        }

        output.WriteLine($"worst place {worstPlace * 1000.0:0.0} mm, worst turn {worstTurn:0.000000}");
        Assert.InRange(worstPlace, 0.0, 0.01);
        Assert.InRange(worstTurn, 0.0, 0.001);
    }

    private readonly record struct RagdollBoneView(double Half, double Radius, double Mass);

    [RequiresModernBodyFact]
    public void TheCharacterReadsWholeWithBothClips()
    {
        RecordingGraphicsDevice device = new();
        using ModernBodyAsset asset = ModernBodyAsset.Load(device, File);

        Assert.Equal(66, asset.Joints.Length); // the Mixamo skeleton and the weapon bone (weapon_bake.py)
        Assert.InRange(asset.Vertices.Length, 1000, ushort.MaxValue);
        Assert.Equal(3, asset.Subsets.Length);
        Assert.Single(asset.Subsets, subset => subset.Gun);
        Assert.All(asset.Subsets, subset => Assert.True(subset.Diffuse.IsValid, "a subset has no texture"));

        // The uniform's and the rifle's own normal, roughness and metalness, attached to their colour textures.
        Assert.True(asset.HasAuthoredDetail, "no detail maps came with the character");
        Assert.All(asset.Subsets, subset => Assert.Contains(device.TextureDetails, pair => pair.Diffuse == subset.Diffuse));
        Assert.InRange(asset.Idle.Duration, 5f, 12f);
        Assert.InRange(asset.Walk.Duration, 0.8f, 2f);
        Assert.InRange(asset.WalkMetresPerSecond, 0.9f, 1.5f);
    }

    /// <summary>
    /// A body drawn every frame, whose frames then stop drawing it -- smoothing off, as under a menu or a cutscene -- is posed by
    /// its ticks again: it waited on frames that never came, and slid about frozen in its last pose, firing without moving.
    /// </summary>
    [RequiresModernBodyFact]
    public void ABodyNoLongerDrawnEveryFrameIsPosedByItsTicks()
    {
        RecordingGraphicsDevice device = new();
        using ModernBodyAsset asset = ModernBodyAsset.Load(device, File);
        using ModernBody body = new(asset, device);
        for (int tick = 0; tick < 20; tick++)
        {
            body.Tick(3.2f / 30f, 0f, aiming: true);
            body.DrawAt(0.5f);
        }

        Vector3 before = body.GunEnds!.Value.Muzzle;
        for (int tick = 0; tick < 5; tick++)
        {
            body.Tick(3.2f / 30f, 0f, aiming: true);
        }

        Vector3 after = body.GunEnds!.Value.Muzzle;
        output.WriteLine($"muzzle {before} -> {after} over five ticks undrawn");
        Assert.True(Vector3.Distance(before, after) > 0.005f, "the body froze once its frames stopped drawing it");
    }

    /// <summary>
    /// A look that swings right round in a tick -- the player behind him all at once -- leaves the body whole: the blend of two
    /// opposite looks came out as nothing, and its normal as not-a-number, which spread through every pose after it for good.
    /// </summary>
    [RequiresModernBodyFact]
    public void ALookSwungRightRoundLeavesHimWhole()
    {
        RecordingGraphicsDevice device = new();
        using ModernBodyAsset asset = ModernBodyAsset.Load(device, File);
        using ModernBody body = new(asset, device);
        for (int tick = 0; tick < 60; tick++)
        {
            Vector3 look = tick < 20 ? new Vector3(0f, 1f, 0f) : tick < 40 ? new Vector3(0f, -1f, 0f) : default;
            body.Tick(tick > 45 ? 3.2f / 30f : 0f, 0f, aiming: true, look: look);
            body.DrawAt(0.5f);
            (Vector3 muzzle, Vector3 stock) = body.GunEnds!.Value;
            Assert.True(float.IsFinite(muzzle.X) && float.IsFinite(stock.Z), $"the body is not a number at tick {tick}");
        }
    }

    /// <summary>
    /// Aiming at a man above him, the rifle comes up onto him, the upper body bent to it; looking at one aside, the head turns.
    /// The clips aim level, so on stairs he fired level with the rifle pointed off the man he shot at.
    /// </summary>
    [RequiresModernBodyFact]
    public void HeAimsAtAManAboveAndTurnsHisHeadToOneAside()
    {
        RecordingGraphicsDevice device = new();
        using ModernBodyAsset asset = ModernBodyAsset.Load(device, File);
        float Elevation(Vector3 look)
        {
            using ModernBody body = new(asset, device);
            for (int tick = 0; tick < 30; tick++)
            {
                body.Tick(0f, 0f, aiming: true, look: look);
            }

            (Vector3 muzzle, Vector3 stock) = body.GunEnds!.Value;
            Vector3 barrel = muzzle - stock;
            return MathF.Atan2(barrel.Z, MathF.Sqrt((barrel.X * barrel.X) + (barrel.Y * barrel.Y))) * 180f / MathF.PI;
        }

        float level = Elevation(default), up = Elevation(new Vector3(0f, 1f, MathF.Tan(30f * MathF.PI / 180f)));
        output.WriteLine($"rifle at {level:0.0} degrees aiming level, {up:0.0} aiming 30 degrees up");
        Assert.InRange(up - level, 22f, 38f);

        using ModernBody watcher = new(asset, device);
        for (int tick = 0; tick < 30; tick++)
        {
            watcher.Tick(0f, 0f, look: new Vector3(1f, 1f, 0f));
        }

        output.WriteLine($"head turned {watcher.LookForTesting.HeadYaw * 180f / MathF.PI:0.0} degrees to a man 45 degrees to his right");
        Assert.InRange(watcher.LookForTesting.HeadYaw * 180f / MathF.PI, 40f, 50f);
    }

    /// <summary>
    /// A foot the clip puts down stays where it came down while it is held, however the clip's stride and his own pace disagree:
    /// a strafe at 3 metres a second, two thirds of the strafing run's own pace, slid the planted foot along the ground.
    /// </summary>
    [RequiresModernBodyTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void APlantedFootStaysPut(bool strafe)
    {
        RecordingGraphicsDevice device = new();
        using ModernBodyAsset asset = ModernBodyAsset.Load(device, File);
        using ModernBody body = new(asset, device);
        float speed = strafe ? 3.0f : 3.2f;
        Vector2 travel = strafe ? new Vector2(0f, speed / 30f) : new Vector2(speed / 30f, 0f);
        Vector3 walked = Vector3.Zero;
        Vector3?[] was = [null, null];
        float worst = 0f, held = 0f;
        for (int tick = 0; tick < 90; tick++)
        {
            body.Tick(speed / 30f, 0f, aiming: true, heading: strafe ? new Vector2(0f, 1f) : default, travel: travel);
            walked += ModernBodyAsset.FromBody(new Vector3(travel.Y, travel.X, 0f));
            (float left, float right) = body.FootHoldForTesting;
            float[] hold = [left, right];
            for (int foot = 0; foot < 2; foot++)
            {
                Vector3 onGround = walked + body.AnkleForTesting(foot);
                if (hold[foot] >= 1f && was[foot] is { } before && tick > 15)
                {
                    float slid = Vector2.Distance(new Vector2(onGround.X, onGround.Z), new Vector2(before.X, before.Z));
                    worst = Math.Max(worst, slid);
                    held++;
                }

                was[foot] = hold[foot] >= 1f ? onGround : null;
            }
        }

        output.WriteLine($"{(strafe ? "strafe" : "run")}: {held} held foot-ticks, worst slide {worst * 100f:0.0} cm a tick");
        Assert.True(held > 10, "no foot was ever held down");
        Assert.True(worst < 0.02f, $"a held foot slid {worst * 100f:0.0} cm in a tick");
    }

    /// <summary>
    /// A frame drawn between ticks poses the body between them, as his place is drawn: a running stride half a tick on is
    /// halfway between the two ticks' poses, not either of them (posed only each tick, his limbs stepped at thirty a second).
    /// </summary>
    [RequiresModernBodyFact]
    public void AFrameBetweenTicksIsPosedBetweenThem()
    {
        RecordingGraphicsDevice device = new();
        using ModernBodyAsset asset = ModernBodyAsset.Load(device, File);
        using ModernBody body = new(asset, device);
        for (int tick = 0; tick < 20; tick++)
        {
            body.Tick(4f / 30f, 0f, aiming: true);
        }

        Vector3? Muzzle() => body.GunEnds?.Muzzle;
        body.DrawAt(0f);
        Vector3 before = Muzzle()!.Value;
        body.DrawAt(1f);
        Vector3 now = Muzzle()!.Value;
        body.DrawAt(0.5f);
        Vector3 half = Muzzle()!.Value;
        output.WriteLine($"muzzle {before} -> {half} -> {now}");
        Assert.True(Vector3.Distance(before, now) > 0.005f, "the run's two ticks posed him the same");
        Assert.True(Vector3.Distance(half, (before + now) / 2f) < 0.02f * Vector3.Distance(before, now) + 0.002f, "half a tick on is not halfway");
    }

    /// <summary>
    /// A turn on the spot steps the clip as big as the turn is fast, in the legs alone under the aim he keeps, and plays it on by
    /// the angle turned: an about-face at the modern AI's 240 degrees a second steps the 180-degree turn through once in its
    /// three quarters of a second; a slow turn at 60 steps the 45.
    /// </summary>
    [RequiresModernBodyTheory]
    [InlineData(240f, "turn_left_180", 180f)]
    [InlineData(60f, "turn_left", 45f)]
    public void ATurnOnTheSpotStepsAsFarAsHeTurns(float degreesPerSecond, string clip, float clipDegrees)
    {
        RecordingGraphicsDevice device = new();
        using ModernBodyAsset asset = ModernBodyAsset.Load(device, File);
        using ModernBody body = new(asset, device);
        float turning = degreesPerSecond * MathF.PI / 180f;
        int ticks = (int)MathF.Round(clipDegrees / degreesPerSecond * 30f);
        for (int tick = 0; tick < ticks; tick++)
        {
            body.Tick(0f, 0f, aiming: true, turning: turning);
            Assert.Contains($"legs {clip} ", body.Showing, StringComparison.Ordinal);
            Assert.Contains("stand aim_idle,", body.Showing, StringComparison.Ordinal);
        }

        // Turned the clip's own angle, the clip has played through once: its steps kept to the turn.
        output.WriteLine($"{degreesPerSecond} deg/s for {ticks} ticks: {body.Showing}; clip at {body.TurnPhaseForTesting:0.00} of its length");
        Assert.InRange(body.TurnPhaseForTesting, 0.9f, 1.1f);
    }

    [RequiresModernBodyFact]
    public void HeStandsUpAtAMansHeightFacingTheWayASoldierFaces()
    {
        using ModernBodyAsset asset = ModernBodyAsset.Load(new RecordingGraphicsDevice(), File);
        Vertex3D[] vertices = Skinned(asset, walkTime: 0f, walkWeight: 0f);

        float lowest = vertices.Min(v => v.Position.Z), highest = vertices.Max(v => v.Position.Z);
        Assert.InRange(highest - lowest, 1.6f, 2.0f);

        // A soldier faces +Y: the toes are ahead of the ankles.
        Vector3 toe = Joint(asset, "mixamorig:LeftToeBase", 0f, 0f), ankle = Joint(asset, "mixamorig:LeftFoot", 0f, 0f);
        Assert.True(toe.Y > ankle.Y + 0.05f, $"the toes are at {toe.Y:0.00}, the ankle at {ankle.Y:0.00}: he faces backwards");
    }

    [RequiresModernBodyFact]
    public void HeWalksOnTheSpot()
    {
        using ModernBodyAsset asset = ModernBodyAsset.Load(new RecordingGraphicsDevice(), File);
        Vector3 start = Joint(asset, "mixamorig:Hips", 0f, 1f);
        float furthest = 0f;
        for (int step = 1; step <= 20; step++)
        {
            Vector3 hips = Joint(asset, "mixamorig:Hips", asset.Walk.Duration * step / 20f, 1f);
            furthest = Math.Max(furthest, new Vector2(hips.X - start.X, hips.Y - start.Y).Length());
        }

        Assert.True(furthest < 0.15f, $"his hips wander {furthest:0.00} m in a stride: the walk still travels");
    }

    [RequiresModernBodyFact]
    public void EachFallEndsLyingWhereHeStoodAndStaysThere()
    {
        using ModernBodyAsset asset = ModernBodyAsset.Load(new RecordingGraphicsDevice(), File);
        int hips = Array.IndexOf(asset.Names, "mixamorig:Hips");
        foreach (bool behind in new[] { false, true })
        {
            foreach (bool head in new[] { false, true })
            {
                ModernClip? fall = asset.Deaths[behind ? 1 : 0, head ? 1 : 0];
                Assert.True(fall is not null, $"no fall for behind={behind} head={head}");
                Matrix4x4[] world = new Matrix4x4[asset.Rest.Length], skin = new Matrix4x4[asset.Joints.Length];
                asset.PoseFall(fall!, 0f, world, skin);
                Vector3 start = ModernBodyAsset.ToBody(world[hips].Translation, 0f);
                asset.PoseFall(fall!, fall!.Duration, world, skin);
                Vector3 end = ModernBodyAsset.ToBody(world[hips].Translation, 0f);
                Vertex3D[] lying = new Vertex3D[asset.Vertices.Length];
                asset.Skin(skin, 0f, 0f, lying);
                asset.PoseFall(fall, fall.Duration * 3f, world, skin);
                Vector3 later = ModernBodyAsset.ToBody(world[hips].Translation, 0f);

                float height = lying.Max(v => v.Position.Z) - lying.Min(v => v.Position.Z);
                float slide = new Vector2(end.X - start.X, end.Y - start.Y).Length();
                Assert.True(height < 0.8f, $"behind={behind} head={head}: {height:0.00} m tall at the end, not lying");
                Assert.True(slide < 0.05f, $"behind={behind} head={head}: the hips slide {slide:0.00} m");
                Assert.True(Vector3.Distance(end, later) < 1e-4f, $"behind={behind} head={head}: does not hold its last frame");
            }
        }
    }

    [RequiresModernBodyFact]
    public void EachFlinchPushesHisHeadAwayFromTheShooter()
    {
        using ModernBodyAsset asset = ModernBodyAsset.Load(new RecordingGraphicsDevice(), File);
        int head = Array.IndexOf(asset.Names, "mixamorig:Head");
        Matrix4x4[] world = new Matrix4x4[asset.Rest.Length], skin = new Matrix4x4[asset.Joints.Length];
        Vector3 Head(ModernClip hit, float time)
        {
            asset.PoseFall(hit, time, world, skin);
            return ModernBodyAsset.ToBody(world[head].Translation, 0f);
        }

        // Shooter ahead, behind, to the left, to the right, in the body's frame (facing +Y, right +X): the head goes the other way.
        foreach ((string name, float ahead, float right) in new[] { ("front", 1f, 0f), ("back", -1f, 0f), ("left", 0f, -1f), ("right", 0f, 1f) })
        {
            ModernClip hit = asset.Hit(ahead, right)!;
            Vector3 start = Head(hit, 0f), furthest = start;
            for (int step = 1; step <= 30; step++)
            {
                Vector3 at = Head(hit, hit.Duration * step / 30f);
                if (Vector2.Distance(new(at.X, at.Y), new(start.X, start.Y)) > Vector2.Distance(new(furthest.X, furthest.Y), new(start.X, start.Y)))
                {
                    furthest = at;
                }
            }

            Vector2 push = new(furthest.X - start.X, furthest.Y - start.Y);
            float along = -((push.X * right) + (push.Y * ahead));
            output.WriteLine($"{name}: {hit.Duration:0.00} s, head pushed ({push.X:0.00}, {push.Y:0.00}) m, {along:0.00} m away from the shooter");
            Assert.True(along > 0.05f, $"{name}: the head goes {along:0.00} m away from the shooter");
        }
    }

    [RequiresModernBodyFact]
    public void InAFightHisRifleComesUpAndHeRuns()
    {
        using ModernBodyAsset asset = ModernBodyAsset.Load(new RecordingGraphicsDevice(), File);
        Assert.NotSame(asset.Idle, asset.AimIdle);
        Assert.NotSame(asset.AimIdle, asset.Fire);
        Assert.NotSame(asset.Walk, asset.AimWalk);
        Assert.NotSame(asset.AimWalk, asset.Run);
        output.WriteLine($"walk {asset.Walk.MetresPerSecond:0.00} m/s, aimed walk {asset.AimWalk.MetresPerSecond:0.00}, run {asset.Run.MetresPerSecond:0.00}");
        Assert.InRange(asset.AimWalk.MetresPerSecond, 0.8f, 2f);
        Assert.InRange(asset.Run.MetresPerSecond, 2.5f, 6f);

        // The rifle along his facing (+Y) when he aims; held low across him at ease.
        float Reach(ModernClip stand)
        {
            Matrix4x4[] world = new Matrix4x4[asset.Rest.Length], skin = new Matrix4x4[asset.Joints.Length];
            asset.PoseFall(stand, 0.5f, world, skin);
            Vertex3D[] vertices = new Vertex3D[asset.Vertices.Length];
            asset.Skin(skin, 0f, 0f, vertices);
            (int offset, int count, _, _) = asset.Subsets.Single(subset => subset.Gun);
            IEnumerable<Vertex3D> gun = asset.Indices.Skip(offset).Take(count).Select(i => vertices[i]);
            return gun.Max(v => v.Position.Y) - gun.Min(v => v.Position.Y);
        }

        float atEase = Reach(asset.Idle), aimed = Reach(asset.AimIdle), firing = Reach(asset.Fire);
        output.WriteLine($"rifle reaches {atEase:0.00} m forward at ease, {aimed:0.00} aimed, {firing:0.00} firing");
        Assert.True(aimed > 0.6f && firing > 0.6f, "the rifle does not point where he faces when he aims");
    }

    [RequiresModernBodyFact]
    public void ARoundFromTheFrontLandsOnTheSideItStruck()
    {
        using ModernBodyAsset asset = ModernBodyAsset.Load(new RecordingGraphicsDevice(), File);
        int Bone(string name) => Array.IndexOf(asset.Names, "mixamorig:" + name);
        (int left, int right, int head) = (Bone("LeftArm"), Bone("RightArm"), Bone("Head"));

        // In glTF's world: z is ahead, x his left. By the tick: how far his shoulders have turned to his left (his left one going back), in degrees, and his head to his left.
        List<(float Behind, float Aside)> Fall(int side, bool impact)
        {
            using ModernBody body = new(asset, new RecordingGraphicsDevice());
            for (int tick = 0; tick < 30; tick++)
            {
                body.Tick(0f, 0f, aiming: true);
            }

            body.Fall(asset.ShortDeath(false, false, (0, 0, side))!, brief: true);
            if (impact)
            {
                body.Impact(side, 1f);
            }

            List<(float Behind, float Aside)> ticks = [];
            for (int tick = 0; tick < 12; tick++)
            {
                body.Tick(0f, 0f);
                Vector3 across = body.BoneForTesting(left) - body.BoneForTesting(right);
                ticks.Add((MathF.Atan2(-across.Z, across.X) * 180f / MathF.PI, body.BoneForTesting(head).X));
            }

            return ticks;
        }

        // What the impact adds to the fall by itself, at its most over the first third of a second.
        (float Behind, float Aside) Added(int side)
        {
            List<(float Behind, float Aside)> with = Fall(side, true), without = Fall(side, false);
            (float behind, float aside) = (0f, 0f);
            for (int tick = 0; tick < with.Count; tick++)
            {
                (float b, float a) = (with[tick].Behind - without[tick].Behind, with[tick].Aside - without[tick].Aside);
                output.WriteLine($"side {side} tick {tick}: shoulders turned left {with[tick].Behind:+0;-0} degrees ({b:+0;-0} added), head left {with[tick].Aside:+0.00;-0.00} m ({a:+0.00;-0.00} added)");
                (behind, aside) = (Math.Abs(b) > Math.Abs(behind) ? b : behind, Math.Abs(a) > Math.Abs(aside) ? a : aside);
            }

            return (behind, aside);
        }

        (float Behind, float Aside) hitLeft = Added(-1), hitRight = Added(1);
        Assert.True(hitLeft.Behind > 15f, $"a hit on his left turned his shoulders {hitLeft.Behind} degrees to his left");
        Assert.True(hitRight.Behind < -15f, $"a hit on his right turned his shoulders {hitRight.Behind} degrees to his left");
        Assert.True(hitLeft.Aside > 0.05f, $"a hit on his left took his head {hitLeft.Aside} m to his left");
        Assert.True(hitRight.Aside < -0.05f, $"a hit on his right took his head {hitRight.Aside} m to his left");
    }

    [RequiresModernBodyFact]
    public void ARoundKicksTheBoneItStruck()
    {
        using ModernBodyAsset asset = ModernBodyAsset.Load(new RecordingGraphicsDevice(), File);
        int head = Array.IndexOf(asset.Names, "mixamorig:Head");
        int top = Array.IndexOf(asset.Names, "mixamorig:HeadTop_End");
        Matrix4x4[] world = new Matrix4x4[asset.Rest.Length], skin = new Matrix4x4[asset.Joints.Length];
        ModernMix still = new(asset.Idle, 0f, asset.Walk, 0f, 0f);
        asset.Pose(still, still, 1f, world, skin, null, 0f, 0f);
        Vector3 before = ModernBodyAsset.ToBody(world[top].Translation, 0f);
        Vector3[] kicks = new Vector3[asset.Rest.Length];
        kicks[head] = Vector3.UnitX * 0.4f;
        asset.Pose(still, still, 1f, world, skin, null, 0f, 0f, kicks);
        Vector3 after = ModernBodyAsset.ToBody(world[top].Translation, 0f);
        output.WriteLine($"head top moves {Vector3.Distance(before, after):0.000} m under a 0.4 rad kick");
        Assert.True(Vector3.Distance(before, after) > 0.03f, "a kick does not turn the bone");

        // A head shot from the front: the head swings back, at its most about 0.15 s on, and settles over half a second.
        using ModernBody body = new(asset, new RecordingGraphicsDevice());
        body.Tick(0f, 0f);
        body.Kick(15, 1f, 0f);
        float most = 0f;
        for (int tick = 0; tick < 16; tick++)
        {
            body.Tick(0f, 0f);
            most = Math.Max(most, body.KickAngleForTesting(head));
            output.WriteLine($"tick {tick}: head kicked {body.KickAngleForTesting(head) * 180f / MathF.PI:0.0} degrees");
        }

        Assert.InRange(most * 180f / MathF.PI, 18f, 40f);
    }

    [RequiresModernBodyFact]
    public void TheRifleMuzzleIsFoundAtItsFrontEnd()
    {
        using ModernBodyAsset asset = ModernBodyAsset.Load(new RecordingGraphicsDevice(), File);
        using ModernBody body = new(asset, new RecordingGraphicsDevice());
        for (int tick = 0; tick < 20; tick++)
        {
            body.Tick(0f, 0f, 0f, aiming: true);
        }

        // Aimed, the rifle points the way he faces (+Y): the muzzle is ahead of the stock, by about its length.
        (System.Numerics.Vector3 muzzle, System.Numerics.Vector3 stock) = body.GunEnds!.Value;
        output.WriteLine($"muzzle ({muzzle.X:0.00}, {muzzle.Y:0.00}, {muzzle.Z:0.00}), stock ({stock.X:0.00}, {stock.Y:0.00}, {stock.Z:0.00})");
        Assert.True(muzzle.Y - stock.Y > 0.6f, "the muzzle is not at the front of the aimed rifle");
        Assert.InRange(Vector3.Distance(muzzle, stock), 0.75f, 0.95f);
    }

    [RequiresModernBodyFact]
    public void AReactionMovesHisBodyButLeavesHisHandsOnTheRifle()
    {
        using ModernBodyAsset asset = ModernBodyAsset.Load(new RecordingGraphicsDevice(), File);
        Assert.Equal(10, asset.Reacts.Count);
        int chest = Array.IndexOf(asset.Names, "mixamorig:Spine2"), head = Array.IndexOf(asset.Names, "mixamorig:Head");
        int hand = Array.IndexOf(asset.Names, "mixamorig:RightHand"), left = Array.IndexOf(asset.Names, "mixamorig:LeftHand");
        ModernMix still = new(asset.Idle, 0f, asset.Walk, 0f, 0f);
        Matrix4x4[] world = new Matrix4x4[asset.Rest.Length], skin = new Matrix4x4[asset.Joints.Length];

        // The hands where the chest carries them, and the head, at ease and half a second into a headshot.
        (Vector3 Hand, Vector3 Left, Vector3 Head) Measure(ModernClip? react)
        {
            asset.Pose(still, still, 1f, world, skin, react, 0.5f, react is null ? 0f : 1f);
            Matrix4x4.Invert(world[chest], out Matrix4x4 toChest);
            return (Vector3.Transform(world[hand].Translation, toChest), Vector3.Transform(world[left].Translation, toChest), world[head].Translation);
        }

        var calm = Measure(null);
        var shot = Measure(asset.Hit(1f, 0f, zone: 15, heavy: false));
        output.WriteLine($"head moves {Vector3.Distance(calm.Head, shot.Head):0.000}, right hand against the chest {Vector3.Distance(calm.Hand, shot.Hand):0.000000}, left {Vector3.Distance(calm.Left, shot.Left):0.000000} (glTF units)");
        Assert.True(Vector3.Distance(calm.Head, shot.Head) > 0.01f, "the headshot reaction does not move his head");
        Assert.True(Vector3.Distance(calm.Hand, shot.Hand) < 1e-3f && Vector3.Distance(calm.Left, shot.Left) < 1e-3f, "the reaction moves his hands off the rifle");
    }

    [RequiresModernBodyFact]
    public void InEveryClipTheRifleLiesBetweenHisHands()
    {
        using ModernBodyAsset asset = ModernBodyAsset.Load(new RecordingGraphicsDevice(), File);
        foreach ((string name, bool aiming, float speed) in new[] { ("idle", false, 0f), ("aim", true, 0f), ("walk", false, 1.1f), ("aim_walk", true, 1.8f), ("run", true, 3f) })
        {
            using ModernBody body = new(asset, new RecordingGraphicsDevice());
            float worst = 0f;
            for (int tick = 0; tick < 60; tick++)
            {
                body.Tick(speed / 30f, 0f, 0f, aiming);
                if (tick < 15)
                {
                    continue;
                }

                // The left palm on the line the barrel runs along, the right one at the grip end of it.
                (Vector3 muzzle, Vector3 stock) = body.GunEnds!.Value;
                (Vector3 right, Vector3 left) = body.PalmsForTesting;
                Vector3 line = Vector3.Normalize(muzzle - stock);
                Vector3 off = (left - stock) - (line * Vector3.Dot(left - stock, line));
                worst = Math.Max(worst, off.Length());
                Assert.True(Vector3.Dot(muzzle - stock, line) > 0.7f && Vector3.Dot(left - right, line) > 0f, $"{name}: the rifle does not run from the right hand towards the left");
            }

            // Measured against the idle's, where the rifle was fitted: its line runs tip to tip, below the bore the hands hold.
            output.WriteLine($"{name}: left palm at most {worst:0.000} m off the rifle's tip-to-tip line");
            // Aiming, the rifle is raised and drawn back, and the left hand reached after it: no further off than at ease.
            // As frames draw him: the blend between ticks rebuilds each joint, a few millimetres off the tick's own pose.
            Assert.True(worst < 0.105f, $"{name}: the left hand comes {worst:0.00} m off the rifle");
        }
    }

    // The posed mesh in the soldier's frame.
    private static Vertex3D[] Skinned(ModernBodyAsset asset, float walkTime, float walkWeight)
    {
        Matrix4x4[] world = new Matrix4x4[asset.Rest.Length], skin = new Matrix4x4[asset.Joints.Length];
        asset.Pose(0f, walkTime, walkWeight, world, skin);
        Vertex3D[] vertices = new Vertex3D[asset.Vertices.Length];
        asset.Skin(skin, 0f, 0f, vertices);
        return vertices;
    }

    /// <summary>
    /// A round meets the body where it is drawn: standing, a round at head height takes the head and one at the shins a leg; knelt,
    /// the round at the standing head's height passes over him and his head is met lower down. The rifle lets rounds through.
    /// </summary>
    [RequiresModernBodyFact]
    public void ARoundMeetsTheBodyWhereItIsDrawn()
    {
        RecordingGraphicsDevice device = new();
        using ModernBodyAsset asset = ModernBodyAsset.Load(device, File);
        Assert.Contains(-1, asset.TriangleParts); // the rifle's

        // From in front of him -- he faces +Y -- straight back through him, at a height above his lowest point, on his middle.
        (int Part, float Height)[] Profile(bool kneeling, float side = 0f)
        {
            using ModernBody body = new(asset, device);
            for (int tick = 0; tick < 45; tick++)
            {
                body.Tick(0f, 0f, aiming: true, kneeling: kneeling);
            }

            List<(int, float)> met = [];
            for (float z = 0.05f; z < 2.2f; z += 0.05f)
            {
                met.Add((body.TryMeet(new Vector3(side, 3f, z), new Vector3(side, -3f, z), out _, out int part, out _) ? part : -1, z));
            }

            output.WriteLine($"{(kneeling ? "knelt" : "standing")}: " + string.Join(" ", met.Select(m => $"{m.Item2:0.00}:{m.Item1}")));
            return [.. met];
        }

        static bool Head(int part) => part is 4 or 5;
        static bool Leg(int part) => part >= 20;

        (int Part, float Height)[] standing = Profile(kneeling: false), knelt = Profile(kneeling: true);
        float standingHead = standing.Where(m => Head(m.Part)).Max(m => m.Height);
        Assert.InRange(standingHead, 1.5f, 1.95f);
        Assert.Contains(Profile(kneeling: false, side: 0.12f), m => Leg(m.Part) && m.Height < 0.6f); // between his legs, nothing
        Assert.DoesNotContain(standing, m => m.Height > 1.95f && m.Part >= 0);

        Assert.DoesNotContain(knelt, m => m.Height >= standingHead - 0.05f && m.Part >= 0);
        Assert.Contains(knelt, m => Head(m.Part) && m.Height < standingHead - 0.3f);
    }

    /// <summary>
    /// A stop's clip outlasts the tick he arrives on, when he has still moved; his rifle coming up ends it, and so does setting off.
    /// </summary>
    [RequiresModernBodyFact]
    public void AStopsClipEndsWhenHisRifleComesUpOrHeSetsOff()
    {
        RecordingGraphicsDevice device = new();
        using ModernBodyAsset asset = ModernBodyAsset.Load(device, File);
        Assert.Equal(2, asset.StopClips.Count);
        using ModernBody body = new(asset, device);
        body.Tick(0f, 0f);

        body.Gesture(asset.StopClips[0]);
        body.Tick(1.2f / 30f, 0f); // the tick he arrives
        Assert.Contains("stop_", body.Showing, StringComparison.Ordinal);
        body.Tick(0f, 0f);
        Assert.Contains("stop_", body.Showing, StringComparison.Ordinal);
        body.Tick(0f, 0f, aiming: true);
        Assert.DoesNotContain("stop_", body.Showing, StringComparison.Ordinal);

        body.Gesture(asset.StopClips[1]);
        for (int tick = 0; tick < 20; tick++)
        {
            body.Tick(0f, 0f);
        }

        Assert.Contains("stop_", body.Showing, StringComparison.Ordinal);
        body.Tick(1.2f / 30f, 0f);
        Assert.DoesNotContain("stop_", body.Showing, StringComparison.Ordinal);
    }

    /// <summary>The sweep the AI's sight follows is the one the looking-around clip's head makes.</summary>
    [RequiresModernBodyFact]
    public void TheLookingAroundClipTurnsHisHeadBothWays()
    {
        RecordingGraphicsDevice device = new();
        using ModernBodyAsset asset = ModernBodyAsset.Load(device, File);
        using ModernBody body = new(asset, device);
        body.Tick(0f, 0f);
        body.Gesture(asset.StopClips[0]);
        float most = 0f, least = 0f;
        for (int tick = 0; tick < Game.Igi1.Ai.Igi1HumanAi.LookAroundTicks; tick++)
        {
            body.Tick(0f, 0f);
            (most, least) = (Math.Max(most, body.HeadYawForTesting), Math.Min(least, body.HeadYawForTesting));
        }

        Assert.InRange(most * 180f / MathF.PI, 100f, 130f);
        Assert.InRange(least * 180f / MathF.PI, -150f, -120f);
    }

    /// <summary>
    /// Killed kneeling he falls from his kneel, not from his feet: each way of being shot has its own fall, starting with his head
    /// no higher than a kneeling man's, and ending lying down.
    /// </summary>
    [RequiresModernBodyFact]
    public void KilledKneelingHeFallsFromHisKneel()
    {
        RecordingGraphicsDevice device = new();
        using ModernBodyAsset asset = ModernBodyAsset.Load(device, File);
        foreach ((bool behind, bool head) in new[] { (false, false), (true, false), (false, true), (true, true) })
        {
            ModernClip fall = asset.Death(behind, head, kneeling: true)!;
            Assert.NotSame(asset.Death(behind, head), fall);

            using ModernBody body = new(asset, device);
            for (int tick = 0; tick < 45; tick++)
            {
                body.Tick(0f, 0f, aiming: true, kneeling: true);
            }

            Assert.True(body.Kneeling);
            body.Fall(fall);
            float tallest = 0f, last = 0f;
            for (int tick = 0; tick < (fall.Duration * 30) + 10; tick++)
            {
                body.Tick(0f, 0f);
                last = body.HeadHeightForTesting;
                tallest = Math.Max(tallest, last);
            }

            output.WriteLine($"behind {behind}, head {head}: {fall.Duration:0.00} s, tallest {tallest:0.00} m, ends {last:0.00} m");
            Assert.InRange(fall.Duration, 2.5f, 4.5f); // the held kneel is cut
            Assert.True(tallest < 1.35f, $"he stood up first: {tallest:0.00} m");
            Assert.True(last < 0.6f, $"he did not end lying down: {last:0.00} m");
        }
    }

    /// <summary>The cautious walk has a pace the game can measure, and his legs step through it.</summary>
    [RequiresModernBodyFact]
    public void TheSearchWalkSteps()
    {
        RecordingGraphicsDevice device = new();
        using ModernBodyAsset asset = ModernBodyAsset.Load(device, File);
        Assert.InRange(asset.SearchWalk.MetresPerSecond, 0.9f, 1.3f);
        using ModernBody body = new(asset, device);
        float step = asset.SearchWalk.MetresPerSecond / 30f, most = float.MinValue, least = float.MaxValue;
        for (int tick = 0; tick < 90; tick++)
        {
            body.Tick(step, 0f, aiming: true, travel: new Vector2(step, 0f), searching: true);
            (most, least) = (Math.Max(most, body.AnkleForTesting(0).Z), Math.Min(least, body.AnkleForTesting(0).Z));
        }

        Assert.Contains("search_walk", body.Showing, StringComparison.Ordinal);
        Assert.True(most - least > 0.3f, $"his foot only moved {most - least:0.00} m");
    }

    // A joint's place in the soldier's frame.
    private static Vector3 Joint(ModernBodyAsset asset, string name, float walkTime, float walkWeight)
    {
        Matrix4x4[] world = new Matrix4x4[asset.Rest.Length], skin = new Matrix4x4[asset.Joints.Length];
        asset.Pose(0f, walkTime, walkWeight, world, skin);
        int node = Array.IndexOf(asset.Names, name);
        Assert.True(node >= 0, $"no joint named {name}");
        return ModernBodyAsset.ToBody(world[node].Translation, 0f);
    }
}

/// <summary>A fact that needs the play-test character, skipped where it is not.</summary>
public sealed class RequiresModernBodyTheoryAttribute : TheoryAttribute
{
    public RequiresModernBodyTheoryAttribute()
    {
        if (!System.IO.File.Exists(ModernBodyTests.File))
        {
            Skip = $"No modern character at {ModernBodyTests.File} (set OPENIGI_MODERN_BODY).";
        }
    }
}

public sealed class RequiresModernBodyFactAttribute : FactAttribute
{
    /// <summary>Initializes a new instance of the <see cref="RequiresModernBodyFactAttribute"/> class.</summary>
    public RequiresModernBodyFactAttribute()
    {
        if (!System.IO.File.Exists(ModernBodyTests.File))
        {
            Skip = $"No modern character at {ModernBodyTests.File} (set OPENIGI_MODERN_BODY).";
        }
    }
}
