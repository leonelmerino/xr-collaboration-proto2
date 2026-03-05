using Unity.Netcode;
using UnityEngine;

public class NetworkLauncher : MonoBehaviour
{
    // Start is called before the first frame update
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        if(Input.GetKeyDown(KeyCode.H))
            NetworkManager.Singleton.StartHost();      
        if(Input.GetKeyDown(KeyCode.C))
            NetworkManager.Singleton.StartClient();      
    }
}
