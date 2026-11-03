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
    /// <summary>Where the character is: <c>OPENIGI_MODERN_BODY</c>, else the play-test's file beside the checkout.</summary>
    public static readonly string File = Environment.GetEnvironmentVariable("OPENIGI_MODERN_BODY")
        ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../animations/gltf/swat_guy_game.glb"));

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
            Assert.True(worst < 0.10f, $"{name}: the left hand comes {worst:0.00} m off the rifle");
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
