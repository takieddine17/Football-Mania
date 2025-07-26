using UnityEngine;
using Unity.Netcode;

[CreateAssetMenu(fileName = "PlayerAttributes", menuName = "Player Data/Attributes")]
public class PlayerStats : ScriptableObject
{
    public enum PlayerName { Aguero, Salah, Hazard, Henry, Kane, Messi, Ronaldo, Bale, Neymar, Rooney}

    public PlayerName playerName;
    public Sprite lobbySprite;
    public Sprite matchSprite;
    [Range(1, 99)] public int dribbling;
    [Range(1, 99)] public int shooting;
    [Range(1, 99)] public int pace;
    public GameObject prefab;
}