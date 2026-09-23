namespace FinalizerFixture
{
    public class Finalizable
    {
        public static int Released;

        ~Finalizable()
        {
            Released = unchecked(Released + 1);
        }
    }

    public class InheritedFinalizer : Finalizable
    {
    }

    public class DerivedFinalizer : Finalizable
    {
        ~DerivedFinalizer()
        {
            Released = unchecked(Released + 2);
        }
    }

    public class VirtualBase
    {
        public virtual int Value() { return 1; }
    }

    public class VirtualOverride : VirtualBase
    {
        public override int Value() { return 2; }
    }

    public class NewSlot : VirtualBase
    {
        public new virtual int Value() { return 3; }
    }

    public class FinalizeOverload
    {
        public int Finalize(int value) { return value; }
    }
}
