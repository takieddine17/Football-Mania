using UnityEngine;
using Unity.Netcode;
using Unity.Netcode.Components;
using Unity.Netcode.Transports.UTP;
using System;
using System.Net;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using System.Collections;
using Unity.Services.Core;
using Unity.Services.Authentication;
using UnityEngine.SceneManagement;
using Unity.Services.Qos;
using Unity.Networking.Transport.Relay;
using Unity.Networking.Transport;
using System.Threading;
using Unity.Collections;

[RequireComponent(typeof(UnityTransport))]
public class MyNetworkManager : NetworkBehaviour
{
    public enum ConnectionType
    {
        None,
        Relay
    }
    
    public enum RelayState 
    {
        NotInitialised,
        Initialising,
        Ready,
        Hosting,
        Joining,
        Joined,
        Error
    }

    public static MyNetworkManager Instance { get; private set; }
    private RelayState _relayState = RelayState.NotInitialised;
    private const int MAX_PLAYERS = 2; 
    private const int MAX_INITIALISATION_RETRIES = 3;
    private const int MAX_CONNECTION_RETRIES = 3;
    private const float CONNECTION_TIMEOUT = 10f;
    private const int RELAY_CODE_EXPIRY_MINUTES = 30;
    public int hostPort = 7777;
    public int discoveryPort = 7778;
    public bool enableDetailedLogging = true;
    public PlayerSelector playerSelector;
    public float initialisationRetryDelay = 2f;
    public float connectionRetryDelay = 2f;

    public GameObject[] playerPrefabs; 

    private bool isPrefabRegistered = false;
    private bool isInitialised = false;
    private CancellationTokenSource _initialisationCts;

    private UnityTransport transport;
    private string _activeRelayCode;
    public bool _isRelayInitialised;
    private DateTime? _relayCodeCreationTime;
    private bool _isRelayReady = false;
    public bool IsHostReady { get; private set; }
    public bool _isRelayFullyInitialised = false;
    private bool _isInitialising = false;
    [SerializeField] private bool _autoInitialise = true;
    private bool _isInitialised = false;
    private bool _callbacksInitialised = false;
    private int _currentInitialisationAttempt = 0;
    private bool _isRecovering = false;
    public event Action<string> OnRelayCodeGenerated;
    public event Action<RelayState> OnRelayStateChanged;
    public event Action<Exception> OnInitialisationError;
    public event Action OnInitialisationSuccess;
    private RelayServerData _cachedRelayServerData;
    public NetworkList<PlayerSelection> _playerSelections = new NetworkList<PlayerSelection>(); // Always initialised at declaration
    private bool _isServer = false;
    private bool _isClient = false;
    private HashSet<ulong> _connectedClients = new HashSet<ulong>();
    private DateTime _lastCodeGenerationTime = DateTime.UtcNow;
    private string _errorMessage;
    private bool _isJoining = false;
    public bool LockSelections { get; private set; }

    private Dictionary<ulong, NetworkObject> playerImageInstances = new Dictionary<ulong, NetworkObject>();

    [Serializable]
    public struct PlayerSelection : INetworkSerializable, IEquatable<PlayerSelection>
    {
        public ulong clientId;
        public int playerIndex;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref clientId);
            serializer.SerializeValue(ref playerIndex);
        }

        public bool Equals(PlayerSelection other) => clientId == other.clientId && playerIndex == other.playerIndex;
        public override bool Equals(object obj) => obj is PlayerSelection other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(clientId, playerIndex);
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        
        // Initialise player selections
        _playerSelections = new NetworkList<PlayerSelection>();
        
        // Enable scene management for host/server only
        if (NetworkManager.Singleton.IsHost || NetworkManager.Singleton.IsServer || NetworkManager.Singleton.IsClient)
        {
            NetworkManager.Singleton.NetworkConfig.EnableSceneManagement = true;
        }

        InitialiseComponents();
        
        if (_autoInitialise)
        {
            StartCoroutine(InitialiseCoroutine());
        }
    }

    private void Start()
    {
        // Ensure players persist between scenes
        if (SceneManager.GetActiveScene().name == "Lobby")
        {
            DontDestroyOnLoad(gameObject);
            DontDestroyOnLoad(NetworkManager.Singleton.gameObject);
        }

        NetworkManager.Singleton.NetworkConfig.PlayerPrefab = Resources.Load<GameObject>("PlayerPrefab");
        
        // Scene management configuration
        NetworkManager.Singleton.NetworkConfig.EnableSceneManagement = true;
 
    }

    private bool IsPrefabRegistered(GameObject prefab)
    {
        return NetworkManager.Singleton.NetworkConfig.Prefabs.Prefabs.Any(networkPrefab => networkPrefab.Prefab == prefab);
    }

    private void InitialiseComponents()
    {
        // Initialise transport first
        transport = GetComponent<UnityTransport>();
        if (transport == null)
        {
            transport = gameObject.AddComponent<UnityTransport>();
        }

        StartCoroutine(WaitForNetworkManager());
    }

    private IEnumerator WaitForNetworkManager()
    {
        while (NetworkManager.Singleton == null)
        {
            yield return null;
        }

        float timeout = 5f;
        float elapsed = 0f;
        bool isNetworkManagerReady = false;
        bool isNetworkInitialised = false;
        
        while (!isNetworkManagerReady && elapsed < timeout)
        {
            if (NetworkManager.Singleton != null)
            {
                isNetworkManagerReady = true;
                
                // Initialise network callbacks first
                InitialiseNetworkCallbacks();
                
                // Wait for network to be fully initialised
                while (!isNetworkInitialised && elapsed < timeout)
                {
                    if (NetworkManager.Singleton.IsServer || NetworkManager.Singleton.IsClient)
                    {
                        isNetworkInitialised = true;
                    }
                    else
                    {
                        yield return new WaitForSeconds(0.1f);
                        elapsed += 0.1f;
                    }
                }
            }
            else
            {
                elapsed += Time.deltaTime;
                yield return null;
            }
        }

        if (!isNetworkManagerReady)
        {
            yield break;
        }
    }

    private void InitialiseNetworkCallbacks()
    {
        try
        {
            if (NetworkManager.Singleton == null)
            {
                return;
            }

            // Initialise callbacks only if they haven't been initialised
            if (!_callbacksInitialised)
            {

                // Server callbacks
                NetworkManager.Singleton.OnServerStarted += () => 
                {
                    _isServer = true;
                    _isClient = false;
                };

                NetworkManager.Singleton.OnServerStopped += (bool indicator) => 
                {
                    _isServer = false;
                    _isClient = false;
                };

                // Client callbacks
                NetworkManager.Singleton.OnClientStarted += () => 
                {
                    _isClient = true;
                    _isServer = false;
                };

                NetworkManager.Singleton.OnClientStopped += (bool indicator) => 
                {
                    _isClient = false;
                    _isServer = false;
                };

                // Connection callbacks
                NetworkManager.Singleton.OnClientConnectedCallback += (id) => 
                {
                    if (NetworkManager.Singleton.IsServer)
                    {
                        _connectedClients.Add(id);
                        HandleClientConnected(id);
                    }
                };

                NetworkManager.Singleton.OnClientDisconnectCallback += (id) => 
                {
                    if (NetworkManager.Singleton.IsServer)
                    {
                        _connectedClients.Remove(id);
                        HandleClientDisconnected(id);
                    }
                };

                _callbacksInitialised = true;
            }
        }
        catch
        {
            throw;
        }
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn(); // Critical to call base first
    }

    public override void OnDestroy()
    {
        _initialisationCts?.Cancel();
        _initialisationCts?.Dispose();

        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnServerStarted -= OnServerStarted;
            NetworkManager.Singleton.OnClientConnectedCallback -= HandleClientConnected;
            NetworkManager.Singleton.OnClientDisconnectCallback -= HandleClientDisconnected;
            NetworkManager.Singleton.SceneManager.OnLoadComplete -= OnSceneLoadComplete;
        }

        base.OnDestroy();
    }

    public void Cleanup()
    {
        // Cleanup any network objects
        if (_playerSelections != null)
        {
            _playerSelections.Clear();
        }

        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.Shutdown();
        }
    }

    private IEnumerator InitialiseCoroutine()
    {
        if (_isInitialised || _isInitialising) yield break;
        
        _isInitialising = true;
        _currentInitialisationAttempt = 0;
        UpdateRelayState(RelayState.Initialising);

        while (_currentInitialisationAttempt < MAX_INITIALISATION_RETRIES)
        {
            _currentInitialisationAttempt++;
            bool initialisationSuccess = false;
            Exception initialisationError = null;

            yield return WaitForNetworkManager();

            if (NetworkManager.Singleton == null)
            {
                initialisationError = new Exception("NetworkManager.Singleton failed to initialise");
            }
            else
            {
                // Initialise callbacks
                if (!_callbacksInitialised)
                {
                    InitialiseNetworkCallbacks();
                    _callbacksInitialised = true;
                }

        yield return InitialiseServicesCoroutine();

                if (!_isRelayInitialised)
                {
                    initialisationError = new Exception("Relay services failed to initialise");
                }
                else
                {
                    initialisationSuccess = true;
                }
            }

            if (initialisationSuccess)
            {
                _isInitialised = true;
                _isInitialising = false;
                UpdateRelayState(RelayState.Ready);
                OnInitialisationSuccess?.Invoke();
                yield break;
            }
            else
            {
                OnInitialisationError?.Invoke(initialisationError ?? new Exception("Unknown initialisation error"));

                if (_currentInitialisationAttempt < MAX_INITIALISATION_RETRIES)
                {
                    UpdateRelayState(RelayState.NotInitialised);
                    yield return new WaitForSeconds(initialisationRetryDelay * _currentInitialisationAttempt);
                }
                else
                {
                    UpdateRelayState(RelayState.Error);
                    _isInitialising = false;
                    yield break;
                }
            }
        }
    }

    private void UpdateRelayState(RelayState newState)
    {
        if (_relayState != newState)
        {
            _relayState = newState;
            OnRelayStateChanged?.Invoke(newState);
        }
    }

    private IEnumerator InitialiseServicesCoroutine()
    {
        if (_isRelayInitialised)
        {
            yield break;
        }

        UpdateRelayState(RelayState.Initialising);

        int retryCount = 0;
        bool servicesInitialised = false;
        Exception lastError = null;

        while (retryCount < MAX_INITIALISATION_RETRIES && !servicesInitialised)
        {
            retryCount++;
            
            // Create new cancellation token for this attempt
            _initialisationCts?.Cancel();
            _initialisationCts = new CancellationTokenSource();
            
            // Initialise Unity Services
            if (UnityServices.State != ServicesInitializationState.Initialized)
            {
                var initTask = UnityServices.InitializeAsync();
                while (!initTask.IsCompleted)
                {
                    if (_initialisationCts.Token.IsCancellationRequested)
                    {
                        yield break;
                    }
                    yield return null;
                }

                if (initTask.Exception != null)
                {
                    continue;
                }
            }

            // Handle authentication
            if (!AuthenticationService.Instance.IsSignedIn)
            {
                var authTask = AuthenticationService.Instance.SignInAnonymouslyAsync();
                
                float authTimeout = 10f;
                float authElapsed = 0f;
                
                while (!authTask.IsCompleted && authElapsed < authTimeout)
                {
                    if (_initialisationCts.Token.IsCancellationRequested)
                    {
                        yield break;
                    }

                    authElapsed += Time.deltaTime;
                    yield return null;
                }

                if (authElapsed >= authTimeout)
                {
                    lastError = new TimeoutException("Authentication timed out");
                    continue;
                }

                if (authTask.Exception != null)
                {
                    lastError = new Exception($"Authentication failed: {authTask.Exception.Message}");
                    continue;
                }

                // Verify authentication
                if (!AuthenticationService.Instance.IsSignedIn)
                {
                    lastError = new Exception("Authentication verification failed");
                    continue;
                }
            }

            // Initialise transport settings
            try
            {
                ConfigureTransport();
                servicesInitialised = true;
                _isRelayInitialised = true;
                yield break;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }

            if (!servicesInitialised)
            {
                if (retryCount < MAX_INITIALISATION_RETRIES)
                {
                    yield return new WaitForSeconds(initialisationRetryDelay * retryCount);
                }
                else
                {
                    UpdateRelayState(RelayState.Error);
                    throw lastError ?? new Exception("Services initialisation failed");
                }
            }
        }
    }

    private void ConfigureTransport()
    {
        if (transport == null)
        {
            throw new NullReferenceException("Transport component is missing");
        }

        // Configure default transport settings
        transport.MaxConnectAttempts = MAX_CONNECTION_RETRIES;
        transport.ConnectTimeoutMS = (int)(CONNECTION_TIMEOUT * 1000);
    }

    private void OnSceneLoadComplete(ulong clientId, string sceneName, LoadSceneMode loadMode)
    {
        // Only redirect if we're not in Match scene
        if (sceneName != "Match")
        {
            NetworkManager.Singleton.SceneManager.LoadScene("Match", LoadSceneMode.Single);
        }
    }
    private async Task InitialiseUnityServicesAsync()
    {
        try
        {
            await InitialiseUnityServices();
        }
        catch
        {
            throw;
        }
    }

    public async Task InitialiseUnityServices()
    {
        if (UnityServices.State == ServicesInitializationState.Initialized && 
            AuthenticationService.Instance.IsSignedIn)
        {
            _isRelayInitialised = true;
            return;
        }

        try
        {
            // Initialise services
            if (UnityServices.State != ServicesInitializationState.Initialized)
            {
                await UnityServices.InitializeAsync();
            }

            // Authenticate
            if (!AuthenticationService.Instance.IsSignedIn)
            {
                await AuthenticationService.Instance.SignInAnonymouslyAsync();
                
                // Add verification wait
                float timeout = 10f;
                float elapsed = 0f;
                
                while (!AuthenticationService.Instance.IsSignedIn && elapsed < timeout)
                {
                    await Task.Delay(500);
                    elapsed += 0.5f;
                }
                
                if (!AuthenticationService.Instance.IsSignedIn)
                {
                    throw new Exception("Authentication timeout");
                }
            }

            _isRelayInitialised = true;
        }
        catch
        {
            _isRelayInitialised = false;
            throw;
        }
    }

    private void HandleClientConnected(ulong clientId)
    {
        if (IsServer)
        {
            // Force immediate synchronisation
            foreach (var selection in _playerSelections)
            {
                UpdatePlayerSelectionClientRpc(selection.clientId, selection.playerIndex);
            }
            // Update UI state
            if (HostGameManager.Instance != null && !HostGameManager.Instance._isBeingDestroyed)
            {
                if (HostGameManager.Instance != null && !HostGameManager.Instance._isBeingDestroyed)
                    HostGameManager.Instance.OnOpponentConnected();
                // Update button states
                if (HostGameManager.Instance != null && !HostGameManager.Instance._isBeingDestroyed && HostGameManager.Instance.startGameButton != null)
                {
                    if (HostGameManager.Instance != null && !HostGameManager.Instance._isBeingDestroyed && HostGameManager.Instance.startGameButton != null)
                        HostGameManager.Instance.startGameButton.interactable = true;
                }
                // Request client's player selection
                RequestPlayerSelectionClientRpc(clientId);
            }
        }
    }

    private void CleanupNetworkObjects()
    {
        // Find all NetworkObjects in the scene
        var networkObjects = FindObjectsOfType<NetworkObject>();
        foreach (var networkObject in networkObjects)
        {
            // Only destroy if we're the server
            if (IsServer && networkObject.IsSpawned)
            {
                // Let the server handle the destruction
                networkObject.Despawn(false);
            }
        }
    }

    [ClientRpc]
    private void RequestPlayerSelectionClientRpc(ulong clientId)
    {
        if (NetworkManager.Singleton.LocalClientId == clientId)
        {
            // If this is the requested client, send their selection
            int playerIndex = PlayerSelector.Instance?.selectedPlayerIndex ?? -1;
            if (playerIndex >= 0)
            {
                SubmitPlayerSelectionServerRpc(playerIndex);
            }
        }
    }

    [ServerRpc(RequireOwnership = false)]
    public void SubmitPlayerSelectionServerRpc(int playerIndex, ServerRpcParams rpcParams = default)
    {
        ulong clientId = rpcParams.Receive.SenderClientId;
        // First verify the player index is valid
        if (playerIndex < 0 || playerIndex >= PlayerSelector.Instance.playerNames.Length)
        {
            return;
        }
        // Update the selection for this client
        SetPlayerSelection(clientId, playerIndex);
        // Force immediate sync to all clients
        SyncPlayerDataClientRpc(clientId, playerIndex, PlayerSelector.Instance.playerNames[playerIndex]);
    }

    [ServerRpc(RequireOwnership = false)]
    public void SyncPlayerDataServerRpc(ulong clientId, int playerIndex, FixedString128Bytes playerName)
    {
        SyncPlayerDataClientRpc(clientId, playerIndex, playerName);
    }

    [ClientRpc]
    private void SyncPlayerDataClientRpc(ulong clientId, int playerIndex, FixedString128Bytes playerName)
    {
        Debug.Log($"[Network] Received player data - Client: {clientId}, Index: {playerIndex}, Name: {playerName}");
    }

    [ClientRpc]
    private void UpdatePlayerSelectionClientRpc(ulong clientId, int playerIndex)
    {
        if (IsServer) 
        {
            Debug.Log($"[Network] RPC host notification for {clientId}");
        }
        else 
        {
            Debug.Log($"[Network] Received selection update (client) - Client: {clientId}, Index: {playerIndex}");
        }
    }

    [ServerRpc(RequireOwnership = false)]
    private void UpdatePlayerSelectionServerRpc(ulong clientId, int playerIndex)
    {
        if (HostGameManager.Instance != null && !HostGameManager.Instance._isBeingDestroyed)
        {
            HostGameManager.Instance.OnPlayerSelectionUpdated(clientId, playerIndex);
        }
    }

    private void NotifyPlayerSelection(ulong clientId, int playerIndex)
    {
        if (HostGameManager.Instance != null && !HostGameManager.Instance._isBeingDestroyed)
        {
            HostGameManager.Instance.OnPlayerSelectionUpdated(clientId, playerIndex);
        }
    }

    private async Task<string> GetBestRegion()
    {
        const string serviceName = "relay";
        try
        {
            var results = await QosService.Instance.GetSortedQosResultsAsync(serviceName, null);
            return results[0].Region;
        }
        catch
        {
            return "eu-west";
        }
    }

    private void OnServerStarted()
    {
        IsHostReady = true;
        if (playerSelector != null && playerSelector.playerConfirmed)
        {
            SubmitPlayerSelectionServerRpc(playerSelector.selectedPlayerIndex);
        }
    }

    public void SetCurrentRelayCode(string code)
    {
        _activeRelayCode = code;
        PlayerPrefs.SetString("LastRelayCode", code); // Persist between scenes
        PlayerPrefs.Save();
    }

    public string GetCurrentRelayCode()
    {
        if (!string.IsNullOrEmpty(_activeRelayCode))
        {
            return _activeRelayCode;
        }
        
        // Fallback to PlayerPrefs if needed
        return PlayerPrefs.GetString("LastRelayCode", "");
    }

    private string GetLocalIPAddress()
    {
        foreach (var ip in Dns.GetHostEntry(Dns.GetHostName()).AddressList)
        {
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                return ip.ToString();
            }
        }
        return "127.0.0.1";
    }

    private void ConfigureTransport(RelayServerData relayData)
    {
        if (transport == null)
        {
            throw new NullReferenceException("Transport component is missing");
        }

        try
        {
            // Clear any existing configuration
            transport.SetConnectionData("0.0.0.0", 0);
            
            // Apply new relay configuration
            transport.SetRelayServerData(relayData);
        }

        catch
        {
            throw;
        }
    }

    public async Task<bool> StartHostingWithoutSceneLoad(bool useRelay = true)
    {
        // Prevent clients from calling this
        if (!NetworkManager.Singleton.IsServer)
        {
            return false;
        }

        if (NetworkManager.Singleton.IsServer) return true;

        try
        {
            if (useRelay && string.IsNullOrEmpty(_activeRelayCode))
            {
                // Start relay creation and scene load in parallel
                var relayTask = CreateRelay();

                if (NetworkManager.Singleton.IsServer)
                {
                    var sceneLoadTask = SceneManager.LoadSceneAsync("HostMatch");
                    await sceneLoadTask;
                }

                // Wait for relay task
                string relayCode = await relayTask;

                if (string.IsNullOrEmpty(relayCode))
                {
                    throw new Exception("Failed to create relay");
                }
                _activeRelayCode = relayCode;
            }

            // Start host and wait for it to be ready
            if (!NetworkManager.Singleton.StartHost())
            {
                throw new Exception("Failed to start host");
            }

            // Wait for server to be fully ready
            float timeout = 10f;
            float elapsed = 0f;
            while (!IsHostReady && elapsed < timeout)
            {
                await Task.Delay(100);
                elapsed += 0.1f;
            }

            if (!IsHostReady)
            {
                throw new Exception("Host failed to initialise within timeout");
            }

            return true;
        }
        catch
        {
            throw;
        }
    }

    public void ForceShutdown()
    {
        if (NetworkManager.Singleton != null)
        {
            if (NetworkManager.Singleton.IsListening)
            {
                NetworkManager.Singleton.Shutdown();
            }
            
            // Clear all callbacks
            NetworkManager.Singleton.OnServerStarted -= OnServerStarted;
            NetworkManager.Singleton.OnClientConnectedCallback -= HandleClientConnected;
            NetworkManager.Singleton.OnClientDisconnectCallback -= HandleClientDisconnected;
        }
    }

    public bool IsInitialised() => _isInitialised;

    private string GetUserFriendlyError(RelayServiceException e)
    {
        return GetRelayErrorMessage(e);
    }

    public string GetRelayErrorMessage(RelayServiceException e)
    {
        // First try to parse the enum if it exists
        try
        {
            switch (e.Reason.ToString())
            {
                case "JoinCodeNotFound":
                case "AllocationNotFound":
                    return "Game not found - please check the code";
                case "InvalidRequest":
                case "InvalidAllocation":
                    return "Invalid join code format";
                case "RegionNotFound":
                    return "Region unavailable";
                case "JoinFailed":
                    return "Failed to join game";
                default:
                    return "Connection failed";
            }
        }
        catch
        {
            // Fallback to message parsing if enum fails
            return ParseRelayErrorMessage(e.Message);
        }
    }

    private string ParseRelayErrorMessage(string message)
    {
        if (message.Contains("join code", StringComparison.OrdinalIgnoreCase))
            return "Invalid game code";
        if (message.Contains("not found", StringComparison.OrdinalIgnoreCase))
            return "Game not found";
        if (message.Contains("allocation", StringComparison.OrdinalIgnoreCase))
            return "Game session ended";
        if (message.Contains("region", StringComparison.OrdinalIgnoreCase))
            return "Region unavailable";
        
        return "Connection failed";
    }

    public void ResetConnectionState(bool isFullDisconnect = true)
    {
        if (_playerSelections != null && isFullDisconnect)
        {
            _playerSelections.Clear();
        }
        if (NetworkManager.Singleton != null)
        {
            if (NetworkManager.Singleton.IsListening)
            {
                NetworkManager.Singleton.Shutdown();
            }
        }
        
        _activeRelayCode = null;
        _isRelayReady = false;
        _relayState = RelayState.NotInitialised;
        

    }

    public void CleanupBeforeSceneLoad()
    {
        
        // Store critical values before cleanup
        string preservedCode = _activeRelayCode;
        bool preservedRelayState = _isRelayReady;
        bool preservedInitialised = _isRelayInitialised;
        RelayState preservedRelayStateEnum = _relayState;
        RelayServerData preservedRelayData = _cachedRelayServerData;

        // Store in PlayerPrefs immediately as backup
        if (!string.IsNullOrEmpty(preservedCode))
        {
            PlayerPrefs.SetString("LastRelayCode", preservedCode);
            PlayerPrefs.SetInt("LastRelayState", (int)preservedRelayStateEnum);
            PlayerPrefs.Save();
        }

        string savedCode = PlayerPrefs.GetString("LastRelayCode", "");
    }

    public bool GetIsRelayReady()
    {
        return _isRelayReady;
    }

    public async Task<bool> VerifyAndRecoverRelayState()
    {
        if (_isRecovering) return false;
        _isRecovering = true;

        try
        {
            // Check if we need to reinitialise services
            if (!_isRelayInitialised || UnityServices.State != ServicesInitializationState.Initialized)
            {
                var initCoroutine = InitialiseServicesCoroutine();
                while (initCoroutine.MoveNext())
                {
                    await Task.Yield();
                }
            }

            // Verify authentication
            if (!AuthenticationService.Instance.IsSignedIn)
            {
                await EnsureAuthentication();
            }

            // Verify active relay code if we have one
            if (!string.IsNullOrEmpty(_activeRelayCode))
            {
                if (IsRelayCodeExpired())
                {
                    _activeRelayCode = null;
                    UpdateRelayState(RelayState.Ready);
                }
                else
                {
                    // Verify the relay is still joinable
                    bool isJoinable = await VerifyRelayCode(_activeRelayCode);
                    if (!isJoinable)
                    {
                        _activeRelayCode = null;
                        UpdateRelayState(RelayState.Ready);
                    }
                }
            }

            _isRelayReady = true;
            return true;
        }
        catch
        {
            UpdateRelayState(RelayState.Error);
            return false;
        }
        finally
        {
            _isRecovering = false;
        }
    }

    private async Task EnsureAuthentication()
    {
        if (AuthenticationService.Instance.IsSignedIn)
        {
            await Task.Run(() => AuthenticationService.Instance.SignOut());
            await Task.Delay(1000); // Increased delay after signout
        }

        try
        {
            await AuthenticationService.Instance.SignInAnonymouslyAsync();
            
            // Wait for authentication to be fully ready
            float timeout = 10f;
            float elapsed = 0f;
            while (!AuthenticationService.Instance.IsSignedIn && elapsed < timeout)
            {
                await Task.Delay(500);
                elapsed += 0.5f;
            }

            if (!AuthenticationService.Instance.IsSignedIn)
            {
                throw new Exception("Authentication failed to complete");
            }

            await Task.Delay(1000); // Additional stabilisation period
        }
        catch
        {
            throw;
        }
    }

    private string GetEndpointString(NetworkEndpoint endpoint)
    {
        return endpoint.Address.Split(':')[0];
    }

    public bool IsRelayReady() => _isRelayReady;

    public async Task<bool> JoinRelay(string relayCode)
    {
        try
        {
            // Reset transport before joining
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.NetworkConfig.NetworkTransport is UnityTransport transport)
            {
                transport.Shutdown();
                await Task.Delay(100);
            }

            // Ensure services are ready
            if (UnityServices.State != ServicesInitializationState.Initialized)
            {
                var initOptions = new InitializationOptions();
                await UnityServices.InitializeAsync(initOptions);
                
                if (!AuthenticationService.Instance.IsSignedIn)
                {
                    await AuthenticationService.Instance.SignInAnonymouslyAsync();
                }
            }

            // Verify the relay code first
            if (!await VerifyRelayCode(relayCode))
            {
                return false;
            }

            // Join the allocation with retries
            JoinAllocation joinAllocation = null;
            int maxRetries = 3;
            int currentRetry = 0;
            
            while (currentRetry < maxRetries)
            {
                try 
                {
                    joinAllocation = await RelayService.Instance.JoinAllocationAsync(relayCode)
                        .WithTimeout(TimeSpan.FromSeconds(10));
                    break;
                }
                catch
                {
                    currentRetry++;
                    if (currentRetry >= maxRetries)
                    {
                        return false;
                    }
                    await Task.Delay(1000 * currentRetry);
                }
            }

            if (joinAllocation == null)
            {
                return false;
            }

            // Configure transport with relay data
            var relayServerData = new RelayServerData(joinAllocation, "dtls");
            ConfigureTransportWithRelay(relayServerData);

            // Start client
            if (!NetworkManager.Singleton.StartClient())
            {
                return false;
            }

            // Wait for connection with more detailed status checks
            float timeout = 30f;
            float elapsed = 0f;
            bool connectionVerified = false;
            UnityTransport relayTransport = NetworkManager.Singleton.NetworkConfig.NetworkTransport as UnityTransport;

            while (elapsed < timeout && !connectionVerified)
            {
                // Check network state
                bool isClient = NetworkManager.Singleton.IsClient;
                bool isConnected = NetworkManager.Singleton.IsConnectedClient;
                bool isConnecting = !isConnected && NetworkManager.Singleton.NetworkConfig.NetworkTransport.IsSupported;
                bool isDisconnected = !isConnected && !isConnecting;

                if (isConnected)
                {
                    connectionVerified = true;
                    return true;
                }

                await Task.Delay(500);
                elapsed += 0.5f;
            }

            if (!connectionVerified)
            {
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private void ConfigureTransportWithRelay(RelayServerData relayData)
    {
        if (NetworkManager.Singleton.NetworkConfig.NetworkTransport is UnityTransport transport)
        {
            transport.SetRelayServerData(relayData);
        }
    }

    private IEnumerator DelayedPlayerUpdate(ulong clientId, int playerIndex)
    {
        yield return new WaitUntil(() => HostGameManager.Instance != null && !HostGameManager.Instance._isBeingDestroyed);
        
        if (HostGameManager.Instance != null && !HostGameManager.Instance._isBeingDestroyed)
        {
            HostGameManager.Instance.OnPlayerSelectionUpdated(clientId, playerIndex);
        }
    }

    private void HandleClientDisconnected(ulong clientId)
    {
        if (!IsServer)
        {
            SceneManager.LoadScene("Lobby");
            return;
        }

        // If the host is disconnecting, clear all selections
        if (clientId == NetworkManager.Singleton.LocalClientId && NetworkManager.Singleton.IsHost)
        {
            _playerSelections.Clear();
        }
        else
        {
            // Remove only the disconnected client's selection
            for (int i = _playerSelections.Count - 1; i >= 0; i--)
            {
                if (_playerSelections[i].clientId == clientId)
                {
                    _playerSelections.RemoveAt(i);
                }
            }
        }
    }

    public async Task<string> CreateRelay()
    {
        try
        {
            
            // Create allocation with timeout
            var allocation = await RelayService.Instance.CreateAllocationAsync(MAX_PLAYERS)
                .WithTimeout(TimeSpan.FromSeconds(15));
            
            if (allocation == null)
            {
                throw new Exception("Failed to create allocation");
            }
            
            // Get join code with timeout
            string joinCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId)
                .WithTimeout(TimeSpan.FromSeconds(15));
            
            if (string.IsNullOrEmpty(joinCode))
            {
                throw new Exception("Failed to get join code");
            }
            
            // Store and broadcast join code
            SetCurrentRelayCode(joinCode);
            OnRelayCodeGenerated?.Invoke(joinCode);

            // Store the join code and creation time
            _activeRelayCode = joinCode;
            _relayCodeCreationTime = DateTime.UtcNow;
            PlayerPrefs.SetString("LastRelayCode", joinCode);
            PlayerPrefs.SetString("LastRelayCodeTime", _relayCodeCreationTime.Value.ToString());
            PlayerPrefs.Save();

            // Configure transport
            var relayServerData = new RelayServerData(allocation, "dtls");
            if (NetworkManager.Singleton.NetworkConfig.NetworkTransport is UnityTransport transport)
            {
                transport.SetRelayServerData(relayServerData);
            }
            else
            {
                throw new Exception("Failed to configure transport - UnityTransport not found");
            }

            OnRelayCreated?.Invoke(_activeRelayCode);

            return joinCode;
        }
        catch
        {
            return null;
        }
    }

    public event Action<string> OnRelayCreated;


    public async Task<bool> JoinRelayWithRetry(string joinCode, int maxAttempts = 3)
    {
        if (string.IsNullOrWhiteSpace(joinCode))
        {
            return false;
        }

        joinCode = joinCode.Trim().ToUpper();

        // Validate format
        if (joinCode.Length != 6 || !joinCode.All(char.IsLetterOrDigit))
        {
            return false;
        }

        // Track state
        bool success = false;
        Exception lastError = null;
        int currentAttempt = 0;

        while (currentAttempt < maxAttempts && !success)
        {
            currentAttempt++;

            try
            {
                // 1. Ensure services are ready
                await EnsureServicesReady();

                // 2. Reset transport state
                await ResetTransport();

                // 3. Join with verification
                success = await JoinAndVerifyRelay(joinCode);
                
                if (success)
                {
                    return true;
                }
            }
            catch (RelayServiceException ex)
            {
                lastError = ex;
                // Don't retry for these cases
                if (ex.Reason.ToString().Contains("NotFound") || ex.Reason.ToString().Contains("Invalid"))
                {
                    break;
                }
            }
            catch (Exception ex)
            {
                lastError = ex;
            }

            // Exponential backoff before retry
            if (!success && currentAttempt < maxAttempts)
            {
                int delaySeconds = 2 * currentAttempt;
                await Task.Delay(delaySeconds * 1000);
            }
        }

        if (!success)
        {
            UpdateRelayState(RelayState.Error);
        }

        return success;
    }

    private bool IsRelayCodeExpired()
    {
        // If we don't have a creation time, check PlayerPrefs as fallback
        if (!_relayCodeCreationTime.HasValue)
        {
            string savedTimeStr = PlayerPrefs.GetString("LastRelayCodeTime", "");
            if (DateTime.TryParse(savedTimeStr, out DateTime savedTime))
            {
                _relayCodeCreationTime = savedTime;
            }
            else
            {
                return true;
            }
        }
        
        var timeSinceCreation = DateTime.UtcNow - _relayCodeCreationTime.Value;
        bool isExpired = timeSinceCreation.TotalMinutes > RELAY_CODE_EXPIRY_MINUTES;
        
        return isExpired;
    }

    private async Task<bool> VerifyRelayCode(string joinCode)
    {
        try 
        {
            if (!AuthenticationService.Instance.IsSignedIn)
            {
                return false;
            }

            var allocation = await RelayService.Instance.JoinAllocationAsync(joinCode)
                .WithTimeout(TimeSpan.FromSeconds(5));
            
            if (allocation == null)
            {
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task EnsureServicesReady()
    {
        if (UnityServices.State != ServicesInitializationState.Initialized)
        {
            await UnityServices.InitializeAsync();
        }

        if (!AuthenticationService.Instance.IsSignedIn)
        {
            await AuthenticationService.Instance.SignInAnonymouslyAsync();
        }
    }

    private async Task ResetTransport()
    {
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.NetworkConfig.NetworkTransport is UnityTransport transport)
        {
            // Shutdown the transport
            transport.Shutdown();
            
            // Wait for transport to fully shutdown
            await Task.Delay(1000);
            
            // Reset transport state
            transport.SetConnectionData("0.0.0.0", 0);
        }
    }

    private async Task<bool> JoinAndVerifyRelay(string joinCode)
    {
        if (_isJoining)
        {
            return false;
        }

        _isJoining = true;
        UpdateRelayState(RelayState.Joining);

        try
        {
            // Ensure any existing client is shut down
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsClient)
            {
                NetworkManager.Singleton.Shutdown();
                await Task.Delay(2000); // Increased delay for shutdown to complete
            }
            
            // Join the relay
            var joinAllocation = await RelayService.Instance.JoinAllocationAsync(joinCode);

            // Configure transport
            var relayServerData = new RelayServerData(joinAllocation, "dtls");
            if (NetworkManager.Singleton.NetworkConfig.NetworkTransport is UnityTransport transport)
            {
                transport.SetRelayServerData(relayServerData);
            }
            else
            {
                throw new Exception("Failed to configure transport - UnityTransport not found");
            }

            // Start client
            if (!NetworkManager.Singleton.StartClient())
            {
                throw new Exception("Failed to start client");
            }

            // Wait for connection with more detailed status checks
            float timeout = 60f;
            float elapsed = 0f;
            bool connectionVerified = false;

            while (elapsed < timeout && !connectionVerified)
            {
                // Check network state
                bool isClient = NetworkManager.Singleton.IsClient;
                bool isConnected = NetworkManager.Singleton.IsConnectedClient;
                bool isConnecting = !isConnected && NetworkManager.Singleton.NetworkConfig.NetworkTransport.IsSupported;
                bool isDisconnected = !isConnected && !isConnecting;

                if (isConnected)
                {
                    connectionVerified = true;
                    return true;
                }

                await Task.Delay(500);
                elapsed += 0.5f;
            }

            if (!connectionVerified)
            {
                return false;
            }

            return true;
        }
        catch
        {
            UpdateRelayState(RelayState.Error);
            throw;
        }
        finally
        {
            _isJoining = false;
        }
    }

    public void SetPlayerSelection(ulong clientId, int playerIndex)
    {
        bool found = false;
        for (int i = 0; i < _playerSelections.Count; i++)
        {
            if (_playerSelections[i].clientId == clientId)
            {
                // Update the player's selection in the NetworkList
                _playerSelections[i] = new PlayerSelection { clientId = clientId, playerIndex = playerIndex };
                found = true;
                break;
            }
        }
        if (!found)
        {
            // Add new selection to the NetworkList
            _playerSelections.Add(new PlayerSelection { clientId = clientId, playerIndex = playerIndex });
        }
    }

    

    public int GetPlayerSelection(ulong clientId)
    {
        foreach (var selection in _playerSelections)
        {
            if (selection.clientId == clientId)
            {
                return selection.playerIndex;
            }
        }
        return -1;
    }

    [ServerRpc(RequireOwnership = false)]
    public void RemovePlayerSelectionServerRpc(ulong clientId)
    {
        for (int i = _playerSelections.Count - 1; i >= 0; i--)
        {
            if (_playerSelections[i].clientId == clientId)
            {
                _playerSelections.RemoveAt(i);
            }
        }
    }

    public void LockPlayerSelections()
    {
        if (!IsServer) return;
        LockSelections = true;
    }

    public void RegisterSelection(ulong clientId, int playerIndex)
    {
        if (!IsServer || LockSelections) return;

        // Check if the client already has a selection
        bool found = false;
        for (int i = 0; i < _playerSelections.Count; i++)
        {
            if (_playerSelections[i].clientId == clientId)
            {
                var selection = _playerSelections[i];
                selection.playerIndex = playerIndex;
                _playerSelections[i] = selection;
                found = true;
                break;
            }
        }
        if (!found)
        {
            _playerSelections.Add(new PlayerSelection { clientId = clientId, playerIndex = playerIndex });
        }
    }
}

public static class TaskExtensions
{
    public static async Task<T> WithTimeout<T>(this Task<T> task, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource();
        var timeoutTask = Task.Delay(timeout, cts.Token);
        var completedTask = await Task.WhenAny(task, timeoutTask);
        
        if (completedTask == timeoutTask)
        {
            throw new TimeoutException($"Operation timed out after {timeout.TotalSeconds} seconds");
        }
        
        cts.Cancel(); // Cancel the timeout task
        return await task; // Unwrap the result
    }
}