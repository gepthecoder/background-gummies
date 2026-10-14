using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A colour test card on every screen (chat "test colours"): exact colours, each square fully opaque, with see-through
/// gaps between them, so a screenshot shows what Windows really puts on the screen for them (are they washed out? does
/// what's behind show through?). Every card pixel is 4 x 4 screen pixels, snapped to the pixel grid. Gone after 40 s.
/// Rows: a title bar (black, white text), greys 0..255, red / green / blue / the apple's red / the dino's green / yellow /
/// white, then white at 25 / 50 / 75 % and black at 50 % (the only see-through ones).
/// </summary>
public static class ColorTest
{
    public const float Seconds = 40f;
    const int Px = 4, Cell = 10, Gap = 4;
    static readonly List<GameObject> s_cards = new List<GameObject>();

    public static bool Showing { get { s_cards.RemoveAll(c => !c); return s_cards.Count > 0; } }

    public static void Show(Material baseMat)
    {
        Hide();
        var cam = Camera.main;
        float ppu = ScreenBounds.PixelsPerUnit;
        if (!cam || ppu <= 0f) return;
        foreach (var s in ScreenBounds.Screens)
        {
            Color32[] px = Bake(s.name, out int w, out int h);
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = "ColorTest" };
            tex.SetPixels32(px);
            tex.Apply(false, false);
            var sp = Sprite.Create(tex, new Rect(0, 0, w, h), Vector2.zero, 1f, 0, SpriteMeshType.FullRect);
            var go = new GameObject("Colour Test Card");
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sp;
            sr.sortingOrder = 400;
            sr.enabled = true;
            if (baseMat) sr.sharedMaterial = SpriteMats.For(baseMat, tex);
            float unit = Px / ppu; // (world units per card pixel)
            // the middle of the screen, snapped so every card pixel covers whole screen pixels
            float left = cam.transform.position.x - cam.orthographicSize * cam.aspect, bottom = cam.transform.position.y - cam.orthographicSize;
            float x = s.full.center.x - w * unit * 0.5f, y = s.full.center.y - h * unit * 0.5f;
            x = left + Mathf.Round((x - left) * ppu) / ppu;
            y = bottom + Mathf.Round((y - bottom) * ppu) / ppu;
            go.transform.position = new Vector3(x, y, 0f);
            go.transform.localScale = new Vector3(unit, unit, 1f);
            go.AddComponent<ColorTestCard>().until = Time.unscaledTime + Seconds;
            s_cards.Add(go);
        }
    }

    public static void Hide()
    {
        foreach (var c in s_cards) if (c) Object.Destroy(c);
        s_cards.Clear();
    }

    // the card's pixels (y up, like the texture)
    static Color32[] Bake(string screen, out int w, out int h)
    {
        var rows = new List<Color32[]>
        {
            new[] { G(0), G(32), G(64), G(96), G(128), G(160), G(192), G(224), G(255) },
            new[] { C(255, 0, 0), C(0, 255, 0), C(0, 0, 255), C(230, 56, 54), C(92, 194, 77), C(255, 222, 110), G(255) },
            new[] { new Color32(255, 255, 255, 64), new Color32(255, 255, 255, 128), new Color32(255, 255, 255, 191), new Color32(0, 0, 0, 128) },
        };
        int cols = 9, titleH = PixelFont.GlyphH + 4;
        w = Gap + cols * (Cell + Gap);
        h = titleH + rows.Count * (Cell + Gap) + Gap;
        var px = new Color32[w * h];
        // the title bar: black, white text
        for (int y = h - titleH; y < h; y++) for (int x = 0; x < w; x++) px[y * w + x] = new Color32(0, 0, 0, 255);
        string name = string.IsNullOrEmpty(screen) ? "" : screen.Replace("\\\\.\\", "").ToUpperInvariant();
        string title = "WHIPIT COLOUR TEST " + name;
        while (title.Length > 4 && PixelFont.Measure(title) > w - 4) title = title.Substring(0, title.Length - 1);
        PixelFont.Draw(px, w, h, title, 2, h - 3, new Color32(255, 255, 255, 255));
        // the squares
        for (int r = 0; r < rows.Count; r++)
        {
            int y0 = h - titleH - Gap - (r + 1) * Cell - r * Gap;
            for (int c = 0; c < rows[r].Length; c++)
            {
                int x0 = Gap + c * (Cell + Gap);
                for (int y = y0; y < y0 + Cell; y++) for (int x = x0; x < x0 + Cell; x++) px[y * w + x] = rows[r][c];
            }
        }
        return px;
    }

    static Color32 G(int v) => new Color32((byte)v, (byte)v, (byte)v, 255);
    static Color32 C(int r, int g, int b) => new Color32((byte)r, (byte)g, (byte)b, 255);
}

/// <summary>Takes its card away after a while.</summary>
public class ColorTestCard : MonoBehaviour
{
    public float until;
    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("ColorTestCard.Update", t0); }
    void UpdateTimed() { if (Time.unscaledTime >= until) Destroy(gameObject); }
}
