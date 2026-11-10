using RavenfieldVR.Rig;
using UnityEngine;
using UnityEngine.UI;

namespace RavenfieldVR.UI
{
    /// <summary>
    /// The first thing in the main menu each time the game starts (the user, 2026-11-10: "pick at the beginning of the game always
    /// (lays over ravenfield) to play single player or multiplayer (then a custom UI comes)"): two buttons over the game's menu,
    /// which waits behind it. Single player closes it; Multiplayer opens the multiplayer panel.
    /// </summary>
    internal sealed class StartChoicePanel : ModPanel
    {
        protected override float CanvasWidth => 900f;
        protected override float WorldWidth => 1.0f;

        public StartChoicePanel(VRRig rig, Camera eventCamera) : base(rig, eventCamera, "RavenfieldVR Start")
        {
        }

        protected override bool HasClose => false; // (two choices, no third)

        protected override void Build()
        {
            Title("Ravenfield VR", "How do you want to play?");
            Gap(10f);
            RectTransform row = Row(150f);
            Big(row, "Single player", 34f + 420f, () => Close());
            Big(row, "Multiplayer", 34f, () =>
            {
                Close();
                UISystem.Instance?.MultiplayerPanel.Open();
            });
            Gap(10f);
            Label("Single player: the game as always. Multiplayer: host a game or join one; it can also be opened later from the VR settings.", 19, FontStyle.Italic, UiKit.MutedText, 60f);
        }

        private void Big(RectTransform row, string label, float x, System.Action onClick)
        {
            Button b = ButtonIn(row, label, x, 410f, onClick);
            var rt = (RectTransform)b.transform;
            rt.sizeDelta = new Vector2(rt.sizeDelta.x, 130f);
            Text t = b.GetComponentInChildren<Text>();
            if (t != null)
            {
                t.fontSize = 40;
            }
        }
    }
}
