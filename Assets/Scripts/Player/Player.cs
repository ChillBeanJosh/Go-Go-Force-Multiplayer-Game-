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
    [Space]


    private readonly NetworkVariable<Vector3> _networkPosition =
        new NetworkVariable<Vector3>();

    private readonly NetworkVariable<Quaternion> _networkRotation =
        new NetworkVariable<Quaternion>();

    private readonly NetworkVariable<CharacterStatus> _networkStatus =
        new NetworkVariable<CharacterStatus>();

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

        //Ensure Client Owner Has Input + Camera Access:
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


#if UNITY_EDITOR
            if (Keyboard.current.tKey.wasPressedThisFrame)
            {
                var ray = new Ray(
                    playerCamera.transform.position,
                    playerCamera.transform.forward
                );

                if (Physics.Raycast(ray, out var hit))
                {
                    Teleport(hit.point);
                }
            }
#endif
        }

        //Update Client's Mesh + Camera Positions:
        if (IsClient)
        {
            playerCharacter.UpdateBody(deltaTime);
        }
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
        //Server Side Input Handling:
        if (IsServer)
        {
            //Applied Host Client Input:
            if (IsOwner)
            {
                PlayerInputState input = GetPendingSimulationInput();
                ApplyInputToCharacter(input);

                //Reset Button Input Values After Input Is Read And Stores:
                _pendingInput.Jump = false;
                _pendingCrouchToggles = 0;
            }
            //Applied Remote Client Input:
            else
            {
                Debug.Log
                (
                    $"[SERVER SIM] Client {OwnerClientId} " +
                    $"Move={_serverInput.Move} " +
                    $"Jump={_serverInput.Jump} " +
                    $"JumpSustain={_serverInput.JumpSustain} " +
                    $"Crouch={_serverInput.CrouchToggles}"
                );
                ApplyInputToCharacter(_serverInput);
            }
            return;
        }

        //Remote Client Input Logic Sent to ServerRPC So That Server Can Apply Its Logic:
        if (IsOwner)
        {
            PlayerInputState input = GetPendingSimulationInput();
            SubmitInputServerRpc(input);

            //Reset Button Input Values After Input Is Read And Stores:
            _pendingInput.Jump = false;
            _pendingCrouchToggles = 0;
        }
    }

    private PlayerInputState GetPendingSimulationInput()
    {
        //Create A Snapshot Of The Input For This Simulation Tick:
        PlayerInputState input = _pendingInput;

        //Add All Crouch Toggle Presses Collected Since The Last Tick:
        input.CrouchToggles = _pendingCrouchToggles;

        return input;
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
        _serverInput = input;

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

        //Apply The Latest Server Character State On This Client:
        playerCharacter.SetNetworkState
        (
            _networkPosition.Value,
            _networkRotation.Value,
            _networkStatus.Value
        );
    }
    public void Teleport(Vector3 position)
    {
        if (!IsOwner) return;

        playerCharacter.SetPosition(position);
    }
}
