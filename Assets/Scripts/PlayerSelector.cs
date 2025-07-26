using UnityEngine;
using UnityEngine.UI;
using TMPro;
using UnityEngine.Audio;

public class PlayerSelector : MonoBehaviour
{
    public static PlayerSelector Instance { get; private set; }

    public Image selectedPlayerImage;
    public TMP_Text playerNameText;
    public TMP_Text paceText;
    public TMP_Text shootingText;
    public TMP_Text dribblingText;
    public TMP_Text confirmedPlayerText;
    public Button confirmButton;
    public Button okButton;
    public Button leftArrowButton;
    public Button rightArrowButton;
    public GameObject confirmedPanel;
    public GameObject invalidPopUp;
    public Sprite[] playerSprites;
    public string[] playerNames;
    public int[] playerPace;
    public int[] playerShooting;
    public int[] playerDribbling;

    public int selectedPlayerIndex { get; private set; } = 0;
    public bool playerConfirmed { get; private set; } = false;

    public System.Action<int> OnPlayerConfirmed;
    public AudioSource buttonAudioSource;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void Start()
    {        
        SetupButtonListeners();
        UpdatePlayerSelection();
    }

    private void SetupButtonListeners()
    {
        confirmButton.onClick.AddListener(ConfirmButtonClicked);
        leftArrowButton.onClick.AddListener(SelectPreviousPlayer);
        rightArrowButton.onClick.AddListener(SelectNextPlayer);
        okButton.onClick.AddListener(ClosePopUp);
    }

    public void ConfirmPlayerSelection()
    {
        if (selectedPlayerIndex < 0 || selectedPlayerIndex >= playerSprites.Length)
        {
            invalidPopUp.SetActive(true);
            return;
        }

        PlayerPrefs.SetInt("ConfirmedPlayerIndex", selectedPlayerIndex);
        PlayerPrefs.Save();

        playerConfirmed = true;

        // Notify other systems (when they start)
        OnPlayerConfirmed?.Invoke(selectedPlayerIndex);

        if (confirmedPanel != null)
        {
            confirmedPanel.SetActive(true);
            LobbyManager.Instance.UpdateButtons();
        }
    }

    private void UpdatePlayerSelection()
    {
        if (selectedPlayerIndex < 0 || selectedPlayerIndex >= playerSprites.Length)
        {
            selectedPlayerIndex = 0; //defaults to 0 if invalid
        }

        selectedPlayerImage.sprite = playerSprites[selectedPlayerIndex];
        playerNameText.text = playerNames[selectedPlayerIndex];
        paceText.text = playerPace[selectedPlayerIndex].ToString();
        shootingText.text = playerShooting[selectedPlayerIndex].ToString();
        dribblingText.text = playerDribbling[selectedPlayerIndex].ToString();
        confirmedPlayerText.text = playerNames[selectedPlayerIndex];

        paceText.color = GetStatColor(playerPace[selectedPlayerIndex]);
        shootingText.color = GetStatColor(playerShooting[selectedPlayerIndex]);
        dribblingText.color = GetStatColor(playerDribbling[selectedPlayerIndex]);
    }

    private void SelectNextPlayer()
    {
        if (!playerConfirmed)
        {
            if (LobbyManager.Instance != null && LobbyManager.Instance.selectorAudioSource != null)
            {
                LobbyManager.Instance.selectorAudioSource.Play();
            }

            selectedPlayerIndex = (selectedPlayerIndex + 1) % playerSprites.Length;
            UpdatePlayerSelection();
        }
    }

    private void SelectPreviousPlayer()
    {
        if (!playerConfirmed)
        {
            if (LobbyManager.Instance != null && LobbyManager.Instance.selectorAudioSource != null)
            {
                LobbyManager.Instance.selectorAudioSource.Play();
            }
            
            selectedPlayerIndex = (selectedPlayerIndex - 1 + playerSprites.Length) % playerSprites.Length;
            UpdatePlayerSelection();
        }
    }

    private void ClosePopUp()
    {
        buttonAudioSource.Play();
        if (confirmedPanel != null) 
        {
            confirmedPanel.SetActive(false);
        }
    }

    private void ConfirmButtonClicked()
    {
        buttonAudioSource.Play();
        ConfirmPlayerSelection();
    }

    private Color GetStatColor(int statValue)
    {
        if (statValue <= 85) 
        {
            return new Color(0.85f, 0.88f, 0.25f);
        }

        else if (statValue <= 95) 
        {
            return new Color(0.47f, 0.87f, 0.36f);
        }
        
        else 
        {
            return new Color(0.0588f, 0.5569f, 0.0f);
        }
    }

    public void ResetSelector()
    {
        // Reset selection state
        selectedPlayerIndex = 0; 
        playerConfirmed = false;

        UpdatePlayerSelection(); // Reset UI
        if (confirmedPanel != null)
        {
            confirmedPanel.SetActive(false);
        }
    }
}