using Content.Server.Antag;
using Content.Server.GameTicking.Rules.Components;
using Content.Shared.Dataset;
using Content.Shared.GameTicking.Components;
using Robust.Server.Player;
using Robust.Shared.Configuration;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using System.Text;
using Content.Shared._Goobstation.CCVar;

namespace Content.Server.GameTicking.Rules;

// btw this code is god jolly.
// a single look at it burns my sleigh.
// i do not wish to gift it presents.
// all that matters is that it works.
// regards.

/// <summary>
///     Dynamic rule system generates a random amount of points each round,
///     assigns random weighted threats, *edits the dynamic rules minmax antag count correspondingly*
///     and adds the gamerule to the current pool.
/// </summary>
public sealed partial class DynamicRuleSystem : GameRuleSystem<DynamicRuleComponent>
{
    [Dependency] private readonly GameTicker _gameTicker = default!;
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly AntagSelectionSystem _antag = default!;
    [Dependency] private readonly IRobustRandom _rand = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly IComponentFactory _compfact = default!;
    [Dependency] private readonly INetConfigurationManager _cfg = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<GameRuleAddedEvent>(OnGameRuleAdded);
    }

    #region gamerule processing

    public List<SDynamicRuleset?> GetRulesets(ProtoId<DatasetPrototype> dataset)
    {
        var l = new List<SDynamicRuleset?>();

        foreach (var rprot in _proto.Index(dataset).Values)
        {
            var ruleset = _proto.Index(rprot);

            if (!ruleset.TryGetComponent<DynamicRulesetComponent>(out var drc, _compfact)
            || !ruleset.TryGetComponent<GameRuleComponent>(out var grc, _compfact))
                continue;

            if (drc.Weight == 0)
                continue;

            l.Add(new SDynamicRuleset(ruleset, drc, grc));
        }
        return l;
    }
    public SDynamicRuleset? WeightedPickRule(List<SDynamicRuleset?> rules)
    {
        // get total weight of all rules
        var sum = 0f;
        foreach (var rule in rules)
            if (rule != null)
                sum += rule.DynamicRuleset.Weight;

        var accumulated = 0f;

        var rand = _rand.NextFloat() * sum;

        foreach (var rule in rules)
        {
            if (rule == null)
                continue;

            accumulated += rule.DynamicRuleset.Weight;

            if (accumulated >= rand)
                return rule;
        }

        return null;
    }

    /// <summary>
    ///     Dynamic gamemode roundstart behavior
    /// </summary>
    protected override void Started(EntityUid uid, DynamicRuleComponent component, GameRuleComponent gameRule, GameRuleStartedEvent args)
    {
        base.Started(uid, component, gameRule, args);

        var playersCount = _antag.GetAliveConnectedPlayers(_playerManager.Sessions).Count;

        // lowpop processing
        // test scenario: 100 players and 20 lowpop = 80 minimum threat
        var lowpopThreshold = (float) _cfg.GetCVar(GoobCVars.LowpopThreshold.Name);
        var minThreat = 0f + Math.Max(playersCount - lowpopThreshold, 0);

        // highpop processing
        // test scenario: 100 players and 70 highpop threshold = + 60 more max threat
        var highpopThreshold = (float) _cfg.GetCVar(GoobCVars.HighpopThreshold.Name);
        var maxThreat = playersCount < lowpopThreshold ? component.MaxThreat / 2 : component.MaxThreat;
        if (playersCount >= highpopThreshold)
            maxThreat += (playersCount - highpopThreshold) * 2;

        var threat = _rand.NextFloat(minThreat, maxThreat);

        // generate a random amount of points
        component.ThreatLevel = threat;

        // distribute budgets
        component.RoundstartBudget = _rand.NextFloat(threat / 2.5f, threat); // generally more roundstart threat
        component.MidroundBudget = component.ThreatLevel - component.RoundstartBudget;

        // get gamerules from dataset and add them to draftedRules
        var draftedRules = new List<SDynamicRuleset?>();
        var roundstartRules = GetRulesets(component.RoundstartRulesPool);
        foreach (var rule in roundstartRules)
        {
            if (rule == null) continue;

            // exclude gamerules if not enough overall budget or players
            if (rule.DynamicRuleset.Weight == 0
            || component.RoundstartBudget < rule.DynamicRuleset.Cost
            || rule.GameRule.MinPlayers > playersCount)
                continue;

            draftedRules.Add(rule);
        }

        // remove budget and try to add these drafted rules
        var pickedRules = new List<SDynamicRuleset>();
        var roundstartBudget = component.RoundstartBudget;
        while (roundstartBudget > 0)
        {
            var ruleset = WeightedPickRule(draftedRules);

            if (ruleset == null)
                break;

            var r = ruleset.DynamicRuleset;

            var cost = pickedRules.Contains(ruleset) ? r.ScalingCost : r.Cost;
            if (cost > roundstartBudget)
            {
                draftedRules[draftedRules.IndexOf(ruleset)] = null;
                continue;
            }

            roundstartBudget -= cost;
            pickedRules.Add(ruleset);

            // if one chosen ruleset is high impact we cancel every other high impact ruleset
            if (r.HighImpact && !component.Unforgiving)
                foreach (var otherRule in draftedRules)
                    if (otherRule != null && otherRule.DynamicRuleset.HighImpact)
                        draftedRules[draftedRules.IndexOf(otherRule)] = null;
        }

        // spend budget and start the gamer rule
        // it will automatically get added using OnGameRuleAdded()
        foreach (var rule in pickedRules)
            _gameTicker.StartGameRule(rule.Prototype.ID);

        // save up leftout roundstart budget for midround rolls
        component.MidroundBudget += roundstartBudget;
    }

    #endregion

    #region roundend text

    protected override void AppendRoundEndText(EntityUid uid, DynamicRuleComponent component, GameRuleComponent gameRule, ref RoundEndTextAppendEvent args)
    {
        base.AppendRoundEndText(uid, component, gameRule, ref args);
        var sb = new StringBuilder();

        var threatLevel = 0f;
        var roundstartBudget = 0f;
        var midroundBudget = 0f;

        foreach (var dynamicRule in EntityQuery<DynamicRuleComponent>())
        {
            threatLevel += dynamicRule.ThreatLevel;
            roundstartBudget += dynamicRule.RoundstartBudget;
            midroundBudget += dynamicRule.MidroundBudget;
        }

        // total threat & points:
        sb.AppendLine(Loc.GetString("dynamic-roundend-totalthreat", ("points", (int) threatLevel)));
        sb.AppendLine(Loc.GetString("dynamic-roundend-points-roundstart", ("points", (int) roundstartBudget)));
        sb.AppendLine(Loc.GetString("dynamic-roundend-points-midround", ("points", (int) midroundBudget)));

        // all executed gamerules:
        sb.AppendLine($"\n{Loc.GetString("dynamic-roundend-gamerules-title")}");
        sb.AppendLine(GenerateLocalizedGameruleList(component.ExecutedRules));

        args.AppendAtStart(sb.ToString());
    }
    private string GenerateLocalizedGameruleList(List<(EntProtoId, EntityUid?)> executedGameRules)
    {
        var sb = new StringBuilder();

        var grd = new Dictionary<string, (int, float)>();
        foreach (var gamerule in executedGameRules)
        {
            if (!_proto.Index(gamerule.Item1).TryGetComponent<DynamicRulesetComponent>(out var dynset, _compfact))
                continue;

            var name = dynset.NameLoc;

            var executed = grd.ContainsKey(name);
            var executedTimes = executed ? grd[name].Item1 + 1 : 1;
            var cost = executed ? grd[name].Item2 + dynset.ScalingCost : dynset.Cost;

            if (executed)
                grd[name] = (executedTimes, cost);
            else grd.Add(name, (executedTimes, cost));
        }
        foreach (var gr in grd)
            sb.AppendLine($"{Loc.GetString(gr.Key)} (x{grd[gr.Key].Item1}) - {Loc.GetString("dynamic-gamerule-threat-perrule", ("num", grd[gr.Key].Item2))}");

        return sb.ToString();
    }

    #endregion

    #region events

    private void OnGameRuleAdded(ref GameRuleAddedEvent args)
    {
        // nothing goes unnoticed
        // killing 2 birds here because now i don't need to hook up to it from another system
        foreach (var dgr in EntityQuery<DynamicRuleComponent>())
            dgr.ExecutedRules.Add((args.RuleId, args.RuleEntity));
    }

    #endregion
}

// this struct is used only for making my job of handling all the game rules much easier and cleaner
// this should not be used outside of coding (e.g. in yaml)
// regards
public sealed class SDynamicRuleset
{
    public EntityPrototype Prototype;
    public DynamicRulesetComponent DynamicRuleset;
    public GameRuleComponent GameRule;

    public SDynamicRuleset(EntityPrototype prot, DynamicRulesetComponent drc, GameRuleComponent grc)
    {
        Prototype = prot;
        DynamicRuleset = drc;
        GameRule = grc;
    }
}
