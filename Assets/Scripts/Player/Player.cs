using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Netcode;

public struct PlayerInputState : INetworkSerializable
{
    public Quaternion Rotation;
    public Vector2 Move;
    public bool Jump;
    public bool JumpSustain;
    public int CrouchToggles;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer)
        where T : IReaderWriter
    {
        serializer.SerializeValue(ref Rotation);
        serializer.SerializeValue(ref Move);
        serializer.SerializeValue(ref Jump);
        serializer.SerializeValue(ref JumpSustain);
        serializer.SerializeValue(ref CrouchToggles);
    }
}

public class Player : NetworkBehaviour
{
    [SerializeField] private PlayerCharacter playerCharacter;
    [Space]
    [SerializeField] private PlayerCamera playerCamera;
    [SerializeField] private CameraSpring cameraSpring;
    [SerializeField] private CameraLean cameraLean;
    [Space]


    //Input Values From InputSystem That Will Be Sent To Player Input Data:
    private PlayerInputActions _inputActions;
    [Space]


    //Raw Player Input Data:
    private PlayerInputState _pendingInput;
    private int _pendingCrouchToggles;
    [Space]


    //Copied Raw Input Date Onto Server:
    private PlayerInputState _serverInput;
    private int _serverCrouchToggles;
    [Space]


    //Position, Rotation, and Status Info To Be Send To The Server:
    private readonly NetworkVariable<Vector3> _networkPosition = new NetworkVariable<Vector3>();
    private readonly NetworkVariable<Quaternion> _networkRotation = new NetworkVariable<Quaternion>();
    private readonly NetworkVariable<CharacterStatus> _networkStatus = new NetworkVariable<CharacterStatus>();
    [Space]


    //Previous + Current Server Positions Used For Remote Player Interpolation:
    private Vector3 _previousNetworkPosition;
    private Vector3 _currentNetworkPosition;
    [Space]


    //Previous + Current Server Rotation Used For Remote Player Presentation:
    private Quaternion _previousNetworkRotation;
    private Quaternion _currentNetworkRotation;
    [Space]


    //Previous + Current Server Status Used For Remote Player Presentation:
    private CharacterStatus _previousNetworkStatus;
    private CharacterStatus _currentNetworkStatus;
    [Space]


    private float _networkStateTimer;
    private bool _hasNetworkState;

    public override void OnNetworkSpawn()
    {
        //Initialize KCC Character And Register Player For Simulation:
        playerCharacter.Initialize();
        KCCSimulationDriver.RegisterPlayer(this);

        //Ensure Player Camera Doesn't Swap To A Newly Joined Client:
        if (!IsOwner)
        {
            playerCamera.gameObject.SetActive(false);
            return;
        }

        //Hide The Owner's Player Mesh From Their Own Camera:
        playerCharacter.SetOwnerVisibility(false);

        Cursor.lockState = CursorLockMode.Locked;
        _inputActions = new PlayerInputActions();
        _inputActions.Enable();

        playerCamera.Initialize(playerCharacter.GetCameraTarget());
        cameraSpring.Initialize();
        cameraLean.Initialize();
    }

    public override void OnNetworkDespawn()
    {
        //Disconnect Client From KCC Simulation Tick:
        KCCSimulationDriver.UnregisterPlayer(this);

        if (!IsOwner) return;

        _inputActions.Dispose();
        Cursor.lockState = CursorLockMode.None;
    }

    private void Update()
    {
        var deltaTime = Time.deltaTime;

        //Read Input Values From Mapped Input Actions:
        if (IsOwner)
        {
            var input = _inputActions.Gameplay;

            var cameraInput = new CameraInput
            {
                Look = input.Look.ReadValue<Vector2>()
            };
            playerCamera.UpdateRotation(cameraInput);

            _pendingInput.Rotation = playerCamera.transform.rotation;
            _pendingInput.Move = input.Move.ReadValue<Vector2>();
            _pendingInput.JumpSustain = input.Jump.IsPressed();
            _pendingInput.Jump |= input.Jump.WasPressedThisFrame();

            if (input.Crouch.WasPressedThisFrame())
            {
                //Count crouch Input Presses For Spam Control:
                //Ex: 0 -> NO CROUCH, 1 -> CROUCH, 2 -> NO CROUCH, ...:
                _pendingCrouchToggles++;
            }
        }

        //Update Client's Mesh Render + Camera Positions:
        if (IsOwner)
        {
            playerCharacter.UpdateCameraTarget(deltaTime);
            playerCharacter.UpdateMesh(deltaTime);
        }
        else
        {
            playerCharacter.UpdateMesh(deltaTime);
            UpdateRemotePresentation(deltaTime);
        }
    }

    private void LateUpdate()
    {
        if (!IsOwner) return;

        var deltaTime = Time.deltaTime;
        var cameraTarget = playerCharacter.GetCameraTarget();
        var status = playerCharacter.GetStatus();
        
        //Updates Camera Position + Effects:
        playerCamera.UpdatePosition(playerCharacter.GetVisualCameraPosition());
        cameraSpring.UpdateSpring(deltaTime, cameraTarget.up);
        cameraLean.UpdateLean(deltaTime, status.State is State.Slide ,status.Acceleration, cameraTarget.up);
    }

    public void ApplySimulationInput()
    {
        //Get The Input For This Simulation Tick:
        PlayerInputState input = GetSimulationInput();

        //Server Applies Input Directly:
        if (IsServer)
        {
            ApplyInputToCharacter(input);

            //Reset Remote Client One-Shot Input:
            if (!IsOwner)
            {
                _serverInput.Jump = false;
                _serverCrouchToggles = 0;
            }
            //Reset Host One-Shot Input:
            else
            {
                _pendingInput.Jump = false;
                _pendingCrouchToggles = 0;
            }

            return;
        }

        //Remote Client Sends Its Input To The Server:
        if (IsOwner)
        {
            SubmitInputServerRpc(input);

            //Reset Local One-Shot Input:
            _pendingInput.Jump = false;
            _pendingCrouchToggles = 0;
        }
    }

    private PlayerInputState GetSimulationInput()
    {
        //Server Gets The Latest Input Received From A Remote Client:
        if (IsServer && !IsOwner)
        {
            PlayerInputState input = _serverInput;
            input.CrouchToggles = _serverCrouchToggles;

            return input;
        }

        //Owner Gets Its Locally Collected Input:
        PlayerInputState localInput = _pendingInput;
        localInput.CrouchToggles = _pendingCrouchToggles;

        return localInput;
    }

    private void ApplyInputToCharacter(PlayerInputState inputState)
    {
        //Applies PlayerInputState (pending/server) -> PlayerCharacter.cs PlayerInput (KCC Movement Logic):
        var input = new PlayerInput
        {
            Rotation = inputState.Rotation,
            Move = inputState.Move,
            Jump = inputState.Jump,
            JumpSustain = inputState.JumpSustain,
            Crouch = CrouchInput.None
        };
        playerCharacter.UpdateInputs(input);

        //Apply Each Queued Crouch Toggle To The Character:
        for (int i = 0; i < inputState.CrouchToggles; i++)
        {
            playerCharacter.UpdateInputs
            (
                new PlayerInput
                {
                    Rotation = inputState.Rotation,
                    Move = inputState.Move,
                    Jump = false,
                    JumpSustain = inputState.JumpSustain,
                    Crouch = CrouchInput.Toggle
                }
            );
        }
    }

    [ServerRpc(RequireOwnership = true)]
    private void SubmitInputServerRpc(PlayerInputState input)
    {
        //Store Client's PlayerInputState Values (pending) -> Server's PlayerInputState Values (server):
        _serverInput.Rotation = input.Rotation;
        _serverInput.Move = input.Move;
        _serverInput.Jump = _serverInput.Jump || input.Jump;
        _serverInput.JumpSustain = input.JumpSustain;

        //Accumulate Crouch Toggle Presses Until The Server Applies Them:
        _serverCrouchToggles += input.CrouchToggles;


        Debug.Log
        (
            $"Server received input from Client {OwnerClientId}: " +
            $"Move={input.Move}, " +
            $"Jump={input.Jump}, " +
            $"JumpSustain={input.JumpSustain}, " +
            $"CrouchToggles={input.CrouchToggles}"
        );
    }


    //Send The Server's Current Character State To All Clients:
    public void PublishServerState()
    {
        if (!IsServer) return;

        //Send Authoritative Position, Rotation, And Movement State:
        _networkPosition.Value = playerCharacter.GetPosition();
        _networkRotation.Value = playerCharacter.GetRotation();
        _networkStatus.Value = playerCharacter.GetStatus();
    }
    public void ApplyNetworkState()
    {
        if (IsServer) return;

        Vector3 newPosition = _networkPosition.Value;
        Quaternion newRotation = _networkRotation.Value;
        CharacterStatus newStatus = _networkStatus.Value;

        playerCharacter.SetNetworkState
        (
            newPosition,
            newRotation,
            newStatus
        );

        if (!_hasNetworkState)
        {
            _previousNetworkPosition = newPosition;
            _currentNetworkPosition = newPosition;

            _previousNetworkRotation = newRotation;
            _currentNetworkRotation = newRotation;

            _previousNetworkStatus = newStatus;
            _currentNetworkStatus = newStatus;

            _networkStateTimer = Time.fixedDeltaTime;
            _hasNetworkState = true;

            return;
        }

        _previousNetworkPosition = _currentNetworkPosition;
        _previousNetworkRotation = _currentNetworkRotation;
        _previousNetworkStatus = _currentNetworkStatus;

        _currentNetworkPosition = newPosition;
        _currentNetworkRotation = newRotation;
        _currentNetworkStatus = newStatus;

        _networkStateTimer = 0f;
    }

    private void UpdateRemotePresentation(float deltaTime)
    {
        if (!_hasNetworkState) return;

        _networkStateTimer += deltaTime;

        float interpolationTime = Mathf.Clamp01
        (
            _networkStateTimer / Time.fixedDeltaTime
        );

        Vector3 position = Vector3.Lerp
        (
            _previousNetworkPosition,
            _currentNetworkPosition,
            interpolationTime
        );

        Quaternion rotation = Quaternion.Slerp
        (
            _previousNetworkRotation,
            _currentNetworkRotation,
            interpolationTime
        );

        playerCharacter.SetPresentationState
        (
            position,
            rotation,
            _currentNetworkStatus
        );
    }
}
