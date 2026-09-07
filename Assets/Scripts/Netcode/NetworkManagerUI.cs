using UnityEngine;
using Unity.Netcode;
using UnityEngine.UI;

public class NetworkManagerUI : MonoBehaviour
{
    public GameObject startHostButton;
    public GameObject startServerButton;
    public GameObject startClientButton;


    private void Start()
    {
        if (startHostButton != null)
        {
            startHostButton.GetComponent<Button>().onClick.AddListener(StartHost);
        }

        if (startServerButton != null)
        {
            startServerButton.GetComponent<Button>().onClick.AddListener(StartServer);
        }

        if (startClientButton != null)
        {
            startClientButton.GetComponent<Button>().onClick.AddListener(StartClient);
        }
    }

    private void StartHost()
    {
        NetworkManager.Singleton.StartHost();
        ChangeButtonColor(startHostButton, Color.green);
        DisableOtherButtons(startHostButton);
    }

    private void StartServer()
    {
        NetworkManager.Singleton.StartServer();
        ChangeButtonColor(startServerButton, Color.green);
        DisableOtherButtons(startServerButton);
    }

    private void StartClient()
    {
        NetworkManager.Singleton.StartClient();
        ChangeButtonColor(startClientButton, Color.green);
        DisableOtherButtons(startClientButton);
    }

    private void ChangeButtonColor(GameObject buttonObject, Color color)
    {
       if (buttonObject != null)
        {
            Button button = buttonObject.GetComponent<Button>();
            if (button != null)
            {
                // Change the button's color
                ColorBlock colors = button.colors;
                colors.normalColor = color;
                colors.highlightedColor = color;
                colors.pressedColor = color;
                colors.selectedColor = color;
                colors.disabledColor = color;
                button.colors = colors;
            }
        }
    }

    private void DisableOtherButtons(GameObject activeButton)
    {
        if (activeButton != startHostButton && startHostButton != null)
        {
            startHostButton.GetComponent<Button>().interactable = false;
        }

        if (activeButton != startServerButton && startServerButton != null)
        {
            startServerButton.GetComponent<Button>().interactable = false;
        }

        if (activeButton != startClientButton && startClientButton != null)
        {
            startClientButton.GetComponent<Button>().interactable = false;
        }
    }
}
