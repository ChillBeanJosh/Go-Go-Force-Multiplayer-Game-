using KinematicCharacterController;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

public class KCCSimulationDriver : MonoBehaviour
{
    private static readonly List<Player> _players = new();

    //KCC Motors Used For Local Client Prediction:
    private static readonly List<KinematicCharacterMotor> _predictedMotors = new(1);

    //Authoritative Simulation Tick Used By The Server:
    private int _serverSimulationTick;


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


        //Server:
        if (NetworkManager.Singleton.IsServer)
        {
            //Advance The Authoritative Server Simulation Tick:
            _serverSimulationTick++;


            //Process Each Player's Input For This Server Simulation Tick:
            for (int i = 0; i < _players.Count; i++)
            {
                _players[i].ApplySimulationInput(_serverSimulationTick);
            }


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
                _players[i].PublishServerState(_serverSimulationTick);
            }
        }
        //Client:
        else
        {
            //Apply The Latest Character State Received From The Server
            //Before Running The Next Prediction Step.
            for (int i = 0; i < _players.Count; i++)
            {
                _players[i].ApplyNetworkState();
            }


            //Process Local Player Prediction Input:
            for (int i = 0; i < _players.Count; i++)
            {
                if (_players[i].IsOwner)
                {
                    _players[i].ApplySimulationInput(0);
                    break;
                }
            }


            _predictedMotors.Clear();


            //Find The Locally Owned Player For Client Prediction:
            for (int i = 0; i < _players.Count; i++)
            {
                if (_players[i].IsOwner &&
                    !_players[i].IsPredictionPaused)
                {
                    _predictedMotors.Add(_players[i].GetMotor());
                    break;
                }
            }


            //Simulate Only The Locally Owned KCC Character:
            if (_predictedMotors.Count > 0)
            {
                KinematicCharacterSystem.Simulate
                (
                    Time.fixedDeltaTime,
                    _predictedMotors,
                    KinematicCharacterSystem.PhysicsMovers
                );


                //Save The Predicted State After This Simulation Tick:
                for (int i = 0; i < _players.Count; i++)
                {
                    if (_players[i].IsOwner)
                    {
                        _players[i].SavePredictionState();
                        break;
                    }
                }
            }
        }
    }
}
