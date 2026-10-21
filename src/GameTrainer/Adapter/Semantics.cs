using System.Text.Json;

namespace GameTrainer.Adapter;

/// <summary>
/// 通用属性语义。跨游戏的"同一个东西"。
///
/// 设计目标：换游戏时，使用者关心的永远是「血量」「金币」「能量」这些**概念**，
/// 而不是 or / vie / Gold / HP 这些**实现名**。适配包负责把语义映射到实现，
/// 上层命令与 UI 只认语义，因此新增游戏不需要改主程序。
/// </summary>
internal static class Semantics
{
    /// <summary>当前生命值。</summary>
    public const string Health = "health";
    /// <summary>生命上限。</summary>
    public const string HealthMax = "health_max";
    /// <summary>主要货币（局内/常用金币）。</summary>
    public const string Gold = "gold";
    /// <summary>累计/总量货币。</summary>
    public const string GoldTotal = "gold_total";
    /// <summary>可消耗能量/行动力。</summary>
    public const string Energy = "energy";
    /// <summary>能量累积进度。</summary>
    public const string EnergyCharge = "energy_charge";
    /// <summary>能量累积上限。</summary>
    public const string EnergyChargeMax = "energy_charge_max";
    /// <summary>攻击/力量。</summary>
    public const string Attack = "attack";
    /// <summary>魔法/法术强度。</summary>
    public const string Magic = "magic";
    /// <summary>防御/护盾。</summary>
    public const string Defense = "defense";
    /// <summary>经验/等级。</summary>
    public const string Level = "level";
    /// <summary>背包/容量上限。</summary>
    public const string Capacity = "capacity";

    /// <summary>内置语义表：语义 → 中文名 + 常见别名（用于自动探测时打分）。</summary>
    public static readonly IReadOnlyDictionary<string, SemanticInfo> All =
        new Dictionary<string, SemanticInfo>(StringComparer.OrdinalIgnoreCase)
        {
            [Health] = new("生命值", new[] { "vie", "hp", "health", "life", "lifebar", "lives", "sante" }),
            [HealthMax] = new("生命上限", new[] { "viemax", "maxhp", "maxhealth", "maxlife", "hpmax", "santeMax" }),
            [Gold] = new("金币", new[] { "or", "gold", "coin", "coins", "money", "monnaie", "pieces" }),
            [GoldTotal] = new("累计金币", new[] { "orever", "totalgold", "goldtotal", "orTotal" }),
            [Energy] = new("能量", new[] { "capacite", "energy", "mana", "stamina", "energie", "action", "actions" }),
            [EnergyCharge] = new("能量累积", new[] { "capacite_seuilactuel", "energyCharge", "charge", "seuil" }),
            [EnergyChargeMax] = new("能量累积上限", new[] { "capacite_seuilmax", "energyChargeMax", "chargemax" }),
            [Attack] = new("攻击", new[] { "pouvoir_force", "attack", "force", "strength", "atk", "power" }),
            [Magic] = new("魔法", new[] { "pouvoir_magie", "magic", "magie", "spell", "int" }),
            [Defense] = new("防御", new[] { "bouclieractif", "defense", "shield", "armor", "bouclier" }),
            [Level] = new("等级", new[] { "level", "niveau", "lvl", "exp", "xp" }),
            [Capacity] = new("容量", new[] { "inventairemax", "capacity", "slots", "bag", "inventory" }),
        };

    public sealed record SemanticInfo(string DisplayName, string[] Aliases);

    /// <summary>语义是否已在内置表中。</summary>
    public static bool IsKnown(string semantic) => All.ContainsKey(semantic);

    /// <summary>取中文显示名；未知语义原样返回。</summary>
    public static string Display(string semantic)
        => All.TryGetValue(semantic, out var i) ? i.DisplayName : semantic;

    /// <summary>
    /// 给一个实际变量名打分，判断它最像哪个语义。用于自动探测时排序。
    /// 返回 (语义, 置信度 0..1)。无匹配返回 (null, 0)。
    ///
    /// 打分规则：
    ///   完全相等                     → 1.0
    ///   按词边界包含别名             → 0.7（别名长度须 >= 3）
    ///   按词边界被别名包含           → 0.5（两边长度都须 >= 3）
    ///
    /// 两个约束都是必需的，来自实测踩坑：
    ///   1. 纯子串匹配会把 constructor、_exportTimestamp 判成 gold
    ///      （因为它们含两字符别名 "or"）。
    ///   2. 即使要求别名 >= 3 字符，_originalViewportWidth 仍会因为
    ///      含 "vie"（Viewport 的前三个字母）被判成 health。
    /// 所以必须按**词边界**匹配：把变量名按驼峰和下划线/连字符拆成词，
    /// 只在完整词上比较。
    /// </summary>
    public static (string Semantic, double Score) Guess(string variableName)
    {
        if (string.IsNullOrWhiteSpace(variableName)) return (null, 0);
        string raw = variableName.Trim();
        string v = raw.ToLowerInvariant();

        // 拆词：驼峰边界 + 下划线/连字符/点，全部转小写
        var words = SplitWords(raw);
        string joined = string.Join("", words);   // playerHealth → playerhealth

        string best = null;
        double bestScore = 0;

        foreach (var (semantic, info) in All)
        {
            foreach (string aliasRaw in info.Aliases)
            {
                string alias = aliasRaw.ToLowerInvariant();
                var aliasWords = SplitWords(aliasRaw);

                double score = 0;

                // 1) 完全相等（含去掉分隔符后相等）
                if (v == alias || joined == alias)
                {
                    score = 1.0;
                }
                // 2) 别名作为完整词出现在变量名里（playerHealth 的 Health）
                else if (alias.Length >= 3 && words.Contains(alias))
                {
                    score = 0.7;
                }
                // 3) 变量名作为完整词出现在别名里
                else if (v.Length >= 3 && aliasWords.Contains(v))
                {
                    score = 0.5;
                }
                // 4) 多词别名逐词匹配（如 capacite_seuilactuel）
                else if (aliasWords.Count >= 2 && words.Count >= 2)
                {
                    int hit = aliasWords.Count(w => words.Contains(w) && w.Length >= 3);
                    if (hit >= 2) score = 0.7;
                }

                if (score > bestScore) { bestScore = score; best = semantic; }
            }
        }
        return (best, bestScore);
    }

    /// <summary>
    /// 把标识符拆成词。
    /// playerHealth   → [player, health]
    /// MAX_HEALTH     → [max, health]
    /// _exportTimestamp → [export, timestamp]
    /// </summary>
    private static List<string> SplitWords(string s)
    {
        var words = new List<string>();
        if (string.IsNullOrEmpty(s)) return words;

        var cur = new System.Text.StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '_' || c == '-' || c == '.' || c == ' ')
            {
                if (cur.Length > 0) { words.Add(cur.ToString().ToLowerInvariant()); cur.Clear(); }
                continue;
            }

            // 驼峰边界：小写/数字 → 大写 时断开
            if (i > 0 && char.IsUpper(c) && cur.Length > 0
                && (char.IsLower(s[i - 1]) || char.IsDigit(s[i - 1])))
            {
                words.Add(cur.ToString().ToLowerInvariant());
                cur.Clear();
            }

            cur.Append(c);
        }
        if (cur.Length > 0) words.Add(cur.ToString().ToLowerInvariant());
        return words;
    }
}
