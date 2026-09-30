using System.Runtime.CompilerServices;

namespace ScalarFloatSelectionFixture
{
    public sealed class ScalarSelections
    {
        public float Value32;
        public double Value64;
        public int Neighbor;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static float Minimum32(float left, float right) { return UnityEngine.Mathf.Min(left, right); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static float Maximum32(float left, float right) { return UnityEngine.Mathf.Max(left, right); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static double Minimum64(double left, double right) { return left < right ? left : right; }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static double Maximum64(double left, double right) { return left > right ? left : right; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public float ReadPositive32() { return UnityEngine.Mathf.Max(Value32, 0f); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public double ReadPositive64() { return Value64 > 0d ? Value64 : 0d; }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void StorePositive32(float value) { Value32 = UnityEngine.Mathf.Max(value, 0f); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void StorePositive64(double value) { Value64 = value > 0d ? value : 0d; }
    }
}
