using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.Netcode;
using TMPro;
using UnityEngine.UI;
using System.Collections;
using System;
using System.Linq;
using System.Threading.Tasks;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Relay;
using System.Collections.Generic;
using UnityEngine.UI.Extensions;
using Unity.Collections;
using UnityEngine.Audio;

public class HostGameManager : NetworkBehaviour
{
    public bool _isBeingDestroyed = false; 
    public GameObject hostOnlyUIContainer;
    private bool isHost => IsServer;
    private bool isClientOnly => !IsServer && IsClient;
    public static HostGameManager Instance { get; private set; }
    public Button startGameButton;
    public Button backButton;
    public GameObject opponentLeftPopUp; 
    private MyNetworkManager _networkManager;
    public TMP_Text playerNameText;
    public Image playerImage;
    public TMP_Text opponentNameText;
    public Image opponentImage;
    private bool leftUISet = false;
    private bool rightUISet = false;
    public NetworkVariable<FixedString32Bytes> networkJoinCode = new NetworkVariable<FixedString32Bytes>(new FixedString32Bytes(""), NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    public GameObject loadingPopUp;
    public GameObject[] pingBars;
    public TMP_Text pingText;
    public TMP_Text CodeText;
    private string pendingJoinCode;
    private string joinCode;
    private ulong clientId;
    private bool isUpdatingUI = false;
    private bool opponentConnected = false;
    private NetworkVariable<int> localPlayerIndex = new NetworkVariable<int>(-1);
    private NetworkVariable<int> opponentPlayerIndex = new NetworkVariable<int>(-1);
    private NetworkVariable<int> opponentSelectionIndex = new NetworkVariable<int>(-1);
    private List<ulong> connectedClients = new List<ulong>();
    public AudioSource buttonAudioSource;
    public AudioSource transitionAudioSource;

    public NetworkVariable<int> ClientPing = new NetworkVariable<int>(999, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private Coroutine pingCoroutine;

    private void Awake()
    {
        transitionAudioSource.Play();
        leftUISet = false;
        rightUISet = false;
        SubscribeToEvents();

        if (Instance == null)
        {
            Instance = this;
        }
        else if (Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        // All: subscribe to join code changes for UI update
        networkJoinCode.OnValueChanged += (oldCode, newCode) =>
        {
            // Host: update UI from authoritative value
            if (IsHost)
            {
                SetJoinCode(newCode.ToString());
            }
            // Client: update UI directly from network variable
            else if (IsClient && !IsHost && CodeText != null)
            {
                CodeText.text = newCode.ToString();
            }
        };

        // Subscribe to NetworkVariable changes
        localPlayerIndex.OnValueChanged += (prev, current) => UpdatePlayerUI();
        opponentPlayerIndex.OnValueChanged += (prev, current) => OnOpponentPlayerIndexChanged(prev, current);
        opponentSelectionIndex.OnValueChanged += (prev, current) => {
            if (current >= 0 && PlayerSelector.Instance != null && 
                current < PlayerSelector.Instance.playerSprites.Length)
            {
                opponentImage.sprite = PlayerSelector.Instance.playerSprites[current];
                opponentNameText.text = PlayerSelector.Instance.playerNames[current];
            }
        };
        // Try to find the CodeText component if not set
        if (CodeText == null)
        {
            CodeText = GameObject.Find("JoinCodeText")?.GetComponent<TMP_Text>();
        }

        if (isHost)
        {
            // Host: enable host-only UI, allow interaction
            if (hostOnlyUIContainer != null)
            {
                hostOnlyUIContainer.SetActive(true);
            }
        }
        else if (isClientOnly)
        {
            // Client: disable host-only UI, block interaction
            if (hostOnlyUIContainer != null)
            {
                hostOnlyUIContainer.SetActive(false);
            }
            // Only back button is interactable for client
            if (startGameButton != null)
            {
                startGameButton.interactable = false;
            }
            if (backButton != null)
            {
                backButton.interactable = true;
            }
        }
    }

    // Call this after relay creation and after network is started (not in Awake)
    public void SetNetworkJoinCode(string code)
    {
        if (!string.IsNullOrEmpty(code))
        {
            networkJoinCode.Value = new FixedString32Bytes(code);
            SetJoinCode(code); // Update UI for host
        }
    }

    private void Start()
    {
        if (startGameButton != null)
        {
            startGameButton.onClick.AddListener(TryStartGame);
        }

        if (backButton != null)
        {
            backButton.onClick.AddListener(OnBackButtonClicked);
        }
        StartCoroutine(WaitForHostUIReady());
        // Ensure subscription to MyNetworkManager.Instance._playerSelections.OnListChanged
        if (MyNetworkManager.Instance._playerSelections != null)
        {
            MyNetworkManager.Instance._playerSelections.OnListChanged -= OnNetworkSelectionsChanged; // Prevent duplicates
            MyNetworkManager.Instance._playerSelections.OnListChanged += OnNetworkSelectionsChanged;
        }
        // Host: set UI from authoritative join code
        if (IsHost && !string.IsNullOrEmpty(networkJoinCode.Value.ToString()))
        {
            SetJoinCode(networkJoinCode.Value.ToString());
        }
        // Client: set UI from authoritative join code, never from PlayerPrefs or input
        if (IsClient && !IsHost && CodeText != null)
        {
            CodeText.text = networkJoinCode.Value.ToString();
        }
    }
    
    public int GetPlayerSelection(ulong clientId)
    {
        // Use the authoritative source from MyNetworkManager
        return MyNetworkManager.Instance != null ? MyNetworkManager.Instance.GetPlayerSelection(clientId) : -1;
    }

    private void OnRelayCreatedHandler(string joinCode)
    {
        if (_isBeingDestroyed || this == null)
        {
            return; // Guard against destroyed object
        }

        SetJoinCode(joinCode);
        UpdateButtonStates();
    }

    public ulong GetOpponentClientId(ulong localClientId)
    {
        var clients = NetworkManager.Singleton.ConnectedClientsIds;
        if (clients.Count == 2)
        {
            foreach (var client in clients)
            {
                if (client != localClientId)
                    return client;
            }
        }
        return 0;
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        if (IsServer && MyNetworkManager.Instance != null && MyNetworkManager.Instance._playerSelections != null)
        {
            // Blank right-side UI (client) as well
            if (opponentImage != null) opponentImage.sprite = null;
            if (opponentNameText != null) opponentNameText.text = "";
        }

        // --- UI Role Logic ---
        if (IsServer || isHost)
        {
            if (hostOnlyUIContainer != null)
            {
                hostOnlyUIContainer.SetActive(true);
            }
        }
        else if (IsClient || isClientOnly)
        {
            if (hostOnlyUIContainer != null)
            {
                hostOnlyUIContainer.SetActive(false);
            }
            // Only back button is interactable for client
            if (startGameButton != null)
            {
                startGameButton.interactable = false;
            }
            
            if (backButton != null)
            {
                backButton.interactable = true;
            }
        }

        // Network Variable/List Subscriptions

        if (MyNetworkManager.Instance != null)
        {
            if (MyNetworkManager.Instance._playerSelections == null)
            {
                MyNetworkManager.Instance._playerSelections = new NetworkList<MyNetworkManager.PlayerSelection>();
            }

            MyNetworkManager.Instance._playerSelections.OnListChanged -= OnNetworkSelectionsChanged;
            MyNetworkManager.Instance._playerSelections.OnListChanged += OnNetworkSelectionsChanged;
        }

        opponentPlayerIndex.OnValueChanged += (prev, current) => OnOpponentPlayerIndexChanged(prev, current);
        localPlayerIndex.OnValueChanged += (prev, current) => UpdatePlayerUI();

        opponentSelectionIndex.OnValueChanged += (prev, current) => 
        {
            if (current >= 0 && PlayerSelector.Instance != null && current < PlayerSelector.Instance.playerSprites.Length)
            {
                opponentImage.sprite = PlayerSelector.Instance.playerSprites[current];
                opponentNameText.text = PlayerSelector.Instance.playerNames[current];
            }
        };

        if (IsServer)
        {
            NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
            if (pingCoroutine == null)
            {
                pingCoroutine = StartCoroutine(UpdateClientPingCoroutine());
            }
        }

        if (IsHost)
        {
            int hostSelection = PlayerSelector.Instance.selectedPlayerIndex;
            AddOrUpdatePlayerSelectionServerRpc(hostSelection);
            UpdateHostPlayerUIWithOwnSelection();
        }
        UpdatePlayerUI();
    }

    private System.Collections.IEnumerator WaitForHostUIReady()
    {
        int frame = 0;
        while ((playerNameText == null || playerImage == null) || PlayerSelector.Instance == null)
        {
            frame++;
            if (playerNameText == null || playerImage == null)
            {
                FindUIElements();
            }
            yield return null;
        }

        UpdateHostPlayerUIWithOwnSelection();
        while (MyNetworkManager.Instance == null || !NetworkManager.Singleton.IsServer)
        {
            yield return null;
        }

        // Add host's selection to network list (if not already present)
        if (PlayerSelector.Instance != null && PlayerSelector.Instance.selectedPlayerIndex >= 0)
        {
            MyNetworkManager.Instance.SetPlayerSelection(NetworkManager.Singleton.LocalClientId, PlayerSelector.Instance.selectedPlayerIndex);
        }
    }

    public void UpdatePlayerUI()
    {
        if (!IsSpawned || MyNetworkManager.Instance._playerSelections == null)
        {
            return;
        }

        if (MyNetworkManager.Instance != null && MyNetworkManager.Instance._playerSelections != null)
        {
            string selections = "";

            foreach (var sel in MyNetworkManager.Instance._playerSelections)
            {
                selections += $"[clientId={sel.clientId}, index={sel.playerIndex}], ";
            }
        }

        if (MyNetworkManager.Instance != null && MyNetworkManager.Instance._playerSelections != null)
        {
            string selections = "";

            foreach (var sel in MyNetworkManager.Instance._playerSelections)
            {
                selections += $"[clientId={sel.clientId}, index={sel.playerIndex}], ";
            }
        }

        int selectedIndex = -1;

        if (MyNetworkManager.Instance != null && MyNetworkManager.Instance._playerSelections != null)
        {
            selectedIndex = MyNetworkManager.Instance.GetPlayerSelection(NetworkManager.Singleton.LocalClientId);
        }

        if (playerNameText == null || playerImage == null)
        {
            StartCoroutine(RetryUpdateUI());
            return;
        }

        if (MyNetworkManager.Instance._playerSelections.Count > (int)NetworkManager.Singleton.LocalClientId)
        {
            selectedIndex = MyNetworkManager.Instance._playerSelections[(int)NetworkManager.Singleton.LocalClientId].playerIndex;
        }

        if (selectedIndex >= 0 && selectedIndex < PlayerSelector.Instance.playerSprites.Length)
        {
            playerImage.sprite = PlayerSelector.Instance.playerSprites[selectedIndex];
            playerNameText.text = PlayerSelector.Instance.playerNames[selectedIndex];
        }
        else
        {
            playerImage.sprite = null;
            playerNameText.text = "";
        }
    }

    public void UpdateButtonStates()
    {
        bool isServer = NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;
        bool hasClient = NetworkManager.Singleton != null && NetworkManager.Singleton.ConnectedClients.Count > 1;
        bool hasRelayCode = !string.IsNullOrEmpty(MyNetworkManager.Instance?.GetCurrentRelayCode());

        if (startGameButton != null)
        {
            startGameButton.interactable = isServer && hasClient;
        }

        if (CodeText != null && !string.IsNullOrEmpty(joinCode))
        {
            CodeText.text = joinCode;
        }
    }

    private void HandleClientConnected(ulong clientId)
    {   
        // For new clients, resend all existing selections
        for (int i = 0; i < MyNetworkManager.Instance._playerSelections.Count; i++)
        {
            if (MyNetworkManager.Instance._playerSelections[i].playerIndex >= 0)
            {
                ClientRpcParams target = new ClientRpcParams
                {
                    Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } }
                };
                UpdateSelectionClientRpc((ulong)i, MyNetworkManager.Instance._playerSelections[i].playerIndex, target);
            }
        }
        
        // Update button states when new client connects
        UpdateButtonStates();
    }

    private void HandleClientDisconnected(ulong clientId)
    {
        UpdateButtonStates();
        UpdatePlayerUI();
    }

    private void HandleNewRelayCode(string code)
    {
        SetJoinCode(code);
        UpdateButtonStates();
    }

    private async Task<bool> InitialiseUnityServices()
    {
        try
        {
            if (UnityServices.State != ServicesInitializationState.Initialized)
            {
                await UnityServices.InitializeAsync();
            }

            // Only sign in if not already signed in
            if (!AuthenticationService.Instance.IsSignedIn)
            {
                await AuthenticationService.Instance.SignInAnonymouslyAsync();
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private IEnumerator UpdateClientPingCoroutine()
    {
        while (true)
        {
            if (NetworkManager.Singleton.ConnectedClients.Count > 1)
            {
                foreach (var client in NetworkManager.Singleton.ConnectedClients)
                {
                    if (client.Key != NetworkManager.Singleton.LocalClientId) // Not the host
                    {
                        var transport = NetworkManager.Singleton.NetworkConfig.NetworkTransport as Unity.Netcode.Transports.UTP.UnityTransport;
                        if (transport != null)
                        {
                            int ping = (int)transport.GetCurrentRtt(client.Key);
                            ClientPing.Value = ping;
                        }
                    }
                }
            }
            yield return new WaitForSeconds(1f);
        }
    }

    public async Task InitialiseJoinCode()
    {
        await WaitForRelayReady();

        joinCode = MyNetworkManager.Instance.GetCurrentRelayCode();
        SetJoinCode(joinCode);
        StartCoroutine(KeepAliveRelay());
    }

    private async Task WaitForRelayReady()
    {
        float timeout = 10f;
        float elapsed = 0f;

        while (!MyNetworkManager.Instance.IsRelayReady() && elapsed < timeout)
        {
            await Task.Delay(100);
            elapsed += 0.1f;
        }
    }

    private IEnumerator KeepAliveRelay()
    {
        while (true)
        {
            yield return new WaitForSeconds(30);
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
            {
                NetworkManager.Singleton.SceneManager.SetClientSynchronizationMode(LoadSceneMode.Single);
            }
        }
    }

    private IEnumerator HandleNewClient(ulong clientId)
    {
        yield return new WaitUntil(() => NetworkManager.Singleton.ConnectedClients.ContainsKey(clientId) && opponentImage != null);
        
        int opponentIndex = MyNetworkManager.Instance.GetPlayerSelection(clientId);
        OnOpponentSelectionChanged(opponentIndex);
        UpdateButtonStates();
    }

    private IEnumerator WaitAndAddHostSelection(int hostIndex)
    {
        while (!IsSpawned || MyNetworkManager.Instance._playerSelections == null)
        {
            yield return null;
        }

        float timeout = 5f;
        float elapsed = 0f;

        while ((MyNetworkManager.Instance == null || MyNetworkManager.Instance._playerSelections == null) && elapsed < timeout)
        {
            yield return null;
            elapsed += Time.deltaTime;
        }
        if (MyNetworkManager.Instance != null && MyNetworkManager.Instance._playerSelections != null && IsServer)
        {
            MyNetworkManager.Instance.SetPlayerSelection(NetworkManager.Singleton.LocalClientId, hostIndex);
            string selections = "";

            foreach (var sel in MyNetworkManager.Instance._playerSelections)
            {
                selections += $"[clientId={sel.clientId}, index={sel.playerIndex}], ";
            }
        }
    }

    private void CheckIfGameCanStart()
    {
        if (startGameButton == null) return;

        if (NetworkManager.Singleton.IsServer)
        {
            bool canStart = NetworkManager.Singleton.ConnectedClients.Count >= 2;
            startGameButton.interactable = canStart;
        }

        else
        {
            startGameButton.interactable = false;
        }
    }

    private void TryStartGame()
    {
        buttonAudioSource.Play();

        if (!NetworkManager.Singleton.IsServer)
        {
            if (loadingPopUp != null) 
            {
                loadingPopUp.SetActive(false);
            }

            return;
        }
        if (NetworkManager.Singleton.SceneManager == null)
        {
            if (loadingPopUp != null) 
            {
                loadingPopUp.SetActive(false);
            }

            return;
        }

        if (loadingPopUp != null) 
        {
            loadingPopUp.SetActive(true);
        }

        if (!SceneExistsInBuild("Match"))
        {
            loadingPopUp.SetActive(false);
            return;
        }

        NetworkManager.Singleton.SceneManager.LoadScene("Match", LoadSceneMode.Single);
    }

    private bool SceneExistsInBuild(string sceneName)
    {
        for (int i = 0; i < SceneManager.sceneCountInBuildSettings; i++)
        {
            var scenePath = SceneUtility.GetScenePathByBuildIndex(i);
            if (System.IO.Path.GetFileNameWithoutExtension(scenePath) == sceneName)
                return true;
        }
        return false;
    }

    private void OnGameSceneLoaded(ulong clientId, string sceneName, LoadSceneMode loadMode)
    {
        if (clientId == NetworkManager.Singleton.LocalClientId)
        {
            NetworkManager.Singleton.SceneManager.OnLoadComplete -= OnGameSceneLoaded;
            
            if (loadingPopUp != null)
            {
                loadingPopUp.SetActive(false);
            }
        }
    }

    private void UpdateUI()
    {
        if (isUpdatingUI) return;
        isUpdatingUI = true;

        try
        {
            if (PlayerSelector.Instance != null)
            {
                int selectedIndex = PlayerSelector.Instance.selectedPlayerIndex;
                UpdatePlayerUI();
            }
            else
            {
                // Try to find PlayerSelector in the scene
                PlayerSelector foundSelector = FindObjectOfType<PlayerSelector>();
                if (foundSelector != null)
                {
                    int selectedIndex = foundSelector.selectedPlayerIndex;
                    UpdatePlayerUI();
                }
            }
            
            // Ensure join code is displayed
            if (CodeText != null && string.IsNullOrEmpty(CodeText.text))
            {
                string relayCode = MyNetworkManager.Instance?.GetCurrentRelayCode();
                if (!string.IsNullOrEmpty(relayCode))
                {
                    SetJoinCode(relayCode);
                }
                else
                {
                    relayCode = PlayerPrefs.GetString("LastRelayCode", "");
                    if (!string.IsNullOrEmpty(relayCode))
                    {
                        SetJoinCode(relayCode);
                    }
                }
            }
            
            // Update button states
            UpdateButtonStates();
        }
        catch (Exception e)
        {
            Debug.LogError($"UI update failed: {e.Message}");
        }
        finally
        {
            isUpdatingUI = false;
        }
    }

    private IEnumerator RetryUpdateUI()
    {
        UpdatePlayerUI();
        yield break;
    }

    public void SetJoinCode(string code)
    {
        joinCode = code;

        // Only update the join code UI for the host
        if (IsHost && CodeText != null)
        {
            CodeText.text = code;
        }

        else if (IsHost && CodeText == null)
        {
            pendingJoinCode = code;
            if (!_isBeingDestroyed && this != null)
            {
                StartCoroutine(RetryJoinCodeDisplay());
            }
        }

        if (IsHost) 
        {
            UpdateButtonStates();
        }
    }

    private IEnumerator RetryJoinCodeDisplay()
    {
        if (_isBeingDestroyed || this == null)
        {
            yield break;
        }

        float timeout = 10f;
        float elapsed = 0f;
        float checkInterval = 0.5f;

        while (CodeText == null && elapsed < timeout)
        {
            if (_isBeingDestroyed || this == null)
            {
                yield break;
            }
            CodeText = GameObject.Find("JoinCodeText")?.GetComponent<TMP_Text>();
            
            if (CodeText != null)
            {
                break;
            }

            elapsed += checkInterval;
            yield return new WaitForSeconds(checkInterval);
        }

        if (_isBeingDestroyed || this == null)
        {
            yield break;
        }
        if (CodeText != null && !string.IsNullOrEmpty(pendingJoinCode))
        {
            CodeText.text = pendingJoinCode;
            pendingJoinCode = null;
        }
    }

    private void HandlePendingJoinCode()
    {
        if (!string.IsNullOrEmpty(pendingJoinCode) && CodeText != null)
        {
            CodeText.text = pendingJoinCode;
            pendingJoinCode = null;
        }
    }

    public void OnOpponentConnected()
    {
        if (!NetworkManager.Singleton.IsServer) return;
        
        UnityMainThreadDispatcher.Instance().Enqueue(() => {
            // Update UI elements
            opponentConnected = true;
            UpdatePlayerUI();
            
            // Force UI refresh
            if (opponentImage != null && opponentImage.transform.parent is RectTransform rt)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(rt);
            }
        });
    }

    public void OnOpponentSelectionChanged(int opponentIndex)
    {
        if (!IsServer) 
        {
            return;
        }
        
        UnityMainThreadDispatcher.Instance().Enqueue(() => 
        {
            // Update UI with latest selection
            UpdateOpponentUI();
            
            // Force canvas rebuild if needed
            if (opponentImage != null && opponentImage.transform.parent is RectTransform rt)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(rt);
            }
        });
    }

    public void OnPlayerSelectionUpdated(ulong clientId, int playerIndex)
    {
        bool found = false;
        for (int i = 0; i < MyNetworkManager.Instance._playerSelections.Count; i++)
        {
            if (MyNetworkManager.Instance._playerSelections[i].clientId == clientId)
            {
                MyNetworkManager.Instance._playerSelections[i] = new MyNetworkManager.PlayerSelection
                {
                    clientId = clientId,
                    playerIndex = playerIndex
                };
                found = true;
                break;
            }
        }
        if (!found)
        {
            MyNetworkManager.Instance._playerSelections.Add(new MyNetworkManager.PlayerSelection
            {
                clientId = clientId,
                playerIndex = playerIndex
            });
        }
        UpdateAllPlayerUIs();
    }

    [ClientRpc]
    private void UpdateSelectionClientRpc(ulong clientId, int selectionIndex, ClientRpcParams rpcParams = default)
    {
        UpdateAllPlayerUIs();
    }

    private void OnOpponentPlayerIndexChanged(int prev, int current)
    {
        if (!IsSpawned || MyNetworkManager.Instance._playerSelections == null)
        {
            return;
        }
        
        if (current >= 0 && PlayerSelector.Instance != null && current < PlayerSelector.Instance.playerSprites.Length)
        {
            UnityMainThreadDispatcher.Instance().Enqueue(() => 
            {
                opponentImage.sprite = PlayerSelector.Instance.playerSprites[current];
                opponentNameText.text = PlayerSelector.Instance.playerNames[current];
                
                if (opponentImage != null && opponentImage.transform.parent is RectTransform rt)
                {
                    LayoutRebuilder.ForceRebuildLayoutImmediate(rt);
                }
            });
        }
    }

    private void UpdateOpponentUI()
    {
        foreach (var client in NetworkManager.Singleton.ConnectedClients)
        {
            if (client.Key != NetworkManager.Singleton.LocalClientId)
            {
                int selection = GetPlayerIndex(client.Key);
                if (selection >= 0 && PlayerSelector.Instance != null)
                {
                    opponentImage.sprite = PlayerSelector.Instance.playerSprites[selection];
                    opponentNameText.text = PlayerSelector.Instance.playerNames[selection];
                    return;
                }
            }
        }
        
        opponentImage.sprite = null;
        opponentNameText.text = "";
    }

    private void OnNetworkSelectionsChanged(NetworkListEvent<MyNetworkManager.PlayerSelection> change)
    {
        if (!IsSpawned || MyNetworkManager.Instance._playerSelections == null)
            return;

        string selections = "";
        foreach (var sel in MyNetworkManager.Instance._playerSelections)
        {
            selections += $"[clientId={sel.clientId}, index={sel.playerIndex}], ";
        }
        TryUpdatePlayerOpponentUIWithRetry(3, 0.5f);
    }

    private void TryUpdatePlayerOpponentUIWithRetry(int maxTries, float delaySeconds)
    {
        StartCoroutine(RetryPlayerOpponentUICoroutine(maxTries, delaySeconds));
    }

    private IEnumerator RetryPlayerOpponentUICoroutine(int maxTries, float delaySeconds)
    {
        int tries = 0;
        bool updated = false;
        while (tries < maxTries && !updated)
        {
            tries++;
            updated = UpdatePlayerOpponentUI();
            if (!updated)
            {
                yield return new WaitForSeconds(delaySeconds);
            }
        }
    }

    private bool UpdatePlayerOpponentUI()
    {
        if (MyNetworkManager.Instance == null || MyNetworkManager.Instance._playerSelections == null || PlayerSelector.Instance == null)
        {
            return false;
        }

        int hostIndex = -1;
        int clientIndex = -1;
        ulong hostId = NetworkManager.ServerClientId;
        ulong clientId = 0;
        // Find the clientId (the one that's not host)
        foreach (var sel in MyNetworkManager.Instance._playerSelections)
        {
            if (sel.clientId != hostId)
            {
                clientId = sel.clientId;
                break;
            }
        }
        // Now assign indices
        foreach (var sel in MyNetworkManager.Instance._playerSelections)
        {
            if (sel.clientId == hostId)
            {
                hostIndex = sel.playerIndex;
            }

            else if (sel.clientId == clientId)
            {
                clientIndex = sel.playerIndex;
            }
        }

        bool valid = true;

        if (playerImage != null && playerNameText != null && hostIndex >= 0 && hostIndex < PlayerSelector.Instance.playerSprites.Length)
        {
            playerImage.sprite = PlayerSelector.Instance.playerSprites[hostIndex];
            playerNameText.text = PlayerSelector.Instance.playerNames[hostIndex];
        }

        else
        {
            if (playerImage != null) 
            {
                playerImage.sprite = null;
            }
            
            if (playerNameText != null) 
            {
                playerNameText.text = "";
            }
            
            valid = false;
        }

        if (opponentImage != null && opponentNameText != null && clientIndex >= 0 && clientIndex < PlayerSelector.Instance.playerSprites.Length)
        {
            opponentImage.sprite = PlayerSelector.Instance.playerSprites[clientIndex];
            opponentNameText.text = PlayerSelector.Instance.playerNames[clientIndex];
        }
        else
        {
            if (opponentImage != null) 
            {
                opponentImage.sprite = null;
            }
            
            if (opponentNameText != null) 
            {
                opponentNameText.text = "";
            }
            
            valid = false;
        }

        return valid;
    }

    private void OnClientConnected(ulong clientId)
    {
        if (!IsSpawned || MyNetworkManager.Instance._playerSelections == null)
        {
            return;
        }
        // Host should always re-set its own selection to ensure it is present
        if (IsHost)
        {
            int hostSelection = PlayerSelector.Instance.selectedPlayerIndex;
            AddOrUpdatePlayerSelectionServerRpc(hostSelection);
        }

        UpdateAllPlayerUIs();

        if (MyNetworkManager.Instance != null && MyNetworkManager.Instance._playerSelections != null)
        {
            string selections = "";
            foreach (var sel in MyNetworkManager.Instance._playerSelections)
            {
                selections += $"[clientId={sel.clientId}, index={sel.playerIndex}], ";
            }
        }

        if (IsServer && clientId != NetworkManager.Singleton.LocalClientId)
        {
            MyNetworkManager.Instance._playerSelections.OnListChanged += OnNetworkSelectionsChanged;
        }
    }

    // Ensures the host's player selection is shown on the left immediately upon loading
    private void UpdateHostPlayerUIWithOwnSelection()
    {
        if (playerImage == null || playerNameText == null || PlayerSelector.Instance == null)
        {
            return;
        }

        int selectedIndex = PlayerSelector.Instance.selectedPlayerIndex;

        if (selectedIndex >= 0 && selectedIndex < PlayerSelector.Instance.playerSprites.Length)
        {
            playerImage.sprite = PlayerSelector.Instance.playerSprites[selectedIndex];
            playerNameText.text = PlayerSelector.Instance.playerNames[selectedIndex];
        }
        else
        {
            playerImage.sprite = null;
            playerNameText.text = "";
        }
    }

    private void FindUIElements()
    {
        Canvas canvas = FindObjectOfType<Canvas>();
        if (canvas != null)
        {
            // Find player UI elements (host's UI)
            Transform playerPanel = canvas.transform.Find("PlayerPanel");
            if (playerPanel != null)
            {
                playerNameText = playerPanel.Find("PlayerName")?.GetComponent<TMP_Text>();
                playerImage = playerPanel.Find("PlayerImage")?.GetComponent<Image>();
            }
            
            // Find opponent UI elements
            Transform opponentPanel = canvas.transform.Find("OpponentPanel");
            if (opponentPanel != null)
            {
                opponentNameText = opponentPanel.Find("OpponentName")?.GetComponent<TMP_Text>();
                opponentImage = opponentPanel.Find("OpponentImage")?.GetComponent<Image>();
            }
        }
        
        // If we couldn't find the panels in the Canvas, try finding them by name
        if (playerNameText == null || playerImage == null || opponentNameText == null || opponentImage == null)
        {
            GameObject playerPanel = GameObject.Find("PlayerPanel");
            if (playerPanel != null)
            {
                playerNameText = playerPanel.transform.Find("PlayerName")?.GetComponent<TMP_Text>();
                playerImage = playerPanel.transform.Find("PlayerImage")?.GetComponent<Image>();
            }
            
            GameObject opponentPanel = GameObject.Find("OpponentPanel");
            if (opponentPanel != null)
            {
                opponentNameText = opponentPanel.transform.Find("OpponentName")?.GetComponent<TMP_Text>();
                opponentImage = opponentPanel.transform.Find("OpponentImage")?.GetComponent<Image>();
            }
        }
        
        // If we still couldn't find the panels, try finding them by tag
        if (playerNameText == null || playerImage == null || opponentNameText == null || opponentImage == null)
        {
            GameObject[] panels = GameObject.FindGameObjectsWithTag("PlayerPanel");
            foreach (GameObject panel in panels)
            {
                if (panel.name.Contains("PlayerPanel"))
                {
                    playerNameText = panel.transform.Find("PlayerName")?.GetComponent<TMP_Text>();
                    playerImage = panel.transform.Find("PlayerImage")?.GetComponent<Image>();
                }
                else if (panel.name.Contains("OpponentPanel"))
                {
                    opponentNameText = panel.transform.Find("OpponentName")?.GetComponent<TMP_Text>();
                    opponentImage = panel.transform.Find("OpponentImage")?.GetComponent<Image>();
                }
            }
        }
        
        if (playerNameText == null || playerImage == null || opponentNameText == null || opponentImage == null)
        {
            StartCoroutine(RetryFindUIElements());
        }
        else if (IsHost)
        {
            UpdateHostPlayerUIWithOwnSelection();
        }
    }

    private IEnumerator RetryFindUIElements()
    {
        yield return new WaitForSeconds(0.5f);
        FindUIElements();
    }

    public int GetPlayerIndex(ulong clientId)
    {
        return MyNetworkManager.Instance != null ? MyNetworkManager.Instance.GetPlayerSelection(clientId) : -1;
    }

    private void SubscribeToEvents()
    {
        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnect;
            NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
        }

        if (MyNetworkManager.Instance != null)
        {
            MyNetworkManager.Instance.OnRelayCodeGenerated += HandleNewRelayCode;
        }
    }

    private void UnsubscribeFromEvents()
    {
        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnect;
            NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnected;
        }

        if (MyNetworkManager.Instance != null)
        {
            MyNetworkManager.Instance.OnRelayCodeGenerated -= HandleNewRelayCode;
        }
    }

    private void OnClientDisconnect(ulong clientId)
    {
        if (!IsServer)
        {
            return;
        }

        if (MyNetworkManager.Instance != null && MyNetworkManager.Instance._playerSelections != null)
        {
            var list = MyNetworkManager.Instance._playerSelections;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].clientId == clientId)
                {
                    list.RemoveAt(i);
                    break;
                }
            }
        } 

        opponentNameText.text = null;
        opponentImage.sprite = null;

        if (startGameButton != null)
        {
            startGameButton.interactable = false;
        }
    }

    [ServerRpc(RequireOwnership = false)]
    public void AddOrUpdatePlayerSelectionServerRpc(int playerIndex, ServerRpcParams rpcParams = default)
    {
        ulong senderClientId = rpcParams.Receive.SenderClientId;

        if (senderClientId == NetworkManager.ServerClientId && !IsHost)
        {
            return;
        }

        bool found = false;
        if (MyNetworkManager.Instance != null)
        {
            for (int i = 0; i < MyNetworkManager.Instance._playerSelections.Count; i++)
            {
                if (MyNetworkManager.Instance._playerSelections[i].clientId == senderClientId)
                {
                    MyNetworkManager.Instance._playerSelections[i] = new MyNetworkManager.PlayerSelection
                    {
                        clientId = senderClientId,
                        playerIndex = playerIndex
                    };
                    found = true;
                    break;
                }
            }
        }

        if (!found && MyNetworkManager.Instance != null)
        {
            MyNetworkManager.Instance._playerSelections.Add(new MyNetworkManager.PlayerSelection { clientId = senderClientId, playerIndex = playerIndex });
        }
    }

    private void HandlePlayerSelection(int playerIndex)
    {
        if (IsHost)
        {
            AddOrUpdatePlayerSelectionServerRpc(playerIndex);
        }

        else
        {
            AddOrUpdatePlayerSelectionServerRpc(playerIndex);
        }
    }

    private void OnEnable()
    {
        _isBeingDestroyed = false; 

        if (MyNetworkManager.Instance != null && MyNetworkManager.Instance._playerSelections != null)
        {
            MyNetworkManager.Instance.OnRelayCreated += OnRelayCreatedHandler;

            // Immediately update UI with current join code if available (handles scene reloads)
            if (MyNetworkManager.Instance != null)
            {
                string joinCode = MyNetworkManager.Instance.GetCurrentRelayCode();

                if (!string.IsNullOrEmpty(joinCode))
                {
                    HandleNewRelayCode(joinCode);
                }
            }
            MyNetworkManager.Instance._playerSelections.OnListChanged -= OnNetworkSelectionsChanged;
            MyNetworkManager.Instance._playerSelections.OnListChanged += OnNetworkSelectionsChanged;
        }
    }

    private void OnDisable()
    {
        _isBeingDestroyed = true; 

        if (Instance == this) 
        {
            Instance = null;
        }

        if (MyNetworkManager.Instance != null && MyNetworkManager.Instance._playerSelections != null)
        {
            MyNetworkManager.Instance._playerSelections.OnListChanged -= OnNetworkSelectionsChanged;
            MyNetworkManager.Instance.OnRelayCreated -= OnRelayCreatedHandler;
        }
    }

    private void UpdateAllPlayerUIs()
    {
        if (MyNetworkManager.Instance != null && MyNetworkManager.Instance._playerSelections != null)
        {
            string selections = "";
            foreach (var sel in MyNetworkManager.Instance._playerSelections)
            {
                selections += $"[clientId={sel.clientId}, index={sel.playerIndex}], ";
            }
        }

        if (MyNetworkManager.Instance == null || MyNetworkManager.Instance._playerSelections == null)
        {
            return;
        }

        ulong hostId = NetworkManager.ServerClientId;
        int hostSelection = -1;
        int clientSelection = -1;

        foreach (var sel in MyNetworkManager.Instance._playerSelections)
        {
            if (sel.clientId == hostId)
            {
                hostSelection = sel.playerIndex;
            }

            else
            {
                clientSelection = sel.playerIndex;
            }
        }

        // Left UI (host)
        if (!leftUISet && hostSelection >= 0)
        {
            playerImage.sprite = PlayerSelector.Instance.playerSprites[hostSelection];
            playerNameText.text = PlayerSelector.Instance.playerNames[hostSelection];
            leftUISet = true;
        }
        // Right UI (client)
        if (!rightUISet && clientSelection >= 0)
        {
            opponentImage.sprite = PlayerSelector.Instance.playerSprites[clientSelection];
            opponentNameText.text = PlayerSelector.Instance.playerNames[clientSelection];
            rightUISet = true;
        }
        else if (!rightUISet)
        {
            opponentImage.sprite = null;
            opponentNameText.text = null;
        }

        if (UnityMainThreadDispatcher.Instance() != null)
        {
            UnityMainThreadDispatcher.Instance().Enqueue(UpdateUI);
        }

        else
        {
            UpdateUI();
        }
    }

    public void OnBackButtonClicked()
    {
        buttonAudioSource.Play();

        if (IsHost)
        {
            LeaveAndReturnToLobby();
            ShowOpponentLeftPopUpClientRpc();
        }
        else
        {
            LeaveAndReturnToLobby();
            ShowOpponentLeftPopUpHostServerRpc();
        }
    }

    private void LeaveAndReturnToLobby()
    {
        // Remove player selection from network list on server
        if (MyNetworkManager.Instance != null)
        {
            MyNetworkManager.Instance.RemovePlayerSelectionServerRpc(NetworkManager.Singleton.LocalClientId);
            // Host: Clear all selections BEFORE shutdown to prevent stale data on future sessions
            if (NetworkManager.Singleton.IsServer && MyNetworkManager.Instance._playerSelections != null)
            {
                MyNetworkManager.Instance.ResetConnectionState(true);
            }
        }
        // Destroy player selector singleton so a fresh one is created in the lobby
        if (PlayerSelector.Instance != null)
        {
            GameObject.Destroy(PlayerSelector.Instance.gameObject);
        }

        // Leave network session if needed
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
        {
            NetworkManager.Singleton.Shutdown();
        }

        // Ensure only one NetworkManager exists by destroying it after shutdown
        if (NetworkManager.Singleton != null)
        {
            Destroy(NetworkManager.Singleton.gameObject);
        }

        // Load lobby scene
        SceneManager.LoadScene("Lobby");
    }

    [ClientRpc]
    private void ShowOpponentLeftPopUpClientRpc()
    {
        if (!IsHost)
        {
            StartCoroutine(ShowPopUpAndReturnToLobby());
        }
    }

    [ServerRpc(RequireOwnership = false)]
    private void ShowOpponentLeftPopUpHostServerRpc()
    {
        if (IsHost && opponentLeftPopUp != null)
        {
            StartCoroutine(ShowHostPopUpCoroutine());
        }
    }

    private IEnumerator ShowHostPopUpCoroutine()
    {
        opponentLeftPopUp.SetActive(true);
        yield return new WaitForSeconds(2f);
        opponentLeftPopUp.SetActive(false);
    }

    private IEnumerator ShowPopUpAndReturnToLobby()
    {
        if (opponentLeftPopUp != null)
        {
            opponentLeftPopUp.SetActive(true);
        }

        yield return new WaitForSeconds(2f);
        
        if (MyNetworkManager.Instance != null)
        {
            MyNetworkManager.Instance.RemovePlayerSelectionServerRpc(NetworkManager.Singleton.LocalClientId);
            // Also clear on client-side leave, just in case
            if (NetworkManager.Singleton.IsServer && MyNetworkManager.Instance._playerSelections != null)
            {
                MyNetworkManager.Instance.ResetConnectionState(true);
            }
        }

        if (PlayerSelector.Instance != null)
        {
            GameObject.Destroy(PlayerSelector.Instance.gameObject);
        }
        SceneManager.LoadScene("Lobby");
    }

    private void OnApplicationQuit()
    {
        if (Unity.Netcode.NetworkManager.Singleton != null && Unity.Netcode.NetworkManager.Singleton.IsListening)
        {
            if (Unity.Netcode.NetworkManager.Singleton.IsServer)
            {
                ShowOpponentLeftPopUpHostServerRpc();
            }

            else
            {
                ShowOpponentLeftPopUpClientRpc();
            }
        }
    }
}