using System;
using System.Collections.Generic;
using System.Text;

namespace OTAService
{

    public class cls_APP_OTA_Ack
    {
        public string Trace_ID { get; set; }
        public string Version { get; set; }
        public string Timestamp { get; set; }
        public string Status { get; set; }
        public string ProcrssID { get; set; }
    }


    public class cls_Cmd_OTA
    {
        public string Trace_ID { get; set; }
        public string FTP_Server { get; set; }
        public string FTP_Port { get; set; }
        public string User_name { get; set; }
        public string Password { get; set; }
        public string App_Name { get; set; }
        public string Current_Version { get; set; }
        public string New_Version { get; set; }
        public string Process_ID { get; set; }
        public string Image_Name { get; set; }
        public string MD5_String { get; set; }
    }


    public class cls_Cmd_OTA_Ack
    {
        public string Trace_ID { get; set; }
        public string Cmd_Result { get; set; }
        public string App_Name { get; set; }
        public string New_Version { get; set; }
        public string MD5_String { get; set; }
    }

}
