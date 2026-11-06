using OpenIGI.Core.Numerics;
using OpenIGI.Game.Igi1.Ai;
using OpenIGI.Game.Igi1.Player;
using OpenIGI.Game.Player;
using OpenIGI.Game.Weapons;
using OpenIGI.Game.World;

namespace OpenIGI.Game.Igi1.World;

/// <content>
/// The knife takedown: who can be taken, the key, the clock while the pair runs, then the silent kill and the player where the attacker
/// ended. <b>Not retail.</b>
/// </content>
public sealed partial class Igi1MissionTick
{
    /// <summary>How near a soldier must be, on the ground in metres, and how far above or below.</summary>
    public const double TakedownReachMetres = 1.6, TakedownHeightMetres = 0.5;

    /// <summary>How far off directly behind him the player may be, and off looking at him, as cosines: within about 55 and 60 degrees.</summary>
    public const double TakedownBehindCosine = 0.57, TakedownLookCosine = 0.5;

    /// <summary>How far off a soldier who sees it notices a takedown, in metres.</summary>
    public const double TakedownSeenMetres = 30.0;

    /// <summary>From when, in the pair's ticks, a takedown can be seen for what it is (the grab), and how often it is looked for.</summary>
    public const int TakedownSeenFromTick = 26, TakedownSeenEveryTicks = 5;

    // What it sounds like, tick by tick with the clip. Each can be replaced by a file (Igi1MissionAudio.PlayTakedownCue).
    private static readonly (int Tick, string Cue, string Sound)[] TakedownCues =
    [
        (24, "grab", "punched_01"), (34, "struggle", "ai_hit_01"), (40, "stab", "stab_01"), (47, "out", "knife_1"), (50, "gasp", "ai_hit_03"), (79, "fall", "bodyfall_3"),
    ];

    /// <summary>How far behind the victim the attacker's clip starts him, in metres (takedown v3).</summary>
    public const double TakedownStartMetres = 0.7;

    private void TickTakedown(PlayerCamera player)
    {
        if (mission.SoldierPresenter?.Simulation is not { } soldiers)
        {
            return;
        }

        if (soldiers.Takedown is { } run)
        {
            soldiers.TakedownOffer = null;
            run.Tick++;
            foreach ((int tick, string cue, string sound) in TakedownCues)
            {
                if (tick == run.Tick)
                {
                    audio.PlayTakedownCue(cue, sound, run.Victim.Position);
                }
            }

            if (run.Tick >= TakedownSeenFromTick && run.Tick % TakedownSeenEveryTicks == 0)
            {
                TellWitnesses(player, soldiers, run);
            }

            if (run.Tick >= TakedownRun.Ticks)
            {
                EndTakedown(player, soldiers, run);
            }

            return;
        }

        if (soldiers.TakedownTestPlace is { } placed)
        {
            soldiers.TakedownTestPlace = null;
            StandBehind(player, soldiers, placed, placed.Position.Z);
        }

        soldiers.TakedownOffer = GameplayCVars.Takedowns.Value && GameplayCVars.ModernBodies.Value && !mission.IsPlayerDead
            && player is { IsOnLadder: false, IsInWorldInteraction: false } && player.InteractionPrompt(1f) is null
            ? TakedownTarget(player, soldiers)
            : null;
        if (soldiers.TakedownOffer is { } offered
            && (GameplayCVars.BotUse.Value || player.UseWentDown(PlayerInputSampler.SampleFrame(host.ViewInput, player.InputProfile))))
        {
            BeginTakedown(player, soldiers, offered);
        }
    }

    // Whoever has the player in his cone with a clear line, near enough, knows what he saw: the whole squad's fight starts from him.
    private static void TellWitnesses(PlayerCamera player, SoldierSimulation soldiers, TakedownRun run)
    {
        Vector3d eye = player.Body.Eye;
        double reach = TakedownSeenMetres * WorldScene.UnitsPerMetre;
        foreach (SoldierView view in soldiers.SoldierViews)
        {
            if (!ReferenceEquals(view, run.Victim) && !view.IsDead && !run.Witnesses.Contains(view)
                && soldiers.Brains.TryGetValue(view, out Igi1HumanAi? brain) && !brain.HasCombatRecord)
            {
                (double distance, bool inCone, bool clear, _, _) = brain.ProbeDetection(eye);
                if (distance <= reach && inCone && clear && brain.ReceiveAlert(eye, radiusInUnits: reach))
                {
                    run.Witnesses.Add(view);
                    brain.ModernLog?.Invoke("saw the takedown");
                }
            }
        }
    }

    /// <summary>Starts a takedown: the player stands where the attacker's clip starts him, and the view goes to its eye from where it was.</summary>
    /// <param name="player">The player.</param>
    /// <param name="soldiers">The soldiers.</param>
    /// <param name="victim">The soldier taken down.</param>
    private static void BeginTakedown(PlayerCamera player, SoldierSimulation soldiers, SoldierView victim)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(soldiers);
        ArgumentNullException.ThrowIfNull(victim);
        soldiers.Takedown = new TakedownRun(victim, player.Camera);
        soldiers.TakedownOffer = null;
        StandBehind(player, soldiers, victim, player.Body.Position.Z);
        player.IsHeld = true;
    }

    private static void StandBehind(PlayerCamera player, SoldierSimulation soldiers, SoldierView victim, double floor)
    {
        if (soldiers.Brains.TryGetValue(victim, out Igi1HumanAi? brain))
        {
            // The brain's forward is -sin, cos of its facing; so is a spawn's gamma.
            Vector3d behind = new(Math.Sin(brain.Facing) * TakedownStartMetres * WorldScene.UnitsPerMetre, -Math.Cos(brain.Facing) * TakedownStartMetres * WorldScene.UnitsPerMetre, 0.0);
            Vector3d at = victim.Position + behind;
            player.StandAt(new Vector3d(at.X, at.Y, floor), brain.Facing);
        }
    }

    // Shot dead with his hands full: the tick of the living player no longer comes, so it ends here, and the man in his grip lives.
    private void EndTakedownIfPlayerDied()
    {
        if (mission.IsPlayerDead && mission.SoldierPresenter?.Simulation is { Takedown: not null } soldiers)
        {
            soldiers.Takedown = null;
            if (mission.Player is { } player)
            {
                player.IsHeld = false;
            }
        }
    }

    private void EndTakedown(PlayerCamera player, SoldierSimulation soldiers, TakedownRun run)
    {
        soldiers.Takedown = null;
        player.IsHeld = false;
        soldiers.SilentDeaths.Add(run.Victim);
        if (run.End is ({ } feet, float gamma))
        {
            player.StandAt(new Vector3d(feet.X, feet.Y, player.Body.Position.Z), gamma, TakedownRun.EndPitch);
        }

        // The blade's own silent kill on a man with no fight on (SoldierDamage.TakeRound). He has bled already, at the wound.
        if (mission.WeaponTypes?.Weapons.FirstOrDefault(weapon => weapon.Category == WeaponCategory.Knife) is { } knife)
        {
            Vector3d neck = run.Victim.Position + new Vector3d(0.0, 0.0, 1.4 * WorldScene.UnitsPerMetre);
            HumanHit hit = new(neck, 0.0, 16, 0f, 0f);
            Action<object, Vector3d, Vector3f>? bleed = soldiers.BloodSpray;
            soldiers.BloodSpray = null;
            soldiers.StrikeWithRound(run.Victim, in hit, neck, neck, knife, 1000f, null);
            soldiers.BloodSpray = bleed;
        }
    }

    // The highest ground under a point: the terrain's, or anything standing on it.
    private double? GroundUnder(WorldRayCast walls, double x, double y, double from)
    {
        double? height = host.Ground is { } terrain && terrain.TryGetGround(x, y, from, out TerrainGround ground) ? ground.Height : null;
        if (walls(new Vector3d(x, y, from), new Vector3d(x, y, from - (3.0 * WorldScene.UnitsPerMetre)), out WorldRayHit hit) && (height is null || hit.Position.Z > height))
        {
            height = hit.Position.Z;
        }

        return height;
    }

    // The nearest soldier the player stands behind and looks at, in reach, who has no idea, with nothing between them.
    private SoldierView? TakedownTarget(PlayerCamera player, SoldierSimulation soldiers)
    {
        Vector3d feet = player.Body.Position;
        double reach = TakedownReachMetres * WorldScene.UnitsPerMetre, best = double.MaxValue;
        SoldierView? found = null;
        float sinYaw = MathF.Sin(player.Yaw), cosYaw = MathF.Cos(player.Yaw);
        foreach (SoldierView view in soldiers.SoldierViews)
        {
            Vector3d to = view.Position - feet;
            double along = Math.Sqrt((to.X * to.X) + (to.Y * to.Y));
            if (along > reach || along < 1.0 || along >= best || Math.Abs(to.Z) > TakedownHeightMetres * WorldScene.UnitsPerMetre + PlayerBody.StandingEyeHeight
                || view.IsDead || !soldiers.Brains.TryGetValue(view, out Igi1HumanAi? brain)
                || brain.HasCombatRecord || brain.WatchingPlayer)
            {
                continue;
            }

            double ux = to.X / along, uy = to.Y / along;
            if ((ux * -Math.Sin(brain.Facing)) + (uy * Math.Cos(brain.Facing)) < TakedownBehindCosine
                || (ux * sinYaw) + (uy * cosYaw) < TakedownLookCosine)
            {
                continue;
            }

            Vector3d eye = player.Body.Eye, chest = view.Position + new Vector3d(0.0, 0.0, 1.2 * WorldScene.UnitsPerMetre);
            if (player.GrenadeWalls is { } walls && walls(eye, chest, out _))
            {
                continue;
            }

            // And room for the pair to play.
            if (player.GrenadeWalls is { } room && !TakedownRoom.Fits(view.Position, brain.Facing, room, (x, y, from) => GroundUnder(room, x, y, from)))
            {
                continue;
            }

            (best, found) = (along, view);
        }

        return found;
    }
}
