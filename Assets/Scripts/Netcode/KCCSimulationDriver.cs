using KinematicCharacterController;
using UnityEngine;

public class KCCSimulationDriver : MonoBehaviour
{
    private void Awake()
    {

        // Make sure the KCC system exists before accessing its settings.
        //KinematicCharacterSystem.EnsureCreation();

        // Disable KCC automatic simulation.
        // Our code will manually control when the KCC world is simulated.
        KinematicCharacterSystem.Settings.AutoSimulation = false;
        KinematicCharacterSystem.Settings.Interpolate = false;
    }

    private void FixedUpdate()
    {
        // Manually simulate all registered KCC character motors
        // and physics movers once per frame.
        KinematicCharacterSystem.Simulate(
            Time.deltaTime,
            KinematicCharacterSystem.CharacterMotors,
            KinematicCharacterSystem.PhysicsMovers
        );
    }
}
