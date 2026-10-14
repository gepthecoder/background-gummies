using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Everything the Editor's WhipIt > Buddy menu does, for the built app: the buttons in a window a buddy broke into
/// (WindowInfestation). Each one does its thing right away where it can; changing levels down (and resetting to a seed)
/// can't happen while they're running (they'd save over it), so those wait for the next time WhipIt starts
/// (PendingSave). Each returns a few words for the panel ("RAIN!", "NOT NOW: BEDTIME").
/// </summary>
public static class BuddyActions
{
    /// <summary>Where it's done from: the buddy in the window, a spot on the floor beside the window (for the hoop, the
    /// ball pit, the slide) and a point above it (snacks drop there).</summary>
    public struct Ctx
    {
        public GummyBody inside;
        public Vector2 beside, above;
    }

    public class Act
    {
        public int tab;
        public Func<string> label;
        public Func<Ctx, string> run;
        /// <summary>Asks "SURE?" first (a second tap does it).</summary>
        public bool confirm;
    }

    public const int TabMe = 0, TabPlay = 1, TabToys = 2, TabWorld = 3, TabGrow = 4;
    public static readonly string[] Tabs = { "ME", "PLAY", "TOYS", "WORLD", "GROW" };

    static List<Act> s_all;
    public static List<Act> All => s_all ?? (s_all = Build());

    public static List<Act> InTab(int tab)
    {
        var l = new List<Act>();
        foreach (var a in All) if (a.tab == tab) l.Add(a);
        return l;
    }

    static Act A(int tab, string label, Func<Ctx, string> run, bool confirm = false) => new Act { tab = tab, label = () => label, run = run, confirm = confirm };
    static Act A(int tab, Func<string> label, Func<Ctx, string> run) => new Act { tab = tab, label = label, run = run };

    static List<Act> Build()
    {
        return new List<Act>
        {
            // ---------------------------------------------------------------- play
            A(TabPlay, "THROW THE BALL", c =>
            {
                var d = UnityEngine.Object.FindFirstObjectByType<SnackDropper>();
                Rect r = ScreenBounds.ScreenRectAt(c.above);
                FetchBall.Spawn(new Vector2(Mathf.Clamp(c.above.x, r.xMin + 1f, r.xMax - 1f), r.yMax - 0.3f), new Vector2(UnityEngine.Random.Range(-6f, 6f), UnityEngine.Random.Range(0f, 3f)), d ? d.material : null, d ? d.sortingOrder : 8, true);
                return "BALL!";
            }),
            A(TabPlay, "THROW A PARTY", c =>
            {
                var host = Outside(c.inside);
                if (!host) return "NOBODY OUT THERE";
                var p = host.GetComponent<BuddyParty>();
                var prog = host.GetComponent<BuddyProgress>();
                LevelUpParty.Throw(host, prog ? prog.Level : 1, p ? p.tune : null, p ? p.volume : 0.5f, p && p.material ? p.material : host.spriteMaterial);
                return "PARTY!";
            }),
            A(TabPlay, "BUILD A TOWER", c =>
            {
                foreach (var b in GummyBody.All) if (Free(b, c.inside) && BuddyTower.Show(b)) return "TOWER TIME!";
                return "NOT NOW: NEEDS 2 FREE";
            }),
            A(TabPlay, "HIDE AND SEEK", c => BuddyHideSeek.StartGame() > 0 ? "READY OR NOT!" : "NOWHERE TO HIDE"),
            A(TabPlay, "PICKUP GAME", c =>
            {
                if (!Hoop.Current) Hoop.Put(Mat(c), c.beside);
                foreach (var h in BuddyHoops.All) if (h && !In(h.GetComponent<GummyBody>()) && PickupGame.TryStart(h, true)) return "GAME ON!";
                return "NOT NOW: NEEDS 2 FREE";
            }),
            A(TabPlay, "START A JAM", c => BandJam.Current ? "JAMMING ALREADY" : BandJam.Begin(null, true) ? "ROCK ON!" : "NOBODY CAN PLAY"),
            A(TabPlay, () => PinballTable.Playing ? "STOP PINBALL" : "DESKTOP PINBALL", c =>
            {
                var pt = PinballTable.Current;
                if (!pt) return "NO TABLE";
                if (PinballTable.Playing) { pt.Close(); return "TABLE AWAY"; }
                return pt.Begin() ? "SHIFT FLIPS, SPACE LAUNCHES" : "NOBODY TO PLAY";
            }),
            A(TabPlay, "LASSO MY CURSOR", c =>
            {
                int n = 0;
                foreach (var b in GummyBody.All) { var rp = b ? b.GetComponent<BuddyRope>() : null; if (rp && !In(b)) { rp.LassoSoon(10f); n++; } }
                return n > 0 ? "HOVER OVER ONE (10 S)" : "NO ROPES (LEVEL 11)";
            }),
            A(TabPlay, () => LaserPointer.On ? "LASER OFF" : "LASER POINTER", c =>
            {
                var l = LaserPointer.Current ? LaserPointer.Current : UnityEngine.Object.FindFirstObjectByType<LaserPointer>();
                if (!l) return "NO LASER";
                l.Toggle();
                return LaserPointer.On ? "PEW PEW" : "LASER AWAY";
            }),
            A(TabPlay, "TRY EVERY TRICK", c =>
            {
                int n = 0;
                foreach (var b in GummyBody.All) { var sk = b ? b.GetComponent<BuddySkills>() : null; if (sk) { sk.levelOverride = 17; n++; } }
                return n + " ACT LIKE LEVEL 17";
            }),
            A(TabPlay, "GIVE THE WHIP", c =>
            {
                var w = UnityEngine.Object.FindFirstObjectByType<BuddyWhip>();
                if (!w) return "NO WHIP";
                w.Give(true);
                return "CRACK!";
            }),
            A(TabPlay, "FUSE TWO", c => Fuse(c.inside)),
            A(TabPlay, "SPLIT MONSTERS", c =>
            {
                int n = BuddyMonster.All.Count;
                BuddyMonster.SplitAll();
                return n > 0 ? "SPLIT!" : "NO MONSTERS";
            }),
            A(TabPlay, "GO WILD", c => CaughtInTheAct.Current && CaughtInTheAct.Current.StartNow(6f) ? "HANDS OFF 6 S..." : "NOT NOW"),

            // ---------------------------------------------------------------- toys
            A(TabToys, () => Hoop.Current ? "HOOP AWAY" : "PUT UP THE HOOP", c =>
            {
                if (Hoop.Current) { Hoop.TakeDown(); return "HOOP AWAY"; }
                Hoop.Put(Mat(c), c.beside);
                return "HOOP UP!";
            }),
            A(TabToys, () => BallPit.Current ? "BALL PIT AWAY" : "BALL PIT", c =>
            {
                if (BallPit.Current) { BallPit.TakeDown(); return "PUT AWAY"; }
                Playground.PutPit(Mat(c), c.beside);
                return "BALLS!";
            }),
            A(TabToys, () => Slide.Current ? "SLIDE AWAY" : "PUT UP THE SLIDE", c =>
            {
                if (Slide.Current) { Slide.TakeDown(); return "SLIDE DOWN"; }
                Playground.PutSlide(Mat(c), c.beside);
                return "WHEEE!";
            }),
            A(TabToys, "DROP A SNACK", c =>
            {
                var d = UnityEngine.Object.FindFirstObjectByType<SnackDropper>();
                return d && d.Drop(true, null, c.above.x) ? "SNACK!" : "NO SNACKS";
            }),
            A(TabToys, () => GiantPotion.Current && GiantPotion.Current.Giant ? "POP THE GIANT" : "GIANT POTION", c =>
            {
                var g = GiantPotion.Current;
                if (!g) return "NO POTIONS";
                if (g.Giant) { g.PopNow(); return "POP!"; }
                return g.DropFor(c.inside) ? "GLUG GLUG?" : "NOT NOW";
            }),
            A(TabToys, "CANDY CLOUD", c =>
            {
                if (!CandyCloud.Instance) return "NO CLOUDS";
                CandyCloud.Instance.Shower();
                return "SUGAR!";
            }),
            A(TabToys, () => Trophies.Current && Trophies.Current.Showing ? "SHELF AWAY" : "TROPHY SHELF", c =>
            {
                var t = Trophies.Current;
                if (!t) return "NO SHELF";
                if (t.Showing) { t.Hide(); return "PUT AWAY"; }
                return t.Show(Outside(c.inside)) ? "HALL OF FAME!" : "NOBODY TO SHOW";
            }),

            // ---------------------------------------------------------------- the world
            A(TabWorld, () => Rain.Current && Rain.Current.Raining ? "STOP THE RAIN" : "MAKE IT RAIN", c =>
            {
                if (!Rain.Current) return "NO CLOUDS";
                Rain.Current.Toggle();
                return Rain.Current.Raining ? "RAIN!" : "SUNSHINE";
            }),
            A(TabWorld, () => Lava.Current && Lava.Current.On ? "COOL THE LAVA" : "FLOOR IS LAVA", c =>
            {
                var l = Lava.Current;
                if (!l) return "NO LAVA";
                if (l.On) { l.Stop(); return "COOLING..."; }
                string why = Lava.Blocked();
                if (why != null) return "NOT NOW: " + why.ToUpperInvariant();
                return l.Begin() ? "THE FLOOR IS LAVA!" : "NOBODY OUT THERE";
            }),
            A(TabWorld, () => Bedtime.Sleeping ? "GOOD MORNING" : "BEDTIME", c =>
            {
                var b = Bedtime.Instance;
                if (!b) return "NO BEDTIME";
                if (Bedtime.Sleeping) { b.Morning(); return "RISE AND SHINE"; }
                b.Night();
                return "PILLOW FORT!";
            }),
            A(TabWorld, () => BulletTime.Stopped ? "LET TIME GO" : "FREEZE TIME", c =>
            {
                var t = BulletTime.Current;
                if (!t) return "NO CLOCK";
                t.Toggle();
                return BulletTime.Stopped ? "TIME STOPPED" : "SLOW MOTION";
            }),
            A(TabWorld, "WORKED AN HOUR", c =>
            {
                var w = WorkClock.Instance;
                if (!w) return "NO CLOCK";
                w.AddStreak(3600f);
                return "+1 HOUR";
            }),

            // ---------------------------------------------------------------- growing up
            A(TabGrow, "PLANT TOMATO", c => Friends(f => f.PlantNow(), "SEED PLANTED!", "ALREADY HERE")),
            A(TabGrow, "PLANT COFFEE BEAN", c => Friends(f => f.PlantBeanNow(), "SEED PLANTED!", "ALREADY HERE")),
            A(TabGrow, "PLANT BURGER", c => Friends(f => f.PlantBurgerNow(), "SEED PLANTED!", "ALREADY HERE")),
            A(TabGrow, "HATCH DINO", c => Friends(f => f.PlantDinoNow(), "AN EGG!", "ALREADY HERE")),
            A(TabGrow, "DUCK ARRIVES", c => Friends(f => f.PlantDuckNow(), "QUACK!", "ALREADY HERE")),
            A(TabGrow, "+1 LEVEL", c =>
            {
                var p = c.inside ? c.inside.GetComponent<BuddyProgress>() : null;
                if (!p) return "NO LEVELS";
                p.AddXp(Mathf.Max(1, p.XpForNext - p.Xp), c.inside.Center);
                return "LEVEL " + p.Level + "!";
            }),
            A(TabGrow, "ONE SNACK TO GO", c =>
            {
                var p = c.inside ? c.inside.GetComponent<BuddyProgress>() : null;
                if (!p) return "NO LEVELS";
                int want = p.XpForNext - p.Xp - 3;
                if (want > 0) p.AddXp(want, c.inside.Center);
                return "FEED ME (CTRL+C)";
            }),
            A(TabGrow, "APPLE TO LEVEL 10", c => AppleTo(10, 0, false)),
            A(TabGrow, "1 SNACK: TOMATO", c => AppleTo(4, -3, true)),
            A(TabGrow, "1 SNACK: BEAN", c => AppleTo(9, -3, false, true)),
            A(TabGrow, "1 SNACK: BURGER", c => AppleTo(14, -3, false)),
            A(TabGrow, "1 SNACK: DINO EGG", c => AppleTo(19, -3, false)),
            A(TabGrow, "1 SNACK: DUCK", c => AppleTo(24, -3, false)),
            A(TabGrow, "RESET TO SEED", c =>
            {
                PendingSave.Request(1, 0, true, true);
                return "SEED AT NEXT START";
            }, true),
        };
    }

    // ------------------------------------------------------------------ helpers

    static bool In(GummyBody b) => b && b.InRoom;
    static bool Free(GummyBody b, GummyBody inside) => b && b != inside && b.isActiveAndEnabled && !b.InRoom;

    /// <summary>Someone out in the world (not in a window): the apple first.</summary>
    static GummyBody Outside(GummyBody inside)
    {
        GummyBody any = null;
        foreach (var b in GummyBody.All)
        {
            if (!Free(b, inside)) continue;
            var p = b.GetComponent<BuddyProgress>();
            if (p && p.saveName == "buddy_apple") return b;
            if (!any) any = b;
        }
        return any;
    }

    static Material Mat(Ctx c)
    {
        if (c.inside && c.inside.spriteMaterial) return c.inside.spriteMaterial;
        foreach (var b in GummyBody.All) if (b && b.spriteMaterial) return b.spriteMaterial;
        return null;
    }

    static string Friends(Func<BuddyFriends, bool> plant, string yes, string no)
    {
        var f = UnityEngine.Object.FindFirstObjectByType<BuddyFriends>();
        if (!f) return "NO GARDEN";
        return plant(f) ? yes : no;
    }

    // the apple to this level (xp < 0: that many XP short of the next one): up right now; down only at the next start
    static string AppleTo(int level, int xp, bool unbornTomato, bool unbornBean = false)
    {
        BuddyProgress apple = null;
        foreach (var b in GummyBody.All) { var p = b ? b.GetComponent<BuddyProgress>() : null; if (p && p.saveName == "buddy_apple") { apple = p; break; } }
        if (!apple) return "NO APPLE";
        if (apple.Level > level || (apple.Level == level && xp < 0 && apple.XpForNext - apple.Xp < -xp))
        {
            int baseXp = apple.baseXpPerLevel, extra = apple.extraXpPerLevel;
            int x = xp < 0 ? baseXp + extra * (level - 1) + xp : xp;
            PendingSave.Request(level, x, unbornTomato, unbornBean);
            return "LEVEL " + level + " AT NEXT START";
        }
        // up: XP until it's there (whatever it unlocks on the way happens, like the real thing)
        int need = 0, lv = apple.Level, have = apple.Xp;
        while (lv < level) { need += apple.baseXpPerLevel + apple.extraXpPerLevel * (lv - 1) - have; have = 0; lv++; }
        if (xp < 0) need += apple.baseXpPerLevel + apple.extraXpPerLevel * (level - 1) + xp - have;
        else need += xp;
        if (need > 0) apple.AddXp(need, apple.GetComponent<GummyBody>().Center);
        return "APPLE LEVEL " + apple.Level + "!";
    }

    // the two nearest free buddies (or the nearest one into the monster)
    static string Fuse(GummyBody inside)
    {
        var free = new List<GummyBody>();
        foreach (var b in GummyBody.All) if (Free(b, inside) && !b.IsMonster && b.GetComponent<BuddyFusion>()) free.Add(b);
        GummyBody into = null, incoming = null;
        float best = float.MaxValue;
        if (BuddyMonster.All.Count > 0)
        {
            into = BuddyMonster.All[0].GetComponent<GummyBody>();
            foreach (var b in free) { float d = (b.Center - into.Center).sqrMagnitude; if (d < best) { best = d; incoming = b; } }
        }
        else
            for (int i = 0; i < free.Count; i++)
                for (int j = i + 1; j < free.Count; j++)
                {
                    float d = (free[i].Center - free[j].Center).sqrMagnitude;
                    if (d < best) { best = d; into = free[i]; incoming = free[j]; }
                }
        var m = incoming ? BuddyMonster.Fuse(incoming, into) : null;
        return m ? m.MonsterName + "!" : "NOTHING TO FUSE";
    }
}

/// <summary>
/// A level change that can't happen while WhipIt runs (down, or a reset to a seed: the running buddies would save over
/// it): written to a little file now, done the next time WhipIt starts, before the buddies load their saves.
/// </summary>
public static class PendingSave
{
    public static string FilePath => System.IO.Path.Combine(Application.persistentDataPath, "pending_level.txt");

    public static void Request(int level, int xp, bool unbornTomato, bool unbornBean)
    {
        try { System.IO.File.WriteAllText(FilePath, level + " " + xp + " " + (unbornTomato ? 1 : 0) + " " + (unbornBean ? 1 : 0)); }
        catch (Exception e) { Debug.LogWarning("[WhipIt] Couldn't write " + FilePath + ": " + e.Message); }
    }

    /// <summary>At the start (before the scene): if there's one waiting, do it (the apple's save, friends not born yet).</summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    public static void Apply()
    {
        try
        {
            if (!System.IO.File.Exists(FilePath)) return;
            string[] f = System.IO.File.ReadAllText(FilePath).Trim().Split(' ');
            System.IO.File.Delete(FilePath);
            if (f.Length < 4 || !int.TryParse(f[0], out int level) || !int.TryParse(f[1], out int xp)) return;
            bool unbornTomato = f[2] == "1", unbornBean = f[3] == "1";
            const int baseXp = 20, extra = 15; // (BuddyProgress defaults)
            int total = xp;
            for (int l = 1; l < level; l++) total += baseXp + extra * (l - 1);
            BuddyProgress.WriteSave(level, xp, total);
            Delete("buddy_tomato", unbornTomato);
            Delete("buddy_bean", unbornTomato || unbornBean);
            Delete("buddy_burger", level < 15);
            Delete("buddy_dino", level < 20);
            Delete("buddy_duck", level < 25);
        }
        catch (Exception e) { Debug.LogWarning("[WhipIt] Couldn't apply " + FilePath + ": " + e.Message); }
    }

    static void Delete(string save, bool when)
    {
        if (!when) return;
        string p = BuddyProgress.PathFor(save);
        if (System.IO.File.Exists(p)) System.IO.File.Delete(p);
    }
}
