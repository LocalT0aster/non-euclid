using System;
using System.Collections.Generic;
using Godot;

public partial class EnemyGrammar : RichTextLabel {
    public int Seed = 0;

    private Dictionary<String, List<String>> Parts = new() {
        ["Bhead"] = ["Head", "[b]Head[/b]"],
        ["Bbody"] = ["Body", "[b]Body[/b]"],
        ["Blegs"] = ["Legs", "[b]Legs[/b]"],
        ["Clegs"] = ["    Blegs  ", "   Blegs Blegs ", " Blegs Blegs Blegs "],
        ["Body"] = ["CoreCoreCoreTail", "CoreCoreTail", "CoreCoreCoreTail"],
        ["Head"] = ["'_'", "o_o", "-_-", "@-@", "Д_Д", "^_^"],
        ["Core"] = ["%", "//", "||", "@"],
        ["Tail"] = ["_/", "_|", "__", "_9"],
        ["Legs"] = ["/\\", "||", "/|", "|\\"],
    };

    public string GetStick() {
        Random rand = Seed != 0 ? new(Seed) : new();
        var S = "(Bhead)Bbody\nClegs";
        bool found;
        do
        {
            found = false;
            foreach (var key in Parts.Keys)
            {
                S = ReplaceFirst(S, key, Parts[key][rand.Next() % Parts[key].Count], out var f);
                found = found || f;
            }
        } while (found);

        return S;
    }

    public override void _Ready() {
        Text = GetStick();
    }
    
    private static string ReplaceFirst(string text, string search, string replace, out bool found) {
        int pos = text.IndexOf(search);
        if (pos < 0)
        {
            found = false;
            return text; // Search string not found
        }

        found = true;
        return text[..pos] + replace + text[(pos + search.Length)..];
    }

}
