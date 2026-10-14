using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// Writes how every buddy's face is drawn right now (each face sprite: on, visible, sprite, draw order, mask, colour,
/// position, size, material; the body's draw order; its layer and the windows covering it; every sprite mask in the scene)
/// to whipit_faces.txt next to the saves, and to the log. Chat "debug faces" in the app, or WhipIt > Debug > Dump Faces.
/// </summary>
public static class FaceDebug
{
    public static string FilePath => Path.Combine(Application.persistentDataPath, "whipit_faces.txt");

    public static string Dump()
    {
        var sb = new StringBuilder();
        sb.Append($"WhipIt faces {System.DateTime.Now:yyyy-MM-dd HH:mm:ss}, background mode {Occlusion.Enabled}, windows {DesktopPlatforms.Windows.Count}, frame {Time.frameCount}\n");
        foreach (var b in GummyBody.All)
        {
            if (!b) continue;
            var layer = b.GetComponent<BuddyLayer>();
            var face = b.GetComponent<CuteFace>();
            sb.Append($"{b.name}: size {b.size:0.000} body order {b.sortingOrder} centre {b.Center.x:0.000},{b.Center.y:0.000} angle {b.Angle:0.0} " +
                      $"layer {(layer ? layer.Layer.ToString() : "-")} covering {(layer ? layer.Covering.ToString() : "-")} hiding {b.Hiding}\n");
            sb.Append(face ? face.Describe() : "  no CuteFace\n");
        }
        foreach (var m in Object.FindObjectsByType<SpriteMask>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            var tr = m.transform;
            sb.Append($"mask {m.name} (under {(tr.parent ? tr.parent.name : "-")}): on {m.enabled} active {m.gameObject.activeInHierarchy} sprite {(m.sprite ? m.sprite.name : "NULL")} " +
                      $"range {m.isCustomRangeActive} {m.backSortingOrder}..{m.frontSortingOrder} pos {tr.position.x:0.00},{tr.position.y:0.00} size {tr.lossyScale.x:0.00},{tr.lossyScale.y:0.00}\n");
        }
        string text = sb.ToString();
        try { File.WriteAllText(FilePath, text); } catch (System.Exception e) { Debug.LogWarning("[WhipIt] Couldn't write " + FilePath + ": " + e.Message); }
        Debug.Log("[WhipIt] Faces written to " + FilePath + "\n" + text);
        return text;
    }
}
