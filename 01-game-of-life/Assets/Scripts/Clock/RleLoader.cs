using System;
using System.Globalization;

/// <summary>
/// Reads Life patterns in RLE format, the standard format used by Golly, LifeWiki and most Life tools.
///
/// Example:
///   #C A glider
///   x = 3, y = 3, rule = B3/S23
///   bob$2bo$3o!
///
/// Tokens: b = dead, o = alive, $ = end of row, ! = end. A number before a token repeats it.
/// "2bo" = two dead cells, then one alive cell.
/// </summary>
public static class RleLoader
{
    /// <summary>
    /// Returns one byte per cell (255 = alive), row-major, with row 0 at the BOTTOM
    /// so it can be uploaded straight into a Unity texture.
    /// </summary>
    public static byte[] Parse(string text, out int width, out int height)
    {
        width = height = 0;
        int pos = 0;

        // Header: skip # comment lines, then read "x = ..., y = ..."
        while (pos < text.Length)
        {
            int end = text.IndexOf('\n', pos);
            if (end < 0) end = text.Length;
            string line = text.Substring(pos, end - pos).Trim();
            pos = end + 1;

            if (line.Length == 0 || line.StartsWith("#")) continue;

            foreach (string part in line.Split(','))
            {
                string[] kv = part.Split('=');
                if (kv.Length != 2) continue;
                string key = kv[0].Trim().ToLowerInvariant();
                string val = kv[1].Trim();
                if (key == "x") width = int.Parse(val, CultureInfo.InvariantCulture);
                else if (key == "y") height = int.Parse(val, CultureInfo.InvariantCulture);
                else if (key == "rule" && !IsConway(val))
                    UnityEngine.Debug.LogWarning($"RLE rule is '{val}', but this engine only runs B3/S23.");
            }
            break;
        }

        if (width <= 0 || height <= 0)
            throw new FormatException("RLE header is missing 'x = ..., y = ...'.");

        var cells = new byte[width * height];
        int x = 0, y = 0, run = 0;

        for (; pos < text.Length; pos++)
        {
            char ch = text[pos];

            if (ch >= '0' && ch <= '9') { run = run * 10 + (ch - '0'); continue; }
            if (char.IsWhiteSpace(ch)) continue;
            if (ch == '!') break;

            int n = run == 0 ? 1 : run;
            run = 0;

            if (ch == 'b' || ch == '.')
            {
                x += n;
            }
            else if (ch == '$')
            {
                y += n;
                x = 0;
            }
            else // 'o' or any other state letter counts as alive
            {
                if (y < height)
                {
                    int row = (height - 1 - y) * width;   // flip: RLE row 0 is the top
                    for (int k = 0; k < n; k++)
                        if (x + k < width) cells[row + x + k] = 255;
                }
                x += n;
            }
        }

        return cells;
    }

    static bool IsConway(string rule)
    {
        string r = rule.Replace(" ", "").ToUpperInvariant();
        return r == "B3/S23" || r == "23/3" || r == "S23/B3";
    }
}
