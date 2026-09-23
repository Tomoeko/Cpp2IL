using UnityEngine;

namespace ComponentFixture
{
    public sealed class MarkerBehaviour : MonoBehaviour
    {
        public int Count;
        [SerializeField] private string caption;
        public DataAsset Configuration;
        public Payload Payload;
    }
}
