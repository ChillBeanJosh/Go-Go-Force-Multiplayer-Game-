using KinematicCharacterController;
using System.Collections.Generic;
using UnityEngine;

public class KCCSimulationDriver : MonoBehaviour
{
    private static readonly List<Player> _players = new();

    private void Awake()
    {
        KinematicCharacterSystem.EnsureCreation();

        // Disable KCC automatic simulation.
        // Our code will manually control when the KCC world is simulated.
        KinematicCharacterSystem.Settings.AutoSimulation = false;
        KinematicCharacterSystem.Settings.Interpolate = false;
    }


    public static void RegisterPlayer(Player player)
    {
        if (!_players.Contains(player))
        {
            _players.Add(player);
        }
    }

    public static void UnregisterPlayer(Player player)
    {
        _players.Remove(player);
    }


    private void FixedUpdate()
    {

        // Provide each player with the input that belongs
        // to this simulation step.
        for (int i = 0; i < _players.Count; i++)
        {
            _players[i].ApplySimulationInput();
        }


        // Manually simulate all registered KCC character motors
        // and physics movers once per frame.
        KinematicCharacterSystem.Simulate(
            Time.fixedDeltaTime,
            KinematicCharacterSystem.CharacterMotors,
            KinematicCharacterSystem.PhysicsMovers
        );
    }
}
