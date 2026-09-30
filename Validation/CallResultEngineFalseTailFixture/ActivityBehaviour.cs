using System.Runtime.CompilerServices;
using UnityEngine;

namespace CallResultEngineFalseTailFixture
{
    public sealed class ActivityBehaviour : MonoBehaviour
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void DisableSelf()
        {
            gameObject.SetActive(false);
        }
    }
}
