using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// What you told the buddies in the chat box, kept on this PC (chat_memory.json in the app's data folder): your name,
/// a few things you like and don't like, your favourite things, what you're working on and a few notes ("remember
/// that..."). Only what you typed into the chat box and asked them to remember; "forget everything" deletes it.
/// All the buddies share it.
/// </summary>
public static class ChatMemory
{
    public const int Keep = 5;

    [Serializable]
    class Data
    {
        public string name = "", workingOn = "";
        public string[] likes = new string[0], dislikes = new string[0], notes = new string[0];
        public string[] favWhat = new string[0], favIs = new string[0];
    }

    static Data d;

    public static string FilePath => Path.Combine(Application.persistentDataPath, "chat_memory.json");

    static Data D
    {
        get
        {
            if (d == null) Load();
            return d;
        }
    }

    public static string Name => D.name;
    public static string WorkingOn => D.workingOn;
    public static IList<string> Likes => D.likes;
    public static IList<string> Dislikes => D.dislikes;
    public static IList<string> Notes => D.notes;
    public static bool Empty => D.name.Length == 0 && D.workingOn.Length == 0 && D.likes.Length == 0 && D.dislikes.Length == 0 &&
                                D.notes.Length == 0 && D.favWhat.Length == 0;

    public static void SetName(string name) { D.name = name ?? ""; Save(); }
    public static void SetWorkingOn(string what) { D.workingOn = what ?? ""; Save(); }
    public static void AddLike(string what) { D.likes = Add(D.likes, what); D.dislikes = Remove(D.dislikes, what); Save(); }
    public static void AddDislike(string what) { D.dislikes = Add(D.dislikes, what); D.likes = Remove(D.likes, what); Save(); }
    public static void AddNote(string what) { D.notes = Add(D.notes, what); Save(); }

    public static void SetFavorite(string what, string value)
    {
        var w = new List<string>(D.favWhat);
        var v = new List<string>(D.favIs);
        int i = w.IndexOf(what);
        if (i >= 0) { w.RemoveAt(i); v.RemoveAt(i); }
        w.Add(what);
        v.Add(value);
        while (w.Count > Keep) { w.RemoveAt(0); v.RemoveAt(0); }
        D.favWhat = w.ToArray();
        D.favIs = v.ToArray();
        Save();
    }

    /// <summary>Your favourite (food, colour...), or null if you never said.</summary>
    public static string Favorite(string what)
    {
        int i = Array.IndexOf(D.favWhat, what);
        return i >= 0 && i < D.favIs.Length ? D.favIs[i] : null;
    }

    /// <summary>Any one of your favourites as (what, value), or null.</summary>
    public static string[] AnyFavorite()
    {
        if (D.favWhat.Length == 0) return null;
        int i = UnityEngine.Random.Range(0, D.favWhat.Length);
        return new[] { D.favWhat[i], i < D.favIs.Length ? D.favIs[i] : "" };
    }

    public static void ForgetName() { D.name = ""; Save(); }

    /// <summary>Forget everything (and delete the file).</summary>
    public static void Forget()
    {
        d = new Data();
        try { if (File.Exists(FilePath)) File.Delete(FilePath); }
        catch (Exception e) { Debug.LogWarning("[WhipIt] Couldn't delete chat_memory.json: " + e.Message); }
    }

    /// <summary>Read it from disk again (tests, or after another copy of the app changed it).</summary>
    public static void Reload() => d = null;

    // newest last, no duplicates, at most Keep
    static string[] Add(string[] list, string item)
    {
        var l = new List<string>(list);
        l.Remove(item);
        l.Add(item);
        while (l.Count > Keep) l.RemoveAt(0);
        return l.ToArray();
    }

    static string[] Remove(string[] list, string item)
    {
        var l = new List<string>(list);
        l.Remove(item);
        return l.ToArray();
    }

    static void Load()
    {
        d = new Data();
        try
        {
            if (!File.Exists(FilePath)) return;
            var loaded = JsonUtility.FromJson<Data>(File.ReadAllText(FilePath));
            if (loaded == null) return;
            loaded.name = loaded.name ?? "";
            loaded.workingOn = loaded.workingOn ?? "";
            loaded.likes = loaded.likes ?? new string[0];
            loaded.dislikes = loaded.dislikes ?? new string[0];
            loaded.notes = loaded.notes ?? new string[0];
            loaded.favWhat = loaded.favWhat ?? new string[0];
            loaded.favIs = loaded.favIs ?? new string[0];
            if (loaded.favIs.Length != loaded.favWhat.Length)
            {
                int n = Mathf.Min(loaded.favIs.Length, loaded.favWhat.Length);
                Array.Resize(ref loaded.favWhat, n);
                Array.Resize(ref loaded.favIs, n);
            }
            d = loaded;
        }
        catch (Exception e) { Debug.LogWarning("[WhipIt] Couldn't read chat_memory.json: " + e.Message); }
    }

    static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, JsonUtility.ToJson(d));
        }
        catch (Exception e) { Debug.LogWarning("[WhipIt] Couldn't write chat_memory.json: " + e.Message); }
    }

    /// <summary>For the pixel bubble: capitals, accents dropped, only letters / digits / a few marks, at most max characters.</summary>
    public static string Show(string s, int max = 14)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder();
        foreach (char ch in s.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            char c = char.ToUpperInvariant(ch);
            if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == ' ' || c == '\'' || c == '-' || c == '+' || c == '.') sb.Append(c);
        }
        string r = sb.ToString().Trim();
        while (r.Contains("  ")) r = r.Replace("  ", " ");
        if (r.Length > max)
        {
            int cut = r.LastIndexOf(' ', max);
            r = cut > 3 ? r.Substring(0, cut) : r.Substring(0, max);
        }
        return r;
    }
}
