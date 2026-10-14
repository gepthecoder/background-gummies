using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

/// <summary>
/// One-click setup for the WhipIt desktop overlay.
///   WhipIt > Setup Scene + Project Settings : camera, whip, material, player + URP settings
///   WhipIt > Build And Run (Windows)       : builds Builds/WhipIt.exe and launches it
///   WhipIt > Buddy > ...                   : reset to a seed / skip to the apple / one snack before the tomato /
///                                            one snack before the coffee bean / plant the tomato / the coffee bean now /
///                                            drop a snack / show the save file / play hide and seek /
///                                            give the apple the whip / pretend you worked an hour (steam)
/// In the app: Ctrl+Shift+Space talks to the buddy nearest the cursor, Ctrl+Shift+W hides the whip, Ctrl+Shift+Q quits.
/// </summary>
public static class WhipItSetup
{
    const string Root = "Assets/WhipIt";
    const string MaterialPath = Root + "/Whip.mat";
    const string SwishPath = Root + "/Audio/whip_swish.wav";
    const string CrackPath = Root + "/Audio/whip_crack.wav";
    const string BuildPath = "Builds/WhipIt.exe";
    const string GummyShaderPath = Root + "/Shaders/Gummy.shader";
    const string GummyAudio = Root + "/Audio/";

    [MenuItem("WhipIt/Setup Scene + Project Settings", priority = 0)]
    public static void Setup()
    {
        ApplyPlayerSettings();
        int urpCount = ApplyUrpSettings();
        var mat = GetOrCreateMaterial();
        var cam = SetupCamera();
        SetupWhip(cam, mat);
        SetupInteraction(cam, mat);

        var scene = SceneManager.GetActiveScene();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        EnsureSceneInBuild(scene.path);
        AssetDatabase.SaveAssets();

        Debug.Log($"[WhipIt] Setup done (scene '{scene.name}', {urpCount} URP asset(s) set to HDR off). " +
                  "Press Play to try it in the Game view, or WhipIt > Build And Run (Windows) for the transparent overlay.");
    }

    [MenuItem("WhipIt/Build And Run (Windows)", priority = 1)]
    public static void BuildAndRun()
    {
        var scenes = new List<string>();
        foreach (var s in EditorBuildSettings.scenes) if (s.enabled) scenes.Add(s.path);
        if (scenes.Count == 0) scenes.Add(SceneManager.GetActiveScene().path);

        Directory.CreateDirectory(Path.GetDirectoryName(BuildPath));
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = scenes.ToArray(),
            locationPathName = BuildPath,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.AutoRunPlayer
        });
        Debug.Log($"[WhipIt] Build {report.summary.result}: {Path.GetFullPath(BuildPath)}  " +
                  "(Ctrl+Shift+W hide/show whip, Ctrl+Shift+Q quit)");
    }

    // ------------------------------------------------------------------

    static void ApplyPlayerSettings()
    {
        // a normal window that TransparentWindow makes borderless and as big as all your screens together
        PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
        PlayerSettings.defaultIsNativeResolution = false;
        PlayerSettings.defaultScreenWidth = 1280;
        PlayerSettings.defaultScreenHeight = 720;
        PlayerSettings.runInBackground = true;
        PlayerSettings.visibleInBackground = true;
        PlayerSettings.resizableWindow = true; // (so the player follows when it's sized over all of them)
        PlayerSettings.allowFullscreenSwitch = false;
        PlayerSettings.forceSingleInstance = true;
        PlayerSettings.SplashScreen.show = false;
        PlayerSettings.enableFrameTimingStats = true; // (whipit_perf.txt: main thread / render thread / GPU times)

        // Per-pixel window transparency needs the D3D11 blit-model swapchain.
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D11 });
        SetSerialized("ProjectSettings/ProjectSettings.asset", "useFlipModelSwapchain", false);
    }

    static int ApplyUrpSettings()
    {
        // HDR color buffers have no alpha channel, so HDR must be off for a see-through background.
        var assets = new HashSet<RenderPipelineAsset>();
        if (GraphicsSettings.defaultRenderPipeline) assets.Add(GraphicsSettings.defaultRenderPipeline);
        for (int i = 0; i < QualitySettings.names.Length; i++)
        {
            var a = QualitySettings.GetRenderPipelineAssetAt(i);
            if (a) assets.Add(a);
        }
        int count = 0;
        foreach (var a in assets)
        {
            var so = new SerializedObject(a);
            var hdr = so.FindProperty("m_SupportsHDR");
            if (hdr == null) continue;
            hdr.boolValue = false;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(a);
            count++;
        }
        return count;
    }

    static Material GetOrCreateMaterial()
    {
        var mat = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        if (mat) return mat;
        var shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default");
        if (!shader) shader = Shader.Find("Sprites/Default");
        mat = new Material(shader) { name = "Whip" };
        AssetDatabase.CreateAsset(mat, MaterialPath);
        return mat;
    }

    static Camera SetupCamera()
    {
        var cam = Camera.main;
        if (!cam) cam = Object.FindFirstObjectByType<Camera>();
        if (!cam)
        {
            var go = new GameObject("Main Camera") { tag = "MainCamera" };
            Undo.RegisterCreatedObjectUndo(go, "WhipIt Camera");
            cam = go.AddComponent<Camera>();
            go.AddComponent<AudioListener>();
        }
        Undo.RecordObject(cam, "WhipIt Camera");
        Undo.RecordObject(cam.transform, "WhipIt Camera");
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
        cam.orthographic = true;
        cam.orthographicSize = 5f;
        cam.allowHDR = false;
        cam.allowMSAA = false;
        cam.transform.SetPositionAndRotation(new Vector3(0f, 0f, -10f), Quaternion.identity);
        if (!cam.GetComponent<AudioListener>()) Undo.AddComponent<AudioListener>(cam.gameObject);

        // URP camera data: post-processing off (it would drop the alpha channel).
        var urpData = cam.GetComponent("UniversalAdditionalCameraData");
        if (urpData)
        {
            var so = new SerializedObject(urpData);
            var pp = so.FindProperty("m_RenderPostProcessing");
            if (pp != null) { pp.boolValue = false; so.ApplyModifiedProperties(); }
        }
        return cam;
    }

    static void SetupWhip(Camera cam, Material mat)
    {
        var whip = Object.FindFirstObjectByType<Whip>(FindObjectsInactive.Include); // include the parked (inactive) whip
        if (!whip)
        {
            var go = new GameObject("Whip");
            Undo.RegisterCreatedObjectUndo(go, "WhipIt Whip");
            whip = go.AddComponent<Whip>();
        }
        Undo.RecordObject(whip, "WhipIt Whip");
        whip.cam = cam;
        whip.material = mat;
        whip.swishClip = AssetDatabase.LoadAssetAtPath<AudioClip>(SwishPath);
        whip.crackClip = AssetDatabase.LoadAssetAtPath<AudioClip>(CrackPath);
        if (!whip.swishClip || !whip.crackClip) Debug.LogWarning("[WhipIt] Sound clips not found under " + Root + "/Audio.");
        EditorUtility.SetDirty(whip);

        // Parked: the whip no longer follows the mouse. It stays in the scene (inactive) as the attachment
        // the buddies will wield later.
        Undo.RecordObject(whip.gameObject, "WhipIt Whip");
        whip.gameObject.SetActive(false);

        if (!Object.FindFirstObjectByType<TransparentWindow>())
        {
            var go = new GameObject("Overlay Window");
            Undo.RegisterCreatedObjectUndo(go, "WhipIt Overlay");
            go.AddComponent<TransparentWindow>();
        }
    }

    static void SetupInteraction(Camera cam, Material mat)
    {
        // Smart click-through + grab & throw, walls around the screen (floor = top of the taskbar).
        var grabber = Object.FindFirstObjectByType<Grabber>();
        if (!grabber)
        {
            var go = new GameObject("Interaction");
            Undo.RegisterCreatedObjectUndo(go, "WhipIt Interaction");
            grabber = go.AddComponent<Grabber>();
        }
        var bounds = grabber.GetComponent<ScreenBounds>();
        if (!bounds) bounds = Undo.AddComponent<ScreenBounds>(grabber.gameObject);
        var platforms = grabber.GetComponent<DesktopPlatforms>();
        if (!platforms) platforms = Undo.AddComponent<DesktopPlatforms>(grabber.gameObject); // window tops = platforms
        Undo.RecordObject(platforms, "WhipIt Interaction");
        platforms.cam = cam;
        // Editor test icons (the app uses your real desktop icons): a little column on the left, the last one is the Recycle Bin
        if (platforms.editorIcons == null || platforms.editorIcons.Count == 0)
            platforms.editorIcons = new System.Collections.Generic.List<Vector3> { new Vector3(0.04f, 0.86f, 0f), new Vector3(0.04f, 0.74f, 0f), new Vector3(0.04f, 0.62f, 1f) };
        EditorUtility.SetDirty(platforms);
        Undo.RecordObject(grabber, "WhipIt Interaction");
        Undo.RecordObject(bounds, "WhipIt Interaction");
        grabber.cam = cam;
        bounds.cam = cam;
        EditorUtility.SetDirty(grabber);
        EditorUtility.SetDirty(bounds);

        // The apple is a gummy soft body now: replace the old rigid apple if it's still in the scene.
        for (int guard = 0; guard < 16; guard++)
        {
            var old = Object.FindFirstObjectByType<ProceduralApple>(FindObjectsInactive.Include);
            if (!old) break;
            Undo.DestroyObjectImmediate(old.gameObject);
        }

        var gummy = FindBuddy(Kind.Apple);
        if (!gummy)
        {
            var go = new GameObject("Gummy Apple");
            Undo.RegisterCreatedObjectUndo(go, "WhipIt Gummy Apple");
            go.transform.position = new Vector3(0f, 2f, 0f);
            gummy = go.AddComponent<GummyBody>();
            go.AddComponent<CuteFace>();
        }
        SetupBuddy(gummy, mat, Kind.Apple);

        // copy = snack: the dropper lives on Interaction, the eating on each buddy
        var dropper = grabber.GetComponent<SnackDropper>();
        if (!dropper) dropper = Undo.AddComponent<SnackDropper>(grabber.gameObject);
        Undo.RecordObject(dropper, "WhipIt Interaction");
        dropper.material = mat;
        dropper.sortingOrder = gummy.sortingOrder + 3;
        dropper.dropClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "snack_plink.wav");
        EditorUtility.SetDirty(dropper);
        if (!dropper.dropClip) Debug.LogWarning("[WhipIt] Snack sounds are missing under " + GummyAudio);

        // candy cloud: Ctrl+Shift+K (or chat "candy rain") and a cloud over your cursor rains sweets for the buddies
        var candy = grabber.GetComponent<CandyCloud>();
        if (!candy) candy = Undo.AddComponent<CandyCloud>(grabber.gameObject);
        Undo.RecordObject(candy, "WhipIt Interaction");
        candy.material = mat;
        candy.candyOrder = dropper.sortingOrder;
        candy.poofClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "cloud_poof.wav");
        candy.whooshClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "cloud_whoosh.wav");
        candy.plinkClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "snack_plink.wav");
        EditorUtility.SetDirty(candy);
        if (!candy.poofClip || !candy.whooshClip) Debug.LogWarning("[WhipIt] cloud_poof.wav / cloud_whoosh.wav are missing under " + GummyAudio);
        // rain: Ctrl+Shift+R (or chat "rain"): clouds roll in, it rains on the desktop, puddles; the buddies take cover
        var rain = grabber.GetComponent<Rain>();
        if (!rain) rain = Undo.AddComponent<Rain>(grabber.gameObject);
        Undo.RecordObject(rain, "WhipIt Interaction");
        rain.material = mat;
        rain.rainLoop = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "rain_loop.wav");
        rain.splashClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "puddle_splash.wav");
        rain.chimeClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "rainbow_chime.wav");
        EditorUtility.SetDirty(rain);
        if (!rain.rainLoop || !rain.splashClip || !rain.chimeClip) Debug.LogWarning("[WhipIt] rain_loop.wav / puddle_splash.wav / rainbow_chime.wav are missing under " + GummyAudio);
        // the floor is lava: chat "lava" (or now and then by itself): up onto the windows before it's hot
        var lava = grabber.GetComponent<Lava>();
        if (!lava) lava = Undo.AddComponent<Lava>(grabber.gameObject);
        Undo.RecordObject(lava, "WhipIt Interaction");
        lava.material = mat;
        lava.loopClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "lava_loop.wav");
        lava.bloopClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "lava_bloop.wav");
        lava.sizzleClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "lava_sizzle.wav");
        lava.alarmClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "lava_alarm.wav");
        EditorUtility.SetDirty(lava);
        if (!lava.loopClip || !lava.bloopClip || !lava.sizzleClip || !lava.alarmClip) Debug.LogWarning("[WhipIt] lava_loop.wav / lava_bloop.wav / lava_sizzle.wav / lava_alarm.wav are missing under " + GummyAudio);
        // the giant potion: now and then a snack you copy is a glowing potion (or chat "potion"): whoever drinks it goes giant
        var potion = grabber.GetComponent<GiantPotion>();
        if (!potion) potion = Undo.AddComponent<GiantPotion>(grabber.gameObject);
        Undo.RecordObject(potion, "WhipIt Interaction");
        potion.material = mat;
        potion.gulpClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "espresso_gulp.wav");
        potion.growClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "potion_grow.wav");
        potion.stompClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "dino_stomp.wav");
        potion.popClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "potion_pop.wav");
        EditorUtility.SetDirty(potion);
        if (!potion.gulpClip || !potion.growClip || !potion.stompClip || !potion.popClip) Debug.LogWarning("[WhipIt] espresso_gulp.wav / potion_grow.wav / dino_stomp.wav / potion_pop.wav are missing under " + GummyAudio);
        // the trophy shelf: medals for the games they play (chat "trophies" shows the shelf)
        var trophies = grabber.GetComponent<Trophies>();
        if (!trophies) trophies = Undo.AddComponent<Trophies>(grabber.gameObject);
        Undo.RecordObject(trophies, "WhipIt Interaction");
        trophies.material = mat;
        trophies.dingClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "trophy_ding.wav");
        trophies.shelfClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "trophy_shelf.wav");
        trophies.poofClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "cloud_poof.wav");
        EditorUtility.SetDirty(trophies);
        if (!trophies.dingClip || !trophies.shelfClip) Debug.LogWarning("[WhipIt] trophy_ding.wav / trophy_shelf.wav are missing under " + GummyAudio);
        // caught in the act: they party while you're away, and freeze when you come back
        var caught = grabber.GetComponent<CaughtInTheAct>();
        if (!caught) caught = Undo.AddComponent<CaughtInTheAct>(grabber.gameObject);
        Undo.RecordObject(caught, "WhipIt Interaction");
        caught.material = mat;
        caught.loopClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "mischief_loop.wav");
        caught.scratchClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "record_scratch.wav");
        caught.pomfClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "pillow_pomf.wav");
        caught.boingClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "window_boing.wav");
        EditorUtility.SetDirty(caught);
        if (!caught.loopClip || !caught.scratchClip) Debug.LogWarning("[WhipIt] mischief_loop.wav / record_scratch.wav are missing under " + GummyAudio);
        // bullet time: chat "freeze" stops time; click the bare desktop (or chat "go"): slow motion back to normal
        var bullet = grabber.GetComponent<BulletTime>();
        if (!bullet) bullet = Undo.AddComponent<BulletTime>(grabber.gameObject);
        Undo.RecordObject(bullet, "WhipIt Interaction");
        bullet.material = mat;
        bullet.stopClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "time_stop.wav");
        bullet.goClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "time_go.wav");
        EditorUtility.SetDirty(bullet);
        if (!bullet.stopClip || !bullet.goClip) Debug.LogWarning("[WhipIt] time_stop.wav / time_go.wav are missing under " + GummyAudio);
        // window infestation: push a buddy into a window, hard and long, and it breaks in (the candy panel)
        var infest = grabber.GetComponent<WindowInfestation>();
        if (!infest) infest = Undo.AddComponent<WindowInfestation>(grabber.gameObject);
        Undo.RecordObject(infest, "WhipIt Interaction");
        infest.material = mat;
        infest.creakClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "window_creak.wav");
        infest.breachClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "window_breach.wav");
        infest.ejectClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "window_eject.wav");
        infest.clickClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "panel_click.wav");
        EditorUtility.SetDirty(infest);
        if (!infest.creakClip || !infest.breachClip || !infest.ejectClip || !infest.clickClip) Debug.LogWarning("[WhipIt] window_creak.wav / window_breach.wav / window_eject.wav / panel_click.wav are missing under " + GummyAudio);
        // desktop pinball: chat "pinball" and the main screen turns into a pinball table (a buddy is the ball)
        var pinball = grabber.GetComponent<PinballTable>();
        if (!pinball) pinball = Undo.AddComponent<PinballTable>(grabber.gameObject);
        Undo.RecordObject(pinball, "WhipIt Interaction");
        pinball.material = mat;
        pinball.flipClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "pinball_flip.wav");
        pinball.popClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "pinball_pop.wav");
        pinball.dingClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "pinball_ding.wav");
        pinball.launchClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "pinball_launch.wav");
        pinball.gulpClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "pinball_gulp.wav");
        pinball.startClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "pinball_start.wav");
        pinball.overClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "pinball_over.wav");
        pinball.jackpotClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "pinball_jackpot.wav");
        EditorUtility.SetDirty(pinball);
        if (!pinball.flipClip || !pinball.popClip || !pinball.gulpClip || !pinball.jackpotClip) Debug.LogWarning("[WhipIt] pinball_*.wav are missing under " + GummyAudio);
        // day and night on your clock: sleepy from 21:00, a pillow fort, asleep in a pile, dreams, up in the morning
        var bedtime = grabber.GetComponent<Bedtime>();
        if (!bedtime) bedtime = Undo.AddComponent<Bedtime>(grabber.gameObject);
        Undo.RecordObject(bedtime, "WhipIt Interaction");
        bedtime.material = mat;
        bedtime.pomfClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "pillow_pomf.wav");
        bedtime.blanketClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "blanket_flap.wav");
        bedtime.yawnClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "buddy_yawn.wav");
        bedtime.poofClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "cloud_poof.wav");
        EditorUtility.SetDirty(bedtime);
        if (!bedtime.pomfClip || !bedtime.blanketClip || !bedtime.yawnClip) Debug.LogWarning("[WhipIt] pillow_pomf.wav / blanket_flap.wav / buddy_yawn.wav are missing under " + GummyAudio);

        // the playground toy box: the ball pit (chat "ball pit") and the slide (chat "slide")
        var play = grabber.GetComponent<Playground>();
        if (!play) play = Undo.AddComponent<Playground>(grabber.gameObject);
        Undo.RecordObject(play, "WhipIt Interaction");
        play.material = mat;
        play.pitSplash = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "pit_splash.wav");
        play.slideLoop = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "slide_loop.wav");
        play.slideBoing = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "slide_boing.wav");
        EditorUtility.SetDirty(play);
        if (!play.pitSplash || !play.slideLoop || !play.slideBoing) Debug.LogWarning("[WhipIt] pit_splash.wav / slide_loop.wav / slide_boing.wav are missing under " + GummyAudio);

        // laser pointer: Ctrl+Shift+L (or chat "laser") and a red dot follows your cursor; the buddies chase it (BuddyLaser)
        var laser = grabber.GetComponent<LaserPointer>();
        if (!laser) laser = Undo.AddComponent<LaserPointer>(grabber.gameObject);
        Undo.RecordObject(laser, "WhipIt Interaction");
        laser.material = mat;
        laser.sortingOrder = 300; // above every buddy
        EditorUtility.SetDirty(laser);

        // the tomato friend: waits (inactive) until the apple plants it at level 5
        var tomato = FindBuddy(Kind.Tomato);
        if (!tomato)
        {
            var go = new GameObject("Gummy Tomato");
            Undo.RegisterCreatedObjectUndo(go, "WhipIt Gummy Tomato");
            go.transform.position = new Vector3(1.5f, 2f, 0f);
            tomato = go.AddComponent<GummyBody>();
            go.AddComponent<CuteFace>();
            MakeTomato(tomato);
        }
        SetupBuddy(tomato, mat, Kind.Tomato);
        var jp = tomato.GetComponent<BuddyJetpack>(); // its level-10 gadget (instead of a rope): a half-broken jetpack
        if (!jp) jp = Undo.AddComponent<BuddyJetpack>(tomato.gameObject);
        Undo.RecordObject(jp, "WhipIt Gummy Tomato");
        jp.material = mat;
        jp.rumbleLoop = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "jet_rumble_loop.wav");
        jp.sputterClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "jet_sputter.wav");
        jp.bangClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "jet_bang.wav");
        if (!jp.rumbleLoop || !jp.sputterClip || !jp.bangClip) Debug.LogWarning("[WhipIt] Jetpack sounds are missing under " + GummyAudio);
        EditorUtility.SetDirty(jp);
        tomato.gameObject.SetActive(false);

        // the coffee bean: waits (inactive) until the apple plants it at level 10
        var bean = FindBuddy(Kind.Bean);
        if (!bean)
        {
            var go = new GameObject("Gummy Coffee Bean");
            Undo.RegisterCreatedObjectUndo(go, "WhipIt Gummy Coffee Bean");
            go.transform.position = new Vector3(-1.5f, 2f, 0f);
            bean = go.AddComponent<GummyBody>();
            go.AddComponent<CuteFace>();
            MakeBean(bean);
        }
        SetupBuddy(bean, mat, Kind.Bean);
        if (!bean.GetComponent<BeanSteam>()) Undo.AddComponent<BeanSteam>(bean.gameObject); // steams after a long stretch of work
        var esp = bean.GetComponent<BuddyEspresso>(); // its level-10 gadget (instead of a rope): an espresso shot
        if (!esp) esp = Undo.AddComponent<BuddyEspresso>(bean.gameObject);
        Undo.RecordObject(esp, "WhipIt Gummy Coffee Bean");
        esp.material = mat;
        esp.gulpClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "espresso_gulp.wav");
        if (!esp.gulpClip) Debug.LogWarning("[WhipIt] espresso_gulp.wav is missing under " + GummyAudio);
        EditorUtility.SetDirty(esp);
        bean.gameObject.SetActive(false);

        // the burger: waits (inactive) until the apple plants a sesame seed at level 15
        var burger = FindBuddy(Kind.Burger);
        if (!burger)
        {
            var go = new GameObject("Gummy Burger");
            Undo.RegisterCreatedObjectUndo(go, "WhipIt Gummy Burger");
            go.transform.position = new Vector3(3f, 2f, 0f);
            burger = go.AddComponent<GummyBody>();
            go.AddComponent<CuteFace>();
            MakeBurger(burger);
        }
        SetupBuddy(burger, mat, Kind.Burger);
        var bb = burger.GetComponent<BuddyBurger>(); // the lettuce, cheese, patty and seeds; its belly is a trampoline
        if (!bb) bb = Undo.AddComponent<BuddyBurger>(burger.gameObject);
        Undo.RecordObject(bb, "WhipIt Gummy Burger");
        bb.material = mat;
        bb.boingClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "window_boing.wav");
        EditorUtility.SetDirty(bb);
        var pogo = burger.GetComponent<BuddyPogo>(); // its level-10 gadget (instead of a rope): a pogo fork
        if (!pogo) pogo = Undo.AddComponent<BuddyPogo>(burger.gameObject);
        Undo.RecordObject(pogo, "WhipIt Gummy Burger");
        pogo.material = mat;
        pogo.boingClip = bb.boingClip;
        EditorUtility.SetDirty(pogo);
        burger.gameObject.SetActive(false);

        // the little dino: waits (inactive) until the apple finds its egg at level 20
        var dino = FindBuddy(Kind.Dino);
        if (!dino)
        {
            var go = new GameObject("Gummy Dino");
            Undo.RegisterCreatedObjectUndo(go, "WhipIt Gummy Dino");
            go.transform.position = new Vector3(-3f, 2f, 0f);
            dino = go.AddComponent<GummyBody>();
            go.AddComponent<CuteFace>();
            MakeDino(dino);
        }
        SetupBuddy(dino, mat, Kind.Dino);
        var bd = dino.GetComponent<BuddyDino>(); // back spikes, tail, tiny arms, feet, belly; RAWR
        if (!bd) bd = Undo.AddComponent<BuddyDino>(dino.gameObject);
        Undo.RecordObject(bd, "WhipIt Gummy Dino");
        bd.material = mat;
        bd.rawrClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "dino_rawr.wav");
        if (!bd.rawrClip) Debug.LogWarning("[WhipIt] dino_rawr.wav is missing under " + GummyAudio);
        EditorUtility.SetDirty(bd);
        var stomp = dino.GetComponent<BuddyStomp>(); // its level-10 gadget (instead of a rope): the super stomp
        if (!stomp) stomp = Undo.AddComponent<BuddyStomp>(dino.gameObject);
        Undo.RecordObject(stomp, "WhipIt Gummy Dino");
        stomp.material = mat;
        stomp.stompClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "dino_stomp.wav");
        if (!stomp.stompClip) Debug.LogWarning("[WhipIt] dino_stomp.wav is missing under " + GummyAudio);
        EditorUtility.SetDirty(stomp);
        dino.gameObject.SetActive(false);

        // the duckling: waits (inactive) until it waddles in at level 25 and decides the apple is its mama
        var duck = FindBuddy(Kind.Duck);
        if (!duck)
        {
            var go = new GameObject("Gummy Duck");
            Undo.RegisterCreatedObjectUndo(go, "WhipIt Gummy Duck");
            go.transform.position = new Vector3(3f, 2f, 0f);
            duck = go.AddComponent<GummyBody>();
            go.AddComponent<CuteFace>();
            MakeDuck(duck);
        }
        SetupBuddy(duck, mat, Kind.Duck);
        var dk = duck.GetComponent<BuddyDuck>(); // bill, tuft, wings, webbed feet, tail; follows its mama around
        if (!dk) dk = Undo.AddComponent<BuddyDuck>(duck.gameObject);
        Undo.RecordObject(dk, "WhipIt Gummy Duck");
        dk.material = mat;
        dk.mama = gummy;
        EditorUtility.SetDirty(dk);
        duck.gameObject.SetActive(false);

        // your day (active time, breaks, counts; only the time since your last input is read)
        if (!grabber.GetComponent<WorkClock>()) Undo.AddComponent<WorkClock>(grabber.gameObject);

        // hide and seek (all), the whip (the apple, from level 10), the chat box (Ctrl+Shift+Space)
        foreach (var b in new[] { gummy, tomato, bean, burger, dino, duck })
            if (!b.GetComponent<BuddyHideSeek>()) Undo.AddComponent<BuddyHideSeek>(b.gameObject);
        var bw = gummy.GetComponent<BuddyWhip>();
        if (!bw) bw = Undo.AddComponent<BuddyWhip>(gummy.gameObject);
        Undo.RecordObject(bw, "WhipIt Gummy Apple");
        bw.whip = Object.FindFirstObjectByType<Whip>(FindObjectsInactive.Include);
        if (!bw.whip) Debug.LogWarning("[WhipIt] No Whip in the scene for the apple.");
        EditorUtility.SetDirty(bw);
        var chat = grabber.GetComponent<BuddyChat>();
        if (!chat) chat = Undo.AddComponent<BuddyChat>(grabber.gameObject);
        Undo.RecordObject(chat, "WhipIt Interaction");
        chat.material = mat;
        chat.sortingOrder = 300; // above every buddy and bubble
        EditorUtility.SetDirty(chat);

        var friends = gummy.GetComponent<BuddyFriends>();
        if (!friends) friends = Undo.AddComponent<BuddyFriends>(gummy.gameObject);
        Undo.RecordObject(friends, "WhipIt Gummy Apple");
        friends.tomato = tomato;
        friends.bean = bean;
        friends.burger = burger;
        friends.dino = dino;
        friends.duck = duck;
        friends.plantClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "snack_plink.wav");
        friends.poofClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "gummy_pop.wav");
        EditorUtility.SetDirty(friends);
    }

    enum Kind { Apple, Tomato, Bean, Burger, Dino, Duck }

    // tomato / bean = the one saving as buddy_tomato / buddy_bean (or named so); apple = the other one
    static GummyBody FindBuddy(Kind kind)
    {
        var all = Object.FindObjectsByType<GummyBody>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        if (all == null) return null;
        foreach (var b in all)
        {
            var p = b.GetComponent<BuddyProgress>();
            Kind k = (p && p.saveName == TomatoSave) || b.name.Contains("Tomato") ? Kind.Tomato
                   : (p && p.saveName == BeanSave) || b.name.Contains("Bean") ? Kind.Bean
                   : (p && p.saveName == BurgerSave) || b.name.Contains("Burger") ? Kind.Burger
                   : (p && p.saveName == DinoSave) || b.name.Contains("Dino") ? Kind.Dino
                   : (p && p.saveName == DuckSave) || b.name.Contains("Duck") ? Kind.Duck : Kind.Apple;
            if (k == kind) return b;
        }
        return null;
    }

    const string TomatoSave = "buddy_tomato", BeanSave = "buddy_bean", BurgerSave = "buddy_burger", DinoSave = "buddy_dino", DuckSave = "buddy_duck";
    // unripe cherry tomato: pale yellow-green with a blush (not the young apple's lime green)
    static readonly Color LittleTomato = new Color(0.80f, 0.88f, 0.52f), LittleTomatoRim = new Color(0.84f, 0.50f, 0.36f);

    // first-time look + personality of the tomato (Setup won't touch your tweaks after that)
    static void MakeTomato(GummyBody t)
    {
        t.shape = GummyBody.BodyShape.Tomato;
        t.topper = GummyBody.Topper.Calyx;
        t.color = new Color(0.93f, 0.20f, 0.16f);
        t.rimColor = new Color(0.50f, 0.07f, 0.05f);
        t.leafColor = new Color(0.22f, 0.55f, 0.22f);
        t.stemColor = new Color(0.25f, 0.45f, 0.18f);
        t.sortingOrder = 15; // its own range of draw orders above the apple (SetupBuddy keeps it)
        var face = t.GetComponent<CuteFace>();
        if (face) { face.sortingOrder = t.sortingOrder + 1; face.eyeOffset = new Vector2(0.18f, -0.05f); }

        var p = Undo.AddComponent<BuddyProgress>(t.gameObject);
        p.saveName = TomatoSave;
        p.stages = new[]
        {
            new BuddyProgress.Stage { name = "Little tomato", fromLevel = 1, shape = GummyBody.BodyShape.Tomato, size = 0.36f,
                color = LittleTomato, rim = LittleTomatoRim, topper = GummyBody.Topper.Calyx },
            new BuddyProgress.Stage { name = "Tomato", fromLevel = 3, shape = GummyBody.BodyShape.Tomato, size = 0.45f,
                color = new Color(0.98f, 0.50f, 0.22f), rim = new Color(0.55f, 0.20f, 0.08f), topper = GummyBody.Topper.Calyx },
            new BuddyProgress.Stage { name = "Ripe tomato", fromLevel = 6, shape = GummyBody.BodyShape.Tomato, size = 0.52f,
                color = new Color(0.93f, 0.20f, 0.16f), rim = new Color(0.50f, 0.07f, 0.05f), topper = GummyBody.Topper.Calyx },
        };

        var brain = Undo.AddComponent<GummyBrain>(t.gameObject); // livelier than the apple
        brain.hopSpeed = 1.9f;
        brain.hopUp = 6f;
        brain.idleTime = new Vector2(1.5f, 4.5f);
        brain.hopTowardsCursor = 0.5f;
        brain.climbChance = 0.35f;
        brain.peekChance = 0.2f;
        brain.sleepAfter = 50f;

        var sp = Undo.AddComponent<BuddySpeech>(t.gameObject);
        sp.voice = BuddySpeech.Voice.Tomato;
        sp.chattiness = 0.85f;
    }

    // first-time look + personality of the coffee bean: green coffee -> light -> medium -> dark roast
    static void MakeBean(GummyBody t)
    {
        t.shape = GummyBody.BodyShape.Bean;
        t.topper = GummyBody.Topper.Crease;
        t.color = new Color(0.50f, 0.31f, 0.17f);
        t.rimColor = new Color(0.24f, 0.12f, 0.05f);
        t.size = 0.5f;
        t.uprightSpeed = 10f; // tall and round-bottomed: stands up straighter than the apple (or it rocks)
        t.sortingOrder = 25; // its own range of draw orders above the tomato (SetupBuddy keeps it)
        var face = t.GetComponent<CuteFace>();
        if (face)
        {
            face.sortingOrder = t.sortingOrder + 1;
            face.eyeOffset = new Vector2(0.15f, -0.08f);
            face.eyeSize = 0.22f;
            face.blushOffset = new Vector2(0.27f, -0.2f);
            face.mouthOffset = new Vector2(0f, -0.24f);
            face.pupilColor = new Color(0.06f, 0.03f, 0.02f); // shows on the dark roast too
        }

        var p = Undo.AddComponent<BuddyProgress>(t.gameObject);
        p.saveName = BeanSave;
        p.stages = new[]
        {
            new BuddyProgress.Stage { name = "Green bean", fromLevel = 1, shape = GummyBody.BodyShape.Bean, size = 0.34f,
                color = new Color(0.60f, 0.66f, 0.47f), rim = new Color(0.30f, 0.36f, 0.22f), topper = GummyBody.Topper.Crease },
            new BuddyProgress.Stage { name = "Light roast", fromLevel = 3, shape = GummyBody.BodyShape.Bean, size = 0.40f,
                color = new Color(0.82f, 0.62f, 0.40f), rim = new Color(0.46f, 0.29f, 0.15f), topper = GummyBody.Topper.Crease },
            new BuddyProgress.Stage { name = "Medium roast", fromLevel = 6, shape = GummyBody.BodyShape.Bean, size = 0.46f,
                color = new Color(0.62f, 0.39f, 0.21f), rim = new Color(0.32f, 0.17f, 0.08f), topper = GummyBody.Topper.Crease },
            new BuddyProgress.Stage { name = "Dark roast", fromLevel = 10, shape = GummyBody.BodyShape.Bean, size = 0.50f,
                color = new Color(0.50f, 0.31f, 0.17f), rim = new Color(0.24f, 0.12f, 0.05f), topper = GummyBody.Topper.Crease },
        };

        var brain = Undo.AddComponent<GummyBrain>(t.gameObject); // jittery: quick little hops, hardly ever naps
        brain.hopSpeed = 2.1f;
        brain.hopUp = 5.5f;
        brain.idleTime = new Vector2(0.8f, 2.5f);
        brain.pauseBetweenHops = new Vector2(0.2f, 0.45f);
        brain.hopTowardsCursor = 0.55f;
        brain.climbChance = 0.3f;
        brain.peekChance = 0.25f;
        brain.sleepAfter = 120f;

        var sp = Undo.AddComponent<BuddySpeech>(t.gameObject);
        sp.voice = BuddySpeech.Voice.Bean;
        sp.chattiness = 0.8f;
    }

    // first-time look + personality of the burger: a slider -> a burger -> a big burger -> a double (two patties)
    static void MakeBurger(GummyBody t)
    {
        t.shape = GummyBody.BodyShape.Burger;
        t.topper = GummyBody.Topper.None; // (the sesame seeds are BuddyBurger's)
        t.color = new Color(0.90f, 0.60f, 0.28f);
        t.rimColor = new Color(0.58f, 0.32f, 0.12f);
        t.size = 0.6f;
        t.uprightSpeed = 9f; // flat-bottomed and wide: sits square
        t.sortingOrder = 35; // its own range of draw orders above the bean (SetupBuddy keeps it)
        var face = t.GetComponent<CuteFace>();
        if (face)
        {
            face.sortingOrder = t.sortingOrder + 1;
            face.eyeOffset = new Vector2(0.17f, 0.17f); // on the top bun, above the filling
            face.eyeSize = 0.22f;
            face.blushOffset = new Vector2(0.31f, 0.07f);
            face.mouthOffset = new Vector2(0f, 0.06f);
            face.pupilColor = new Color(0.12f, 0.06f, 0.03f);
        }

        var p = Undo.AddComponent<BuddyProgress>(t.gameObject);
        p.saveName = BurgerSave;
        p.stages = new[]
        {
            new BuddyProgress.Stage { name = "Slider", fromLevel = 1, shape = GummyBody.BodyShape.Burger, size = 0.40f,
                color = new Color(0.95f, 0.70f, 0.38f), rim = new Color(0.62f, 0.38f, 0.16f), topper = GummyBody.Topper.None },
            new BuddyProgress.Stage { name = "Burger", fromLevel = 3, shape = GummyBody.BodyShape.Burger, size = 0.50f,
                color = new Color(0.92f, 0.64f, 0.32f), rim = new Color(0.60f, 0.34f, 0.13f), topper = GummyBody.Topper.None },
            new BuddyProgress.Stage { name = "Big burger", fromLevel = 6, shape = GummyBody.BodyShape.Burger, size = 0.58f,
                color = new Color(0.90f, 0.60f, 0.28f), rim = new Color(0.58f, 0.32f, 0.12f), topper = GummyBody.Topper.None },
            new BuddyProgress.Stage { name = "Double burger", fromLevel = 10, shape = GummyBody.BodyShape.Burger, size = 0.64f,
                color = new Color(0.88f, 0.57f, 0.26f), rim = new Color(0.55f, 0.30f, 0.11f), topper = GummyBody.Topper.None },
        };

        var brain = Undo.AddComponent<GummyBrain>(t.gameObject); // heavy and laid-back: slow, low hops, long sits, naps
        brain.hopSpeed = 1.3f;
        brain.hopUp = 4.8f;
        brain.idleTime = new Vector2(2.5f, 6f);
        brain.pauseBetweenHops = new Vector2(0.5f, 0.9f);
        brain.hopTowardsCursor = 0.35f;
        brain.climbChance = 0.15f;
        brain.peekChance = 0.15f;
        brain.sleepAfter = 35f;

        var sp = Undo.AddComponent<BuddySpeech>(t.gameObject);
        sp.voice = BuddySpeech.Voice.Burger;
        sp.chattiness = 0.7f;
    }

    // first-time look + personality of the little dino: a hatchling -> a little dino -> a dino -> a big dino. Our own toon
    // T-rex: green gummy, big happy eyes, a grin full of little pointy teeth (BuddyDino: spikes, tail, tiny arms, feet)
    static void MakeDino(GummyBody t)
    {
        t.shape = GummyBody.BodyShape.Dino;
        t.topper = GummyBody.Topper.None; // (the spikes are BuddyDino's)
        t.color = new Color(0.36f, 0.76f, 0.30f);
        t.rimColor = new Color(0.15f, 0.42f, 0.14f);
        t.size = 0.6f;
        t.uprightSpeed = 9f; // stands up on its feet
        t.sortingOrder = 45; // its own range of draw orders above the burger (SetupBuddy keeps it)
        var face = t.GetComponent<CuteFace>();
        if (face)
        {
            face.sortingOrder = t.sortingOrder + 1;
            face.eyeOffset = new Vector2(0.17f, 0.12f); // big happy eyes up on its head
            face.eyeSize = 0.27f;
            face.pupilSize = 0.17f;
            face.blushOffset = new Vector2(0.31f, -0.02f);
            face.mouthOffset = new Vector2(0f, -0.03f);
            face.mouthWidth = 0.27f;
            face.toothy = true;
            face.pupilColor = new Color(0.10f, 0.07f, 0.04f);
        }

        var p = Undo.AddComponent<BuddyProgress>(t.gameObject);
        p.saveName = DinoSave;
        p.stages = new[]
        {
            new BuddyProgress.Stage { name = "Hatchling", fromLevel = 1, shape = GummyBody.BodyShape.Dino, size = 0.38f,
                color = new Color(0.56f, 0.86f, 0.42f), rim = new Color(0.24f, 0.50f, 0.18f), topper = GummyBody.Topper.None },
            new BuddyProgress.Stage { name = "Little dino", fromLevel = 3, shape = GummyBody.BodyShape.Dino, size = 0.46f,
                color = new Color(0.45f, 0.81f, 0.35f), rim = new Color(0.19f, 0.46f, 0.16f), topper = GummyBody.Topper.None },
            new BuddyProgress.Stage { name = "Dino", fromLevel = 6, shape = GummyBody.BodyShape.Dino, size = 0.54f,
                color = new Color(0.36f, 0.76f, 0.30f), rim = new Color(0.15f, 0.42f, 0.14f), topper = GummyBody.Topper.None },
            new BuddyProgress.Stage { name = "Big dino", fromLevel = 10, shape = GummyBody.BodyShape.Dino, size = 0.62f,
                color = new Color(0.29f, 0.69f, 0.27f), rim = new Color(0.12f, 0.37f, 0.12f), topper = GummyBody.Topper.None },
        };

        var brain = Undo.AddComponent<GummyBrain>(t.gameObject); // bouncy and curious: big stompy hops, chases you
        brain.hopSpeed = 1.8f;
        brain.hopUp = 6.2f;
        brain.idleTime = new Vector2(1.2f, 3.8f);
        brain.pauseBetweenHops = new Vector2(0.3f, 0.6f);
        brain.hopTowardsCursor = 0.6f;
        brain.climbChance = 0.3f;
        brain.peekChance = 0.3f;
        brain.sleepAfter = 60f;

        var sp = Undo.AddComponent<BuddySpeech>(t.gameObject);
        sp.voice = BuddySpeech.Voice.Dino;
        sp.chattiness = 0.85f;
    }

    // first-time look + personality of the duckling: a duckling -> a little duck -> a duck -> a big duck. Our own round
    // gummy duck: yellow, big shiny eyes, the bill / tuft / wings / feet / tail are BuddyDuck's
    static void MakeDuck(GummyBody t)
    {
        t.shape = GummyBody.BodyShape.Duck;
        t.topper = GummyBody.Topper.None; // (the tuft is BuddyDuck's)
        t.color = new Color(1f, 0.86f, 0.30f);
        t.rimColor = new Color(0.85f, 0.60f, 0.10f);
        t.size = 0.5f;
        t.uprightSpeed = 9f; // stands up on its feet
        t.sortingOrder = 55; // its own range of draw orders above the dino (SetupBuddy keeps it)
        var face = t.GetComponent<CuteFace>();
        if (face)
        {
            face.sortingOrder = t.sortingOrder + 1;
            face.eyeOffset = new Vector2(0.15f, 0.08f); // big eyes above its bill
            face.eyeSize = 0.26f;
            face.pupilSize = 0.17f;
            face.blushOffset = new Vector2(0.3f, -0.06f);
            face.mouthOffset = new Vector2(0f, -0.14f); // (hidden under the bill)
            face.mouthWidth = 0.1f;
            face.pupilColor = new Color(0.10f, 0.07f, 0.04f);
        }

        var p = Undo.AddComponent<BuddyProgress>(t.gameObject);
        p.saveName = DuckSave;
        p.stages = new[]
        {
            new BuddyProgress.Stage { name = "Duckling", fromLevel = 1, shape = GummyBody.BodyShape.Duck, size = 0.34f,
                color = new Color(1f, 0.92f, 0.45f), rim = new Color(0.90f, 0.70f, 0.20f), topper = GummyBody.Topper.None },
            new BuddyProgress.Stage { name = "Little duck", fromLevel = 3, shape = GummyBody.BodyShape.Duck, size = 0.42f,
                color = new Color(1f, 0.89f, 0.38f), rim = new Color(0.88f, 0.65f, 0.15f), topper = GummyBody.Topper.None },
            new BuddyProgress.Stage { name = "Duck", fromLevel = 6, shape = GummyBody.BodyShape.Duck, size = 0.5f,
                color = new Color(1f, 0.86f, 0.30f), rim = new Color(0.85f, 0.60f, 0.10f), topper = GummyBody.Topper.None },
            new BuddyProgress.Stage { name = "Big duck", fromLevel = 10, shape = GummyBody.BodyShape.Duck, size = 0.56f,
                color = new Color(0.99f, 0.82f, 0.24f), rim = new Color(0.80f, 0.55f, 0.08f), topper = GummyBody.Topper.None },
        };

        var brain = Undo.AddComponent<GummyBrain>(t.gameObject); // little and quick: short pattering hops
        brain.hopSpeed = 1.7f;
        brain.hopUp = 5.2f;
        brain.idleTime = new Vector2(1f, 3f);
        brain.pauseBetweenHops = new Vector2(0.2f, 0.45f);
        brain.hopTowardsCursor = 0.4f;
        brain.climbChance = 0.2f;
        brain.peekChance = 0.25f;
        brain.sleepAfter = 50f;

        var sp = Undo.AddComponent<BuddySpeech>(t.gameObject);
        sp.voice = BuddySpeech.Voice.Duck;
        sp.chattiness = 0.8f;
    }

    static void SetupBuddy(GummyBody gummy, Material mat, Kind kind)
    {
        bool tomato = kind == Kind.Tomato;
        string what = kind == Kind.Tomato ? "WhipIt Gummy Tomato" : kind == Kind.Bean ? "WhipIt Gummy Coffee Bean" : kind == Kind.Burger ? "WhipIt Gummy Burger" : kind == Kind.Dino ? "WhipIt Gummy Dino" : kind == Kind.Duck ? "WhipIt Gummy Duck" : "WhipIt Gummy Apple";
        Undo.RecordObject(gummy, what);
        gummy.shader = AssetDatabase.LoadAssetAtPath<Shader>(GummyShaderPath);
        // each buddy its own range of draw orders (o-4..o+5): the windows-in-front masks of one never touch another
        gummy.sortingOrder = kind == Kind.Tomato ? 15 : kind == Kind.Bean ? 25 : kind == Kind.Burger ? 35 : kind == Kind.Dino ? 45 : kind == Kind.Duck ? 55 : 5;
        var faceOrder = gummy.GetComponent<CuteFace>();
        if (faceOrder) { Undo.RecordObject(faceOrder, what); faceOrder.sortingOrder = gummy.sortingOrder + 1; }
        if (!gummy.shader) Debug.LogWarning("[WhipIt] Gummy shader not found at " + GummyShaderPath);
        gummy.spriteMaterial = mat;
        gummy.collideBuddies = true;
        EditorUtility.SetDirty(gummy);
        var face = gummy.GetComponent<CuteFace>();
        if (!face) face = Undo.AddComponent<CuteFace>(gummy.gameObject);
        Undo.RecordObject(face, what);
        face.material = mat;
        face.scale = gummy.size;
        EditorUtility.SetDirty(face);

        // sounds + goo effects
        var fx = gummy.GetComponent<GummyFx>();
        if (!fx) fx = Undo.AddComponent<GummyFx>(gummy.gameObject);
        Undo.RecordObject(fx, what);
        fx.splatClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "gummy_splat.wav");
        fx.bumpClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "gummy_bump.wav");
        fx.popClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "gummy_pop.wav");
        fx.stretchLoop = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "gummy_stretch_loop.wav");
        if (!fx.splatClip || !fx.bumpClip || !fx.popClip || !fx.stretchLoop)
            Debug.LogWarning("[WhipIt] Some gummy sounds are missing under " + GummyAudio);
        EditorUtility.SetDirty(fx);

        // behaviour: hops around, watches the cursor, naps
        if (!gummy.GetComponent<GummyBrain>()) Undo.AddComponent<GummyBrain>(gummy.gameObject);
        // gets cleverer every level (new tricks: hop down / up, climb, leap, wall jump, routes, catch, lasso, swing...)
        if (!gummy.GetComponent<BuddySkills>()) Undo.AddComponent<BuddySkills>(gummy.gameObject);
        // its rope (lasso your cursor, swings, rope climbs): red licorice / a green vine / caramel (the apple's whip once it has it)
        var rope = gummy.GetComponent<BuddyRope>();
        if (!rope) rope = Undo.AddComponent<BuddyRope>(gummy.gameObject);
        Undo.RecordObject(rope, what);
        rope.material = mat;
        rope.color = kind == Kind.Tomato ? new Color(0.30f, 0.62f, 0.25f) : kind == Kind.Bean ? new Color(0.86f, 0.66f, 0.40f) : kind == Kind.Burger ? new Color(0.96f, 0.76f, 0.18f) : kind == Kind.Dino ? new Color(0.55f, 0.36f, 0.20f) : kind == Kind.Duck ? new Color(0.98f, 0.60f, 0.15f) : new Color(0.80f, 0.13f, 0.18f);
        rope.zipLoop = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "zip_whirr_loop.wav"); // (sliding down a zipline)
        rope.zipThunk = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "zip_thunk.wav");    // (the apple's zipline lands)
        rope.zipSnap = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "zip_snap.wav");      // (... and snaps)
        if (!rope.zipLoop || !rope.zipThunk || !rope.zipSnap) Debug.LogWarning("[WhipIt] Zipline sounds are missing under " + GummyAudio);
        EditorUtility.SetDirty(rope);
        // skateboard (level 13): the same blue board for everyone, rolling sound + clack
        var skate = gummy.GetComponent<BuddySkate>();
        if (!skate) skate = Undo.AddComponent<BuddySkate>(gummy.gameObject);
        Undo.RecordObject(skate, what);
        skate.material = mat;
        skate.rollLoop = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "skate_roll_loop.wav");
        skate.clackClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "skate_clack.wav");
        skate.grindLoop = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "grind_loop.wav");
        if (!skate.rollLoop || !skate.clackClip || !skate.grindLoop) Debug.LogWarning("[WhipIt] Skateboard sounds are missing under " + GummyAudio);
        EditorUtility.SetDirty(skate);
        // paper plane (level 14): folded from notebook paper, crinkle + wind
        var plane = gummy.GetComponent<BuddyPlane>();
        if (!plane) plane = Undo.AddComponent<BuddyPlane>(gummy.gameObject);
        Undo.RecordObject(plane, what);
        plane.material = mat;
        plane.foldClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "paper_fold.wav");
        plane.windLoop = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "glide_wind_loop.wav");
        if (!plane.foldClip || !plane.windLoop) Debug.LogWarning("[WhipIt] Paper plane sounds are missing under " + GummyAudio);
        EditorUtility.SetDirty(plane);
        // balloon (level 17): floats up onto high windows; a blue / yellow / pink balloon, a squeaky blow-up and a pop
        var balloon = gummy.GetComponent<BuddyBalloon>();
        if (!balloon) balloon = Undo.AddComponent<BuddyBalloon>(gummy.gameObject);
        Undo.RecordObject(balloon, what);
        balloon.material = mat;
        balloon.color = kind == Kind.Tomato ? new Color(0.99f, 0.80f, 0.25f) : kind == Kind.Bean ? new Color(0.98f, 0.50f, 0.68f) : kind == Kind.Burger ? new Color(0.45f, 0.82f, 0.42f) : kind == Kind.Dino ? new Color(0.70f, 0.45f, 0.95f) : kind == Kind.Duck ? new Color(0.98f, 0.40f, 0.45f) : new Color(0.30f, 0.62f, 0.95f);
        balloon.inflateClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "balloon_inflate.wav");
        balloon.popClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "balloon_pop.wav");
        if (!balloon.inflateClip || !balloon.popClip) Debug.LogWarning("[WhipIt] Balloon sounds are missing under " + GummyAudio);
        EditorUtility.SetDirty(balloon);
        // fetch: races for the ball you throw and brings it back
        if (!gummy.GetComponent<BuddyFetch>()) Undo.AddComponent<BuddyFetch>(gummy.gameObject);
        if (!gummy.GetComponent<BuddyHoops>()) Undo.AddComponent<BuddyHoops>(gummy.gameObject); // basketball: rebounds, pickup games, dunks
        if (!gummy.GetComponent<BuddyPit>()) Undo.AddComponent<BuddyPit>(gummy.gameObject);     // the ball pit: cannonballs (the swimming is GummyBrain.Pit)
        if (!gummy.GetComponent<BuddySlide>()) Undo.AddComponent<BuddySlide>(gummy.gameObject); // the slide: climbs, queues, trains, tricks
        // the rain: takes cover under a window, drips, shakes itself dry, splashes in the puddles (the duck loves it)
        var wetness = gummy.GetComponent<BuddyRain>();
        if (!wetness) wetness = Undo.AddComponent<BuddyRain>(gummy.gameObject);
        Undo.RecordObject(wetness, what);
        wetness.shakeClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "shake_spray.wav");
        if (!wetness.shakeClip) Debug.LogWarning("[WhipIt] shake_spray.wav is missing under " + GummyAudio);
        EditorUtility.SetDirty(wetness);
        // fusing: pressed into another buddy it squashes into one big monster with it (BuddyMonster); scared of monsters
        var fusion = gummy.GetComponent<BuddyFusion>();
        if (!fusion) fusion = Undo.AddComponent<BuddyFusion>(gummy.gameObject);
        Undo.RecordObject(fusion, what);
        fusion.squishClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "fuse_squish.wav");
        fusion.fuseClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "fuse_gulp.wav");
        fusion.splitClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "monster_split.wav");
        fusion.roarClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "monster_roar.wav");
        fusion.stompClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "dino_stomp.wav");
        if (!fusion.squishClip || !fusion.fuseClip || !fusion.splitClip || !fusion.roarClip || !fusion.stompClip) Debug.LogWarning("[WhipIt] Monster sounds are missing under " + GummyAudio);
        EditorUtility.SetDirty(fusion);
        // the desktop band: every window top is a note, landing on one plays it on its instrument; jams (BandJam)
        var band = gummy.GetComponent<BuddyBand>();
        if (!band) band = Undo.AddComponent<BuddyBand>(gummy.gameObject);
        Undo.RecordObject(band, what);
        band.marimba = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "band_marimba.wav");
        band.uke = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "band_uke.wav");
        band.cup = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "band_cup.wav");
        band.bongo = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "band_bongo.wav");
        band.bass = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "band_bass.wav");
        band.quack = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "band_quack.wav");
        band.tuba = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "band_tuba.wav");
        band.maraca = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "band_maraca.wav");
        band.kick = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "band_kick.wav");
        band.snare = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "band_snare.wav");
        band.hat = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "band_hat.wav");
        band.material = mat;
        if (!band.marimba || !band.uke || !band.cup || !band.bongo || !band.bass || !band.quack || !band.tuba || !band.maraca || !band.kick || !band.snare || !band.hat)
            Debug.LogWarning("[WhipIt] Band sounds (band_*.wav) are missing under " + GummyAudio);
        EditorUtility.SetDirty(band);
        // learns to dodge your grabs (better every level)
        if (!gummy.GetComponent<BuddyDodge>()) Undo.AddComponent<BuddyDodge>(gummy.gameObject);
        // lives in the desktop: windows in front of where it is hide it (Ctrl+Shift+B: always in front)
        if (!gummy.GetComponent<BuddyLayer>()) Undo.AddComponent<BuddyLayer>(gummy.gameObject);
        // chases the laser pointer's dot like a cat (wiggle, pounce)
        if (!gummy.GetComponent<BuddyLaser>()) Undo.AddComponent<BuddyLaser>(gummy.gameObject);
        // level-up party: cake, confetti, a tune, the others come over
        var party = gummy.GetComponent<BuddyParty>();
        if (!party) party = Undo.AddComponent<BuddyParty>(gummy.gameObject);
        Undo.RecordObject(party, what);
        party.material = mat;
        party.tune = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "party_tune.wav");
        if (!party.tune) Debug.LogWarning("[WhipIt] party_tune.wav is missing under " + GummyAudio);
        EditorUtility.SetDirty(party);
        // the Halloween costumes are gone (2026-10-02): take the leftover (now missing) costume script off the buddy
        int gone = GameObjectUtility.RemoveMonoBehavioursWithMissingScript(gummy.gameObject);
        if (gone > 0) { EditorUtility.SetDirty(gummy.gameObject); Debug.Log($"[WhipIt] {gummy.name}: removed {gone} leftover script(s) (the old costume)."); }

        // XP + growth and the level / XP bar HUD (each buddy has its own save)
        var buddy = gummy.GetComponent<BuddyProgress>();
        if (!buddy) buddy = Undo.AddComponent<BuddyProgress>(gummy.gameObject);
        Undo.RecordObject(buddy, what);
        if (tomato && buddy.stages != null && buddy.stages.Length > 0)
        {
            // first version made the little tomato the same green as the young apple: switch it (only if it's still that green)
            Color c = buddy.stages[0].color;
            if (Mathf.Abs(c.r - 0.55f) < 0.01f && Mathf.Abs(c.g - 0.78f) < 0.01f && Mathf.Abs(c.b - 0.30f) < 0.01f)
            {
                buddy.stages[0].color = LittleTomato;
                buddy.stages[0].rim = LittleTomatoRim;
            }
        }
        buddy.levelUpClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "buddy_levelup.wav");
        buddy.stageUpClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "buddy_stageup.wav");
        if (!buddy.levelUpClip || !buddy.stageUpClip) Debug.LogWarning("[WhipIt] Level-up sounds are missing under " + GummyAudio);
        EditorUtility.SetDirty(buddy);
        if (!gummy.GetComponent<BuddyHud>()) Undo.AddComponent<BuddyHud>(gummy.gameObject);

        // your windows wobble when it hits them
        var wobble = gummy.GetComponent<WindowWobble>();
        if (!wobble) wobble = Undo.AddComponent<WindowWobble>(gummy.gameObject);
        Undo.RecordObject(wobble, what);
        wobble.boingClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "window_boing.wav");
        EditorUtility.SetDirty(wobble);

        // eats snacks
        var eater = gummy.GetComponent<BuddyEater>();
        if (!eater) eater = Undo.AddComponent<BuddyEater>(gummy.gameObject);
        Undo.RecordObject(eater, what);
        eater.crunchClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "snack_crunch.wav");
        EditorUtility.SetDirty(eater);

        // speech bubbles + hanging out with the other buddy
        var speech = gummy.GetComponent<BuddySpeech>();
        if (!speech) speech = Undo.AddComponent<BuddySpeech>(gummy.gameObject);
        Undo.RecordObject(speech, what);
        speech.voice = kind == Kind.Tomato ? BuddySpeech.Voice.Tomato : kind == Kind.Bean ? BuddySpeech.Voice.Bean : kind == Kind.Burger ? BuddySpeech.Voice.Burger : kind == Kind.Dino ? BuddySpeech.Voice.Dino : kind == Kind.Duck ? BuddySpeech.Voice.Duck : BuddySpeech.Voice.Apple;
        speech.blipClip = AssetDatabase.LoadAssetAtPath<AudioClip>(GummyAudio + "speech_blip.wav");
        if (!speech.blipClip) Debug.LogWarning("[WhipIt] speech_blip.wav is missing under " + GummyAudio);
        EditorUtility.SetDirty(speech);
        if (!gummy.GetComponent<BuddySocial>()) Undo.AddComponent<BuddySocial>(gummy.gameObject);
        if (!wobble.boingClip || !eater.crunchClip) Debug.LogWarning("[WhipIt] Snack / wobble sounds are missing under " + GummyAudio);
    }

    // ------------------------------------------------------------------ buddy save (shared by Play mode and the built app)

    [MenuItem("WhipIt/Buddy/Reset To Seed (level 1, no friends)", priority = 20)]
    public static void BuddyResetToSeed() => SetBuddyLevel(1, 0, true);

    [MenuItem("WhipIt/Buddy/Skip To Apple (level 10)", priority = 21)]
    public static void BuddySkipToApple() => SetBuddyLevel(10, 0, false);

    [MenuItem("WhipIt/Buddy/One Snack Before The Tomato (level 4)", priority = 22)]
    public static void BuddyAlmostTomato()
    {
        var b = FindBuddy(Kind.Apple)?.GetComponent<BuddyProgress>();
        int baseXp = b ? b.baseXpPerLevel : 20, extra = b ? b.extraXpPerLevel : 15;
        SetBuddyLevel(4, baseXp + extra * 3 - 3, true); // 3 XP short of level 5: eat a snack (Ctrl+C) and watch
    }

    [MenuItem("WhipIt/Buddy/One Snack Before The Coffee Bean (level 9)", priority = 23)]
    public static void BuddyAlmostBean()
    {
        var b = FindBuddy(Kind.Apple)?.GetComponent<BuddyProgress>();
        int baseXp = b ? b.baseXpPerLevel : 20, extra = b ? b.extraXpPerLevel : 15;
        SetBuddyLevel(9, baseXp + extra * 8 - 3, false, true); // 3 XP short of level 10 (whip + coffee seed); keeps the tomato
    }

    [MenuItem("WhipIt/Buddy/Plant Tomato Now (Play mode)", priority = 24)]
    public static void BuddyPlantTomato()
    {
        var f = Object.FindFirstObjectByType<BuddyFriends>();
        if (!EditorApplication.isPlaying || !f) { Debug.LogWarning("[WhipIt] Press Play first (and run WhipIt > Setup once)."); return; }
        if (!f.PlantNow()) Debug.Log("[WhipIt] The tomato is already here (or on its way).");
    }

    [MenuItem("WhipIt/Buddy/Plant Coffee Bean Now (Play mode)", priority = 24)]
    public static void BuddyPlantBean()
    {
        var f = Object.FindFirstObjectByType<BuddyFriends>();
        if (!EditorApplication.isPlaying || !f) { Debug.LogWarning("[WhipIt] Press Play first (and run WhipIt > Setup once)."); return; }
        if (!f.PlantBeanNow()) Debug.Log("[WhipIt] The coffee bean is already here (or a seed is on its way).");
    }

    [MenuItem("WhipIt/Buddy/One Snack Before The Burger (level 14)", priority = 23)]
    public static void BuddyAlmostBurger()
    {
        var b = FindBuddy(Kind.Apple)?.GetComponent<BuddyProgress>();
        int baseXp = b ? b.baseXpPerLevel : 20, extra = b ? b.extraXpPerLevel : 15;
        SetBuddyLevel(14, baseXp + extra * 13 - 3, false); // 3 XP short of level 15 (the sesame seed); keeps the tomato and bean
    }

    [MenuItem("WhipIt/Buddy/Plant Burger Now (Play mode)", priority = 24)]
    public static void BuddyPlantBurger()
    {
        var f = Object.FindFirstObjectByType<BuddyFriends>();
        if (!EditorApplication.isPlaying || !f) { Debug.LogWarning("[WhipIt] Press Play first (and run WhipIt > Setup once)."); return; }
        if (!f.PlantBurgerNow()) Debug.Log("[WhipIt] The burger is already here (or a seed is on its way).");
    }

    [MenuItem("WhipIt/Buddy/One Snack Before The Dino Egg (level 19)", priority = 23)]
    public static void BuddyAlmostDino()
    {
        var b = FindBuddy(Kind.Apple)?.GetComponent<BuddyProgress>();
        int baseXp = b ? b.baseXpPerLevel : 20, extra = b ? b.extraXpPerLevel : 15;
        SetBuddyLevel(19, baseXp + extra * 18 - 3, false); // 3 XP short of level 20 (the egg); keeps the others
    }

    [MenuItem("WhipIt/Buddy/Hatch Dino Now (Play mode)", priority = 24)]
    public static void BuddyHatchDino()
    {
        var f = Object.FindFirstObjectByType<BuddyFriends>();
        if (!EditorApplication.isPlaying || !f) { Debug.LogWarning("[WhipIt] Press Play first (and run WhipIt > Setup once)."); return; }
        if (!f.PlantDinoNow()) Debug.Log("[WhipIt] The dino is already here (or an egg is on its way).");
    }

    [MenuItem("WhipIt/Buddy/One Snack Before The Duck (level 24)", priority = 23)]
    public static void BuddyAlmostDuck()
    {
        var b = FindBuddy(Kind.Apple)?.GetComponent<BuddyProgress>();
        int baseXp = b ? b.baseXpPerLevel : 20, extra = b ? b.extraXpPerLevel : 15;
        SetBuddyLevel(24, baseXp + extra * 23 - 3, false); // 3 XP short of level 25 (the duckling); keeps the others
    }

    [MenuItem("WhipIt/Buddy/Duck Arrives Now (Play mode)", priority = 24)]
    public static void BuddyDuckArrives()
    {
        var f = Object.FindFirstObjectByType<BuddyFriends>();
        if (!EditorApplication.isPlaying || !f) { Debug.LogWarning("[WhipIt] Press Play first (and run WhipIt > Setup once)."); return; }
        if (!f.PlantDuckNow()) Debug.Log("[WhipIt] The duck is already here (or someone else is being born right now).");
    }

    [MenuItem("WhipIt/Buddy/Pretend I Worked An Hour (Play mode)", priority = 32)]
    public static void BuddyWorkedAnHour()
    {
        var c = Object.FindFirstObjectByType<WorkClock>();
        if (!EditorApplication.isPlaying || !c) { Debug.LogWarning("[WhipIt] Press Play first (and run WhipIt > Setup once)."); return; }
        c.AddStreak(3600f);
        Debug.Log($"[WhipIt] Work stretch now {WorkClock.Duration(c.Streak)} (the coffee bean steams from 1 h, more at 1.5 h and 2 h).");
    }

    [MenuItem("WhipIt/Buddy/Play Hide And Seek (Play mode)", priority = 30)]
    public static void BuddyHideAndSeek()
    {
        if (!EditorApplication.isPlaying) { Debug.LogWarning("[WhipIt] Press Play first. In the app: chat \"hide and seek\" (Ctrl+Shift+Space)."); return; }
        if (BuddyHideSeek.StartGame() == 0) Debug.Log("[WhipIt] Nowhere to hide: they need a window that comes down to the floor they're on (or a tall enough taskbar).");
    }

    [MenuItem("WhipIt/Buddy/Give The Apple The Whip (Play mode)", priority = 31)]
    public static void BuddyGiveWhip()
    {
        var w = Object.FindFirstObjectByType<BuddyWhip>();
        if (!EditorApplication.isPlaying || !w) { Debug.LogWarning("[WhipIt] Press Play first (and run WhipIt > Setup once). It gets it by itself at level " + (w ? w.unlockLevel : 10) + "."); return; }
        w.Give(true);
    }

    [MenuItem("WhipIt/Buddy/Try Every Trick (level 17, Play mode)", priority = 33)]
    public static void BuddyTryEveryTrick()
    {
        if (!EditorApplication.isPlaying) { Debug.LogWarning("[WhipIt] Press Play first. This is just for this Play session (your saves stay as they are)."); return; }
        int n = 0;
        foreach (var sk in Object.FindObjectsByType<BuddySkills>(FindObjectsSortMode.None)) { sk.levelOverride = 17; n++; }
        Debug.Log($"[WhipIt] {n} buddies act like level 17 until you stop Play: hold the cursor still above one (lasso), drop snacks up high (swing / rope climb / balloon), far away (skateboard) or far below a window (paper plane / zipline), or chat \"swing\" / \"lasso me\" / \"skate\" / \"kickflip\" / \"grind\" / \"fly\" / \"zipline\" / \"balloon\".");
    }

    [MenuItem("WhipIt/Buddy/Laser Pointer On or Off (Play mode)", priority = 38)]
    public static void BuddyLaserPointer()
    {
        var l = Object.FindFirstObjectByType<LaserPointer>();
        if (!EditorApplication.isPlaying || !l) { Debug.LogWarning("[WhipIt] Press Play first (and run WhipIt > Setup once). In the app: Ctrl+Shift+L, or chat \"laser\"."); return; }
        l.Toggle();
        Debug.Log(LaserPointer.On ? "[WhipIt] Laser on: move the mouse over the Game view and watch them chase the dot. Ctrl+Shift+L (in the app) or this menu again to put it away." : "[WhipIt] Laser off.");
    }

    [MenuItem("WhipIt/Buddy/Throw A Party (Play mode)", priority = 37)]
    public static void BuddyThrowParty()
    {
        var b = Object.FindFirstObjectByType<GummyBody>();
        if (!EditorApplication.isPlaying || !b) { Debug.LogWarning("[WhipIt] Press Play first. The party comes by itself when a buddy levels up (from level 3)."); return; }
        var p = b.GetComponent<BuddyParty>();
        var prog = b.GetComponent<BuddyProgress>();
        LevelUpParty.Throw(b, prog ? prog.Level : 1, p ? p.tune : null, p ? p.volume : 0.5f, p && p.material ? p.material : b.spriteMaterial);
    }

    [MenuItem("WhipIt/Buddy/Build A Tower (Play mode)", priority = 38)]
    public static void BuddyBuildTower()
    {
        if (!EditorApplication.isPlaying) { Debug.LogWarning("[WhipIt] Press Play first. A tower comes by itself when a snack is too high for everyone (chat \"tower\" in the app)."); return; }
        foreach (var b in GummyBody.All)
            if (b && b.isActiveAndEnabled && BuddyTower.Show(b)) { Debug.Log("[WhipIt] Tower time! " + BuddyTower.Current.Height + " high."); return; }
        Debug.LogWarning("[WhipIt] No tower: needs two buddies that are awake, on their feet and not busy (and room above them).");
    }

    [MenuItem("WhipIt/Buddy/Put Up The Hoop (Play mode)", priority = 39)]
    public static void BuddyPutHoop()
    {
        var b = Object.FindFirstObjectByType<GummyBody>();
        if (!EditorApplication.isPlaying || !b) { Debug.LogWarning("[WhipIt] Press Play first. In the app: chat \"basketball\" (\"no hoop\" takes it down)."); return; }
        if (Hoop.Current) { Hoop.TakeDown(); Debug.Log("[WhipIt] Hoop taken down."); return; }
        var h = Hoop.Put(b.spriteMaterial, Grabber.CursorWorld);
        Debug.Log("[WhipIt] Hoop up (" + h.State + "). Flick the ball at it; with two buddies free a pickup game starts. Chat \"game\", \"dunk\", \"shoot\".");
    }

    [MenuItem("WhipIt/Buddy/Make It Rain (Play mode)", priority = 42)]
    public static void MakeItRain()
    {
        if (!EditorApplication.isPlaying || !Rain.Current) { Debug.LogWarning("[WhipIt] Press Play first (and run WhipIt > Setup once). In the app: Ctrl+Shift+R, or chat \"rain\"."); return; }
        Rain.Current.Toggle();
        Debug.Log(Rain.Current.Raining ? "[WhipIt] Rain: the clouds roll in (2 minutes, or Ctrl+Shift+R / this menu again to stop it)." : "[WhipIt] Rain stops: the clouds roll away.");
    }

    [MenuItem("WhipIt/Buddy/Fuse Two Buddies (Play mode)", priority = 43)]
    public static void FuseTwo()
    {
        if (!EditorApplication.isPlaying) { Debug.LogWarning("[WhipIt] Press Play first (and run WhipIt > Setup once). In the app: hold a buddy pressed into another one."); return; }
        var free = new System.Collections.Generic.List<GummyBody>();
        foreach (var b in GummyBody.All) if (b && b.isActiveAndEnabled && !b.IsMonster && b.GetComponent<BuddyFusion>()) free.Add(b);
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
        Debug.Log(m ? "[WhipIt] Fused: " + m.MonsterName + " (" + m.Members.Count + " inside). Pull it apart to split it." : "[WhipIt] Nothing to fuse (two buddies needed, at most six in a monster).");
    }

    [MenuItem("WhipIt/Buddy/Split The Monsters (Play mode)", priority = 44)]
    public static void SplitMonsters()
    {
        if (!EditorApplication.isPlaying) { Debug.LogWarning("[WhipIt] Press Play first. In the app: pull a monster apart."); return; }
        int n = BuddyMonster.All.Count;
        BuddyMonster.SplitAll();
        Debug.Log(n > 0 ? "[WhipIt] Split " + n + " monster(s) back into buddies." : "[WhipIt] No monsters right now.");
    }

    [MenuItem("WhipIt/Buddy/Drop A Giant Potion (Play mode)", priority = 25)]
    public static void DropGiantPotion()
    {
        if (!EditorApplication.isPlaying || !GiantPotion.Current) { Debug.LogWarning("[WhipIt] Press Play first (and run WhipIt > Setup once). In the app: chat \"potion\"."); return; }
        if (GiantPotion.Current.Giant) { GiantPotion.Current.PopNow(); Debug.Log("[WhipIt] Pop! Back to normal size."); return; }
        if (!GiantPotion.Current.DropFor(null)) { Debug.LogWarning("[WhipIt] There's a potion lying around already."); return; }
        Debug.Log("[WhipIt] A giant potion drops over your cursor: whoever drinks it goes giant for 15 s. This menu again (while there's a giant) pops it.");
    }

    [MenuItem("WhipIt/Buddy/Show The Trophy Shelf (Play mode)", priority = 26)]
    public static void ShowTrophies()
    {
        if (!EditorApplication.isPlaying || !Trophies.Current) { Debug.LogWarning("[WhipIt] Press Play first (and run WhipIt > Setup once). In the app: chat \"trophies\"."); return; }
        if (Trophies.Current.Showing) { Trophies.Current.Hide(); return; }
        if (!Trophies.Current.Show()) Debug.LogWarning("[WhipIt] No buddies to show the shelf for.");
        else Debug.Log("[WhipIt] The trophy shelf: medals for lava wins, dunks, buckets, hide and seek, tower tops and band jams (bronze, silver, gold). Saved in " + Trophies.FilePath);
    }

    [MenuItem("WhipIt/Buddy/Go Wild Now (Play mode)", priority = 44)]
    public static void GoWildNow()
    {
        if (!EditorApplication.isPlaying || !CaughtInTheAct.Current) { Debug.LogWarning("[WhipIt] Press Play first (and run WhipIt > Setup once). In the app they party by themselves when you've been away 2 minutes (chat \"go wild\": after 20 s)."); return; }
        if (!CaughtInTheAct.Current.StartNow(6f)) { Debug.LogWarning("[WhipIt] Not now (something else is going on)."); return; }
        Debug.Log("[WhipIt] They think you're gone: party! Keep your hands off the mouse and keyboard for 6 s, then move the mouse to catch them.");
    }

    [MenuItem("WhipIt/Buddy/Freeze Time (Play mode)", priority = 48)]
    public static void FreezeTime()
    {
        if (!EditorApplication.isPlaying || !BulletTime.Current) { Debug.LogWarning("[WhipIt] Press Play first (and run WhipIt > Setup once). In the app: chat \"freeze\"."); return; }
        if (BulletTime.Stopped) { BulletTime.Current.Go(); Debug.Log("[WhipIt] Time goes on: slow motion first."); return; }
        BulletTime.Current.Freeze();
        Debug.Log("[WhipIt] Time stopped. Move things around (they stay where you let go), then click empty space in the Game view (or this menu again) to let it go.");
    }

    [MenuItem("WhipIt/Buddy/The Floor Is Lava (Play mode)", priority = 43)]
    public static void FloorIsLava()
    {
        if (!EditorApplication.isPlaying || !Lava.Current) { Debug.LogWarning("[WhipIt] Press Play first (and run WhipIt > Setup once). In the app: chat \"lava\"."); return; }
        if (Lava.Current.On) { Lava.Current.Stop(); Debug.Log("[WhipIt] Lava off: it cools down."); return; }
        string why = Lava.Blocked();
        if (why != null || !Lava.Current.Begin()) { Debug.LogWarning("[WhipIt] Not now (" + (why ?? "no buddies") + ")."); return; }
        Debug.Log("[WhipIt] The floor is lava! Get them up onto a window (drag a window under one, or pick one up). This menu again stops it.");
    }

    [MenuItem("WhipIt/Buddy/Bedtime Now (Play mode)", priority = 46)]
    public static void BedtimeNow()
    {
        if (!EditorApplication.isPlaying || !Bedtime.Instance) { Debug.LogWarning("[WhipIt] Press Play first (and run WhipIt > Setup once). In the app: chat \"pillow fort\", or wait for 21:40."); return; }
        Bedtime.Instance.Night();
    }

    [MenuItem("WhipIt/Buddy/Good Morning (Play mode)", priority = 47)]
    public static void GoodMorning()
    {
        if (!EditorApplication.isPlaying || !Bedtime.Instance) { Debug.LogWarning("[WhipIt] Press Play first (and run WhipIt > Setup once)."); return; }
        if (!Bedtime.Sleeping) { Debug.Log("[WhipIt] They're awake."); return; }
        Bedtime.Instance.Morning();
    }

    [MenuItem("WhipIt/Buddy/Start A Jam (Play mode)", priority = 45)]
    public static void StartJam()
    {
        if (!EditorApplication.isPlaying) { Debug.LogWarning("[WhipIt] Press Play first (and run WhipIt > Setup once). In the app: chat \"jam\" or \"music\"."); return; }
        if (BandJam.Current) { Debug.Log("[WhipIt] They're jamming already."); return; }
        var j = BandJam.Begin(null, true);
        Debug.Log(j ? "[WhipIt] Jam: " + j.Bpm + " bpm, chords " + j.Chords + (j.PlaysYourTune ? ", playing your tune" : "") + "." : "[WhipIt] Nobody can play right now.");
    }

    [MenuItem("WhipIt/Buddy/Candy Cloud (Play mode)", priority = 41)]
    public static void BuddyCandyCloud()
    {
        if (!EditorApplication.isPlaying || !CandyCloud.Instance) { Debug.LogWarning("[WhipIt] Press Play first (and run WhipIt > Setup once). In the app: Ctrl+Shift+K, or chat \"candy rain\"."); return; }
        CandyCloud.Instance.Shower();
        Debug.Log("[WhipIt] A candy cloud over the cursor: 15 sweets. Hold Ctrl+Shift+K for more (up to 100).");
    }

    [MenuItem("WhipIt/Buddy/Put Out The Ball Pit (Play mode)", priority = 42)]
    public static void BuddyBallPit()
    {
        var b = Object.FindFirstObjectByType<GummyBody>();
        if (!EditorApplication.isPlaying || !b) { Debug.LogWarning("[WhipIt] Press Play first. In the app: chat \"ball pit\" (\"no pit\" puts it away)."); return; }
        if (BallPit.Current) { BallPit.TakeDown(); Debug.Log("[WhipIt] Ball pit put away."); return; }
        var p = Playground.PutPit(b.spriteMaterial, Grabber.CursorWorld);
        Debug.Log("[WhipIt] Ball pit out at x " + p.X0.ToString("0.0") + ".." + p.X1.ToString("0.0") + ". Drop a buddy in, grab a ball, chat \"cannonball\".");
    }

    [MenuItem("WhipIt/Buddy/Put Up The Slide (Play mode)", priority = 43)]
    public static void BuddySlideUp()
    {
        var b = Object.FindFirstObjectByType<GummyBody>();
        if (!EditorApplication.isPlaying || !b) { Debug.LogWarning("[WhipIt] Press Play first. In the app: chat \"slide\" (\"no slide\" takes it down)."); return; }
        if (Slide.Current) { Slide.TakeDown(); Debug.Log("[WhipIt] Slide taken down."); return; }
        var s = Playground.PutSlide(b.spriteMaterial, Grabber.CursorWorld);
        Debug.Log("[WhipIt] Slide up (" + s.State + (s.Window != 0 ? ", window to " + (s.LandWindow != 0 ? "window" : "taskbar") : "") + "). Chat \"go slide\".");
    }

    [MenuItem("WhipIt/Buddy/Desktop Pinball (Play mode)", priority = 40)]
    public static void DesktopPinball()
    {
        if (!EditorApplication.isPlaying || !PinballTable.Current) { Debug.LogWarning("[WhipIt] Press Play first (and run WhipIt > Setup once). In the app: chat \"pinball\" (\"stop pinball\" ends it)."); return; }
        if (PinballTable.Playing) { PinballTable.Current.Close(); Debug.Log("[WhipIt] Pinball table packed up."); return; }
        if (!PinballTable.Current.Begin()) { Debug.LogWarning("[WhipIt] No buddy free to be the ball."); return; }
        Debug.Log("[WhipIt] Pinball! Click the Game view, then Left Shift / Right Shift flip, hold Space and let go to launch, Esc stops. " + PinballTable.Current.Report);
    }

    [MenuItem("WhipIt/Buddy/Pickup Game (Play mode)", priority = 40)]
    public static void BuddyPickupGame()
    {
        if (!EditorApplication.isPlaying) { Debug.LogWarning("[WhipIt] Press Play first."); return; }
        var any = Object.FindFirstObjectByType<GummyBody>();
        if (!Hoop.Current) Hoop.Put(any ? any.spriteMaterial : null, Grabber.CursorWorld);
        foreach (var h in BuddyHoops.All) if (PickupGame.TryStart(h, true)) { Debug.Log("[WhipIt] Game on: " + PickupGame.Current.Teams[0].Count + " v " + PickupGame.Current.Teams[1].Count + ", first to " + PickupGame.ToWin + "."); return; }
        Debug.LogWarning("[WhipIt] No game: needs two buddies that aren't busy (and the hoop up somewhere they can reach).");
    }

    [MenuItem("WhipIt/Buddy/Throw The Ball (Play mode)", priority = 36)]
    public static void BuddyThrowBall()
    {
        var d = Object.FindFirstObjectByType<SnackDropper>();
        if (!EditorApplication.isPlaying || !d) { Debug.LogWarning("[WhipIt] Press Play first (and run WhipIt > Setup once). In the app: chat \"fetch\" for a ball, then grab it and throw it."); return; }
        Rect r = ScreenBounds.WorldRect;
        float x = Random.Range(r.xMin + 1f, r.xMax - 1f);
        FetchBall.Spawn(new Vector2(x, r.yMax - 0.3f), new Vector2(Random.Range(-6f, 6f), Random.Range(0f, 3f)), d.material, d.sortingOrder, true);
        Debug.Log("[WhipIt] Ball thrown! Whoever gets it brings it back to your cursor. Grab it and throw it again (a real throw: let go while moving).");
    }

    [MenuItem("WhipIt/Buddy/Lasso My Cursor (Play mode)", priority = 34)]
    public static void BuddyLassoCursor()
    {
        if (!EditorApplication.isPlaying) { Debug.LogWarning("[WhipIt] Press Play first. In the app: hold the cursor still just above a buddy, or chat \"lasso me\"."); return; }
        int n = 0;
        foreach (var r in Object.FindObjectsByType<BuddyRope>(FindObjectsSortMode.None)) { r.LassoSoon(10f); n++; }
        if (n == 0) Debug.LogWarning("[WhipIt] No buddy with a rope: run WhipIt > Setup once.");
        else Debug.Log("[WhipIt] Within 10 s, move the cursor just above a buddy and it lassoes it (needs level 11, or Try Every Trick first). Shake the mouse to let go.");
    }

    [MenuItem("WhipIt/Buddy/Drop A Snack (Play mode)", priority = 25)]
    public static void BuddyDropSnack()
    {
        var d = Object.FindFirstObjectByType<SnackDropper>();
        if (!EditorApplication.isPlaying || !d) { Debug.LogWarning("[WhipIt] Press Play first (and run WhipIt > Setup once). Copying anything (Ctrl+C) drops a snack too."); return; }
        d.Drop(true);
    }

    [MenuItem("WhipIt/Debug/Dump Faces (Play mode)", priority = 60)]
    public static void DebugDumpFaces()
    {
        if (!EditorApplication.isPlaying) { Debug.LogWarning("[WhipIt] Press Play first. In the app: chat \"debug faces\"."); return; }
        FaceDebug.Dump();
    }

    [MenuItem("WhipIt/Buddy/Show Save File", priority = 26)]
    public static void BuddyShowSave()
    {
        if (File.Exists(BuddyProgress.SavePath)) EditorUtility.RevealInFinder(BuddyProgress.SavePath);
        else Debug.Log("[WhipIt] No buddy save yet (it's written once it earns XP): " + BuddyProgress.SavePath);
    }

    static void SetBuddyLevel(int level, int xp, bool unbornTomato, bool unbornBean = false)
    {
        if (EditorApplication.isPlaying) { Debug.LogWarning("[WhipIt] Stop Play mode first (the running buddy would save over it)."); return; }
        var b = FindBuddy(Kind.Apple)?.GetComponent<BuddyProgress>();
        int baseXp = b ? b.baseXpPerLevel : 20, extra = b ? b.extraXpPerLevel : 15, total = xp;
        for (int l = 1; l < level; l++) total += baseXp + extra * (l - 1);
        BuddyProgress.WriteSave(level, xp, total);
        string tomatoSave = BuddyProgress.PathFor(TomatoSave), beanSave = BuddyProgress.PathFor(BeanSave);
        if (unbornTomato && File.Exists(tomatoSave)) File.Delete(tomatoSave); // the apple plants it again at level 5
        if ((unbornTomato || unbornBean) && File.Exists(beanSave)) File.Delete(beanSave); // ... and the coffee bean at 10
        string burgerSave = BuddyProgress.PathFor(BurgerSave);
        if (level < 15 && File.Exists(burgerSave)) File.Delete(burgerSave); // ... and the burger at 15
        string dinoSave = BuddyProgress.PathFor(DinoSave);
        if (level < 20 && File.Exists(dinoSave)) File.Delete(dinoSave); // ... and the dino egg at 20
        string duckSave = BuddyProgress.PathFor(DuckSave);
        if (level < 25 && File.Exists(duckSave)) File.Delete(duckSave); // ... and the duckling at 25
        string friends = unbornTomato ? ", no friends yet" : unbornBean ? ", coffee bean not born yet" : "";
        Debug.Log($"[WhipIt] Apple set to level {level}{friends}. " +
                  $"Close the built WhipIt app first if it's running, it saves on quit. ({BuddyProgress.SavePath})");
    }

    static void EnsureSceneInBuild(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        var list = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
        foreach (var s in list) if (s.path == path) { s.enabled = true; EditorBuildSettings.scenes = list.ToArray(); return; }
        list.Insert(0, new EditorBuildSettingsScene(path, true));
        EditorBuildSettings.scenes = list.ToArray();
    }

    static void SetSerialized(string assetPath, string property, bool value)
    {
        var objs = AssetDatabase.LoadAllAssetsAtPath(assetPath);
        if (objs == null || objs.Length == 0) { Debug.LogWarning($"[WhipIt] Couldn't load {assetPath}."); return; }
        var so = new SerializedObject(objs[0]);
        var prop = so.FindProperty(property);
        if (prop == null) { Debug.LogWarning($"[WhipIt] '{property}' not found, set it manually in Player Settings."); return; }
        prop.boolValue = value;
        so.ApplyModifiedPropertiesWithoutUndo();
    }
}
