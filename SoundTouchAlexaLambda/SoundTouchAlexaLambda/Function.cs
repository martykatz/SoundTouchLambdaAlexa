using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Amazon.Lambda.Core;
using Amazon.Lambda.Serialization;

using Slight.Alexa.Framework.Models;
using Slight.Alexa.Framework.Models.Requests;
using Slight.Alexa.Framework.Models.Responses;
using System.Net;
using System.Net.Http;
using System.Text;

using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DocumentModel;

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializerAttribute(typeof(Amazon.Lambda.Serialization.Json.JsonSerializer))]

namespace SoundTouchLambdaAlexa
{


    class SoundTouchSpeakerMetaData
    {
        private string _MACAddress;
        public string MACAddress { set { this._MACAddress = value; } get { return _MACAddress.Replace(":", ""); } }
        public string InternalIPAddress { get; set; }
        public string SpeakerURLToAPIEndPoint { get; set; }
        public string SpeakerName { get; set; }
    }
    public class Function
    {
        static HttpClient httpClient;// = new HttpClient();
        static List<SoundTouchSpeakerMetaData> speakers;

        static Function()
        {
            httpClient = new HttpClient();

            //System.Environment.GetEnvironmentVariable("SpeakerMetaData")
        }

        private static void GetSpeakerMetaData(ILambdaLogger log)
        {
            log.LogLine("in speaker metadata function");
            var client = new AmazonDynamoDBClient();
            var table = Table.LoadTable(client, "SoundTouchSpeakers");
            ScanOperationConfig config = new ScanOperationConfig()
            {
                //AttributesToGet = new List<string> { "SpeakerName", "InternalIPAddress", "MACAddress", "SpeakerURLToAPIEndPoint" } 
            };
            log.LogLine("about to scan");

            Search search = table.Scan(config);
            log.LogLine("scan set");

            List<Document> documents;

            if (speakers == null)
            {
                speakers = new List<SoundTouchSpeakerMetaData>();
            }

            do
            {
                Task<List<Document>> documentsTask = search.GetNextSetAsync();
                documentsTask.Wait();
                documents = documentsTask.Result;
                foreach (var doc in documents)
                {
                    speakers.Add(new SoundTouchSpeakerMetaData() { SpeakerName = doc["SpeakerName"], InternalIPAddress = doc["InternalIPAddress"], MACAddress = doc["MACAddress"], SpeakerURLToAPIEndPoint = doc["SpeakerURLToAPIEndPoint"] });
                }

            } while (!search.IsDone);
        }

        private async Task<string> GetPresetItemName(SoundTouchSpeakerMetaData targetSpeaker, int presetNumber, ILambdaLogger log)
        {
            log.LogLine($"Getting Item Name for speaker {targetSpeaker.SpeakerName}, preset {presetNumber}");
            if (presetNumber >= 1 && presetNumber <= 6)
            {
                ResetHttpClient(targetSpeaker);

                string action = "presets";
                string presetsResponse = await httpClient.GetStringAsync(action);
                log.LogLine("get system presets response =" + presetsResponse);
                var x = System.Xml.Linq.XDocument.Parse(presetsResponse);
                var k = from t in x.Descendants("preset") select t;
                var preset = k.Where(y => y.Attributes().Where(z => z.Name == "id").First().Value == presetNumber.ToString()).First();
                var presetContentItem = preset.Descendants().Where(y => y.Name == "ContentItem").First();
                var presetDescription = presetContentItem.Descendants().Where(y => y.Name == "itemName").First().Value;
                return presetDescription;
            }
            else if (presetNumber > 6)
            {
                var client = new AmazonDynamoDBClient();
                var table = Table.LoadTable(client, "SoundTouchPresets");
                Dictionary<string, DynamoDBEntry> key = new Dictionary<string, DynamoDBEntry>();
                //key["SpeakerNumber"] = speakerNumber;
                key["PresetNumber"] = presetNumber;
                Task<Document> docTask = table.GetItemAsync(key);
                Document doc = docTask.Result;
                if (doc == null || doc["ContentItem"] == null)
                {
                    return "sorry, could not locate that preset or there was a problem";
                }
                else
                {
                    var presetContentItem = System.Xml.Linq.XDocument.Parse(doc["ContentItem"]);
                    var presetDescription = presetContentItem.Descendants().Where(y => y.Name == "itemName").First().Value;
                    return presetDescription;
                }
            }
            else
            {
                return $"preset {presetNumber} is undefined";
            }
        }
        private async Task<string> GetNowPlayingContentItemName(SoundTouchSpeakerMetaData targetSpeaker, ILambdaLogger log)
        {
            ResetHttpClient(targetSpeaker);

            string action = "now_playing";
            string nowPlayingResponse = await httpClient.GetStringAsync(action);
            log.LogLine("get item name nowPlayingResponse=" + nowPlayingResponse);
            var x = System.Xml.Linq.XDocument.Parse(nowPlayingResponse);
            var k = from t in x.Descendants("ContentItem") select t;
            var contentItemNode = k.First();
            contentItemNode.Attribute("isPresetable").Remove();
            return contentItemNode.Descendants().Where(y => y.Name == "itemName").First().Value;
        }

        private async Task<string> GetNowPlayingContentItem(SoundTouchSpeakerMetaData targetSpeaker, ILambdaLogger log)
        {
            ResetHttpClient(targetSpeaker);

            string action = "now_playing";
            string nowPlayingResponse = await httpClient.GetStringAsync(action);
            log.LogLine("nowPlayingResponse=" + nowPlayingResponse);
            var x = System.Xml.Linq.XDocument.Parse(nowPlayingResponse);
            var k = from t in x.Descendants("ContentItem") select t;
            var contentItemNode = k.First();
            contentItemNode.Attribute("isPresetable").Remove();
            contentItemNode.ReplaceNodes(contentItemNode.Descendants().Where(y => y.Name == "itemName"));

            return contentItemNode.ToString();
        }

        private string GetItemNameFromNowPlaying(SoundTouchSpeakerMetaData targetSpeaker, string contentItem, ILambdaLogger log)
        {
            var contentItemNode = System.Xml.Linq.XDocument.Parse(contentItem);
            return contentItemNode.Descendants().Where(y => y.Name == "itemName").First().Value;
        }


        private void SetNowPlayingContentItem(SoundTouchSpeakerMetaData targetSpeaker, string contentItemXML, ILambdaLogger log)
        {
            log.LogLine($"on speaker {targetSpeaker.SpeakerName} selecting XML {contentItemXML}");
            ResetHttpClient(targetSpeaker);

            string action = "select";
            //volume = Convert.ToInt32(input.Request.Intent.Slots["volume"].Value);
            //string URL = String.Format(baseURL, speakerNumber);
            string URL = targetSpeaker.SpeakerURLToAPIEndPoint;
            httpClient.BaseAddress = new Uri(URL);
            Task<HttpResponseMessage> myTask = httpClient.PostAsync(action, new StringContent(contentItemXML, Encoding.UTF8, "application/xml"));
            //myTask.Run();
            myTask.Wait();
            HttpResponseMessage myResponse = myTask.Result;
            myResponse.EnsureSuccessStatusCode();

        }

        private void SaveSession(string sessionId, SoundTouchSpeakerMetaData targetSpeaker, int presetNumber, int volume)
        {
            var client = new AmazonDynamoDBClient();
            var table = Table.LoadTable(client, "SoundTouchPresets");
            Document doc = new Document();
            //doc["SpeakerNumber"] = speakerNumber;
            doc["PresetNumber"] = presetNumber;
            doc["Volume"] = volume;
            doc["SessionId"] = sessionId;
            Task<Document> docTask = table.PutItemAsync(doc);
            docTask.Wait();
        }
        private void DeleteSession(string sessionId)
        {
            var client = new AmazonDynamoDBClient();
            var table = Table.LoadTable(client, "SoundTouchAlexaSessions");
            Dictionary<string, DynamoDBEntry> key = new Dictionary<string, DynamoDBEntry>();
            key["SessionId"] = sessionId;
            Task<Document> documentDeletionTask = table.DeleteItemAsync(key);
            documentDeletionTask.Wait();
        }

        private void PlayCustomPreset(SoundTouchSpeakerMetaData targetSpeaker, int presetNumber, ILambdaLogger log)
        {
            var client = new AmazonDynamoDBClient();
            var table = Table.LoadTable(client, "SoundTouchPresets");
            Dictionary<string, DynamoDBEntry> key = new Dictionary<string, DynamoDBEntry>();
            //key["SpeakerNumber"] = speakerNumber;
            key["PresetNumber"] = presetNumber;
            Task<Document> docTask = table.GetItemAsync(key);
            Document doc = docTask.Result;
            SetNowPlayingContentItem(targetSpeaker, doc["ContentItem"], log);
        }
        private void SaveCustomPreset(SoundTouchSpeakerMetaData targetSpeaker, int presetNumber, ILambdaLogger log)
        {
            Task<string> getNowPlayingTask = GetNowPlayingContentItem(targetSpeaker, log);
            getNowPlayingTask.Wait();
            string contentItem = getNowPlayingTask.Result;
            var client = new AmazonDynamoDBClient();
            var table = Table.LoadTable(client, "SoundTouchPresets");
            Document doc = new Document();
            //doc["SpeakerNumber"] = speakerNumber;
            doc["PresetNumber"] = presetNumber;
            doc["ContentItem"] = contentItem;
            doc["ItemName"] = GetItemNameFromNowPlaying(targetSpeaker, contentItem, log);
            Task<Document> docTask = table.PutItemAsync(doc);
            docTask.Wait();

        }
        private void SetSpeakerPower(SoundTouchSpeakerMetaData targetSpeaker, bool power, ILambdaLogger log)
        {
            Task<bool> getSpeakerPowerStatusTask = IsSpeakerPowerOn(targetSpeaker, log);
            getSpeakerPowerStatusTask.Wait();
            bool currentPower = getSpeakerPowerStatusTask.Result;

            if (power != currentPower)
            {
                PressAndReleaseKey(targetSpeaker, "POWER");
            }

        }
        private async Task<bool> IsSpeakerPowerOn(SoundTouchSpeakerMetaData targetSpeaker, ILambdaLogger log)
        {

            //<?xml version="1.0" encoding="UTF-8" ?><nowPlaying deviceID="689E194B6EF6" source="STANDBY"><ContentItem source="STANDBY" isPresetable="true" /></nowPlaying>

            ResetHttpClient(targetSpeaker);

            string action = "now_playing";
            string volumeInfoResponse = await httpClient.GetStringAsync(action);
            log.LogLine("now Playing Info response=" + volumeInfoResponse);
            var x = System.Xml.Linq.XDocument.Parse(volumeInfoResponse);
            var k = from t in x.Descendants("nowPlaying") select t.Attributes().Where(y => y.Name == "source").First().Value;
            return !(k.First() == "STANDBY");

        }
        private void ResetHttpClient(SoundTouchSpeakerMetaData targetSpeaker)
        {
            httpClient = new HttpClient();
            //string URL = String.Format(baseURL, speakerNumber);
            string URL = targetSpeaker.SpeakerURLToAPIEndPoint;
            httpClient.BaseAddress = new Uri(URL);

        }
        private async Task<int> GetCurrentVolume(SoundTouchSpeakerMetaData targetSpeaker, ILambdaLogger log)
        {
            ResetHttpClient(targetSpeaker);

            string action = "volume";
            string volumeInfoResponse = await httpClient.GetStringAsync(action);
            log.LogLine("volume Info response=" + volumeInfoResponse);
            var x = System.Xml.Linq.XDocument.Parse(volumeInfoResponse);
            var k = from t in x.Descendants("actualvolume") select t.Value;
            return Int32.Parse(k.First());
        }

        private void SetVolume(SoundTouchSpeakerMetaData targetSpeaker, int volume)
        {
            ResetHttpClient(targetSpeaker);

            string action = "volume";
            //volume = Convert.ToInt32(input.Request.Intent.Slots["volume"].Value);
            //string URL = String.Format(baseURL, speakerNumber);
            string URL = targetSpeaker.SpeakerURLToAPIEndPoint;
            httpClient.BaseAddress = new Uri(URL);
            Task<HttpResponseMessage> myTask = httpClient.PostAsync(action, new StringContent(String.Format("<volume>{0}</volume>", volume), Encoding.UTF8, "application/xml"));
            //myTask.Run();
            myTask.Wait();
            HttpResponseMessage myResponse = myTask.Result;
            myResponse.EnsureSuccessStatusCode();

        }

        private void IncrementVolume(SoundTouchSpeakerMetaData targetSpeaker, int increment, ILambdaLogger log)
        {
            ResetHttpClient(targetSpeaker);

            Task<int> volumeTask = GetCurrentVolume(targetSpeaker, log);
            //volumeTask.Start();
            volumeTask.Wait();
            int currentVolume = volumeTask.Result;
            int newVolume = increment + currentVolume;
            if (newVolume > 100)
            {
                newVolume = 100;
            }
            else if (newVolume < 0)
            {
                newVolume = 0;
            }
            SetVolume(targetSpeaker, newVolume);
        }

        private void PressAndReleaseKey(SoundTouchSpeakerMetaData targetSpeaker, string keyName)
        {
            ResetHttpClient(targetSpeaker);

            string action = "key";
            //string URL = String.Format(baseURL, speakerNumber);
            string URL = targetSpeaker.SpeakerURLToAPIEndPoint;
            httpClient.BaseAddress = new Uri(URL);

            Task<HttpResponseMessage> myKeyPressTask = httpClient.PostAsync(action, new StringContent(String.Format("<key state=\"press\" sender=\"Gabbo\">{0}</key>", keyName), Encoding.UTF8, "application/xml"));
            //myKeyPressTask.Start();
            myKeyPressTask.Wait();
            HttpResponseMessage myKeyPressResponse = myKeyPressTask.Result;
            myKeyPressResponse.EnsureSuccessStatusCode();

            Task<HttpResponseMessage> myKeyReleaseTask = httpClient.PostAsync(action, new StringContent(String.Format("<key state=\"release\" sender=\"Gabbo\">{0}</key>", keyName), Encoding.UTF8, "application/xml"));
            //myKeyReleaseTask.Start();
            myKeyReleaseTask.Wait();
            HttpResponseMessage myKeyReleaseResponse = myKeyPressTask.Result;
            myKeyReleaseResponse.EnsureSuccessStatusCode();

        }

        private void PlayEverywhere(SoundTouchSpeakerMetaData targetSpeaker)
        {
            ResetHttpClient(targetSpeaker);

            //int speakerIndexNumber = speakerNumber - 1;
            StringBuilder xmlBuilder = new StringBuilder("");
            //xmlBuilder.Append(String.Format("<zone master=\"{0}\" senderIPAddress=\"127.0.0.1\">", macAddresses[speakerIndexNumber]));
            xmlBuilder.Append(String.Format("<zone master=\"{0}\" senderIPAddress=\"127.0.0.1\">", targetSpeaker.MACAddress));

            foreach (string macaddress in speakers.Select(x => x.MACAddress))
            {
                xmlBuilder.Append(String.Format("<member>{0}</member>", macaddress));
            }
            xmlBuilder.Append("</zone>");

            string action = "setZone";
            Task<HttpResponseMessage> myZoneSettingTask = httpClient.PostAsync(action, new StringContent(xmlBuilder.ToString(), Encoding.UTF8, "application/xml"));
            //myZoneSettingTask.Start();
            myZoneSettingTask.Wait();
            HttpResponseMessage myKeyPressResponse = myZoneSettingTask.Result;
            myKeyPressResponse.EnsureSuccessStatusCode();
        }


        private async Task<IEnumerable<string>> GetStatus(SoundTouchSpeakerMetaData targetSpeaker, ILambdaLogger log)
        {
            List<string> statusStrings = new List<string>();
            ResetHttpClient(targetSpeaker);

            string action = "now_playing";
            string nowPlayingResponseString = await httpClient.GetStringAsync(action);
            //TODO
            var nowPlayingResponseXML = System.Xml.Linq.XDocument.Parse(nowPlayingResponseString);
            var nowPlayingTag = (from t in nowPlayingResponseXML.Descendants("nowPlaying") select t)?.First();
            var contentItemTag = (from t in nowPlayingTag?.Descendants("contentItem") select t)?.First();
            var itemNameTag = (from t in contentItemTag?.Descendants("itemName") select t)?.First();

            var playStatusTag = (from t in nowPlayingResponseXML.Descendants("playStatus") select t)?.First();

            if ((nowPlayingTag?.Attributes().Where(y => y.Name == "source")?.First()?.Value == "STANDBY"))
            {
                statusStrings.Add($"Power is off");
            }
            else
            {
                switch (playStatusTag.Value)
                {
                    case "PLAY_STATE":

                        Task<int> volumeTask = GetCurrentVolume(targetSpeaker, log);


                        string sourceString;
                        string source = (nowPlayingTag?.Attributes().Where(y => y.Name == "source")?.First()?.Value) ?? "unknown source";
                        string sourceAccount = (nowPlayingTag?.Attributes().Where(y => y.Name == "sourceAccount")?.First()?.Value);
                        switch (source)
                        {
                            case "AUX":
                                sourceString = "Auxiliary Input";
                                break;
                            case "PANDORA":
                                sourceString = "Pandora for user " + sourceAccount.Replace("@", " at ").Replace(".", " dot ");
                                break;
                            case "IHEART":
                                sourceString = "I Heart Radio for user " + sourceAccount.Replace("@", " at ").Replace(".", " dot ");
                                break;
                            case "AMAZON":
                                sourceString = "Amazon Music for user " + sourceAccount.Replace("@", " at ").Replace(".", " dot ");
                                break;
                            case "UPNP":
                                sourceString = "You Pea N Pea";
                                break;
                            case "STORED_MUSIC":
                                ResetHttpClient(targetSpeaker);
                                action = "sources";
                                string sourcesResponseString = await httpClient.GetStringAsync(action);
                                //TODO
                                var sourcesResponseXML = System.Xml.Linq.XDocument.Parse(sourcesResponseString);
                                var sourceItems = sourcesResponseXML.Descendants().Where(y => y.Name == "sourceItem");
                                var sourceItem = sourceItems.Where(y => y.Attributes().Any(z => z.Name == "source" && z.Value == "STORED_MUSIC") && y.Attributes().Any(z => z.Name == "sourceAccount" && z.Value == sourceAccount))?.First();
                                sourceString = sourceItem.Value;
                                break;
                            default:
                                sourceString = (source ?? "unspecified").Replace("_", " ");
                                break;
                        }
                        statusStrings.Add($"Playing {itemNameTag.Value ?? "unknown item"} from {(nowPlayingTag?.Attributes().Where(y => y.Name == "source")?.First()?.Value) ?? "unknown source"}.");


                        var artistTag = (from t in nowPlayingResponseXML.Descendants("artist") select t)?.First();
                        var trackTag = (from t in nowPlayingResponseXML.Descendants("track") select t)?.First();
                        var albumTag = (from t in nowPlayingResponseXML.Descendants("album") select t)?.First();

                        statusStrings.Add($"This is {trackTag?.Value ?? "unknown track"} by {artistTag?.Value ?? "unknown artist"} from {albumTag?.Value ?? "unknown album"}");

                        var shuffleSettingTag = (from t in nowPlayingResponseXML.Descendants("shuffleSetting") select t)?.First();
                        var repeatSettingTag = (from t in nowPlayingResponseXML.Descendants("repeatSetting") select t)?.First();

                        string shuffleSetting = "not applicable or unknown";
                        if (shuffleSettingTag?.Value == "SHUFFLE_OFF") { shuffleSetting = "off"; }
                        if (shuffleSettingTag?.Value == "SHUFFLE_ON") { shuffleSetting = "on"; }

                        statusStrings.Add($"Shuffle is {shuffleSetting}");

                        string repeatSetting = "not applicable or unknown";
                        if (repeatSettingTag?.Value == "REPEATE_OFF") { shuffleSetting = "off"; }
                        if (repeatSettingTag?.Value == "REPEAT_ALL") { shuffleSetting = "set to all tracks"; }
                        if (repeatSettingTag?.Value == "REPEAT_ONE") { shuffleSetting = "set to single track"; }

                        statusStrings.Add($"Repeat is  {repeatSetting}");
                        volumeTask.Wait();
                        statusStrings.Add($"Volume level is {volumeTask.Result} of one hundred");
                        break;
                    case "PAUSE_STATE":
                        statusStrings.Add($"Playback is paused");
                        break;
                    case "STOP_STATE":
                        statusStrings.Add($"Playback is stopped");
                        break;
                }
            }
            return statusStrings.AsEnumerable();
        }
        private SkillResponse BuildResponse(IOutputSpeech innerResponse)
        {
            Response response = new Response();
            response.ShouldEndSession = true;
            response.OutputSpeech = innerResponse;
            SkillResponse skillResponse = new SkillResponse();
            skillResponse.Response = response;
            skillResponse.Version = "1.0";

            return skillResponse;
        }
        /// <summary>
        /// A simple function that takes a string and does a ToUpper
        /// </summary>
        /// <param name="input"></param>
        /// <param name="context"></param>
        /// <returns></returns>
        public SkillResponse FunctionHandler(SkillRequest input, ILambdaContext context)
        {
            string action;
            string URL;

            Response response;
            IOutputSpeech innerResponse = null;
            var log = context.Logger;

            log.LogLine("speaker metadata about to retrieve!");

            GetSpeakerMetaData(log);
            log.LogLine($"speaker metadata retrieved! count= {speakers.Count()}");
            /*
            foreach (var speaker in speakers)
            {
                log.LogLine($"speakerName: {speaker.SpeakerName}, MAC Address: {speaker.MACAddress}, Internal IP Address: {speaker.InternalIPAddress}, End Point: {speaker.SpeakerURLToAPIEndPoint} ");
            }
            */
            if (input.GetRequestType() == typeof(Slight.Alexa.Framework.Models.Requests.RequestTypes.ILaunchRequest))
            {
                log.LogLine($"Default LaunchRequest made");
                innerResponse = new PlainTextOutputSpeech();
                (innerResponse as PlainTextOutputSpeech).Text = "Welcome to my Bose Sound Touch Skill.  For now, you can issue commands to play presets, change volume, and play everywhere.";
                return BuildResponse(innerResponse);
            }
            else if (input.GetRequestType() == typeof(Slight.Alexa.Framework.Models.Requests.RequestTypes.ISessionEndedRequest))
            {
                DeleteSession(input.Session.SessionId);
            }
            else if (input.GetRequestType() == typeof(Slight.Alexa.Framework.Models.Requests.RequestTypes.IIntentRequest))
            {
                HttpClient httpClient = new HttpClient();
                // intent request, process the intent
                log.LogLine($"Intent Requested {input.Request.Intent.Name}");


                log.LogLine("speaker number string came through as:" + input.Request.Intent.Slots["speaker"].Value);

                string targetSpeakerName = input.Request.Intent.Slots["speaker"].Value;
                IEnumerable<SoundTouchSpeakerMetaData> candidateSpeakers = speakers.Where(x => x.SpeakerName.ToLower() == targetSpeakerName.ToLower());
                SoundTouchSpeakerMetaData targetSpeaker = candidateSpeakers.FirstOrDefault();
                if (targetSpeaker == null)
                {
                    log.LogLine("could not resolve target speaker");
                    innerResponse = new PlainTextOutputSpeech();
                    (innerResponse as PlainTextOutputSpeech).Text = "You asked to perform this action on a speaker named " + targetSpeakerName + " but I couldn't find a speaker with that name";
                    return BuildResponse(innerResponse);

                }



                int volume;
                int increment;
                int presetNumber;

                switch (input.Request.Intent.Name)
                {
                    case "ChangeVolume":
                        volume = Convert.ToInt32(input.Request.Intent.Slots["volume"].Value);
                        if (volume >= 0 && volume <= 100)
                        {
                            SetVolume(targetSpeaker, volume);
                            log.LogLine($"Volume set to: " + volume.ToString());
                            innerResponse = new PlainTextOutputSpeech();
                            (innerResponse as PlainTextOutputSpeech).Text = "Okay";
                        }
                        else
                        {
                            log.LogLine($"Attempt to set volume to: " + volume.ToString());
                            innerResponse = new PlainTextOutputSpeech();
                            (innerResponse as PlainTextOutputSpeech).Text = "You can only set the volume between zero and one hundred.  I heard you try to set it to " + volume.ToString();
                        }
                        break;
                    case "IncreaseVolume":
                        increment = Convert.ToInt32(input.Request.Intent.Slots["increment"].Value);
                        if (increment >= 0 && increment <= 100)
                        {

                            IncrementVolume(targetSpeaker, increment, log);
                            log.LogLine($"Volume increased by: " + increment.ToString());
                            innerResponse = new PlainTextOutputSpeech();
                            (innerResponse as PlainTextOutputSpeech).Text = "Okay";

                        }
                        else
                        {
                            log.LogLine($"Attempt to increase volume by: " + increment.ToString());
                            innerResponse = new PlainTextOutputSpeech();
                            (innerResponse as PlainTextOutputSpeech).Text = "You can only increase the volume by an amount between zero and one hundred.  I heard you try to increase it by " + increment.ToString();
                        }
                        break;
                    case "DecreaseVolume":
                        increment = Convert.ToInt32(input.Request.Intent.Slots["increment"].Value);
                        if (increment >= 0 && increment <= 100)
                        {
                            IncrementVolume(targetSpeaker, -increment, log);
                            log.LogLine($"Volume decreased by: " + increment.ToString());
                            innerResponse = new PlainTextOutputSpeech();
                            (innerResponse as PlainTextOutputSpeech).Text = "Okay";
                        }
                        else
                        {
                            log.LogLine($"Attempt to decrease volume by: " + increment.ToString());
                            innerResponse = new PlainTextOutputSpeech();
                            (innerResponse as PlainTextOutputSpeech).Text = "You can only decrease the volume by an amount between zero and one hundred.  I heard you try to increase it by " + increment.ToString();
                        }
                        break;
                    case "PlayPreset":
                        log.LogLine("preset number raw value  = " + input.Request.Intent.Slots["presetNumber"].Value);
                        presetNumber = Convert.ToInt32(input.Request.Intent.Slots["presetNumber"].Value);
                        if (presetNumber >= 1 && presetNumber <= 6)
                        {
                            log.LogLine($"Preset playback preset #: " + presetNumber);
                            PressAndReleaseKey(targetSpeaker, "PRESET_" + presetNumber.ToString());
                            innerResponse = new PlainTextOutputSpeech();
                            (innerResponse as PlainTextOutputSpeech).Text = "Playing preset " + presetNumber.ToString();
                        }
                        else if (presetNumber > 6)
                        {
                            log.LogLine($"Custom Preset playback custom preset #: " + presetNumber);
                            PlayCustomPreset(targetSpeaker, presetNumber, log);
                            innerResponse = new PlainTextOutputSpeech();
                            (innerResponse as PlainTextOutputSpeech).Text = "Playing preset " + presetNumber.ToString();

                        }
                        else
                        {
                            log.LogLine($"Attempt to change preset number to: " + presetNumber.ToString());
                            innerResponse = new PlainTextOutputSpeech();
                            (innerResponse as PlainTextOutputSpeech).Text = "I heard you try to change the preset number to " + presetNumber.ToString() + ". You can only change the preset to a number between one and six.  Please try again.";
                        }
                        break;
                    case "PlayEverywhere":
                        log.LogLine($"Playing {targetSpeaker.SpeakerName} everywhere");
                        innerResponse = new PlainTextOutputSpeech();
                        (innerResponse as PlainTextOutputSpeech).Text = $"Okay.  Playing speaker {targetSpeaker.SpeakerName} everywhere";
                        PlayEverywhere(targetSpeaker);
                        break;
                    case "PowerOn":
                        SetSpeakerPower(targetSpeaker, true, log);
                        break;
                    case "PowerOff":
                        SetSpeakerPower(targetSpeaker, false, log);
                        break;
                    case "SaveCustomPreset":
                        presetNumber = Convert.ToInt32(input.Request.Intent.Slots["customPresetNumber"].Value);
                        if (presetNumber >= 7)
                        {
                            SaveCustomPreset(targetSpeaker, presetNumber, log);
                            log.LogLine($"Custom Preset {presetNumber} on speaker {targetSpeaker.SpeakerName} set.");
                            innerResponse = new PlainTextOutputSpeech();
                            (innerResponse as PlainTextOutputSpeech).Text = $"Your music has been saved to preset slot {presetNumber}.";
                        }
                        else if (presetNumber >= 1 && presetNumber <=6)
                        {
                            log.LogLine($"Attempt to save Custom Preset {presetNumber} on speaker {targetSpeaker} specified a reserved.  Please specify a preset number of seven or greater.");
                            innerResponse = new PlainTextOutputSpeech();
                            (innerResponse as PlainTextOutputSpeech).Text = $"You attempted to save preset number {presetNumber}.  At this time, its best to leave setting preset numbers one through six up to Bose.";
                        }
                        else
                        {
                            log.LogLine($"Attempt to save Custom Preset {presetNumber} on speaker {targetSpeaker} specified an invalid preset number.  Please specify a preset number of seven or greater.");
                            innerResponse = new PlainTextOutputSpeech();
                            (innerResponse as PlainTextOutputSpeech).Text = $"You attempted to create preset number {presetNumber}.  At this time, only preset numbers seven and up are supported.";
                        }
                        break;
                    case "DescribeNowPlaying":
                        innerResponse = new PlainTextOutputSpeech();
                        (innerResponse as PlainTextOutputSpeech).Text = $"Speaker {targetSpeaker.SpeakerName} is playing " + GetNowPlayingContentItemName(targetSpeaker, log);
                        break;
                    case "DescribePreset":
                        presetNumber = Convert.ToInt32(input.Request.Intent.Slots["presetNumber"].Value);
                        Task<string> itemNameTask = GetPresetItemName(targetSpeaker, presetNumber, log);
                        itemNameTask.Wait();
                        string itemName = itemNameTask.Result;
                        innerResponse = new PlainTextOutputSpeech();
                        (innerResponse as PlainTextOutputSpeech).Text = $"Preset {presetNumber} on speaker {targetSpeaker.SpeakerName} is {itemName}";
                        break;
                    case "DescribeVolume":
                        Task<int> getVolumeTask = GetCurrentVolume(targetSpeaker, log);
                        getVolumeTask.Wait();
                        volume = getVolumeTask.Result;
                        innerResponse = new PlainTextOutputSpeech();
                        (innerResponse as PlainTextOutputSpeech).Text = $"Current volume level on speaker {targetSpeaker.SpeakerName} is {volume}";
                        break;
                    case "DescribeSpeakerStatus":
                        Task<IEnumerable<string>> statusMessagesTask = GetStatus(targetSpeaker, log);
                        statusMessagesTask.Wait();
                        IEnumerable<string> statusMessages = statusMessagesTask.Result;
                        innerResponse = new PlainTextOutputSpeech();
                        (innerResponse as PlainTextOutputSpeech).Text = $"Status of {targetSpeaker.SpeakerName} is. {String.Join(".  ", statusMessages)}";
                        break;

                    default:
                        log.LogLine($"Bad Request received");
                        innerResponse = new PlainTextOutputSpeech();
                        (innerResponse as PlainTextOutputSpeech).Text = "I don't think I heard you correctly.  That didn't sound like a valid request for this skill.";
                        break;

                }
                return BuildResponse(innerResponse);
            }
            log.LogLine($"Not implemented");
            innerResponse = new PlainTextOutputSpeech();
            (innerResponse as PlainTextOutputSpeech).Text = "I am not ready to handle that request yet.";
            return BuildResponse(innerResponse);

        }
    }
}
