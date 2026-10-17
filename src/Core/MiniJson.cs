using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RavenfieldVR.Core
{
    /// <summary>
    /// A small JSON reader: objects become Dictionary&lt;string, object&gt;, arrays List&lt;object&gt;, numbers double, plus string,
    /// bool and null. For files other programs write (the sandbox's poses): Unity's JsonUtility read the pose file's
    /// nested hands as empty without a word, and the game held the gun the mod's own way (check-1017i).
    /// </summary>
    internal static class MiniJson
    {
        public static object Parse(string text)
        {
            int i = 0;
            object v = Value(text, ref i);
            Skip(text, ref i);
            if (i != text.Length)
            {
                throw new FormatException($"unexpected '{text[i]}' at {i}");
            }
            return v;
        }

        private static void Skip(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i]))
            {
                i++;
            }
        }

        private static object Value(string s, ref int i)
        {
            Skip(s, ref i);
            if (i >= s.Length)
            {
                throw new FormatException("unexpected end");
            }
            char c = s[i];
            switch (c)
            {
                case '{':
                {
                    var o = new Dictionary<string, object>();
                    i++;
                    Skip(s, ref i);
                    if (i < s.Length && s[i] == '}')
                    {
                        i++;
                        return o;
                    }
                    while (true)
                    {
                        Skip(s, ref i);
                        string key = String(s, ref i);
                        Skip(s, ref i);
                        Expect(s, ref i, ':');
                        o[key] = Value(s, ref i);
                        Skip(s, ref i);
                        if (i < s.Length && s[i] == ',')
                        {
                            i++;
                            continue;
                        }
                        Expect(s, ref i, '}');
                        return o;
                    }
                }
                case '[':
                {
                    var a = new List<object>();
                    i++;
                    Skip(s, ref i);
                    if (i < s.Length && s[i] == ']')
                    {
                        i++;
                        return a;
                    }
                    while (true)
                    {
                        a.Add(Value(s, ref i));
                        Skip(s, ref i);
                        if (i < s.Length && s[i] == ',')
                        {
                            i++;
                            continue;
                        }
                        Expect(s, ref i, ']');
                        return a;
                    }
                }
                case '"':
                    return String(s, ref i);
                case 't':
                    Word(s, ref i, "true");
                    return true;
                case 'f':
                    Word(s, ref i, "false");
                    return false;
                case 'n':
                    Word(s, ref i, "null");
                    return null;
                default:
                {
                    int start = i;
                    while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0)
                    {
                        i++;
                    }
                    if (i == start || !double.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                    {
                        throw new FormatException($"bad value at {start}");
                    }
                    return d;
                }
            }
        }

        private static void Expect(string s, ref int i, char c)
        {
            if (i >= s.Length || s[i] != c)
            {
                throw new FormatException($"expected '{c}' at {i}");
            }
            i++;
        }

        private static void Word(string s, ref int i, string w)
        {
            if (string.CompareOrdinal(s, i, w, 0, w.Length) != 0)
            {
                throw new FormatException($"bad value at {i}");
            }
            i += w.Length;
        }

        private static string String(string s, ref int i)
        {
            Expect(s, ref i, '"');
            var sb = new StringBuilder();
            while (i < s.Length && s[i] != '"')
            {
                char c = s[i++];
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }
                if (i >= s.Length)
                {
                    break;
                }
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (i + 4 <= s.Length)
                        {
                            sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16));
                            i += 4;
                        }
                        break;
                    default: sb.Append(e); break;
                }
            }
            Expect(s, ref i, '"');
            return sb.ToString();
        }

        // ---- reading helpers ----------------------------------------------------------------------------------------

        public static Dictionary<string, object> Obj(object o, string key) =>
            o is Dictionary<string, object> d && d.TryGetValue(key, out object v) ? v as Dictionary<string, object> : null;

        public static float[] Floats(object o, string key, int count)
        {
            if (!(o is Dictionary<string, object> d) || !d.TryGetValue(key, out object v) || !(v is List<object> list) || list.Count != count)
            {
                return null;
            }
            var f = new float[count];
            for (int k = 0; k < count; k++)
            {
                if (!(list[k] is double x) || double.IsNaN(x) || double.IsInfinity(x))
                {
                    return null;
                }
                f[k] = (float)x;
            }
            return f;
        }

        public static float Num(object o, string key, float fallback) =>
            o is Dictionary<string, object> d && d.TryGetValue(key, out object v) && v is double x ? (float)x : fallback;

        public static bool Bool(object o, string key) =>
            o is Dictionary<string, object> d && d.TryGetValue(key, out object v) && v is bool b && b;

        public static string Str(object o, string key) =>
            o is Dictionary<string, object> d && d.TryGetValue(key, out object v) ? v as string : null;
    }
}
