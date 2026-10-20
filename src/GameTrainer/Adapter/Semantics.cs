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
    /// </summary>
    public static (string Semantic, double Score) Guess(string variableName)
    {
        if (string.IsNullOrWhiteSpace(variableName)) return (null, 0);
        string v = variableName.Trim().ToLowerInvariant();
        string best = null;
        double bestScore = 0;

        foreach (var (semantic, info) in All)
        {
            foreach (string alias in info.Aliases)
            {
                double score = 0;
                if (v == alias.ToLowerInvariant()) score = 1.0;
                else if (v.Contains(alias.ToLowerInvariant())) score = 0.7;
                else if (alias.ToLowerInvariant().Contains(v) && v.Length >= 3) score = 0.5;

                if (score > bestScore) { bestScore = score; best = semantic; }
            }
        }
        return (best, bestScore);
    }
}
