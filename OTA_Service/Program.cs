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
using System.Net;
using System.Diagnostics;



using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Ionic.Zip;
using NLog;
using System.Security.Cryptography;



namespace OTAService
{
    class Program
    {

        //每支程式以不同GUID當成Mutex名稱，可避免執行檔同名同姓的風險
        static string appGuid = "{B19DAFDD-729C-43A7-8232-F3C31BB4E404}";

        static string osNameAndVersion = System.Runtime.InteropServices.RuntimeInformation.OSDescription;

        //- 1. 宣告 MQTT 實體物件 
        private static bool keepRunning = true;
        private static IMqttClient client = new MqttFactory().CreateMqttClient();

        //--2. SetLog
        private static Logger logger = NLog.LogManager.GetCurrentClassLogger();

        //--3. config 
        private static Dictionary<string, string> dic_SYS_Setting = null;
        private static Dictionary<string, string> dic_MQTT_Basic = null;
        private static Dictionary<string, string> dic_MQTT_Recv = null;
        private static Dictionary<string, string> dic_MQTT_Send = null;
        private static Dictionary<string, cls_APP_OTA_Ack> dic_PID = null;
        private static Dictionary<string, cls_Cmd_OTA_Ack>  dic_APP_OTA = null;

        //--4. Set Const Value 
        private const string Gateway_ID = "GateWayID";
        private const string Device_ID = "DeviceID";

        //--5. 設定 OTA Download Path and ZIP Path and Http Path 
        private const string OTA_DL_Path = "OTA_Download_Path";
        private const string OTA_ZIP_Path = "OTA_ZIP_Path";
        private const string OTA_Http_Path = "OTA_Http_Path";
        private const string OTA_Http_Remote_Path = "OTA_Http_Remote_Path";


        private static  int iHB_interval = 60000;

        //---6. Routin_Job
        private static System.Threading.Timer timer_routine_job;

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
                
                   // * Get Procrss ID sample code 
                   // Process currentProcess = Process.GetCurrentProcess();
                   // string pid =  currentProcess.Id.ToString();


                    //---- Create APP OTA Ack Dictionary ------
                    dic_PID = new Dictionary<string, cls_APP_OTA_Ack>();
                    dic_APP_OTA = new Dictionary<string, cls_Cmd_OTA_Ack>();



                    logger.Info("Welcome DotNet Core C# MQTT Client");
                    string Config_Path = AppContext.BaseDirectory + "/settings/Setting.xml";

                    logger.Info("Load MQTT Config From File: " + Config_Path);
                    Load_Xml_Config_To_Dict(Config_Path);

                    logger.Info(string.Format("MQTT Broker ID :{0}, Port No : {1}.", dic_MQTT_Basic["BrokerIP"], dic_MQTT_Basic["BrokerPort"]));
                    logger.Info("Load MQTT Config successful");

                    var options = new MqttClientOptionsBuilder()
                        .WithClientId(dic_MQTT_Basic["ClinetID"])
                        .WithTcpServer(dic_MQTT_Basic["BrokerIP"], Convert.ToInt32(dic_MQTT_Basic["BrokerPort"]))
                        .Build();

                    //- 1. setting receive topic defect # for all
                    client.Connected += async (s, e) =>
                    {
                        logger.Info("Connect TO MQTT Server successful");

                        foreach (KeyValuePair<string, string> kvp in dic_MQTT_Recv)
                        {
                            string Subscrive_Topic = kvp.Value;
                            await client.SubscribeAsync(new TopicFilterBuilder().WithTopic(Subscrive_Topic).WithAtMostOnceQoS().Build());
                            logger.Info("MQTT-Subscribe-Topic : " + Subscrive_Topic);
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

                    

                    //-  5.2 set thread pool max
                    ThreadPool.SetMaxThreads(16, 16);
                    ThreadPool.SetMinThreads(4, 4);

                    //-- 5.3 Get OS Name and Version 
                    var osNameAndVersion = System.Runtime.InteropServices.RuntimeInformation.OSDescription;

                    //-- 5.4 Report HeartBeat Ratio

                    string tmpHBInterval = string.Empty;
                    if (dic_SYS_Setting.TryGetValue("HeartBeat", out tmpHBInterval))
                    {
                        int number = 60000;
                        if(Int32.TryParse(tmpHBInterval, out number))
                        {
                            iHB_interval = number * 1000;
                        } 
                    }

                    Timer_Routine_Job(iHB_interval);  //execute routine job 
                    logger.Info(string.Format("HeartBeat Report : {0} ms",iHB_interval.ToString()));
                    logger.Info("System Initial Finished");

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

        // For MQTT Receive Message and get Config Tag Name 
        static string GetSubscribeTagName(string AliasTopic)
        {
            string SubscribeTagName = string.Empty;
            SubscribeTagName = dic_MQTT_Recv.Where(p => p.Value.Equals(AliasTopic)).FirstOrDefault().Key;
            return SubscribeTagName;

        }
        static string GetSubscribeAliasTopic(string Topic)
        {
            string AliasTopic = Topic;
            string ReturnAliasTopic = string.Empty;
            string[] tmpTopic = Topic.Split('/');
            List<string> Compare = new List<string>();
            int IndexLimit = 0;

            foreach (KeyValuePair<string, string> kvp in dic_MQTT_Recv)
            {
                Compare.Clear();
                string[] tmpSource = kvp.Value.Split('/');
                if (tmpSource[tmpSource.Length - 1] != "#" && tmpSource.Length < tmpTopic.Length)
                    continue;

                IndexLimit = Math.Min(tmpSource.Length, tmpTopic.Length);
                for (int i = 0; i < IndexLimit; i++)
                {
                    if (tmpSource[i] == "")
                        continue;

                    if (tmpSource[i] == tmpTopic[i])
                    {
                        Compare.Add(tmpSource[i]);
                    }
                    if (tmpSource[i] == "+")
                    {
                        Compare.Add(tmpSource[i]);
                    }
                    if (tmpSource[i] == "#")
                    {
                        Compare.Add(tmpSource[i]);
                        break;
                    }
                }
                string CompareResult = "/" + String.Join("/", Compare).ToString();
                if (kvp.Value.ToString() == CompareResult)
                {
                    AliasTopic = kvp.Value.ToString();
                    break;
                }

            }

            ReturnAliasTopic = AliasTopic;
            return ReturnAliasTopic;
        }

        // Report HeartBeat Timer Job
        static void Timer_Routine_Job(int interval)
        {
            if (interval == 0)
                interval = 10000;  // 10s

            System.Threading.Thread Thread_Timer_Report_EDC = new System.Threading.Thread
            (
               delegate (object value)
               {
                   int Interval = Convert.ToInt32(value);
                   timer_routine_job = new System.Threading.Timer(new System.Threading.TimerCallback(Routine_TimerTask), null, 1000, Interval);
               }
            );
            Thread_Timer_Report_EDC.Start(interval);
        }

        // Report HeartBeat Timer Task 
        static void Routine_TimerTask(object timerState)
        {
            try
            {
                string _OTA_App_key = "HeartBeat";
                string _Publish_OTA_Topic = dic_MQTT_Send[_OTA_App_key].Replace("{GateWayID}", dic_SYS_Setting[Gateway_ID]);
                string _Publish_OTA_Message = JsonConvert.SerializeObject(new { Trace_ID = DateTime.Now.ToString("yyyyMMddHHmmssfff"), Cmd = "OTA_HB" }, Formatting.Indented);
                client_Publish_To_Broker(_Publish_OTA_Topic, _Publish_OTA_Message);
                logger.Info(string.Format ("Report OTA HeartBeat, Topoc : {0} .", _Publish_OTA_Topic));

            }
            catch (Exception ex)
            {
                logger.Error("Report OTA HeartBeat Error Msg"+ ex.Message);
            }
        }


        // Handle MQTT Receive Message
        static void client_PublishArrived(object sender, MqttApplicationMessageReceivedEventArgs e)
        {
            string topic = e.ApplicationMessage.Topic;
            string message = Encoding.UTF8.GetString(e.ApplicationMessage.Payload);
            string AliasTopic = GetSubscribeAliasTopic(topic);
            string SubsTopic_Tag = GetSubscribeTagName(AliasTopic);

            switch(SubsTopic_Tag)
            {
                case "Host_OTA":
                     Thread thread = new Thread(() => ProcrssOTA(topic, message));
                     thread.Start();
                    break;

                case "IOT_OTA_Ack":
                case "Worker_OTA_Ack":
                    try
                    {
                        OTAService.cls_APP_OTA_Ack OTA_Ack = JsonConvert.DeserializeObject<OTAService.cls_APP_OTA_Ack>(message);
                        // string to datetime sample code
                        //DateTime dt = DateTime.ParseExact(dateString, "yyyyMMddHHmmssfff", System.Globalization.CultureInfo.CurrentCulture);
                        if (OTA_Ack.Status != "OTA")
                        {
                            logger.Error("IOT/Work OTA Ack Receive Message but Status not OTA so Skip Processing");
                            return;
                        }

                        lock (dic_PID)
                        {
                            dic_PID.Add(OTA_Ack.Trace_ID, OTA_Ack);
                        }
                    }

                    catch (Exception ex)
                    {
                        logger.Error("Handle IOT/Work OTA Ack Receive exception msg : "+ ex.Message);
                    }
                    break;

                case "IOT_OTA_Init":
                case "Worker_OTA_Init":
                case "Frameware_OTA_Init":

                    string _Publish_Topic = dic_MQTT_Send["Host_OTA_Ack"].Replace("{GateWayID}", dic_SYS_Setting[Gateway_ID]).Replace("{DeviceID}", dic_SYS_Setting[Device_ID]);
                    string AppName = string.Empty;
                    // from topic paser app name
                    if(dic_APP_OTA.ContainsKey(AppName))
                    {
                        OTAService.cls_Cmd_OTA_Ack OTA_CMD_Ack = dic_APP_OTA[AppName];
                        string _Publish_Message = JsonConvert.SerializeObject(OTA_CMD_Ack);
                        client_Publish_To_Broker(_Publish_Topic, _Publish_Message);
                        lock(dic_APP_OTA)
                        {
                            dic_APP_OTA.Remove(AppName);
                        }
                    }

                    break;

                default:
                    break;

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
            string[] Topic = topic.Split('/');
            string GateWayID = Topic[2].ToString();
            string DeviceID = Topic[3].ToString();

            OTAService.cls_Cmd_OTA OTA_CMD = JsonConvert.DeserializeObject<cls_Cmd_OTA>(payload);

            string OTA_Result = string.Empty;
            string OTA_Key = string.Concat(OTA_CMD.App_Name, "_", OTA_CMD.Trace_ID);

            string RemotePath = string.Concat("ftp://", OTA_CMD.FTP_Server, "/", OTA_CMD.Image_Name);

            string LocalPath = Path.Combine(dic_SYS_Setting[OTA_DL_Path],OTA_CMD.Trace_ID);
            string UnZIPPath   = Path.Combine(dic_SYS_Setting[OTA_ZIP_Path],OTA_CMD.Trace_ID);
            string Real_UnZIPPath = Path.Combine(dic_SYS_Setting[OTA_ZIP_Path], OTA_CMD.Trace_ID,OTA_CMD.App_Name);

            //------ 使用Local 變數 ------
            string _APP_OTA_Topic_key = string.Empty;

            string _Publish_APP_OTA_Topic = string.Empty;
            string _Publish_APP_OTA_Message = string.Empty;

            //------- Check Download Path Directory exist or not -------
            if (!Directory.Exists(Path.GetDirectoryName(LocalPath)))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LocalPath));
            }

            try
            {
                WebClient client = new WebClient();
                client.Credentials = new NetworkCredential(OTA_CMD.User_name, OTA_CMD.Password);
                client.DownloadFile(RemotePath, LocalPath);

                logger.Info("Download file finished");

                string strMD5 = GetMD5HashFromFile(LocalPath);

                logger.Info("Check MD5");

                if (strMD5.Equals(OTA_CMD.MD5_String))
                {
                    logger.Info("UnZIP file name = " + LocalPath);
                    using (var zip = ZipFile.Read(LocalPath))
                    {
                        foreach (var zipEntry in zip)
                        {
                            zipEntry.Extract(Real_UnZIPPath, ExtractExistingFileAction.OverwriteSilently);
                        }
                    }

                   switch( OTA_CMD.App_Name)   
                    {
                        case "IOT":
                        case "WORKER":

                             _APP_OTA_Topic_key = string.Concat(OTA_CMD.App_Name, "_OTA");
                             _Publish_APP_OTA_Topic = dic_MQTT_Send[_APP_OTA_Topic_key].Replace("{GateWayID}", dic_SYS_Setting[Gateway_ID]).Replace("{DeviceID}", dic_SYS_Setting[Device_ID]);
                             _Publish_APP_OTA_Message = JsonConvert.SerializeObject(new { Trace_ID = OTA_Key, Cmd = "OTA" }, Formatting.Indented);
                             client_Publish_To_Broker(_Publish_APP_OTA_Topic, _Publish_APP_OTA_Message);

                            Thread.Sleep(1000); // Wait 10 s 

                            int intProcessID = 0;
                            string strProcessID = string.Empty;

                            lock(dic_PID)
                            {
                                if (dic_PID.ContainsKey(OTA_Key))
                                {
                                    strProcessID = dic_PID[OTA_Key].ProcrssID;
                                    dic_PID.Remove(OTA_Key);

                                    if (int.TryParse(strProcessID, out intProcessID))
                                    {
                                        if (ProcessExists(intProcessID))
                                        {
                                            Process processToKill = Process.GetProcessById(intProcessID);
                                            processToKill.Kill();
                                            // Array.ForEach(Process.GetProcessesByName("cmd"), x => x.Kill());  // cmd line colsed ?
                                        }
                                    }
                                }
                                else
                                {

                                    // logging no receive Ack information 
                                }
                            }

                           

                            Thread.Sleep(3000); // Wait 3 s

                            if (osNameAndVersion.Contains("Linux") || osNameAndVersion.Contains("Darwin") )
                            {
                                string shell_argument =string.Concat(OTA_CMD.App_Name, " ", UnZIPPath) ;


                                System.IO.DirectoryInfo APP_OTA_Dir = new System.IO.DirectoryInfo(Real_UnZIPPath);
  
                                IEnumerable<System.IO.FileInfo> APP_OTA_fileList = APP_OTA_Dir.GetFiles("*.*", System.IO.SearchOption.AllDirectories);
 
                                IEnumerable<System.IO.FileInfo> APP_OTA_fileQuery =
                                    from file in APP_OTA_fileList
                                    where file.Extension == ".bat"
                                    orderby file.Name
                                    select file;


                                if (APP_OTA_fileQuery.Count() == 1)
                                {
                                   
                                    foreach (System.IO.FileInfo fi in APP_OTA_fileQuery)
                                    {
                                        string changemod = string.Concat(@"chmod 755 ", fi.FullName);
                                        logger.Info(string.Format("Handle IOT/Work OTA Process Execute change mod scripts : {0}.", changemod));

                                        Execute_Linux_Command(changemod);

                                        string shell_cmd = string.Concat(@"sh ", fi.FullName, " ", shell_argument);
                                        logger.Info(string.Format("Handle IOT/Work OTA Process Execute Lunux Shell scripts : {0}.", shell_cmd));
                                        Execute_Linux_Command(shell_cmd);
                                        
                                    }

                                }
                                else if (APP_OTA_fileQuery.Count() > 1)
                                {
                                    var result = String.Join(", ", APP_OTA_fileList.Select(p => p.FullName).ToArray());
                                    logger.Error(string.Format("Handle IOT/Work OTA Process Execute more than one Lunux Shell scripts in folder {0}, Lists: {1}.", Real_UnZIPPath, result.ToString()));
                                }
                                else
                                {
                                    logger.Error(string.Format("Handle IOT/Work OTA Process Execute without Lunux Shell scripts in folder {0}.", Real_UnZIPPath));
                                }
                            }
                               
                            else
                            {
                                string shell_argument = string.Concat(UnZIPPath, " ", OTA_CMD.App_Name);

                                System.IO.DirectoryInfo Win_APP_OTA_Dir = new System.IO.DirectoryInfo(Real_UnZIPPath);

                                IEnumerable<System.IO.FileInfo> Win_APP_OTA_fileList = Win_APP_OTA_Dir.GetFiles("*.*", System.IO.SearchOption.AllDirectories);

                                IEnumerable<System.IO.FileInfo> Win_APP_OTA_fileQuery =
                                    from file in Win_APP_OTA_fileList
                                    where file.Extension == ".bat"
                                    orderby file.Name
                                    select file;


                                if (Win_APP_OTA_fileQuery.Count() == 1)
                                {
                                    foreach (System.IO.FileInfo fi in Win_APP_OTA_fileQuery)
                                    {

                                        ProcessStartInfo ProcInfo = new ProcessStartInfo();
                                        ProcInfo.FileName = fi.FullName;//執行的檔案名稱
                                        ProcInfo.WorkingDirectory = fi.DirectoryName;//檔案所在的目錄
                                        ProcInfo.Arguments = shell_argument;
                                        Process.Start(ProcInfo);
                            
                                        logger.Info(string.Format("Handle IOT/Work OTA Process Execute Windows Batch file : {0} and arguments : {1} .", fi.FullName, shell_argument));
                                    }

                                }
                                else if (Win_APP_OTA_fileQuery.Count() > 1)
                                {
                                    var result = String.Join(", ", Win_APP_OTA_fileQuery.Select(p => p.FullName).ToArray());
                                    logger.Error(string.Format("Handle IOT/Work OTA Process Execute more than one Windows Batch file in folder {0}, Lists: {1}.", Real_UnZIPPath, result.ToString()));
                                }
                                else
                                {
                                    logger.Error(string.Format("Handle IOT/Work OTA Process Without Windows Batch file in folder {0}.", Real_UnZIPPath));
                                }


                            }
                            
                            break;

                        case "FIRMWARE":

                            // Take a snapshot of the file system.  
                            string Firmware_Name = string.Empty;

                            
                            System.IO.DirectoryInfo dir = new System.IO.DirectoryInfo(Real_UnZIPPath);

                            // This method assumes that the application has discovery permissions  
                            // for all folders under the specified path.  
                            IEnumerable<System.IO.FileInfo> firmware_OTA_fileList = dir.GetFiles("*.*", System.IO.SearchOption.AllDirectories);

                            //Create the query  
                            IEnumerable<System.IO.FileInfo> firm_OTA_fileQuery =
                                from file in firmware_OTA_fileList
                                where file.Extension == ".bin"
                                orderby file.Name
                                select file;

                            //Execute the query. This might write out a lot of files!  
                            foreach (System.IO.FileInfo fi in firm_OTA_fileQuery)
                            {
                                Firmware_Name = Path.GetFileName(fi.FullName);
                                System.IO.File.Copy(fi.FullName, Path.Combine(dic_SYS_Setting[OTA_Http_Path], Firmware_Name), true);
                            }


                            string firmwarw_OTA_key = string.Concat("OTA_", DeviceID); // OTA cmd  no define gateway id or device id.
                            string OTA_Image_Path = string.Concat(dic_SYS_Setting[OTA_Http_Remote_Path], Firmware_Name);
                            string _Publish_OTA_Topic = dic_MQTT_Send[firmwarw_OTA_key]; 
                            string _Publish_OTA_Message = JsonConvert.SerializeObject(new { Type = "OTA", OTA_Path = OTA_Image_Path,Interval = 60000 }, Formatting.Indented);
                            client_Publish_To_Broker(_Publish_OTA_Topic, _Publish_OTA_Message);
                            OTA_Result = "OK";
                            break;

                        default:
                            logger.Error("OTA Update App is not in support list (IOT,Worker,Firmware) AppName : " + OTA_CMD.App_Name);
                            break;
                    }

                }
                else
                {
                    OTA_Result = "NG";
                    logger.Error(string.Format("Download File MD5 Check Mismatch, MD5 : {0}, OTA_Cmd : {1}", strMD5, payload));
                }



                string _Publish_Topic = dic_MQTT_Send["Host_OTA_Ack"].Replace("{GateWayID}", dic_SYS_Setting[Gateway_ID]).Replace("{DeviceID}", dic_SYS_Setting[Device_ID]);
                OTAService.cls_Cmd_OTA_Ack OTA_CMD_Ack = new OTAService.cls_Cmd_OTA_Ack();
                OTA_CMD_Ack.Trace_ID = OTA_CMD.Trace_ID;
                OTA_CMD_Ack.App_Name = OTA_CMD.App_Name;
                OTA_CMD_Ack.MD5_String = OTA_CMD.MD5_String;
                OTA_CMD_Ack.New_Version = OTA_CMD.New_Version;
                OTA_CMD_Ack.Cmd_Result = OTA_Result;
                string _Publish_Message = JsonConvert.SerializeObject(OTA_CMD_Ack);

                if (OTA_Result == "NG")
                {
                    client_Publish_To_Broker(_Publish_Topic, _Publish_Message);
                }
                else
                {
                    lock (dic_APP_OTA)
                    {
                        dic_APP_OTA.Add(OTA_CMD.App_Name, OTA_CMD_Ack);
                    }
                }
            }
            catch (Exception ex)
            {

                Console.WriteLine(ex.Message);
            }

           

        }

        static void Load_Xml_Config_To_Dict(string config_path)
        {
            XElement SettingFromFile = XElement.Load(config_path);

            XElement System_Setting = SettingFromFile.Element("system");

            XElement MQTT_Setting = SettingFromFile.Element("MQTT");
            XElement Basic_Setting = MQTT_Setting.Element("Basic_Setting");
            XElement Receive_Topic = MQTT_Setting.Element("Receive_Topic");
            XElement Send_Topic = MQTT_Setting.Element("Send_Topic");

            dic_SYS_Setting = new Dictionary<string, string>();
            dic_MQTT_Basic = new Dictionary<string, string>();
            dic_MQTT_Recv = new Dictionary<string, string>();
            dic_MQTT_Send = new Dictionary<string, string>();


            if (System_Setting != null)
            {
                dic_SYS_Setting.Clear();
                foreach (var el in System_Setting.Elements())
                {
                    dic_SYS_Setting.Add(el.Name.LocalName, el.Value);
                }
            }

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
                    string receive_topic = el.Value.Replace("{GateWayID}", dic_SYS_Setting[Gateway_ID]);
                    dic_MQTT_Recv.Add(el.Name.LocalName, receive_topic);
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

        public static string GetMD5HashFromFile(string fileName)
        {
            try
            {
                FileStream file = new FileStream(fileName, FileMode.Open);
                MD5 md5 = new MD5CryptoServiceProvider();
                byte[] retVal = md5.ComputeHash(file);
                file.Close();

                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < retVal.Length; i++)
                {
                    sb.Append(retVal[i].ToString("x2"));
                }
                return sb.ToString();
            }
            catch (Exception ex)
            {
                throw new Exception("GetMD5HashFromFile() fail,error:" + ex.Message);
            }
        }

        public static bool ProcessExists(int id)
        {
            return Process.GetProcesses().Any(x => x.Id == id);
        }

        public static void Execute_Linux_Command(string command)
        {
            Process proc = new System.Diagnostics.Process();
            proc.StartInfo.FileName = "/bin/bash";
            proc.StartInfo.Arguments = "-c \" " + command + " \"";
            proc.StartInfo.UseShellExecute = false;
            proc.StartInfo.RedirectStandardOutput = true;
            proc.Start();

            while (!proc.StandardOutput.EndOfStream)
            {
                Console.WriteLine(proc.StandardOutput.ReadLine());
            }


            proc.WaitForExit(1000);


            while (!proc.HasExited)
            {

            }

            Console.WriteLine("**************");
            Console.WriteLine(proc.ExitCode.ToString());

        }

    }
}

