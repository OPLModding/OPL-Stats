using System;
using System.Collections.Generic;
using System.Linq;
using Codebase;
using Unity.Netcode;

namespace OPLStats_Stats;

public partial class Stats
{
    private const string STAT_CAPABILITIES = "oomtm450_statsCAPABILITIES";
    private const string COMBINED_STAT_BATCH = "oomtm450_statsBATCH_ALL_V1";
    private static readonly HashSet<ulong> _combinedBatchClients = new HashSet<ulong>();
    private static int _statReceiveDepth;
    private static bool _clientTeamFilesDirty;

    private static bool SendOrCollectStatBatch(string name, string value, List<KeyValuePair<string, string>> collected)
    {
        if (collected == null) return SafeSendDataToAll(name, value);
        if (!ValidateStringForNetwork(value, name, out _)) return false;
        collected.Add(new KeyValuePair<string, string>(name, value));
        return true;
    }

    private static void SendCollectedStatBatches(List<KeyValuePair<string, string>> collected)
    {
        if (collected == null || collected.Count == 0) return;
        bool encoded = StatBatchCodec.TryEncode(collected, out var payloads);
        foreach (ulong client in NetworkManager.Singleton.ConnectedClientsIds.ToArray())
        {
            if (encoded && _combinedBatchClients.Contains(client))
            {
                foreach (string payload in payloads)
                    NetworkCommunication.SendData(COMBINED_STAT_BATCH, payload, client, "oomtm450_stats_server", ServerConfig);
            }
            else
            {
                foreach (var entry in collected)
                    NetworkCommunication.SendData(entry.Key, entry.Value, client, "oomtm450_stats_server", ServerConfig);
            }
        }
    }
    private static readonly HashSet<string> CombinedBatchNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "oomtm450_statsBATCHSOG",
        "oomtm450_statsBATCHSAVEPERC",
        "oomtm450_statsBATCHBLOCK",
        "oomtm450_statsBATCHHIT",
        "oomtm450_statsBATCHTURNOVER",
        "oomtm450_statsBATCHTAKEAWAY",
        "oomtm450_statsBATCHPASS",
        "oomtm450_statsBATCHPUCKTOUCH",
        "oomtm450_statsBATCHEXIT",
        "oomtm450_statsBATCHENTRY",
        "oomtm450_statsBATCHPOSSESSIONTIME",
        "oomtm450_statsBATCHPUCKBATTLEWINS",
        "oomtm450_statsBATCHPUCKBATTLELOSSES",
        "oomtm450_statsBATCHSHOTATTEMPTS",
        "oomtm450_statsBATCHONICETEAMSHOTATTEMPTS",
        "oomtm450_statsBATCHEXPECTEDGOALS",
        "oomtm450_statsBATCHEXPECTEDGOALSAGAINST",
        "oomtm450_statsBATCHTEAMSHOTS",
        "oomtm450_statsBATCHTEAMSHOTATTEMPTS",
        "oomtm450_statsBATCHHOMEPLATESOGS",
        "oomtm450_statsBATCHTEAMHOMEPLATESOGS",
        "oomtm450_statsBATCHTEAMPASSES",
        "oomtm450_statsBATCHTEAMFACEOFFWINS",
        "oomtm450_statsBATCHTEAMFACEOFFTOTAL",
        "oomtm450_statsBATCHTEAMTAKEAWAYS",
        "oomtm450_statsBATCHTEAMTURNOVERS",
        "oomtm450_statsBATCHTEAMEXITS",
        "oomtm450_statsBATCHTEAMENTRIES",
        "oomtm450_statsBATCHTEAMPOSSESSIONTIME",
        "oomtm450_statsBATCHTEAMPUCKBATTLEWINS",
        "oomtm450_statsBATCHTEAMPUCKBATTLELOSSES",
        "oomtm450_statsBATCHSTICKSAVES",
        "oomtm450_statsBATCHPADSAVES",
        "oomtm450_statsBATCHBODYSAVES",
        "oomtm450_statsBATCHHOMEPLATESAVES",
        "oomtm450_statsBATCHHOMEPLATESHOTSFACED",
    };
}
