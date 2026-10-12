using System.Text.RegularExpressions;

namespace RavenfieldVR.Input
{
    /// <summary>
    /// Produces left-handed variants of the right-handed default binding files: every input source on one hand
    /// moves to the other, and on controllers with A/B + X/Y faces the button letters follow the hand. Poses,
    /// skeletons and haptics are untouched (they already cover both hands).
    /// </summary>
    public static class BindingsWriter
    {
        private static readonly Regex SourcePath = new Regex(
            "\"path\"\\s*:\\s*\"/user/hand/(left|right)/input/(a|b|x|y|trigger|grip|joystick|thumbstick|trackpad|application_menu)\"");

        public static string MirrorToLeftHanded(string bindingJson, string controllerType)
        {
            bool letterFaces = controllerType == "oculus_touch" || controllerType == "hpmotioncontroller";
            string mirrored = SourcePath.Replace(bindingJson, m =>
            {
                string hand = m.Groups[1].Value == "left" ? "right" : "left";
                string input = m.Groups[2].Value;
                if (letterFaces)
                {
                    switch (input)
                    {
                        case "a": input = "x"; break;
                        case "b": input = "y"; break;
                        case "x": input = "a"; break;
                        case "y": input = "b"; break;
                    }
                }
                return $"\"path\": \"/user/hand/{hand}/input/{input}\"";
            });
            return mirrored.Replace("Ravenfield VR default", "Ravenfield VR left-handed")
                           .Replace("Right hand shoots, left hand moves", "Left hand shoots, right hand moves");
        }
    }
}
