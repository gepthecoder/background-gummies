using UnityEngine;

/// <summary>
/// The coffee bean steams when you've been working a long time without a break (WorkClock): a few wisps after an hour,
/// more after an hour and a half, a lot after two hours, with a line each time it gets more ("TAKE A BREAK!!").
/// When you come back from a break the steam's gone and it's happy to see you. Hidden while it hides.
/// </summary>
[RequireComponent(typeof(GummyBody))]
[DisallowMultipleComponent]
public class BeanSteam : MonoBehaviour
{
    [Tooltip("Minutes of work in a row before it starts steaming, steams more, steams a lot.")]
    public float steamAfter = 60f, moreAfter = 90f, mostAfter = 120f;
    public Color steamColor = new Color(1f, 1f, 1f, 0.55f);

    /// <summary>0 = no steam, 1..3 = more and more.</summary>
    public int Level { get; private set; }
    /// <summary>Wisps in the air right now.</summary>
    public int Puffs { get { int n = 0; foreach (var p in puffs) if (p.sr && p.sr.enabled) n++; return n; } }

    struct Puff { public SpriteRenderer sr; public float age, life, phase, scale; public Vector2 at; }
    const int Pool = 24;
    readonly Puff[] puffs = new Puff[Pool];
    GummyBody body;
    BuddySpeech speech;
    GameObject holder;
    /// <summary>The steam wisps (BuddyLayer hides them behind windows with the rest of it).</summary>
    public GameObject Root => holder;
    Sprite puffSprite;
    Texture2D puffTex;
    float nextPuff;
    int said;           // highest level it has talked about this stretch (until you take a break)
    WorkClock hooked;

    void Awake()
    {
        body = GetComponent<GummyBody>();
        speech = GetComponent<BuddySpeech>();
    }

    void OnDisable()
    {
        Unhook();
        for (int i = 0; i < Pool; i++) if (puffs[i].sr) puffs[i].sr.enabled = false;
    }

    void OnDestroy()
    {
        Unhook();
        if (holder) Destroy(holder);
        if (puffSprite) Destroy(puffSprite);
        if (puffTex) Destroy(puffTex);
    }

    void Unhook()
    {
        if (hooked) hooked.BackFromBreak -= OnBack;
        hooked = null;
    }

    void Update() { long t0 = WhipProf.T(); UpdateTimed(); WhipProf.Add("BeanSteam.Update", t0); }
    void UpdateTimed()
    {
        var clock = WorkClock.Instance;
        if (clock != hooked) { Unhook(); hooked = clock; if (clock) clock.BackFromBreak += OnBack; }
        float minutes = clock ? clock.Streak / 60f : 0f;
        int level = minutes >= mostAfter ? 3 : minutes >= moreAfter ? 2 : minutes >= steamAfter ? 1 : 0;
        if (level > Level && level > said) Talk(level);
        Level = level;

        float now = Time.time;
        bool show = Level > 0 && !body.Hiding;
        if (show && now >= nextPuff)
        {
            nextPuff = now + (Level == 1 ? 0.55f : Level == 2 ? 0.32f : 0.18f) * Random.Range(0.8f, 1.2f);
            Spawn();
        }
        Animate(Time.deltaTime, !body.Hiding);
    }

    void Talk(int level)
    {
        said = level;
        if (!speech) return;
        string[] lines = level == 1 ? new[] { "I'M STEAMING!", "1 HOUR STRAIGHT!", "WE'RE ON FIRE" }
                       : level == 2 ? new[] { "SO PRODUCTIVE! BREAK SOON?", "STEAM LEVEL: HIGH", "90 MIN! WOW" }
                       : new[] { "TAKE A BREAK!!", "OVERHEATING!", "2 HOURS! STRETCH!" };
        speech.Say(lines[Random.Range(0, lines.Length)], 0f, true);
    }

    void OnBack(float seconds)
    {
        if (said == 0 || !speech) return; // it wasn't steaming: nothing to say
        said = 0;
        speech.Say(Random.value < 0.5f ? "REFRESHED!" : "WELCOME BACK!", 0f, true);
    }

    // ------------------------------------------------------------------ the wisps

    void Spawn()
    {
        if (!holder) Build();
        for (int i = 0; i < Pool; i++)
        {
            if (puffs[i].sr.enabled) continue;
            float s = body.size / 0.5f * (Level == 3 ? 1.3f : 1f);
            // from the top of its head (the crease), a little to either side
            puffs[i].at = new Vector2(body.Center.x + Random.Range(-0.12f, 0.12f) * body.size, body.Extent.yMax - 0.02f);
            puffs[i].age = 0f;
            puffs[i].life = Random.Range(1.2f, 1.7f);
            puffs[i].phase = Random.value * 6.28f;
            puffs[i].scale = s;
            puffs[i].sr.enabled = true;
            return;
        }
    }

    void Animate(float dt, bool visible)
    {
        for (int i = 0; i < Pool; i++)
        {
            var p = puffs[i];
            if (!p.sr || !p.sr.enabled) continue;
            p.age += dt;
            float u = p.age / p.life;
            if (u >= 1f || !visible) { p.sr.enabled = false; puffs[i] = p; continue; }
            float y = p.at.y + 0.45f * p.age;
            float x = p.at.x + 0.06f * Mathf.Sin(p.age * 3f + p.phase) * Mathf.Min(1f, p.age * 2f);
            p.sr.transform.position = new Vector3(x, y, 0f);
            p.sr.transform.localScale = Vector3.one * ((0.05f + 0.11f * u) * p.scale);
            Color c = steamColor;
            c.a *= Mathf.Min(1f, p.age / 0.15f) * (1f - u);
            p.sr.color = c;
            puffs[i] = p;
        }
    }

    void Build()
    {
        holder = new GameObject(name + " Steam");
        const int R = 32;
        var px = new Color32[R * R];
        for (int y = 0; y < R; y++)
            for (int x = 0; x < R; x++)
            {
                float u = (x + 0.5f) / R * 2f - 1f, v = (y + 0.5f) / R * 2f - 1f;
                float a = Mathf.Clamp01(1f - Mathf.Sqrt(u * u + v * v));
                px[y * R + x] = new Color32(255, 255, 255, (byte)(255f * Mathf.Pow(a, 1.5f)));
            }
        puffTex = new Texture2D(R, R, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "Steam" };
        puffTex.SetPixels32(px);
        puffTex.Apply(false, true);
        puffSprite = Sprite.Create(puffTex, new Rect(0, 0, R, R), new Vector2(0.5f, 0.5f), R / 2f); // 2 x 2 world units at scale 1
        for (int i = 0; i < Pool; i++)
        {
            var go = new GameObject("Wisp");
            go.transform.SetParent(holder.transform, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = puffSprite;
            sr.sortingOrder = body.sortingOrder + 2;
            if (body.spriteMaterial) sr.sharedMaterial = body.spriteMaterial;
            sr.enabled = false;
            puffs[i].sr = sr;
        }
    }
}
