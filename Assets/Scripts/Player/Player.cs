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
    // [RECONCILIATION VISUAL DEBUG]
    [SerializeField] private GameObject predictedPositionMarker;
    [SerializeField] private GameObject authoritativePositionMarker;
    [Space]
    [SerializeField] private PlayerStatusUI playerStatusUI;

    private Transform _predictedPositionMarker;
    private Transform _authoritativePositionMarker;

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

    //Latest Client Input Tick Already Sent As An Authoritative State:
    private int _lastPublishedInputTick = -1;

    //-----------------------------------------------------------------------------------------------------------
    // [CLIENT PREDICTION]
    private int _simulationTick;
    [Space]

    //Conditional Checks To Ensure Remote Client Input Is Predicted:
    private bool _isPredicting;
    private bool _predictionTickInitialized;

    //The Most Recent Input Simulated By The Local Client:
    private PlayerInputState _lastPredictionInput;

    //Reusable KCC Motor List Used During Input Replay:
    private readonly List<KinematicCharacterMotor> _replayMotors = new(1);

    //-----------------------------------------------------------------------------------------------------------
    // [PREDICTION PAUSE]

    //True While Local Prediction Has Been Paused Because The Prediction Window Is No Longer Large Enough To Safely Retain Rollback History:
    private bool _predictionPaused;

    //Prediction Lead Must Fall To This Value Before Prediction Resumes. Using A Lower Resume Threshold Prevents Rapid Pause/Resume Cycling:
    private const int PredictionResumeLead = MaxInputTickLead / 2;

    //Optional Local Connection Warning UI:
    [SerializeField] private GameObject predictionPausedWarningUI;

    //-----------------------------------------------------------------------------------------------------------
    // [PREDICTION HISTORY]

    //Queued Remote Client Input Data + Index To Its Current Previous:
    private const int PredictionHistorySize = 128;
    private const int MaxInputTickLead = PredictionHistorySize - 1;
    private int _lastProcessedAuthoritativeInputTick = -1;

    private struct PredictionHistoryEntry
    {
        //The Client Input Tick:
        public int Tick;

        //The Input Used For This Prediction Tick:
        public PlayerInputState Input;

        //The Character State After This Input Was Simulated:
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

        //Create Local Reconciliation Debug Markers:
        if (_isPredicting)
        {
            if (predictedPositionMarker != null)
            {
                GameObject predictedMarker =
                    Instantiate(predictedPositionMarker);

                _predictedPositionMarker = predictedMarker.transform;
            }

            if (authoritativePositionMarker != null)
            {
                GameObject authoritativeMarker =
                    Instantiate(authoritativePositionMarker);

                _authoritativePositionMarker =
                    authoritativeMarker.transform;
            }

            if (predictionPausedWarningUI != null)
            {
                predictionPausedWarningUI.SetActive(false);
            }
        }

        //Ensure Player Camera Doesn't Swap To A Newly Joined Client:
        if (!IsOwner)
        {
            playerCamera.gameObject.SetActive(false);
            playerStatusUI.gameObject.SetActive(false);

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

        if (_predictedPositionMarker != null)
        {
            Destroy(_predictedPositionMarker.gameObject);
        }

        if (_authoritativePositionMarker != null)
        {
            Destroy(_authoritativePositionMarker.gameObject);
        }

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

    private void UpdateReconciliationMarkers(
    Vector3 predictedPosition,
    Vector3 authoritativePosition)
    {
        if (_predictedPositionMarker != null)
        {
            _predictedPositionMarker.position = predictedPosition;
        }

        if (_authoritativePositionMarker != null)
        {
            _authoritativePositionMarker.position = authoritativePosition;
        }
    }

    private void AddRemoteSnapshot(CharacterNetworkState state)
    {
        //Ignore Snapshots That Are Not Newer Than The Newest Snapshot We Already Have:
        if (_remoteSnapshotBuffer.Count > 0)
        {
            int newestServerTick =
                _remoteSnapshotBuffer[_remoteSnapshotBuffer.Count - 1].ServerTick;

            if (state.ServerTick <= newestServerTick)
            {
                return;
            }
        }

        RemoteSnapshot snapshot = new RemoteSnapshot
        {
            ServerTick = state.ServerTick,
            Position = state.Position,
            Rotation = state.Rotation,
            Status = state.Status
        };

        //Store The Newest Authoritative Snapshot:
        _remoteSnapshotBuffer.Add(snapshot);

        //Keep The Buffer Bounded:
        if (_remoteSnapshotBuffer.Count > RemoteSnapshotBufferSize)
        {
            _remoteSnapshotBuffer.RemoveAt(0);
        }

        Debug.Log(
            $"[REMOTE SNAPSHOT BUFFER] " +
            $"ServerTick={state.ServerTick} | " +
            $"BufferCount={_remoteSnapshotBuffer.Count} | " +
            $"OldestTick={_remoteSnapshotBuffer[0].ServerTick} | " +
            $"NewestTick={_remoteSnapshotBuffer[_remoteSnapshotBuffer.Count - 1].ServerTick}"
        );
    }

    private bool TryGetInterpolatedRemoteState(
     out Vector3 position,
     out Quaternion rotation,
     out CharacterStatus status)
    {
        position = default;
        rotation = Quaternion.identity;
        status = default;

        if (_remoteSnapshotBuffer.Count < 2)
        {
            return false;
        }

        //Render Deliberately Behind The Newest Received Authoritative Snapshot:
        int newestTick =
            _remoteSnapshotBuffer[_remoteSnapshotBuffer.Count - 1].ServerTick;

        int renderTick =
            newestTick - RemoteInterpolationDelayTicks;

        RemoteSnapshot fromSnapshot = default;
        RemoteSnapshot toSnapshot = default;

        bool foundFromSnapshot = false;
        bool foundToSnapshot = false;

        //Find the Two Buffered Snapshots Surrounding The Desired Render Tick:
        for (int i = 0; i < _remoteSnapshotBuffer.Count - 1; i++)
        {
            RemoteSnapshot from = _remoteSnapshotBuffer[i];
            RemoteSnapshot to = _remoteSnapshotBuffer[i + 1];

            if (from.ServerTick <= renderTick &&
                to.ServerTick >= renderTick)
            {
                fromSnapshot = from;
                toSnapshot = to;

                foundFromSnapshot = true;
                foundToSnapshot = true;

                break;
            }
        }

        //Normal Interpolation Path:
        if (foundFromSnapshot && foundToSnapshot)
        {
            int tickRange =
                toSnapshot.ServerTick - fromSnapshot.ServerTick;

            float interpolationTime = tickRange > 0
                ? (float)(renderTick - fromSnapshot.ServerTick) / tickRange
                : 0f;

            interpolationTime =
                Mathf.Clamp01(interpolationTime);

            position = Vector3.Lerp(
                fromSnapshot.Position,
                toSnapshot.Position,
                interpolationTime
            );

            rotation = Quaternion.Slerp(
                fromSnapshot.Rotation,
                toSnapshot.Rotation,
                interpolationTime
            );

            status =
                interpolationTime < 0.5f
                    ? fromSnapshot.Status
                    : toSnapshot.Status;

            return true;
        }

        //-------------------------------------------------------------------------------------------------------
        // [SHORT EXTRAPOLATION]

        RemoteSnapshot newestSnapshot =
            _remoteSnapshotBuffer[_remoteSnapshotBuffer.Count - 1];

        int ticksBeyondNewest =
            renderTick - newestSnapshot.ServerTick;

        if (ticksBeyondNewest > 0)
        {
            float extrapolationTime =
                ticksBeyondNewest * Time.fixedDeltaTime;

            if (extrapolationTime <= MaxRemoteExtrapolationTime)
            {
                position =
                    newestSnapshot.Position +
                    newestSnapshot.Status.Velocity *
                    extrapolationTime;

                rotation = newestSnapshot.Rotation;

                status = newestSnapshot.Status;

                Debug.Log
                (
                    $"[REMOTE SNAPSHOT EXTRAPOLATION] " +
                    $"NewestTick={newestSnapshot.ServerTick} | " +
                    $"RenderTick={renderTick} | " +
                    $"TicksBeyondNewest={ticksBeyondNewest} | " +
                    $"ExtrapolationTime={extrapolationTime:F3}"
                );

                return true;
            }
        }

        return false;
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
            //Do Not Generate New Predicted Inputs While Paused:
            if (_predictionPaused)
            {
                return;
            }

            //Do Not Predict Farther Ahead Than The Prediction History Can Safely Retain:
            if (_lastProcessedAuthoritativeInputTick >= 0)
            {
                int predictionLead =
                    _simulationTick - _lastProcessedAuthoritativeInputTick;

                if (predictionLead >= MaxInputTickLead)
                {
                    EnterPredictionPause();
                    return;
                }
            }

            //Advance The Local Client Input Tick:
            _simulationTick++;

            //Create Input Data For This Prediction Tick:
            PlayerInputState input = _pendingInput;
            input.CrouchToggles = _pendingCrouchToggles;
            input.Tick = _simulationTick;

            //Remember The Input Used For This Prediction Tick:
            _lastPredictionInput = input;

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

        //Remote Players Only Publish A New State When A New Client Input Has Actually Been Processed:
        if (!IsOwner && _lastProcessedInputTick == _lastPublishedInputTick)
        {
            return;
        }

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

        //Remember Which Client Input This Snapshot Represents:
        _lastPublishedInputTick = _lastProcessedInputTick;

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
                if (state.InputTick < 0)
                {
                    return;
                }

                _simulationTick = state.InputTick;
                _lastProcessedAuthoritativeInputTick = state.InputTick;
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

            //Check Whether The Server Has Caught Up Enough For Prediction To Resume:
            if (_predictionPaused)
            {
                int predictionLead =
                    _simulationTick - _lastProcessedAuthoritativeInputTick;

                if (predictionLead <= PredictionResumeLead)
                {
                    ExitPredictionPause();
                }
            }

            //Find The Prediction State Corresponding To The Client Input The Server Actually Processed:
            if (TryGetPredictionState(state.InputTick, out _, out PlayerCharacterState predictedState))
            {
                bool isDifferent = IsPredictionStateDifferent(predictedState, state);

                //Detect When The Client Prediction Does Not Match The Server's Authoritative State:
                if (isDifferent)
                {
                    //Remember How Far The Client Had Predicted Before Rolling Back:
                    int currentPredictionTick = _simulationTick;

                    Debug.Log
                    (
                        $"[RECONCILIATION START] " +
                        $"ServerTick={state.ServerTick} | " +
                        $"InputTick={state.InputTick} | " +
                        $"CurrentPredictionTick={currentPredictionTick} | " +
                        $"ReplayCount={currentPredictionTick - state.InputTick}"
                    );


                    //Restore The Authoritative Server State:
                    RestoreAuthoritativeState(state, predictedState);


                    //Replay Every Local Input After The Authoritative Input Tick:
                    ReplayPredictedInputs(
                        state.InputTick,
                        currentPredictionTick
                    );

                    LogPostReconciliationState(
                        state,
                        currentPredictionTick
                    );
                }
            }
            else
            {
                Debug.LogError
                (
                    $"[RECONCILIATION HISTORY MISSING] " +
                    $"InputTick={state.InputTick} | " +
                    $"CurrentPredictionTick={_simulationTick}"
                );
            }

            return;
        }


        //-REMOTE PLAYER:

        //Read The Latest Authoritative Remote Player State:
        Vector3 newPosition = state.Position;
        Quaternion newRotation = state.Rotation;
        CharacterStatus newStatus = state.Status;

        //Store the authoritative state in the remote snapshot buffer.
        AddRemoteSnapshot(state);

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

        if (TryGetInterpolatedRemoteState(out Vector3 position, out Quaternion rotation, out CharacterStatus status))
        {
            //Store The Newest Valid Interpolated Presentation State:
            _lastRemotePresentationPosition = position;
            _lastRemotePresentationRotation = rotation;
            _lastRemotePresentationStatus = status;
            _hasLastRemotePresentation = true;

            playerCharacter.SetPresentationState
            (
                position,
                rotation,
                status
            );

            return;
        }

        //No Pair Of Buffered Snapshots Currently Surrounds The Desired Render Time. Hold The Last Valid Presentation Instead Of Snapping:
        if (_hasLastRemotePresentation)
        {
            Debug.LogWarning
            (
                $"[REMOTE SNAPSHOT WAITING] " +
                $"BufferCount={_remoteSnapshotBuffer.Count}"
            );

            playerCharacter.SetPresentationState
            (
                _lastRemotePresentationPosition,
                _lastRemotePresentationRotation,
                _lastRemotePresentationStatus
            );
        }
    }

    //-----------------------------------------------------------------------------------------------------------
    // [REMOTE SNAPSHOT BUFFER]

    private const int RemoteSnapshotBufferSize = 32;

    //Number Of Server Ticks The Remote Presentation Intentionally Renders Behind The Newest Received Snapshot:
    private const int RemoteInterpolationDelayTicks = 6;

    private struct RemoteSnapshot
    {
        //Authoritative Server Simulation Tick This Snapshot Represents:
        public int ServerTick;

        //Authoritative Character State Received From The Server:
        public Vector3 Position;
        public Quaternion Rotation;
        public CharacterStatus Status;
    }
    private readonly List<RemoteSnapshot> _remoteSnapshotBuffer = new();

    //Last Valid Remote Presentation State Produced By Snapshot Interpolation:
    private Vector3 _lastRemotePresentationPosition;
    private Quaternion _lastRemotePresentationRotation;
    private CharacterStatus _lastRemotePresentationStatus;
    private bool _hasLastRemotePresentation;

    //Maximum Amount Of Time A Remote Player May Extrapolate When A Future Authoritative Snapshot Is Unavailable:
    private const float MaxRemoteExtrapolationTime = 0.10f;



    //-----------------------------------------------------------------------------------------------------------
    // [PREDICTION HISTORY]

    public bool TryGetPredictionState(int tick,out PlayerInputState input ,out PlayerCharacterState state)
    {
        //Find The History Entry For The Requested Tick:
        int index = tick % PredictionHistorySize;
        PredictionHistoryEntry entry = _predictionHistory[index];

        //Stored Entry That Actually Belongs To The Requested Tick:
        if (entry.Tick != tick)
        {
            input = default;
            state = default;
            return false;
        }
        input = entry.Input;
        state = entry.State;
        return true;
    }

    private bool IsPredictionStateDifferent(PlayerCharacterState predictedState, CharacterNetworkState authoritativeState)
    {
        //Initial Position Data:
        Vector3 predictedPosition = predictedState.MotorState.Position;
        Vector3 authoritativePosition = authoritativeState.Position;
        Vector3 positionDelta = authoritativePosition - predictedPosition;


        UpdateReconciliationMarkers(predictedPosition, authoritativePosition);

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

        int predictionLead = _simulationTick - authoritativeState.InputTick;
        Debug.Log
        (
            $"[RECONCILIATION DIAGNOSTIC] " +
            $"ServerTick={authoritativeState.ServerTick} | " +
            $"InputTick={authoritativeState.InputTick} | " +
            $"CurrentPredictionTick={_simulationTick} | " +
            $"PredictionLead={predictionLead} | " +
            $"TickGap={authoritativeState.ServerTick - authoritativeState.InputTick} | " +
            $"PredictedPos={predictedPosition} | " +
            $"AuthoritativePos={authoritativePosition} | " +
            $"PosDiff={positionDifference:F6} | " +
            $"RotDiff={rotationDifference:F6}° | " +
            $"VelDiff={velocityDifference:F6} | " +
            $"GroundedDiff={groundedDifferent} | " +
            $"StateDiff={stateDifferent}"
        );


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

    private void RestoreAuthoritativeState(CharacterNetworkState state, PlayerCharacterState predictedState)
    {
        Debug.Log
        (
            $"[RECONCILIATION RESTORE] " +
            $"ServerTick={state.ServerTick} | " +
            $"InputTick={state.InputTick}"
        );

        playerCharacter.ApplyReconciliationState(state, predictedState);

        //The Restored Character State Represents This Client Input Tick:
        _simulationTick = state.InputTick;
    }

    public void SavePredictionState()
    {
        //Use The Current Client Prediction Tick As The History Index:
        int index = _simulationTick % PredictionHistorySize;

        //Capture The Character State After This Prediction Tick:
        PlayerCharacterState state =
            playerCharacter.GetPredictionState();

        //Store The Tick, Input, And Resulting Character State:
        _predictionHistory[index].Tick = _simulationTick;
        _predictionHistory[index].Input = _lastPredictionInput;
        _predictionHistory[index].State = state;
    }

    private void ReplayPredictedInputs(int authoritativeInputTick, int currentPredictionTick)
    {
        Debug.Log
        (
            $"[REPLAY START] " +
            $"FromTick={authoritativeInputTick + 1} | " +
            $"ToTick={currentPredictionTick}"
        );


        for (int tick = authoritativeInputTick + 1; tick <= currentPredictionTick; tick++)
        {
            if (!TryGetPredictionState(tick, out PlayerInputState input, out _))
            {
                Debug.LogError
                (
                    $"[RECONCILIATION REPLAY FAILED] " +
                    $"MissingPredictionTick={tick} | " +
                    $"AuthoritativeInputTick={authoritativeInputTick} | " +
                    $"CurrentPredictionTick={currentPredictionTick}"
                );
                return;
            }


            _simulationTick = tick;

            ApplyInputToCharacter(input);

            SimulatePredictionTick();

            SaveReplayedState(tick, input);
        }

        Debug.Log
        (
            $"[REPLAY COMPLETE] " +
            $"FinalPredictionTick={_simulationTick}"
        );
    }

    private void LogPostReconciliationState(CharacterNetworkState authoritativeState,int finalPredictionTick)
    {
        bool finalTickValid = _simulationTick == finalPredictionTick;
        bool historyValid = true;

        for (int tick = authoritativeState.InputTick + 1; tick <= finalPredictionTick; tick++)
        {
            int index = tick % PredictionHistorySize;

            if (_predictionHistory[index].Tick != tick)
            {
                historyValid = false;
                break;
            }
        }

        Debug.Log
        (
            $"[RECONCILIATION COMPLETE] " +
            $"AuthoritativeInputTick={authoritativeState.InputTick} | " +
            $"FinalPredictionTick={finalPredictionTick} | " +
            $"ReplayCount={finalPredictionTick - authoritativeState.InputTick} | " +
            $"FinalTickValid={finalTickValid} | " +
            $"HistoryValid={historyValid}"
        );
    }

    private void SaveReplayedState(int tick, PlayerInputState input)
    {
        //Use The Replayed Client Input Tick As The History Index:
        int index = tick % PredictionHistorySize;

        //Capture The Complete Character State After This Replay Tick:
        PlayerCharacterState state = playerCharacter.GetPredictionState();

        //Overwrite The Old Predicted State With The Corrected Replayed State:
        _predictionHistory[index].Tick = tick;
        _predictionHistory[index].Input = input;
        _predictionHistory[index].State = state;
    }

    private void SimulatePredictionTick()
    {
        //Clear Any Previous Replay Motor:
        _replayMotors.Clear();

        //Add This Player's KCC Motor:
        _replayMotors.Add(playerCharacter.GetMotor());


        //Simulate One Fixed Prediction Step:
        KinematicCharacterSystem.Simulate
        (
            Time.fixedDeltaTime,
            _replayMotors,
            KinematicCharacterSystem.PhysicsMovers
        );
    }

    private void EnterPredictionPause()
    {
        //Prevent The Same Transition From Happening Repeatedly:
        if (_predictionPaused) return;

        _predictionPaused = true;

        Debug.LogWarning
        (
            $"[PREDICTION PAUSED] " +
            $"AuthoritativeInputTick={_lastProcessedAuthoritativeInputTick} | " +
            $"CurrentPredictionTick={_simulationTick} | " +
            $"PredictionLead={_simulationTick - _lastProcessedAuthoritativeInputTick}"
        );

        //Show The Optional Local Connection Warning:
        if (predictionPausedWarningUI != null)
        {
            predictionPausedWarningUI.SetActive(true);
        }
    }

    private void ExitPredictionPause()
    {
        //Prevent The Same Transition From Happening Repeatedly:
        if (!_predictionPaused) return;

        _predictionPaused = false;

        Debug.Log(
            $"[PREDICTION RESUMED] " +
            $"AuthoritativeInputTick={_lastProcessedAuthoritativeInputTick} | " +
            $"CurrentPredictionTick={_simulationTick} | " +
            $"PredictionLead={_simulationTick - _lastProcessedAuthoritativeInputTick}"
        );

        //Hide The Local Connection Warning:
        if (predictionPausedWarningUI != null)
        {
            predictionPausedWarningUI.SetActive(false);
        }
    }

    //-----------------------------------------------------------------------------------------------------------
    // [PUBLIC ACCESS]
    public KinematicCharacterMotor GetMotor() => playerCharacter.GetMotor();
    public bool IsPredictionPaused => _predictionPaused;
}
