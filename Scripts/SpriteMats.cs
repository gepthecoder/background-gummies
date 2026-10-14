using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Every picture gets its own copy of the sprite material. Unity 6 can mix up the pictures of SpriteRenderers that share
/// a material once they've been switched off and on (Unity issue UUM-98517): the skateboard and the paper plane drew the
/// buddy's leaf or tail instead (whatever sprite was drawn just before them), the dino's spikes drew its tail. With one
/// copy per picture, sprites that share a material always show the same picture, so there's nothing left to mix up.
/// BuddyLayer puts every sprite of a buddy on the copy for its picture (and its lines on a plain one).
/// </summary>
public static class SpriteMats
{
    // the original material -> (texture -> its copy), and each copy -> the original it copies
    static readonly Dictionary<Material, Dictionary<Texture, Material>> s_copies = new Dictionary<Material, Dictionary<Texture, Material>>();
    static readonly Dictionary<Material, Material> s_original = new Dictionary<Material, Material>();
    static readonly List<Texture> s_dead = new List<Texture>();
    static int s_count, s_sweepAt = 128;

    /// <summary>The copy of baseMat for this texture (made the first time it's asked for).</summary>
    public static Material For(Material baseMat, Texture tex)
    {
        if (!baseMat || !tex) return baseMat;
        if (!s_copies.TryGetValue(baseMat, out var byTex)) s_copies[baseMat] = byTex = new Dictionary<Texture, Material>();
        if (byTex.TryGetValue(tex, out Material m) && m) return m;
        if (s_count >= s_sweepAt) Sweep();
        m = new Material(baseMat) { name = baseMat.name + " (" + tex.name + ")", mainTexture = tex };
        byTex[tex] = m;
        s_original[m] = baseMat;
        s_count++;
        return m;
    }

    /// <summary>Puts a sprite drawn with baseMat (or one of its copies) on the copy for its picture. Other materials are
    /// left alone.</summary>
    public static void Fit(SpriteRenderer sr, Material baseMat)
    {
        if (!sr || !baseMat) return;
        Material cur = sr.sharedMaterial;
        if ((object)cur == null) return;
        bool ours = s_original.TryGetValue(cur, out Material o) && o == baseMat;
        if (!ours && cur != baseMat) return;
        Sprite sp = sr.sprite;
        Texture tex = sp ? sp.texture : null;
        if (!tex) return;
        Material want = For(baseMat, tex);
        if (cur != want) sr.sharedMaterial = want;
    }

    /// <summary>Puts a line drawn with baseMat on a plain (white) copy of its own, away from the sprites.</summary>
    public static void FitLine(Renderer r, Material baseMat)
    {
        if (!r || !baseMat || r.sharedMaterial != baseMat) return;
        Material want = For(baseMat, Texture2D.whiteTexture);
        if (want && want != baseMat) r.sharedMaterial = want;
    }

    // drop the copies of pictures that are gone (a buddy repaints its parts when it changes colour)
    static void Sweep()
    {
        s_count = 0;
        foreach (var byTex in s_copies.Values)
        {
            s_dead.Clear();
            foreach (var kv in byTex) if (!kv.Key || !kv.Value) s_dead.Add(kv.Key);
            foreach (var t in s_dead)
            {
                Material m = byTex[t];
                byTex.Remove(t);
                if ((object)m == null) continue;
                s_original.Remove(m);
                if (m) Object.Destroy(m);
            }
            s_count += byTex.Count;
        }
        s_sweepAt = Mathf.Max(128, s_count * 2);
    }
}
