using KinematicCharacterController;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

public struct PlayerInputState : INetworkSerializable
{
    public int Tick;
    public Quaternion Rotation;
    public Vector2 Move;
    public bool Jump;
    public bool JumpSustain;
    public int CrouchToggles;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer)
        where T : IReaderWriter
    {
        serializer.SerializeValue(ref Tick);
        serializer.SerializeValue(ref Rotation);
        serializer.SerializeValue(ref Move);
        serializer.SerializeValue(ref Jump);
        serializer.SerializeValue(ref JumpSustain);
        serializer.SerializeValue(ref CrouchToggles);
    }
}

public struct CharacterNetworkState : INetworkSerializable
{
    public int ServerTick;
    public int InputTick;

    public Vector3 Position;
    public Quaternion Rotation;
    public CharacterStatus Status;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer)
        where T : IReaderWriter
    {
        serializer.SerializeValue(ref ServerTick);
        serializer.SerializeValue(ref InputTick);
        serializer.SerializeValue(ref Position);
        serializer.SerializeValue(ref Rotation);
        serializer.SerializeValue(ref Status);
    }
}


public class Player : NetworkBehaviour
{
    //-----------------------------------------------------------------------------------------------------------
    // [PLAYER COMPONENT REFERENCES]
    [SerializeField] private PlayerCharacter playerCharacter;
    [Space]
    [SerializeField] private PlayerCamera playerCamera;
    [SerializeField] private CameraSpring cameraSpring;
    [SerializeField] private CameraLean cameraLean;
    [Space]

    //-----------------------------------------------------------------------------------------------------------
    // [LOCAL INPUT]
    private PlayerInputActions _inputActions;
    [Space]
    private PlayerInputState _pendingInput;
    private int _pendingCrouchToggles;
    [Space]

    //-----------------------------------------------------------------------------------------------------------
    // [SERVER INPUT]

    //Inputs Received From The Client But Not Yet Processed By The Server:
    private readonly Dictionary<int, PlayerInputState> _serverInputBuffer = new();

    //Most Recent Continuous Input Used By The Server:
    private PlayerInputState _lastServerInput;

    //Whether The Server Has Received The First Client Input:
    private bool _hasServerInputSequence;

    //Next Client Input Tick The Server Is Waiting To Process:
    private int _nextServerInputTick = -1;

    //Latest Client Input Tick Actually Processed By The Server:
    private int _lastProcessedInputTick = -1;

    //-----------------------------------------------------------------------------------------------------------
    // [CLIENT PREDICTION]
    private int _simulationTick;
    [Space]

    //Conditional Checks To Ensure Remote Client Input Is Predicted:
    private bool _isPredicting;
    private bool _predictionTickInitialized;

    //-----------------------------------------------------------------------------------------------------------
    // [PREDICTION HISTORY]

    //Queued Remote Client Input Data + Index To Its Current Previous:
    private const int PredictionHistorySize = 128;
    private int _lastProcessedAuthoritativeInputTick = -1;

    private struct PredictionHistoryEntry
    {
        public int Tick;
        public PlayerCharacterState State;
    }
    private readonly PredictionHistoryEntry[] _predictionHistory = new PredictionHistoryEntry[PredictionHistorySize];

    //-----------------------------------------------------------------------------------------------------------
    // [AUTHORITATIVE NETWORK STATE]

    //Authoritative Character State Received From The Server:
    private readonly NetworkVariable<CharacterNetworkState> _networkState = new NetworkVariable<CharacterNetworkState>();

    //Latest Authoritative Character State Received By The Owner:
    private CharacterNetworkState _authoritativeState;

    //-----------------------------------------------------------------------------------------------------------
    // [REMOTE PLAYER PRESENTATION]

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

    //-----------------------------------------------------------------------------------------------------------
    // [NETWORK LIFECYCLE]

    public override void OnNetworkSpawn()
    {
        //Set True For ALL Remote Clients:
        _isPredicting = IsOwner && !IsServer;


        for (int i = 0; i < PredictionHistorySize; i++)
        {
            _predictionHistory[i].Tick = -1;
        }


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

    //-----------------------------------------------------------------------------------------------------------
    // [FRAME UPDATE]
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

    //-----------------------------------------------------------------------------------------------------------
    // [Simulation Input]
    public void ApplySimulationInput(int serverSimulationTick)
    {
        //Server Controlled:
        if (IsServer)
        {
            PlayerInputState input;

            //Host Player:
            if (IsOwner)
            {
                input = _pendingInput;
                input.CrouchToggles = _pendingCrouchToggles;

                //Host Does Not Need Client Input Networking:
                input.Tick = serverSimulationTick;
            }
            //Remote Client Player:
            else
            {
                input = GetNextServerInput();
            }

            //Apply Input To The Character:
            ApplyInputToCharacter(input);

            //Reset Host One-Shot Input:
            if (IsOwner)
            {
                _pendingInput.Jump = false;
                _pendingCrouchToggles = 0;
            }

            return;
        }


        //Remote Client Input Prediction:
        if (_isPredicting && _predictionTickInitialized)
        {
            //Advance The Local Client Input Tick:
            _simulationTick++;

            PlayerInputState input = _pendingInput;
            input.CrouchToggles = _pendingCrouchToggles;
            input.Tick = _simulationTick;


            //Apply Locally For Immediate Prediction:
            ApplyInputToCharacter(input);


            //Send This Input To The Server:
            SubmitInputServerRpc(input);


            //Reset Local One-Shot Input:
            _pendingInput.Jump = false;
            _pendingCrouchToggles = 0;
        }
    }

    //-----------------------------------------------------------------------------------------------------------
    // [SERVER INPUT PROCESSING]

    private PlayerInputState GetNextServerInput()
    {
        //No Client Input Has Arrived Yet, Use The Default Input Until The First Input Arrives:
        if (!_hasServerInputSequence) return _lastServerInput;

        //Check Whether The Next Expected Client Input Has Arrived At The Server:
        if (_serverInputBuffer.TryGetValue(_nextServerInputTick, out PlayerInputState input))
        {
            //Remove The Input From The Waiting Buffer:
            _serverInputBuffer.Remove(_nextServerInputTick);

            //Remember Which Client Input The Server Processed:
            _lastProcessedInputTick = input.Tick;

            //Move To The Next Expected Client Input:
            _nextServerInputTick++;

            //Save Only Continuous Input For Use If The Next Client Input Has Not Arrived Yet:
            PlayerInputState continuousInput = input;
            continuousInput.Jump = false;
            continuousInput.CrouchToggles = 0;

            _lastServerInput = continuousInput;

            //Return The Original Input So One-Shot Inputs Are Applied On This Simulation Step:
            return input;
        }

        //The Next Client Input Has Not Arrived Yet, Continue Using The Most Recent Continuous Input:
        PlayerInputState fallbackInput = _lastServerInput;

        //Do Not Repeat One-Shot Input:
        fallbackInput.Jump = false;
        fallbackInput.CrouchToggles = 0;

        return fallbackInput;
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

    //-----------------------------------------------------------------------------------------------------------
    // [SEND CLIENT INPUT TO SERVER]

    [ServerRpc(RequireOwnership = true)]
    private void SubmitInputServerRpc(PlayerInputState input)
    {
        //Initialize The Server's Client Input Sequence When The First Input Arrives:
        if (!_hasServerInputSequence)
        {
            _hasServerInputSequence = true;
            _nextServerInputTick = input.Tick;
        }

        //Ignore Inputs That Were Already Processed:
        if (input.Tick < _nextServerInputTick)  return;

        //Store The Input Using Its Client Input Tick:
        _serverInputBuffer[input.Tick] = input;
    }

    //-----------------------------------------------------------------------------------------------------------
    // [SERVER STATE PUBLISHING]

    public void PublishServerState(int serverSimulationTick)
    {
        if (!IsServer) return;

        CharacterNetworkState state = new CharacterNetworkState
        {
            //Global Server Simulation Tick:
            ServerTick = serverSimulationTick,

            //Client Input Tick Actually Processed:
            InputTick = IsOwner
                ? serverSimulationTick
                : _lastProcessedInputTick,

            //Authoritative KCC Character State:
            Position = playerCharacter.GetPosition(),
            Rotation = playerCharacter.GetRotation(),
            Status = playerCharacter.GetStatus()
        };
        _networkState.Value = state;
    }
    //-----------------------------------------------------------------------------------------------------------
    // [RECEIVE SERVER STATE]

    public void ApplyNetworkState()
    {
        //The Server Does Not Need To Receive Its Own State:
        if (IsServer) return;

        CharacterNetworkState state = _networkState.Value;

        //-LOCAL PLAYER:
        if (IsOwner)
        {
            //Store The Latest Authoritative Server State:
            _authoritativeState = state;

            //Initialize The Client Prediction Tick Using The Server's Global Simulation Tick:
            if (!_predictionTickInitialized)
            {
                _simulationTick = state.ServerTick;
                _predictionTickInitialized = true;

                return;
            }

            //There Is Nothing To Compare Until The Server Has Processed At Least One Client Input:
            if (state.InputTick < 0)
            {
                return;
            }

            //Do Not Process The Same Authoritative Input Tick Twice:
            if (state.InputTick <= _lastProcessedAuthoritativeInputTick)
            {
                return;
            }

            _lastProcessedAuthoritativeInputTick = state.InputTick;

            //Find The Prediction State Corresponding To The Client Input The Server Actually Processed:
            if (TryGetPredictionState(state.InputTick,out PlayerCharacterState predictedState))
            {
                IsPredictionStateDifferent(predictedState, state);
            }

            return;
        }


        //-REMOTE PLAYER:

        //Read The Latest Authoritative Remote Player State:
        Vector3 newPosition = state.Position;
        Quaternion newRotation = state.Rotation;
        CharacterStatus newStatus = state.Status;

        //Apply The Latest State To The Remote Player's Authoritative KCC Body:
        playerCharacter.SetNetworkState
        (
            newPosition,
            newRotation,
            newStatus
        );

        //Initialize Remote Player Presentation:
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

        //Move Current State Into Previous State:
        _previousNetworkPosition = _currentNetworkPosition;
        _previousNetworkRotation = _currentNetworkRotation;
        _previousNetworkStatus = _currentNetworkStatus;

        //Store The Newest Authoritative State:
        _currentNetworkPosition = newPosition;
        _currentNetworkRotation = newRotation;
        _currentNetworkStatus = newStatus;

        //Start A New Interpolation Period:
        _networkStateTimer = 0f;
    }

    //-----------------------------------------------------------------------------------------------------------
    // [REMOTE PLAYER PRESENTATION]
    private void UpdateRemotePresentation(float deltaTime)
    {
        if (!_hasNetworkState) return;

        //Track How Long We Have Been Interpolating:
        _networkStateTimer += deltaTime;

        //Convert The Timer Into A 0-1 Interpolation Value:
        float interpolationTime = Mathf.Clamp01(_networkStateTimer / Time.fixedDeltaTime);

        //Smoothly Move Between The Previous And Current Authoritative Positions:
        Vector3 position = Vector3.Lerp
        (
            _previousNetworkPosition,
            _currentNetworkPosition,
            interpolationTime
        );

        //Smoothly Rotate Between The Previous And Current Authoritative Rotations:
        Quaternion rotation = Quaternion.Slerp
        (
            _previousNetworkRotation,
            _currentNetworkRotation,
            interpolationTime
        );

        //Apply The Smoothed State To The Remote Player's Visual Presentation:
        playerCharacter.SetPresentationState
        (
            position,
            rotation,
            _currentNetworkStatus
        );
    }

    //-----------------------------------------------------------------------------------------------------------
    // [PREDICTION HISTORY]
    public void SavePredictionState()
    {
        //Use The Client Input Tick As The History Index:
        int index = _simulationTick % PredictionHistorySize;

        //Capture The Complete Character State After This Prediction Tick Has Been Simulated:
        PlayerCharacterState state = playerCharacter.GetPredictionState();

        //Store The Tick And State In The History Buffer:
        _predictionHistory[index].Tick = _simulationTick;
        _predictionHistory[index].State = state;
    }

    public bool TryGetPredictionState(int tick, out PlayerCharacterState state)
    {
        //Find The History Entry For The Requested Tick:
        int index = tick % PredictionHistorySize;
        PredictionHistoryEntry entry = _predictionHistory[index];

        //Stored Entry That Actually Belongs To The Requested Tick:
        if (entry.Tick != tick)
        {
            state = default;
            return false;
        }
        state = entry.State;
        return true;
    }

    private bool IsPredictionStateDifferent(PlayerCharacterState predictedState, CharacterNetworkState authoritativeState)
    {
        //Initial Position Data:
        Vector3 predictedPosition = predictedState.MotorState.Position;
        Vector3 authoritativePosition = authoritativeState.Position;
        Vector3 positionDelta = authoritativePosition - predictedPosition;


        //Compare Position:
        float positionDifference = positionDelta.magnitude;

        //Compare Rotation:
        float rotationDifference = Quaternion.Angle(predictedState.MotorState.Rotation, authoritativeState.Rotation);

        //Compare Velocity:
        float velocityDifference = Vector3.Distance(predictedState.Status.Velocity,authoritativeState.Status.Velocity);

        //Compare Grounded State:
        bool groundedDifferent = predictedState.Status.Grounded != authoritativeState.Status.Grounded;

        //Compare Movement State:
        bool stateDifferent = predictedState.Status.State != authoritativeState.Status.State;


        //Tolerance Used To Determine If States Are Different Enough To Require Correction:
        const float positionTolerance = 0.01f;
        if (positionDifference > positionTolerance) return true;

        const float rotationTolerance = 0.5f;
        if (rotationDifference > rotationTolerance) return true;

        const float velocityTolerance = 0.01f;
        if (velocityDifference > velocityTolerance) return true;


        if (groundedDifferent) return true;
        if (stateDifferent) return true;

        return false;
    }

    //-----------------------------------------------------------------------------------------------------------
    // [PUBLIC ACCESS]
    public KinematicCharacterMotor GetMotor() => playerCharacter.GetMotor();
}
