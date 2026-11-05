using OpenIGI.Core.Diagnostics;
using OpenIGI.Core.Graphics;
using OpenIGI.Game;
using OpenIGI.Game.Igi1.Ai;
using OpenIGI.Game.Igi1.Physics;
using OpenIGI.Game.Igi1.Player;
using OpenIGI.Game.Weapons;
using OpenIGI.Game.Player;
using OpenIGI.Game.World;
using OpenIGI.Core.Numerics;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace OpenIGI.Presentation.Igi1;

/// <content>
/// The modern bodies (<see cref="GameplayCVars.ModernBodies"/>): a glTF character read once, and each living soldier given his
/// own posed copy of it, which the drawer draws in his own model's place. <b>Not retail.</b>
/// </content>
public sealed partial class SoldierRenderer
{
    /// <summary>Gets every soldier the renderer draws, with his model's lowest vertex in metres, for testing.</summary>
    internal IEnumerable<(SoldierMember Member, float Lowest)> MembersForTesting =>
        _groups.SelectMany(group => group.Soldiers.Select(member => (member, group.Body.LowestVertexZ)));

    private ModernBodyAsset? _modernAsset;
    private string? _modernAssetPath;
    private bool _modernAssetFailed;
    private readonly Dictionary<SoldierMember, double> _modernFallHeights = [];
    private readonly Dictionary<SoldierMember, double> _modernStandingHeights = [];
    private readonly Dictionary<SoldierMember, float> _modernRootTops = [];
    private readonly Dictionary<SoldierMember, int> _modernStopsSeen = [];
    private readonly Dictionary<SoldierMember, (int, int)> _modernSearchSeen = [];
    private readonly Dictionary<SoldierMember, int> _modernHitsSeen = [];
    private readonly Dictionary<SoldierMember, int> _modernLastHitTick = [];
    private readonly Dictionary<SoldierMember, (float Facing, bool Reloading, bool Throwing)> _modernLast = [];

    // Two rounds within this many ticks make the second a heavy one: a burst staggers where a single round flinches.
    private const int HeavyHitTicks = 18;

    /// <summary>Gives a soldier his modern body for this tick, or takes it away.</summary>
    /// <param name="group">The soldier's group, whose model stands him on the ground.</param>
    /// <param name="member">The soldier.</param>
    private void TickModernBody(SoldierGroup group, SoldierMember member)
    {
        SoldierView view = member.Agent.View;
        if (ModernAsset() is not { } asset || (view.IsDead && member.Modern is not { Fallen: true } && !StartFall(member, asset, view)))
        {
            // Dead with no fall to play, or one that came back to life (a respawn): his own model, or a fresh body.
            DropModernBody(member);
            return;
        }

        string forced = GameplayCVars.ModernPose.Value;
        if (!view.IsDead && member.Modern is { Fallen: true } && !forced.StartsWith("die", StringComparison.Ordinal))
        {
            DropModernBody(member);
        }

        if (member.Modern is null)
        {
            // His feet where his own model's are: its lowest vertex, in metres, which sets the body on the ground. (Not his
            // skin scratch: until his first skinning succeeds that holds the model's raw geometry, in its own units.)
            member.Modern = new ModernBody(asset, _device);
            member.Modern.MatchLowest(group.Body.LowestVertexZ);
            _modernHitsSeen[member] = view.LastHit.Count;
        }

        ModernBody body = member.Modern;
        if (view.IsDead && GameplayCVars.ModernRagdoll.Value && TickModernCorpse(member, body, asset, view))
        {
            return;
        }

        if (forced.Length > 0 && !view.IsDead)
        {
            TakeHit(member, body, asset, view);
            TickForcedPose(member, body, asset, forced);
            return;
        }

        float raise = 0f;
        bool kneeling = false, keepsDown = false;
        if (!view.IsDead)
        {
            // A kneel the character has a clip for is his own.
            if (_sim.Brains.TryGetValue(view, out Igi1HumanAi? brain) && !ReferenceEquals(asset.KneelIdle, ModernClip.None)
                && brain.CombatState is HumanSoldierStates.CrouchDownState
                    or (>= HumanSoldierStates.KneelingState and <= HumanSoldierStates.KneelReadyState))
            {
                (kneeling, keepsDown) = (true, brain.KeepsDown);
            }

            raise = brain is not null && brain.HasGroundQuery ? Raise(member, view) : 0f;

            _modernStandingHeights[member] = member.TickWhere.Z + raise;

            TakeHit(member, body, asset, view);
        }
        else
        {
            // His own death clip brings where he is drawn down to the floor, and the character's fall its own hips: however far
            // he is drawn lower than he stood is given back, or he would sink twice.
            raise = (float)(_modernFallHeights.GetValueOrDefault(member, member.TickWhere.Z) - member.TickWhere.Z);
        }

        double dx = member.TickWhere.X - member.PrevTickWhere.X, dy = member.TickWhere.Y - member.PrevTickWhere.Y;
        float moved = member.HasTickState ? (float)Math.Sqrt((dx * dx) + (dy * dy)) : 0f;

        // Blinded: his own stagger, once. The hidden original's clip still drifts him a little, which is not walking, and would end it.
        if (_sim.Brains.TryGetValue(view, out Igi1HumanAi? flashed) && !view.IsDead)
        {
            if (flashed.Blinded && !_modernBlinded.GetValueOrDefault(member))
            {
                body.Act(asset.FlashStagger);
            }

            _modernBlinded[member] = flashed.Blinded;
            flashed.CanLean = !ReferenceEquals(asset.LeanLeft, ModernClip.None) && !ReferenceEquals(asset.LeanRight, ModernClip.None);
            moved = flashed.Blinded && !ReferenceEquals(asset.FlashStagger, ModernClip.None) ? 0f : moved;
        }

        // In a fight his rifle comes up, and his shots fire it.
        bool aiming = _sim.Brains.TryGetValue(view, out Igi1HumanAi? fighter)
            && (fighter.HasCombatRecord || fighter.Activity is AiSoldierActivity.Engaging or AiSoldierActivity.FiringAtTask);

        // How fast he turns, and the reload or throw he starts: a reload asked for either way (the displayed one, or the
        // direct one the original plays nothing for), a throw as his body enters it.
        float turning = 0f;
        if (fighter is not null && !view.IsDead)
        {
            bool reloading = fighter.Combat.PressReload || fighter.Combat.ReloadWeaponDirectly
                || fighter.CombatState is HumanSoldierStates.DisplayReloadState or HumanSoldierStates.KneelReloadState;
            bool throwing = fighter.CombatState == HumanSoldierStates.GrenadeThrowState;
            if (_modernLast.TryGetValue(member, out var last))
            {
                float turned = fighter.Facing - last.Facing;
                turned = MathF.IEEERemainder(turned, 2f * MathF.PI);
                turning = turned * 30f;
                if (reloading && !last.Reloading)
                {
                    body.Act(kneeling ? asset.KneelReload : asset.Reload);
                }

                if (throwing && !last.Throwing)
                {
                    body.Act(asset.Throw);
                }
            }

            _modernLast[member] = (fighter.Facing, reloading, throwing);
        }

        // Strafing, the way he goes against the way he faces (the brain's forward is -sin, cos of its facing).
        // His step this tick against the way he faces, ahead and right (the brain's forward is -sin, cos of its facing): what
        // the planted feet are held against, and strafing, which way he goes.
        Vector2 step = default, heading = default;
        if (fighter is not null && moved > 0f)
        {
            float f = fighter.Facing;
            float ahead = (float)((-dx * Math.Sin(f)) + (dy * Math.Cos(f))), right = (float)((dx * Math.Cos(f)) + (dy * Math.Sin(f)));
            step = new Vector2(ahead, right);
            heading = fighter.Strafing ? Vector2.Normalize(step) : default;
        }

        // What he looks at, from his eyes, in his own frame: the man he fights, where he aims at him; or one he watches.
        Vector3 look = default;
        if (fighter is not null && !view.IsDead && (aiming || fighter.WatchingPlayer) && fighter.TargetFeet != default)
        {
            Vector3d at = aiming ? fighter.TargetAimPoint / WorldScene.UnitsPerMetre
                : (fighter.TargetFeet / WorldScene.UnitsPerMetre) + new Vector3d(0, 0, 1.5);
            Vector3d from = member.TickWhere + new Vector3d(0, 0, 1.55);
            double lx = at.X - from.X, ly = at.Y - from.Y, f = fighter.Facing;
            look = new Vector3((float)((lx * Math.Cos(f)) + (ly * Math.Sin(f))), (float)((-lx * Math.Sin(f)) + (ly * Math.Cos(f))), (float)(at.Z - from.Z));
        }
        else if (fighter?.PatrolGlance is { } glance)
        {
            look = new Vector3(MathF.Sin(glance), MathF.Cos(glance), 0f);
        }

        // Called away from his patrol, he leaves whatever he was doing at a stop.
        if (fighter is { OnPatrol: false, HoldsGesture: false })
        {
            body.EndGesture();
        }

        // Checking a body, or waving the squad on.
        if (fighter is not null)
        {
            (int checks, int signals) seen = _modernSearchSeen.GetValueOrDefault(member);
            if (fighter.SearchGestures.Checks != seen.checks)
            {
                body.Gesture(asset.BodyCheck);
            }
            else if (fighter.SearchGestures.Signals != seen.signals)
            {
                body.Gesture(asset.HandSignal);
            }

            _modernSearchSeen[member] = fighter.SearchGestures;
        }

        bool ready = fighter is { RifleReady: true } && !aiming;

        // A stop on his patrol: something to do with it, where the character has clips for one.
        if (fighter is not null && fighter.PatrolStops != _modernStopsSeen.GetValueOrDefault(member))
        {
            _modernStopsSeen[member] = fighter.PatrolStops;
            if (asset.StopClips.Count > 0)
            {
                body.Gesture(asset.StopClips[fighter.PatrolStopPick % asset.StopClips.Count]);
            }
        }

        body.Tick(moved, GameplayCVars.ModernBodyYaw.Value * MathF.PI / 180f, raise, aiming || ready, member.Agent.ShotSequence, kneeling, keepsDown, turning,
            crouched: fighter?.CrouchMoving ?? false, heading, travel: step, look: look, searching: ready, scanning: fighter?.KneelScanning ?? false, limp: fighter?.LimpLeg ?? 0, lean: fighter?.LeanSide ?? 0);
        if (GameplayCVars.BodyLog.Value >= 0 && GameplayCVars.BodyLog.Value == view.Soldier.Id && fighter is not null)
        {
            _logger.Information(LogCategory + ".Body", FormattableString.Invariant(
                $"t{_sim.TickCount} at {fighter.Position.X / WorldScene.UnitsPerMetre:F2} {fighter.Position.Y / WorldScene.UnitsPerMetre:F2} face {fighter.Facing * 180f / MathF.PI:F1} node {fighter.CurrentNode}>{fighter.DestinationNode} {fighter.CoverState} state {fighter.CombatState} kneel {kneeling}: {body.Showing}"));
        }
    }

    private readonly Dictionary<SoldierMember, string> _modernForcedSeen = [];
    private readonly Dictionary<SoldierMember, bool> _modernBlinded = [];
    private int _modernForcedTicks;

    // A test range's pose (dev_modern_pose), whatever the soldier is doing: the clips played on the spot at their own pace.
    // A soldier with a brain is drawn at his clip's root height above the ground. His own flinch, kneel or crouch brings that
    // down, but the character's clips carry their own hips -- his kneel too -- so the whole drop is given back, or a kneeling
    // man struck sank onto his heels for the three seconds the original's flinch keeps its root low. (Measured from the highest
    // his root has stood, which is standing.)
    private float Raise(SoldierMember member, SoldierView view)
    {
        float top = Math.Max(_modernRootTops.GetValueOrDefault(member, view.RootSample.Z), view.RootSample.Z);
        _modernRootTops[member] = top;
        return (top - view.RootSample.Z) / (float)WorldScene.UnitsPerMetre;
    }

    // A round that struck without killing: the character's own flinch, the way it pushed him.
    private void TakeHit(SoldierMember member, ModernBody body, ModernBodyAsset asset, SoldierView view)
    {
        if (view.LastHit.Count != _modernHitsSeen.GetValueOrDefault(member))
        {
            _modernHitsSeen[member] = view.LastHit.Count;
            (float ahead, float right, int zone) = (view.LastHit.Ahead, view.LastHit.Right, view.LastHit.Zone);

            // A captured reaction for where it struck and from where -- a heavy one when it came hard on the heels of
            // another -- played over all of him but his arms, which keep the rifle. A limb also takes a small kick of its own.
            int now = _sim.TickCount;
            bool heavy = _modernLastHitTick.TryGetValue(member, out int last) && now - last <= HeavyHitTicks;
            _modernLastHitTick[member] = now;
            if (asset.Hit(ahead, right, zone, heavy) is { } hit)
            {
                body.Flinch(hit);
            }

            if (zone < 12)
            {
                body.Kick(zone, ahead, right, strength: 0.5f);
            }
        }
    }

    private void TickForcedPose(SoldierMember member, ModernBody body, ModernBodyAsset asset, string forced)
    {
        bool fresh = _modernForcedSeen.GetValueOrDefault(member) != forced;
        _modernForcedSeen[member] = forced;
        float yaw = GameplayCVars.ModernBodyYaw.Value * MathF.PI / 180f;
        if (forced.StartsWith("die", StringComparison.Ordinal))
        {
            if (fresh || !body.Fallen)
            {
                body.Fall(asset.Death(forced.Contains("back", StringComparison.Ordinal), forced.EndsWith("head", StringComparison.Ordinal))!);
            }

            body.Tick(0f, yaw);
            return;
        }

        // kick_<zone>_<front|back|left|right>: the round's kick alone, once each time it is set, for the range to catch at its swing.
        if (fresh && forced.StartsWith("kick_", StringComparison.Ordinal))
        {
            string[] parts = forced.Split('_');
            if (parts.Length == 3 && int.TryParse(parts[1], out int zone))
            {
                (float ahead, float right) = parts[2] switch
                {
                    "back" => (-1f, 0f),
                    "left" => (0f, -1f),
                    "right" => (0f, 1f),
                    _ => (1f, 0f),
                };
                body.Kick(zone, ahead, right);
            }
        }

        // react_<zone>_<front|back|left|right>[_heavy]: the reaction a round there would play, once each time it is set.
        if (fresh && forced.StartsWith("react_", StringComparison.Ordinal))
        {
            string[] parts = forced.Split('_');
            if (parts.Length >= 3 && int.TryParse(parts[1], out int zone))
            {
                (float ahead, float right) = parts[2] switch
                {
                    "back" => (-1f, 0f),
                    "left" => (0f, -1f),
                    "right" => (0f, 1f),
                    _ => (1f, 0f),
                };
                if (asset.Hit(ahead, right, zone, heavy: parts.Length > 3) is { } react)
                {
                    body.Flinch(react);
                }

                if (zone < 12)
                {
                    body.Kick(zone, ahead, right, strength: 0.5f);
                }
            }
        }

        if (fresh && forced.StartsWith("hit_", StringComparison.Ordinal))
        {
            (float ahead, float right) = forced switch
            {
                "hit_back" => (-1f, 0f),
                "hit_left" => (0f, -1f),
                "hit_right" => (0f, 1f),
                _ => (1f, 0f),
            };
            if (asset.Hit(ahead, right) is { } hit)
            {
                body.Flinch(hit);
            }
        }

        const float tick = 1f / 30f;
        float moved = forced switch
        {
            "walk" => asset.Walk.MetresPerSecond * tick,
            "aim_walk" => asset.AimWalk.MetresPerSecond * tick,
            "run" => asset.Run.MetresPerSecond * tick,
            _ => 0f,
        };
        // body_check, hand_signal: once each time it is set; search_walk: walking on the spot.
        if (fresh && forced is "body_check" or "hand_signal")
        {
            body.Gesture(forced == "body_check" ? asset.BodyCheck : asset.HandSignal);
        }

        // run_start, run_stop_left, run_stop_right: once each time it is set, on the spot.
        if (fresh && forced is "run_start" or "run_stop_left" or "run_stop_right")
        {
            body.Act(forced == "run_start" ? asset.RunStart : forced == "run_stop_left" ? asset.RunStopLeft : asset.RunStopRight);
        }

        // limp_walk_left and the rest: limping on the spot at the clip's own pace.
        if (forced.StartsWith("limp_", StringComparison.Ordinal))
        {
            int leg = forced.EndsWith("left", StringComparison.Ordinal) ? 0 : 1;
            ModernClip limp = (forced.Contains("run", StringComparison.Ordinal) ? asset.LimpRun : asset.LimpWalk)[leg];
            float pace = limp.MetresPerSecond * tick;
            body.Tick(pace, GameplayCVars.ModernBodyYaw.Value * MathF.PI / 180f, 0f, aiming: true, travel: new Vector2(pace, 0f), limp: leg == 0 ? -1 : 1);
            return;
        }

        if (forced == "search_walk")
        {
            float step = asset.SearchWalk.MetresPerSecond * tick;
            body.Tick(step, yaw, 0f, aiming: true, travel: new Vector2(step, 0f), searching: true);
            return;
        }

        // reload, throw: played once each time it is set; turn_left, turn_right: a steady turn of 60 degrees a second.
        if (fresh && forced is "reload" or "throw")
        {
            body.Act(forced == "reload" ? asset.Reload : asset.Throw);
        }

        // turn_left, turn_right: a steady 60 degrees a second (the 45-degree steps); turn_fast_left, turn_fast_right: 240 (the
        // about-face's).
        float turning = forced switch
        {
            "turn_left" => MathF.PI / 3f, "turn_right" => -MathF.PI / 3f,
            "turn_fast_left" => 4f * MathF.PI / 3f, "turn_fast_right" => -4f * MathF.PI / 3f, _ => 0f,
        };
        Vector2 heading = forced switch { "strafe_left" => new(0f, -1f), "strafe_right" => new(0f, 1f), "walk_back" => new(-1f, 0f), _ => default };
        if (heading != Vector2.Zero || forced == "crouch_walk")
        {
            ModernClip clip = forced switch { "strafe_left" => asset.StrafeLeft, "strafe_right" => asset.StrafeRight, "walk_back" => asset.WalkBack, _ => asset.CrouchWalk };
            // At the modern AI's own paces for each, which the planted feet are held against.
            float pace = forced switch { "crouch_walk" => 1.2f, "walk_back" => 1.86f, _ => 3.0f };
            Vector2 way = heading == Vector2.Zero ? new Vector2(1f, 0f) : heading;
            body.Tick(pace * tick, yaw, 0f, aiming: true, 0, crouched: forced == "crouch_walk", heading: heading, travel: way * pace * tick);
            return;
        }

        bool aiming = forced is "aim" or "fire" or "aim_walk" or "kneel_aim" || forced.StartsWith("turn_", StringComparison.Ordinal);
        Vector2 travel = moved > 0f ? new Vector2(moved, 0f) : default;
        int shots = forced == "fire" ? ++_modernForcedTicks / 10 : 0;
        // aim_up: aiming at a man 30 degrees above; look_right: watching one 60 degrees to his right.
        Vector3 look = forced switch
        {
            "aim_up" => new Vector3(0f, 1f, MathF.Tan(MathF.PI / 6f)),
            "look_right" => new Vector3(MathF.Sin(MathF.PI / 3f), MathF.Cos(MathF.PI / 3f), 0f),
            _ => default,
        };
        aiming |= forced is "aim_up" or "lean_left" or "lean_right" or "run_start" or "run_stop_left" or "run_stop_right";
        float forcedRaise = Raise(member, member.Agent.View);
        _modernStandingHeights[member] = member.TickWhere.Z + forcedRaise; // the height a fall from this pose starts at
        body.Tick(moved, yaw, forcedRaise, aiming, shots, kneeling: forced is "kneel" or "kneel_aim" or "kneel_scan", keepsDown: forced == "kneel", turning: turning, travel: travel, look: look, scanning: forced == "kneel_scan", lean: forced == "lean_left" ? -1 : forced == "lean_right" ? 1 : 0);
    }

    // The fall the killing shot calls for, started on the body he died in; false where there is none to play.
    private bool StartFall(SoldierMember member, ModernBodyAsset asset, SoldierView view)
    {
        if (member.Modern is not { } body || ModernFallFor(member, asset, view, body) is not { } fall)
        {
            return false;
        }

        // The height he was drawn at standing, from the tick before: this tick's may already be his death clip's.
        _modernFallHeights[member] = _modernStandingHeights.GetValueOrDefault(member, member.TickWhere.Z);
        bool brief = ReferenceEquals(fall, asset.ShortDeath(view.KillShot.FromBehind, view.KillShot.Headshot, view.KillAngle));
        body.Fall(fall, brief);

        // A round from the front into his body lands on one side of him, and that side takes it; a little even from near his centre.
        if (brief && !view.KillShot.FromBehind && !view.KillShot.Headshot && view.KillAngle is { Leg: 0, Side: 0 })
        {
            body.Impact(view.KillAngle.Landed, 0.6f + (0.4f * view.KillOffCentre));
        }

        return true;
    }

    // The fall a kill plays: with the ragdoll on, a short one for a man standing, which physics finishes. Not for a kneel, whose
    // falls are their own; not for a man running, who carries on into the fall from the run; not for a blast, which throws him at once.
    private static ModernClip? ModernFallFor(SoldierMember member, ModernBodyAsset asset, SoldierView view, ModernBody body)
    {
        bool standing = !body.Kneeling && !IsBlast(member) && CarriedMetresPerSecond(member) <= RagdollRunningMetresPerSecond;
        return GameplayCVars.ModernRagdoll.Value && standing && asset.ShortDeath(view.KillShot.FromBehind, view.KillShot.Headshot, view.KillAngle) is { } brief
            ? brief
            : asset.Death(view.KillShot.FromBehind, view.KillShot.Headshot, body.Kneeling);
    }

    private static bool IsBlast(SoldierMember member) => member.ModernFall?.Fall.Category is WeaponCategory.Grenade or WeaponCategory.ProximityMine;

    private static double CarriedMetresPerSecond(SoldierMember member) => (member.ModernFall?.Going.Length ?? 0.0) / WorldScene.UnitsPerMetre * 30.0;

    // The character, read the first time it is wanted and again whenever the file named changes; null while off or unreadable.
    private ModernBodyAsset? ModernAsset()
    {
        if (!GameplayCVars.ModernBodies.Value)
        {
            return null;
        }

        string path = GameplayCVars.ModernBodyFile.Value;
        if (path != _modernAssetPath)
        {
            ReleaseModernBodies();
            _modernAssetPath = path;
            _modernAssetFailed = false;
        }

        if (_modernAsset is null && !_modernAssetFailed)
        {
            try
            {
                _modernAsset = ModernBodyAsset.Load(_device, path);
                _logger.Information(LogCategory, FormattableString.Invariant(
                    $"Modern bodies from {path}: {_modernAsset.Vertices.Length} vertices, {_modernAsset.Indices.Length / 3} triangles, {_modernAsset.Joints.Length} joints, walk {_modernAsset.WalkMetresPerSecond:0.00} m/s."));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException
                or NotSupportedException or SharpGLTF.Validation.DataException)
            {
                // Once: a soldier a tick asking again would fill the log, and the original models carry on meanwhile.
                _modernAssetFailed = true;
                _logger.Warning(LogCategory, $"Modern bodies off: could not read '{path}': {ex.Message}");
            }
        }

        if (_modernAsset is not null)
        {
            // The vertex colour is decoded from sRGB before it multiplies the albedo, so the linear tone is encoded into it.
            float tone = GameplayCVars.ModernBodyTone.Value;
            byte grey = (byte)Math.Round(255.0 * (tone <= 0.0031308f ? 12.92 * tone : (1.055 * Math.Pow(tone, 1.0 / 2.4)) - 0.055));
            _modernAsset.Tone = Bgra32.FromRgba(grey, grey, grey, 255);
        }

        return _modernAsset;
    }

    private static void DropModernBody(SoldierMember member)
    {
        member.Modern?.Dispose();
        member.Modern = null;
        member.ModernCorpse = null;
        member.ModernFall = null;
    }

    /// <summary>How long a fall plays before the ragdoll takes it over, in seconds: a moment, or less for a man at a run, who would stop dead.</summary>
    public const float RagdollAfterShotgunSeconds = 0.1f, RagdollAfterSeconds = 0.25f, RagdollAfterRunningSeconds = 0.1f;

    /// <summary>Above this speed, in metres a second, a man shot dead counts as running.</summary>
    public const double RagdollRunningMetresPerSecond = 2.2;

    private ModernRagdoll? _modernRagdoll;
    private ModernBodyAsset? _modernRagdollOf;

    // The dead body's tick once its fall has played a moment: the ragdoll takes over from the skeleton on screen. False while the clip
    // still has him, or where there is no ragdoll for him.
    private bool TickModernCorpse(SoldierMember member, ModernBody body, ModernBodyAsset asset, SoldierView view)
    {
        double units = WorldScene.UnitsPerMetre;
        Vector3d where = member.TickWhere * units;
        if (member.ModernCorpse is null)
        {
            // A blast throws him at once; a run carries on into the fall; anything else plays its clip a moment first.
            bool blast = IsBlast(member);
            double speed = CarriedMetresPerSecond(member);

            // A short fall runs to its end, where it is going at the speed physics carries on from; and its own motion is the round's.
            bool brief = body.FallIsBrief;

            // A shotgun's charge is more than any clip plays: it cuts the fall short and throws him itself.
            bool thrown = brief && member.ModernFall?.Fall.Category == WeaponCategory.Shotgun;
            float after = blast ? 0f : speed > RagdollRunningMetresPerSecond ? RagdollAfterRunningSeconds : thrown ? RagdollAfterShotgunSeconds : brief ? body.FallDuration : RagdollAfterSeconds;
            if (!body.Fallen || body.FallTime < after || body.Kept.Now.Length == 0)
            {
                return false;
            }

            if (!ReferenceEquals(_modernRagdollOf, asset))
            {
                (_modernRagdollOf, _modernRagdoll) = (asset, ModernRagdoll.Build(asset, units));
            }

            if (_modernRagdoll is null)
            {
                return false;
            }

            (System.Numerics.Matrix4x4[] now, System.Numerics.Matrix4x4[] before, float lift, float liftBefore) = body.Kept;
            RagdollBody seeded = _modernRagdoll.Seed(now, before, lift, liftBefore, where, member.PrevTickWhere * units, member.TickOrientation);
            if (member.ModernFall is var (fall, going))
            {
                // The way he was going, and the round that killed him.
                seeded.AddVelocity(going);
                double force = GameplayCVars.RagdollForce.Value;
                double momentum = brief && !thrown
                    ? RagdollImpulse.MomentumAfterClip(_modernRagdoll.Rig, fall.Category, force)
                    : RagdollImpulse.MomentumOf(_modernRagdoll.Rig, fall.Category, force);
                RagdollImpulse.Apply(seeded, -1, fall.Travel, momentum, blast, blast ? null : fall.Struck);
            }

            member.ModernCorpse = seeded;
            _logger.Information(LogCategory + ".Corpse", FormattableString.Invariant($"soldier {view.Soldier.Id}: the ragdoll takes over at {body.FallTime:0.00} s, going {speed:0.0} m/s, leg {view.KillAngle.Leg} side {view.KillAngle.Side} landed {view.KillAngle.Landed}, {member.ModernFall?.Fall.Category}."));
        }

        if (member.ModernCorpse is not { } corpse || _modernRagdoll is not { } ragdoll)
        {
            return false;
        }

        bool wasResting = corpse.IsAtRest;

        // Thrown by a shotgun, he has no muscle left to stand against it: held up as long as the rest, the blast seems to land late.
        var muscle = SoldierFrameInterpolator.MuscleTicks();
        if (body.FallIsBrief && member.ModernFall?.Fall.Category == WeaponCategory.Shotgun)
        {
            muscle = Math.Min(muscle, 3);
        }

        corpse.Step(_sim.RagdollWorldForCorpses(), GameplayCVars.RagdollSubsteps.Value, GameplayCVars.RagdollGravity.Value, null, muscle);
        if (!corpse.IsValid)
        {
            member.ModernCorpse = null;
            _logger.Warning(LogCategory + ".Corpse", $"soldier {view.Soldier.Id}: the ragdoll left the level; the fall's clip has him.");
            return false;
        }

        body.TickPosed(world => ragdoll.Write(corpse, where, member.TickOrientation, world), GameplayCVars.ModernBodyYaw.Value * MathF.PI / 180f);
        if (corpse.IsAtRest && !wasResting)
        {
            double standing = _modernFallHeights.GetValueOrDefault(member, member.TickWhere.Z);
            _logger.Information(LogCategory + ".Corpse", FormattableString.Invariant(
                $"soldier {view.Soldier.Id}: at rest after {corpse.Age / 30.0:0.0} s, lowest of him {member.TickWhere.Z + body.LowestSkinned - standing - body.Floor:0.00} m from the floor he stood on."));
        }

        return true;
    }

    // Every soldier's modern body, and the character they were made from.
    private void ReleaseModernBodies()
    {
        foreach (SoldierGroup group in _groups)
        {
            foreach (SoldierMember member in group.Soldiers)
            {
                DropModernBody(member);
            }
        }

        _modernAsset?.Dispose();
        _modernAsset = null;
    }
}
