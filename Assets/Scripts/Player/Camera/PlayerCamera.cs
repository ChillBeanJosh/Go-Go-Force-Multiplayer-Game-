using UnityEngine;

public struct CameraInput
{
    public Vector2 Look;
}

public class PlayerCamera : MonoBehaviour
{
    private Vector3 _eulerAngles;
    [SerializeField][Range(0.01f, 1f)] private float sensitivityX = 0.1f;
    [SerializeField][Range(0.01f, 1f)] private float sensitivityY = 0.1f;


    public void Initialize(Transform target)
    {
        transform.position = target.position;
        transform.eulerAngles = _eulerAngles = target.eulerAngles;
    }

    public void UpdateRotation(CameraInput input)
    {
        //Update Camera Rotation Based On Input:
        _eulerAngles.x += -input.Look.y * sensitivityY;
        _eulerAngles.y += input.Look.x * sensitivityX;

        //Clamp Vertical Rotation To Prevent Camera From Flipping:
        _eulerAngles.x = Mathf.Clamp(_eulerAngles.x, -89f, 89f);

        //Apply Rotation To Camera:
        transform.eulerAngles = _eulerAngles;
    }

    public void UpdatePosition(Transform target)
    {
        //Update Camera Position To Follow Target:
        transform.position = target.position;
    }
}
