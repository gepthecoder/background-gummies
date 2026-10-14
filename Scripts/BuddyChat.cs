using System;
using System.Collections.Generic;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Talk to your buddies: Ctrl+Shift+Space opens a little pixel chat box next to the buddy nearest the cursor.
/// Type, Enter sends, Tab switches buddy, Esc (or clicking back into your app) closes it. They answer in their speech
/// bubble, and some messages make them do things ("hide and seek", "snack", "jump", "come here", "nap", "tag",
/// "crack the whip"...). The replies are scripted for now (ChatReplies: they remember what you tell them about you and
/// know about your day); ReplyOverride is where the AI agent plugs in later. The first time today it says good morning.
/// While the box is open the overlay takes the keyboard; closing it gives the keyboard back to the app you were in.
/// </summary>
[DisallowMultipleComponent]
public class BuddyChat : MonoBehaviour
{
    [Tooltip("Sprite material (WhipIt > Setup assigns it).")]
    public Material material;
    public int sortingOrder = 40;
    [Tooltip("Screen pixels per font pixel on a 1080p screen.")]
    public int pixelScale1080 = 3;
    public int maxChars = 60;
    [Tooltip("Closes by itself after this long without typing (seconds).")]
    public float closeAfterIdle = 30f;
    [Tooltip("XP for talking to it (at most once every 5 s).")]
    public int xpPerMessage = 1;

    /// <summary>The chat box is open (the buddies hold still and listen; no games start meanwhile).</summary>
    public static bool Open { get; private set; }
    /// <summary>Who you're talking to.</summary>
    public static GummyBody Target { get; private set; }
    /// <summary>The AI agent's hook: (buddy, what you typed) -> its answer; return null to use the scripted reply.</summary>
    public static Func<GummyBody, string, string> ReplyOverride;
    /// <summary>(buddy, what you typed)</summary>
    public event Action<GummyBody, string> Sent;

    string text = "";
    float openedAt, lastKey, lastXp = -10f, replyAt, fontPx = 0.028f, screenPx;
    bool wasHotkey, caret, dirty;
    string pendingReply;
    GummyBody replyFrom;
    Action pendingAction;
    bool closeAfterReply;
    GameObject holder;
    SpriteRenderer box;
    Texture2D tex;
    Sprite sprite;
    int boxW, boxH;
    readonly List<char> typed = new List<char>();
#if ENABLE_INPUT_SYSTEM
    Keyboard hooked;
#endif

    void OnDisable()
    {
        if (Open) Close();
#if ENABLE_INPUT_SYSTEM
        if (hooked != null) { hooked.onTextInput -= OnText; hooked = null; }
#endif
    }

    void OnDestroy()
    {
        if (holder) Destroy(holder);
        if (sprite) Destroy(sprite);
        if (tex) Destroy(tex);
    }

    // ------------------------------------------------------------------ open / close

    /// <summary>Open the box for this buddy (or the one nearest the cursor).</summary>
    public void OpenFor(GummyBody who = null)
    {
        who = who ? who : Nearest(Grabber.CursorWorld);
        if (!who) return;
        Target = who;
        Open = true;
        text = "";
        typed.Clear();
        openedAt = lastKey = Time.unscaledTime;
        dirty = true;
        TransparentWindow.BeginTyping();
        Hook();
        var sp = who.GetComponent<BuddySpeech>();
        var clock = WorkClock.Instance;
        if (sp && clock && !clock.Greeted) { sp.Say(ChatReplies.Greeting(sp.voice), 0f, true); clock.Greeted = true; } // first time today
        else if (sp && UnityEngine.Random.value < 0.6f) sp.Say(UnityEngine.Random.value < 0.5f ? "YES?" : "HM?", 1.2f, true);
    }

    public void Close()
    {
        if (!Open) return;
        Open = false;
        if (box) box.enabled = false;
        TransparentWindow.EndTyping();
    }

    static GummyBody Nearest(Vector2 at)
    {
        GummyBody best = null;
        float bd = float.MaxValue;
        foreach (var b in GummyBody.All)
        {
            if (!b || !b.GetComponent<BuddySpeech>()) continue;
            float d = (b.Center - at).sqrMagnitude;
            if (d < bd) { bd = d; best = b; }
        }
        return best;
    }

    void NextTarget()
    {
        var all = new List<GummyBody>();
        foreach (var b in GummyBody.All) if (b && b.GetComponent<BuddySpeech>()) all.Add(b);
        if (all.Count < 2) return;
        int i = all.IndexOf(Target);
        Target = all[(i + 1) % all.Count];
        dirty = true;
    }

    // ------------------------------------------------------------------ typing

    void Hook()
    {
#if ENABLE_INPUT_SYSTEM
        var k = Keyboard.current;
        if (k == hooked) return;
        if (hooked != null) hooked.onTextInput -= OnText;
        hooked = k;
        if (k != null) k.onTextInput += OnText;
#endif
    }

    void OnText(char c)
    {
        if (!Open) return;
        typed.Add(c);
    }

    /// <summary>Feed typed characters (the harness and the legacy input path use this too). '\b' deletes, '\n' sends.</summary>
    public void Type(string chars)
    {
        foreach (char c in chars) Key(c);
    }

    void Key(char c)
    {
        if (!Open) return;
        lastKey = Time.unscaledTime;
        if (c == '\b') { if (text.Length > 0) text = text.Substring(0, text.Length - 1); dirty = true; return; }
        if (c == '\n' || c == '\r') { Send(); return; }
        if (c == 27) { Close(); return; }
        if (c == '\t') { NextTarget(); return; }
        if (c < 32 || text.Length >= maxChars) return;
        text += c;
        dirty = true;
    }

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("BuddyChat.Update", t0); }
    void UpdateTimed()
    {
        float now = Time.unscaledTime;
        bool hk = DesktopInput.ChordHeld(DesktopInput.VK_CONTROL, DesktopInput.VK_SHIFT, DesktopInput.VK_SPACE);
        if (hk && !wasHotkey) { if (Open) Close(); else OpenFor(); }
        wasHotkey = hk;

        // the answer (after a little "..." pause)
        if (pendingReply != null && now >= replyAt)
        {
            var sp = replyFrom ? replyFrom.GetComponent<BuddySpeech>() : null;
            if (sp) sp.Say(pendingReply, 0f, true);
            pendingReply = null;
            var act = pendingAction;
            pendingAction = null;
            if (closeAfterReply) { closeAfterReply = false; Close(); }
            act?.Invoke();
        }

        if (!Open) return;
        if (!Target || !Target.isActiveAndEnabled) { Close(); return; }
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        if (!Application.isFocused && now - openedAt > 0.6f) { Close(); return; } // you clicked back into your app
#endif
        if (now - Mathf.Max(lastKey, openedAt) > closeAfterIdle) { Close(); return; }

        ReadKeys();
        foreach (char c in typed.ToArray()) Key(c);
        typed.Clear();

        var brain = Target.GetComponent<GummyBrain>();
        if (brain) brain.Linger(1f); // stays and listens

        bool blink = Mathf.Repeat(now - lastKey, 1f) < 0.55f;
        if (blink != caret) { caret = blink; dirty = true; }
        UpdateBox();
    }

    void ReadKeys()
    {
        if (!Open) return;
#if ENABLE_INPUT_SYSTEM
        Hook();
        var k = Keyboard.current;
        if (k == null) return;
        typed.RemoveAll(c => c < 32); // Enter / Backspace / Tab / Esc come from the keys below, not the text stream
        if (k.escapeKey.wasPressedThisFrame) { typed.Clear(); Close(); return; }
        if (k.backspaceKey.wasPressedThisFrame) typed.Add('\b');
        if (k.tabKey.wasPressedThisFrame) typed.Add('\t');
        if (k.enterKey.wasPressedThisFrame || k.numpadEnterKey.wasPressedThisFrame) typed.Add('\n');
#else
        foreach (char c in Input.inputString) typed.Add(c == '\r' ? '\n' : c);
        if (Input.GetKeyDown(KeyCode.Escape)) typed.Add((char)27);
        if (Input.GetKeyDown(KeyCode.Tab)) typed.Add('\t');
#endif
    }

    void Send()
    {
        string msg = text.Trim();
        text = "";
        dirty = true;
        if (msg.Length == 0 || !Target) return;
        var progress = Target.GetComponent<BuddyProgress>();
        if (progress && Time.unscaledTime - lastXp > 5f) { lastXp = Time.unscaledTime; progress.AddXp(xpPerMessage, Target.Center); }
        Sent?.Invoke(Target, msg);
        WorkClock.NoteChat();

        string reply = ReplyOverride?.Invoke(Target, msg);
        Action act = null;
        bool close = false;
        if (reply == null) reply = ChatReplies.Reply(Target, msg, this, out act, out close);
        var sp = Target.GetComponent<BuddySpeech>();
        if (sp) sp.Say("...", 0.7f, true); // thinking
        pendingReply = reply;
        pendingAction = act;
        closeAfterReply = close;
        replyFrom = Target;
        replyAt = Time.unscaledTime + UnityEngine.Random.Range(0.55f, 0.9f);
    }

    // ------------------------------------------------------------------ the box

    void UpdateBox()
    {
        if (!holder)
        {
            holder = new GameObject("Chat Box");
            var go = new GameObject("Box");
            go.transform.SetParent(holder.transform, false);
            box = go.AddComponent<SpriteRenderer>();
            box.sortingOrder = sortingOrder;
            if (material) box.sharedMaterial = material;
        }
        PixelSize();
        if (dirty)
        {
            dirty = false;
            var sp = Target.GetComponent<BuddySpeech>();
            string label = (sp ? sp.Name : "APPLE") + ":";
            Color c = Target.color;
            var ink = new Color32((byte)(Mathf.Clamp01(c.r * 0.6f + 0.4f) * 255), (byte)(Mathf.Clamp01(c.g * 0.6f + 0.4f) * 255), (byte)(Mathf.Clamp01(c.b * 0.6f + 0.4f) * 255), 255);
            Color32[] px = Bake(label, ink, text, caret, out int w, out int h);
            if (!tex || tex.width != w || tex.height != h)
            {
                if (sprite) Destroy(sprite);
                if (tex) Destroy(tex);
                tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Point, name = "ChatBox" };
                sprite = null;
            }
            tex.SetPixels32(px);
            tex.Apply(false, false);
            if (!sprite) sprite = Sprite.Create(tex, new Rect(0, 0, w, h), Vector2.zero, 1f, 0, SpriteMeshType.FullRect);
            box.sprite = sprite;
            boxW = w; boxH = h;
        }
        box.enabled = true;

        // beside the buddy (right if there's room), a little above its middle
        Rect e = Target.Extent, r = ScreenBounds.ScreenRectAt(Target.Center); // (its own screen)
        float bw = boxW * fontPx, bh = boxH * fontPx;
        Vector2 p = new Vector2(e.xMax + 3f * fontPx, e.center.y + 0.1f * e.height - bh * 0.5f);
        if (p.x + bw > r.xMax - fontPx) p.x = e.xMin - 3f * fontPx - bw;
        p.x = Mathf.Clamp(p.x, r.xMin + fontPx, r.xMax - bw - fontPx);
        p.y = Mathf.Clamp(p.y, r.yMin + fontPx, r.yMax - bh - fontPx);
        holder.transform.position = Snap(p);
        holder.transform.localScale = new Vector3(fontPx, fontPx, 1f);
    }

    /// <summary>Dark rounded box: "APPLE: what you typed_" (pixels, y up). Long text scrolls (shows the end).</summary>
    public static Color32[] Bake(string label, Color32 labelInk, string text, bool caret, out int w, out int h)
    {
        const int pad = 3, maxText = 150;
        var fill = new Color32(38, 30, 26, 238);
        var edge = new Color32(255, 252, 244, 255);
        var ink = new Color32(255, 246, 228, 255);
        var dim = new Color32(150, 138, 124, 255);
        bool empty = text.Length == 0;
        string shown = empty ? "TYPE SOMETHING..." : text;
        while (!empty && shown.Length > 1 && PixelFont.Measure(shown) > maxText) shown = shown.Substring(1);
        int lw = PixelFont.Measure(label), tw = PixelFont.Measure(shown);
        w = Mathf.Max(pad + lw + PixelFont.SpaceAdvance + tw + (empty ? 10 : 5) + pad, 70);
        h = PixelFont.GlyphH + 2 * pad;
        var px = new Color32[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                bool ex = x == 0 || x == w - 1, ey = y == 0 || y == h - 1;
                px[y * w + x] = ex && ey ? new Color32(0, 0, 0, 0) : ex || ey ? edge : fill;
            }
        int top = h - 1 - pad;
        PixelFont.Draw(px, w, h, label, pad, top, labelInk);
        int tx = pad + lw + PixelFont.SpaceAdvance;
        PixelFont.Draw(px, w, h, shown, empty ? tx + 5 : tx, top, empty ? dim : ink); // the caret sits in front of the hint
        if (caret)
        {
            int cx = empty ? tx : tx + tw + 1;
            for (int i = 0; i < 3 && cx + i < w - 1; i++) px[pad * w + cx + i] = ink; // underscore on the baseline
        }
        return px;
    }

    void PixelSize()
    {
        var cam = Camera.main;
        if (!cam || Screen.height <= 0) { screenPx = 0f; return; }
        screenPx = 2f * cam.orthographicSize / Screen.height;
        fontPx = screenPx * Mathf.Max(1, Mathf.RoundToInt(pixelScale1080 * ScreenBounds.MainScreenPixels / 1080f));
    }

    Vector3 Snap(Vector2 p)
    {
        if (screenPx <= 0f) return new Vector3(p.x, p.y, 0f);
        var cam = Camera.main;
        Vector2 o = (Vector2)cam.transform.position - new Vector2(cam.orthographicSize * Screen.width / Screen.height, cam.orthographicSize);
        Vector2 d = p - o;
        return new Vector3(o.x + Mathf.Round(d.x / screenPx) * screenPx, o.y + Mathf.Round(d.y / screenPx) * screenPx, 0f);
    }
}
