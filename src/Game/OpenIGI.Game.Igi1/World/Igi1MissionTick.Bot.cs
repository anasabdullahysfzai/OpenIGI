using OpenIGI.Core.Numerics;
using OpenIGI.Game.Ai;
using OpenIGI.Game.Igi1.Player;

namespace OpenIGI.Game.Igi1.World;

/// <content>
/// A scripted player for testing the soldiers (<see cref="GameplayCVars.BotTarget"/>): his aim held on a soldier's chest, or
/// beside it, so a script can shoot at a soldier as a player would and watch what he does. <b>Not retail.</b>
/// </content>
public sealed partial class Igi1MissionTick
{
    private readonly List<SoldierSnapshot> _botSoldiers = [];

    // The player's eye turned onto the target soldier's chest (or the miss beside it), before his tick reads his look.
    private void AimBot(PlayerCamera player)
    {
        int id = GameplayCVars.BotTarget.Value;
        if (id < 0 || mission.Soldiers is not { } soldiers)
        {
            return;
        }

        _botSoldiers.Clear();
        soldiers.Snapshot(_botSoldiers);
        if (_botSoldiers.FindIndex(s => s.Id == id) is not (>= 0 and var at))
        {
            return;
        }

        const double Metre = 4096.0;
        Vector3d eye = player.Body.Eye;
        // The snapshot is in metres; the player's eye in world units.
        Vector3d chest = (_botSoldiers[at].Position * Metre) + new Vector3d(0.0, 0.0, 1.2 * Metre);
        Vector3d look = chest - eye;

        // The miss is to the soldier's side as the player sees him: along the ground, square to the line of fire.
        double flat = Math.Sqrt((look.X * look.X) + (look.Y * look.Y));
        if (flat > 1.0)
        {
            double miss = GameplayCVars.BotMiss.Value * Metre / flat;
            look += new Vector3d(look.Y * miss, -look.X * miss, 0.0);
        }

        player.Body.Yaw = (float)Math.Atan2(look.X, look.Y);
        player.Body.Pitch = (float)Math.Asin(look.Z / look.Length);
    }
}
