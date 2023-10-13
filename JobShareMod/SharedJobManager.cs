using System;
using System.Collections.Generic;
using DV;
using DV.Logic.Job;
using DV.ThingTypes;
using DV.ThingTypes.TransitionHelpers;
using DV.Utils;
using HarmonyLib;
using UnityEngine;

namespace JobShareMod
{
    class StationPaylodJobsController
    {
        private StationController stationController;

        public StationPaylodJobsController(StationController stationController)
        {
            this.stationController = stationController;
        }

        public JobChainController MakeJobController(string name)
        {
            GameObject gameObject = new GameObject(name);
            gameObject.transform.SetParent(stationController.transform);

            JobChainController jobChainController = new JobChainController(gameObject);

            return jobChainController;
        }

        public void SpawnJobController(JobChainController jobChainController, StaticJobDefinition definition)
        {
            jobChainController.AddJobDefinitionToChain(definition);
            jobChainController.FinalizeSetupAndGenerateFirstJob();
            FileLog.Log($"Job generation complete");
        }

        public StaticJobDefinition CreateJobController(JobPayload payload, JobChainController controller)
        {
            StationController targetStation = SingletonBehaviour<LogicController>.Instance.YardIdToStationController[payload.TargetStation];
            if (targetStation == null)
            {
                FileLog.Log($"unable to find targetStation: {payload.TargetStation}");
                return null;
            }

            RailTrack startingRailTrack = null;
            foreach (RailTrack rt in stationController.AllStationTracks)
            {
                if (rt.logicTrack.ID.FullID == payload.StartingTrack)
                {
                    startingRailTrack = rt;
                }
            }

            Track targetTrack = null;
            foreach (RailTrack rt in targetStation.AllStationTracks)
            {
                if (rt.logicTrack.ID.FullID == payload.TargetTrack)
                {
                    targetTrack = rt.logicTrack;
                }
            }
            if (targetTrack == null)
            {
                FileLog.Log($"unable to find targetTrack: {payload.TargetTrack}");
                return null;
            }

            // convert cargo type
            CargoType cType = (CargoType)Enum.Parse(typeof(CargoType), payload.CargoType);

            StationsChainData chainData = new StationsChainData(stationController.stationInfo.YardID, targetStation.stationInfo.YardID);

            List<TrainCar> trainCars = SpawnJobCars(payload.CarCount, cType, startingRailTrack);
            List<Car> cars = new List<Car>();
            List<CargoType> cargoTypesPerCar = new List<CargoType>();
            List<float> cargoAmountPerCar = new List<float>();
            float totalCargoAmount = 0f;
            for (int i = 0; i < trainCars.Count; i++)
            {
                TrainCar trainCar = trainCars[i];
                cars.Add(trainCar.logicCar);
                cargoTypesPerCar.Add(cType);

                totalCargoAmount += trainCar.cargoCapacity;

                if (payload.Type == "freight" || payload.Type == "shunting_unload")
                {
                    cargoAmountPerCar.Add(trainCar.cargoCapacity);
                    trainCar.logicCar.LoadCargo(trainCar.cargoCapacity, cType);
                }
                else
                {
                    cargoAmountPerCar.Add(0f);
                }
            }

            StaticJobDefinition definition = null;
            switch (payload.Type)
            {
                case "logistics":
                    StaticEmptyHaulJobDefinition emptyHaulDefinition = controller.jobChainGO.AddComponent<StaticEmptyHaulJobDefinition>();
                    emptyHaulDefinition.PopulateBaseJobDefinition(stationController.logicStation, 0, payload.Wage, chainData, JobLicenses.LogisticalHaul);
                    emptyHaulDefinition.startingTrack = startingRailTrack.logicTrack;
                    emptyHaulDefinition.destinationTrack = targetTrack;
                    controller.trainCarsForJobChain = trainCars;
                    emptyHaulDefinition.trainCarsToTransport = cars;

                    definition = emptyHaulDefinition;
                    break;

                case "freight":
                    StaticTransportJobDefinition transportDefinition = controller.jobChainGO.AddComponent<StaticTransportJobDefinition>();
                    transportDefinition.PopulateBaseJobDefinition(stationController.logicStation, 0, payload.Wage, chainData, JobLicenses.LogisticalHaul);
                    transportDefinition.startingTrack = startingRailTrack.logicTrack;
                    transportDefinition.destinationTrack = targetTrack;
                    controller.trainCarsForJobChain = trainCars;
                    transportDefinition.trainCarsToTransport = cars;
                    transportDefinition.cargoAmountPerCar = cargoAmountPerCar;
                    transportDefinition.transportedCargoPerCar = cargoTypesPerCar;

                    definition = transportDefinition;
                    break;

                case "shunting_load":
                    controller.trainCarsForJobChain = trainCars;

                    StaticShuntingLoadJobDefinition shuntingDefinition = controller.jobChainGO.AddComponent<StaticShuntingLoadJobDefinition>();
                    shuntingDefinition.PopulateBaseJobDefinition(stationController.logicStation, 0, payload.Wage, chainData, JobLicenses.Shunting);
                    shuntingDefinition.destinationTrack = targetTrack;
                    shuntingDefinition.loadMachine = getMatchingWarehouse(stationController.warehouseMachineControllers, cType);
                    shuntingDefinition.loadData = new List<CarsPerCargoType>
                    {
                        new CarsPerCargoType(cType, cars, totalCargoAmount)
                    };

                    List<CarsPerTrack> sources = new List<CarsPerTrack>
                    {
                        new CarsPerTrack(startingRailTrack.logicTrack, cars)
                    };
                    shuntingDefinition.carsPerStartingTrack = sources;

                    definition = shuntingDefinition;
                    break;

                case "shunting_unload":
                    controller.trainCarsForJobChain = trainCars;

                    StaticShuntingUnloadJobDefinition unshuntingDefinition = controller.jobChainGO.AddComponent<StaticShuntingUnloadJobDefinition>();
                    unshuntingDefinition.PopulateBaseJobDefinition(stationController.logicStation, 0, payload.Wage, chainData, JobLicenses.Shunting);
                    unshuntingDefinition.startingTrack = startingRailTrack.logicTrack;
                    unshuntingDefinition.unloadMachine = getMatchingWarehouse(stationController.warehouseMachineControllers, cType);
                    unshuntingDefinition.unloadData = new List<CarsPerCargoType>
                    {
                        new CarsPerCargoType(cType, cars, totalCargoAmount)
                    };

                    unshuntingDefinition.carsPerDestinationTrack = new List<CarsPerTrack>
                    {
                        new CarsPerTrack(targetTrack, cars)
                    };

                    definition = unshuntingDefinition;
                    break;

                default:
                    return null;
            }

            definition.ForceJobId(payload.ID);

            return definition;
        }

        public static List<TrainCar> SpawnJobCars(int carCount, CargoType cType, RailTrack spawnTrack)
        {
            FileLog.Log($"try car spawn for cargo: {cType} as v2 {cType.ToV2()}");
            List<TrainCarType_v2> listOfTrainCarTypes = Globals.G.Types.CargoToLoadableCarTypes[cType.ToV2()];

            List<TrainCarLivery> liveryData = new List<TrainCarLivery>();
            for (int i = 0; i < carCount; i++)
            {
                liveryData.Add(listOfTrainCarTypes[0].liveries[0]);
            }

            CarSpawner.SpawnData carSpawnData = CarSpawner.GetTrackMiddleBasedSpawnDataRandomOrientation(liveryData, spawnTrack);
            FileLog.Log($"car spawn result: {carSpawnData.result}");

            return SingletonBehaviour<CarSpawner>.Instance.SpawnCars(carSpawnData, true, true);
        }

        private WarehouseMachine getMatchingWarehouse(List<WarehouseMachineController> machineControllers, CargoType cType)
        {
            foreach(WarehouseMachineController machineCntrl in machineControllers)
            {
                if (machineCntrl.warehouseMachine.IsCargoSupported(cType))
                    return machineCntrl.warehouseMachine;
            }

            return null;
        }
    }

    [HarmonyPatch(typeof(IdGenerator), nameof(IdGenerator.GenerateJobID))]
    static class IdGenerator_GenerateJobID_Patch
    {
        static void Postfix(ref string __result, JobType jobType, StationsChainData jobStationsInfo = null) 
        {
            string[] parts = __result.Split('-');
            parts[0] = "FAILED-";
            __result = String.Join("-", parts);
        }
    }
}