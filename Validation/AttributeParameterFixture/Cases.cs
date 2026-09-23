using System;
using System.Runtime.InteropServices;

namespace AttributeParameterFixture
{
    public enum ByteMode : byte
    {
        Low = 0,
        High = 225
    }

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class ChoiceAttribute : Attribute
    {
        public ChoiceAttribute(object value) { }
        public ChoiceAttribute(string value) { }
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public sealed class PayloadAttribute : Attribute
    {
        public object Boxed;
        public Type Target;
        public int[] Numbers;
        public string Label;

        public PayloadAttribute(object value) { }
    }

    [AttributeUsage(AttributeTargets.Parameter | AttributeTargets.ReturnValue)]
    public sealed class ParameterMarkAttribute : Attribute
    {
        public ParameterMarkAttribute(int value) { }
    }

    [Choice((object)"fixture")]
    public static class BoxedStringCase { }

    [Choice("fixture")]
    public static class StringCase { }

    [Payload((object)ByteMode.High, Boxed = typeof(int), Target = typeof(ParameterCases), Numbers = new[] { -1, 0, 5 }, Label = "")]
    [Payload(typeof(int[]), Boxed = null, Target = null, Numbers = null, Label = null)]
    [Payload(new int[] { -7, 0, 9 })]
    [Payload(new Type[] { typeof(int), typeof(string[]) })]
    [Payload(new object[] { "fixture", (byte)7, ByteMode.High, typeof(string), null })]
    [Payload(null)]
    [Payload((int[])null)]
    [Payload(new int[0])]
    public static class PayloadCases { }

    public static class ParameterCases
    {
        private static int _storage;

        public static void RefAndOut(ref int value, out int previous)
        {
            previous = value;
            value = unchecked(value + 1);
        }

        public static int ReadIn(in int value) { return value; }
        public static ref int RefReturn() { return ref _storage; }
        public static ref readonly int ReadOnlyReturn() { return ref _storage; }
        public static decimal DecimalDefault(decimal value = 1.25m) { return value; }
        public static object OptionalObject([Optional] object value) { return value; }
        public static string OptionalString(string value = null) { return value; }
        public static ByteMode EnumDefault(ByteMode value = ByteMode.High) { return value; }
        public static int ParamsCount(params int[] values) { return values.Length; }

        public static void InOut([In, Out] ref int value) { value = unchecked(value + 3); }

        [return: ParameterMark(7)]
        public static int Marked([ParameterMark(-2)] int value = 3) { return value; }
    }
}
