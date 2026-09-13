using UnityEngine;

public class MagneticZone : MonoBehaviour
{
    [Header("Magnetic Properties")]
    public float magneticStrength = 10f;
    public bool isPositive = true;
    public float maxForce = 50f;
    public float interactionRadius = 5f;  //for sphere radius

    [Header("Collider Selection")]
    public Collider chosenCollider;

    private void Start()
    {
        if (chosenCollider == null)
        {
            Debug.LogError("no collider available!!");
            return;
        }

        if (!chosenCollider.isTrigger)
        {
            Debug.LogWarning("set trigger to true!");
            chosenCollider.isTrigger = true;
        }

        if (chosenCollider is SphereCollider sphereCol) //if sphere collider -> set to given radius
        {
            sphereCol.radius = interactionRadius;
        }
    }

    private void OnTriggerStay(Collider other)
    {
        if (other.CompareTag("Neutral")) return; //for the case neutral object dont get effected by magnetism.

        MagneticZone otherMagnet = other.GetComponent<MagneticZone>();
        if (otherMagnet == null || otherMagnet == this) return;

        Rigidbody otherRb = other.GetComponent<Rigidbody>();
        if (otherRb == null) return;

        //calculating distance:
        Vector3 r = other.transform.position - transform.position;
        float distance = r.magnitude;
        if (distance < 0.1f) return; //cannot divide by zero or error


        float forceMultiplier = (isPositive != otherMagnet.isPositive) ? -1f : 1f; //force application

        //inverse-square law: force ∝ 1 / distance²
        Vector3 force = forceMultiplier * (magneticStrength * otherMagnet.magneticStrength / (distance * distance)) * r.normalized;
        force = Vector3.ClampMagnitude(force, maxForce); // Prevent extreme values

        otherRb.AddForce(force, ForceMode.Force); //force application on other object
    }

    //Field Visualization:
    private void OnDrawGizmos()
    {
        if (chosenCollider is SphereCollider)
        {
            Gizmos.color = Color.green;
            Gizmos.DrawWireSphere(transform.position, interactionRadius);
        }
        else if (chosenCollider is BoxCollider boxCol)
        {
            Gizmos.color = Color.green;
            Gizmos.DrawWireCube(boxCol.bounds.center, boxCol.bounds.size);
        }
    }
}
