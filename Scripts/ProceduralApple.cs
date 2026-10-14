using UnityEngine;

/// <summary>
/// A throwable apple drawn in code (signed-distance shapes baked into a texture at startup), no art assets needed.
/// First prop for grab & throw, and the seed of the apple buddy later on.
/// </summary>
[RequireComponent(typeof(Rigidbody2D), typeof(CircleCollider2D))]
[DisallowMultipleComponent]
public class ProceduralApple : MonoBehaviour
{
    [Tooltip("Body width in world units (screen height = 10).")]
    public float size = 0.6f;
    public Color skin = new Color(0.90f, 0.22f, 0.21f);
    public Color leafColor = new Color(0.26f, 0.63f, 0.28f);
    public Color stemColor = new Color(0.43f, 0.30f, 0.25f);
    [Tooltip("Optional. Unlit sprite material (WhipIt > Setup assigns Whip.mat); empty = pipeline default.")]
    public Material material;
    public int sortingOrder = 5;
    [Range(0f, 1f)] public float bounciness = 0.45f;
    [Range(0f, 1f)] public float friction = 0.5f;
    [Tooltip("Wobbles back upright when it's lying still, so the face ends up the right way up.")]
    public float uprightSpring = 60f;
    public float uprightDamping = 5f;

    const int Res = 128;
    const float Span = 1.25f; // texture covers 1.25 x body diameter (room for stem + leaf)

    Texture2D tex;
    Sprite sprite;
    PhysicsMaterial2D physMat;
    Rigidbody2D rb;
    Grabbable grab;

    void Awake()
    {
        tex = Bake();
        sprite = Sprite.Create(tex, new Rect(0, 0, Res, Res), new Vector2(0.5f, 0.5f), Res / (size * Span));

        var go = new GameObject("Visual");
        go.transform.SetParent(transform, false);
        go.transform.SetAsFirstSibling(); // Grabbable scales child 0
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.sortingOrder = sortingOrder;
        if (material) sr.sharedMaterial = material;

        var col = GetComponent<CircleCollider2D>();
        col.radius = size * 0.4f; // between the apple's half-width and half-height
        col.offset = new Vector2(0f, -0.045f * size);
        physMat = new PhysicsMaterial2D("Apple") { bounciness = bounciness, friction = friction };
        col.sharedMaterial = physMat;

        rb = GetComponent<Rigidbody2D>();
        grab = GetComponent<Grabbable>();
        var face = GetComponent<CuteFace>();
        if (!face) face = gameObject.AddComponent<CuteFace>();
        face.scale = size;
        if (material && !face.material) face.material = material;
    }

    void FixedUpdate() { long t0 = WhipProf.T(); FixedUpdateTimed(); WhipProf.Add("ProceduralApple.FixedUpdate", t0); }
    void FixedUpdateTimed()
    {
        // Weeble: spring back to upright once it's slow (thrown apples still spin freely).
        if ((grab && grab.Held) || rb.linearVelocity.sqrMagnitude > 4f) return;
        float angle = Mathf.DeltaAngle(0f, rb.rotation);
        rb.angularVelocity += (-angle * uprightSpring - rb.angularVelocity * uprightDamping) * Time.fixedDeltaTime;
    }

    void OnDestroy()
    {
        if (sprite) Destroy(sprite);
        if (tex) Destroy(tex);
        if (physMat) Destroy(physMat);
    }

    void OnDrawGizmos()
    {
        Gizmos.color = skin;
        Gizmos.DrawWireSphere(transform.position, size * 0.5f);
    }

    // ------------------------------------------------------------------ baking

    Texture2D Bake()
    {
        var t = new Texture2D(Res, Res, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            name = "ProceduralApple"
        };
        var px = new Color32[Res * Res];
        float pixel = 2f / Res; // one texel in uv units (uv spans -1..1)
        Color rim = skin * 0.55f; rim.a = 1f;
        Color shade = skin * 0.78f; shade.a = 1f;

        for (int y = 0; y < Res; y++)
        {
            for (int x = 0; x < Res; x++)
            {
                Vector2 p = new Vector2((x + 0.5f) / Res * 2f - 1f, (y + 0.5f) / Res * 2f - 1f);
                Color c = new Color(0f, 0f, 0f, 0f);

                // leaf + stem behind the body
                Vector2 q = Rotate(p - new Vector2(0.27f, 0.70f), -35f);
                float dLeaf = (new Vector2(q.x / 0.24f, q.y / 0.10f).magnitude - 1f) * 0.10f;
                Over(ref c, leafColor, Coverage(dLeaf, pixel));
                float dStem = Segment(p, new Vector2(0f, 0.38f), new Vector2(0.07f, 0.78f)) - 0.05f;
                Over(ref c, stemColor, Coverage(dStem, pixel));

                // body: two smoothly merged lobes = natural dimples top and bottom
                float a = (p - new Vector2(-0.2f, -0.06f)).magnitude - 0.6f;
                float b = (p - new Vector2(0.2f, -0.06f)).magnitude - 0.6f;
                float dBody = SmoothMin(a, b, 0.12f);
                float cover = Coverage(dBody, pixel);
                if (cover > 0f)
                {
                    Color body = Color.Lerp(shade, skin, Mathf.Clamp01(-dBody / 0.28f)); // darker towards the edge
                    float dHi = (Rotate(p - new Vector2(-0.36f, 0.16f), 25f) / new Vector2(0.11f, 0.2f)).magnitude - 1f;
                    body = Color.Lerp(body, Color.white, 0.35f * Mathf.Clamp01(-dHi * 4f));
                    body = Color.Lerp(body, rim, Mathf.Clamp01(1f - (-dBody) / 0.035f)); // outline
                    Over(ref c, body, cover);
                }
                px[y * Res + x] = c;
            }
        }
        t.SetPixels32(px);
        t.Apply(false, true);
        return t;
    }

    static float Coverage(float d, float pixel) => Mathf.Clamp01(0.5f - d / pixel);

    static void Over(ref Color dst, Color src, float a)
    {
        if (a <= 0f) return;
        float outA = a + dst.a * (1f - a);
        Color rgb = (src * a + dst * (dst.a * (1f - a))) / Mathf.Max(outA, 1e-5f);
        dst = new Color(rgb.r, rgb.g, rgb.b, outA);
    }

    static float SmoothMin(float a, float b, float k)
    {
        float h = Mathf.Clamp01(0.5f + 0.5f * (b - a) / k);
        return Mathf.Lerp(b, a, h) - k * h * (1f - h);
    }

    static float Segment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 pa = p - a, ba = b - a;
        float h = Mathf.Clamp01(Vector2.Dot(pa, ba) / Vector2.Dot(ba, ba));
        return (pa - ba * h).magnitude;
    }

    static Vector2 Rotate(Vector2 v, float deg)
    {
        float r = deg * Mathf.Deg2Rad, c = Mathf.Cos(r), s = Mathf.Sin(r);
        return new Vector2(c * v.x - s * v.y, s * v.x + c * v.y);
    }
}
