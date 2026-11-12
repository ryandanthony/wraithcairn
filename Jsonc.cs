using System.Text;

namespace Wraithcairn;

/// <summary>
/// Minimal in-place editor for the JSON-with-comments config.json, so the app
/// can change one setting without throwing away the user's comments, order,
/// and formatting (a plain serialize round-trip would).
/// </summary>
internal static class Jsonc
{
    /// <summary>
    /// Sets top-level <paramref name="key"/> to <paramref name="jsonValue"/>
    /// (already-encoded JSON, e.g. "\"Dracula\"" or "null"). Replaces the
    /// existing value if present, otherwise appends the key before the
    /// closing brace. Falls back to a fresh object if the text has no root.
    /// </summary>
    public static string SetTopLevelProperty(string text, string key, string jsonValue)
    {
        int rootOpen = -1;
        int rootClose = -1;
        int depth = 0;
        (int Start, int End)? existing = null;
        int lastValueEnd = -1;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (TrySkipComment(text, ref i))
            {
                continue;
            }

            if (c == '"')
            {
                int strStart = i;
                int strEnd = SkipString(text, i);
                if (depth == 1 && rootOpen >= 0)
                {
                    // Is this string a property name? (next non-space is ':')
                    int after = SkipTrivia(text, strEnd);
                    if (after < text.Length && text[after] == ':')
                    {
                        string name = text[(strStart + 1)..(strEnd - 1)];
                        int valueStart = SkipTrivia(text, after + 1);
                        int valueEnd = SkipValue(text, valueStart);
                        if (name == key)
                        {
                            existing = (valueStart, valueEnd);
                        }

                        lastValueEnd = valueEnd;
                        i = valueEnd - 1;
                        continue;
                    }
                }

                i = strEnd - 1;
                continue;
            }

            if (c is '{' or '[')
            {
                if (depth == 0 && c == '{' && rootOpen < 0)
                {
                    rootOpen = i;
                }

                depth++;
            }
            else if (c is '}' or ']')
            {
                depth--;
                if (depth == 0 && rootOpen >= 0)
                {
                    rootClose = i;
                    break;
                }
            }
        }

        if (rootOpen < 0 || rootClose < 0)
        {
            return $"{{\n  \"{key}\": {jsonValue}\n}}\n";
        }

        if (existing is { } span)
        {
            return string.Concat(text.AsSpan(0, span.Start), jsonValue, text.AsSpan(span.End));
        }

        // Append as the last property. Add a comma after the previous value
        // unless it already has one (trailing commas are allowed in config.json).
        StringBuilder sb = new();
        if (lastValueEnd < 0)
        {
            // No properties yet. Insert just before the closing brace (after
            // any comments in the body), so those comments survive.
            int insert = rootClose;
            while (insert > rootOpen + 1 && text[insert - 1] is ' ' or '\t')
            {
                insert--;
            }

            string body = text[(rootOpen + 1)..insert];
            bool needsNewline = body.Length > 0 && !body.EndsWith('\n');
            sb.Append(text, 0, insert);
            sb.Append(needsNewline || body.Length == 0 ? "\n" : string.Empty);
            sb.Append($"  \"{key}\": {jsonValue}\n");
            sb.Append(text, rootClose, text.Length - rootClose);
            return sb.ToString();
        }

        int afterLast = SkipTrivia(text, lastValueEnd);
        bool hasComma = afterLast < text.Length && text[afterLast] == ',';
        int insertAt = hasComma ? afterLast + 1 : lastValueEnd;
        sb.Append(text, 0, insertAt);
        if (!hasComma)
        {
            sb.Append(',');
        }

        sb.Append($"\n  \"{key}\": {jsonValue}");
        sb.Append(text, insertAt, text.Length - insertAt);
        return sb.ToString();
    }

    private static bool TrySkipComment(string text, ref int i)
    {
        if (text[i] != '/' || i + 1 >= text.Length)
        {
            return false;
        }

        if (text[i + 1] == '/')
        {
            int nl = text.IndexOf('\n', i);
            i = nl < 0 ? text.Length : nl;
            return true;
        }

        if (text[i + 1] == '*')
        {
            int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
            i = end < 0 ? text.Length : end + 1;
            return true;
        }

        return false;
    }

    /// <summary>Index just past the closing quote of the string starting at <paramref name="i"/>.</summary>
    private static int SkipString(string text, int i)
    {
        for (int j = i + 1; j < text.Length; j++)
        {
            if (text[j] == '\\')
            {
                j++;
            }
            else if (text[j] == '"')
            {
                return j + 1;
            }
        }

        return text.Length;
    }

    /// <summary>Skips whitespace and comments.</summary>
    private static int SkipTrivia(string text, int i)
    {
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
            }
            else if (TrySkipComment(text, ref i))
            {
                i++;
            }
            else
            {
                break;
            }
        }

        return Math.Min(i, text.Length);
    }

    /// <summary>Index just past the JSON value starting at <paramref name="i"/>.</summary>
    private static int SkipValue(string text, int i)
    {
        if (i >= text.Length)
        {
            return i;
        }

        if (text[i] == '"')
        {
            return SkipString(text, i);
        }

        if (text[i] is '{' or '[')
        {
            int depth = 0;
            for (int j = i; j < text.Length; j++)
            {
                if (TrySkipComment(text, ref j))
                {
                    continue;
                }

                char c = text[j];
                if (c == '"')
                {
                    j = SkipString(text, j) - 1;
                }
                else if (c is '{' or '[')
                {
                    depth++;
                }
                else if (c is '}' or ']' && --depth == 0)
                {
                    return j + 1;
                }
            }

            return text.Length;
        }

        // number / true / false / null
        int k = i;
        while (k < text.Length && !char.IsWhiteSpace(text[k]) && text[k] is not (',' or '}' or ']' or '/'))
        {
            k++;
        }

        return k;
    }
}
