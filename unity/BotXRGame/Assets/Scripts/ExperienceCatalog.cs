using UnityEngine;

/// <summary>
/// Every experience on the start menu, in menu order - the game modes and the
/// standalone tests alike.
///
/// To add one: add a value to ShipSource, add a line here, write its mode
/// script (poll GameMode.Chosen, never check the mode in Start), then run
/// Tools > BotXRGame > Wire Session Flow. The menu is a scroll list built from
/// this table, so nothing needs placing by hand and there is no row limit.
/// Experiences that never talk to the robot also go in GameMode.NeedsNoRobot.
/// </summary>
public static class ExperienceCatalog
{
    public struct Entry
    {
        public ShipSource Source;
        public string Title;
        public string Description;
        public Color Colour;

        public Entry(ShipSource source, string title, string description, Color colour)
        {
            Source = source;
            Title = title;
            Description = description;
            Colour = colour;
        }
    }

    public static readonly Entry[] All =
    {
        new Entry(ShipSource.VirtualBot, "Virtual Bot",
                  "The game - fly a virtual ship, no robot needed",
                  new Color(0.16f, 0.42f, 0.28f)),
        new Entry(ShipSource.AprilTag, "AprilTag",
                  "The game - drive the real robot, tracked by its tag",
                  new Color(0.18f, 0.32f, 0.52f)),
        new Entry(ShipSource.LinkTest, "Link Test",
                  "Stick straight to the robot over ROS, nothing else",
                  new Color(0.50f, 0.35f, 0.15f)),
        new Entry(ShipSource.CupDetect, "Cup Detect",
                  "Headset camera finds and tracks cups",
                  new Color(0.45f, 0.18f, 0.22f)),
        new Entry(ShipSource.DistanceTest, "Distance Test",
                  "Aim your hand and pinch - distance from headset depth",
                  new Color(0.15f, 0.40f, 0.45f)),
        new Entry(ShipSource.NpuTest, "NPU Test",
                  "Run the cup model on CPU, Hexagon NPU and GPU - timings",
                  new Color(0.35f, 0.22f, 0.50f)),
        new Entry(ShipSource.ControllerTest, "Controller Test",
                  "Bluetooth gamepad - live sticks, buttons and drive output",
                  new Color(0.30f, 0.40f, 0.18f)),
    };
}
