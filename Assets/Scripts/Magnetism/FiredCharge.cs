using UnityEngine;

public class FiredCharge : MonoBehaviour
{
    public GameObject positiveProjectilePrefab;
    public GameObject negativeProjectilePrefab;

    public GameObject strongPositiveProjectilePrefab;
    public GameObject strongNegativeProjectilePrefab;

    [SerializeField] private Transform spawnPosition;
    [SerializeField] private GameObject targetObject;

    [SerializeField] private Material positiveMaterial;
    [SerializeField] private Material negativeMaterial;

    public float spawnOffset = 1.5f;
    public float projectileSpeed = 10f;
    public bool projectileMotion = false;

    public float destroyAfter = 5f;
    public float cooldownTime = 1f;

    //Variables for Strong Projectile:
    private bool isCharging = false;

    public float chargeTimeRequired = 2f;
    public float currentChargeTime = 0f;


    private PlayerCharacter playerCharacter;
    private float lastFireTime;


    /*
    void Start()
    {
        playerCharacter = FindObjectOfType<PlayerCharacter>();
    }

    void Update()
    {
        UpdateMaterial();

        //When the Key is presed Down:
        if (Input.GetKeyDown(KeyCode.Mouse1) && Time.time >= lastFireTime + cooldownTime)
        {
            isCharging = true;
            currentChargeTime = 0f;
        }

        //Counts up a timer when the key is pressed down:
        if (isCharging)
        {
            currentChargeTime += Time.deltaTime;
        }

        //When the key is released:
        if (Input.GetKeyUp(KeyCode.Mouse1))
        {
            SpawnProjectile();

            isCharging = false;
        }
    }


    //Spawn Orbs:
    void SpawnProjectile()
    {
        if (Time.time < lastFireTime + cooldownTime) return;

        if (playerCharacter == null) return;

        bool isStrongProjectile = currentChargeTime >= chargeTimeRequired;

        GameObject projectilePrefab = playerCharacter.positiveCharge
            ? (isStrongProjectile ? strongPositiveProjectilePrefab : positiveProjectilePrefab)
            : (isStrongProjectile ? strongNegativeProjectilePrefab : negativeProjectilePrefab);

        if (projectilePrefab == null)
        {
            Debug.LogError("no prefab available!!!");
            return;
        }


        Transform cameraTransform = Camera.main.transform; //gets the player camera transform (1)

        Vector3 spawnPos = cameraTransform.position + cameraTransform.forward * spawnOffset; //sets camera transform position to be the spawn position for the projectile (2)

        Quaternion spawnRot = Quaternion.LookRotation(cameraTransform.forward); //ensure the projectile when fired will shoot forward (3)


        GameObject projectile = Instantiate(projectilePrefab, spawnPos, spawnRot);

        //Apply Projectile Movement Logic is true:
        ChargeMotion projectileMovement = projectile.GetComponent<Cha>();
        if (projectileMovement != null)
        {
            projectileMovement.SetProjectileProperties(projectileSpeed, projectileMotion, projectileMotion ? 0.5f : 0f);
        }

        Destroy(projectile, destroyAfter);

        lastFireTime = Time.time;
        currentChargeTime = 0f;
        isCharging = false;
    }

    //Color Changer:
    private void UpdateMaterial()
    {
        if (targetObject != null && positiveMaterial != null && negativeMaterial != null)
        {
            Renderer renderer = targetObject.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.material = playerCharacter.positiveCharge ? positiveMaterial : negativeMaterial;
            }
        }
    }

    */
}
