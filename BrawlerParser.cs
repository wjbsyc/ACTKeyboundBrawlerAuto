using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ACTKeyboundBrawlerAuto
{
    public enum BrawlerEventType
    {
        None,
        Zone,
        WordAssign,
        Typed,
        WordDisappear,
        CarrierClear,
        CastStart
    }

    public class CarrierWordState
    {
        public uint CarrierId { get; set; }
        public uint PhaseParam { get; set; }
        public uint WordTableTag { get; set; }
        public uint CurrentWordIndex { get; set; }
        public uint NextWordIndex { get; set; }
        public bool HasWord { get; set; }
        public DateTime LastAssignTime { get; set; }
        public int AssignCount { get; set; }
        public long AssignmentRevision { get; set; }
    }

    public class BrawlerEvent
    {
        public BrawlerEventType EventType { get; set; }
        public ushort Opcode { get; set; }
        public uint SourceActor { get; set; }
        public uint TargetActor { get; set; }
        public bool IsLocalPlayer { get; set; }
        public DateTime Timestamp { get; set; }
        public uint ZoneId { get; set; }
        public string ZoneName { get; set; }

        // 0x01E6 / 10000008
        public uint ContentId { get; set; }
        public uint CarrierId { get; set; }
        public uint PhaseParam { get; set; }
        public uint WordTableTag { get; set; }
        public uint CurrentWordIndex { get; set; }
        public uint NextWordIndex { get; set; }
        public uint CurrentWordTableTag { get; set; }
        public uint NextWordTableTag { get; set; }
        public uint CommandId { get; set; }
        public uint CommandParam { get; set; }
        public bool DerivedCarrierId { get; set; }
        public float CastDurationSeconds { get; set; }

        // 0x00BD
        public uint ActionId { get; set; }
        public uint RelatedSequence { get; set; }
        public uint WordHandle { get; set; }
        public uint PlayerCounter { get; set; }
        public bool HasCarrierState { get; set; }
        public long AssignmentRevision { get; set; }

        public string RawHex { get; set; }

        public string WordText { get; set; }
        public string NextWordText { get; set; }
    }

    public class BrawlerParser
    {
        private readonly Dictionary<uint, CarrierWordState> _carriers =
            new Dictionary<uint, CarrierWordState>();

        private readonly object _stateLock = new object();

        private BrawlerEvent _lastLocalWordEffect;
        private long _sequence;

        public uint LocalPlayerId { get; private set; }
        public bool InGoldSaucer { get; private set; }

        public IDictionary<uint, CarrierWordState> Carriers
        {
            get { return _carriers; }
        }

        public void Reset()
        {
            lock (_stateLock)
            {
                _carriers.Clear();
                _lastLocalWordEffect = null;
                InGoldSaucer = false;
                LocalPlayerId = 0;
            }
        }

        public BrawlerEvent ParseBinaryPacket(byte[] message)
        {
            if (message == null || message.Length < BrawlerOpcodes.HEADER_SIZE + 4)
                return null;

            ushort opcode = BitConverter.ToUInt16(message, BrawlerOpcodes.OPCODE_OFFSET);
            if (opcode != BrawlerOpcodes.OP_EVENT_PACKET &&
                opcode != BrawlerOpcodes.OP_EFFECT_RESULT &&
                opcode != BrawlerOpcodes.OP_ACTOR_CAST)
                return null;

            uint sourceActor = BitConverter.ToUInt32(message, BrawlerOpcodes.SOURCE_ACTOR_OFFSET);
            uint targetActor = BitConverter.ToUInt32(message, BrawlerOpcodes.TARGET_ACTOR_OFFSET);
            uint[] payload = ExtractBinaryPayload(message);

            lock (_stateLock)
            {
                return Dispatch(opcode, sourceActor, targetActor, payload);
            }
        }

        public BrawlerEvent ParseLogLine(string logLine)
        {
            if (string.IsNullOrEmpty(logLine))
                return null;

            string[] parts = logLine.Split('|');
            if (parts.Length < 3)
                return null;

            int logType;
            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out logType))
                return null;

            if (logType == 1)
            {
                lock (_stateLock)
                {
                    return ParseZoneLine(parts);
                }
            }

            if (logType != 252 || parts.Length < 11)
                return null;

            uint opcodeSegment;
            if (!TryParseHex(parts[6], out opcodeSegment))
                return null;

            ushort opcode = (ushort)(opcodeSegment >> 16);
            if (opcode != BrawlerOpcodes.OP_EVENT_PACKET &&
                opcode != BrawlerOpcodes.OP_EFFECT_RESULT &&
                opcode != BrawlerOpcodes.OP_ACTOR_CAST)
                return null;

            uint sourceActor;
            uint targetActor;
            if (!TryParseHex(parts[3], out sourceActor) ||
                !TryParseHex(parts[4], out targetActor))
                return null;

            uint[] payload = ExtractTextPayload(parts, 10, parts.Length - 1);
            lock (_stateLock)
            {
                return Dispatch(opcode, sourceActor, targetActor, payload);
            }
        }

        private BrawlerEvent ParseZoneLine(string[] parts)
        {
            if (parts.Length < 3) return null;

            uint zoneId;
            if (!TryParseHex(parts[2], out zoneId)) return null;

            bool wasInside = InGoldSaucer;
            InGoldSaucer = zoneId == BrawlerOpcodes.GOLD_SAUCER_ZONE ||
                           zoneId == BrawlerOpcodes.KEYBOUND_BRAWLER_ZONE;

            if (!wasInside && !InGoldSaucer)
                return null;

            if (!InGoldSaucer)
            {
                _carriers.Clear();
                _lastLocalWordEffect = null;
            }

            return new BrawlerEvent
            {
                EventType = BrawlerEventType.Zone,
                Timestamp = DateTime.Now,
                ZoneId = zoneId,
                ZoneName = parts.Length > 3 ? parts[3] : "",
                SourceActor = 0,
                TargetActor = 0
            };
        }

        private BrawlerEvent Dispatch(ushort opcode, uint sourceActor, uint targetActor, uint[] payload)
        {
            _sequence++;

            // Server_MessageHeader.ActorID 与 LoginUserID 相同的包通常是本机玩家相关的自发包回执。
            if (sourceActor != 0 && sourceActor == targetActor)
                LocalPlayerId = sourceActor;

            if (opcode == BrawlerOpcodes.OP_EVENT_PACKET)
                return ParseEventPacket(sourceActor, targetActor, payload);
            if (opcode == BrawlerOpcodes.OP_ACTOR_CAST)
                return ParseActorCast(sourceActor, targetActor, payload);

            return ParseEffectResult(sourceActor, targetActor, payload);
        }

        private BrawlerEvent ParseActorCast(uint sourceActor, uint targetActor, uint[] payload)
        {
            if (payload.Length < 8)
                return null;

            // 0301/StartCast:
            // P[1] = ActionID，P[2] = float32 读条秒数，P[3] = casting ActorID。
            uint actionId = payload[1] & 0xFFFF;
            uint carrierId = payload[3];
            if (actionId == 0 || carrierId == 0)
                return null;

            byte[] durationBits = BitConverter.GetBytes(payload[2]);
            float duration = BitConverter.ToSingle(durationBits, 0);
            if (float.IsNaN(duration) || float.IsInfinity(duration) ||
                duration < 0 || duration > 120)
                duration = 0;

            return new BrawlerEvent
            {
                EventType = BrawlerEventType.CastStart,
                Opcode = BrawlerOpcodes.OP_ACTOR_CAST,
                SourceActor = sourceActor,
                TargetActor = targetActor,
                IsLocalPlayer = LocalPlayerId != 0 && sourceActor == LocalPlayerId,
                Timestamp = DateTime.Now,
                CarrierId = carrierId,
                ActionId = actionId,
                CastDurationSeconds = duration
            };
        }

        private BrawlerEvent ParseEventPacket(uint sourceActor, uint targetActor, uint[] payload)
        {
            if (payload.Length < 8 || payload[0] != BrawlerOpcodes.WORD_ASSIGN_MARKER0)
                return null;

            if (payload[2] == BrawlerOpcodes.WORD_ASSIGN_COMMAND)
                return ParseWordAssign(sourceActor, targetActor, payload);

            if (payload[2] == BrawlerOpcodes.WORD_DISAPPEAR_COMMAND)
                return ParseWordDisappear(sourceActor, targetActor, payload);

            if (payload[2] == BrawlerOpcodes.CARRIER_CLEAR_COMMAND_1 ||
                payload[2] == BrawlerOpcodes.CARRIER_CLEAR_COMMAND_2)
                return ParseCarrierClear(sourceActor, targetActor, payload);

            return null;
        }

        private BrawlerEvent ParseWordAssign(uint sourceActor, uint targetActor, uint[] payload)
        {
            if (payload.Length < 8)
                return null;

            if (payload[0] != BrawlerOpcodes.WORD_ASSIGN_MARKER0 ||
                payload[2] != BrawlerOpcodes.WORD_ASSIGN_COMMAND ||
                (payload[5] & 0xFFFFFF00) != BrawlerOpcodes.WORD_TABLE_PREFIX)
                return null;

            uint carrierId = payload[3];
            uint current = payload[6];
            uint next = payload[7];
            uint phaseParam = payload[4];
            uint tableTag = payload[5];
            if (carrierId == 0 || current > 0xFFFF || next > 0xFFFF)
                return null;

            CarrierWordState state;
            if (!_carriers.TryGetValue(carrierId, out state))
            {
                state = new CarrierWordState { CarrierId = carrierId };
                _carriers[carrierId] = state;
            }

            if (!state.HasWord || state.WordTableTag != tableTag ||
                state.CurrentWordIndex != current)
                state.AssignmentRevision = _sequence;

            state.CurrentWordIndex = current;
            state.NextWordIndex = next;
            state.PhaseParam = phaseParam;
            state.WordTableTag = tableTag;
            state.HasWord = true;
            state.LastAssignTime = DateTime.Now;
            state.AssignCount++;

            return new BrawlerEvent
            {
                EventType = BrawlerEventType.WordAssign,
                Opcode = BrawlerOpcodes.OP_EVENT_PACKET,
                SourceActor = sourceActor,
                TargetActor = targetActor,
                IsLocalPlayer = LocalPlayerId != 0 && sourceActor == LocalPlayerId,
                Timestamp = DateTime.Now,
                ContentId = payload[1],
                CarrierId = carrierId,
                PhaseParam = phaseParam,
                WordTableTag = tableTag,
                CurrentWordIndex = current,
                NextWordIndex = next,
                CurrentWordTableTag = tableTag,
                NextWordTableTag = tableTag,
                HasCarrierState = true,
                AssignmentRevision = state.AssignmentRevision
            };
        }

        private BrawlerEvent ParseWordDisappear(uint sourceActor, uint targetActor, uint[] payload)
        {
            // Match one local result snapshot, never the carrier's newer assignment
            // or another player's result. Duplicate disappearance packets cannot reuse it.
            BrawlerEvent effect = _lastLocalWordEffect;
            bool local = LocalPlayerId != 0 && sourceActor == LocalPlayerId;
            bool derived = local && effect != null && effect.SourceActor == sourceActor &&
                           (DateTime.Now - effect.Timestamp).TotalSeconds <= 2;
            if (local)
                _lastLocalWordEffect = null;

            return new BrawlerEvent
            {
                EventType = BrawlerEventType.WordDisappear,
                Opcode = BrawlerOpcodes.OP_EVENT_PACKET,
                SourceActor = sourceActor,
                TargetActor = targetActor,
                IsLocalPlayer = local,
                Timestamp = DateTime.Now,
                CarrierId = derived ? effect.CarrierId : 0,
                PhaseParam = derived ? effect.PhaseParam : 0,
                WordTableTag = derived ? effect.CurrentWordTableTag : 0,
                CurrentWordIndex = derived ? effect.CurrentWordIndex : 0,
                CurrentWordTableTag = derived ? effect.CurrentWordTableTag : 0,
                HasCarrierState = derived,
                AssignmentRevision = derived ? effect.AssignmentRevision : 0,
                CommandId = payload[2],
                CommandParam = payload.Length > 3 ? payload[3] : 0,
                DerivedCarrierId = derived
            };
        }

        private BrawlerEvent ParseCarrierClear(uint sourceActor, uint targetActor, uint[] payload)
        {
            uint carrierId = payload.Length > 3 ? payload[3] : 0;
            CarrierWordState state = null;
            if (carrierId != 0)
                _carriers.TryGetValue(carrierId, out state);

            if (state != null)
                state.HasWord = false;

            return new BrawlerEvent
            {
                EventType = BrawlerEventType.CarrierClear,
                Opcode = BrawlerOpcodes.OP_EVENT_PACKET,
                SourceActor = sourceActor,
                TargetActor = targetActor,
                IsLocalPlayer = LocalPlayerId != 0 && sourceActor == LocalPlayerId,
                Timestamp = DateTime.Now,
                CarrierId = carrierId,
                PhaseParam = state != null ? state.PhaseParam : 0,
                WordTableTag = state != null ? state.WordTableTag : 0,
                CurrentWordIndex = state != null ? state.CurrentWordIndex : 0,
                CurrentWordTableTag = state != null ? state.WordTableTag : 0,
                CommandId = payload[2],
                CommandParam = 0,
                HasCarrierState = state != null,
                AssignmentRevision = state != null ? state.AssignmentRevision : 0
            };
        }

        private BrawlerEvent ParseEffectResult(uint sourceActor, uint targetActor, uint[] payload)
        {
            if (payload.Length < 7)
                return null;

            uint action = payload[2];
            if (action != BrawlerOpcodes.ACTION_WORD_HIT &&
                action != BrawlerOpcodes.ACTION_WORD_KILL &&
                action != BrawlerOpcodes.ACTION_WORD_MISS_1 &&
                action != BrawlerOpcodes.ACTION_WORD_MISS_2 &&
                action != BrawlerOpcodes.ACTION_WORD_SELF_PUNISH)
                return null;

            uint carrierId = payload[0];
            if (carrierId == 0)
                return null;

            CarrierWordState state;
            bool hasState = _carriers.TryGetValue(carrierId, out state);
            uint wordIndex = hasState ? state.CurrentWordIndex : 0;
            uint wordTableTag = hasState ? state.WordTableTag : 0;

            // 当前词应当早已由 0x01E6/10000008 下发。这里保留 10 分钟有效期，
            // 避免金碟内复用 ActorID 时拿到过期的词。
            if (hasState && (DateTime.Now - state.LastAssignTime).TotalMinutes > 10)
            {
                hasState = false;
                wordIndex = 0;
                wordTableTag = 0;
            }

            uint combined = payload[6];
            var result = new BrawlerEvent
            {
                EventType = BrawlerEventType.Typed,
                Opcode = BrawlerOpcodes.OP_EFFECT_RESULT,
                SourceActor = sourceActor,
                TargetActor = targetActor,
                IsLocalPlayer = LocalPlayerId != 0 && sourceActor == LocalPlayerId,
                Timestamp = DateTime.Now,
                CarrierId = carrierId,
                PhaseParam = hasState ? state.PhaseParam : 0,
                WordTableTag = wordTableTag,
                CurrentWordIndex = wordIndex,
                NextWordIndex = hasState ? state.NextWordIndex : 0,
                CurrentWordTableTag = wordTableTag,
                NextWordTableTag = hasState ? state.WordTableTag : 0,
                ActionId = action,
                RelatedSequence = payload[3],
                WordHandle = combined >> 16,
                PlayerCounter = combined & 0xFFFF,
                HasCarrierState = hasState,
                AssignmentRevision = hasState ? state.AssignmentRevision : 0
            };

            if (result.IsLocalPlayer)
            {
                _lastLocalWordEffect = hasState && state.HasWord &&
                    action != BrawlerOpcodes.ACTION_WORD_SELF_PUNISH ? result : null;
                if (_lastLocalWordEffect != null)
                    state.HasWord = false;
            }

            return result;
        }

        private static uint[] ExtractBinaryPayload(byte[] message)
        {
            int count = (message.Length - BrawlerOpcodes.HEADER_SIZE) / 4;
            uint[] payload = new uint[count];
            for (int i = 0; i < count; i++)
                payload[i] = BitConverter.ToUInt32(message, BrawlerOpcodes.HEADER_SIZE + i * 4);
            return payload;
        }

        private static uint[] ExtractTextPayload(string[] parts, int startIndex, int endIndex)
        {
            List<uint> payload = new List<uint>();
            for (int i = startIndex; i < endIndex; i++)
            {
                string field = parts[i].Trim();
                uint value;
                if (TryParseHex(field, out value))
                    payload.Add(value);
            }
            return payload.ToArray();
        }

        public static string FormatBinaryAsHex(byte[] message)
        {
            if (message == null || message.Length == 0)
                return "(empty)";

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < message.Length; i += 4)
            {
                if (i > 0) sb.Append('|');
                if (i + 4 <= message.Length)
                    sb.Append(BitConverter.ToUInt32(message, i).ToString("X8"));
                else
                    for (int j = i; j < message.Length; j++)
                        sb.Append(message[j].ToString("X2"));
            }
            return sb.ToString();
        }

        public static bool TryParseHex(string text, out uint value)
        {
            value = 0;
            if (string.IsNullOrEmpty(text)) return false;

            text = text.Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                text = text.Substring(2);

            return uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }
    }
}
