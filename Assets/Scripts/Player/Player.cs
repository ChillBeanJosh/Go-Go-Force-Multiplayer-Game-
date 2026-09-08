using KinematicCharacterController;
using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Netcode;

public class Player : NetworkBehaviour
{
    [SerializeField] private PlayerCharacter playerCharacter;
    [SerializeField] private PlayerCamera playerCamera;
    [Space]
    [SerializeField] private CameraSpring cameraSpring;

    [SerializeField] private CameraLean cameraLean;

    private PlayerInputActions _inputActions;

    //Network Version Of Start()
    public override void OnNetworkSpawn()
    {
        playerCharacter.Initialize();

        if (!IsOwner)
        {
            playerCamera.gameObject.SetActive(false);
            return;
        }

        Cursor.lockState = CursorLockMode.Locked;

        _inputActions = new PlayerInputActions();
        _inputActions.Enable();

        playerCamera.Initialize(playerCharacter.GetCameraTarget());
        cameraSpring.Initialize();
        cameraLean.Initialize();
    }

    //Network Version of OnDestroy()
    public override void OnNetworkDespawn()
    {
        if (!IsOwner) return;

        _inputActions.Dispose();
        Cursor.lockState = CursorLockMode.None;
    }

    void Update()
    {
        if (!IsOwner) return;

        var deltaTime = Time.deltaTime;
        //Stores The Gameplay Action Map From PlayerInputActions InputActions:
        var input = _inputActions.Gameplay;

        //Assign Action Values To Camera Input Struct Variables:
        var cameraInput = new CameraInput
        {
            Look = input.Look.ReadValue<Vector2>()

        };
        playerCamera.UpdateRotation(cameraInput);

        //Assign Action Values To Character Input Struct Variables:
        var playerInput = new PlayerInput
        {
            Rotation = playerCamera.transform.rotation,
            Move = input.Move.ReadValue<Vector2>(),
            Jump = input.Jump.WasPressedThisFrame(),
            JumpSustain = input.Jump.IsPressed(),
            Crouch = input.Crouch.WasPressedThisFrame() ? CrouchInput.Toggle : CrouchInput.None
        };
        playerCharacter.UpdateInputs(playerInput);
        playerCharacter.UpdateBody(deltaTime);

#if UNITY_EDITOR
        if (Keyboard.current.tKey.wasPressedThisFrame)
        {
            var ray = new Ray(playerCamera.transform.position, playerCamera.transform.forward);
            if (Physics.Raycast(ray, out var hit))
            {
                Teleport(hit.point);
            }
        }
#endif
    }

    private void LateUpdate()
    {
        if (!IsOwner) return;

        var deltaTime = Time.deltaTime;
        var cameraTarget = playerCharacter.GetCameraTarget();
        var status = playerCharacter.GetStatus();
        
        //Updates Camera Position To Ensure It Follows The Player Character's Camera Target:
        playerCamera.UpdatePosition(cameraTarget);
        cameraSpring.UpdateSpring(deltaTime, cameraTarget.up);
        cameraLean.UpdateLean(deltaTime, status.State is State.Slide ,status.Acceleration, cameraTarget.up);
    }

    public void Teleport(Vector3 position)
    {
        if (!IsOwner) return;

        playerCharacter.SetPosition(position);
    }

}
