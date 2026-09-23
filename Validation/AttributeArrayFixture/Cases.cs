using System;

namespace AttributeArrayFixture
{
    public enum ByteChoice : byte
    {
        None = 0,
        High = 210
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public sealed class ArrayPayloadAttribute : Attribute
    {
        public object Boxed { get; set; }
        public int[] Numbers { get; set; }
        public ByteChoice[] Modes { get; set; }
        public Type Target { get; set; }

        public ArrayPayloadAttribute(ByteChoice[] values) { }
        public ArrayPayloadAttribute(object value) { }
    }

    [ArrayPayload(new ByteChoice[] { ByteChoice.None, ByteChoice.High })]
    [ArrayPayload(new ByteChoice[0])]
    [ArrayPayload((ByteChoice[])null)]
    [ArrayPayload((object)new ByteChoice[] { ByteChoice.High })]
    [ArrayPayload("values", Boxed = new object[] { (byte)7, ByteChoice.High, typeof(string), null }, Numbers = new int[0], Modes = new ByteChoice[] { ByteChoice.High }, Target = typeof(string))]
    [ArrayPayload("nulls", Boxed = null, Numbers = null, Modes = null, Target = null)]
    [ArrayPayload("type", Boxed = typeof(int))]
    [ArrayPayload("integers", Boxed = new int[] { -1, 2 })]
    [ArrayPayload("enums", Boxed = new ByteChoice[] { ByteChoice.High })]
    public static class ArrayCases { }
}
