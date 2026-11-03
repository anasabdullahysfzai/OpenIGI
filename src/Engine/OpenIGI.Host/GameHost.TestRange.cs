using System.Globalization;
using OpenIGI.Core.Configuration;
using OpenIGI.Core.Diagnostics;
using OpenIGI.Core.Graphics;
using OpenIGI.Core.Numerics;
using OpenIGI.Game;
using OpenIGI.Game.Ai;
using OpenIGI.Host.Infrastructure;
using OpenIGI.Presentation.Cameras;
using OpenIGI.UI;
using OpenIGI.UI.Layout;

namespace OpenIGI.Host;

public sealed partial class GameHost
{
    private string? _pendingScreenshot;

    // The animation viewer's list (dev_pose_next): every clip of a modern body, at ease, in a fight, struck and falling.
    private static readonly string[] ViewerPoses =
    [
        "idle", "walk", "aim", "fire", "aim_walk", "run", "kneel", "kneel_aim", "turn_left", "turn_right", "reload", "throw", "crouch_walk", "strafe_left", "strafe_right", "walk_back",
        "react_15_front", "react_15_back", "react_13_front", "react_13_front_heavy", "react_14_back", "react_14_back_heavy",
        "react_13_left", "react_13_left_heavy", "react_13_right", "react_13_right_heavy",
        "react_5_front", "react_11_front", "react_2_front", "react_8_front",
        "die_front", "die_back", "die_front_head", "die_back_head",
    ];

    private int _viewerPose = -1;
    private string? _replayPose;
    private int _poseLabelUntil;
    private bool _skipCutscenes;

    /// <summary>Registers the commands a test range is driven by: look at a soldier from a side, and write the frame.</summary>
    /// <param name="console">The console to register with.</param>
    /// <remarks>
    /// With <c>-runat</c> they make a capture that needs nobody at the keyboard: stand the free camera off a guard, give his
    /// body a pose (<c>dev_modern_pose</c>), write the frame, and go round him. Not a gameplay feature.
    /// </remarks>
    private void RegisterTestRangeCommands(ConsoleSystem console)
    {
        console.Commands.Add(
            "dev_look",
            "dev_look <soldier id> [degrees round from his front] [metres away] [eye height]: the free camera on a soldier.",
            context =>
            {
                string[] args = context.Arguments.ToArray();
                if (args.Length < 1 || !int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
                {
                    context.Print("dev_look <soldier id> [degrees] [metres] [height]");
                    return;
                }

                float Arg(int i, float otherwise) =>
                    args.Length > i && float.TryParse(args[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ? value : otherwise;
                float degrees = Arg(1, 0f), distance = Arg(2, 3f), height = Arg(3, 1.6f);

                List<SoldierSnapshot> soldiers = [];
                _soldiers?.Snapshot(soldiers);
                if (soldiers.FirstOrDefault(s => s.Id == id) is not { Name: not null } soldier || soldier.Id != id)
                {
                    context.Print($"dev_look: no soldier {id}.");
                    return;
                }

                // Round from his front, anticlockwise seen from above: 90 is his left, 180 his back.
                double turn = degrees * Math.PI / 180.0;
                Vector3d f = soldier.Forward;
                Vector3d along = new((f.X * Math.Cos(turn)) - (f.Y * Math.Sin(turn)), (f.X * Math.Sin(turn)) + (f.Y * Math.Cos(turn)), 0.0);
                Vector3d target = soldier.Position + new Vector3d(0.0, 0.0, 1.0);
                Vector3d eye = soldier.Position + (along * distance) + new Vector3d(0.0, 0.0, height);
                Vector3d look = target - eye;

                // Camera3D.LookRotation's yaw is measured from +Y, x first.
                float yaw = (float)Math.Atan2(look.X, look.Y);
                float pitch = (float)Math.Asin(look.Z / look.Length);
                _camera = new FreeCamera(eye, yaw, pitch);
                _playerMode = false;
                context.Print(FormattableString.Invariant($"dev_look: soldier {id} from {degrees:0} degrees, {distance:0.0} m."));
            });

        console.Commands.Add(
            "dev_stand_near",
            "dev_stand_near <soldier id> [degrees round from his front] [metres away]: stands the player there, facing him.",
            context =>
            {
                string[] args = context.Arguments.ToArray();
                if (args.Length < 1 || !int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
                {
                    context.Print("dev_stand_near <soldier id> [degrees] [metres]");
                    return;
                }

                float Arg(int i, float otherwise) =>
                    args.Length > i && float.TryParse(args[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ? value : otherwise;
                List<SoldierSnapshot> soldiers = [];
                _soldiers?.Snapshot(soldiers);
                if (soldiers.FirstOrDefault(s => s.Id == id) is not { Name: not null } soldier || soldier.Id != id
                    || HostedPlayer is not { } player)
                {
                    context.Print($"dev_stand_near: no soldier {id}, or no player.");
                    return;
                }

                double turn = Arg(1, 0f) * Math.PI / 180.0;
                Vector3d f = soldier.Forward;
                Vector3d along = new((f.X * Math.Cos(turn)) - (f.Y * Math.Sin(turn)), (f.X * Math.Sin(turn)) + (f.Y * Math.Cos(turn)), 0.0);
                Vector3d at = soldier.Position + (along * Arg(2, 12f));
                Vector3d look = soldier.Position - at;
                if (!player.Place(_mission.Ground, at, (float)Math.Atan2(look.X, look.Y)))
                {
                    context.Print("dev_stand_near: could not stand the player there.");
                    return;
                }

                _playerMode = true;
                context.Print(FormattableString.Invariant($"dev_stand_near: {Arg(2, 12f):0.0} m from soldier {id}."));
            });

        console.Commands.Add(
            "dev_pose_next",
            "Shows the next animation of the modern soldier bodies (the animation viewer); dev_pose_prev goes back.",
            context => ShowViewerPose(context, +1));
        console.Commands.Add(
            "dev_pose_prev",
            "Shows the animation of the modern soldier bodies before this one.",
            context => ShowViewerPose(context, -1));
        console.Commands.Add(
            "dev_pose_replay",
            "Plays the modern soldier bodies' animation again from its start (a reaction or a fall plays once).",
            context =>
            {
                _replayPose = GameplayCVars.ModernPose.Value;
                GameplayCVars.ModernPose.Value = string.Empty;
                _poseLabelUntil = _loop.Clock.FrameCount + 180;
            });
        console.Commands.Add(
            "dev_pose_off",
            "Hands the modern soldier bodies back to their soldiers.",
            context =>
            {
                GameplayCVars.ModernPose.Value = string.Empty;
                _viewerPose = -1;
                _poseLabelUntil = _loop.Clock.FrameCount + 120;
            });

        console.Commands.Add(
            "dev_skip_cutscenes",
            "Skips every cutscene that can be skipped, from now on, as holding the key would.",
            context =>
            {
                _skipCutscenes = true;
                context.Print("dev_skip_cutscenes: on.");
            });

        console.Commands.Add(
            "screenshot",
            "screenshot <file.bmp>: writes the next frame drawn.",
            context =>
            {
                string? path = context.Arguments.Count > 0 ? context.Arguments[0] : null;
                if (string.IsNullOrWhiteSpace(path))
                {
                    context.Print("screenshot <file.bmp>");
                    return;
                }

                _pendingScreenshot = path;
            });
    }

    private void ShowViewerPose(ConsoleCommandContext context, int step)
    {
        _viewerPose = (((_viewerPose < 0 && step < 0 ? 0 : _viewerPose) + step) % ViewerPoses.Length + ViewerPoses.Length) % ViewerPoses.Length;
        GameplayCVars.ModernPose.Value = ViewerPoses[_viewerPose];
        _poseLabelUntil = _loop.Clock.FrameCount + 240;
        context.Print($"dev_pose: {ViewerPoses[_viewerPose]}");
    }

    // Once a frame: a replay puts its pose back the frame after it cleared it, and god mode is kept on the player's health,
    // which a mission load builds afresh.
    private void TickTestRange()
    {
        if (_replayPose is { } replay)
        {
            GameplayCVars.ModernPose.Value = replay;
            _replayPose = null;
        }

        if (_mission.PlayerHealth is { } health)
        {
            health.Invulnerable = GameplayCVars.God.Value;
        }
    }

    // What the animation viewer is showing, top centre, for a few seconds after it changes.
    private void DrawPoseLabel(FrameLoopState state)
    {
        if (_loop.Clock.FrameCount > _poseLabelUntil || _shellType is not { } type)
        {
            return;
        }

        string pose = GameplayCVars.ModernPose.Value;
        string label = pose.Length == 0 ? "ANIMATION: OFF (SOLDIERS' OWN)" : $"ANIMATION {_viewerPose + 1}/{ViewerPoses.Length}: {pose.ToUpperInvariant()}";
        _device.Set2DProjection(SafeWindowWidth, SafeWindowHeight);
        state.Batch.Begin();
        LayoutContext layout = new(_window.Width, _window.Height);
        UiPainter painter = UiPainter.Create(state.Batch, layout);
        float size = layout.Px(28f);
        UiVec2 extent = type.Heading.Measure(label, size);
        painter.DrawTextShadowed(
            type.Heading,
            label,
            new UiVec2(layout.SafeRect.X + ((layout.SafeRect.Width - extent.X) * 0.5f), layout.SafeRect.Y + layout.Px(60f)),
            size,
            Bgra32.FromRgb(236, 235, 230),
            Bgra32.FromRgba(0, 0, 0, 160),
            new UiVec2(0f, layout.Px(1f)));
        state.Batch.End();
    }

    // The frame a screenshot command asked for, written once it is drawn.
    private void WritePendingScreenshot()
    {
        if (_pendingScreenshot is not { } path)
        {
            return;
        }

        _pendingScreenshot = null;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        Bgra32[] frame = new Bgra32[_window.Width * _window.Height];
        _device.CaptureFrame(_window.Width, _window.Height, frame);
        BitmapWriter.Write(path, _window.Width, _window.Height, frame);
        _logger.Information("Desktop", $"Wrote {path}.");
    }
}
