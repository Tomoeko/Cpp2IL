using System;
using System.Runtime.CompilerServices;

namespace NarrowComparisonFixture
{
    public sealed class ByteState
    {
        public bool Condition;
        public bool Observed;
        public byte ByteValue;
        public sbyte SignedValue;
        public ushort WordValue;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ObserveCondition()
        {
            if (Condition)
                Observed = true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool IsByteZero() { return ByteValue == 0; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool HasByteHighBit() { return ByteValue >= 128; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool IsSignedNegative() { return SignedValue < 0; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool IsWordZero() { return WordValue == 0; }
    }

    public static class MetadataCases
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static string ReadLiteral() { return "neutral metadata literal"; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static Type ReadTypeToken() { return typeof(ByteState); }
    }

    public static class InitializationObserver
    {
        public static int CompletedCount;
        public static int ThrowingCount;
    }

    public static class InitializedValue
    {
        public static readonly int Value;

        static InitializedValue()
        {
            InitializationObserver.CompletedCount++;
            Value = 17;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Read() { return Value; }
    }

    public static class ThrowingInitialization
    {
        static ThrowingInitialization()
        {
            InitializationObserver.ThrowingCount++;
            throw new InvalidOperationException("neutral initialization failure");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Touch() { return 23; }
    }

    public static class InitializationConsumers
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int ReadInitialized() { return InitializedValue.Read(); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int TouchThrowing() { return ThrowingInitialization.Touch(); }
    }
}
