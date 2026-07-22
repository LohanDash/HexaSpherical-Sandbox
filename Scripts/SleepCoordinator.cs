namespace HexaSphericalSandbox;

public interface ISleepAdvanceRule
{
    bool ShouldAdvanceNight(int sleepingPlayers, int connectedPlayers);
}

public sealed class SoloSleepAdvanceRule : ISleepAdvanceRule
{
    public bool ShouldAdvanceNight(int sleepingPlayers, int connectedPlayers)
        => connectedPlayers == 1 && sleepingPlayers == 1;
}

/// <summary>
/// Global sleep decision, deliberately independent from player position and
/// local solar time. A future server can replace the rule with a vote/majority.
/// </summary>
public sealed class SleepCoordinator(ISleepAdvanceRule rule)
{
    public bool RequestSleep(int sleepingPlayers = 1, int connectedPlayers = 1)
        => rule.ShouldAdvanceNight(sleepingPlayers, connectedPlayers);
}
