using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using Codebase;
using Newtonsoft.Json;

namespace OPLStats_Stats;

public partial class Stats
{
    private static readonly OrderedWorkQueue _fileWork = new OrderedWorkQueue();
    private static readonly ConditionalWeakTable<FrozenGameExportSnapshot, Lazy<string>> _exportJsonCache = new ConditionalWeakTable<FrozenGameExportSnapshot, Lazy<string>>();
    private static readonly Dictionary<string, string> _lastClientFileContents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static Lazy<string> CaptureExportJson(FrozenGameExportSnapshot snapshot)
    {
        // Building the payload can update role summaries, so do that on the game thread.
        // The returned dictionary owns its data; only serialization runs in the worker.
        return _exportJsonCache.GetValue(snapshot, key => {
            var payload = BuildGameStatsExportPayload(key);
            return new Lazy<string>(() => JsonConvert.SerializeObject(payload, Formatting.Indented));
        });
    }

    private static void QueueClientFiles(Dictionary<string, string> files)
    {
        _fileWork.Enqueue(() => {
            foreach (var file in files)
            {
                if (_lastClientFileContents.TryGetValue(file.Key, out string previous) && previous == file.Value && File.Exists(file.Key)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(file.Key));
                File.WriteAllText(file.Key, file.Value);
                _lastClientFileContents[file.Key] = file.Value;
            }
        }, error => { if (error != null) Logging.LogError($"Error writing client team stats file: {error}", _clientConfig); });
    }

    private static void DrainFileWork() => _fileWork.Drain();
}
