using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Codebase;

internal static class StatBatchCodec
{
    internal const string Prefix = "oomtm450_statsBATCH";
    internal const int MaximumPayloadBytes = 48000;

    internal static bool TryEncode(IReadOnlyList<KeyValuePair<string, string>> entries, out List<string> payloads)
    {
        payloads = new List<string>();
        var builder = new StringBuilder();
        int bytes = 0;
        int count = 0;
        foreach (var entry in entries)
        {
            if (entry.Key == null || !entry.Key.StartsWith(Prefix, StringComparison.Ordinal) || entry.Value == null) return false;
            string name = entry.Key.Substring(Prefix.Length);
            string encoded = name.Length.ToString(CultureInfo.InvariantCulture) + ":" + name + entry.Value.Length.ToString(CultureInfo.InvariantCulture) + ":" + entry.Value;
            int size = Encoding.UTF8.GetByteCount(encoded);
            if (size > MaximumPayloadBytes) return false; // Caller sends the existing legacy packet.
            if (bytes + size > MaximumPayloadBytes || count == 4096)
            {
                payloads.Add(builder.ToString()); builder.Clear(); bytes = 0; count = 0;
            }
            builder.Append(encoded); bytes += size; count++;
        }
        if (builder.Length > 0) payloads.Add(builder.ToString());
        return true;
    }

    internal static bool TryDecode(string payload, ISet<string> allowedNames, out List<KeyValuePair<string, string>> entries)
    {
        entries = new List<KeyValuePair<string, string>>();
        if (string.IsNullOrEmpty(payload) || Encoding.UTF8.GetByteCount(payload) > MaximumPayloadBytes) return false;
        int offset = 0;
        while (offset < payload.Length)
        {
            if (entries.Count >= 4096 || !ReadPart(payload, ref offset, out string name) || !allowedNames.Contains(Prefix + name) || !ReadPart(payload, ref offset, out string value)) return false;
            entries.Add(new KeyValuePair<string, string>(Prefix + name, value));
        }
        return true;
    }

    private static bool ReadPart(string payload, ref int offset, out string value)
    {
        value = null;
        int colon = payload.IndexOf(':', offset);
        if (colon < offset || colon - offset > 5 || !int.TryParse(payload.Substring(offset, colon - offset), NumberStyles.None, CultureInfo.InvariantCulture, out int length)) return false;
        offset = colon + 1;
        if (length > payload.Length - offset) return false;
        value = payload.Substring(offset, length);
        offset += length;
        return true;
    }
}
