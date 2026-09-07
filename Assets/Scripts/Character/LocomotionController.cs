using KinematicCharacterController;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

public struct ClientInput
{
    public int sequenceNumber;
    public PlayerCharacterInputs Inputs;
}

public enum CharacterState
{
    Default,
}

public struct PlayerCharacterInputs
{
    public float MoveAxisForward;
    public float MoveAxisRight;
    public Quaternion CameraRotation;
    public bool JumpDown;
    public bool JumpHeld;
}

public enum OrientationMethod
{
    TowardsCamera,
    TowardsMovement,
}

public enum JumpStatus
{
    None,
    Requested,
    Executed,
    DoubleJumpUsed
}


public class LocomotionController : NetworkBehaviour, ICharacterController
{
    public KinematicCharacterMotor Motor;

    [Header("Stable Movement")]
    [SerializeField] private float MaxStableMoveSpeed = 10f;
    [SerializeField] private float StableMovementSharpness = 15f;
    [SerializeField] private float OrientationSharpness = 10f;
    [SerializeField] private OrientationMethod OrientationMethod = OrientationMethod.TowardsCamera;

    [Header("Air Movement")]
    [SerializeField] private float MaxAirMoveSpeed = 15f;
    [SerializeField] private float AirAccelerationSpeed = 15f;
    [SerializeField] private float Drag = 0.1f;
    [SerializeField] private Vector3 Gravity = new Vector3(0, -30f, 0);

    [Header("Jumping")]
    [SerializeField] private bool AllowJumpingWhenSliding = false;
    [SerializeField] private bool AllowDoubleJump = false;
    [SerializeField] private bool AllowWallJump = false;
    [SerializeField] private float JumpUpSpeed = 10f;
    [SerializeField] private float JumpScalableForwardSpeed = 10f;
    [SerializeField] private float JumpPreGroundingGraceTime = 0f;
    [SerializeField] private float JumpPostGroundingGraceTime = 0f;
    [SerializeField] private float HighJumpMultiplier = 1.5f;
    [SerializeField] private float LongJumpMultiplier = 1.5f;

    public CharacterState CurrentCharacterState { get; private set; }
    public NetworkVariable<CharacterState> NetworkCharacterState = new NetworkVariable<CharacterState>(CharacterState.Default);

    private float m_targetForwardAxis;
    private float m_targetRightAxis;

    private Vector3 m_moveInputVector;
    private Vector3 m_lookInputVector;

    private int _latchedJumpType = -1;
    private float _jumpTypeTimer = 0f;
    private const float JumpTypeDuration = 0.2f;
    private float _landingTime = -100f;
    private JumpStatus _jumpStatus = JumpStatus.None;
    private float _timeSinceJumpRequested = Mathf.Infinity;
    private float _timeSinceLastAbleToJump = 0f;
    private bool _canWallJump = false;
    private Vector3 _wallJumpNormal;
    private bool _hasJumped = false;
    private bool _hasDoubleJump = false;
    private bool _hasWallJumped = false;


    private PlayerCharacterInputs _latestInputs;
    private List<ClientInput> inputBuffer = new List<ClientInput>();
    private int localSequence = 0;


    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            TransitionToState(CharacterState.Default);
        }
    }

    private void Start()
    {
        if (IsServer)
        {
            Motor.CharacterController = this;
        }
        else
        {
            Motor.enabled = false;
        }
    }

    private void Update()
    {
        if (IsServer)
        {
            NetworkCharacterState.Value = CurrentCharacterState;
        }
    }

    public void TransitionToState(CharacterState newState)
    {
        CharacterState tmpInitialState = CurrentCharacterState;
        OnStateExit(tmpInitialState, newState);
        CurrentCharacterState = newState;
        OnStateEnter(newState, tmpInitialState);

        if (IsServer)
        {
            NetworkCharacterState.Value = newState;
        }
    }

    public void OnStateEnter(CharacterState state, CharacterState fromState)
    {
        switch (state)
        {
            case CharacterState.Default:
                {
                    break;
                }
        }
    }

    public void OnStateExit(CharacterState state, CharacterState toState)
    {
        switch (state)
        {
            case CharacterState.Default:
            {
                break;
            }
        }
    }

    public void SetInputs(ref PlayerCharacterInputs inputs)
    {
        if (!IsServer)
        {
            _latestInputs = inputs;
            ProcessInputs();

            localSequence++;
            inputBuffer.Add(new ClientInput { sequenceNumber = localSequence, Inputs = inputs});

            SubmitInputsServerRpc(inputs.MoveAxisForward, inputs.MoveAxisRight, inputs.CameraRotation, inputs.JumpDown, inputs.JumpHeld, localSequence);
        }
        else
        {
            _latestInputs = inputs;
            ProcessInputs();

            UpdateClientStateClientRpc(Motor.TransientPosition, Motor.TransientRotation, -1);
        }
    }

    [ServerRpc]
    private void SubmitInputsServerRpc(float moveAxisForward, float moveAxisRight, Quaternion cameraRotation, bool jumpDown, bool jumpHeld, int sequenceNumber)
    {
        PlayerCharacterInputs inputs;
        inputs.MoveAxisForward = moveAxisForward;
        inputs.MoveAxisRight = moveAxisRight;
        inputs.CameraRotation = cameraRotation;
        inputs.JumpDown = jumpDown;
        inputs.JumpHeld = jumpHeld;

        _latestInputs = inputs;
        ProcessInputs();

        UpdateClientStateClientRpc(Motor.TransientPosition, Motor.TransientRotation, sequenceNumber);
    }

    [ClientRpc]
    private void UpdateClientStateClientRpc(Vector3 serverPostion, Quaternion serverRotation, int confirmedSequence)
    {
        if (IsServer) return;

        Motor.transform.position = serverPostion;
        Motor.transform.rotation = serverRotation;

        inputBuffer.RemoveAll(x => x.sequenceNumber <= confirmedSequence);

        foreach (var clientInput in inputBuffer)
        {
            _latestInputs = clientInput.Inputs;
            ProcessInputs();
        }
    }

    private void ProcessInputs()
    {
        // Clamp input
        Vector3 moveInputVector = Vector3.ClampMagnitude(new Vector3(_latestInputs.MoveAxisRight, 0f, _latestInputs.MoveAxisForward), 1f);

        // Calculate camera direction and rotation on the character plane
        Vector3 cameraPlanarDirection = Vector3.ProjectOnPlane(_latestInputs.CameraRotation * Vector3.forward, Motor.CharacterUp).normalized;
        if (cameraPlanarDirection.sqrMagnitude == 0f)
        {
            cameraPlanarDirection = Vector3.ProjectOnPlane(_latestInputs.CameraRotation * Vector3.up, Motor.CharacterUp).normalized;
        }
        Quaternion cameraPlanarRotation = Quaternion.LookRotation(cameraPlanarDirection, Motor.CharacterUp);

        m_targetForwardAxis = _latestInputs.MoveAxisForward;
        m_targetRightAxis = _latestInputs.MoveAxisRight;
        m_moveInputVector = cameraPlanarRotation * moveInputVector;

        switch (OrientationMethod)
        {
            case OrientationMethod.TowardsCamera:
                m_lookInputVector = cameraPlanarDirection;
                break;
            case OrientationMethod.TowardsMovement:
                m_lookInputVector = m_moveInputVector.normalized;
                break;
        }

        if (_latestInputs.JumpDown)
        {
            bool canJumpFromGround = Motor.GroundingStatus.IsStableOnGround || (AllowJumpingWhenSliding && Motor.GroundingStatus.FoundAnyGround);
            bool doubleJumpAvailable = AllowDoubleJump && !_hasDoubleJump;
            bool wallJumpAvailable = AllowWallJump && _canWallJump;

            if (canJumpFromGround)
            {
                _jumpStatus = JumpStatus.Requested;
            }
            else if (doubleJumpAvailable)
            {
                _jumpStatus = JumpStatus.Requested;
            }
            else if (wallJumpAvailable)
            {
                _jumpStatus = JumpStatus.Requested;
            }
        }
    }


    public void UpdateRotation(ref Quaternion currentRotation, float deltaTime)
    {
        if (m_lookInputVector.sqrMagnitude > 0f && OrientationSharpness > 0f)
        {
            // Smoothly interpolate from current to target look direction
            Vector3 smoothedLookInputDirection = Vector3.Slerp(Motor.CharacterForward, m_lookInputVector, 1 - Mathf.Exp(-OrientationSharpness * deltaTime)).normalized;

            // Set the current rotation (which will be used by the KinematicCharacterMotor)
            currentRotation = Quaternion.LookRotation(smoothedLookInputDirection, Motor.CharacterUp);
        }
    }

    public void UpdateVelocity(ref Vector3 currentVelocity, float deltaTime)
    {
        // Ground movement
        if (Motor.GroundingStatus.IsStableOnGround)
        {
            float currentVelocityMagnitude = currentVelocity.magnitude;

            Vector3 effectiveGroundNormal = Motor.GroundingStatus.GroundNormal;

            // Reorient velocity on slope
            currentVelocity = Motor.GetDirectionTangentToSurface(currentVelocity, effectiveGroundNormal) * currentVelocityMagnitude;

            // Calculate target velocity
            Vector3 inputRight = Vector3.Cross(m_moveInputVector, Motor.CharacterUp);
            Vector3 reorientedInput = Vector3.Cross(effectiveGroundNormal, inputRight).normalized * m_moveInputVector.magnitude;
            Vector3 targetMovementVelocity = reorientedInput * MaxStableMoveSpeed;

            // Smooth movement Velocity
            currentVelocity = Vector3.Lerp(currentVelocity, targetMovementVelocity, 1f - Mathf.Exp(-StableMovementSharpness * deltaTime));
        }
        // Air movement
        else
        {
            // Add move input
            if (m_moveInputVector.sqrMagnitude > 0f)
            {
                Vector3 addedVelocity = m_moveInputVector * AirAccelerationSpeed * deltaTime;

                Vector3 currentVelocityOnInputsPlane = Vector3.ProjectOnPlane(currentVelocity, Motor.CharacterUp);

                // Limit air velocity from inputs
                if (currentVelocityOnInputsPlane.magnitude < MaxAirMoveSpeed)
                {
                    // clamp addedVel to make total vel not exceed max vel on inputs plane
                    Vector3 newTotal = Vector3.ClampMagnitude(currentVelocityOnInputsPlane + addedVelocity, MaxAirMoveSpeed);
                    addedVelocity = newTotal - currentVelocityOnInputsPlane;
                }
                else
                {
                    // Make sure added vel doesn't go in the direction of the already-exceeding velocity
                    if (Vector3.Dot(currentVelocityOnInputsPlane, addedVelocity) > 0f)
                    {
                        addedVelocity = Vector3.ProjectOnPlane(addedVelocity, currentVelocityOnInputsPlane.normalized);
                    }
                }

                // Prevent air-climbing sloped walls
                if (Motor.GroundingStatus.FoundAnyGround)
                {
                    if (Vector3.Dot(currentVelocity + addedVelocity, addedVelocity) > 0f)
                    {
                        Vector3 perpenticularObstructionNormal = Vector3.Cross(Vector3.Cross(Motor.CharacterUp, Motor.GroundingStatus.GroundNormal), Motor.CharacterUp).normalized;
                        addedVelocity = Vector3.ProjectOnPlane(addedVelocity, perpenticularObstructionNormal);
                    }
                }

                // Apply added velocity
                currentVelocity += addedVelocity;
            }

            // Gravity
            currentVelocity += Gravity * deltaTime;

            // Drag
            currentVelocity *= (1f / (1f + (Drag * deltaTime)));
        }

        {
            _timeSinceJumpRequested += deltaTime;

            if (_jumpStatus == JumpStatus.Requested)
            {
                if(AllowWallJump && _canWallJump)
                {
                    Vector3 jumpDirection = (Motor.CharacterUp + _wallJumpNormal).normalized;
                    Motor.ForceUnground(0.1f);
                    
                    currentVelocity += (jumpDirection * JumpUpSpeed) - Vector3.Project(currentVelocity, Motor.CharacterUp);
                    currentVelocity += (m_moveInputVector * JumpScalableForwardSpeed);

                    _jumpStatus = JumpStatus.Executed;
                    _hasJumped = true;
                    _hasWallJumped = true;
                }
                else if ((AllowJumpingWhenSliding ? Motor.GroundingStatus.FoundAnyGround : Motor.GroundingStatus.IsStableOnGround) || _timeSinceLastAbleToJump <= JumpPostGroundingGraceTime)
                {
                    Vector3 jumpDirection = Motor.CharacterUp;
                    Motor.ForceUnground(0.1f);
                    
                    currentVelocity += (jumpDirection * JumpUpSpeed) - Vector3.Project(currentVelocity, Motor.CharacterUp);
                    currentVelocity += (m_moveInputVector * JumpScalableForwardSpeed);

                    _jumpStatus = JumpStatus.Executed;
                    _hasJumped = true;
                }
                else if (!Motor.GroundingStatus.IsStableOnGround && AllowDoubleJump)
                {
                    Motor.ForceUnground(0.1f);
                    currentVelocity += (Motor.CharacterUp * JumpUpSpeed) - Vector3.Project(currentVelocity, Motor.CharacterUp);
                    _jumpStatus = JumpStatus.DoubleJumpUsed;
                    _hasDoubleJump = true;
                }
            }

            _canWallJump = false;
        }
    }

    public void BeforeCharacterUpdate(float deltaTime)
    {
    }

    public void PostGroundingUpdate(float deltaTime)
    {
        // Handle landing and leaving ground
        if (Motor.GroundingStatus.IsStableOnGround && !Motor.LastGroundingStatus.IsStableOnGround)
        {
            OnLanded();
        }
        else if (!Motor.GroundingStatus.IsStableOnGround && Motor.LastGroundingStatus.IsStableOnGround)
        {
            OnLeaveStableGround();
        }
    }

    protected void OnLanded()
    {
        _hasJumped = false;
        _hasDoubleJump = false;
        _hasWallJumped = false;
        _jumpStatus = JumpStatus.None;
        _timeSinceJumpRequested = 0f;
        _landingTime = Time.time;
    }

    protected void OnLeaveStableGround()
    {

    }


    public void AfterCharacterUpdate(float deltaTime)
    {
        {
            _timeSinceJumpRequested += deltaTime;

            if (_jumpStatus == JumpStatus.Requested && _timeSinceJumpRequested > JumpPreGroundingGraceTime)
            {
                _jumpStatus = JumpStatus.None;
            }

            if (AllowJumpingWhenSliding ? Motor.GroundingStatus.FoundAnyGround : Motor.GroundingStatus.IsStableOnGround)
            {
                _timeSinceLastAbleToJump = 0f;
            }
            else
            {
                _timeSinceLastAbleToJump += deltaTime;
            }
        }
    }

    public bool IsColliderValidForCollisions(Collider coll)
    {
        return true;
    }

    public void OnGroundHit(Collider hitCollider, Vector3 hitNormal, Vector3 hitPoint, ref HitStabilityReport hitStabilityReport)
    {
    }

    public void OnMovementHit(Collider hitCollider, Vector3 hitNormal, Vector3 hitPoint,
        ref HitStabilityReport hitStabilityReport)
    {
        if (AllowWallJump && !Motor.GroundingStatus.IsStableOnGround && !hitStabilityReport.IsStable)
        {
            _canWallJump = true;
            _hasWallJumped = false;
            _wallJumpNormal = hitNormal;
        }
    }

    public void ProcessHitStabilityReport(Collider hitCollider, Vector3 hitNormal, Vector3 hitPoint, Vector3 atCharacterPosition,
        Quaternion atCharacterRotation, ref HitStabilityReport hitStabilityReport)
    {
    }

    public void OnDiscreteCollisionDetected(Collider hitCollider)
    {
    }
}
