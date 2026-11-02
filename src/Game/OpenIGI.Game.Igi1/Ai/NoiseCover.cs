namespace OpenIGI.Game.Igi1.Ai;

/// <summary>What stands between a soldier and a noise, which decides how much of its range the noise keeps. <b>Not retail.</b></summary>
public enum NoiseCover
{
    /// <summary>Nothing: the noise carries its whole range.</summary>
    Open,

    /// <summary>Only moving objects, a door leaf: it carries most of its range (<see cref="Igi1HumanAi.DoorHearingShare"/>).</summary>
    Door,

    /// <summary>Terrain or a building's wall, floor or roof: it carries a quarter (<see cref="Igi1HumanAi.MuffledHearingShare"/>).</summary>
    Wall,
}
