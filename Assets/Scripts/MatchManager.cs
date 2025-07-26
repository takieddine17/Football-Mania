using UnityEngine;
using Unity.Netcode;
using System.Collections;
using TMPro;
using UnityEngine.UI;
using System.Linq;
using UnityEngine.Audio;

public class MatchManager : NetworkBehaviour
{
    public NetworkVariable<int> ClientPing = new NetworkVariable<int>();
    private Coroutine pingCoroutine;


    public GameObject goalPopUp;
    public Transform GoalFire_L1;
    public Transform GoalFire_R1;
    public Transform GoalFire_L2;
    public Transform GoalFire_R2;
    public Button okButton;
    public GameObject disconnectedPopUp;
    public GameObject confettiPrefab;
    public Transform confettiSpawnLeft;
    public Transform confettiSpawnRight;
    private bool timerPaused = false;
    public GameObject winnerPopUp;
    public Image TrophyImage_L;
    public Image TrophyImage_R;
    public TMPro.TMP_Text winnerNameText;
    public GameObject firePrefab;
    public GameObject[] playerPrefabs; 
    public Transform[] player1Spawns;
    public Transform[] player2Spawns;
    public PlayerStats[] allPlayerStats;
    public NetworkVariable<int> player1Score = new NetworkVariable<int>(0);
    public NetworkVariable<int> player2Score = new NetworkVariable<int>(0);
    [SerializeField] private GameObject ballPrefab;
    public GameObject BallPrefab => ballPrefab;
    public Transform ballSpawnPoint; 
    public NetworkVariable<float> matchTimer = new NetworkVariable<float>(300f); 
    public NetworkVariable<int> countdown = new NetworkVariable<int>(0); 
    public GameObject countdownPopUp; 
    [SerializeField] private TMP_Text countdownText;
    [SerializeField] private TMP_Text matchTimerText;
    [SerializeField] private TMP_Text player1NameText;
    [SerializeField] private TMP_Text player2NameText;
    [SerializeField] private TMP_Text player1ScoreText;
    [SerializeField] private TMP_Text player2ScoreText;
    public NetworkVariable<float> goalResetDelay = new NetworkVariable<float>(5f);
    private NetworkVariable<int> localPlayerIndex = new NetworkVariable<int>();
    private NetworkVariable<int> opponentPlayerIndex = new NetworkVariable<int>();
    private GameObject ball;
    public AudioSource buttonAudioSource;
    public AudioSource transitionAudioSource;
    public AudioSource refereeAudioSource;
    public AudioSource crowdAudioSource; 
    public AudioSource henryCelebration;
    public AudioSource messiCelebration;
    public AudioSource rooneyCelebration;
    public AudioSource rooneyCrowdAudioSource;
    public AudioSource agueroCelebration;
    public AudioSource salahCelebration;
    public AudioSource ronaldoCelebration;
    public AudioSource hazardCelebration;
    public AudioSource goalCelebration; 
    public AudioSource crowdCheerAudioSource; 
    public AudioSource winnersAudioSource;
    public float crowdNormalVolume = 1.0f;
    public float crowdDuckVolume = 0.3f; 

    public static MatchManager Instance;

    void Start()
    {
        okButton.onClick.AddListener(OnWinnerOkButtonClicked);

        if (matchTimerText != null)
        {
            matchTimerText.gameObject.SetActive(true);
            matchTimerText.text = "05:00";
        }

        matchTimer.Value = 300f; // Set timer value to 5 minutes (300 seconds) at scene load

        // Make crowd silent before countdown
        if (crowdAudioSource != null)
        {
            crowdAudioSource.volume = 0f;
            if (crowdAudioSource.isPlaying) 
            {
                crowdAudioSource.Stop();
            }
        }

        if (NetworkManager.Singleton == null)
        {
            return;
        }
        
        if (PlayerSelector.Instance == null)
        {
            return;
        }
        
        if (IsServer)
        {
            SpawnPlayers();
            PlayerMovement.SetAllPlayersLocked(true); // Locks all player movement after spawning
            countdown.Value = 3; // Start countdown
            StartCoroutine(CountdownCoroutine());
        }
        
        InitialisePlayerUI();
    }
    
    private void Awake()
    {
        transitionAudioSource.Play();
        if (Instance == null) 
        {
            Instance = this;
        }
    }
    
    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            pingCoroutine = StartCoroutine(UpdateClientPingCoroutine());
        }

        if (IsServer)
        {
            pingCoroutine = StartCoroutine(UpdateClientPingCoroutine());
        }

        player1Score.OnValueChanged += (prev, curr) => UpdateScoreUI(); // Subscribe to score changes to update UI for all clients
        player2Score.OnValueChanged += (prev, curr) => UpdateScoreUI();
        UpdateScoreUI(); // Ensure UI is correct on spawn
    
        
        // Sync player selections
        localPlayerIndex.OnValueChanged += OnLocalPlayerChanged;
        opponentPlayerIndex.OnValueChanged += OnOpponentChanged;
        countdown.OnValueChanged += UpdateCountdownUI;
        matchTimer.OnValueChanged += UpdateMatchTimerUI;

        // Force initial UI update for countdown and timer
        UpdateCountdownUI(0, countdown.Value);
        UpdateMatchTimerUI(0, matchTimer.Value);
        
        if (IsClient)
        {
            RequestPlayerDataServerRpc(NetworkManager.Singleton.LocalClientId); // Request player data sync when client connects
        }
    }

    private void VerifyPlayerObject()
    {
        NetworkObject playerObj = NetworkManager.Singleton.LocalClient.PlayerObject;
    }
    
    private void SpawnPlayers()
    {
        foreach (var client in NetworkManager.Singleton.ConnectedClients)
        {
            int selectedIndex = MyNetworkManager.Instance.GetPlayerSelection(client.Key);
            
            if (selectedIndex >= 0 && selectedIndex < MyNetworkManager.Instance.playerPrefabs.Length)
            {
                Vector3 spawnPos = client.Key == NetworkManager.ServerClientId ? player1Spawns[0].position : player2Spawns[0].position;
                GameObject prefab = MyNetworkManager.Instance.playerPrefabs[selectedIndex];
                
                var playerObj = Instantiate(prefab, spawnPos, Quaternion.identity);
                var netObj = playerObj.GetComponent<NetworkObject>();
                netObj.SpawnAsPlayerObject(client.Key);
                // Assign speed based on PlayerStats ScriptableObject
                PlayerStats stats = null;
                // Assume you have a PlayerStats[] allPlayerStats assigned in the inspector, matching selection indices
                if (allPlayerStats != null && selectedIndex >= 0 && selectedIndex < allPlayerStats.Length)
                {
                    stats = allPlayerStats[selectedIndex];
                }
                if (stats != null)
                {
                    var movement = playerObj.GetComponent<PlayerMovement>();
                    if (movement != null)
                    {
                        movement.SetSpeedFromPace(stats.pace);
                    }
                }
            }
        }
    }

    private void OnEnable()
    {
        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnected;
        }
    }

    private void OnDisable()
    {
        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnected;
        }
    }

    private void OnClientDisconnected(ulong clientId)
    {
        if (disconnectedPopUp != null)
        {
            disconnectedPopUp.SetActive(true);
        }

        StartCoroutine(HandleDisconnectAndReturnToLobbyCoroutine());
    }

    private void CreateWall(Transform parent, string name, Vector2 size, Vector2 position)
    {
        GameObject wall = new GameObject(name);
        wall.transform.SetParent(parent);
        BoxCollider2D collider = wall.AddComponent<BoxCollider2D>();
        collider.size = size;
        collider.transform.localPosition = position;
    }

    private void CreateGoal(Transform parent, string name, bool isPlayer1Goal, Vector2 size, Vector2 position)
    {
        GameObject goal = new GameObject(name);
        goal.transform.SetParent(parent);
        goal.tag = "Goal";
        
        BoxCollider2D collider = goal.AddComponent<BoxCollider2D>();
        collider.isTrigger = true;
        collider.size = size;
        collider.transform.localPosition = position;
        
        goal.AddComponent<GoalDetector>().isPlayer1Goal = isPlayer1Goal;
    }

    private void SpawnBall()
    {
        if (!IsServer || ballPrefab == null) 
        {
            return;
        }

        // Despawn existing ball if any
        if (ball != null && ball.GetComponent<NetworkObject>().IsSpawned)
        {
            ball.GetComponent<NetworkObject>().Despawn(true); // true = destroy object
        }

        Vector3 spawnPos = ballSpawnPoint != null ? ballSpawnPoint.position : Vector3.zero;
        ball = Instantiate(ballPrefab, spawnPos, Quaternion.identity);
        ball.GetComponent<NetworkObject>().Spawn();
        
        // Optional physics setup
        var rb = ball.GetComponent<Rigidbody2D>();
        if (rb != null)
        {
            rb.gravityScale = 0;
            rb.linearDamping = 0.5f; 
        }
    }

    private void OnLocalPlayerChanged(int previous, int current)
    {
        Debug.Log($"Local player changed to index {current}");
    }

    private void OnOpponentChanged(int previous, int current)
    {
        Debug.Log($"Opponent changed to index {current}");
    }

    public void OnPlayerDisconnected(ulong clientId)
    {
        if (IsServer)
        {
            // Handle player disconnect
            if (clientId == NetworkManager.Singleton.LocalClientId)
            {
                localPlayerIndex.Value = -1;
            }
            else
            {
                opponentPlayerIndex.Value = -1;
            }
        }
    }

    private void VerifyPlayers()
    {
        if (!IsSpawned) return;
        
        foreach (var client in NetworkManager.Singleton.ConnectedClients)
        {
            ulong clientId = client.Key;
            int selection = -1;
            
            if (HostGameManager.Instance != null && !HostGameManager.Instance._isBeingDestroyed)
            {
                selection = HostGameManager.Instance.GetPlayerSelection(clientId);
            }
        }
    }

    public void Player1Scores()
    {
        if (!IsServer) 
        {
            return;
        }

        player1Score.Value++;
        StartCoroutine(GoalCelebration(true)); // Player 1 scored
        StartCoroutine(ResetAfterGoal());
    }

    public void Player2Scores()
    {
        if (!IsServer) 
        {
            return;
        }
        
        player2Score.Value++;
        StartCoroutine(GoalCelebration(false)); // Player 2 scored
        StartCoroutine(ResetAfterGoal());
    }

    private IEnumerator ResetAfterGoal()
    {
        // Freeze ball and players
        if (ball != null) 
        {
            var ballRb = ball.GetComponent<Rigidbody2D>();
            if (ballRb != null) 
            {
                ballRb.simulated = false;
            }
        }
        SetPlayersMovement(false);
        
        yield return new WaitForSeconds(goalResetDelay.Value);
        
        // Reset positions
        SpawnBall(); // Despawns old ball and spawns new one at center
        ResetPlayers();
    }

    private void SetPlayersMovement(bool canMove)
    {
        foreach (var player in FindObjectsOfType<PlayerMovement>())
        {
            player.SetControlsEnabled(canMove);
        }
    }

    private void ResetPlayers()
    {
        // Find all PlayerMovement scripts in the scene
        var players = FindObjectsOfType<PlayerMovement>();
        // Gather all player NetworkObjects and sort by OwnerClientId
        var sortedPlayers = players.OrderBy(p => p.GetComponent<NetworkObject>().OwnerClientId).ToArray();
        // Assign spawns in sorted order
        for (int i = 0; i < sortedPlayers.Length; i++)
        {
            var player = sortedPlayers[i];
            if (i == 0 && player1Spawns.Length > 0)
            {
                player.transform.position = player1Spawns[0].position;
            }
            else if (i == 1 && player2Spawns.Length > 0)
            {
                player.transform.position = player2Spawns[0].position;
            }
            // Reset velocity
            var rb = player.GetComponent<Rigidbody2D>();
            if (rb != null) 
            {
                rb.linearVelocity = Vector2.zero;
            }
        }
    }

    [ClientRpc]
    private void ShowGoalCelebrationClientRpc(bool player1Scored)
    {
        // Pause timer and lock movement for all clients
        timerPaused = true;
        SetPlayersMovement(false);
        if (ball != null)
        {
            var rb = ball.GetComponent<Rigidbody2D>();
            if (rb != null) 
            {
                rb.simulated = false;
            }
        }

        // Show the goal popUp
        if (goalPopUp != null)
        {
            goalPopUp.SetActive(true);
        }

        // Destroy previous fire effects
        if (GoalFire_L1 != null) 
        {
            foreach (Transform child in GoalFire_L1) Destroy(child.gameObject);
        }

        if (GoalFire_R1 != null) 
        {
            foreach (Transform child in GoalFire_R1) Destroy(child.gameObject);
        }
        
        if (GoalFire_L2 != null) 
        {
            foreach (Transform child in GoalFire_L2) Destroy(child.gameObject);
        }
        
        if (GoalFire_R2 != null) 
        {
            foreach (Transform child in GoalFire_R2) Destroy(child.gameObject);
        }

        // Instantiate fire and confetti at the correct anchors
        if (player1Scored)
        {
            if (firePrefab && GoalFire_L1) 
            {
                Instantiate(firePrefab, GoalFire_L1);
            }
            
            if (firePrefab && GoalFire_R1) 
            {
                Instantiate(firePrefab, GoalFire_R1);
            }
            
            if (confettiPrefab && confettiSpawnLeft) 
            {
                Instantiate(confettiPrefab, confettiSpawnLeft.position, Quaternion.identity);
            }
        }
        else
        {
            if (firePrefab && GoalFire_L2) 
            {
                Instantiate(firePrefab, GoalFire_L2);
            }
            
            if (firePrefab && GoalFire_R2) 
            {
                Instantiate(firePrefab, GoalFire_R2);
            }
            
            if (confettiPrefab && confettiSpawnRight) 
            {
                Instantiate(confettiPrefab, confettiSpawnRight.position, Quaternion.identity);
            }
        }

        if (crowdAudioSource != null)
            StartCoroutine(FadeCrowdVolume(crowdAudioSource, crowdAudioSource.volume, crowdDuckVolume, 0.5f));

        // Play cheer
        if (crowdCheerAudioSource != null && crowdCheerAudioSource.clip != null)
        {
            crowdCheerAudioSource.Stop();
            crowdCheerAudioSource.volume = 1f;
            crowdCheerAudioSource.Play();
        }

        // Play correct player celebration sound
        string scorerName = player1Scored ? player1NameText.text : player2NameText.text;
        string name = scorerName.ToLower().Trim();
        AudioSource chosenSource = null;
        switch (name)
        {
            case "henry":
                chosenSource = henryCelebration; 
                break;
            case "messi":
                chosenSource = messiCelebration; 
                break;
            case "rooney":
                chosenSource = rooneyCelebration; 
                chosenSource = rooneyCrowdAudioSource;
                break;
            case "aguero":
                chosenSource = agueroCelebration; 
                break;
            case "salah":
                chosenSource = salahCelebration; 
                break;
            case "hazard":
                chosenSource = hazardCelebration; 
                break;
            case "ronaldo":
                chosenSource = ronaldoCelebration; 
                break;
            default:
                chosenSource = goalCelebration; 
                break;
        }

        if (chosenSource != null && chosenSource.clip != null)
        {
            chosenSource.Stop();
            chosenSource.Play();
        }
    }

    [ClientRpc]
    private void HideGoalCelebrationClientRpc()
    {
        // Hide the goal popUp
        if (goalPopUp != null) 
        {
            goalPopUp.SetActive(false);
        }
        
        if (GoalFire_L1 != null) 
        {
            foreach (Transform child in GoalFire_L1) Destroy(child.gameObject);
        }
        
        if (GoalFire_R1 != null) 
        {
            foreach (Transform child in GoalFire_R1) Destroy(child.gameObject);
        }
        
        if (GoalFire_L2 != null) 
        {
            foreach (Transform child in GoalFire_L2) Destroy(child.gameObject);
        }
        
        if (GoalFire_R2 != null) 
        {
            foreach (Transform child in GoalFire_R2) Destroy(child.gameObject);
        }
        
        if (confettiPrefab != null) 
        {
            foreach (Transform child in confettiSpawnLeft) Destroy(child.gameObject);
        }
        
        if (confettiPrefab != null) 
        {
            foreach (Transform child in confettiSpawnRight) Destroy(child.gameObject);
        }

        timerPaused = false;
        SetPlayersMovement(true);
        if (ball != null)
        {
            var rb = ball.GetComponent<Rigidbody2D>();
            if (rb != null) 
            {
                rb.simulated = true;
            }
        }
        refereeAudioSource.Play();
    }

    public void StartMatch()
    {
        if (!IsServer) 
        {
            return;
        }
        
        MyNetworkManager.Instance.LockPlayerSelections();
    }

    private IEnumerator RunMatchTimer()
    {
        while (matchTimer.Value > 0)
        {
            // Only decrement timer if not paused
            if (!timerPaused)
            {
                matchTimer.Value -= 1f;
                if (IsHost && matchTimerText != null)
                {
                    UpdateMatchTimerUI(0, matchTimer.Value);
                }
            }
            yield return new WaitForSeconds(1f);
        }

        EndMatch();
    }

    private void UpdateCountdownUI(int previous, int current)
    {
        if (countdownPopUp != null)
        {
            countdownPopUp.SetActive(current > 0);
            if (current > 0)
            {
                countdownText.text = current.ToString();
                // Do nothing; crowd stays silent during countdown
            }
            else if (previous > 0 && current == 0)
            {
                // Countdown just finished!
                if (IsServer)
                {
                    SpawnBall();
                    PlayerMovement.SetAllPlayersLocked(false); // Unlock all players
                    StartCoroutine(RunMatchTimer());
                    refereeAudioSource.Play();
                }
                // Fade in and resume crowd noise
                if (crowdAudioSource != null) 
                {
                    StartCoroutine(FadeInCrowdNoise());
                }
            }
        }
    }

    private void UpdateMatchTimerUI(float previous, float current)
    {
        if (matchTimerText != null)
        {
            int totalSeconds = Mathf.CeilToInt(current);
            int minutes = totalSeconds / 60;
            int seconds = totalSeconds % 60;
            matchTimerText.text = $"{minutes:00}:{seconds:00}";
        }
    }

    // Helper to update both score UI fields
    private void UpdateScoreUI()
    {
        if (player1ScoreText != null) 
        {
            player1ScoreText.text = player1Score.Value.ToString();
        }
        
        if (player2ScoreText != null) 
        {
            player2ScoreText.text = player2Score.Value.ToString();
        }
    }

    private IEnumerator GoalCelebration(bool player1Scored)
    {
        ShowGoalCelebrationClientRpc(player1Scored);
        yield return new WaitForSeconds(5f);
        HideGoalCelebrationClientRpc();
    }

    private IEnumerator FadeCrowdVolume(AudioSource source, float from, float to, float duration, bool stopAfter = false)
    {
        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            source.volume = Mathf.Lerp(from, to, elapsed / duration);
            yield return null;
        }
        source.volume = to;
        if (stopAfter) 
        {
            source.Stop();
        }
    }

    private void EndMatch()
    {
        SetPlayersMovement(false);
        if (ball != null)
        {
            var rb = ball.GetComponent<Rigidbody2D>();
            if (rb != null) 
            {
                rb.simulated = false;
            }
        }

        // Determine winner
        string winner;
        if (player1Score.Value > player2Score.Value)
        {
            winner = player1NameText != null ? player1NameText.text : "Player 1";
        }
        else if (player2Score.Value > player1Score.Value)
        {
            winner = player2NameText != null ? player2NameText.text : "Player 2";
        }
        else
        {
            winner = "Draw!";
        }

        // Show popUp on all clients
        ShowWinnerPopUpClientRpc(winner);

        // Fade out crowd noise
        StartCoroutine(FadeOutCrowdNoise(2f));
    }

    private IEnumerator FadeOutCrowdNoise(float duration = 2f)
    {
        if (crowdAudioSource == null) yield break;

        float startVolume = crowdAudioSource.volume;
        float elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            crowdAudioSource.volume = Mathf.Lerp(startVolume, 0f, elapsed / duration);
            yield return null;
        }

        crowdAudioSource.volume = 0f;
        crowdAudioSource.Stop();
    }

    private IEnumerator FadeInCrowdNoise(float duration = 2f)
    {
        if (crowdAudioSource == null) 
        {
            yield break;
        }

        crowdAudioSource.volume = 0f;

        if (!crowdAudioSource.isPlaying)
        {
            crowdAudioSource.Play();
        }

        crowdAudioSource.loop = true;

        float elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            crowdAudioSource.volume = Mathf.Lerp(0f, crowdNormalVolume, elapsed / duration);
            yield return null;
        }

        crowdAudioSource.volume = crowdNormalVolume;
    }

    [ClientRpc]
    private void ShowWinnerPopUpClientRpc(string winner)
    {
        TrophyImage_L.gameObject.SetActive(false);
        TrophyImage_R.gameObject.SetActive(false);

        if (winner != "Draw!")
        {
            winnersAudioSource.Play();
            TrophyImage_L.gameObject.SetActive(true);
            TrophyImage_R.gameObject.SetActive(true);
        }

        if (winnerPopUp != null)
        {
            winnerPopUp.SetActive(true);
        }

        if (winnerNameText != null)
        {
            winnerNameText.text = winner;
        }
    }

    private void InitialisePlayerUI()
    {
        UpdateScoreUI();
        
        // Set names from connected clients
        var clients = NetworkManager.Singleton.ConnectedClients;
        if (clients.Count > 0)
        {
            // Host is always player 1
            player1NameText.text = PlayerSelector.Instance.playerNames[MyNetworkManager.Instance.GetPlayerSelection(NetworkManager.ServerClientId)];
            
            // Connected client is player 2
            if (clients.Count > 1)
            {
                var clientId = clients.Keys.First(id => id != NetworkManager.ServerClientId);
                player2NameText.text = PlayerSelector.Instance.playerNames[MyNetworkManager.Instance.GetPlayerSelection(clientId)];
            }
        }
    }

    [ServerRpc]
    private void RequestPlayerDataServerRpc(ulong clientId)
    {
        // Send player data to requesting client
        UpdatePlayerDataClientRpc(NetworkManager.ServerClientId, MyNetworkManager.Instance.GetPlayerSelection(NetworkManager.ServerClientId));
        
        foreach (var id in NetworkManager.Singleton.ConnectedClients.Keys)
        {
            if (id != NetworkManager.ServerClientId)
            {
                UpdatePlayerDataClientRpc(id, MyNetworkManager.Instance.GetPlayerSelection(id));
            }
        }
    }

    [ClientRpc]
    private void UpdatePlayerDataClientRpc(ulong clientId, int playerIndex)
    {
        if (clientId == NetworkManager.ServerClientId)
        {
            player1NameText.text = PlayerSelector.Instance.playerNames[playerIndex];
        }
        else
        {
            player2NameText.text = PlayerSelector.Instance.playerNames[playerIndex];
        }
    }

    [ServerRpc]
    public void SetPlayerNamesServerRpc(string player1Name, string player2Name)
    {
        SetPlayerNamesClientRpc(player1Name, player2Name);
    }

    [ClientRpc]
    private void SetPlayerNamesClientRpc(string player1Name, string player2Name)
    {
        player1NameText.text = player1Name;
        player2NameText.text = player2Name;
    }

    private IEnumerator UpdateClientPingCoroutine()
    {
        while (true)
        {
            if (NetworkManager.Singleton.ConnectedClients.Count > 1)
            {
                foreach (var client in NetworkManager.Singleton.ConnectedClients)
                {
                    if (client.Key != NetworkManager.Singleton.LocalClientId) 
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

    // Coroutine to decrement countdown every second
    private IEnumerator CountdownCoroutine()
    {
        while (countdown.Value > 0)
        {
            yield return new WaitForSeconds(1f);
            countdown.Value--;
        }

        if (IsServer)
        {
            SpawnBall();
            UnlockAllPlayersClientRpc(); // Unlock all players on all clients
            StartCoroutine(RunMatchTimer());
        }
    }

    [ClientRpc]
    private void UnlockAllPlayersClientRpc()
    {
        PlayerMovement.SetAllPlayersLocked(false);
    }

    public void OnWinnerOkButtonClicked()
    {
        buttonAudioSource.Play();
        winnersAudioSource.Stop();
        HandleDisconnectAndReturnToLobby(false);
    }

    public void HandleDisconnectAndReturnToLobby(bool skipSceneReload = false)
    {
        if (MyNetworkManager.Instance != null)
        {
            MyNetworkManager.Instance.RemovePlayerSelectionServerRpc(NetworkManager.Singleton.LocalClientId);
            if (NetworkManager.Singleton.IsServer && MyNetworkManager.Instance._playerSelections != null)
            {
                MyNetworkManager.Instance.ResetConnectionState(true);
            }
        }
    
        if (PlayerSelector.Instance != null)
            Destroy(PlayerSelector.Instance.gameObject);
        if (MyNetworkManager.Instance != null)
            Destroy(MyNetworkManager.Instance.gameObject);
        if (NetworkManager.Singleton != null)
            Destroy(NetworkManager.Singleton.gameObject);
    
        if (!skipSceneReload)
        {
            UnityEngine.SceneManagement.SceneManager.LoadScene("Lobby");
        }
    }
    
    private IEnumerator HandleDisconnectAndReturnToLobbyCoroutine(bool skipSceneReload = false, float popUpWaitSeconds = 2f)
    {
        if (disconnectedPopUp != null)
        {
            disconnectedPopUp.SetActive(true);
        }
    
        yield return new WaitForSeconds(popUpWaitSeconds);
    
        HandleDisconnectAndReturnToLobby(skipSceneReload);
    }

    private void OnApplicationQuit()
    {
        HandleDisconnectAndReturnToLobby(true);
    }
}