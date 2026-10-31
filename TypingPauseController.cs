using System;
using System.Collections.Generic;
using System.Globalization;

namespace ACTKeyboundBrawlerAuto
{
    public sealed class TypingPauseController
    {
        public const double ResolvedGuardSeconds = 3.0;
        public const double FallbackGuardSeconds = 3.5;
        public const double StatusGuardSeconds = 0.2;

        private sealed class CastWindow
        {
            public DateTime PauseFrom;
            public DateTime PauseUntil;
            public bool Resolved;
            public string Sequence;
        }

        private readonly object _sync = new object();
        private readonly Dictionary<ulong, CastWindow> _casts = new Dictionary<ulong, CastWindow>();
        private readonly Dictionary<uint, DateTime> _statuses = new Dictionary<uint, DateTime>();

        public static bool IsStopAction(uint action)
        {
            return action == 0xC179 || action == 0xC17C || action == 0xC226;
        }

        public static bool IsStopStatus(uint status)
        {
            return status == 0x14E2 || status == 0x153D || status == 0x14E3;
        }

        private static ulong Key(uint source, uint action)
        {
            return ((ulong)source << 32) | action;
        }

        public bool RecordCastStart(uint source, uint action, double seconds, DateTime observedUtc)
        {
            if (source == 0 || !IsStopAction(action) || Double.IsNaN(seconds) ||
                Double.IsInfinity(seconds) || seconds <= 0 || seconds > 120)
                return false;
            lock (_sync)
            {
                Prune(observedUtc);
                ulong key = Key(source, action);
                CastWindow existing;
                // Binary ActorCast and line 20 describe the same cast. Keep the
                // first observed deadline even if the text callback arrives later.
                if (_casts.TryGetValue(key, out existing) && observedUtc < existing.PauseUntil)
                    return false;
                DateTime end = observedUtc.AddSeconds(seconds);
                _casts[key] = new CastWindow
                {
                    PauseFrom = end,
                    PauseUntil = end.AddSeconds(FallbackGuardSeconds)
                };
                return true;
            }
        }

        public bool RecordResolution(uint source, uint action, string sequence, DateTime observedUtc)
        {
            if (source == 0 || !IsStopAction(action)) return false;
            lock (_sync)
            {
                Prune(observedUtc);
                ulong key = Key(source, action);
                CastWindow window;
                if (!_casts.TryGetValue(key, out window))
                {
                    window = new CastWindow { PauseFrom = observedUtc };
                    _casts[key] = window;
                }
                if (window.Resolved && window.Sequence == sequence) return false;
                if (window.PauseFrom > observedUtc) window.PauseFrom = observedUtc;
                window.PauseUntil = observedUtc.AddSeconds(ResolvedGuardSeconds);
                window.Resolved = true;
                window.Sequence = sequence;
                return true;
            }
        }

        public bool RecordCancellation(uint source, uint action)
        {
            lock (_sync)
            {
                CastWindow window;
                ulong key = Key(source, action);
                // A cancellation only withdraws an unresolved cast, not a live status.
                return _casts.TryGetValue(key, out window) && !window.Resolved && _casts.Remove(key);
            }
        }

        public bool RecordStatus(uint target, uint localPlayer, uint status, bool gained,
            double seconds, DateTime observedUtc)
        {
            if (localPlayer == 0 || target != localPlayer || !IsStopStatus(status)) return false;
            if (gained && (Double.IsNaN(seconds) || Double.IsInfinity(seconds) || seconds <= 0 || seconds > 120))
                return false;
            lock (_sync)
            {
                _statuses[status] = observedUtc.AddSeconds((gained ? seconds : 0) + StatusGuardSeconds);
                return true;
            }
        }

        public bool ProcessLogLine(string line, uint localPlayer, DateTime observedUtc, out string detail)
        {
            detail = null;
            if (String.IsNullOrEmpty(line)) return false;
            int separator = line.IndexOf('|');
            if (separator < 0) return false;
            string type = line.Substring(0, separator);
            if (type != "20" && type != "21" && type != "22" && type != "23" && type != "26" && type != "30")
                return false;
            string[] fields = line.Split('|');
            uint source, action;
            bool changed;
            if (type == "26" || type == "30")
            {
                uint status, target;
                double duration = 0;
                if (fields.Length < 9 || !Hex(fields[2], out status) || !Hex(fields[7], out target) ||
                    (type == "26" && !Double.TryParse(fields[4], NumberStyles.Float, CultureInfo.InvariantCulture, out duration)))
                    return false;
                changed = RecordStatus(target, localPlayer, status, type == "26", duration, observedUtc);
                detail = String.Format(CultureInfo.InvariantCulture,
                    "STATUS|id=0x{0:X}|gained={1}|duration={2:F3}|target=0x{3:X8}", status, type == "26", duration, target);
                return changed;
            }
            if (fields.Length < 6 || !Hex(fields[2], out source) || !Hex(fields[4], out action) || !IsStopAction(action))
                return false;
            if (type == "20")
            {
                double duration;
                if (fields.Length < 9 || !Double.TryParse(fields[8], NumberStyles.Float, CultureInfo.InvariantCulture, out duration))
                    return false;
                changed = RecordCastStart(source, action, duration, observedUtc);
            }
            else if (type == "23")
                changed = RecordCancellation(source, action);
            else
            {
                // Every target emits a line for the same action sequence.
                string sequence = fields.Length > 44 ? fields[44] : fields[1];
                changed = RecordResolution(source, action, sequence, observedUtc);
            }
            detail = String.Format("CAST|line={0}|source=0x{1:X8}|action=0x{2:X}", type, source, action);
            return changed;
        }

        public int RemainingMilliseconds(DateTime nowUtc)
        {
            lock (_sync)
            {
                Prune(nowUtc);
                DateTime until = nowUtc;
                foreach (CastWindow window in _casts.Values)
                    if (nowUtc >= window.PauseFrom && window.PauseUntil > until) until = window.PauseUntil;
                foreach (DateTime end in _statuses.Values)
                    if (end > until) until = end;
                return (int)Math.Ceiling((until - nowUtc).TotalMilliseconds);
            }
        }

        private void Prune(DateTime nowUtc)
        {
            var expired = new List<ulong>();
            foreach (var pair in _casts)
                if (pair.Value.PauseUntil <= nowUtc) expired.Add(pair.Key);
            foreach (ulong key in expired) _casts.Remove(key);
            var expiredStatuses = new List<uint>();
            foreach (var pair in _statuses)
                if (pair.Value <= nowUtc) expiredStatuses.Add(pair.Key);
            foreach (uint status in expiredStatuses) _statuses.Remove(status);
        }

        public void Reset()
        {
            lock (_sync) { _casts.Clear(); _statuses.Clear(); }
        }

        private static bool Hex(string value, out uint result)
        {
            return UInt32.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result);
        }
    }
}
