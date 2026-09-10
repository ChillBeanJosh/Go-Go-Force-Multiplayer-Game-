using KinematicCharacterController;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

public class KCCSimulationDriver : MonoBehaviour
{
    private static readonly List<Player> _players = new();

    private void Awake()
    {
        //Ensure System is created before Host Connects To Avoid Null Ref:
        KinematicCharacterSystem.EnsureCreation();

        //Disable Automatic KCC Simulation So Network Code Controls When Simulation Runs:
        KinematicCharacterSystem.Settings.AutoSimulation = false;
        KinematicCharacterSystem.Settings.Interpolate = false;
    }


    //Called When Player Is Spawned On Network, Adding Them To Player List:
    public static void RegisterPlayer(Player player)
    {
        if (!_players.Contains(player))
        {
            _players.Add(player);
        }
    }

    //Called When Player Is Despawned From Network, Removing Them From Player List:
    public static void UnregisterPlayer(Player player)
    {
        _players.Remove(player);
    }


    private void FixedUpdate()
    {
        if (NetworkManager.Singleton == null) return;
        if (!NetworkManager.Singleton.IsListening) return;

        //Process Each Player's Input For The Current Simulation Tick:
        for (int i = 0; i < _players.Count; i++)
        {
            _players[i].ApplySimulationInput();
        }

      
        //Server Update:
        if (NetworkManager.Singleton.IsServer)
        {
            //Manually Simulate All KCC Characters Using The Fixed Timestep:
            KinematicCharacterSystem.Simulate
            (
                Time.fixedDeltaTime,
                KinematicCharacterSystem.CharacterMotors,
                KinematicCharacterSystem.PhysicsMovers
            );


            //Send The Resulting Authoritative State To Clients:
            for (int i = 0; i < _players.Count; i++)
            {
                _players[i].PublishServerState();
            }
        }
        //Client Update:
        else
        {
            //Apply The Latest Character State Received From The Server:
            for (int i = 0; i < _players.Count; i++)
            {
                _players[i].ApplyNetworkState();
            }
        }
    }
}
