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
using IniParser;
using IniParser.Model;
using NLog;




namespace Dotnet_JOB_Client
{
    class Program
    {

        //每支程式以不同GUID當成Mutex名稱，可避免執行檔同名同姓的風險
        static string appGuid = "{B19DAFCB-729C-43A6-8232-F3C31BB4E404}";

        //- 1. 宣告 MQTT 實體物件 
        private static bool keepRunning = true;
        private static IMqttClient client = new MqttFactory().CreateMqttClient();

        //- 2. MISC like Config ini or other 
        private static FileIniDataParser parser = new FileIniDataParser();
        private static IniData Config = parser.ReadFile(AppContext.BaseDirectory + "/settings/Task_Client.ini");

        //--3. SetLog
        private static Logger logger = NLog.LogManager.GetCurrentClassLogger();
      

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
                    string Register_Topic = "Topic";
                    string Collecter_Topic = "Topic";

                    var options = new MqttClientOptionsBuilder()
                        .WithClientId(Config["MQTT"]["ClinetID"])
                        .WithTcpServer(Config["MQTT"]["BrokerIP"], Convert.ToInt32(Config["MQTT"]["BrokerPort"]))
                        .Build();

                    //- 1. setting receive topic defect # for all
                    client.Connected += async (s, e) =>
                    {
                        logger.Info("Connect TO MQTT Server");
                        await client.SubscribeAsync(new TopicFilterBuilder().WithTopic(Register_Topic).WithAtMostOnceQoS().Build());
                        await client.SubscribeAsync(new TopicFilterBuilder().WithTopic(Collecter_Topic).WithAtMostOnceQoS().Build());
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
            string topic = e.ApplicationMessage.Topic;
            string message = Encoding.UTF8.GetString(e.ApplicationMessage.Payload);

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

     
       
    }

}

