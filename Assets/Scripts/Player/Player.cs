using KinematicCharacterController;
using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Netcode;

public struct PlayerInputState
{
    public Quaternion Rotation;
    public Vector2 Move;
    public bool Jump;
    public bool JumpSustain;
    public CrouchInput Crouch;
}

public class Player : NetworkBehaviour
{
    [SerializeField] private PlayerCharacter playerCharacter;
    [SerializeField] private PlayerCamera playerCamera;
    [Space]
    [SerializeField] private CameraSpring cameraSpring;

    [SerializeField] private CameraLean cameraLean;

    private PlayerInputActions _inputActions;
    private PlayerInputState _pendingInput;

    //Network Version Of Start()
    public override void OnNetworkSpawn()
    {
        playerCharacter.Initialize();
        KCCSimulationDriver.RegisterPlayer(this);

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
        KCCSimulationDriver.UnregisterPlayer(this);

        if (!IsOwner) return;

        _inputActions.Dispose();
        Cursor.lockState = CursorLockMode.None;
    }

    void Update()
    {
        if (!IsOwner) return;

        var deltaTime = Time.deltaTime;
        var input = _inputActions.Gameplay;

        //Assign Action Values To Camera Input Struct Variables:
        var cameraInput = new CameraInput
        {
            Look = input.Look.ReadValue<Vector2>()

        };
        playerCamera.UpdateRotation(cameraInput);

        //Store Input System Values In State Struct This Is Not Connected To The Character:
        _pendingInput.Rotation = playerCamera.transform.rotation;
        _pendingInput.Move = input.Move.ReadValue<Vector2>();
        _pendingInput.JumpSustain = input.Jump.IsPressed();
        _pendingInput.Jump = _pendingInput.Jump | input.Jump.WasPressedThisFrame();
        if (input.Crouch.WasPressedThisFrame()) _pendingInput.Crouch = CrouchInput.Toggle;
       
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

    public void ApplySimulationInput()
    {
        if (!IsOwner) return;

        //Called Within SimulationDriver -> Apply Input To All Existing Characters Simultaneously:
        var input = new PlayerInput
        {
            Rotation = _pendingInput.Rotation,
            Move =  _pendingInput.Move,
            Jump = _pendingInput.Jump,
            JumpSustain = _pendingInput.JumpSustain,
            Crouch = _pendingInput.Crouch
        };
        playerCharacter.UpdateInputs(input);

        // One-shot inputs must be consumed after being applied.
        _pendingInput.Jump = false;
        _pendingInput.Crouch = CrouchInput.None;
    }

    public void Teleport(Vector3 position)
    {
        if (!IsOwner) return;

        playerCharacter.SetPosition(position);
    }

}
