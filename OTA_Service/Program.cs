using System;
using System.Text;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using MQTTnet;
using MQTTnet.Client;
using System.Xml.Linq;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using NLog;




namespace OTAService
{
    class Program
    {

        //每支程式以不同GUID當成Mutex名稱，可避免執行檔同名同姓的風險
        static string appGuid = "{B19DAFCB-729C-43A6-8232-F3C31BB4E404}";

        //- 1. 宣告 MQTT 實體物件 
        private static bool keepRunning = true;
        private static IMqttClient client = new MqttFactory().CreateMqttClient();

        //--2. SetLog
        private static Logger logger = NLog.LogManager.GetCurrentClassLogger();

        //--3. config 
        private static Dictionary<string, string> dic_MQTT_Basic = null;
        private static Dictionary<string, string> dic_MQTT_Recv = null;
        private static Dictionary<string, string> dic_MQTT_Send = null;

        //--- Main Processing -----------
        static void Main(string[] args)
        {
            //Server need check only one process on going
            //如果要做到跨Session唯一，名稱可加入"Global\"前綴字
            //如此即使用多個帳號透過Terminal Service登入系統
            //整台機器也只能執行一份
            using (Mutex m = new Mutex(false, "Global\\" + appGuid))
            {
                //檢查是否同名Mutex已存在(表示另一份程式正在執行)
                if (!m.WaitOne(0, false))
                {
                    Console.WriteLine("Only one instance is allowed!");
                    return;
                }
                //如果是Windows Form，Application.Run()要包在using Mutex範圍內
                //以確保WinForm執行期間Mutex一直存在

                try
                {
                    Console.WriteLine("Welcome DotNet Core C# MQTT Client");
                    string Config_Path = AppContext.BaseDirectory + "/settings/Setting.xml";

                    logger.Info("Load MQTT Config From File: " + Config_Path);

                    Load_Xml_Config_To_Dict(Config_Path);

                    logger.Info("Load MQTT Config successful");

                    var options = new MqttClientOptionsBuilder()
                        .WithClientId(dic_MQTT_Basic["ClinetID"])
                        .WithTcpServer(dic_MQTT_Basic["BrokerIP"], Convert.ToInt32(dic_MQTT_Basic["BrokerPort"]))
                        .Build();

                    //- 1. setting receive topic defect # for all
                    client.Connected += async (s, e) =>
                    {
                        logger.Info("Connect TO MQTT Server");

                        foreach (KeyValuePair<string, string> kvp in dic_MQTT_Recv)
                        {
                            await client.SubscribeAsync(new TopicFilterBuilder().WithTopic(kvp.Value).WithAtMostOnceQoS().Build());
                            logger.Info("MQTT-Subscribe-Topic" + kvp.Value);
                        }
                    };

                    //- 2. if disconnected try to re-connect 
                    client.Disconnected += async (s, e) =>
                    {
                        logger.Error("Disconnect from MQTT Server");

                        await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(10));
                        try
                        {
                            logger.Debug("Try to reconnect to MQTT Server");
                            await client.ConnectAsync(options);
                        }
                        catch
                        {
                            logger.Error("Reconnect MQTT Server Faild");
                        }
                    };
                    client.ConnectAsync(options);

                    //- 3. receive 委派到 client_PublishArrived 
                    client.ApplicationMessageReceived += client_PublishArrived;

                    //- 4. Handle process Abort Event (Ctrl - C )
                    Console.CancelKeyPress += delegate (object sender, ConsoleCancelEventArgs e)
                    {
                        e.Cancel = true;
                        Program.keepRunning = false;
                    };

                    AppDomain.CurrentDomain.ProcessExit += delegate (object sender, EventArgs e)
                    {
                        logger.Info("Process is exiting!");
                    };

                    logger.Info("System Initial Finished");
                    
                    //-  5.2 set thread pool max
                    ThreadPool.SetMaxThreads(16, 16);
                    ThreadPool.SetMinThreads(4, 4);

                    //- 6. 執行無窮迴圈等待 
                    Console.WriteLine("Please key in Ctrl+ C to exit");
                    while (Program.keepRunning)
                    {
                       
                        System.Threading.Thread.Sleep(100);
                    }
                    logger.Info("Process is exited successfully !!");
                    Console.WriteLine("exited successfully");

                }
                catch (Exception ex)
                {
                    logger.Error(ex.Message);
                    
                }
            }
        }

        // ---------- Handle MQTT Subscribe
        static void client_PublishArrived(object sender, MqttApplicationMessageReceivedEventArgs e)
        {
            string OTA_Topic = "/Cmd/OTA";
            string topic = e.ApplicationMessage.Topic;
            string message = Encoding.UTF8.GetString(e.ApplicationMessage.Payload);

            if(topic.Contains(OTA_Topic))
            {
                ProcrssOTA(topic, message);
            }
          
        }

        // ---------- This is function is put message to MQTT Broker 
        static void client_Publish_To_Broker(string Topic, string Message)
        {
            if (client.IsConnected == true)
            {
                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(Topic)
                    .WithPayload(Message)
                    .Build();
                client.PublishAsync(message);
            }
        }

        static void ProcrssOTA(string topic, string payload)
        {
            
            OTAService.cls_Cmd_OTA OTA_CMD = JsonConvert.DeserializeObject<cls_Cmd_OTA>(payload);
        }

        static void Load_Xml_Config_To_Dict(string config_path)
        {
            XElement SettingFromFile = XElement.Load(config_path);
            XElement MQTT_Setting = SettingFromFile.Element("MQTT");
            XElement Basic_Setting = MQTT_Setting.Element("Basic_Setting");
            XElement Receive_Topic = MQTT_Setting.Element("Receive_Topic");
            XElement Send_Topic = MQTT_Setting.Element("Send_Topic");

            dic_MQTT_Basic = new Dictionary<string, string>();
            dic_MQTT_Recv = new Dictionary<string, string>();
            dic_MQTT_Send = new Dictionary<string, string>();

            if (Basic_Setting != null)
            {
                dic_MQTT_Basic.Clear();
                foreach (var el in Basic_Setting.Elements())
                {
                    dic_MQTT_Basic.Add(el.Name.LocalName, el.Value);
                }
            }

            if (Receive_Topic != null)
            {
                foreach (var el in Receive_Topic.Elements())
                {
                    dic_MQTT_Recv.Add(el.Name.LocalName, el.Value);
                }
            }

            if (Send_Topic != null)
            {
                foreach (var el in Send_Topic.Elements())
                {
                    dic_MQTT_Send.Add(el.Name.LocalName, el.Value);
                }
            }
        }




    }

}

