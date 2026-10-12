using System;
using System.Collections.Generic;
using System.Linq;

namespace OPLStats_Stats;

public partial class Stats
{
    private sealed class ModelSignatureCache
    {
        internal bool Enabled;
        internal string Version, TrainedThrough, Type, Signature;
        internal double Intercept, Minimum, Maximum;
        internal string[] Outcomes;
        internal KeyValuePair<string, double>[] Weights;
    }
    private static ModelSignatureCache _modelSignatureCache;

    private static string GetExpectedGoalsModelCacheSignature()
    {
        var model = _expectedGoalsModel;
        if (model == null) return "none";
        var cached = _modelSignatureCache;
        if (cached != null && cached.Enabled == model.Enabled && cached.Version == model.ModelVersion &&
            cached.TrainedThrough == model.TrainedThrough && cached.Type == model.ModelType &&
            cached.Intercept.Equals(model.Intercept) && cached.Minimum.Equals(model.MinimumProbability) && cached.Maximum.Equals(model.MaximumProbability) &&
            cached.Outcomes.Length == (model.IncludedShotOutcomes?.Count ?? 0) && cached.Weights.Length == (model.Weights?.Count ?? 0))
        {
            bool equal = true;
            for (int i = 0; i < cached.Outcomes.Length; i++)
                if (cached.Outcomes[i] != model.IncludedShotOutcomes[i]) { equal = false; break; }
            int index = 0;
            if (equal && model.Weights != null)
                foreach (var weight in model.Weights)
                {
                    var previous = cached.Weights[index++];
                    if (previous.Key != weight.Key || !previous.Value.Equals(weight.Value)) { equal = false; break; }
                }
            if (equal) return cached.Signature;
        }
        _modelSignatureCache = new ModelSignatureCache {
            Enabled = model.Enabled, Version = model.ModelVersion, TrainedThrough = model.TrainedThrough, Type = model.ModelType,
            Intercept = model.Intercept, Minimum = model.MinimumProbability, Maximum = model.MaximumProbability,
            Outcomes = model.IncludedShotOutcomes?.ToArray() ?? Array.Empty<string>(),
            Weights = model.Weights?.ToArray() ?? Array.Empty<KeyValuePair<string, double>>(),
            Signature = BuildExpectedGoalsModelCacheSignature()
        };
        return _modelSignatureCache.Signature;
    }
}
