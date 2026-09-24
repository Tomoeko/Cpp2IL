using System.Runtime.CompilerServices;

namespace CallResultNullGuardFixture
{
    public sealed class ChainRoot
    {
        public ChainNode First;
        public ChainNode Second;
        public int Trace;
        public int GetterCalls;
        public byte Marker;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public ChainNode GetFirst()
        {
            return First;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int Execute()
        {
            Trace = unchecked(Trace + 1);
            ChainNode first = GetFirst();
            Trace = unchecked(Trace + 2);
            ChainNode second = first.GetNext();
            Trace = unchecked(Trace + 4);
            int value = second.Read();
            Trace = unchecked(Trace + 8);
            return value;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public ChainNode GetRepeated()
        {
            GetterCalls = unchecked(GetterCalls + 1);
            if (GetterCalls == 1)
                return First;
            return Second;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int ExecuteRepeated()
        {
            Trace = unchecked(Trace + 1);
            ChainNode first = GetRepeated();
            int firstValue = first.Read();
            Marker = 0x5a;
            Trace = unchecked(Trace + 2);
            ChainNode second = GetRepeated();
            Trace = unchecked(Trace + 4);
            int value = second.ReadWith(firstValue);
            Trace = unchecked(Trace + 8);
            return value;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int ExecuteInheritedRepeated()
        {
            Trace = unchecked(Trace + 1);
            ChainNode first = GetRepeated();
            first.Enabled = true;
            int firstValue = first.Value;
            Marker = 0x5a;
            Trace = unchecked(Trace + 2);
            ChainNode second = GetRepeated();
            Trace = unchecked(Trace + 4);
            int value = second.ReadWith(firstValue);
            Trace = unchecked(Trace + 8);
            return value;
        }
    }

    public class InheritedFlagBase
    {
        public bool Enabled { get; set; }
    }

    public sealed class ChainNode : InheritedFlagBase
    {
        public ChainNode Next;
        public int Value;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public ChainNode GetNext()
        {
            return Next;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int Read()
        {
            return Value;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int ReadWith(int other)
        {
            return unchecked(Value + other);
        }
    }
}
