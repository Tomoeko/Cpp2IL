namespace VirtualStringCallFixture
{
    public static class InitializationWitness
    {
        public static int OuterRuns;
        public static int MiddleRuns;
        public static int NextOrder;
        public static int OuterOrder;
        public static int MiddleOrder;
    }

    public class OuterBase
    {
        public static int OuterTrigger;

        static OuterBase()
        {
            InitializationWitness.OuterRuns++;
            InitializationWitness.OuterOrder = ++InitializationWitness.NextOrder;
        }
    }

    public class ChainNode : OuterBase
    {
        public string Text;
        public int Marker;
    }

    public class MiddleNode : ChainNode
    {
        public static int MiddleTrigger;

        static MiddleNode()
        {
            InitializationWitness.MiddleRuns++;
            InitializationWitness.MiddleOrder = ++InitializationWitness.NextOrder;
        }
    }
}
