using UnityEngine;

namespace Vidow
{
    public sealed class VidowPreviewOnly : MonoBehaviour
    {
        private void Awake()
        {
            gameObject.SetActive(false);
        }
    }
}
