using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Netcode;

public class InputManager : NetworkBehaviour
{
    [SerializeField] private CharacterCamera Camera;
    [SerializeField] private LocomotionController Character;
    [SerializeField] private Transform CameraFollowPoint;


    private Vector2 m_moveInput;
    private Vector2 m_lookInput;
    private Vector3 m_lookInputVector = Vector3.zero;

    private bool m_jumpDown;
    private bool m_jumpHeld;

    public override void OnNetworkSpawn()
    {
        if (!IsOwner) return;

        Camera.SetFollowTransform(CameraFollowPoint);
        Camera.IgnoredColliders.Clear();
        Camera.IgnoredColliders.AddRange(Character.GetComponents<Collider>());
    }

    private void Update()
    {
        if (!IsOwner) return;
        HandleNonMobileInput();
    }

    private void LateUpdate()
    {
        if (!IsOwner) return;
        HandleCameraInput();
        HandleCharacterInput();
    }

    private void HandleNonMobileInput()
    {
        if(Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
        else if(Mouse.current != null && (Mouse.current.leftButton.wasPressedThisFrame || Mouse.current.rightButton.wasPressedThisFrame))
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }

    private void HandleCameraInput()
    {
        float mouseLookAxisUp = m_lookInput.y;
        float mouseLookAxisRight = m_lookInput.x;
        m_lookInputVector = new Vector3(mouseLookAxisRight, mouseLookAxisUp, 0f);

        //Prevent The Camera From Rotating When The Cursor Is Unlocked And There Is No Gamepad Connected:
        if (Cursor.lockState != CursorLockMode.Locked && Gamepad.current == null)
        {
            m_lookInputVector = Vector3.zero;
        }

        float scrollInput = 0f;
        Camera.UpdateWithInput(Time.deltaTime, scrollInput, m_lookInputVector);
    }

    private void HandleCharacterInput()
    {
        PlayerCharacterInputs characterInputs = new PlayerCharacterInputs();
        characterInputs.MoveAxisForward = m_moveInput.y;
        characterInputs.MoveAxisRight = m_moveInput.x;
        characterInputs.CameraRotation = Camera.transform.rotation;
        characterInputs.JumpDown = m_jumpDown;
        characterInputs.JumpHeld = m_jumpHeld;

        Character.SetInputs(ref characterInputs);
        m_jumpDown = false;
    }
    public void OnLook(InputAction.CallbackContext context)
    {
        m_lookInput = context.ReadValue<Vector2>();
    }

    public void OnMove(InputAction.CallbackContext context)
    {
        m_moveInput = context.ReadValue<Vector2>();
    }

    public void OnJump(InputAction.CallbackContext context)
    {
        switch (context.phase)
        {
            case InputActionPhase.Started:
                m_jumpDown = true ;
                break;

            case InputActionPhase.Performed:
                m_jumpHeld = true;
                break;

            case InputActionPhase.Canceled:
                m_jumpHeld = false;
                break;

        }
    }
}
