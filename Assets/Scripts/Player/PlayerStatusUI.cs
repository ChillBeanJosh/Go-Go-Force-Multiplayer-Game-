using System;
using TMPro;
using UnityEngine;

public class PlayerStatusUI : MonoBehaviour
{
    [SerializeField] private PlayerCharacter playerCharacter;
    [SerializeField] private TMP_Text infoText;

    void Update()
    {
        //Null Check:
        if (playerCharacter == null || infoText == null) return;


        var grounded = playerCharacter.Status.Grounded;
        var state = playerCharacter.Status.State;
        var velocity = playerCharacter.Status.Velocity;
        var speed = velocity.magnitude;

        infoText.text = $"Grounded: {grounded}\n" + $"Stance: {state}\n" + $"Velocity: {velocity:F2}\n" + $"Speed: {speed:F2}\n";
    }
}

