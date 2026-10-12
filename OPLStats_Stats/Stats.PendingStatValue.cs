using System.Globalization;

namespace OPLStats_Stats;

public partial class Stats
{
    private readonly struct PendingStatValue
    {
        private readonly string _text;
        private readonly double _number;
        private readonly string _format;
        private readonly CultureInfo _culture;

        internal PendingStatValue(string text) { _text = text; _number = 0; _format = null; _culture = null; }
        internal PendingStatValue(double number, string format)
        {
            _text = null; _number = number; _format = format;
            _culture = CultureInfo.CurrentCulture;
        }
        public override string ToString() => _format == null ? _text : _number.ToString(_format, _culture);
    }

    private static void QueueStatUpdate(string name, double value, string format)
    {
        _pendingStatUpdates[name] = new PendingStatValue(value, format);
        MarkPreviewLiveStatsChanged();
    }
}
