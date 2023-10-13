using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using ThreadTask = System.Threading.Tasks;
using Newtonsoft.Json;
using System.IO;
using DV.Utils;
using System.Collections;
using System.Threading;
using DV.Logic.Job;
using DV.InventorySystem;
using UnityEngine;
using DV.UserManagement;
using DV.ThingTypes;
using DV.RenderTextureSystem.BookletRender;
using DV.Booklets;
using Unity.Jobs;
using DV.Booklets.Rendered;

namespace JobShareMod
{
    [JsonObject(MemberSerialization.OptIn)]
    public class WebSocketMessage
    {
        [JsonProperty("station_id")]
        public string StationID { get; set; }
    }

    [JsonObject(MemberSerialization.OptIn)]
    public class WebSocketWelcomeMsg
    {
        [JsonProperty("username")]
        public string Username { get; set; }
    }

    [JsonObject(MemberSerialization.OptIn)]
    public class WebSocketSubMsg
    {
        [JsonProperty("station_id")]
        public string StationID { get; set; }

        [JsonProperty("unsub")]
        public bool UnSub { get; set; }
    }

    class StationSubData
    {
        public bool IsChanged { get; set; }

        public bool IsCloseToStation { get; set; }

        public StationSubData(bool isChanged = false, bool isCloseToStation = false)
        {
            IsChanged = isChanged;
            IsCloseToStation = isCloseToStation;
        }
    }

    [HarmonyPatch(typeof(JobSaveManager), "LoadJobSaveGameData")]
    static class JobSaveManagerPatch
    {
        static Mutex stationUpdateLock = new Mutex();
        static List<string> stationNamesToUpdate = new List<string>();

        // TODO: move this into thread proc just need to make sure dictionary is initalised properly then
        static Dictionary<string, StationSubData> stationSubs = new Dictionary<string, StationSubData>();

        public static ClientWebSocket clientWebSocket;

        static bool Prefix(JobSaveManager __instance)
        {
            FileLog.Log($"Job share hook on load");

            StationProceduralJobsController[] stationJobControllers = UnityEngine.Object.FindObjectsOfType<StationProceduralJobsController>();
            if (stationJobControllers != null)
            {
                foreach (StationProceduralJobsController jobStationController in stationJobControllers)
                {
                    string stationID = jobStationController.stationController.logicStation.ID;

                    StationJobGenerationRange stationRange = jobStationController.stationController.GetComponent<StationJobGenerationRange>();
                    if (stationRange == null)
                    {
                        FileLog.Log($"stationRange is null?!");
                        continue;
                    }

                    StationSubData subData = new StationSubData();
                    stationSubs.Add(stationID, subData);

                    if (!stationRange.IsPlayerInJobGenerationZone(stationRange.PlayerSqrDistanceFromStationCenter))
                    {
                        FileLog.Log($"Station {stationID} out of range");
                        continue;
                    }

                    FileLog.Log($"Job loading from savegame for sattion {stationID}");
                    subData.IsChanged = true;
                    subData.IsCloseToStation = true;
                    LoadAndCreateStationJobs(stationID, jobStationController.stationController);
                }
            }

            string baseWsUri = Main.baseURL.Replace("http://", "ws://").Replace("https://", "wss://");
            clientWebSocket = new ClientWebSocket();
            Uri wsUri = new Uri($"{baseWsUri}/ws");
            ThreadTask.Task connectTask = clientWebSocket.ConnectAsync(wsUri, default);
            connectTask.Wait();
            FileLog.Log($"connected WS to {baseWsUri}/ws");

            Thread wsReadThread = new Thread(new ThreadStart(ReadThreadProc));
            wsReadThread.Start();
            Thread wsWriteThread = new Thread(new ThreadStart(WriteThreadProc));
            wsWriteThread.Start();
            
            SingletonBehaviour<CoroutineManager>.Instance.Run(ProcessWebSocketMessage(stationJobControllers));

            return false;
        }

        static private IEnumerator ProcessWebSocketMessage(StationProceduralJobsController[] stationJobControllers)
        {
            yield return null;
            while (true)
            {
                stationUpdateLock.WaitOne();
                string userName = SingletonBehaviour<UserManager>.Instance.CurrentUser.Name;

                if (stationNamesToUpdate.Count > 0)
                {
                    foreach (string stationID in stationNamesToUpdate)
                    {
                        StationController updateStation = SingletonBehaviour<LogicController>.Instance.YardIdToStationController[stationID];
                        StationPaylodJobsController payloadController = new StationPaylodJobsController(updateStation);
                        List<JobPayload> payloads = ShareClient.Instance.GetStationJobs(stationID, userName);
                        List<JobChainController> jobChaninsToDelete = new List<JobChainController>();

                        JobsManager jobsManager = SingletonBehaviour<JobsManager>.Instance;
                        if (jobsManager == null)
                            throw new Exception("JobsManager is null");

                        foreach (JobChainController availableJobChain in updateStation.ProceduralJobsController.GetCurrentJobChains())
                        {
                            if (availableJobChain.currentJobInChain == null)
                            {
                                FileLog.Log($"job chain has no current job. skipping in check.");
                                continue;
                            }

                            JobPayload removeFromPayloads = null;
                            foreach (JobPayload payload in payloads)
                            {
                                if (payload.ID == availableJobChain.currentJobInChain.ID)
                                {
                                    removeFromPayloads = payload;

                                    StationController targetStation = SingletonBehaviour<LogicController>.Instance.YardIdToStationController[payload.TargetStation];
                                    Track targetTrack = null;
                                    foreach (RailTrack rt in targetStation.AllStationTracks)
                                    {
                                        if (rt.logicTrack.ID.FullID == payload.TargetTrack)
                                        {
                                            targetTrack = rt.logicTrack;
                                        }
                                    }

                                    if (targetTrack != null)
                                    {
                                        List<Task> tasks = availableJobChain.currentJobInChain.tasks;
                                        ChangeDesinationTrack(tasks[tasks.Count - 1], targetTrack);
                                    }

                                    if (availableJobChain.trainCarsForJobChain.Count != payload.CarCount)
                                    {
                                        if (payload.CarCount < availableJobChain.trainCarsForJobChain.Count)
                                            throw new Exception("car count on existing track can only grow but shrank");

                                        RailTrack startingRailTrack = null;
                                        foreach (RailTrack rt in updateStation.AllStationTracks)
                                        {
                                            if (rt.logicTrack.ID.FullID == payload.StartingTrack)
                                            {
                                                startingRailTrack = rt;
                                            }
                                        }

                                        // delete current train cars
                                        SingletonBehaviour<CarSpawner>.Instance.DeleteTrainCars(availableJobChain.trainCarsForJobChain);

                                        // convert cargo type
                                        CargoType cType = (CargoType)Enum.Parse(typeof(CargoType), payload.CargoType);

                                        List<TrainCar> trainCars = StationPaylodJobsController.SpawnJobCars(payload.CarCount, cType, startingRailTrack);
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
                                            cargoAmountPerCar.Add(0f);
                                        }

                                        // set train car ref on controller
                                        availableJobChain.trainCarsForJobChain = trainCars;

                                        // update car data and acargo amount in all tasks
                                        List<Task> tasks = availableJobChain.currentJobInChain.tasks;
                                        ChangeJobCars(tasks, cars, cargoTypesPerCar, totalCargoAmount);

                                        UpdateJobOverview(updateStation, availableJobChain.currentJobInChain);
                                    }
                                }
                            }

                            if (removeFromPayloads != null)
                            {
                                FileLog.Log($"keep {removeFromPayloads.ID} around");
                                payloads.Remove(removeFromPayloads);

                                // TODO: make sure job target station has not changed!!

                            }
                            else
                            {
                                bool isActivePlayerJob = false;
                                foreach (Job j in jobsManager.currentJobs)
                                {
                                    if (j.ID == availableJobChain.currentJobInChain.ID)
                                    {
                                        FileLog.Log($"{j.ID} is current player job so keep it");
                                        isActivePlayerJob = true;
                                        break;
                                    }
                                }
                                if (!isActivePlayerJob)
                                {
                                    FileLog.Log($"add {availableJobChain.currentJobInChain.ID} to deletes");
                                    jobChaninsToDelete.Add(availableJobChain);
                                }
                            }
                        }

                        // expire all chains that shouldnt be there according to server
                        foreach (JobChainController toExpire in jobChaninsToDelete)
                        {
                            deleteJob(updateStation.ProceduralJobsController, toExpire);
                        }

                        // add all remaining jobs in payload list as new jobs to station
                        foreach (JobPayload payload in payloads)
                        {
                            FileLog.Log($"creating new {payload.ID}");

                            string controllerName = $"ChainJob[{payload.Type}]: {updateStation.logicStation.ID} - {payload.ID}";
                            JobChainController controller = payloadController.MakeJobController(controllerName);
                            StaticJobDefinition definition = payloadController.CreateJobController(payload, controller);
                            payloadController.SpawnJobController(controller, definition);
                        }
                        updateStation.OverridePlayerEnteredJobGenerationZoneFlag();
                    }
                    stationNamesToUpdate.Clear();
                }
                stationUpdateLock.ReleaseMutex();
                yield return null;
            }
        }

        static void ChangeDesinationTrack(Task task, Track destinationTrack)
        {
            if (task.GetType() == typeof(SequentialTasks))
            {
                SequentialTasks seqTask = (SequentialTasks)task;
                LinkedList<Task> subTasks = Traverse.Create(seqTask).Field("tasks").GetValue<LinkedList<Task>>();
                ChangeDesinationTrack(subTasks.Last.Value, destinationTrack);
                return;
            }

            if (task.GetType() == typeof(ParallelTasks))
            {
                ParallelTasks parTask = (ParallelTasks)task;
                List<Task> subTasks = Traverse.Create(parTask).Field("tasks").GetValue<List<Task>>();
                ChangeDesinationTrack(subTasks[subTasks.Count - 1], destinationTrack);
                return;
            }

            if (task.GetType() == typeof(TransportTask))
            {
                TransportTask transportTask = (TransportTask)task;
                Traverse.Create(transportTask).Field("destinationTrack").SetValue(destinationTrack);
                return;
            }
        }

        static void UpdateJobOverview(StationController stationController, Job j)
        {
            List<JobOverview> overviews = Traverse.Create(stationController).Field("spawnedJobOverviews").GetValue<List<JobOverview>>();
            foreach (JobOverview ov in overviews)
            {
                if (ov.job.ID == j.ID)
                {
                    JobOverviewRender renderer = ((GameObject)UnityEngine.Object.Instantiate(Resources.Load("JobOverviewRender", typeof(GameObject)), SingletonBehaviour<DV.RenderTextureSystem.RenderTextureSystem>.Instance.transform.position, Quaternion.identity)).GetComponent<JobOverviewRender>();
                    if (renderer == null)
                        throw new Exception("JobOverviewRender is null");

                    RenderedTexturesBooklet textureBooklet = ov.gameObject.GetComponent<RenderedTexturesBooklet>();
                    if (textureBooklet == null)
                        throw new Exception("RenderedTexturesBooklet is null");

                    textureBooklet.RegisterTexturesGeneratedEvent(renderer);
                    renderer.GenerateTextures(BookletCreator_JobOverview.GetJobOverviewTemplateData(new Job_data(j)).ToArray());

                    FileLog.Log("job overview should have new texture.... maybe");
                }
            }
        }

        static void ChangeJobCars(IEnumerable<Task> tasks, List<Car> cars, List<CargoType> cargoTypesPerCar, float totalCargoAmount)
        {
            foreach (Task task in tasks) 
            {
                if (task.GetType() == typeof(SequentialTasks))
                {
                    SequentialTasks seqTask = (SequentialTasks)task;
                    LinkedList<Task> subTasks = Traverse.Create(seqTask).Field("tasks").GetValue<LinkedList<Task>>();
                    ChangeJobCars(subTasks, cars, cargoTypesPerCar, totalCargoAmount);
                }

                if (task.GetType() == typeof(ParallelTasks))
                {
                    ParallelTasks parTask = (ParallelTasks)task;
                    List<Task> subTasks = Traverse.Create(parTask).Field("tasks").GetValue<List<Task>>();
                    ChangeJobCars(subTasks, cars, cargoTypesPerCar, totalCargoAmount);
                }

                if (task.GetType() == typeof(TransportTask))
                {
                    TransportTask transportTask = (TransportTask)task;
                    Traverse baseObj = Traverse.Create(transportTask);
                    baseObj.Field("transportedCargoPerCar").SetValue(cargoTypesPerCar);
                    baseObj.Field("cars").SetValue(cars);
                }

                if (task.GetType() == typeof(WarehouseTask))
                {
                    WarehouseTask wharehouseTask = (WarehouseTask)task;
                    Traverse baseObj = Traverse.Create(wharehouseTask);
                    baseObj.Field("cars").SetValue(cars);
                    baseObj.Field("cargoAmount").SetValue(totalCargoAmount);
                }
            }
        }

        static void deleteJob(StationProceduralJobsController stationJobController, JobChainController toExpire)
        {
            FileLog.Log($"try and delete {toExpire.currentJobInChain.ID}");
            // delete cars
            SingletonBehaviour<CarSpawner>.Instance.DeleteTrainCars(toExpire.trainCarsForJobChain);

            // delete booklet
            Inventory inv = SingletonBehaviour<Inventory>.Instance;
            if (inv == null)
                throw new Exception("inventory is null");

            GameObject[] invObjects = inv.GetItemsArray();
            if (invObjects != null && invObjects.Length > 0)
            {
                foreach (GameObject invItem in invObjects)
                {
                    if (invItem == null)
                        continue;

                    JobBooklet booklet = invItem.GetComponent<JobBooklet>();
                    if (booklet != null && booklet.job != null && booklet.job.ID == toExpire.currentJobInChain.ID)
                    {
                        booklet.DestroyJobBooklet();
                        break;
                    }
                }
            }

            // delete overviews
            Traverse fieldTrav = Traverse.Create(stationJobController.stationController).Field("spawnedJobOverviews");
            if (fieldTrav == null)
                throw new Exception("fieldTrav is null");

            List<JobOverview> overviews = fieldTrav.GetValue<List<JobOverview>>();
            if (overviews != null)
            {
                foreach (JobOverview ov in overviews)
                {
                    if (ov.job.ID == toExpire.currentJobInChain.ID)
                    {
                        ov.DestroyJobOverview();
                        break;
                    }
                }
            }

            // delete everything else I can think of
            stationJobController.RemoveJobChainController(toExpire);
            toExpire.currentJobInChain.ExpireJob();
            toExpire.DestroyChain();
        }

        static void ReadThreadProc()
        {
            JsonSerializer serializer = new JsonSerializer();
            while (clientWebSocket.State == WebSocketState.Open)
            {
                byte[] buffer = new byte[2048];
                ThreadTask.Task<WebSocketReceiveResult> readTask = clientWebSocket.ReceiveAsync(new ArraySegment<byte>(buffer), default);
                readTask.Wait();

                if (readTask.Result != null && readTask.Result.Count > 0)
                {
                    int sizeToRead = readTask.Result.Count;
                    JsonTextReader jsonReader = new JsonTextReader(new StreamReader(new MemoryStream(buffer, 0, sizeToRead)));
                    WebSocketMessage msg = serializer.Deserialize<WebSocketMessage>(jsonReader);
                    FileLog.Log($"Update after WS message for {msg.StationID}");
                    stationUpdateLock.WaitOne();
                    stationNamesToUpdate.Add(msg.StationID);
                    stationUpdateLock.ReleaseMutex();
                }
            }

            FileLog.Log($"connection to WS closed for read thread with state {clientWebSocket.State}");
        }

        static void WriteThreadProc()
        {
            bool hasSendWelcome = false;
            StationProceduralJobsController[] stationJobControllers = UnityEngine.Object.FindObjectsOfType<StationProceduralJobsController>();
            JsonSerializer serializer = new JsonSerializer();
            while (clientWebSocket.State == WebSocketState.Open)
            {
                if (!hasSendWelcome)
                {
                    hasSendWelcome = true;

                    string userName = SingletonBehaviour<UserManager>.Instance.CurrentUser.Name;
                    WebSocketWelcomeMsg welcomeMsg = new WebSocketWelcomeMsg();
                    welcomeMsg.Username = userName;

                    StringWriter payloadWriter = new StringWriter();
                    serializer.Serialize(payloadWriter, welcomeMsg);
                    payloadWriter.Flush();
                    string payloadStr = payloadWriter.ToString();
                    byte[] payloadBytes = System.Text.Encoding.UTF8.GetBytes(payloadStr);
                    ThreadTask.Task welcomeSendTask = clientWebSocket.SendAsync(new ArraySegment<byte>(payloadBytes), WebSocketMessageType.Text, true, default);
                    welcomeSendTask.Wait();
                }

                // add check if player moved close to a new station to add a subscribtion for
                // same for leaving area
                foreach (StationProceduralJobsController jobStationController in stationJobControllers)
                {
                    string stationID = jobStationController.stationController.logicStation.ID;

                    StationJobGenerationRange stationRange = jobStationController.stationController.GetComponent<StationJobGenerationRange>();
                    if (stationRange == null)
                    {
                        FileLog.Log($"stationRange is null?!");
                        continue;
                    }

                    StationSubData subData = new StationSubData();
                    stationSubs.TryGetValue(stationID, out subData);

                    if (!stationRange.IsPlayerInJobGenerationZone(stationRange.PlayerSqrDistanceFromStationCenter) && subData.IsCloseToStation)
                    {
                        subData.IsCloseToStation = false;
                        subData.IsChanged = true;
                    } else if (stationRange.IsPlayerInJobGenerationZone(stationRange.PlayerSqrDistanceFromStationCenter) && !subData.IsCloseToStation)
                    {
                        subData.IsCloseToStation = true;
                        subData.IsChanged = true;
                    }
                }

                // update subs
                if (stationSubs.Count > 0)
                {
                    foreach (KeyValuePair<string, StationSubData> stationEntry in stationSubs)
                    {
                        if (!stationEntry.Value.IsChanged)
                            continue;

                        StringWriter payloadWriter = new StringWriter();
                        WebSocketSubMsg subMsg = new WebSocketSubMsg();
                        subMsg.StationID = stationEntry.Key;
                        subMsg.UnSub = !stationEntry.Value.IsCloseToStation;
                        serializer.Serialize(payloadWriter, subMsg);
                        payloadWriter.Flush();
                        string payloadStr = payloadWriter.ToString();
                        byte[] payloadBytes = System.Text.Encoding.UTF8.GetBytes(payloadStr);
                        ThreadTask.Task setupTask = clientWebSocket.SendAsync(new ArraySegment<byte>(payloadBytes), WebSocketMessageType.Text, true, default);
                        setupTask.Wait();
                        
                        string subLogMessagePart = "subbed";
                        if (subMsg.UnSub)
                            subLogMessagePart = "unsubbed";
                        FileLog.Log($"updated web socket state for {subMsg.StationID}: {subLogMessagePart}");
                        
                        stationEntry.Value.IsChanged = false;
                    }
                }

                Thread.Sleep(1000);
            }

            FileLog.Log($"connection to WS closed for write thread with state {clientWebSocket.State}");
        }

        public static void LoadAndCreateStationJobs(string stationID, StationController stationController)
        {
            string userName = SingletonBehaviour<UserManager>.Instance.CurrentUser.Name;
            List<JobPayload> payloads = ShareClient.Instance.GetStationJobs(stationID, userName);
            StationPaylodJobsController payloadController = new StationPaylodJobsController(stationController);

            foreach (JobPayload payload in payloads)
            {
                string controllerName = $"ChainJob[{payload.Type}]: {stationController.logicStation.ID} - {payload.ID}";
                JobChainController controller = payloadController.MakeJobController(controllerName);
                StaticJobDefinition definition = payloadController.CreateJobController(payload, controller);
                payloadController.SpawnJobController(controller, definition);
            }

            stationController.OverridePlayerEnteredJobGenerationZoneFlag();
        }
    }
}
