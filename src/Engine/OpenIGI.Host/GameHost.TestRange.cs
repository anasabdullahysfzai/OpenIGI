using System.Globalization;
using OpenIGI.Core.Configuration;
using OpenIGI.Core.Diagnostics;
using OpenIGI.Core.Graphics;
using OpenIGI.Core.Numerics;
using OpenIGI.Game.Ai;
using OpenIGI.Host.Infrastructure;
using OpenIGI.Presentation.Cameras;

namespace OpenIGI.Host;

public sealed partial class GameHost
{
    private string? _pendingScreenshot;
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
