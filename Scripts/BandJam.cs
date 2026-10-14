using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A jam (BuddyBand): everyone awake plays along right where they stand, a song made up on the spot. Your desktop picks
/// it: the windows you have open set the tempo and the chords; the groove and the tune are new every time, unless you've
/// been throwing them onto windows: then the tune is yours ("WE LEARNED YOUR SONG!"). Who plays what: the duck sings the
/// tune (else the apple, the tomato...), the monster plays the bass on its tuba (else the dino), the burger beatboxes,
/// the coffee bean shakes like a maraca and counts in, the tomato strums the chords and the apple plays marimba runs.
/// Two bars count-in, two bars groove, the tune twice, a solo, and a big TA-DAA (they all jump). They bob to their
/// notes, notes float up, the windows they stand on light up like keys. Nobody's scared of a monster while it plays;
/// grab one and it stops ("HEY! I WAS PLAYING!"); throw one onto a window and its note plays along. +1 XP each at the
/// end. One jam at a time; the notes are scheduled on the audio clock so they stay in time.
/// </summary>
[DefaultExecutionOrder(10)] // after BuddyMonster (8) and BuddyFusion (9): in a jam nobody's chasing or scared
public class BandJam : MonoBehaviour
{
    public enum Part { Melody, Bass, Drums, Shaker, Chords, Arp, Dance }

    /// <summary>The jam going on (null = none).</summary>
    public static BandJam Current { get; private set; }
    /// <summary>Jams started so far.</summary>
    public static int Jams { get; private set; }
    /// <summary>Every note it plays (sims): when (audio clock), who, instrument, MIDI note (0 = a drum), volume.</summary>
    public static event System.Action<double, GummyBody, BuddyBand.Instrument, int, float> Noted;
    /// <summary>A buddy played a jam right to the end (TA-DAA).</summary>
    public static event System.Action<GummyBody> Played;

    public float Bpm => bpm;
    /// <summary>The tune is the one you played by throwing them onto windows.</summary>
    public bool PlaysYourTune => yourTune;
    /// <summary>0 = offbeat skank, 1 = strum, 2 = 3-3-2.</summary>
    public int Groove => style;
    /// <summary>The chords, e.g. "I V vi IV".</summary>
    public string Chords { get { var s = new string[4]; for (int i = 0; i < 4; i++) s[i] = Roman(prog[i]); return string.Join(" ", s); } }
    public IReadOnlyDictionary<GummyBody, Part> Roles => roles;
    public int NotesPlayed => played;
    /// <summary>How long the song is (seconds).</summary>
    public float Length => (float)(Bars * 8 * half) + 2f;
    public bool Finished { get; private set; }

    const int Bars = 16;          // 0-1 count-in, 2-3 groove, 4-7 the tune, 8-11 again (+ runs), 12-15 solo; 16: TA-DAA
    const double Lookahead = 0.15;
    const int Voices = 32;
    static readonly int[][] Progs = { new[] { 0, 7, 9, 5 }, new[] { 0, 9, 5, 7 }, new[] { 9, 5, 0, 7 }, new[] { 0, 5, 9, 7 }, new[] { 0, 5, 7, 5 } };
    static readonly int[] Penta = { 0, 2, 4, 7, 9 };
    // two-bar rhythms for the tune (x = a note on that eighth)
    static readonly string[] Rhythms = { "x.x.x.x.x...x...", "x..x..x.x.x.x...", "x.xxx.x.x...x.x.", "xx.x.x..x.x.x...", "x...x.xxx.x.x...", "x.x..xx.x...xx.." };
    static int s_usedTune = -1;
    // how loud each part is in the mix: the tune on top, the bass and the kick under it, the chords behind
    static readonly float[] PartGain = { 1.5f, 0.55f, 0.8f, 0.9f, 0.6f, 1f, 0f };

    struct Ev { public double t; public GummyBody who; public BuddyBand.Instrument inst; public int note; public float vel; public float look; public bool glyph, key; }
    struct Line { public double t; public GummyBody who; public string text; public bool hop; }

    readonly Dictionary<GummyBody, Part> roles = new Dictionary<GummyBody, Part>();
    readonly Dictionary<Part, GummyBody> byPart = new Dictionary<Part, GummyBody>();
    readonly List<Ev> evs = new List<Ev>();
    readonly List<Line> lines = new List<Line>();
    readonly HashSet<GummyBody> complained = new HashSet<GummyBody>();
    readonly HashSet<GummyBody> didPlay = new HashSet<GummyBody>();
    AudioSource[] pool;
    int nextEv, nextLook, nextLine, nextVoice, played, style;
    int[] prog;
    float bpm, master, startTime, aloneSince = -1f;
    double t0, half, tEnd;
    bool yourTune, ended;

    // ------------------------------------------------------------------ starting one

    /// <summary>Start a jam (asked: from the chat or the menu: it wakes the sleepy ones and unmutes the band).
    /// Null if one's going on already, the band is muted, or nobody can play.</summary>
    public static BandJam Begin(GummyBody asker, bool asked)
    {
        if (Current) return null;
        if (asked && BuddyBand.Muted) BuddyBand.Muted = false;
        if (BuddyBand.Muted) return null;
        var players = Available(asked);
        if (players.Count == 0) return null;
        var go = new GameObject("Band Jam");
        var j = go.AddComponent<BandJam>();
        j.Setup(asker, players);
        return j;
    }

    /// <summary>How many could play right now.</summary>
    public static int Ready(bool wake) => Available(wake).Count;

    static List<GummyBody> Available(bool wake)
    {
        var list = new List<GummyBody>();
        foreach (var b in GummyBody.All)
        {
            if (!b || !b.isActiveAndEnabled || b.InRoom || !b.GetComponent<BuddyBand>()) continue; // (not one inside a window: WindowInfestation)
            var br = b.GetComponent<GummyBrain>();
            if (br && br.Current == GummyBrain.State.Asleep) { if (!wake) continue; br.WakeUp(); }
            if (Busy(b)) continue;
            list.Add(b);
        }
        return list;
    }

    // in the middle of something else
    static bool Busy(GummyBody b)
    {
        if (b.Held || b.OnRope || b.Gliding || b.Skating || b.Hiding) return true;
        var br = b.GetComponent<GummyBrain>();
        if (br && (br.Towering || br.InPit)) return true;
        var sl = b.GetComponent<BuddySlide>();
        if (sl && sl.Busy) return true;
        return BuddyTower.Has(b);
    }

    void Setup(GummyBody asker, List<GummyBody> players)
    {
        Current = this;
        Jams++;
        int layout = LayoutHash();
        var lr = new System.Random(layout);                              // your desktop: tempo, chords
        var rng = new System.Random(layout * 31 + Jams * 7919 + 17);     // new every time: groove, tune
        bpm = 100f + 5f * lr.Next(0, 5);
        prog = Progs[lr.Next(Progs.Length)];
        style = rng.Next(3);
        half = 30.0 / bpm;                                               // an eighth
        Assign(players, asker);
        var mb = asker ? asker.GetComponent<BuddyBand>() : null;
        if (!mb) foreach (var b in players) { mb = b.GetComponent<BuddyBand>(); if (mb) break; }
        master = mb ? mb.jamVolume : 0.5f;
        yourTune = BuddyBand.TuneReady && BuddyBand.TuneVersion != s_usedTune && byPart.ContainsKey(Part.Melody);
        if (yourTune) s_usedTune = BuddyBand.TuneVersion;
        t0 = AudioSettings.dspTime + 0.3;
        startTime = Time.time;
        tEnd = T(Bars, 0) + 2.0; // (the last chord rings out)

        pool = new AudioSource[Voices];
        for (int i = 0; i < Voices; i++)
        {
            var s = gameObject.AddComponent<AudioSource>();
            s.playOnAwake = false;
            s.spatialBlend = 0f;
            pool[i] = s;
        }
        Compose(rng);
        evs.Sort((a, b) => a.t.CompareTo(b.t));
        lines.Sort((a, b) => a.t.CompareTo(b.t));
    }

    // who plays what (one part each; the rest dance)
    void Assign(List<GummyBody> players, GummyBody asker)
    {
        var free = new List<GummyBody>(players);
        Take(free, Part.Melody, BuddySpeech.Voice.Duck, BuddySpeech.Voice.Apple, BuddySpeech.Voice.Tomato, BuddySpeech.Voice.Burger, BuddySpeech.Voice.Bean, BuddySpeech.Voice.Dino, BuddySpeech.Voice.Monster);
        Take(free, Part.Bass, BuddySpeech.Voice.Monster, BuddySpeech.Voice.Dino);
        Take(free, Part.Drums, BuddySpeech.Voice.Burger, BuddySpeech.Voice.Dino);
        Take(free, Part.Shaker, BuddySpeech.Voice.Bean);
        Take(free, Part.Chords, BuddySpeech.Voice.Tomato, BuddySpeech.Voice.Apple, BuddySpeech.Voice.Bean, BuddySpeech.Voice.Burger, BuddySpeech.Voice.Duck);
        Take(free, Part.Arp, BuddySpeech.Voice.Apple, BuddySpeech.Voice.Tomato, BuddySpeech.Voice.Bean, BuddySpeech.Voice.Duck, BuddySpeech.Voice.Burger);
        foreach (var b in free) roles[b] = Part.Dance;
    }

    void Take(List<GummyBody> free, Part part, params BuddySpeech.Voice[] prefs)
    {
        foreach (var v in prefs)
            for (int i = 0; i < free.Count; i++)
            {
                if (VoiceOf(free[i]) != v) continue;
                roles[free[i]] = part;
                byPart[part] = free[i];
                free.RemoveAt(i);
                return;
            }
    }

    static BuddySpeech.Voice VoiceOf(GummyBody b)
    {
        var sp = b ? b.GetComponent<BuddySpeech>() : null;
        return sp ? sp.voice : BuddySpeech.Voice.Apple;
    }

    static BuddyBand.Instrument InstOf(GummyBody b) => BuddyBand.Of(VoiceOf(b));

    // the same windows open = the same tempo and chords
    static int LayoutHash()
    {
        int h = 17;
        var w = DesktopPlatforms.Windows;
        var widths = new List<int>();
        for (int i = 0; i < w.Count; i++) widths.Add(Mathf.RoundToInt((w[i].xMax - w[i].xMin) * 4f));
        widths.Sort();
        foreach (int x in widths) h = h * 31 + x;
        return h & 0x7fffffff;
    }

    // ------------------------------------------------------------------ the song

    double T(int bar, float eighth) => t0 + (bar * 8 + eighth) * half;

    int ChordAt(int bar) => bar >= Bars ? 0 : prog[((bar - 4) % 4 + 4) % 4]; // (the groove plays the second half: it leads into the top)

    static int[] ChordPcs(int root) => new[] { root % 12, (root + (root == 9 || root == 2 || root == 4 ? 3 : 4)) % 12, (root + 7) % 12 };

    static List<int> Scale(int lo, int hi)
    {
        var s = new List<int>();
        for (int n = lo; n <= hi; n++) if (System.Array.IndexOf(Penta, ((n % 12) + 12) % 12) >= 0) s.Add(n);
        return s;
    }

    // the scale note closest to idx that's in this chord
    static int Snap(List<int> scale, int idx, int[] pcs)
    {
        for (int d = 0; d < scale.Count; d++)
        {
            int a = idx - d, b = idx + d;
            if (a >= 0 && System.Array.IndexOf(pcs, scale[a] % 12) >= 0) return a;
            if (b < scale.Count && System.Array.IndexOf(pcs, scale[b] % 12) >= 0) return b;
        }
        return Mathf.Clamp(idx, 0, scale.Count - 1);
    }

    void Note(GummyBody who, BuddyBand.Instrument inst, double t, int note, float vel, float look, bool glyph, bool key = false)
    {
        if (!who) return;
        evs.Add(new Ev { t = t, who = who, inst = inst, note = note, vel = vel, look = look, glyph = glyph, key = key });
    }

    void Say(GummyBody who, double t, string text, bool hop = false)
    {
        if (!who || string.IsNullOrEmpty(text)) return;
        lines.Add(new Line { t = t, who = who, text = text, hop = hop });
    }

    void Compose(System.Random rng)
    {
        byPart.TryGetValue(Part.Melody, out GummyBody mel);
        byPart.TryGetValue(Part.Bass, out GummyBody bas);
        byPart.TryGetValue(Part.Drums, out GummyBody drm);
        byPart.TryGetValue(Part.Shaker, out GummyBody shk);
        byPart.TryGetValue(Part.Chords, out GummyBody chd);
        byPart.TryGetValue(Part.Arp, out GummyBody arp);

        // count-in: the coffee bean (or whoever keeps the beat) ONE . TWO . ONE TWO THREE FOUR
        GummyBody counter = shk ? shk : drm ? drm : mel;
        var countInst = shk ? BuddyBand.Instrument.Maraca : BuddyBand.Instrument.Hat;
        foreach (int b in new[] { 0, 2, 4, 5, 6, 7 }) Note(counter, countInst, T(0, b * 2), 0, b < 4 ? 0.8f : 0.9f, 0.8f, false);
        Say(counter, T(0, 0) - 0.05, V(counter, "ONE, TWO...", "A-ONE, A-TWO...", "ONE, TWO...", "ONE, TWO...", "ONE... TWO...", "QUACK, QUACK...", "WE COUNT. ONE. TWO."));
        Say(counter, T(1, 0) - 0.05, V(counter, "1, 2, 3, 4!", "1, 2, 3, 4!", "1-2-3-4! BUZZ!", "1, 2, BUN BUN!", "1, 2, 3, RAWR!", "QUACK 2 3 4!", "1 2 3 4. RAWR"));

        // the groove from bar 2: drums, bass, shaker, chords
        for (int bar = 2; bar < Bars; bar++)
        {
            int root = ChordAt(bar), nextRoot = ChordAt(bar + 1);
            if (drm) Drums(drm, bar);
            if (shk) for (int e = 0; e < 8; e++) Note(shk, BuddyBand.Instrument.Maraca, T(bar, e), 0, e % 2 == 0 ? 0.5f : 0.75f, 0.45f, e % 4 == 3);
            if (bas) Bass(bas, bar, root, nextRoot, rng);
            if (chd) Strums(chd, bar, root);
            if (arp && bar >= 8) Runs(arp, bar, root);
        }
        foreach (var kv in roles)
            if (kv.Value == Part.Dance)
                for (int bar = 2; bar < Bars; bar++) for (int b = 0; b < 4; b++) Note(kv.Key, BuddyBand.Instrument.Hat, T(bar, b * 2), -1, 0f, 0.6f, b == 0 && bar % 2 == 0);

        if (mel) Melody(mel, rng);
        if (bas) Say(bas, T(2, 0), rng.NextDouble() < 0.6 ? V(bas, "", "", "", "", "BASS! RAWR", "", "WE BRING THE BASS") : null);

        // TA-DAA: everyone at once, and up they jump
        double end = T(Bars, 0);
        if (drm) { Note(drm, BuddyBand.Instrument.Kick, end, 0, 1f, 1f, true); if (VoiceOf(drm) == BuddySpeech.Voice.Burger) { Note(drm, BuddyBand.Instrument.Snare, end, 0, 0.9f, 0f, false); Note(drm, BuddyBand.Instrument.Hat, end, 0, 0.8f, 0f, false); } }
        if (shk) Note(shk, BuddyBand.Instrument.Maraca, end, 0, 1f, 1f, true);
        if (bas) Note(bas, InstOf(bas), end, Bass(bas, 0), 1f, 1f, true, true);
        if (chd) Strum(chd, end, 0, 0.75f, 1f);
        if (arp) { var ai = InstOf(arp); Note(arp, ai, end, BuddyBand.BaseOf(ai) + 12, 0.7f, 1f, true, true); }
        int k = 0;
        foreach (var b in roles.Keys)
            Say(b, end + 0.06 * k++, V(b, "TA-DAA!", "NAILED IT!", "ESPRESS-YEAH!", "BUN-BELIEVABLE!", "RAWR-SOME!", "QUACK-TASTIC!", "WE ROCK. RAWR"), true);
    }

    void Drums(GummyBody who, int bar)
    {
        bool burger = VoiceOf(who) == BuddySpeech.Voice.Burger;
        bool fill = bar == Bars - 1 && burger;
        for (int e = 0; e < 8; e++)
        {
            double t = T(bar, e);
            if (e == 0 || e == 4 || (e == 5 && style == 2)) Note(who, BuddyBand.Instrument.Kick, t, 0, e == 5 ? 0.65f : 0.95f, 0.9f, e == 0);
            if (!burger) continue; // (the dino just stomps)
            if (fill && e >= 4) { Note(who, BuddyBand.Instrument.Snare, t, 0, 0.45f + 0.12f * (e - 4), 0.7f, false); continue; }
            if (e == 2 || e == 6) Note(who, BuddyBand.Instrument.Snare, t, 0, 0.8f, 0.6f, false);
            Note(who, BuddyBand.Instrument.Hat, t, 0, e % 2 == 0 ? 0.55f : 0.35f, 0.2f, false);
        }
    }

    // the bass note for a chord root (low C .. A, on the clip's own octave)
    static int Bass(GummyBody who, int root) => BuddyBand.BaseOf(InstOf(who)) + root;

    void Bass(GummyBody who, int bar, int root, int nextRoot, System.Random rng)
    {
        var inst = InstOf(who);
        int r = Bass(who, root), fifth = r + 7 > BuddyBand.BaseOf(inst) + 12 ? r - 5 : r + 7;
        Note(who, inst, T(bar, 0), r, 0.95f, 1f, true, true);
        if (style == 2) { Note(who, inst, T(bar, 3), fifth, 0.75f, 0.7f, false); Note(who, inst, T(bar, 6), r, 0.8f, 0.7f, false); }
        else Note(who, inst, T(bar, 4), rng.NextDouble() < 0.6 ? fifth : r, 0.8f, 0.8f, true, true);
        // walking into the next chord (from the second time round)
        if (bar >= 8 && nextRoot != root && rng.NextDouble() < 0.6)
        {
            int to = Bass(who, nextRoot);
            Note(who, inst, T(bar, 7), to + (to > r ? -1 : 1), 0.6f, 0.4f, false);
        }
    }

    // the chord on this player's instrument, around its clip's note
    int[] Voicing(GummyBody who, int root)
    {
        int b = BuddyBand.BaseOf(InstOf(who));
        int[] pcs = ChordPcs(root);
        var notes = new int[3];
        for (int i = 0; i < 3; i++)
        {
            int best = 0, bd = 99;
            for (int n = b - 5; n <= b + 9; n++) { if (((n % 12) + 12) % 12 != pcs[i]) continue; int d = Mathf.Abs(n - (b + 2)); if (d < bd) { bd = d; best = n; } }
            notes[i] = best;
        }
        System.Array.Sort(notes);
        return notes;
    }

    void Strum(GummyBody who, double t, int root, float vel, float look)
    {
        var inst = InstOf(who);
        var v = Voicing(who, root);
        for (int i = 0; i < v.Length; i++) Note(who, inst, t + 0.014 * i, v[i], vel * (i == 0 ? 1f : 0.85f), i == 0 ? look : 0f, i == 0, i == 0);
    }

    void Strums(GummyBody who, int bar, int root)
    {
        if (style == 0) { for (int e = 1; e < 8; e += 2) Strum(who, T(bar, e), root, 0.5f, 0.7f); }
        else if (style == 1) { for (int e = 0; e < 8; e += 2) Strum(who, T(bar, e), root, e % 4 == 0 ? 0.55f : 0.42f, 0.7f); }
        else { foreach (int e in new[] { 0, 3, 6 }) Strum(who, T(bar, e), root, 0.55f, 0.7f); }
    }

    // sparkly runs up and down the chord
    void Runs(GummyBody who, int bar, int root)
    {
        var inst = InstOf(who);
        int b = BuddyBand.BaseOf(inst);
        var tones = new List<int>();
        int[] pcs = ChordPcs(root);
        for (int n = b; n <= b + 12; n++) if (System.Array.IndexOf(pcs, ((n % 12) + 12) % 12) >= 0) tones.Add(n);
        int[] pat = { 0, 1, 2, 3, 2, 1, 2, 1 };
        for (int e = 0; e < 8; e++) Note(who, inst, T(bar, e), tones[Mathf.Min(pat[e], tones.Count - 1)], e % 2 == 0 ? 0.42f : 0.32f, 0.35f, e == 0);
    }

    // the tune: a two-bar idea (or yours), twice; then a step higher, then home; then a solo
    void Melody(GummyBody who, System.Random rng)
    {
        var inst = InstOf(who);
        int b = BuddyBand.BaseOf(inst);
        var scale = Scale(b - 5, b + 16); // (G below .. E above the next C: every window note fits)
        string rhythm = Rhythms[rng.Next(Rhythms.Length)];
        var onsets = new List<int>();
        for (int e = 0; e < 16; e++) if (rhythm[e] == 'x') onsets.Add(e);

        // the idea, as places on the scale
        var idea = new List<int>();
        if (yourTune)
        {
            var tune = BuddyBand.Tune;
            for (int i = 0; i < onsets.Count; i++)
            {
                int n = b + tune[i % tune.Count];
                while (n > b + 16) n -= 12;
                while (n < b - 5) n += 12;
                int idx = scale.IndexOf(n);
                idea.Add(idx >= 0 ? idx : Snap(scale, scale.Count / 2, new[] { ((n % 12) + 12) % 12 }));
            }
        }
        else
        {
            int idx = scale.Count / 2 - 1 + rng.Next(3);
            int[] walk = { -2, -1, -1, 0, 1, 1, 2 };
            for (int i = 0; i < onsets.Count; i++)
            {
                if (i > 0) idx = Mathf.Clamp(idx + walk[rng.Next(walk.Length)], 1, scale.Count - 2);
                idea.Add(idx);
            }
        }

        // A: the idea twice (strong beats fit the chords, unless it's your tune: that one's played as you played it)
        Phrase(who, inst, scale, onsets, idea, 4, 0, !yourTune);
        Phrase(who, inst, scale, onsets, idea, 6, 0, !yourTune);
        // A': a step higher, then the idea again ending at home
        Phrase(who, inst, scale, onsets, idea, 8, 1, true);
        Phrase(who, inst, scale, onsets, idea, 10, 0, true, true);
        // B: the solo
        int s = Mathf.Clamp(scale.Count / 2 + 1, 0, scale.Count - 1);
        for (int bar = 12; bar < Bars; bar++)
        {
            int[] pcs = ChordPcs(ChordAt(bar));
            for (int e = 0; e < 8; e++)
            {
                double p = e % 4 == 0 ? 0.9 : e % 2 == 0 ? 0.7 : 0.45;
                if (rng.NextDouble() > p) continue;
                int dir = bar < 14 ? 1 : -1;
                s = Mathf.Clamp(s + (rng.NextDouble() < 0.7 ? dir : -dir) * (rng.NextDouble() < 0.25 ? 2 : 1), 0, scale.Count - 1);
                if (s >= scale.Count - 1 || s <= 0) s = scale.Count / 2;
                int idx = e % 4 == 0 ? Snap(scale, s, pcs) : s;
                Note(who, inst, T(bar, e), scale[idx], e % 4 == 0 ? 0.85f : 0.7f, 0.9f, true, e % 4 == 0);
            }
        }
        // and home: the C above
        int home = b + 12;
        Note(who, inst, T(Bars, 0), home, 1f, 1f, true, true);

        bool duck = VoiceOf(who) == BuddySpeech.Voice.Duck;
        Say(who, T(4, 0), yourTune ? (duck ? "YOUR SONG! QUACK!" : "WE LEARNED YOUR SONG!")
            : V(who, "LA LA LAAA ♪", "TOMATO ON LEAD!", "JAZZ BEAN ♪", "BUN-BOP ♪", "RAWR-OCK ♪", "QUACK QUACK QUAAACK ♪", "WE SING NOW"));
        Say(who, T(12, 0), V(who, "SOLO!", "TOMATO SOLO!", "BEAN SOLO! BZZ", "BUN SOLO!", "DINO SOLO!", "QUACK SOLO!", "TUBA SOLO. RAWR"));
    }

    void Phrase(GummyBody who, BuddyBand.Instrument inst, List<int> scale, List<int> onsets, List<int> idea, int bar0, int shift, bool fit, bool home = false)
    {
        for (int i = 0; i < onsets.Count; i++)
        {
            int e = onsets[i], bar = bar0 + e / 8;
            int idx = Mathf.Clamp(idea[i] + shift, 0, scale.Count - 1);
            int[] pcs = ChordPcs(ChordAt(bar));
            bool last = i == onsets.Count - 1;
            if (last && home) idx = Snap(scale, idx, new[] { ChordAt(bar) % 12 });
            else if (fit && (e % 4 == 0 || last)) idx = Snap(scale, idx, pcs);
            Note(who, inst, T(bar, e % 8), scale[idx], e % 4 == 0 ? 0.9f : 0.75f, 1f, true, e % 8 == 0);
        }
    }

    static string Roman(int root) => root == 0 ? "I" : root == 5 ? "IV" : root == 7 ? "V" : root == 9 ? "vi" : root.ToString();

    static string V(GummyBody b, string apple, string tomato, string bean, string burger, string dino, string duck, string monster)
    {
        switch (VoiceOf(b))
        {
            case BuddySpeech.Voice.Tomato: return tomato;
            case BuddySpeech.Voice.Bean: return bean;
            case BuddySpeech.Voice.Burger: return burger;
            case BuddySpeech.Voice.Dino: return dino;
            case BuddySpeech.Voice.Duck: return duck;
            case BuddySpeech.Voice.Monster: return monster;
            default: return apple;
        }
    }

    // ------------------------------------------------------------------ playing it

    /// <summary>Can it play right now (here, awake, not in your hand, not inside a monster)?</summary>
    public bool Playing(GummyBody b)
    {
        if (!b || !b.isActiveAndEnabled || b.Held || !roles.ContainsKey(b)) return false;
        var br = b.GetComponent<GummyBrain>();
        return !br || br.Current != GummyBrain.State.Asleep;
    }

    public bool Plays(GummyBody b) => b && roles.ContainsKey(b);

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("BandJam.Update", t0); }
    void UpdateTimed()
    {
        if (ended) return;
        double now = AudioSettings.dspTime;
        float t = Time.time;

        // a bit ahead on the audio clock, so every note is right on time
        while (nextEv < evs.Count && evs[nextEv].t < now + Lookahead)
        {
            Ev e = evs[nextEv++];
            if (e.note >= 0 && Playing(e.who)) Schedule(e);
        }
        // what you see, when you hear it
        while (nextLook < evs.Count && evs[nextLook].t <= now)
        {
            Ev e = evs[nextLook++];
            if (e.look <= 0f || !Playing(e.who)) continue;
            var band = e.who.GetComponent<BuddyBand>();
            if (band) band.Show(e.look, e.glyph, e.key);
        }
        while (nextLine < lines.Count && lines[nextLine].t <= now)
        {
            Line l = lines[nextLine++];
            if (!Playing(l.who)) continue;
            var sp = l.who.GetComponent<BuddySpeech>();
            if (sp) sp.Say(l.text, 0f, true);
            if (l.hop && l.who.Grounded) l.who.Hop(new Vector2(0f, GummyBody.JumpSpeed(0.35f * l.who.size / 0.6f, l.who.gravity, l.who.Drag)), 0.6f);
        }

        // the players stay put (no visits or chit-chat mid-song); grabbed: it stops
        BuddySocial.QuietUntil = Mathf.Max(BuddySocial.QuietUntil, t + 1f);
        bool anyone = false;
        foreach (var kv in roles)
        {
            GummyBody b = kv.Key;
            if (!b || !b.isActiveAndEnabled) continue;
            if (b.Held && complained.Add(b))
            {
                var sp = b.GetComponent<BuddySpeech>();
                if (sp && now < T(Bars, 0)) sp.Say(V(b, "HEY! I WAS PLAYING!", "MY SOLO!!", "I WAS IN THE GROOVE!", "PUT ME DOWN, I'M THE BEAT!", "RAWR! THE BASS!", "MY SONG! QUACK!", "PUT US DOWN. WE PLAY"), 0f, true);
            }
            if (!Playing(b)) continue;
            anyone = true;
            var br = b.GetComponent<GummyBrain>();
            if (br) br.Linger(1.5f);
        }
        if (anyone) aloneSince = -1f;
        else if (aloneSince < 0f) aloneSince = t;
        else if (t - aloneSince > 3f) { End(false); return; } // (nobody left to play)

        if (now >= tEnd || t - startTime > Length + 6f) End(true);
    }

    void Schedule(Ev e)
    {
        AudioClip clip = BuddyBand.ClipFor(e.inst);
        roles.TryGetValue(e.who, out Part part);
        float vol = Mathf.Clamp01(master * e.vel * BuddyBand.GainOf(e.inst) * PartGain[(int)part]);
        didPlay.Add(e.who);
        played++;
        Noted?.Invoke(e.t, e.who, e.inst, e.note, vol);
        if (!clip) return;
        AudioSource src = pool[nextVoice];
        nextVoice = (nextVoice + 1) % Voices;
        src.clip = clip;
        src.pitch = BuddyBand.PitchFor(e.inst, e.note);
        src.volume = vol;
        src.panStereo = BuddyBand.PanAt(e.who.Center.x, 0.6f);
        src.PlayScheduled(e.t);
    }

    /// <summary>Stop now (the band was muted, or the chat said stop).</summary>
    public void Stop() => End(false);

    void End(bool finished)
    {
        if (ended) return;
        ended = true;
        Finished = finished;
        if (finished)
        {
            foreach (var b in didPlay)
            {
                if (!b || !b.isActiveAndEnabled) continue;
                var p = b.GetComponent<BuddyProgress>();
                if (p) p.AddXp(1, b.Center);
                Played?.Invoke(b);
            }
            if (Random.value < 0.4f)
                foreach (var b in didPlay) { var sp = b ? b.GetComponent<BuddySpeech>() : null; if (sp && b.isActiveAndEnabled) { sp.Say(V(b, "ENCORE?", "AGAIN! AGAIN!", "ONE MORE! BZZ", "THAT WAS BUN-DERFUL", "RAWR! AGAIN!", "AGAIN! QUACK!", "MORE. MUSIC."), 0f, false); break; } }
        }
        else if (pool != null) foreach (var s in pool) if (s) s.Stop();
        if (Current == this) Current = null;
        Destroy(gameObject);
        enabled = false;
    }

    void OnDestroy()
    {
        if (Current == this) Current = null;
    }
}
