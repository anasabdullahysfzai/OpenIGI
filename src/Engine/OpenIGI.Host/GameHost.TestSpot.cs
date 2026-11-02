using System.Globalization;
using OpenIGI.Core.Configuration;
using OpenIGI.Core.Numerics;

namespace OpenIGI.Host;

/// <summary>Where <c>spot_save</c> last stood the player, which <c>spot_load</c> stands him back at.</summary>
[CVarContainer]
public static class TestSpotCVars
{
    /// <summary>The saved spot: the mission's id, then the feet in metres, the yaw and the pitch.</summary>
    public static readonly StringCVar Spot = CVar.Text(
        "dev_spot",
        string.Empty,
        CVarOptions.Archive,
        "The spot spot_save saved and spot_load returns to: mission x y z yaw pitch. Written by spot_save.");
}

public sealed partial class GameHost
{
    /// <summary>Registers the commands that save where the player stands and stand him back there, for testing.</summary>
    /// <param name="console">The console to register with.</param>
    /// <remarks>
    /// Not a save game: nothing but the player's place is kept, so the level is as it loaded and every soldier as fresh,
    /// which is what a test of the soldiers wants. A spot belongs to the mission it was saved in and is refused in another.
    /// The placement is the renderer switch's own (<see cref="ResumePose"/>).
    /// </remarks>
    private void RegisterTestSpotCommands(ConsoleSystem console)
    {
        console.Commands.Add(
            "spot_save",
            "Saves where the player stands in this mission, for spot_load to return to.",
            context =>
            {
                if (_session.Mission is not { } mission || CurrentPose() is not { Flying: false } pose)
                {
                    context.Print("spot_save: stand the player in a mission first.");
                    return;
                }

                TestSpotCVars.Spot.Value = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{mission.Id} {pose.Position.X:0.###} {pose.Position.Y:0.###} {pose.Position.Z:0.###} {pose.Yaw:0.####} {pose.Pitch:0.####}");
                context.Print($"spot_save: {TestSpotCVars.Spot.Value}");
            });

        console.Commands.Add(
            "spot_load",
            "Stands the player back where spot_save saved him, in the same mission.",
            context =>
            {
                string[] parts = TestSpotCVars.Spot.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                double[] values = new double[parts.Length];
                if (parts.Length != 6
                    || !parts.Select((part, i) => double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out values[i])).All(ok => ok))
                {
                    context.Print("spot_load: no spot saved; spot_save first.");
                    return;
                }

                if (_session.Mission is not { } mission || mission.Id != (int)values[0])
                {
                    context.Print($"spot_load: the spot is in mission {(int)values[0]}.");
                    return;
                }

                if (HostedPlayer is not { } player || !player.Place(_mission.Ground, new Vector3d(values[1], values[2], values[3]), (float)values[4]))
                {
                    context.Print("spot_load: could not stand the player there.");
                    return;
                }

                player.Body.Pitch = (float)values[5];
                _playerMode = true;
                context.Print("spot_load: back at the saved spot.");
            });
    }
}
