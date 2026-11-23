namespace PycRune.Model;

public static class OpCodes
{
    public const int CACHE = 0;
    public const int POP_TOP = 1;
    public const int PUSH_NULL = 2;
    public const int INTERPRETER_EXIT = 3;
    public const int END_FOR = 4;
    public const int END_SEND = 5;
    public const int NOP = 9;
    public const int UNARY_POSITIVE = 10;
    public const int UNARY_NEGATIVE = 11;
    public const int UNARY_NOT = 12;
    public const int UNARY_INVERT = 15;
    public const int BINARY_SUBSCR = 25;
    public const int BINARY_SLICE = 26;
    public const int STORE_SLICE = 27;
    public const int GET_LEN = 30;
    public const int MATCH_MAPPING = 31;
    public const int MATCH_SEQUENCE = 32;
    public const int MATCH_KEYS = 33;
    public const int PUSH_EXC_INFO = 35;
    public const int CHECK_EXC_MATCH = 36;
    public const int CHECK_EG_MATCH = 37;
    public const int WITH_EXCEPT_START = 49;
    public const int GET_AITER = 50;
    public const int GET_ANEXT = 51;
    public const int BEFORE_ASYNC_WITH = 52;
    public const int BEFORE_WITH = 53;
    public const int END_ASYNC_FOR = 54;
    public const int CLEANUP_THROW = 55;
    public const int STORE_SUBSCR = 60;
    public const int DELETE_SUBSCR = 61;
    public const int GET_ITER = 68;
    public const int GET_YIELD_FROM_ITER = 69;
    public const int LOAD_BUILD_CLASS = 71;
    public const int LOAD_ASSERTION_ERROR = 74;
    public const int RETURN_GENERATOR = 75;
    public const int RETURN_VALUE = 83;
    public const int SETUP_ANNOTATIONS = 85;


    public const int LEGACY_SETUP_FINALLY = 1000;
    public const int LEGACY_POP_BLOCK = 1001;
    public const int LEGACY_END_FINALLY = 1002;
    public const int LEGACY_JUMP_IF_NOT_EXC_MATCH = 1003;
    public const int LEGACY_DUP_TOP = 1004;
    public const int LEGACY_CALL_FUNCTION_KW = 1005;
    public const int LEGACY_BUILD_MAP_UNPACK = 1006;
    public const int LEGACY_SETUP_WITH = 1007;
    public const int LEGACY_YIELD_FROM = 1008;
    public const int SET_FUNCTION_ATTRIBUTE = 1009;
    public const int CONVERT_VALUE = 1010;
    public const int FORMAT_SIMPLE = 1011;
    public const int FORMAT_WITH_SPEC = 1012;
    public const int LOAD_SPECIAL = 1013;
    public const int NOT_TAKEN = 1014;
    public const int LEGACY_ROT_TWO = 1015;
    public const int LEGACY_ROT_THREE = 1016;
    public const int LEGACY_ROT_FOUR = 1017;
    public const int LEGACY_DUP_TOP_TWO = 1018;
    public const int LEGACY_LIST_TO_TUPLE = 1019;
    public const int LEGACY_BUILD_TUPLE_UNPACK = 1020;
    public const int LEGACY_BUILD_LIST_UNPACK = 1021;
    public const int LEGACY_BUILD_TUPLE_UNPACK_WITH_CALL = 1022;
    public const int LEGACY_CALL_FINALLY = 1023;
    public const int LEGACY_COPY_DICT_WITHOUT_KEYS = 1024;
    public const int LEGACY_ROT_N = 1025;
    public const int LEGACY_PREP_RERAISE_STAR = 1026;
    public const int LEGACY_ASYNC_GEN_WRAP = 1027;
    public const int LEGACY_BUILD_MAP_UNPACK_WITH_CALL = 1028;
    public const int LEGACY_BUILD_SET_UNPACK = 1029;
    public const int LOAD_LOCALS = 87;
    public const int POP_EXCEPT = 89;
    public const int STORE_NAME = 90;
    public const int DELETE_NAME = 91;
    public const int UNPACK_SEQUENCE = 92;
    public const int FOR_ITER = 93;
    public const int UNPACK_EX = 94;
    public const int STORE_ATTR = 95;
    public const int DELETE_ATTR = 96;
    public const int STORE_GLOBAL = 97;
    public const int DELETE_GLOBAL = 98;
    public const int SWAP = 99;
    public const int LOAD_CONST = 100;
    public const int LOAD_NAME = 101;
    public const int BUILD_TUPLE = 102;
    public const int BUILD_LIST = 103;
    public const int BUILD_SET = 104;
    public const int BUILD_MAP = 105;
    public const int LOAD_ATTR = 106;
    public const int COMPARE_OP = 107;
    public const int IMPORT_NAME = 108;
    public const int IMPORT_FROM = 109;
    public const int JUMP_FORWARD = 110;
    public const int POP_JUMP_IF_FALSE = 114;
    public const int POP_JUMP_IF_TRUE = 115;
    public const int LOAD_GLOBAL = 116;
    public const int IS_OP = 117;
    public const int CONTAINS_OP = 118;
    public const int RERAISE = 119;
    public const int COPY = 120;
    public const int RETURN_CONST = 121;
    public const int BINARY_OP = 122;
    public const int SEND = 123;
    public const int LOAD_FAST = 124;
    public const int STORE_FAST = 125;
    public const int DELETE_FAST = 126;
    public const int LOAD_FAST_CHECK = 127;
    public const int POP_JUMP_IF_NOT_NONE = 128;
    public const int POP_JUMP_IF_NONE = 129;
    public const int RAISE_VARARGS = 130;
    public const int GET_AWAITABLE = 131;
    public const int MAKE_FUNCTION = 132;
    public const int BUILD_SLICE = 133;
    public const int JUMP_BACKWARD_NO_INTERRUPT = 134;
    public const int MAKE_CELL = 135;
    public const int LOAD_CLOSURE = 136;
    public const int LOAD_DEREF = 137;
    public const int STORE_DEREF = 138;
    public const int DELETE_DEREF = 139;
    public const int JUMP_BACKWARD = 140;
    public const int LOAD_SUPER_ATTR = 141;
    public const int CALL_FUNCTION_EX = 142;
    public const int LOAD_FAST_AND_CLEAR = 143;
    public const int EXTENDED_ARG = 144;
    public const int LIST_APPEND = 145;
    public const int SET_ADD = 146;
    public const int MAP_ADD = 147;
    public const int COPY_FREE_VARS = 149;
    public const int YIELD_VALUE = 150;
    public const int RESUME = 151;
    public const int MATCH_CLASS = 152;
    public const int FORMAT_VALUE = 155;
    public const int BUILD_CONST_KEY_MAP = 156;
    public const int BUILD_STRING = 157;
    public const int LIST_EXTEND = 162;
    public const int SET_UPDATE = 163;
    public const int DICT_MERGE = 164;
    public const int DICT_UPDATE = 165;
    public const int CALL = 171;
    public const int KW_NAMES = 172;
    public const int CALL_INTRINSIC_1 = 173;
    public const int CALL_INTRINSIC_2 = 174;
    public const int LOAD_FROM_DICT_OR_GLOBALS = 175;
    public const int LOAD_FROM_DICT_OR_DEREF = 176;

    public static int GetCacheEntries(int opcode) => opcode switch
    {
        BINARY_SUBSCR => 1,
        STORE_SUBSCR => 1,
        UNPACK_SEQUENCE => 1,
        FOR_ITER => 1,
        STORE_ATTR => 4,
        LOAD_ATTR => 9,
        COMPARE_OP => 1,
        LOAD_GLOBAL => 4,
        BINARY_OP => 1,
        SEND => 1,
        LOAD_SUPER_ATTR => 1,
        CALL => 3,
        _ => 0
    };

    public static bool IsRelativeForwardJump(int opcode) =>
        opcode is JUMP_FORWARD or POP_JUMP_IF_FALSE or POP_JUMP_IF_TRUE or POP_JUMP_IF_NOT_NONE or POP_JUMP_IF_NONE;

    public static bool IsConditionalJump(int opcode) =>
        opcode is POP_JUMP_IF_FALSE or POP_JUMP_IF_TRUE or POP_JUMP_IF_NOT_NONE or POP_JUMP_IF_NONE;

    public static bool IsRelativeBackwardJump(int opcode) =>
        opcode is JUMP_BACKWARD or JUMP_BACKWARD_NO_INTERRUPT;

    public static bool IsJump(int opcode) =>
        IsRelativeForwardJump(opcode) || IsRelativeBackwardJump(opcode) || opcode == FOR_ITER;

    public static bool IsTerminator(int opcode) =>
        opcode is RETURN_VALUE or RETURN_CONST or RAISE_VARARGS or RERAISE or JUMP_FORWARD or JUMP_BACKWARD or JUMP_BACKWARD_NO_INTERRUPT;
}
