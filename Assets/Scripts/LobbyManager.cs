using UnityEngine;
using UnityEngine.SceneManagement;
using System.Collections;
using Unity.Netcode;

using UnityEngine.UI;
using System;
using Unity.Netcode.Transports.UTP;
using TMPro;
using System.Threading.Tasks;
using Unity.Services.Authentication;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using Unity.Collections;
using Unity.Networking.Transport;
using System.Linq;
using Unity.Services.Core;
using UnityEngine.Audio;

public class LobbyManager : MonoBehaviour
{
    public AudioSource selectorAudioSource;
    public AudioSource buttonAudioSource;
    public AudioSource transitionAudioSource;
    public static LobbyManager Instance { get; private set; }

    [SerializeField] private PlayerSelector playerSelector;
    [SerializeField] private Button searchGameButton;
    [SerializeField] private Button hostGameButton;
    [SerializeField] private GameObject loadingPopUp;
    [SerializeField] private GameObject connectPanel;
    [SerializeField] private GameObject InvalidCodePopUp;
    [SerializeField] private Button joinButton;
    [SerializeField] private Button CloseButton;
    [SerializeField] private TMP_InputField joinCodeField;

    private const int MIN_JOIN_CODE_LENGTH = 6;
    private string _lastRelayCode;

    private void Awake()
    {
        transitionAudioSource.Play();
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;

        // NetworkManager
        var networkManagers = FindObjectsOfType<Unity.Netcode.NetworkManager>();
        if (networkManagers.Length > 1)
        {
            foreach (var mgr in networkManagers)
            {
                if (mgr != Unity.Netcode.NetworkManager.Singleton)
                {
                    Destroy(mgr.gameObject);
                }
            }
        }

        var myNetworkManagers = FindObjectsOfType<MyNetworkManager>();
        if (myNetworkManagers.Length > 1)
        {
            foreach (var mgr in myNetworkManagers)
            {
                // Only destroy if it's not the current instance AND it's not the one in the scene
                if (mgr != MyNetworkManager.Instance && !mgr.gameObject.scene.isLoaded)
                {
                    Destroy(mgr.gameObject);
                }
            }
        }

        // PlayerSelector
        var playerSelectors = FindObjectsOfType<PlayerSelector>();
        if (playerSelectors.Length > 1)
        {
            foreach (var ps in playerSelectors)
            {
                if (ps != PlayerSelector.Instance)
                {
                    Destroy(ps.gameObject);
                }
            }
        }
    }

    private IEnumerator Start()
    {
        InitialiseUI();
        
        yield return StartCoroutine(WaitForNetworkManagerInitialisation());

        if (MyNetworkManager.Instance == null || !MyNetworkManager.Instance.IsInitialised() || NetworkManager.Singleton == null)
        {
            yield break;
        }

        InitialiseButtons();
        InitialisePlayerSelector();

        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private IEnumerator WaitForNetworkManagerInitialisation()
    {
        float timeout = 10f;
        float elapsed = 0f;

        while (elapsed < timeout)
        {
            if (MyNetworkManager.Instance != null && MyNetworkManager.Instance.IsInitialised() && NetworkManager.Singleton != null)
            {
                break;
            }

            elapsed += Time.deltaTime;
            yield return null;
        }
    }

    private void InitialiseButtons()
    {
        if (hostGameButton != null) 
        {
            hostGameButton.onClick.AddListener(HostGame);
        }
        
        if (searchGameButton != null) 
        {
            searchGameButton.onClick.AddListener(ShowConnectPanel);
        }
        
        if (joinButton != null) 
        {
            joinButton.onClick.AddListener(JoinWithCode);
        }
        
        if (CloseButton != null) 
        {
            CloseButton.onClick.AddListener(ClosePopUp);
        }
    }

    private async Task EnsureNetworkManagerInitialised()
    {
        if (MyNetworkManager.Instance == null)
        {
            var go = new GameObject("NetworkManager");
            var networkManager = go.AddComponent<MyNetworkManager>();
            
            // Wait for NetworkManager to initialise
            await Task.Yield();
        }
    }

    private void InitialiseUI()
    {
        if (hostGameButton != null) 
        {
            hostGameButton.interactable = false;
        }
        
        if (searchGameButton != null) 
        {
            searchGameButton.interactable = false;
        }
        
        if (loadingPopUp != null) 
        {
            loadingPopUp.SetActive(false);
        }
    }

    private void InitialisePlayerSelector()
    {
        if (playerSelector == null)
        {
            playerSelector = FindAnyObjectByType<PlayerSelector>();
        }
    }

    public void UpdateButtons()
    {
        if (playerSelector == null) 
        {
            return;
        }

        bool isNetworkActive = NetworkManager.Singleton != null && 
                            (NetworkManager.Singleton.IsServer || NetworkManager.Singleton.IsClient);

        bool buttonsShouldBeActive = playerSelector.playerConfirmed && !isNetworkActive;

        if (hostGameButton != null) 
        {
            hostGameButton.interactable = true;
        }

        if (searchGameButton != null) 
        {
            searchGameButton.interactable = true;
        }
    }
    
    private async void HostGame()
    {
        buttonAudioSource.Play();
        ShowLoading(true);
        
        try
        {
            if (!MyNetworkManager.Instance.IsInitialised() || NetworkManager.Singleton == null || !playerSelector.playerConfirmed)
            {
                throw new Exception("System not ready - please try again");
            }

            // Create relay first
            string joinCode = await MyNetworkManager.Instance.CreateRelay();

            // Wait for relay connection to be fully established
            await Task.Delay(1000); // Give relay connection time to stabilise

            // Start host and verify
            if (!NetworkManager.Singleton.StartHost())
            {
                throw new Exception("Host failed to start");
            }

            // Additional verification
            await Task.Delay(500);
            if (!NetworkManager.Singleton.IsServer)
            {
                throw new Exception("Host verification failed");
            }

            // Cleanup before scene transition
            MyNetworkManager.Instance.CleanupBeforeSceneLoad();
            // Load scene after everything is ready
            NetworkManager.Singleton.SceneManager.LoadScene("HostMatch", LoadSceneMode.Single);

            // After scene loads, ensure the host game manager is initialised
            await Task.Delay(1000); // Give the scene time to load
            HostGameManager hostManager = FindObjectOfType<HostGameManager>();
            if (hostManager != null)
            {
                // Set authoritative join code for host and update UI immediately
                hostManager.networkJoinCode.Value = new FixedString32Bytes(joinCode);
                hostManager.SetJoinCode(joinCode);
                // Store the join code in PlayerPrefs for HostGameManager to use
                PlayerPrefs.SetString("RelayCode", joinCode);
                PlayerPrefs.Save();
            }
        }
        catch
        {
            // Reset state on failure
            MyNetworkManager.Instance.ResetConnectionState(false);
            if (NetworkManager.Singleton != null)
            {
                NetworkManager.Singleton.Shutdown();
            }
        }
        finally
        {
            ShowLoading(false);
        }
    }

    private async Task VerifyServicesReady()
    {
        if (UnityServices.State != ServicesInitializationState.Initialized)
        {
            await UnityServices.InitializeAsync();
            await AuthenticationService.Instance.SignInAnonymouslyAsync();
            await Task.Delay(500); // Mandatory service stabilisation
        }
    }

    private async Task ClearExistingConnection()
    {
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
        {
            NetworkManager.Singleton.Shutdown();
            await Task.Delay(1000); // Crucial cleanup delay
            
            // Reset transport to clean state
            var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
            transport.SetConnectionData("0.0.0.0", 0);
        }
    }

    private async Task AttemptRelayJoin(string joinCode)
    {
        // First verify basic code format
        if (string.IsNullOrWhiteSpace(joinCode) || joinCode.Length != 6)
        {
            throw new Exception("Invalid join code format");
        }

        // Initialise services if needed
        await VerifyServicesReady();
        await ClearExistingConnection();

        // Attempt join with retries
        bool success = false;
        Exception lastError = null;
        
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                // Fresh authentication each attempt
                if (AuthenticationService.Instance.IsSignedIn)
                {
                    AuthenticationService.Instance.SignOut();
                    await Task.Delay(500);
                }

                await AuthenticationService.Instance.SignInAnonymouslyAsync();
                await Task.Delay(1000);

                // Actual join attempt
                success = await MyNetworkManager.Instance.JoinRelay(joinCode);
                if (success) break;
            }
            catch (Exception e)
            {
                lastError = e;
                Debug.LogWarning($"Attempt {attempt + 1} failed: {e.Message}");
                await Task.Delay(2000 * (attempt + 1));
            }
        }

        if (!success)
        {
            throw lastError ?? new Exception("Failed to join relay");
        }

        // Final verification
        await Task.Delay(1000);
        if (!NetworkManager.Singleton.IsConnectedClient)
        {
            throw new Exception("Connection verification failed");
        }
    }

    public async void JoinWithCode()
    {
        buttonAudioSource.Play();
        if (string.IsNullOrEmpty(joinCodeField.text))
        {
            InvalidCodePopUp.SetActive(true);
            return;
        }

        string code = joinCodeField.text.ToUpper();

        // Show loading popUp and hide join popUp
        if (loadingPopUp != null) loadingPopUp.SetActive(true);
        if (connectPanel != null) connectPanel.SetActive(false);

        try
        {
            bool success = await MyNetworkManager.Instance.JoinRelayWithRetry(code);
            if (success)
            {
                // Wait a bit for the connection to stabilise
                await Task.Delay(1000);
            }
            else
            {
                InvalidCodePopUp.SetActive(true);
            }
        }
        catch (Exception e)
        {
            HandleJoinError(e);
        }
        finally
        {
            if (loadingPopUp != null) loadingPopUp.SetActive(false);
        }
    }

    private void HandleJoinError(Exception e)
    {        
        string errorMsg = e switch
        {
            RelayServiceException relayEx => MyNetworkManager.Instance.GetRelayErrorMessage(relayEx),
            TimeoutException => "Connection timed out - try again",
            _ => e.Message.Contains("NotFound") ? "Game session not found" : "Failed to join game"
        };
        
        // Force full reset
        MyNetworkManager.Instance?.ResetConnectionState();
        NetworkManager.Singleton?.Shutdown();
    }

    private async Task<bool> WaitForSceneChange(string sceneName, float timeout)
    {
        float startTime = Time.time;
        while (Time.time - startTime < timeout)
        {
            if (SceneManager.GetActiveScene().name == sceneName)
            {
                return true;
            }
            await Task.Delay(500);
        }
        return false;
    }

    private async Task<bool> WaitForConditionAsync(Func<bool> condition, float timeout = 10f)
    {
        float elapsed = 0;
        while (!condition() && elapsed < timeout)
        {
            await Task.Delay(500);
            elapsed += 0.5f;
        }
        return condition();
    }

    private async Task<bool> JoinRelayWithRetryAsync(string joinCode, int maxAttempts)
    {
        for (int i = 0; i < maxAttempts; i++)
        {
            try
            {
                if (NetworkManager.Singleton.IsListening)
                {
                    NetworkManager.Singleton.Shutdown();
                    await Task.Delay(1000);
                }

                await MyNetworkManager.Instance.JoinRelay(joinCode);
                
                if (await WaitForConnectionAsync(10f))
                {
                    return true;
                }
            }
            catch
            {
                if (i < maxAttempts - 1)
                {
                    await Task.Delay(1000 * (i + 1));
                }
            }
        }
        return false;
    }

    private async Task<bool> WaitForConnectionAsync(float timeout)
    {
        float startTime = Time.time;
        var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();

        while (Time.time - startTime < timeout)
        {
            if (NetworkManager.Singleton.IsConnectedClient)
            {
                try 
                {
                    if (transport.GetCurrentRtt(NetworkManager.Singleton.LocalClientId) > 0)
                    {
                        return true;
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"Connection check failed: {e.Message}");
                }
            }
            await Task.Delay(500);
        }
        return false;
    }

    private bool IsValidJoinCodeFormat(string code)
    {
        return !string.IsNullOrEmpty(code) && 
            code.Length == MIN_JOIN_CODE_LENGTH && 
            code.All(c => char.IsLetterOrDigit(c));
    }

    private void ShowConnectPanel()
    {
        buttonAudioSource.Play();
        if (!PlayerSelector.Instance.playerConfirmed)
        {
            return;
        }
        transitionAudioSource.Play();
        connectPanel.SetActive(true);
        joinCodeField.text = "";
    }

    private void ShowLoading(bool show)
    {
        if (loadingPopUp != null) 
        {
            loadingPopUp.SetActive(show);
        }
    }

    // Simple error display: logs to console, extend for UI popUp if needed
    private void ShowError(string message)
    {
        Debug.LogError($"[LobbyManager] {message}");
        // TODO: Replace with popUp or UI error feedback as needed
    }

    private IEnumerator HideErrorAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        ShowLoading(false);
        // Prevent client from loading HostMatch scene directly
        if (scene.name == "HostMatch" && !NetworkManager.Singleton.IsServer)
        {
            Debug.LogWarning("[LobbyManager] Client attempted to load HostMatch scene directly. This is not allowed. Waiting for host to sync scene.");
            // Optionally, force client back to Lobby or show a waiting screen
            // SceneManager.LoadScene("Lobby");
        }
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    private void ClosePopUp()
    {
        buttonAudioSource.Play();
        if (connectPanel) 
        {
            connectPanel.SetActive(false);
        }
    }

    public void OnPlayerConfirmed(int selectedIndex)
    {

        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsClient)
        {
            var playerObj = NetworkManager.Singleton.LocalClient.PlayerObject;
            if (playerObj != null)
            {
                var playerController = playerObj.GetComponent<NetworkPlayerController>();
                if (playerController != null)
                {
                    playerController.SubmitPlayerSelectionServerRpc(selectedIndex);
                }
            }
        }
    }
}

public class UnityMainThreadDispatcher : MonoBehaviour
{
    private static UnityMainThreadDispatcher instance;
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> actions = 
        new System.Collections.Concurrent.ConcurrentQueue<Action>();

    public static UnityMainThreadDispatcher Instance()
    {
        if (instance == null)
        {
            instance = FindAnyObjectByType<UnityMainThreadDispatcher>();
            if (instance == null)
            {
                var go = new GameObject("MainThreadDispatcher");
                instance = go.AddComponent<UnityMainThreadDispatcher>();
                DontDestroyOnLoad(go);
            }
        }
        return instance;
    }

    public void Enqueue(Action action)
    {
        actions.Enqueue(action);
    }

    private void Update()
    {
        while (actions.TryDequeue(out var action))
        {
            action?.Invoke();
        }
    }
}