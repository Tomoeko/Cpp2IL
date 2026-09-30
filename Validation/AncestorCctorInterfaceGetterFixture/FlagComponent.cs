using System.Runtime.CompilerServices;
using UnityEngine;

namespace AncestorCctorInterfaceGetterFixture
{
    public interface IFlagReader
    {
        bool Read();
    }

    public class FlagComponent : MonoBehaviour, IFlagReader
    {
        public bool Flag;
        public int Marker;

        [MethodImpl(MethodImplOptions.NoInlining)]
        bool IFlagReader.Read()
        {
            return Flag;
        }
    }
}
