using System;
using System.Runtime.InteropServices;

namespace DeclarationFixture
{
    public enum SampleMode : short
    {
        Minimum = short.MinValue,
        Disabled = -1,
        Default = 0,
        Active = 7,
        Maximum = short.MaxValue
    }

    [AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
    public sealed class SampleAttribute : Attribute
    {
        public string Label { get; }
        public int Number { get; }
        public SampleMode Mode { get; set; }
        public Type Target { get; set; }

        public SampleAttribute(string label, int number)
        {
            Label = label;
            Number = number;
        }
    }

    [StructLayout(LayoutKind.Explicit, Pack = 4, Size = 16)]
    public struct Packet
    {
        [FieldOffset(0)] public int Tag;
        [FieldOffset(4)] public float Weight;
        [FieldOffset(8)] public long Stamp;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public struct Sequence
    {
        public byte Flag;
        public short Code;
        [MarshalAs(UnmanagedType.U1)] public bool Enabled;
    }

    public delegate TResult Transform<T, TResult>(ref T value, out int count);

    public interface ICounter
    {
        int Count { get; }
        int Increment(int amount = 1);
        event Action<int> Changed;
    }

    public abstract class BaseCounter
    {
        protected readonly int Initial;
        protected BaseCounter(int initial)
        {
            Initial = initial;
            Count = initial;
        }

        public virtual int Count { get; protected set; }
        public abstract int Increment(int amount = 1);
        protected internal virtual string Describe() { return "base"; }
    }

    [Sample("counter", 7, Mode = SampleMode.Active, Target = typeof(Packet))]
    [Sample("secondary", -3)]
    public sealed class Counter : BaseCounter, ICounter
    {
        public const int Limit = 31;
        private const string Marker = "fixture";
        public event Action<int> Changed;

        public Counter(int initial = 0) : base(initial) { }

        public override int Count { get; protected set; }

        public override int Increment(int amount = 1)
        {
            Count = unchecked(Count + amount);
            Changed?.Invoke(Count);
            return Count;
        }

        protected internal override string Describe() { return Marker; }
        public string Format(int value) { return value.ToString(); }
        public string Format(string value) { return value ?? Marker; }
        internal static bool IsZero(int value) { return value == 0; }

        public static bool Exchange(ref int value, out int previous, int replacement = -7)
        {
            previous = value;
            value = replacement;
            return previous != value;
        }
    }

    public sealed class Container<T> where T : class, IComparable<T>, new()
    {
        public T Item;

        public T Create() { return new T(); }

        public U Echo<U>(ref T value, out int count, U fallback = default(U)) where U : struct
        {
            Item = value;
            count = 1;
            return fallback;
        }

        public sealed class Pair<U> where U : struct
        {
            public T Outer;
            public U Inner;
        }
    }
}
