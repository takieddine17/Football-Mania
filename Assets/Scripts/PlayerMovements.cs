using UnityEngine;
using Unity.Netcode;
using Unity.Netcode.Components;

[RequireComponent(typeof(Rigidbody2D))]
[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(NetworkTransform))]

public class PlayerMovement : NetworkBehaviour
{
    public float speed = 5f;
    private NetworkVariable<float> netSpeed = new NetworkVariable<float>();

    public float spawnProtectionTime = 3f;
    [SerializeField] private PlayerType[] playerTypes; 
    [SerializeField] private GameObject pitchObject; 
    private int playerTypeIndex; 
    private Rigidbody2D rb;
    private Vector2 moveInput;
    private float spawnTime;
    private bool controlsEnabled = false; 

    private Collider2D _pitchCollider;
    private Bounds _pitchBounds;

    public void SetSpeedFromPace(int pace)
    {
        float newSpeed = 5f * (pace / 100f);
        if (IsServer)
        {
            netSpeed.Value = newSpeed;
        }
        speed = newSpeed;
    }
    private void OnEnable()
    {
        netSpeed.OnValueChanged += OnSpeedChanged;
    }
    private void OnDisable()
    {
        netSpeed.OnValueChanged -= OnSpeedChanged;
    }
    private void OnSpeedChanged(float oldValue, float newValue)
    {
        speed = newValue;
    }

    private void Start()
    {
        rb = GetComponent<Rigidbody2D>();
        spawnTime = Time.time;
        
        // Auto-find pitch if not assigned
        if (pitchObject == null)
        {
            GameObject pitch = GameObject.FindWithTag("Pitch");
            if (pitch != null)
            {
                pitchObject = pitch;
            }
        }
        
        if (pitchObject != null)
        {
            _pitchCollider = pitchObject.GetComponent<Collider2D>();
            if (_pitchCollider != null)
            {
                _pitchBounds = _pitchCollider.bounds;
            }
        }
    }

    public void SetControlsEnabled(bool enabled)
    {
        controlsEnabled = enabled;
        // Immediately stop movement when disabled
        if (!enabled && rb != null)
        {
            rb.linearVelocity = Vector2.zero;
        }
    }

    // Static helper to set movement lock for all players
    public static void SetAllPlayersLocked(bool locked)
    {
        foreach (var player in FindObjectsOfType<PlayerMovement>())
        {
            player.SetControlsEnabled(!locked);
        }
    }

    private void Update()
    {
        if (!controlsEnabled) return;
        if (!IsOwner) return;

        moveInput.x = Input.GetAxis("Horizontal");
        moveInput.y = Input.GetAxis("Vertical");

        MoveServerRpc(moveInput);
    }

    private void MoveLocally(Vector2 input)
    {
        if (!controlsEnabled) return;
        rb.linearVelocity = input * speed;
    }

    [ServerRpc(RequireOwnership = true)]
    private void MoveServerRpc(Vector2 input)
    {
        // Server relays input to all clients (including owner)
        MoveClientRpc(input);
    }

    [ClientRpc]
    private void MoveClientRpc(Vector2 input)
    {
        // All clients, including owner, set velocity from server
        if (!controlsEnabled) return;
        rb.linearVelocity = input * speed;
    }

    public bool HasSpawnProtection()
    {
        return Time.time < spawnTime + spawnProtectionTime;
    }

    public void Initialise(bool isHost, int typeIndex)
    {
        playerTypeIndex = typeIndex;
        
        // Apply selected player attributes
        if (playerTypes.Length > typeIndex)
        {
            if (TryGetComponent<SpriteRenderer>(out var renderer))
            {
                renderer.color = playerTypes[typeIndex].playerColor;
            }
            speed = playerTypes[typeIndex].moveSpeed;
        }
    }

    [System.Serializable]
    public class PlayerType
    {
        public Color playerColor;
        public float moveSpeed;
    }
}