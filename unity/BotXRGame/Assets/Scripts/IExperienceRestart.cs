/// <summary>
/// Implemented by anything that can start the current experience over in
/// place - the run, the robot's approach, Distance Test pins, Cup Detect
/// tracks. The ExperienceBar's Restart button calls it on every active,
/// enabled implementer, so each experience restarts the parts it owns and
/// nothing else needs to know which experience is running.
/// </summary>
public interface IExperienceRestart
{
    void RestartExperience();
}
