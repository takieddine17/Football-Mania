using UnityEngine;

public class BallPhysics : MonoBehaviour
{
    private Rigidbody2D rb;

    void Start()
    {
        rb = GetComponent<Rigidbody2D>();
    }

    void Update()
    {
        // Rotate ball based on movement speed
        float rotationSpeed = rb.linearVelocity.magnitude * 10f;
        transform.Rotate(0, 0, -rotationSpeed * Time.deltaTime);
    }

    void OnCollisionEnter2D(Collision2D collision)
    {
        if (collision.gameObject.CompareTag("Player"))
        {
            // Calculate direction from player to ball
            Vector2 kickDirection = (transform.position - collision.transform.position).normalized;
            float kickForce = 10f;  // Adjust force for stronger/weaker kicks
            rb.AddForce(kickDirection * kickForce, ForceMode2D.Impulse);
        }
    }
}
