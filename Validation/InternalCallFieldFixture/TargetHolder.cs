using System.Runtime.CompilerServices;
using UnityEngine;

namespace InternalCallFieldFixture
{
    public sealed class TargetHolder
    {
        public GameObject Target;
        public string Label;
        public int Neighbor;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public TargetHolder()
        {
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool ReadActive()
        {
            return Target.activeSelf;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Rename()
        {
            Target.name = Label;
        }
    }
}
