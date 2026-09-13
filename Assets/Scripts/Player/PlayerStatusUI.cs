using System;
using TMPro;
using UnityEngine;

public class PlayerStatusUI : MonoBehaviour
{
    [SerializeField] private PlayerCharacter playerCharacter;
    [SerializeField] private Player player;
    [SerializeField] private TMP_Text infoText;

    private void Update()
    {
        UpdatePlayerInfo();
    }

    private void UpdatePlayerInfo()
    {
        // Null Check
        if (playerCharacter == null || player == null || infoText == null)
        {
            return;
        }

        // Movement Information
        bool grounded = playerCharacter.Status.Grounded;
        var state = playerCharacter.Status.State;
        Vector3 velocity = playerCharacter.Status.Velocity;
        float speed = velocity.magnitude;

        bool ElectricField = playerCharacter.GetActiveCharge();
        bool IsPositive = playerCharacter.IsPositiveCharge();

        // Prediction Information
        bool predictionPaused = player.IsPredictionPaused;
        string predictionState = predictionPaused ? "Paused" : "Active";

        // Display
        infoText.text =
            $"Grounded: {grounded}\n" +
            $"Stance: {state}\n" +
            $"Velocity: {velocity:F2}\n" +
            $"Speed: {speed:F2}\n" +
            $"\n" +
            $"Field Active: {ElectricField}\n" +
            $"Positive Field: {IsPositive}\n" +
            $"\n" +
            $"Prediction: {predictionState}";
    }
}

