namespace SideEffectClassCctorFixture
{
    public static class InitializationWitness
    {
        public static int Events;
    }

    public static class StaticCells
    {
        public static int Marker;
        public static float Bias;

        static StaticCells()
        {
            InitializationWitness.Events++;
            Marker = 37;
            Bias = -0.0f;
        }
    }
}
