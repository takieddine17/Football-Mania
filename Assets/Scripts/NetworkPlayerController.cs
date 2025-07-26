using Unity.Netcode;
using UnityEngine;
using TMPro;
using System.Collections;

public class NetworkPlayerController : NetworkBehaviour
{
    private NetworkVariable<int> selectedPlayerIndex = new NetworkVariable<int>(-1);
    private bool hasSentSelection = false;
    public SpriteRenderer playerSpriteRenderer;
    public TMP_Text playerNameText;

    private void Start()
    {
        if (IsClient)
        {
            StartCoroutine(WaitForConnectionAndSendSelection());
        }
    }

    private IEnumerator WaitForConnectionAndSendSelection()
    {
        // Wait for the network connection to be established
        while (!NetworkManager.Singleton.IsConnectedClient)
        {
            yield return new WaitForSeconds(0.1f);
        }

        // Wait a bit more to ensure everything is ready
        yield return new WaitForSeconds(0.5f);

        // Retry logic: try up to 5 times to send selection if not registered on server
        if (PlayerSelector.Instance != null && PlayerSelector.Instance.playerConfirmed && !hasSentSelection)
        {
            int localSelection = PlayerSelector.Instance.selectedPlayerIndex;
            for (int attempt = 1; attempt <= 5; attempt++)
            {
                QuerySelectionOnServerServerRpc(localSelection);
                // Wait 1s for server to process and respond
                yield return new WaitForSeconds(1.0f);
                // If the server has acknowledged, break
                if (hasSentSelection)
                {
                    break;
                }
            }
        }

        // Monitor connection state for reconnection
        while (NetworkManager.Singleton.IsConnectedClient)
        {
            // If we lose connection and get a new one, reset the selection flag
            if (!NetworkManager.Singleton.IsConnectedClient)
            {
                hasSentSelection = false;
                yield return new WaitUntil(() => NetworkManager.Singleton.IsConnectedClient);

                // Retry logic after reconnection
                if (PlayerSelector.Instance != null && PlayerSelector.Instance.playerConfirmed)
                {
                    int localSelection = PlayerSelector.Instance.selectedPlayerIndex;
                    for (int attempt = 1; attempt <= 5; attempt++)
                    {
                        QuerySelectionOnServerServerRpc(localSelection);
                        yield return new WaitForSeconds(1.0f);
                        
                        if (hasSentSelection)
                        {
                            break;
                        }
                    }
                }
            }

            yield return new WaitForSeconds(0.5f);
        }
    }

    [ServerRpc(RequireOwnership = false)]
    public void SubmitPlayerSelectionServerRpc(int playerIndex, ServerRpcParams rpcParams = default)
    {
        ulong senderClientId = rpcParams.Receive.SenderClientId;

        // Validate player index
        if (playerIndex < 0 || playerIndex >= PlayerSelector.Instance.playerNames.Length)
        {
            return;
        }

        // Update the network variable
        selectedPlayerIndex.Value = playerIndex;

        // Update the NetworkManager's tracking
        MyNetworkManager.Instance?.SetPlayerSelection(senderClientId, playerIndex);
    }

    [ServerRpc(RequireOwnership = false)]
    private void QuerySelectionOnServerServerRpc(int localSelection, ServerRpcParams rpcParams = default)
    {
        ulong senderClientId = rpcParams.Receive.SenderClientId;
        int serverSelection = -1;

        if (MyNetworkManager.Instance != null)
        {
            serverSelection = MyNetworkManager.Instance.GetPlayerSelection(senderClientId);
        }

        bool needsSend = (serverSelection != localSelection);
        ForceSelectionResendClientRpc(needsSend, localSelection);
    }

    [ClientRpc]
    private void ForceSelectionResendClientRpc(bool needsSend, int localSelection)
    {
        if (!IsOwner) return;
        if (needsSend && PlayerSelector.Instance != null && PlayerSelector.Instance.playerConfirmed && PlayerSelector.Instance.selectedPlayerIndex == localSelection)
        {
            SubmitPlayerSelectionServerRpc(localSelection, default);
            hasSentSelection = true;
        }
        else
        {
            hasSentSelection = true;
        }
    }

    [ServerRpc(RequireOwnership = false)]
    public void SendPlayerSelectionServerRpc(int playerIndex, ServerRpcParams rpcParams = default)
    {
        // Just forward to HostGameManager without spawning prefabs
        if (HostGameManager.Instance != null && !HostGameManager.Instance._isBeingDestroyed)
        {
            HostGameManager.Instance.OnPlayerSelectionUpdated(rpcParams.Receive.SenderClientId, playerIndex);
        }
    }

    public void OnPlayerSelectionChanged(int oldValue, int newValue)
    {
        if (IsClient && !IsServer)
        {
        }
    }

    private void ApplySelectedPlayer(int selectedIndex)
    {
        if (PlayerSelector.Instance == null)
        {
            return;
        }

        if (selectedIndex < 0 || selectedIndex >= PlayerSelector.Instance.playerSprites.Length)
        {
            return;
        }

        if (playerSpriteRenderer == null || playerNameText == null)
        {
            return;
        }

        playerSpriteRenderer.sprite = PlayerSelector.Instance.playerSprites[selectedIndex];
        playerNameText.text = PlayerSelector.Instance.playerNames[selectedIndex];
    }

    public void ConfirmPlayerSelection()
    {
        if (!IsOwner) 
        {
            return;
        }
        
        int selection = PlayerSelector.Instance.selectedPlayerIndex;
        if (HostGameManager.Instance != null && !HostGameManager.Instance._isBeingDestroyed)
        {
            HostGameManager.Instance.AddOrUpdatePlayerSelectionServerRpc(selection);
        }
    }

    public override void OnNetworkSpawn()
    {
        hasSentSelection = false; // Reset on new network spawn
        if (IsClient)
        {
            StartCoroutine(WaitForConnectionAndSendSelection());
            // Immediately query the server for the selection if already confirmed
            if (PlayerSelector.Instance != null && PlayerSelector.Instance.playerConfirmed)
            {
                int localSelection = PlayerSelector.Instance.selectedPlayerIndex;
                QuerySelectionOnServerServerRpc(localSelection);
            }
        }
    }
}