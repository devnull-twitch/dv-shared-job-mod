using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using DV.Logic.Job;
using HarmonyLib;

namespace JobShareMod
{
    [JsonObject(MemberSerialization.OptIn)]
    public class JobPayload
    {
        [JsonProperty("id")]
        public string ID { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("starting_track")]
        public string StartingTrack { get; set; }

        [JsonProperty("target_station")]
        public string TargetStation { get; set; }

        [JsonProperty("target_track")]
        public string TargetTrack { get; set; }

        [JsonProperty("car_count")]
        public int CarCount { get; set; }

        [JsonProperty("cargo_type")]
        public string CargoType { get; set; }

        [JsonProperty("wage")]
        public int Wage { get; set; }
    }

    [JsonObject(MemberSerialization.OptIn)]
    public class UserPayload
    {
        [JsonProperty("username")]
        public string Name { get; set; }
    }

    public class ShareClient
    {
        private static ShareClient instance;

        public static ShareClient Instance
        {
            get 
            {
                if (instance == null)
                {
                    instance = new ShareClient();
                }
                return instance;
            }
        }

        private HttpClient httpClient;

        private ShareClient()
		{
            httpClient = new HttpClient
            {
                BaseAddress = new Uri(Main.baseURL),
            };
        }

        public List<JobPayload> GetStationJobs(string stationID, string userName)
        {
            try
            {
                Task<HttpResponseMessage> httpJobTask = httpClient.GetAsync($"station/{stationID}?username={userName}");
                httpJobTask.Wait();

                HttpResponseMessage response = httpJobTask.Result;
                Task<Stream> contentRead = response.Content.ReadAsStreamAsync();
                contentRead.Wait();
                Stream str = contentRead.Result;

                StreamReader streamReader = new StreamReader(str);
                JsonSerializer serializer = new JsonSerializer();
                return serializer.Deserialize<List<JobPayload>>(new JsonTextReader(streamReader));
            } 
            catch (HttpRequestException e)
            {
                FileLog.Log($"HTTP exception loading station jobs: {e.ToString()}");
            }

            return new List<JobPayload>();
        }

        public bool ReserveJob(Job job, string userName)
        {
            return UpdateJobStatus(job, userName, "reserve");
        }

        public bool TakeJob(Job job, string userName)
        {
            return UpdateJobStatus(job, userName, "take");
        }

        public bool FinishJob(Job job, string userName)
        {
            return UpdateJobStatus(job, userName, "finish");
        }

        private bool UpdateJobStatus(Job job, string userName, string statusKey)
        {
            UserPayload sendPayload = new UserPayload();
            sendPayload.Name = userName;

            StringWriter payloadWriter = new StringWriter();

            JsonSerializer serializer = new JsonSerializer();
            serializer.Serialize(payloadWriter, sendPayload);
            payloadWriter.Flush();

            HttpContent content = new StringContent(payloadWriter.ToString());

            Task<HttpResponseMessage> postTask = httpClient.PostAsync($"job/{job.ID}/{statusKey}", content);
            postTask.Wait();

            HttpResponseMessage response = postTask.Result;
            return response.IsSuccessStatusCode;
        }
    }
}