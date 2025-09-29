namespace SkillEye
{
    using System;
    using System.Runtime.InteropServices;
    using GameOffsets.Natives;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct ActiveSkillPtrOnly
    {
        public IntPtr ActiveSkillPtr;
    }

    internal static class ActiveSkillDetailsLayout
    {
        public const int StatsPtr = 0xC8;

        public static IntPtr ReadStatsPtr(IntPtr activeSkillEntryPtr)
        {
            if (activeSkillEntryPtr == IntPtr.Zero) return IntPtr.Zero;
            var addr = new IntPtr(activeSkillEntryPtr.ToInt64() + StatsPtr);
            return ProcReader.Read<IntPtr>(addr);
        }
    }

    [StructLayout(LayoutKind.Explicit, Pack = 1)]
    internal struct SubStatsComponentOffsets
    {
        [FieldOffset(0xF8)] public StdVector Stats;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct StatPair
    {
        public int StatId;
        public int Value;
    }
}
