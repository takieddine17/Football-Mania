using UnityEngine;
using Unity.Netcode;

public class GoalDetector : NetworkBehaviour
{
    [SerializeField] public bool isPlayer1Goal;
    [SerializeField] public ParticleSystem goalParticles;
    
    private void OnTriggerEnter2D(Collider2D other)
    {
        if (!other.CompareTag("Ball")) return;
        
        Vector2 ballCentre = other.transform.position;
        float goalLineY = transform.position.y;
        float margin = 0.2f; 

        // For a top goal (player1Goal), ball centre must cross above the goal line (with margin)
        if (isPlayer1Goal && ballCentre.y > goalLineY - margin)
        {
            if (IsServer)
            {
                MatchManager.Instance?.Player2Scores();
                PlayGoalEffectsClientRpc();
            }
        }
        // For a bottom goal (player2Goal), ball centre must cross below the goal line (with margin)
        else if (!isPlayer1Goal && ballCentre.y < goalLineY + margin)
        {
            if (IsServer)
            {
                MatchManager.Instance?.Player1Scores();
                PlayGoalEffectsClientRpc();
            }
        }
    }

    [ClientRpc]
    private void PlayGoalEffectsClientRpc()
    {
        if (goalParticles != null) goalParticles.Play();
    }
}
