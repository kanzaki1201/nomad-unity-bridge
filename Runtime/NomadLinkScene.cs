using UnityEngine;

namespace Malloc.NomadLink
{
    [DisallowMultipleComponent]
    public sealed class NomadLinkScene : MonoBehaviour
    {
        [SerializeField] private string host = "127.0.0.1";
        [SerializeField] private int port = 48312;

        public string Host => host;
        public int Port => port;
    }
}
