using UnityEngine;

namespace ComponentFixture
{
    public sealed class DataAsset : ScriptableObject
    {
        public int Value;
        [SerializeField] private string label;
    }
}
