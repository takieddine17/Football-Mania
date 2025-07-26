using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public class MainMenuManager : MonoBehaviour
{
    [SerializeField] private Button playGameButton;
    [SerializeField] private Button quitButton;
    [SerializeField] private AudioSource buttonAudioSource;
    [SerializeField] private AudioSource menuAudioSource;

    private void Start()
    {
        menuAudioSource.Play();
        playGameButton.onClick.AddListener(OnPlayGameClicked);
        quitButton.onClick.AddListener(OnQuitClicked);
    }

    private void OnPlayGameClicked()
    {
        PlayButtonSound();
        SceneManager.LoadScene("Lobby");
        menuAudioSource.Stop();
    }

    private void OnQuitClicked()
    {
        PlayButtonSound();
        Application.Quit();
    }

    private void PlayButtonSound()
    {
        if (buttonAudioSource != null)
        {
            buttonAudioSource.Play();
        }
    }
} 