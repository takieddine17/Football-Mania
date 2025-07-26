using UnityEngine;
using UnityEngine.UI;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using System.Collections.Generic;
using System.Linq;

public class ConnectionBarsManager : NetworkBehaviour
{
    public Image[] connectionBars;
    public TMPro.TMP_Text pingText;
    public Color goodConnectionColour = Color.green;
    public Color mediumConnectionColour = Color.yellow;
    public Color badConnectionColour = Color.red;
    public int maxPing = 300;
    public int minPing = 0;
    public float pingUpdateInterval = 1f;
    private const int PING_SAMPLES = 5;

    private Queue<int> pingSamples = new Queue<int>(PING_SAMPLES);

    private void Update()
    {
        int ping = -1;
        bool valid = false;
        if (HostGameManager.Instance != null && Unity.Netcode.NetworkManager.Singleton.IsConnectedClient)
        {
            ping = HostGameManager.Instance.ClientPing.Value;
            valid = true;
        }
        else if (MatchManager.Instance != null && Unity.Netcode.NetworkManager.Singleton.IsConnectedClient)
        {
            ping = MatchManager.Instance.ClientPing.Value;
            valid = true;
        }

        if (!valid)
        {
            SetErrorState();
            if (pingText != null)
                pingText.text = "---";
            return;
        }

        UpdateConnectionBars(ping);
        if (pingText != null)
        {
            pingText.text = $"{ping} ms";
        }
    }

    private void SetErrorState()
    {
        for (int i = 0; i < connectionBars.Length; i++)
        {
            if (connectionBars[i] != null)
            {
                connectionBars[i].enabled = true;
                connectionBars[i].color = Color.red;
            }
        }
    }

    private void UpdateConnectionBars(int ping)
    {
        int barsToShow = ping switch 
        {
            _ when ping <= maxPing/3 => 3,  
            _ when ping <= maxPing*2/3 => 2, 
            _ => 1                           
        };

        for (int i = 0; i < connectionBars.Length; i++)
        {
            if (connectionBars[i] != null)
            {
                connectionBars[i].enabled = i < barsToShow;
                connectionBars[i].color = i switch
                {
                    0 => goodConnectionColour,
                    1 => mediumConnectionColour,
                    _ => badConnectionColour
                };
            }
        }
    }
}