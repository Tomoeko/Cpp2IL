using System;

namespace AttributeTypeQualificationFixture
{
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class TypeMarkerAttribute : Attribute
    {
        public TypeMarkerAttribute(Type target) { }
    }

    [TypeMarker(typeof(string))]
    public static class Case { }
}
