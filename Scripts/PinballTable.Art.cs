using System.Collections.Generic;
using UnityEngine;

// Desktop pinball: how the table looks. All pixel art baked at start: the rails, walls, top corners, the one-way gate,
// the shelf of the stands and the arrows up the lane in one picture; the flippers, pop bumpers, icon rings, the bin
// (its inside, its front and its lid), the plunger and the dot-matrix scoreboard as their own sprites. Under the ball:
// the table (0) and its parts (1); over every buddy: the bin's front and lid (95, 96), the scoreboard (90), puffs (97)
// and the score pop-ups (240).
public partial class PinballTable
{
    Transform root;
    readonly List<Object> made = new List<Object>();
    SpriteRenderer staticSr, sbSr, xSr, binBackSr, binFrontSr, lidSr, knobSr, coilSr;
    readonly SpriteRenderer[] flipSr = new SpriteRenderer[2];
    readonly List<SpriteRenderer> popSrs = new List<SpriteRenderer>();
    readonly List<Sprite> popOff = new List<Sprite>(), popOn = new List<Sprite>();
    readonly List<SpriteRenderer> ringSrs = new List<SpriteRenderer>();
    Sprite ringOffSp, ringOnSp, dotSp, whiteSp;
    Vector2 staticOrigin;
    float coilPx, rimPx, lidAng, lidVel;
    Texture2D sbTex;
    Color32[] dmdBuf;
    string sbKey = "";
    int sbHash;
    int dmdPitch;
    const int DmdW = 120, DmdH = 24;
    const int OrderTable = 0, OrderParts = 1, OrderBoard = 90, OrderBinFront = 95, OrderLid = 96, OrderFx = 97, OrderPopup = 240;

    static readonly Color32 White = new Color32(255, 255, 255, 255), Black = new Color32(0, 0, 0, 255);
    static readonly Color32 Ink = new Color32(64, 22, 58, 255);

    void MakeArt()
    {
        DropArt();
        root = new GameObject("Pinball Table").transform;
        // the table: rails, walls, corners, the gate, the shelf, the lane's arrows
        staticSr = Sr("Pinball Rails", BakeStatic(), OrderTable);
        Place(staticSr, staticOrigin, tp);
        for (int f = 0; f < 2; f++) flipSr[f] = Sr(f == 0 ? "Pinball Flipper L" : "Pinball Flipper R", BakeFlipper(f == 1), OrderParts);
        popSrs.Clear(); popOff.Clear(); popOn.Clear();
        foreach (var pb in pops)
        {
            popOff.Add(BakePop(pb.col, false));
            popOn.Add(BakePop(pb.col, true));
            popSrs.Add(Sr("Pinball Pop Bumper", popOff[popOff.Count - 1], OrderParts));
        }
        // the bin
        rimPx = Mathf.Max(6f, binR.height / tp * 0.32f);
        binBackSr = Sr("Pinball Bin Inside", BakeBin(0), OrderParts);
        binFrontSr = Sr("Pinball Bin", BakeBin(1), OrderBinFront);
        lidSr = Sr("Pinball Bin Lid", BakeBin(2), OrderLid);
        // the plunger
        knobSr = Sr("Pinball Plunger", BakeKnob(), OrderParts);
        coilSr = Sr("Pinball Spring", BakeCoil(), OrderParts);
        // the scoreboard and its X
        int p = dmdPitch, w = DmdW * p + 4 * p, h = DmdH * p + 4 * p;
        sbTex = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = "Pinball Scoreboard" };
        made.Add(sbTex);
        var sbSp = Sprite.Create(sbTex, new Rect(0, 0, w, h), Vector2.zero, 1f, 0, SpriteMeshType.FullRect);
        made.Add(sbSp);
        sbSr = Sr("Pinball Scoreboard", sbSp, OrderBoard);
        dmdBuf = new Color32[DmdW * DmdH];
        sbKey = "";
        xSr = Sr("Pinball Close", BakeX(), OrderBoard + 1);
        // shared bits
        ringOffSp = BakeRing(false);
        ringOnSp = BakeRing(true);
        dotSp = BakeDot();
        var wc = new InfestedWindow.Canvas(4, 4);
        for (int i = 0; i < wc.px.Length; i++) wc.px[i] = White;
        whiteSp = MakeSprite(wc, new Vector2(0.5f, 0.5f), "Pinball White", false);
        MakeRings();
        lidAng = 0f; lidVel = 0f;
        Animate(0f, Time.unscaledTime);
    }

    void DropArt()
    {
        if (root) Destroy(root.gameObject);
        root = null;
        foreach (var o in made) if (o) Destroy(o);
        made.Clear();
        popups.Clear(); dots.Clear(); flashes.Clear();
        popSrs.Clear(); ringSrs.Clear();
        staticSr = sbSr = xSr = binBackSr = binFrontSr = lidSr = knobSr = coilSr = null;
        flipSr[0] = flipSr[1] = null;
    }

    void MakeRings()
    {
        if (!root) return;
        foreach (var sr in ringSrs) if (sr) Destroy(sr.gameObject);
        ringSrs.Clear();
        foreach (var t in targets) ringSrs.Add(Sr("Pinball Target", ringOffSp, OrderParts));
    }

    SpriteRenderer Sr(string name, Sprite sp, int order)
    {
        var go = new GameObject(name);
        go.transform.SetParent(root, false);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sp;
        sr.sortingOrder = order;
        sr.color = Color.white;
        sr.enabled = true;
        if (material && sp) sr.sharedMaterial = SpriteMats.For(material, sp.texture);
        return sr;
    }

    Sprite MakeSprite(InfestedWindow.Canvas cv, Vector2 pivot, string name, bool smooth)
    {
        var tex = new Texture2D(cv.w, cv.h, TextureFormat.RGBA32, false) { filterMode = smooth ? FilterMode.Bilinear : FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = name };
        tex.SetPixels32(cv.px);
        tex.Apply(false, false);
        made.Add(tex);
        var sp = Sprite.Create(tex, new Rect(0, 0, cv.w, cv.h), pivot, 1f, 0, SpriteMeshType.FullRect);
        made.Add(sp);
        return sp;
    }

    static void Place(SpriteRenderer sr, Vector2 at, float scale, float rot = 0f, float sy = -1f)
    {
        if (!sr) return;
        var t = sr.transform;
        t.position = new Vector3(at.x, at.y, 0f);
        t.rotation = Quaternion.Euler(0f, 0f, rot);
        t.localScale = new Vector3(scale, sy < 0f ? scale : sy, 1f);
    }

    static Color32 Mix(Color32 a, Color32 b, float t) => new Color32((byte)(a.r + (b.r - a.r) * t), (byte)(a.g + (b.g - a.g) * t), (byte)(a.b + (b.b - a.b) * t), (byte)(a.a + (b.a - a.a) * t));
    static Color32 C(int r, int g, int b, int a = 255) => new Color32((byte)r, (byte)g, (byte)b, (byte)a);

    // ------------------------------------------------------------------ the table picture

    // world -> (x, row from the top) in the table picture
    Vector2 Cv(Vector2 w) => new Vector2((w.x - W.xMin) / tp, (W.yMax - w.y) / tp);

    Sprite BakeStatic()
    {
        int w = Mathf.CeilToInt(W.width / tp) + 2, h = Mathf.CeilToInt(W.height / tp) + 2;
        var cv = new InfestedWindow.Canvas(w, h);
        staticOrigin = new Vector2(W.xMin, W.yMax - h * tp);
        Shelf(cv);
        // little arrows up the lane
        float lx = laneX + laneW * 0.5f;
        for (int k = 0; k < 3; k++)
        {
            Vector2 tip = new Vector2(lx, plungerRest + 0.75f + 0.42f * k);
            Color32 c = C(255, 212, 92, 150 + 35 * k);
            Line(cv, tip, tip + new Vector2(-0.16f, -0.16f), 0.035f, c);
            Line(cv, tip, tip + new Vector2(0.16f, -0.16f), 0.035f, c);
        }
        var styles = new[] { (C(255, 112, 164), C(255, 246, 250)), (C(98, 210, 172), C(246, 255, 250)), (C(186, 140, 255), C(250, 246, 255)), (C(255, 206, 76), C(255, 252, 238)) };
        foreach (var sg in segs)
        {
            if (sg.kind == KPlunger) continue;
            var st = styles[Mathf.Clamp(sg.style, 0, styles.Length - 1)];
            Stick(cv, sg.a, sg.b, sg.r, st.Item1, st.Item2);
        }
        return MakeSprite(cv, Vector2.zero, "Pinball Table", false);
    }

    // a candy stick: diagonal stripes, light on top, darker underneath, an outline
    void Stick(InfestedWindow.Canvas cv, Vector2 a, Vector2 b, float r, Color32 c1, Color32 c2)
    {
        Vector2 A = Cv(a), B = Cv(b);
        float R = r / tp, len = (B - A).magnitude;
        if (len < 1e-3f) return;
        Vector2 u = (B - A) / len, nrm = new Vector2(-u.y, u.x);
        if (nrm.y > 0f) nrm = -nrm; // (rows go down: this one points up the screen)
        Color32 ink = Mix(c1, Ink, 0.75f);
        float stripe = Mathf.Max(3f, R * 1.3f);
        int x0 = Mathf.FloorToInt(Mathf.Min(A.x, B.x) - R - 1f), x1 = Mathf.CeilToInt(Mathf.Max(A.x, B.x) + R + 1f);
        int y0 = Mathf.FloorToInt(Mathf.Min(A.y, B.y) - R - 1f), y1 = Mathf.CeilToInt(Mathf.Max(A.y, B.y) + R + 1f);
        for (int row = Mathf.Max(0, y0); row <= Mathf.Min(cv.h - 1, y1); row++)
            for (int x = Mathf.Max(0, x0); x <= Mathf.Min(cv.w - 1, x1); x++)
            {
                var P = new Vector2(x + 0.5f, row + 0.5f);
                float t = Mathf.Clamp(Vector2.Dot(P - A, u), 0f, len);
                Vector2 d = P - (A + u * t);
                float dist = d.magnitude;
                if (dist > R + 0.5f) continue;
                Color32 col;
                if (dist > R - 1.1f) col = ink;
                else
                {
                    float up = Vector2.Dot(d, nrm);
                    int band = Mathf.FloorToInt((t + up * 0.9f) / stripe);
                    col = (band % 2 + 2) % 2 == 0 ? c1 : c2;
                    float side = up / Mathf.Max(R, 1f);
                    if (side > 0.4f) col = Mix(col, White, 0.45f);
                    else if (side < -0.45f) col = Mix(col, Black, 0.16f);
                }
                cv.Set(x, row, col);
            }
    }

    void Line(InfestedWindow.Canvas cv, Vector2 a, Vector2 b, float r, Color32 c)
    {
        Vector2 A = Cv(a), B = Cv(b);
        float R = Mathf.Max(0.8f, r / tp), len = Mathf.Max((B - A).magnitude, 1e-3f);
        Vector2 u = (B - A) / len;
        for (int row = Mathf.FloorToInt(Mathf.Min(A.y, B.y) - R - 1f); row <= Mathf.CeilToInt(Mathf.Max(A.y, B.y) + R + 1f); row++)
            for (int x = Mathf.FloorToInt(Mathf.Min(A.x, B.x) - R - 1f); x <= Mathf.CeilToInt(Mathf.Max(A.x, B.x) + R + 1f); x++)
            {
                var P = new Vector2(x + 0.5f, row + 0.5f);
                float t = Mathf.Clamp(Vector2.Dot(P - A, u), 0f, len);
                if ((P - (A + u * t)).magnitude <= R) cv.Set(x, row, c);
            }
    }

    // the stands' shelf: a wafer right across, with icing drips under it
    void Shelf(InfestedWindow.Canvas cv)
    {
        int top = Mathf.RoundToInt((W.yMax - (ledgeY + shelfT)) / tp), bot = Mathf.RoundToInt((W.yMax - ledgeY) / tp);
        Color32 pink = C(255, 150, 192), cream = C(255, 236, 214), ink = Mix(pink, Ink, 0.7f);
        for (int row = top; row <= bot; row++)
            for (int x = 0; x < cv.w; x++)
            {
                Color32 c = (x / 5) % 2 == 0 ? pink : cream;
                if (row <= top + 1) c = Mix(c, White, 0.5f);
                if (row == top || row == bot) c = ink;
                cv.Set(x, row, c);
            }
        // drips
        int rr = Mathf.Max(2, (bot - top) / 3);
        for (int x0 = 6; x0 < cv.w - 6; x0 += 13 + (x0 * 7) % 9)
        {
            int len = rr + (x0 * 13) % (rr + 2);
            for (int row = bot + 1; row <= bot + len; row++)
                for (int x = x0 - rr; x <= x0 + rr; x++)
                {
                    float dx = x - x0, dy = row - (bot + len - rr);
                    bool inside = row <= bot + len - rr ? Mathf.Abs(dx) <= rr * (1f - 0.3f * (row - bot) / (float)len) : dx * dx + dy * dy <= rr * rr;
                    if (!inside) continue;
                    bool edge = Mathf.Abs(dx) > rr * (1f - 0.3f * (row - bot) / (float)len) - 1f || (row > bot + len - rr && dx * dx + dy * dy > (rr - 1) * (rr - 1));
                    cv.Set(x, row, edge ? ink : dx < 0 ? Mix(pink, White, 0.35f) : pink);
                }
        }
    }

    // ------------------------------------------------------------------ the parts

    // a candy flipper: a tapered jelly stick, light along the top, a white candy button on its pivot
    Sprite BakeFlipper(bool right)
    {
        float L = flipLen / tp, R0 = r0 / tp, R1 = r1 / tp;
        const int pad = 2;
        int w = Mathf.CeilToInt(L + R0 + R1) + 2 * pad, h = Mathf.CeilToInt(2f * R0) + 2 * pad;
        float cx0 = pad + R0, cy = h * 0.5f;
        var cv = new InfestedWindow.Canvas(w, h);
        Color32 ink = C(110, 20, 46), body = C(238, 68, 98), hi = C(255, 132, 150), lo = C(196, 44, 74), spec = C(255, 226, 232), cream = C(255, 246, 236), dot = C(255, 118, 160);
        for (int row = 0; row < h; row++)
            for (int x = 0; x < w; x++)
            {
                float px = x + 0.5f, py = h - row - 0.5f;
                if (right) px = w - px;
                float t = Mathf.Clamp(px - cx0, 0f, L), rr = Mathf.Lerp(R0, R1, t / L);
                var d = new Vector2(px - (cx0 + t), py - cy);
                float dist = d.magnitude;
                if (dist > rr + 0.5f) continue;
                Color32 col;
                if (dist > rr - 1.2f) col = ink;
                else
                {
                    float up = d.y / rr;
                    col = up > 0.35f ? hi : up < -0.45f ? lo : body;
                    if (up > 0.5f && up < 0.78f && t > R0 && t < L - R1) col = spec;
                }
                float pd = new Vector2(px - cx0, py - cy).magnitude, br = R0 * 0.62f;
                if (pd < br) col = pd > br - 1.2f ? ink : pd < R0 * 0.27f ? dot : cream;
                cv.Set(x, row, col);
            }
        return MakeSprite(cv, new Vector2((right ? w - cx0 : cx0) / w, 0.5f), right ? "Pinball Flipper R" : "Pinball Flipper L", true);
    }

    // a jelly pop bumper: a plum skirt, a glossy jelly cap, a white star on top (lit: the cap glows)
    Sprite BakePop(Color32 col, bool lit)
    {
        float R = popR / tp;
        int D = Mathf.CeilToInt(2f * R) + 4;
        float c = D * 0.5f;
        var cv = new InfestedWindow.Canvas(D, D);
        Color32 skirt = C(120, 60, 132), skirtHi = C(176, 112, 186), cap = lit ? Mix(col, White, 0.5f) : col, star = lit ? C(255, 255, 210) : C(255, 252, 244);
        for (int row = 0; row < D; row++)
            for (int x = 0; x < D; x++)
            {
                float dx = x + 0.5f - c, dy = (D - row - 0.5f) - c, d = Mathf.Sqrt(dx * dx + dy * dy);
                if (d > R + 0.5f) continue;
                Color32 k;
                if (d > R - 1.2f) k = Ink;
                else if (d > R * 0.8f) k = dy > 0f ? skirtHi : skirt;
                else if (d > R * 0.8f - 1.2f) k = Mix(col, Ink, 0.6f);
                else
                {
                    float v = dy / (R * 0.8f);
                    k = v > 0.35f ? Mix(cap, White, 0.3f) : v < -0.4f ? Mix(cap, Black, 0.15f) : cap;
                    float gx = (dx + R * 0.3f) / (R * 0.22f), gy = (dy - R * 0.38f) / (R * 0.13f);
                    if (gx * gx + gy * gy < 1f) k = Mix(k, White, 0.75f);
                    float a = Mathf.Atan2(dy, dx), rs = R * 0.16f + R * 0.17f * Mathf.Max(0f, Mathf.Cos(5f * (a - 0.5f * Mathf.PI)));
                    if (d < rs) k = d > rs - 1f ? Mix(star, cap, 0.4f) : star;
                }
                cv.Set(x, row, k);
            }
        return MakeSprite(cv, new Vector2(0.5f, 0.5f), lit ? "Pinball Pop Lit" : "Pinball Pop", true);
    }

    // the ring round an icon: dotted and faint until the ball rolls over it, then gold and sparkling
    Sprite BakeRing(bool lit)
    {
        const int S = 64;
        const float c = 32f;
        var cv = new InfestedWindow.Canvas(S, S);
        for (int row = 0; row < S; row++)
            for (int x = 0; x < S; x++)
            {
                float dx = x + 0.5f - c, dy = (S - row - 0.5f) - c, d = Mathf.Sqrt(dx * dx + dy * dy);
                if (!lit)
                {
                    if (d >= 27f && d <= 29f) cv.Set(x, row, C(255, 255, 255, 120));
                    for (int k = 0; k < 8; k++)
                    {
                        float a = k * Mathf.PI / 4f, qx = dx - 28f * Mathf.Cos(a), qy = dy - 28f * Mathf.Sin(a);
                        if (qx * qx + qy * qy <= 5.5f) cv.Set(x, row, C(255, 236, 248, 230));
                    }
                }
                else
                {
                    if (d >= 29.5f && d <= 31.5f) cv.Set(x, row, C(255, 222, 120, (int)(200 * (31.5f - d) / 2f)));
                    if (d >= 25f && d < 29.5f) cv.Set(x, row, d > 26.5f && d < 27.8f ? C(255, 252, 226) : C(255, 200, 64));
                    if (d >= 24f && d < 25f) cv.Set(x, row, C(196, 120, 20));
                }
            }
        if (lit)
            for (int k = 0; k < 4; k++)
            {
                float a = Mathf.PI / 4f + k * Mathf.PI / 2f;
                int sx = Mathf.RoundToInt(c + 26f * Mathf.Cos(a)), sy = Mathf.RoundToInt(c - 26f * Mathf.Sin(a));
                for (int i = -3; i <= 3; i++) { cv.Set(sx + i, sy, White); cv.Set(sx, sy + i, White); }
            }
        return MakeSprite(cv, new Vector2(0.5f, 0.5f), lit ? "Pinball Ring Lit" : "Pinball Ring", true);
    }

    Sprite BakeDot()
    {
        var cv = new InfestedWindow.Canvas(8, 8);
        for (int row = 0; row < 8; row++)
            for (int x = 0; x < 8; x++)
            {
                float dx = x + 0.5f - 4f, dy = row + 0.5f - 4f, d = Mathf.Sqrt(dx * dx + dy * dy);
                if (d < 3.6f) cv.Set(x, row, C(255, 255, 255, d < 2.4f ? 255 : 150));
            }
        return MakeSprite(cv, new Vector2(0.5f, 0.5f), "Pinball Puff", true);
    }

    // the bin: 0 = its inside (the dark opening and the back of the rim), 1 = its front (the can, the front of the rim,
    // the recycling arrows), 2 = the lid (hinged on the left)
    Sprite BakeBin(int part)
    {
        float bw = binR.width / tp, bh = binR.height / tp, ry = rimPx * 0.5f;
        Color32 mint = C(104, 200, 166), rib = C(82, 172, 140), ink = C(28, 92, 72), rim = C(156, 230, 200), dark = C(30, 58, 50), arrow = C(242, 255, 248);
        if (part == 2)
        {
            float domeH = bh * 0.26f;
            int w = Mathf.CeilToInt(bw) + 4, h = Mathf.CeilToInt(rimPx + domeH + 6f);
            var cv = new InfestedWindow.Canvas(w, h);
            float cx = w * 0.5f, rx = w * 0.5f - 1f;
            for (int row = 0; row < h; row++)
                for (int x = 0; x < w; x++)
                {
                    float px = x + 0.5f - cx, py = h - row - 0.5f; // (y up from the lid's bottom)
                    float e = (px * px) / (rx * rx) + ((py - ry) * (py - ry)) / (ry * ry);
                    float de = (px * px) / ((rx - 2f) * (rx - 2f)) + ((py - ry) * (py - ry)) / (domeH * domeH);
                    bool band = e <= 1f, dome = py >= ry && de <= 1f;
                    bool handle = Mathf.Abs(px) < rx * 0.18f && py >= ry + domeH - 1f && py < ry + domeH + 4f;
                    if (!band && !dome && !handle) continue;
                    Color32 k = handle ? (Mathf.Abs(px) > rx * 0.18f - 1.2f || py > ry + domeH + 3f ? ink : rim) : dome && !band ? (de > 0.86f ? ink : py > ry + domeH * 0.55f ? Mix(rim, White, 0.3f) : C(124, 214, 180)) : (e > 0.8f ? ink : rim);
                    cv.Set(x, row, k);
                }
            return MakeSprite(cv, new Vector2(1f / w, ry / h), "Pinball Bin Lid", true);
        }
        {
            int w = Mathf.CeilToInt(bw) + 2, h = Mathf.CeilToInt(bh) + 1;
            var cv = new InfestedWindow.Canvas(w, h);
            float cx = w * 0.5f, rx = bw * 0.5f, cyRim = bh - ry, bodyTop = cyRim;
            float ar = Mathf.Min(bw, bh) * 0.2f, acx = cx, acy = bodyTop * 0.48f;
            for (int row = 0; row < h; row++)
                for (int x = 0; x < w; x++)
                {
                    float px = x + 0.5f - cx, py = h - row - 0.5f;
                    float e = (px * px) / (rx * rx) + ((py - cyRim) * (py - cyRim)) / (ry * ry);
                    float ei = (px * px) / ((rx - 2.5f) * (rx - 2.5f)) + ((py - cyRim) * (py - cyRim)) / ((ry - 1.5f) * (ry - 1.5f));
                    if (part == 0)
                    {
                        if (e > 1f) continue;
                        cv.Set(x, row, ei <= 1f ? (py > cyRim ? Mix(dark, Black, 0.2f) : dark) : py >= cyRim ? (e > 0.8f ? ink : rim) : ink);
                        continue;
                    }
                    // the front: the can below the rim, then the front half of the rim
                    Color32 k = default;
                    bool any = false;
                    if (py <= bodyTop)
                    {
                        float hw = Mathf.Lerp(bw * 0.42f, rx - 1f, py / Mathf.Max(bodyTop, 1f));
                        if (Mathf.Abs(px) <= hw)
                        {
                            any = true;
                            k = Mathf.Abs(px) > hw - 1.2f || py < 1.2f ? ink : py < 3f ? rib : mint;
                            float ribW = bw / 6f;
                            if (k.r == mint.r && Mathf.Abs(Mathf.Repeat(px + ribW * 0.5f, ribW) - ribW * 0.5f) < 0.7f && Mathf.Abs(px) < hw - ribW * 0.5f) k = rib;
                            if (k.r == mint.r && px < -hw * 0.55f) k = Mix(mint, White, 0.18f);
                            // the recycling arrows: three bent arrows chasing each other round
                            float ax = px - (acx - cx), ay = py - acy, ad = Mathf.Sqrt(ax * ax + ay * ay), aa = Mathf.Repeat(Mathf.Atan2(ay, ax) * Mathf.Rad2Deg + 90f, 120f);
                            if (Mathf.Abs(ad - ar) < Mathf.Max(1.2f, ar * 0.16f) && aa > 12f && aa < 86f) k = arrow;
                            if (aa >= 86f && aa < 108f && ad > ar - ar * 0.38f * (108f - aa) / 22f && ad < ar + ar * 0.38f * (108f - aa) / 22f) k = arrow;
                        }
                    }
                    if (py < cyRim && e <= 1f) { any = true; k = ei > 1f ? (e > 0.8f ? ink : rim) : k.a == 0 ? default : k; if (ei <= 1f && py > bodyTop - 0.5f) any = false; }
                    if (any && k.a > 0) cv.Set(x, row, k);
                }
            return MakeSprite(cv, Vector2.zero, part == 0 ? "Pinball Bin Inside" : "Pinball Bin", false);
        }
    }

    Sprite BakeKnob()
    {
        int w = Mathf.CeilToInt(laneW * 0.72f / tp), h = Mathf.CeilToInt(0.16f / tp);
        var cv = new InfestedWindow.Canvas(w, h);
        int r = Mathf.Max(2, h / 2);
        cv.RoundRect(0, 0, w, h, r, C(110, 20, 46));
        cv.RoundRect(1, 1, w - 1, h - 1, r - 1, C(232, 62, 88));
        for (int x = r; x < w - r; x++) cv.Set(x, 2, C(255, 160, 172));
        return MakeSprite(cv, new Vector2(0.5f, 0.5f), "Pinball Plunger", false);
    }

    Sprite BakeCoil()
    {
        coilPx = Mathf.Max(8f, (plungerRest - 0.08f - W.yMin) / tp);
        int w = Mathf.CeilToInt(laneW * 0.46f / tp), h = Mathf.CeilToInt(coilPx);
        var cv = new InfestedWindow.Canvas(w, h);
        const int turns = 6;
        for (int row = 0; row < h; row++)
        {
            float v = (h - row - 0.5f) / h * turns, f = v - Mathf.Floor(v);
            float x = f < 0.5f ? f * 2f : 2f - f * 2f;
            int cx = Mathf.RoundToInt(1 + x * (w - 3));
            for (int k = -1; k <= 1; k++) cv.Set(cx + k, row, k == 0 ? C(250, 226, 240) : C(150, 110, 150));
        }
        return MakeSprite(cv, new Vector2(0.5f, 0f), "Pinball Spring", false);
    }

    Sprite BakeX()
    {
        int d = Mathf.Max(9, Mathf.RoundToInt(xRect.width / screenPx));
        var cv = new InfestedWindow.Canvas(d, d);
        float c = d * 0.5f, r = c - 0.5f;
        cv.Disc(c - 0.5f, c - 0.5f, r, Ink);
        cv.Disc(c - 0.5f, c - 0.5f, r - Mathf.Max(1f, d / 12f), C(255, 112, 164));
        int a = Mathf.RoundToInt(d * 0.3f), th = Mathf.Max(1, d / 10);
        for (int i = a; i < d - a; i++)
            for (int k = -th; k <= th; k++) { cv.Set(i + k, i, White); cv.Set(d - 1 - i + k, i, White); }
        return MakeSprite(cv, Vector2.zero, "Pinball Close", false);
    }

    // ------------------------------------------------------------------ the scoreboard: an orange dot matrix

    void Board(float now)
    {
        if (!sbTex) return;
        // (only when something on it changed: no strings every frame)
        bool showing = now < msgUntil && msg.Length > 0, lit = !msgBlink || Mathf.Repeat(now, 0.5f) < 0.33f;
        int h = BallNumber * 31 + High * 17 + Score * 7 + (int)phase * 1009 + (showing ? msg.GetHashCode() ^ msg2.GetHashCode() : 0) + (showing && lit ? 13 : 0);
        if (h == sbHash && sbKey.Length > 0) return;
        sbHash = h;
        string top = "BALL " + Mathf.Clamp(BallNumber, 1, balls) + "/" + balls, hi = "HI " + Fmt(High);
        string big, small = null;
        int scale = 3;
        bool showMsg = now < msgUntil && msg.Length > 0;
        if (showMsg)
        {
            bool on = !msgBlink || Mathf.Repeat(now, 0.5f) < 0.33f;
            big = on ? msg : "";
            small = msg2;
            scale = 2;
        }
        else if (phase == Phase.Setup || phase == Phase.Closing) { big = "PINBALL"; small = "SHIFT FLIPS, SPACE LAUNCHES"; scale = 2; }
        else if (phase == Phase.Over) { big = "GAME OVER"; small = "SCORE " + Fmt(Score); scale = 2; }
        else big = Fmt(Score);
        if (scale == 3 && PixelFont.Measure(big) * 3 > DmdW - 4) scale = 2;
        if (PixelFont.Measure(big) * scale > DmdW - 4) scale = 1;
        string key = top + "|" + hi + "|" + big + "|" + small + "|" + scale;
        if (key == sbKey) return;
        sbKey = key;
        for (int i = 0; i < dmdBuf.Length; i++) dmdBuf[i] = default;
        DmdText(top, 1, 1, 0, false);
        DmdText(hi, 1, 1, DmdW - 1 - PixelFont.Measure(hi), false);
        if (small != null && small.Length > 0)
        {
            DmdText(big, scale, 7, -1, true);
            DmdText(small, 1, DmdH - 6, -1, true);
        }
        else DmdText(big, scale, scale == 3 ? 7 : 10, -1, true);
        BakeDmd();
    }

    static string Fmt(int v)
    {
        string s = Mathf.Max(0, v).ToString();
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < s.Length; i++) { if (i > 0 && (s.Length - i) % 3 == 0) sb.Append(','); sb.Append(s[i]); }
        return sb.ToString();
    }

    // text into the dot buffer (rowTop: dots from the top; x < 0 = centred)
    void DmdText(string s, int scale, int rowTop, int x, bool center)
    {
        if (string.IsNullOrEmpty(s)) return;
        int tw = PixelFont.Measure(s) + 1, th = PixelFont.GlyphH;
        var tmp = new Color32[tw * th];
        PixelFont.Draw(tmp, tw, th, s, 0, th - 1, White);
        if (center) x = (DmdW - (tw - 1) * scale) / 2;
        for (int yy = 0; yy < th; yy++)
            for (int xx = 0; xx < tw; xx++)
            {
                if (tmp[yy * tw + xx].a == 0) continue;
                for (int sy = 0; sy < scale; sy++)
                    for (int sx = 0; sx < scale; sx++)
                    {
                        int dx = x + xx * scale + sx, drow = rowTop + (th - 1 - yy) * scale + sy;
                        if (dx < 0 || dx >= DmdW || drow < 0 || drow >= DmdH) continue;
                        dmdBuf[(DmdH - 1 - drow) * DmdW + dx] = White;
                    }
            }
    }

    void BakeDmd()
    {
        int p = dmdPitch, bz = 2 * p, w = sbTex.width, h = sbTex.height;
        var cv = new InfestedWindow.Canvas(w, h);
        cv.RoundRect(0, 0, w, h, bz, C(36, 14, 40));
        cv.RoundRect(1, 1, w - 1, h - 1, bz - 1, C(104, 48, 112));
        cv.RoundRectRow(1, 1, w - 1, h - 1, bz - 1, 1, C(176, 112, 186));
        cv.RoundRectRow(1, 1, w - 1, h - 1, bz - 1, 2, C(150, 86, 160));
        cv.RoundRect(bz - 2, bz - 2, w - bz + 2, h - bz + 2, 3, C(16, 9, 8));
        Color32 on = C(255, 152, 44), onHi = C(255, 214, 140), off = C(58, 30, 16);
        int q = Mathf.Max(1, p - 1);
        for (int j = 0; j < DmdH; j++)
            for (int i = 0; i < DmdW; i++)
            {
                bool lit = dmdBuf[(DmdH - 1 - j) * DmdW + i].a > 0;
                int x0 = bz + i * p, r0 = bz + j * p;
                for (int yy = 0; yy < q; yy++)
                    for (int xx = 0; xx < q; xx++)
                    {
                        if (q >= 4 && (xx == 0 || xx == q - 1) && (yy == 0 || yy == q - 1)) continue;
                        cv.Set(x0 + xx, r0 + yy, lit ? (q >= 3 && xx <= 1 && yy <= 1 ? onHi : on) : off);
                    }
            }
        sbTex.SetPixels32(cv.px);
        sbTex.Apply(false, false);
    }

    // ------------------------------------------------------------------ every frame

    static float BackOut(float t) { float c = 1.7f, u = t - 1f; return 1f + (c + 1f) * u * u * u + c * u * u; }

    void Animate(float dt, float now)
    {
        if (!root) return;
        bool opening = phase == Phase.Setup && BallNumber == 0;
        float a = phase == Phase.Closing ? 1f - Mathf.Clamp01(phaseT / 0.5f) : opening ? Mathf.Clamp01(phaseT / 0.4f) : 1f;
        float e = phase == Phase.Closing ? 1f - Mathf.Clamp01(phaseT / 0.5f) : opening ? BackOut(Mathf.Clamp01(phaseT / 0.7f)) : 1f;
        var tint = new Color(1f, 1f, 1f, a);
        if (staticSr) staticSr.color = tint;
        for (int f = 0; f < 2; f++)
        {
            if (!flipSr[f]) continue;
            float deg = flip[f].ang * Mathf.Rad2Deg * flip[f].side;
            Place(flipSr[f], flip[f].pivot + Vector2.down * (1f - e) * 1.6f, tp, deg);
            flipSr[f].color = tint;
        }
        for (int k = 0; k < popSrs.Count && k < pops.Count; k++)
        {
            var pb = pops[k];
            pb.flash = Mathf.Max(0f, pb.flash - dt * 3f);
            popSrs[k].sprite = pb.flash > 0.35f ? popOn[k] : popOff[k];
            float sq = 0.16f * pb.flash;
            Place(popSrs[k], pb.c, tp * Mathf.Max(0f, e) * (1f + sq), 0f, tp * Mathf.Max(0f, e) * (1f - sq * 0.6f));
            popSrs[k].color = tint;
        }
        bool blink = jackpotAt >= 0f && Mathf.Repeat(now, 0.25f) < 0.125f;
        for (int k = 0; k < ringSrs.Count && k < targets.Count; k++)
        {
            var t = targets[k];
            t.flash = Mathf.Max(0f, t.flash - dt * 2.5f);
            ringSrs[k].sprite = (t.lit && !blink) || (jackpotAt >= 0f && !t.lit) ? ringOnSp : ringOffSp;
            Place(ringSrs[k], t.c, 2f * t.r * 1.3f / 64f * (1f + 0.4f * t.flash) * e);
            ringSrs[k].color = tint;
        }
        // the bin: the lid flaps open when the ball comes down at it, slams shut on a gulp; the can rattles
        float want = lidOpen * 72f;
        lidVel += ((want - lidAng) * 160f - lidVel * 14f) * dt;
        lidAng += lidVel * dt;
        if (lidAng < 0f) { lidAng = 0f; lidVel = Mathf.Abs(lidVel) * 0.35f; }
        shake = Mathf.Max(0f, shake - dt);
        float sx = shake > 0f ? Mathf.Sin(now * 70f) * 0.03f * shake / 0.6f : 0f;
        float by = e;
        Place(binBackSr, new Vector2(binR.xMin - tp + sx, binR.yMin), tp, 0f, tp * by);
        Place(binFrontSr, new Vector2(binR.xMin - tp + sx, binR.yMin), tp, 0f, tp * by);
        Place(lidSr, new Vector2(binR.xMin - 2f * tp + sx, binR.yMin + (binR.height - rimPx * tp * 0.5f) * by), tp, lidAng);
        if (binBackSr) { binBackSr.color = tint; binFrontSr.color = tint; lidSr.color = tint; }
        // the plunger
        float lx = laneX + laneW * 0.5f;
        Place(knobSr, new Vector2(lx, plungerY + (1f - e) * -1f), tp);
        if (coilSr) Place(coilSr, new Vector2(lx, W.yMin), tp, 0f, tp * Mathf.Max(0.05f, (plungerY - 0.08f - W.yMin) / tp / coilPx) * e);
        if (knobSr) { knobSr.color = tint; coilSr.color = tint; }
        // the scoreboard drops in from the top
        Board(now);
        float drop = (1f - e) * 1.8f;
        Place(sbSr, new Vector2(sbRect.xMin, sbRect.yMin + drop), screenPx);
        Place(xSr, new Vector2(xRect.xMin, xRect.yMin + drop), screenPx);
        if (sbSr) { sbSr.color = tint; xSr.color = new Color(1f, 1f, 1f, a * (Hovered ? 1f : 0.85f)); }
        Effects(dt);
    }

    // ------------------------------------------------------------------ pop-ups, puffs, window flashes

    class PopupFx { public SpriteRenderer sr; public Vector2 at; public float t = 9f; }
    readonly List<PopupFx> popups = new List<PopupFx>();
    readonly Dictionary<string, Sprite> textSp = new Dictionary<string, Sprite>();
    class DotFx { public SpriteRenderer sr; public Vector2 p, v; public float t = 9f, life; }
    readonly List<DotFx> dots = new List<DotFx>();
    class FlashFx { public SpriteRenderer[] e = new SpriteRenderer[4]; public Rect r; public float t = 9f; }
    readonly List<FlashFx> flashes = new List<FlashFx>();

    void Popup(string text, Vector2 at, bool big)
    {
        if (!root) return;
        if (!textSp.TryGetValue(text, out Sprite sp) || !sp)
        {
            int k = big ? 2 : 1, tw = PixelFont.Measure(text) * k + 4, th = PixelFont.GlyphH * k + 4;
            var cv = new InfestedWindow.Canvas(tw, th);
            Color32 col = big ? C(255, 132, 196) : C(255, 226, 96);
            for (int oy = -1; oy <= 1; oy++)
                for (int ox = -1; ox <= 1; ox++)
                    if (ox != 0 || oy != 0) { if (big) cv.Big(text, 2 + ox, 2 + oy, Ink); else cv.Text(text, 2 + ox, 2 + oy, Ink); }
            if (big) cv.Big(text, 2, 2, col); else cv.Text(text, 2, 2, col);
            sp = MakeSprite(cv, new Vector2(0.5f, 0.5f), "Pinball Points", false);
            textSp[text] = sp;
        }
        PopupFx fx = null;
        foreach (var q in popups) if (q.t > 0.9f) { fx = q; break; }
        if (fx == null)
        {
            if (popups.Count >= 10) return;
            fx = new PopupFx { sr = Sr("Pinball Points", sp, OrderPopup) };
            popups.Add(fx);
        }
        fx.sr.sprite = sp;
        if (material) fx.sr.sharedMaterial = SpriteMats.For(material, sp.texture);
        fx.at = at;
        fx.t = 0f;
    }

    void Poof(Vector2 at)
    {
        if (!root) return;
        for (int i = 0; i < 9; i++)
        {
            DotFx d = null;
            foreach (var q in dots) if (q.t >= q.life) { d = q; break; }
            if (d == null)
            {
                if (dots.Count >= 45) return;
                d = new DotFx { sr = Sr("Pinball Puff", dotSp, OrderFx) };
                dots.Add(d);
            }
            float ang = i / 9f * Mathf.PI * 2f + Random.Range(-0.2f, 0.2f);
            d.p = at;
            d.v = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * Random.Range(1.6f, 2.6f);
            d.t = 0f;
            d.life = Random.Range(0.35f, 0.5f);
        }
    }

    void Flash(Rect r)
    {
        if (!root) return;
        FlashFx f = null;
        foreach (var q in flashes) if (q.t > 0.45f) { f = q; break; }
        if (f == null)
        {
            if (flashes.Count >= 4) return;
            f = new FlashFx();
            for (int i = 0; i < 4; i++) f.e[i] = Sr("Pinball Bumper Glow", whiteSp, OrderFx);
            flashes.Add(f);
        }
        f.r = r;
        f.t = 0f;
    }

    void Effects(float dt)
    {
        float px = screenPx;
        foreach (var fx in popups)
        {
            fx.t += dt;
            bool on = fx.t < 0.9f;
            fx.sr.enabled = on;
            if (!on) continue;
            float k = Mathf.Max(2, Mathf.RoundToInt(3f * ScreenBounds.MainScreenPixels / 1080f));
            Place(fx.sr, fx.at + Vector2.up * (0.7f * fx.t), px * k);
            fx.sr.color = new Color(1f, 1f, 1f, Mathf.Clamp01((0.9f - fx.t) / 0.3f));
        }
        foreach (var d in dots)
        {
            d.t += dt;
            bool on = d.t < d.life;
            d.sr.enabled = on;
            if (!on) continue;
            d.p += d.v * dt;
            d.v *= 1f - 4f * dt;
            float u = d.t / d.life;
            Place(d.sr, d.p, px * 3f * (1.2f - u));
            d.sr.color = new Color(1f, 0.94f, 0.98f, 1f - u);
        }
        foreach (var f in flashes)
        {
            f.t += dt;
            bool on = f.t < 0.45f;
            float th = 4f * px, al = Mathf.Clamp01(1f - f.t / 0.45f);
            for (int i = 0; i < 4; i++)
            {
                f.e[i].enabled = on;
                if (!on) continue;
                f.e[i].color = new Color(1f, 0.86f, 0.3f, al);
                Rect r = f.r;
                Vector2 c = i == 0 ? new Vector2(r.center.x, r.yMax) : i == 1 ? new Vector2(r.center.x, r.yMin) : i == 2 ? new Vector2(r.xMin, r.center.y) : new Vector2(r.xMax, r.center.y);
                float sxw = i < 2 ? r.width + th : th, syw = i < 2 ? th : r.height + th;
                var t = f.e[i].transform;
                t.position = new Vector3(c.x, c.y, 0f);
                t.rotation = Quaternion.identity;
                t.localScale = new Vector3(sxw / 4f, syw / 4f, 1f);
            }
        }
    }
}
