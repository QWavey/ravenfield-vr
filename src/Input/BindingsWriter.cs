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
            // The description names the buttons by their letters ("B: drop magazine") and their hands ("Left stick hold",
            // "right A"): both mirrored, or the left-handed layout's text named the buttons where the right-handed one has them
            // (letters: input hunt, 2026-10-21; the hands, which every controller's text names: round-4 scout).
            mirrored = Description.Replace(mirrored, d =>
            {
                string text = letterFaces ? Letter.Replace(d.Value, l => MirrorLetter(l.Groups[1].Value) + ":") : d.Value;
                return HandWord.Replace(text, w => OtherHand(w.Value));
            });
            return mirrored.Replace("Ravenfield VR default", "Ravenfield VR left-handed");
        }

        private static readonly Regex Description = new Regex(@"""description""\s*:\s*""(?:[^""\\]|\\.)*""");
        private static readonly Regex Letter = new Regex(@"\b([ABXY]):");
        private static readonly Regex HandWord = new Regex(@"\b(left|right)\b", RegexOptions.IgnoreCase);

        /// <summary>The other hand's word, in the same case ("Left" to "Right", "right" to "left").</summary>
        private static string OtherHand(string word)
        {
            string other = word.ToLowerInvariant() == "left" ? "right" : "left";
            if (word.Length > 1 && word.ToUpperInvariant() == word)
            {
                return other.ToUpperInvariant();
            }
            return char.IsUpper(word[0]) ? char.ToUpperInvariant(other[0]) + other.Substring(1) : other;
        }

        private static string MirrorLetter(string l)
        {
            switch (l)
            {
                case "A": return "X";
                case "B": return "Y";
                case "X": return "A";
                default: return "B";
            }
        }
    }
}
