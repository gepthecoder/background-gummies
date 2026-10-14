using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Tiny 5 px high pixel font (capitals, digits, a little punctuation, a heart) for speech bubbles; most glyphs are
/// 3 px wide, M, W and the heart 5. Text is drawn into Color32 pixel buffers; lower-case is shown as capitals.
/// </summary>
public static class PixelFont
{
    public const int GlyphH = 5, SpaceAdvance = 3, LineHeight = 7;

    // rows top to bottom, '#' = ink
    static readonly Dictionary<char, string> Glyphs = new Dictionary<char, string>
    {
        { 'A', ".#.,#.#,###,#.#,#.#" }, { 'B', "##.,#.#,##.,#.#,##." }, { 'C', ".##,#..,#..,#..,.##" }, { 'D', "##.,#.#,#.#,#.#,##." },
        { 'E', "###,#..,##.,#..,###" }, { 'F', "###,#..,##.,#..,#.." }, { 'G', ".##,#..,#.#,#.#,.##" }, { 'H', "#.#,#.#,###,#.#,#.#" },
        { 'I', "###,.#.,.#.,.#.,###" }, { 'J', "..#,..#,..#,#.#,.#." }, { 'K', "#.#,#.#,##.,#.#,#.#" }, { 'L', "#..,#..,#..,#..,###" },
        { 'M', "#...#,##.##,#.#.#,#...#,#...#" }, { 'N', "###,#.#,#.#,#.#,#.#" }, { 'O', ".#.,#.#,#.#,#.#,.#." }, { 'P', "##.,#.#,##.,#..,#.." },
        { 'Q', ".#.,#.#,#.#,##.,.##" }, { 'R', "##.,#.#,##.,#.#,#.#" }, { 'S', ".##,#..,.#.,..#,##." }, { 'T', "###,.#.,.#.,.#.,.#." },
        { 'U', "#.#,#.#,#.#,#.#,###" }, { 'V', "#.#,#.#,#.#,#.#,.#." }, { 'W', "#...#,#...#,#.#.#,##.##,#...#" }, { 'X', "#.#,#.#,.#.,#.#,#.#" },
        { 'Y', "#.#,#.#,.#.,.#.,.#." }, { 'Z', "###,..#,.#.,#..,###" },
        { '0', "###,#.#,#.#,#.#,###" }, { '1', ".#.,##.,.#.,.#.,###" }, { '2', "###,..#,###,#..,###" }, { '3', "###,..#,.##,..#,###" },
        { '4', "#.#,#.#,###,..#,..#" }, { '5', "###,#..,###,..#,###" }, { '6', "###,#..,###,#.#,###" }, { '7', "###,..#,.#.,.#.,.#." },
        { '8', "###,#.#,###,#.#,###" }, { '9', "###,#.#,###,..#,###" },
        { '.', "...,...,...,...,.#." }, { ',', "...,...,...,.#.,#.." }, { '!', ".#.,.#.,.#.,...,.#." }, { '?', "##.,..#,.#.,...,.#." },
        { '\'', ".#.,.#.,...,...,..." }, { '-', "...,...,###,...,..." }, { '+', "...,.#.,###,.#.,..." }, { ':', "...,.#.,...,.#.,..." },
        { '(', ".#.,#..,#..,#..,.#." }, { ')', ".#.,..#,..#,..#,.#." }, { '@', ".#.,#.#,##.,#..,.##" }, { '_', "...,...,...,...,###" },
        { '/', "..#,..#,.#.,#..,#.." }, { '*', "...,#.#,.#.,#.#,..." }, { '<', "..#,.#.,#..,.#.,..#" }, { '>', "#..,.#.,..#,.#.,#.." },
        { '~', "...,.#.,#.#,...,..." }, { '=', "...,###,...,###,..." }, { '\u2665', ".#.#.,#####,#####,.###.,..#.." }, { '\u266A', ".##,.#.,.#.,##.,##." }, // heart, music note
    };

    struct Glyph { public int w; public bool[] bits; }
    static readonly Dictionary<char, Glyph> s_glyphs = new Dictionary<char, Glyph>();

    static bool Get(char c, out Glyph g)
    {
        if (s_glyphs.TryGetValue(c, out g)) return true;
        if (!Glyphs.TryGetValue(c, out string rows)) return false;
        string[] r = rows.Split(',');
        g = new Glyph { w = r[0].Length, bits = new bool[r[0].Length * GlyphH] };
        for (int y = 0; y < GlyphH; y++)
            for (int x = 0; x < g.w; x++)
                g.bits[y * g.w + x] = r[y][x] == '#';
        s_glyphs[c] = g;
        return true;
    }

    static Glyph For(char c) { Get(Norm(c), out Glyph g); if (g.bits == null) Get('?', out g); return g; }

    static char Norm(char c) => c == '\u2665' ? c : char.ToUpperInvariant(c);

    /// <summary>Width in pixels of one line of text.</summary>
    public static int Measure(string line)
    {
        int w = 0;
        for (int i = 0; i < line.Length; i++) w += line[i] == ' ' ? SpaceAdvance : For(line[i]).w + 1;
        return Mathf.Max(0, w - 1);
    }

    /// <summary>Split into lines of at most maxChars, at spaces where possible.</summary>
    public static List<string> Wrap(string text, int maxChars)
    {
        var lines = new List<string>();
        foreach (string para in text.Split('\n'))
        {
            string line = "";
            foreach (string word in para.Split(' '))
            {
                if (line.Length == 0) line = word;
                else if (line.Length + 1 + word.Length <= maxChars) line += " " + word;
                else { lines.Add(line); line = word; }
                while (line.Length > maxChars) { lines.Add(line.Substring(0, maxChars)); line = line.Substring(maxChars); }
            }
            lines.Add(line);
        }
        return lines;
    }

    /// <summary>Draw one line with its top-left glyph pixel at (x, yTop) into a w x h buffer (y up, row 0 = bottom).</summary>
    public static void Draw(Color32[] px, int w, int h, string line, int x, int yTop, Color32 ink)
    {
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == ' ') { x += SpaceAdvance; continue; }
            Glyph g = For(line[i]);
            for (int gy = 0; gy < GlyphH; gy++)
                for (int gx = 0; gx < g.w; gx++)
                {
                    if (!g.bits[gy * g.w + gx]) continue;
                    int px0 = x + gx, py0 = yTop - gy;
                    if (px0 >= 0 && px0 < w && py0 >= 0 && py0 < h) px[py0 * w + px0] = ink;
                }
            x += g.w + 1;
        }
    }
}
