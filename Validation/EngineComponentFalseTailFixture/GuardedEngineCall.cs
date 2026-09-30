using System.Runtime.CompilerServices;
using UnityEngine;

namespace EngineComponentFalseTailFixture
{
    public static class GuardedEngineCall
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Disable(Component component)
        {
            component.gameObject.SetActive(false);
        }
    }
}
