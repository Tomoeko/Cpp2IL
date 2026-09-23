using System;
using System.Reflection;

[assembly: AssemblyMetadata("ComponentFixture", "serialized-identity")]

namespace ComponentFixture
{
    [Serializable]
    public struct Payload
    {
        public int Value;
    }

    public static class OrdinaryHelper
    {
        public static int Identity(int value) { return value; }
    }
}
