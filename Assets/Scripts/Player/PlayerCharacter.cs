using KinematicCharacterController;
using Unity.Netcode;
using UnityEngine;

public enum CrouchInput
{
    None,
    Toggle,
    Hold
}

public enum State
{
    Stand,
    Crouch,
    Slide
}

public struct CharacterStatus : INetworkSerializable
{
    public bool Grounded;
    public State State;
    public Vector3 Velocity;
    public Vector3 Acceleration;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer)
        where T : IReaderWriter
    {
        serializer.SerializeValue(ref Grounded);
        serializer.SerializeValue(ref State);
        serializer.SerializeValue(ref Velocity);
        serializer.SerializeValue(ref Acceleration);
    }
}

public struct PlayerInput
{
    public Quaternion Rotation;
    public Vector2 Move;
    public bool Jump;
    public bool JumpSustain;
    public CrouchInput Crouch;
}

public struct PlayerCharacterState
{
    public KinematicCharacterMotorState MotorState;

    public CharacterStatus Status;
    public CharacterStatus LastStatus;

    public Quaternion RequestedRotation;
    public Vector3 RequestedMovement;

    public bool RequestedJump;
    public bool RequestedSustainedJump;
    public bool RequestedCrouch;
    public bool RequestedCrouchInAir;

    public float TimeSinceUngrounded;
    public float TimeSinceJumpRequested;

    public bool UngroundedDueToJump;
}

public class PlayerCharacter : MonoBehaviour, ICharacterController
{
    [Header("Debug Tools: ")]
    [SerializeField] private int debugRayLength;
    [Space]

    [Header("External References: ")]
    [SerializeField] private KinematicCharacterMotor motor;
    [SerializeField] private Transform root;
    [SerializeField] private Transform cameraTarget;
    [Space]

    [Header("Movement Speed Parameters: ")]
    [SerializeField] private float walkSpeed ;
    [SerializeField] private float walkResponse;
    [Space]
    [SerializeField] private float crouchSpeed;
    [SerializeField] private float crouchResponse;
    [Space] 
    [SerializeField] private float slideStartSpeed;
    [SerializeField] private float slideEndSpeed;
    [SerializeField] private float slideFriction;
    [SerializeField] private float slideSteerAcceleration;
    [SerializeField] private float slideGravity;
    [Space]

    [Header("Airborne Parameters: ")]
    [SerializeField] private float jumpSpeed;

    [SerializeField] private float coyoteTime;
    [Space]
    [SerializeField][Range(0f, 1f)] private float jumpSustainGravity;
    [SerializeField] private float gravity;
    [Space] 
    [SerializeField] private float airSpeed;
    [SerializeField] private float airAcceleration;
    [Space]

    [Header("Height Parameters: ")]
    [SerializeField] private float standHeight;
    [SerializeField][Range(0f, 1f)] float standCameraHeight;
    [Space]
    [SerializeField] private float crouchHeight;
    [SerializeField][Range(0f, 1f)] private float crouchCameraHeight;
    [Space] 
    [SerializeField] private float crouchHeightResponse;
    [Space]
    [SerializeField] private float cameraPositionResponse = 20f;

    private CharacterStatus _status;
    public CharacterStatus Status => _status;
    private CharacterStatus _lastStatus;
    private CharacterStatus _tempStatus;


    private Collider[] _uncrouchOverlapResults;

    //Player Input Variables:
    private Quaternion _requestedRotation;
    private Vector3 _requestedMovement;
    private bool _requestedJump;
    private bool _requestedSustainedJump;
    private bool _requestedCrouch;
    private bool _requestedCrouchInAir;
    private float _timeSinceUngrounded;
    private float _timeSinceJumpRequested;
    private bool _ungroundedDueToJump;


    private Vector3 _visualCameraPosition;
    private bool _visualCameraInitialized;
    private Renderer[] _playerRenderers;


    public void Initialize()
    {
        _status.State = State.Stand;
        _lastStatus = _status;
        _uncrouchOverlapResults = new Collider[8];
        _playerRenderers = root.GetComponentsInChildren<Renderer>();
        motor.CharacterController = this;
    }

    public void UpdateInputs(PlayerInput input)
    {
        _requestedRotation = input.Rotation;


        //1.) Convert 2D Input to 3D Movement:
        _requestedMovement = new Vector3(input.Move.x, 0f, input.Move.y);
        //2.) Clamp The Magnitude of the Movement Vector to 1 to Avoid Faster Diagonal Movement:
        _requestedMovement = Vector3.ClampMagnitude(_requestedMovement, 1f);
        //3.) Orientate the Movement Vector to the Player's Forward Direction:
        _requestedMovement = input.Rotation * _requestedMovement;

        var wasRequestingJump = _requestedJump;
        _requestedJump = _requestedJump || input.Jump;
        if (_requestedJump && !wasRequestingJump) _timeSinceJumpRequested = 0f;
        _requestedSustainedJump = input.JumpSustain;

        var wasRequestingCrouch = _requestedCrouch;
        _requestedCrouch = input.Crouch switch
        {
            CrouchInput.None => _requestedCrouch,
            CrouchInput.Toggle => !_requestedCrouch,
            CrouchInput.Hold => _requestedCrouch,
        };

        if (_requestedCrouch && !wasRequestingCrouch) _requestedCrouchInAir = !_status.Grounded;
        else if (!_requestedCrouch && wasRequestingCrouch) _requestedCrouchInAir = false;
    }

    public void UpdateBody(float deltaTime)
    {
        UpdateCameraTarget(deltaTime);
        UpdateMesh(deltaTime);
    }

    //-----------------------------------------------------------------------------------------------------------
    // [KCC CHARACTER METHODS]

    public void UpdateVelocity(ref Vector3 currentVelocity, float deltaTime)
    {
        _status.Acceleration = Vector3.zero;

        //Grounded State:
        if (motor.GroundingStatus.IsStableOnGround)
        {
            _timeSinceUngrounded = 0f;
            _ungroundedDueToJump = false;

            //Flattens Out _requestedMovement Vector On Sloped Surfaces:
            var groundedMovement = motor.GetDirectionTangentToSurface
            (
                direction: _requestedMovement,
                surfaceNormal: motor.GroundingStatus.GroundNormal
            ) * _requestedMovement.magnitude;

            //Grounded Sliding Movement:
            {
                var moving = groundedMovement.sqrMagnitude > 0f;
                var crouching = _status.State is State.Crouch;
                var wasStanding = _lastStatus.State is State.Stand;
                var wasInAir = !_lastStatus.Grounded;
                if (moving && crouching && (wasStanding || wasInAir))
                {
                    _status.State = State.Slide;

                    if (wasInAir)
                    {
                        currentVelocity = Vector3.ProjectOnPlane
                        (
                            vector: _lastStatus.Velocity,
                            planeNormal: motor.GroundingStatus.GroundNormal
                        );
                    }

                    var effectiveSlideStartSpeed = slideStartSpeed;
                    if (!_lastStatus.Grounded && !_requestedCrouchInAir)
                    {
                        effectiveSlideStartSpeed = 0f;
                        _requestedCrouchInAir = false;
                    }
                    var slideSpeed = Mathf.Max(effectiveSlideStartSpeed, currentVelocity.magnitude);
                    currentVelocity = motor.GetDirectionTangentToSurface
                    (
                        direction: currentVelocity,
                        surfaceNormal: motor.GroundingStatus.GroundNormal
                    ) * slideSpeed;
                }
            }

            //Grounded Walking Movement:
            if (_status.State is State.Stand or State.Crouch)
            {
                var speed = _status.State is State.Stand ? walkSpeed : crouchSpeed;
                var response = _status.State is State.Stand ? walkResponse : crouchResponse;

                //Lerp Player Speed:
                var targetVelocity = groundedMovement * speed;
                var moveVelocity = Vector3.Lerp
                (
                    a: currentVelocity,
                    b: targetVelocity,
                    t: 1f - Mathf.Exp(-response * deltaTime)
                );

                _status.Acceleration = (moveVelocity - currentVelocity) / deltaTime;
                currentVelocity = moveVelocity;
            }
            //Slide State:
            else
            {
                //Friction:
                currentVelocity -= currentVelocity * (slideFriction * deltaTime);

                //Slope:
                {
                    var force = Vector3.ProjectOnPlane
                    (
                        vector: -motor.CharacterUp,
                        planeNormal: motor.GroundingStatus.GroundNormal
                    ) * slideGravity;
                    currentVelocity -= force * deltaTime;
                }

                //Steering:
                {
                    var currentSpeed = currentVelocity.magnitude;
                    var targetVelocity = groundedMovement * currentSpeed;
                    var steerVelocity = currentVelocity;

                    var steerForce = (targetVelocity - steerVelocity) * slideSteerAcceleration * deltaTime;
                    steerVelocity += steerForce;
                    steerVelocity = Vector3.ClampMagnitude(steerVelocity, currentSpeed);

                    _status.Acceleration = (steerVelocity - currentVelocity) / deltaTime;
                    currentVelocity = steerVelocity;
                }

                //End:
                if (currentVelocity.magnitude < slideEndSpeed) _status.State = State.Crouch;
            }
        }
        //Aerial State:
        else
        {
            _timeSinceUngrounded += deltaTime;

            if (_requestedMovement.sqrMagnitude > 0f)
            {
                var planarMovement = Vector3.ProjectOnPlane
                (
                    _requestedMovement,
                    motor.CharacterUp
                ) * _requestedMovement.magnitude;
                var movementForce = planarMovement * airAcceleration * deltaTime;


                var currentPlanarVelocity = Vector3.ProjectOnPlane
                (
                    currentVelocity, 
                    motor.CharacterUp
                );

                if (currentPlanarVelocity.magnitude < airSpeed)
                {
                    var targetPlanarVelocity = currentPlanarVelocity + movementForce;
                    targetPlanarVelocity = Vector3.ClampMagnitude(targetPlanarVelocity, airSpeed);

                    movementForce = targetPlanarVelocity - currentPlanarVelocity;
                }
                else if (Vector3.Dot(currentPlanarVelocity, movementForce) > 0f)
                {
                    var constrainedMovementForce = Vector3.ProjectOnPlane
                    (
                        movementForce,
                        currentPlanarVelocity.normalized
                    );

                    movementForce = constrainedMovementForce;
                }

                if (motor.GroundingStatus.FoundAnyGround)
                {
                    if (Vector3.Dot(movementForce, currentVelocity + movementForce) > 0f)
                    {
                        var obstructionNormal = Vector3.Cross
                        (
                            motor.CharacterUp,
                            Vector3.Cross
                            (
                                motor.CharacterUp,
                                motor.GroundingStatus.GroundNormal
                            )
                        ).normalized;

                        movementForce = Vector3.ProjectOnPlane
                        (
                            movementForce,
                            obstructionNormal
                        );
                    }
                }

                currentVelocity += movementForce;
                Debug.DrawRay(cameraTarget.position, currentPlanarVelocity, Color.purple);
            }


            //Constant Gravity:
            var effectiveGravity = gravity;
            var verticalSpeed = Vector3.Dot(currentVelocity, motor.CharacterUp);
            if (_requestedSustainedJump && verticalSpeed > 0f) effectiveGravity *= jumpSustainGravity;
            
            currentVelocity += motor.CharacterUp * effectiveGravity * deltaTime;
        }

        //Jump Input:
        if (_requestedJump)
        {
            var grounded = motor.GroundingStatus.IsStableOnGround;
            var canCoyoteJump = _timeSinceUngrounded < coyoteTime && !_ungroundedDueToJump;

            if (grounded || canCoyoteJump)
            {
                _requestedJump = false;
                _requestedCrouch = false;
                _requestedCrouchInAir = false;
                motor.ForceUnground(time: 0f);
                _ungroundedDueToJump = true;

                var currentVerticalSpeed = Vector3.Dot(currentVelocity, motor.CharacterUp);
                var targetVerticalSpeed = Mathf.Max(currentVerticalSpeed, jumpSpeed);
                currentVelocity += motor.CharacterUp * (targetVerticalSpeed - currentVerticalSpeed);
            }
            else
            {
                _timeSinceJumpRequested += deltaTime;

                var canJumpLater = _timeSinceJumpRequested < coyoteTime;
                _requestedJump = canJumpLater;
            }
            
        }

        Debug.DrawRay(cameraTarget.position, currentVelocity, Color.green);
    }


    public Vector3 GetVisualCameraPosition()
    {
        return _visualCameraPosition;
    }

    public void UpdateRotation(ref Quaternion currentRotation, float deltaTime)
    {
        var forward = Vector3.ProjectOnPlane
        (
            _requestedRotation * Vector3.forward,
            motor.CharacterUp                   
        );
        currentRotation = Quaternion.LookRotation(forward, motor.CharacterUp);
        Debug.DrawRay(cameraTarget.position, forward * debugRayLength, Color.red);
    }



    public void BeforeCharacterUpdate(float deltaTime)
    {
        _tempStatus = _status;

        //Crouch Input:
        if (_requestedCrouch && _status.State is State.Stand)
        {
            _status.State = State.Crouch;
            motor.SetCapsuleDimensions
            (
                radius: motor.Capsule.radius,
                height: crouchHeight,
                yOffset: crouchHeight * 0.5f
            );
        }
    }

    public void PostGroundingUpdate(float deltaTime)
    {
        if (!motor.GroundingStatus.IsStableOnGround && _status.State is State.Slide) _status.State = State.Crouch;
    }

    public void AfterCharacterUpdate(float deltaTime)
    {
        //Uncrouch Input:
        if (!_requestedCrouch && _status.State is not State.Stand)
        {
            motor.SetCapsuleDimensions
            (
                radius: motor.Capsule.radius,
                height: standHeight,
                yOffset: standHeight * 0.5f
            );

            //If There Are Any Overlaps When Capsule Uncrouch's --> Set Back to Crouch:
            //Else Stand Like Normal:
            var position = motor.TransientPosition;
            var rotation = motor.TransientRotation;
            var mask = motor.CollidableLayers;
            if (motor.CharacterOverlap(position, rotation, _uncrouchOverlapResults, mask, QueryTriggerInteraction.Ignore) > 0)
            {
                _requestedCrouch = true;
                motor.SetCapsuleDimensions
                (
                    radius: motor.Capsule.radius,
                    height: crouchHeight,
                    yOffset: crouchHeight * 0.5f
                );
            }
            else
            {
                _status.State = State.Stand;
            }
        }

        _status.Grounded = motor.GroundingStatus.IsStableOnGround;
        _status.Velocity = motor.Velocity;  
        _lastStatus = _tempStatus;
    }
    


    public void OnGroundHit(Collider hitCollider, Vector3 hitNormal, Vector3 hitPoint, ref HitStabilityReport hitStabilityReport)
    {
        
    }

    public void OnMovementHit(Collider hitCollider, Vector3 hitNormal, Vector3 hitPoint, ref HitStabilityReport hitStabilityReport)
    {
        
    }

    public bool IsColliderValidForCollisions(Collider coll)
    {
        return true;
    }
    public void OnDiscreteCollisionDetected(Collider hitCollider)
    {
       
    }

    public void ProcessHitStabilityReport(Collider hitCollider, Vector3 hitNormal, Vector3 hitPoint, Vector3 atCharacterPosition, Quaternion atCharacterRotation, ref HitStabilityReport hitStabilityReport)
    {
        
    }

    //-----------------------------------------------------------------------------------------------------------

    public void SetNetworkState(Vector3 position, Quaternion rotation, CharacterStatus status)
    {
        //Apply KCC Data:
        motor.SetPosition(position);
        motor.SetRotation(rotation);

        //Update Player Status:
        _status.Grounded = status.Grounded;
        _status.State = status.State;
        _status.Velocity = status.Velocity;
        _status.Acceleration = status.Acceleration;

        //Update Camera + Mesh Position:
        ApplyCapsuleState(status.State);
    }

    private void ApplyCapsuleState(State state)
    {
        if (state is State.Stand)
        {
            motor.SetCapsuleDimensions
            (
                radius: motor.Capsule.radius,
                height: standHeight,
                yOffset: standHeight * 0.5f
            );
        }
        else
        {
            motor.SetCapsuleDimensions
            (
                radius: motor.Capsule.radius,
                height: crouchHeight,
                yOffset: crouchHeight * 0.5f
            );
        }
    }

    public void SetPresentationState(Vector3 position, Quaternion rotation, CharacterStatus status)
    {
        Vector3 motorPosition = motor.TransientPosition;
        Quaternion motorRotation = motor.TransientRotation;

        //Visual offset from the authoritative KCC body:
        Vector3 positionOffset = Quaternion.Inverse(motorRotation) * (position - motorPosition);

        root.localPosition = positionOffset;

        Quaternion rotationOffset = Quaternion.Inverse(motorRotation) * rotation;

        root.localRotation = rotationOffset;

        _status = status;
    }

    public void UpdateCameraTarget(float deltaTime)
    {
        var currentHeight = motor.Capsule.height;

        var targetHeight = currentHeight * (_status.State is State.Stand ? standCameraHeight: crouchCameraHeight);

        var targetLocalPosition = new Vector3(0f, targetHeight, 0f);

        cameraTarget.localPosition = Vector3.Lerp
        (
            cameraTarget.localPosition,
            targetLocalPosition,
            1f - Mathf.Exp(-crouchHeightResponse * deltaTime)
        );

        //Initialize Visual Camera Position:
        if (!_visualCameraInitialized)
        {
            _visualCameraPosition = cameraTarget.position;
            _visualCameraInitialized = true;
        }

        //Smooth Camera Position Toward The Authoritative Camera Target:
        _visualCameraPosition = Vector3.Lerp(
            _visualCameraPosition,
            cameraTarget.position,
            1f - Mathf.Exp(-cameraPositionResponse * deltaTime)
        );
    }

    public void UpdateMesh(float deltaTime)
    {
        var currentHeight = motor.Capsule.height;
        var normalizedHeight = currentHeight / standHeight;

        var targetScale = new Vector3(1f, normalizedHeight, 1f);

        root.localScale = Vector3.Lerp
        (
            root.localScale,
            targetScale,
            1f - Mathf.Exp(-crouchHeightResponse * deltaTime)
        );
    }

    public void SetOwnerVisibility(bool visible)
    {
        for (int i = 0; i < _playerRenderers.Length; i++)
        {
            _playerRenderers[i].enabled = visible;
        }
    }

    public PlayerCharacterState GetPredictionState()
    {
        return new PlayerCharacterState
        {
            MotorState = motor.GetState(),

            Status = _status,
            LastStatus = _lastStatus,

            RequestedRotation = _requestedRotation,
            RequestedMovement = _requestedMovement,

            RequestedJump = _requestedJump,
            RequestedSustainedJump = _requestedSustainedJump,
            RequestedCrouch = _requestedCrouch,
            RequestedCrouchInAir = _requestedCrouchInAir,

            TimeSinceUngrounded = _timeSinceUngrounded,
            TimeSinceJumpRequested = _timeSinceJumpRequested,

            UngroundedDueToJump = _ungroundedDueToJump
        };
    }

    public void ApplyPredictionState(PlayerCharacterState state)
    {
        motor.ApplyState(state.MotorState);

        _status = state.Status;
        _lastStatus = state.LastStatus;

        _requestedRotation = state.RequestedRotation;
        _requestedMovement = state.RequestedMovement;

        _requestedJump = state.RequestedJump;
        _requestedSustainedJump = state.RequestedSustainedJump;
        _requestedCrouch = state.RequestedCrouch;
        _requestedCrouchInAir = state.RequestedCrouchInAir;

        _timeSinceUngrounded = state.TimeSinceUngrounded;
        _timeSinceJumpRequested = state.TimeSinceJumpRequested;

        _ungroundedDueToJump = state.UngroundedDueToJump;

        ApplyCapsuleState(_status.State);
    }

    //-----------------------------------------------------------------------------------------------------------
    // [PUBLIC ACCESS]
    public Transform GetCameraTarget() => cameraTarget;
    public CharacterStatus GetStatus() => _status;
    public CharacterStatus GetLastStatus() => _lastStatus;
    public Vector3 GetPosition() => motor.TransientPosition;
    public Quaternion GetRotation() => motor.TransientRotation;
    public KinematicCharacterMotor GetMotor() => motor;
}
