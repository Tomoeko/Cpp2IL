using System.Runtime.CompilerServices;

namespace ReferenceArrayScalarResetFixture
{
    public class BooleanEntry
    {
        public bool Active;
        public int Neighbor;
    }

    public class SingleEntry
    {
        public float Value;
        public int Neighbor;
    }

    public class ResetHolder
    {
        public float Marker;
        public BooleanEntry[] Flags;
        public SingleEntry[] Values;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ResetActive()
        {
            Marker = 0.0f;
            for (var index = 0; index < Flags.Length; index++)
                Flags[index].Active = false;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ResetValues()
        {
            for (var index = 0; index < 7; index++)
                Values[index].Value = 0.0f;
        }
    }
}
