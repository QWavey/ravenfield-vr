using RavenfieldVR.Core;
using UnityEngine;

namespace RavenfieldVR.Local
{
    /// <summary>How the screen is shared between the players at this PC.</summary>
    internal enum SplitLayout
    {
        /// <summary>Two players side by side (left and right halves); three or four in quarters.</summary>
        SideBySide,
        /// <summary>Two players one above the other (top and bottom halves); three or four in quarters.</summary>
        AboveBelow,
    }

    /// <summary>
    /// Splitscreen at one PC, 2 to 4 players (the user, 2026-11-14: "splitscreen left and right or above and under. playable with controller
    /// or with keyboard and mouse ... Make Local splitscreen possible up to 4 players"). The settings shown on the Local page; the players
    /// themselves are <see cref="SplitPlayers"/>.
    /// </summary>
    internal static class Splitscreen
    {
        /// <summary>Splitscreen wanted for the next match (and the one running).</summary>
        public static bool On;

        /// <summary>Players at this PC, 2 to 4 (the game's own player is the first). Kept in the settings.</summary>
        public static int Players
        {
            get => VRConfig.SplitPlayers != null ? Mathf.Clamp(VRConfig.SplitPlayers.Value, 2, 4) : 2;
            set
            {
                if (VRConfig.SplitPlayers != null)
                {
                    VRConfig.SplitPlayers.Value = Mathf.Clamp(value, 2, 4);
                }
            }
        }

        public static SplitLayout Layout
        {
            get => VRConfig.SplitAboveBelow != null && VRConfig.SplitAboveBelow.Value ? SplitLayout.AboveBelow : SplitLayout.SideBySide;
            set
            {
                if (VRConfig.SplitAboveBelow != null)
                {
                    VRConfig.SplitAboveBelow.Value = value == SplitLayout.AboveBelow;
                }
            }
        }

        /// <summary>The controller the second player takes (0: the first; 1 when the game's own player uses the first controller).</summary>
        public static int FirstPad
        {
            get => VRConfig.SplitFirstController != null ? Mathf.Clamp(VRConfig.SplitFirstController.Value, 0, 3) : 0;
            set
            {
                if (VRConfig.SplitFirstController != null)
                {
                    VRConfig.SplitFirstController.Value = Mathf.Clamp(value, 0, 3);
                }
            }
        }

        public static string LayoutName => Players > 2 ? "quarters (four parts)" : Layout == SplitLayout.SideBySide ? "side by side (left and right)" : "one above the other";

        public static void NextLayout() => Layout = Layout == SplitLayout.SideBySide ? SplitLayout.AboveBelow : SplitLayout.SideBySide;
    }
}
