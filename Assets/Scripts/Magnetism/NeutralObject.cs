using System.Collections;
using UnityEngine;

public class NeutralObject : MonoBehaviour
{
    [Header("Material Settings")]
    public Material positiveMaterial;
    public Material negativeMaterial;
    public Material neutralMaterial;

    [Header("Effect Duration")]
    public float effectDuration = 5f;

    [Header("Collider Selection")]
    public Collider chosenCollider; //collider in ref to the magnetism field.

    private MagneticZone gravityZone;
    private Renderer objectRenderer;


    private void Start()
    {
        gravityZone = GetComponent<MagneticZone>();
        objectRenderer = GetComponent<Renderer>();
    }

    private void OnCollisionEnter(Collision collision)
    {
        ChargeMotion projectile = collision.gameObject.GetComponent<ChargeMotion>(); //object with projectile component

        if (projectile == null) return;

        if (CompareTag("Neutral"))
        {
            if (collision.gameObject.CompareTag("Positive")) //if neutral and collides with a positively tagged projectile.
            {
                StartCoroutine(ApplyEffect(true));
            }
            else if (collision.gameObject.CompareTag("Negative")) //if neutral and collides with a negatively tagged projectile.
            {
                StartCoroutine(ApplyEffect(false));
            }
        }
    }

    private IEnumerator ApplyEffect(bool isPositive)
    {
        if (gravityZone == null || objectRenderer == null) yield break;

        gravityZone.isPositive = isPositive; //isPositive = true or false depending on collision factor.

        chosenCollider.enabled = true;
        gravityZone.enabled = true;

        //changing material and tag depending on the collision factor.
        objectRenderer.material = isPositive ? positiveMaterial : negativeMaterial;
        tag = isPositive ? "Positive" : "Negative";

        yield return new WaitForSeconds(effectDuration);

        //resetting the object back to neutral.
        gravityZone.enabled = false;
        chosenCollider.enabled = false;
        objectRenderer.material = neutralMaterial;
        tag = "Neutral";
    }
}
