using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Where the buddy is in the stack of windows (Occlusion): on the desktop (behind every window: the floor, icons, peeking
/// in from the side), on the window it stands on, climbs or is stuck to (in front of that one, behind the ones in front of
/// it), or in front of everything (you're holding it, it's flying after a throw until it lands, it's hanging from your
/// cursor, you're chatting with it). Up in the air it stays where it was. Every frame the windows in front of that layer
/// around it hide those parts of it: the gummy body clips them in its shader, its face, hat, board, coil... with sprite
/// masks (each buddy has its own range of draw orders, so the masks don't touch the others). Its speech bubble and level
/// bar fade out while its head is covered, and your clicks go through to the window there.
/// </summary>
[DefaultExecutionOrder(500)] // last: everything has moved and placed its sprites this frame
[RequireComponent(typeof(GummyBody))]
[DisallowMultipleComponent]
public class BuddyLayer : MonoBehaviour
{
    /// <summary>The layer it's on (Occlusion.Front, 0 = the front-most window ... Occlusion.Back = the desktop).</summary>
    public int Layer { get; private set; }
    /// <summary>How much of its head shows (0..1, smoothed): its bubble and level bar fade with it.</summary>
    public float HeadSeen { get; private set; } = 1f;
    /// <summary>How many windows hide a part of it right now.</summary>
    public int Covering => rects.Count;
    /// <summary>In front of everything right now (held, thrown, on your cursor, chatting).</summary>
    public bool InFront => Layer == Occlusion.Front;
    /// <summary>What it's on (or was last on): a window id, 0 = the desktop, -1 = in front of everything.</summary>
    public long SurfaceId => lastWin;
    /// <summary>Inside a window it broke into (WindowInfestation): on that window's layer, like standing on it.</summary>
    public long InsideWindow { get; set; }
    /// <summary>In front of everything no matter what (on the pinball table: the ball and the crowd in the stands).</summary>
    public bool ForceFront { get; set; }

    GummyBody body;
    BuddyRope rope;
    GummyBrain brain;
    BeanSteam steam;
    long lastWin;      // what it was last on: -1 = in front of everything, 0 = the desktop, else a window id
    bool flying;       // let go from your hand (or your cursor): in front until it lands
    readonly List<Rect> rects = new List<Rect>(GummyBody.MaxOccluders + 1);
    readonly List<SpriteMask> masks = new List<SpriteMask>();
    readonly List<SpriteRenderer> sprites = new List<SpriteRenderer>();
    readonly List<Sprite> fitted = new List<Sprite>();      // the picture each sprite was last put on its material copy for
    readonly List<LineRenderer> lines = new List<LineRenderer>();
    Transform maskRoot;
    float nextScan;
    bool masked;
    static Sprite s_white;
    static bool s_whiteBaked;

    void Awake()
    {
        body = GetComponent<GummyBody>();
        rope = GetComponent<BuddyRope>();
        brain = GetComponent<GummyBrain>();
        steam = GetComponent<BeanSteam>();
        body.ExternalOcclusion = true;
        Layer = Occlusion.Back;
    }

    void OnDisable()
    {
        rects.Clear();
        if (body) body.SetOccluders(rects);
        foreach (var m in masks) if (m) m.enabled = false;
        SetMasked(false);
        HeadSeen = 1f;
        flying = false;
    }

    void OnDestroy()
    {
        if (maskRoot) Destroy(maskRoot.gameObject);
    }

    /// <summary>Is this point of it hidden right now (a window in front of it covers it)?</summary>
    public bool Hides(Vector2 p)
    {
        for (int i = 0; i < rects.Count; i++) if (rects[i].Contains(p)) return true;
        return false;
    }

    void LateUpdate() { long t0 = WhipProf.T(); LateUpdateTimed(); WhipProf.Add("BuddyLayer.LateUpdate", t0); }
    void LateUpdateTimed()
    {
        Occlusion.PollHotkey();
        Layer = Decide();

        // the windows in front of it, around it (with room for a coil, a board, a balloon above it)
        rects.Clear();
        float s = body.size;
        Rect e = body.Extent;
        if (e.width <= 0f) e = new Rect(body.Center.x - s * 0.5f, body.Center.y - s * 0.5f, s, s);
        Rect around = Rect.MinMaxRect(e.xMin - 1.3f * s, e.yMin - 0.6f * s, e.xMax + 1.3f * s, e.yMax + 2.4f * s);
        int max = body.Hiding ? GummyBody.MaxOccluders - 1 : GummyBody.MaxOccluders;
        if (Layer != Occlusion.Front) Occlusion.Occluders(Layer, around, rects, max);
        if (body.Hiding) rects.Add(body.HideRect); // (hide and seek: behind that window)
        body.SetOccluders(rects);
        UpdateMasks();
        FitMaterials();

        // its head: the bubble and the level bar go with it
        bool head = Hides(body.TopPoint + Vector2.down * (0.1f * s)) && Hides(body.Center);
        HeadSeen = Mathf.MoveTowards(HeadSeen, head ? 0f : 1f, Time.unscaledDeltaTime * 8f); // (real time: windows move while time's stopped too)
    }

    int Decide()
    {
        if (ForceFront) { flying = false; return Occlusion.Front; }
        bool chatting = BuddyChat.Open && BuddyChat.Target == body;
        if (body.Held || (rope && rope.Riding) || chatting) { flying = !chatting; return Occlusion.Front; }
        if (flying)
        {
            if (!body.Grounded && !body.IsStuck && !body.Crawling) return Occlusion.Front;
            flying = false;
        }
        if (!Occlusion.Enabled) return Occlusion.Front;
        if (InsideWindow != 0 && Occlusion.Exists(InsideWindow)) { lastWin = InsideWindow; flying = false; return Occlusion.LayerOf(InsideWindow); }
        long now = SurfaceNow();
        if (now != long.MinValue) lastWin = now;
        if (lastWin > 0 && !Occlusion.Exists(lastWin)) lastWin = 0; // (that window closed: the desktop)
        return lastWin < 0 ? Occlusion.Front : lastWin == 0 ? Occlusion.Back : Occlusion.LayerOf(lastWin);
    }

    // what it's on right now (long.MinValue = nothing: up in the air, it stays where it was)
    long SurfaceNow()
    {
        if (body.Crawling) return body.CrawlEdge.surface;           // up a window side (0 = a screen edge)
        if (body.IsStuck) return body.GluedWindow;                   // stuck to a window (0 = a screen edge)
        if (body.Grounded)
        {
            if (body.SupportBody)
            {
                var other = body.SupportBody.GetComponent<BuddyLayer>(); // sitting on a friend: where the friend is
                if (other) return other.lastWin;
            }
            if (body.OnPlatform && body.SupportWindow != 0 && !DesktopPlatforms.IsIcon(body.SupportWindow)) return body.SupportWindow;
            return 0;                                                // the floor, an icon
        }
        if (body.OnRope && rope && rope.Active) return HookWindow(rope.Hook);
        return long.MinValue;
    }

    // the front-most window whose edge (top or bottom) the rope is hooked on
    static long HookWindow(Vector2 hook)
    {
        var w = DesktopPlatforms.Windows;
        for (int i = 0; i < w.Count; i++)
        {
            WindowBox b = w[i];
            if (hook.x < b.xMin - 0.15f || hook.x > b.xMax + 0.15f) continue;
            if (Mathf.Abs(hook.y - b.yMax) < 0.15f || Mathf.Abs(hook.y - b.yMin) < 0.15f) return b.id;
        }
        return long.MinValue;
    }

    // ------------------------------------------------------------------ sprite masks (its face, hat, board...)

    void UpdateMasks()
    {
        int o = body.sortingOrder;
        if (rects.Count > 0 && !maskRoot) maskRoot = new GameObject(name + " Occlusion").transform;
        while (masks.Count < rects.Count) masks.Add(MakeMask());
        for (int i = 0; i < masks.Count; i++)
        {
            var m = masks[i];
            bool on = i < rects.Count;
            if (m.enabled != on) m.enabled = on;
            if (!on) continue;
            Rect r = rects[i];
            m.transform.position = new Vector3(r.center.x, r.center.y, 0f);
            m.transform.localScale = new Vector3(r.width, r.height, 1f);
            m.backSortingOrder = o - 5;  // its own draw orders only (o-4 .. o+5): the other buddies use other ranges
            m.frontSortingOrder = o + 5;
        }
        if (Time.unscaledTime >= nextScan || (rects.Count > 0) != masked)
        {
            if (Time.unscaledTime >= nextScan) { nextScan = Time.unscaledTime + 0.5f; Scan(); }
            SetMasked(rects.Count > 0, true);
        }
    }

    // its sprites: its own, its little icons, its steam
    void Scan()
    {
        sprites.Clear();
        GetComponentsInChildren(true, sprites);
        var extra = new List<SpriteRenderer>();
        if (brain && brain.IconRoot) { brain.IconRoot.GetComponentsInChildren(true, extra); sprites.AddRange(extra); }
        if (steam && steam.Root) { extra.Clear(); steam.Root.GetComponentsInChildren(true, extra); sprites.AddRange(extra); }
        fitted.Clear();
        for (int i = 0; i < sprites.Count; i++) fitted.Add(null);
        GetComponentsInChildren(true, lines);
        if (body.spriteMaterial) foreach (var lr in lines) SpriteMats.FitLine(lr, body.spriteMaterial);
    }

    // every picture on its own copy of the sprite material (Unity can swap the pictures of sprites that share one: the
    // board drew its leaf). Only looks again when a sprite's picture changes.
    void FitMaterials()
    {
        Material m = body.spriteMaterial;
        if (!m) return;
        for (int i = 0; i < sprites.Count && i < fitted.Count; i++)
        {
            SpriteRenderer sr = sprites[i];
            if (!sr) continue;
            Sprite sp = sr.sprite;
            if (sp == fitted[i]) continue;
            fitted[i] = sp;
            SpriteMats.Fit(sr, m);
        }
    }

    void SetMasked(bool on, bool force = false)
    {
        if (on == masked && !force) return;
        masked = on;
        var mode = on ? SpriteMaskInteraction.VisibleOutsideMask : SpriteMaskInteraction.None;
        foreach (var sr in sprites) if (sr && sr.maskInteraction != mode) sr.maskInteraction = mode;
    }

    SpriteMask MakeMask()
    {
        if (!s_whiteBaked)
        {
            var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false) { name = "Occluder" };
            var px = new Color32[16];
            for (int i = 0; i < 16; i++) px[i] = new Color32(255, 255, 255, 255);
            tex.SetPixels32(px);
            tex.Apply(false, true);
            s_white = Sprite.Create(tex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 4f); // 1 x 1 world unit
            s_whiteBaked = true;
        }
        var go = new GameObject("Window In Front");
        go.transform.SetParent(maskRoot, false);
        var m = go.AddComponent<SpriteMask>();
        m.sprite = s_white;
        m.isCustomRangeActive = true;
        m.enabled = false;
        return m;
    }
}
