namespace ACTKeyboundBrawlerAuto
{
    public static class BrawlerOpcodes
    {
        public const int HEADER_SIZE = 32;
        public const int SOURCE_ACTOR_OFFSET = 4;
        public const int TARGET_ACTOR_OFFSET = 8;
        public const int OPCODE_OFFSET = 18;

        public const ushort OP_EFFECT_RESULT = 0x00BD;
        public const ushort OP_EVENT_PACKET = 0x01E6;
        public const ushort OP_ACTOR_CAST = 0x0301;

        public const uint WORD_ASSIGN_COMMAND = 0x10000008;
        public const uint WORD_DISAPPEAR_COMMAND = 0x1000000A;
        public const uint CARRIER_CLEAR_COMMAND_1 = 0x10000012;
        public const uint CARRIER_CLEAR_COMMAND_2 = 0x1000000B;
        public const uint WORD_ASSIGN_MARKER0 = 0x0000006D;
        // P[5] 是 KTGTypeWordProgram row id：004C4B4A/4B/4C/4D 分别对应不同词表/阶段。
        public const uint WORD_TABLE_PREFIX = 0x004C4B00;

        public const uint ACTION_WORD_HIT = 0x0000C16C;
        public const uint ACTION_WORD_KILL = 0x0000C16D;
        public const uint ACTION_WORD_MISS_1 = 0x0000C16E;
        public const uint ACTION_WORD_MISS_2 = 0x0000C16F;
        public const uint ACTION_WORD_SELF_PUNISH = 0x0000C170;

        // 0x0301 StartCast 中确认会带“禁止打字”图标的机制。
        public const uint CAST_CURSE_GAZE = 0x0000C179;
        public const uint CAST_MOVEMENT_JAMMER = 0x0000C17C;

        public const uint GOLD_SAUCER_ZONE = 0x54F;
        public const uint KEYBOUND_BRAWLER_ZONE = 0x550;
    }
}
