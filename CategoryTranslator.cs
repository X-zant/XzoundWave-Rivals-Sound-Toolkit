using CUE4Parse.UE4.Localization;

namespace MRAudioKit;

/// <summary>
/// Turns the Chinese Category labels into English.
///
/// The developers authored that column as a raw FName, not localized text, so the
/// game ships no translation for it. The proper nouns are still derivable: the same
/// locres exists in Chinese and English, so comparing the two by key gives a
/// Chinese-to-English map for everything the game DISPLAYS. That is where
/// "月光骑士" becomes "Moon Knight" and "宇宙能量增幅链" becomes "C.Y.A." without
/// anyone typing them here.
///
/// The game-term vocabulary below IS hand-written -- it has no in-game counterpart.
/// Anything neither source covers is left in Chinese rather than guessed at, so a
/// gap is visible instead of silently wrong.
/// </summary>
public static class CategoryTranslator
{
    /// <summary>
    /// Authoring vocabulary, matched a whole segment at a time. Order does not matter
    /// for exact matches; the fallback pass sorts by length.
    /// </summary>
    private static readonly Dictionary<string, string> Glossary = new()
    {
        // line kinds
        ["互动语音"] = "interaction line",
        ["特殊互动语音"] = "special interaction line",
        ["单人语音"] = "solo line",
        ["特殊单人语音"] = "special solo line",
        ["出场语音"] = "entrance line",
        ["切换角色语音"] = "hero swap line",
        ["选角语音"] = "hero select line",
        ["语音"] = "line",

        // combat
        ["击败"] = "KO",
        ["特殊击败"] = "special KO",
        ["看到击败"] = "saw a KO",
        ["看到被击败"] = "saw an ally go down",
        ["被击败"] = "was KO'd",
        ["助攻击败"] = "assist KO",
        ["近战击败"] = "melee KO",
        ["地形击败"] = "environmental KO",
        ["大招击败"] = "ultimate KO",
        ["小技能击败"] = "minor ability KO",
        ["三重击败"] = "triple KO",
        ["四重击败"] = "quadruple KO",
        ["五重击败"] = "quintuple KO",
        ["击败狙击手"] = "KO'd a sniper",
        ["看到狙击手"] = "saw a sniper",
        ["狙击手"] = "sniper",
        ["重生"] = "respawn",
        ["复仇"] = "revenge",
        ["团灭"] = "team wipe",
        ["回复"] = "recovering",

        // abilities
        ["小技能"] = "minor ability",
        ["连携技能"] = "team-up ability",
        ["终极技能"] = "ultimate",
        ["大招"] = "ultimate",
        ["充能完毕"] = "fully charged",
        ["充能中"] = "charging",
        ["低于"] = "below",
        ["高于"] = "above",
        ["拾取血包"] = "picked up a health pack",

        // buffs
        ["正面buff"] = "buff",
        ["负面buff"] = "debuff",
        ["受控"] = "controlled",
        ["易伤"] = "vulnerable",
        ["禁疗"] = "healing blocked",
        ["虚弱"] = "weakened",
        ["治疗"] = "healing",
        ["濒危状态"] = "critical",
        ["非濒危状态"] = "not critical",
        ["加攻"] = "attack up",
        ["霸体"] = "unstoppable",
        ["加盾"] = "shielded",
        ["减伤"] = "damage reduction",
        ["净化"] = "cleansed",

        // objectives
        ["顺利护送运载目标"] = "escort going well",
        ["护送运载目标不顺利"] = "escort going badly",
        ["占领目标区域"] = "capturing the objective",
        ["庆祝"] = "celebrating",
        ["提示交战"] = "calling to engage",
        ["提示防守"] = "calling to defend",
        ["提示进攻"] = "calling to attack",
        ["时间将尽"] = "time running out",
        ["进攻方"] = "attacking",
        ["防守方"] = "defending",
        ["进攻"] = "attack",
        ["防守"] = "defend",

        // pings and signals
        ["信号轮盘"] = "signal wheel",
        ["响应信号"] = "answering a signal",
        ["标记场景位置"] = "pinging a location",
        ["标记空地"] = "pinging open ground",
        ["标记破碎地形"] = "pinging broken terrain",
        ["标记目标"] = "pinging a target",
        ["标记任务区域"] = "pinging an objective",
        ["标记未被我方占领的任务区域"] = "pinging an objective we do not hold",
        ["标记已被我方占领的任务区域"] = "pinging an objective we hold",
        ["标记任务载具"] = "pinging the payload",
        ["进攻方标记任务载具"] = "attacker pings the payload",
        ["防守方标记任务载具"] = "defender pings the payload",
        ["请求支援"] = "requesting help",
        ["敌人位置"] = "enemy position",
        ["不行"] = "no",

        // state and misc
        ["当前状态"] = "status",
        ["高血量"] = "high health",
        ["低血量"] = "low health",
        ["敌方人数优势"] = "enemy has the numbers",
        ["我方人数优势"] = "we have the numbers",
        ["敌人出现"] = "an enemy appears",
        ["敌人从背后击中队友"] = "an enemy hits a teammate from behind",
        ["敌人"] = "enemy",
        ["己方"] = "own team",
        ["地图彩蛋"] = "map easter egg",
        ["看到"] = "saw",
        ["摧毁"] = "destroyed",
        ["传送门"] = "portal",
        ["电子蛛巢"] = "Spider-Nest",
        ["重生装置"] = "respawn beacon",
        ["机枪塔形态"] = "turret form",
    };

    private static GameSession _for;
    private static Dictionary<string, string> _names;
    private static List<KeyValuePair<string, string>> _byLength;

    /// <summary>Proper nouns, from the game's own Chinese and English locres.</summary>
    private static Dictionary<string, string> Names(GameSession session)
    {
        if (ReferenceEquals(_for, session) && _names is not null) return _names;
        _for = session;

        var cn = Locres(session, "zh-Hans-CN");
        var en = Locres(session, "en");
        var map = new Dictionary<string, string>();
        foreach (var (key, chinese) in cn)
        {
            if (!en.TryGetValue(key, out var english)) continue;
            if (string.IsNullOrWhiteSpace(chinese) || string.IsNullOrWhiteSpace(english)) continue;
            if (chinese == english) continue;
            // Names, not sentences. Anything longer never appears inside a label.
            if (chinese.Length is < 2 or > 12 || english.Length > 32) continue;
            map.TryAdd(chinese, Case(english));
        }
        _names = map;
        return _names;
    }

    /// <summary>
    /// The game's UI strings are frequently SHOUTED. Inside a sentence-shaped label
    /// that reads as an error, so they are brought back to title case.
    /// </summary>
    private static string Case(string s)
    {
        if (s.Length < 4 || s.Any(char.IsLower)) return s;
        return string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Length <= 3 && w.All(c => !char.IsLetter(c) || char.IsUpper(c)) && w.Contains('.')
                ? w                                    // keep initialisms like C.Y.A.
                : char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));
    }

    private static Dictionary<(string, string), string> Locres(GameSession session, string code)
    {
        var map = new Dictionary<(string, string), string>();
        var path = $"Marvel/Content/Localization/Game/{code}/Game.locres";
        if (!session.Provider.Files.TryGetValue(path, out var f)) return map;
        try
        {
            using var ar = f.CreateReader();
            foreach (var (ns, keys) in new FTextLocalizationResource(ar).Entries)
                foreach (var (k, e) in keys)
                    map[(ns.Str, k.Str)] = e.LocalizedString;
        }
        catch { }
        return map;
    }

    /// <summary>
    /// Segments that neither the game nor the glossary nor the cache can translate --
    /// exactly the set worth sending to an online service, and nothing more.
    /// </summary>
    public static IEnumerable<string> Untranslatable(GameSession session, IEnumerable<string> labels)
    {
        var names = Names(session);
        var seen = new HashSet<string>();
        foreach (var label in labels)
        {
            if (!NeedsTranslation(label)) continue;
            foreach (var raw in label.Split('-', StringSplitOptions.RemoveEmptyEntries))
            {
                var seg = raw.Trim();
                if (!NeedsTranslation(seg)) continue;
                if (Glossary.ContainsKey(seg) || names.ContainsKey(seg) ||
                    LiveTranslate.Lookup(seg) is not null) continue;
                if (!NeedsTranslation(Segment(seg, names))) continue;
                if (seen.Add(seg)) yield return seg;
            }
        }
    }

    public static bool NeedsTranslation(string s) =>
        !string.IsNullOrEmpty(s) && s.Any(c => c >= 0x3400 && c <= 0x9FFF);

    /// <summary>
    /// Translate one label. Labels are "-" separated, and each segment is looked up
    /// whole before anything is replaced piecemeal -- matching fragments first turned
    /// "信号轮盘" into "信号WHEEL", because 轮盘 alone is a word the game translates.
    /// </summary>
    public static string Translate(GameSession session, string text)
    {
        if (!NeedsTranslation(text)) return text;
        var names = Names(session);

        var parts = text.Split('-', StringSplitOptions.RemoveEmptyEntries)
                        .Select(p => Segment(p.Trim(), names));
        return Tidy(string.Join(" - ", parts));
    }

    private static string Segment(string seg, Dictionary<string, string> names)
    {
        if (!NeedsTranslation(seg)) return seg;
        if (Glossary.TryGetValue(seg, out var g)) return g;
        if (names.TryGetValue(seg, out var n)) return n;
        // Anything previously fetched online. Reading the cache never touches the
        // network; fetching is a separate, deliberate action.
        if (LiveTranslate.Lookup(seg) is { } live) return live;

        // Nothing matched the whole segment. Fall back to replacing the longest known
        // phrases inside it, glossary first so game terms beat UI strings.
        _byLength ??= Glossary.OrderByDescending(k => k.Key.Length).ToList();
        var s = seg;
        foreach (var (cn, en) in _byLength)
            if (s.Contains(cn, StringComparison.Ordinal))
                s = s.Replace(cn, " " + en + " ", StringComparison.Ordinal);
        // Whatever Chinese survives is looked up as a WHOLE run. Matching fragments
        // here is what produced "信号WHEEL": the game translates 轮盘 on its own, so
        // a substring pass happily replaced half a word.
        // The interpunct is part of a name, not a separator: without it here,
        // 潘妮·帕克 is matched as 潘妮 and 帕克 and neither is a name on its own.
        s = System.Text.RegularExpressions.Regex.Replace(s, "[㐀-鿿·‧・]+",
            m => names.TryGetValue(m.Value, out var hit) ? " " + hit + " " : m.Value);

        // The offline dictionary is the LAST resort, not the first: it glosses word by
        // word, so asking it before the glossary's phrase pass turned a phrase the
        // glossary knew whole into "surname Cong behind to hit teammate".
        return System.Text.RegularExpressions.Regex.Replace(s, "[㐀-鿿]+",
            m => Cedict.Gloss(m.Value) is { } g && !NeedsTranslation(g) ? " " + g + " " : m.Value);
    }

    private static string Tidy(string s)
    {
        s = s.Replace('，', ',').Replace('·', ' ').Replace('、', ',');
        while (s.Contains("  ")) s = s.Replace("  ", " ");
        s = s.Replace(" ,", ",").Replace(" - ", " - ").Trim(' ', '-', ',');
        while (s.Contains("  ")) s = s.Replace("  ", " ");
        return s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
    }
}
