using UnityEngine;

public class ChargeMotion : MonoBehaviour
{
    private Rigidbody rb;

    public float speed = 10f;

    public bool applyGravity = false;
    public float drag = 0f;

    [SerializeField] private LayerMask collisionLayer;

    void Start()
    {
        rb = GetComponent<Rigidbody>();
        if (rb == null)
        {
            Debug.LogError("no rb available!!!");
            return;
        }

        rb.linearVelocity = transform.forward * speed;
        rb.useGravity = applyGravity;
        rb.linearDamping = drag;
    }

    //Projectile Motion Drag Logic:
    public void SetProjectileProperties(float speed, bool applyGravity, float drag)
    {
        this.speed = speed;
        this.applyGravity = applyGravity;
        this.drag = drag;

        if (rb != null)
        {
            rb.linearVelocity = transform.forward * speed;
            rb.useGravity = applyGravity;
            rb.linearDamping = drag;
        }
    }

    private void OnCollisionEnter(Collision collision)
    {
        if (((1 << collision.gameObject.layer) & collisionLayer) != 0)
        {
            StopProjectileMovement();
        }
    }

    private void StopProjectileMovement()
    {
        if (rb != null)
        {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }
    }
}
