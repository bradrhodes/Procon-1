/*  Copyright 2010 Zaeed (Matt Green)

    http://www.viridianphotos.com

    This file is part of Zaeed's Plugins for BFBC2 PRoCon.
    Zaeed's Plugins for BFBC2 PRoCon is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    Zaeed's Plugins for PRoCon is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.
    You should have received a copy of the GNU General Public License
    along with Zaeed's Plugins for BFBC2 PRoCon.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Net.Mail;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;

using PRoCon.Core;
using PRoCon.Core.Plugin;
using PRoCon.Core.Plugin.Commands;
using PRoCon.Core.Players;
using PRoCon.Core.Players.Items;
using PRoCon.Core.Battlemap;
using PRoCon.Core.Maps;

namespace PRoConEvents
{
    using EventType = PRoCon.Core.Events.EventType;
    using CapturableEvent = PRoCon.Core.Events.CapturableEvents;

    public class PBHackDetect : PRoConPluginAPI, IPRoConPluginInterface
    {
        private enum StringVariableName
        {
            SMTP_HOST,
            SMTP_MAIL,
            SMTP_ACCOUNT,
            SMTP_PASSWORD,

            TO_ADDRESS,
            SUBJECT,

            NO_PB_RECORD,
            NO_PBGUID,
            NO_IP_ADDRESS,
            PBGUID_INCORRECT_LENGTH,
            PBGUID_INVALID,
            BLANK_KILLER,
        }

        private enum IntVariableName
        {
            SMTP_PORT
        }

        private enum BoolVariableName
        {
            SMTP_SSL
        }

        private class Variable<T>
        {
            private string description;
            private T value;

            public string Description
            {
                get { return description; }
                private set { description = value; }
            }

            public T Value
            {
                get { return value; }
                set { this.value = value; }
            }

            public Variable(string description, T value)
            {
                Description = description;
                Value = value;
            }
        }

        private Dictionary<StringVariableName, Variable<string>> stringSettings;
        private Dictionary<IntVariableName, Variable<int>> intSettings;
        private Dictionary<BoolVariableName, Variable<bool>> boolSettings;

        private string strHostName;
        private string strPort;
        private string strPRoConVersion;
        private bool readySetGo;

        public Dictionary<String, CPlayerInfo> normalPlayer;
        private Dictionary<String, CPunkbusterInfo> punkbusterPlayer;
        private List<String> joinDelay;

        public PBHackDetect()
        {

            this.normalPlayer = new Dictionary<String, CPlayerInfo>();
            this.punkbusterPlayer = new Dictionary<String, CPunkbusterInfo>();
            this.joinDelay = new List<String>();
            this.readySetGo = false;

            stringSettings = new Dictionary<StringVariableName, Variable<string>>();
            stringSettings.Add(StringVariableName.SMTP_HOST, new Variable<string>("SMTP|Host", ""));
            stringSettings.Add(StringVariableName.SMTP_MAIL, new Variable<string>("SMTP|E-mail", ""));
            stringSettings.Add(StringVariableName.SMTP_ACCOUNT, new Variable<string>("SMTP|Account", ""));
            stringSettings.Add(StringVariableName.SMTP_PASSWORD, new Variable<string>("SMTP|Password", ""));

            stringSettings.Add(StringVariableName.TO_ADDRESS, new Variable<string>("E-mail|To address", ""));
            stringSettings.Add(StringVariableName.SUBJECT, new Variable<string>("E-mail|Subject", ""));
            stringSettings.Add(StringVariableName.NO_PB_RECORD, new Variable<string>("E-mail|No PB record message ({0} = player name, {1} = player GUID)", "{0} ({1}) has no PB record."));
            stringSettings.Add(StringVariableName.NO_PBGUID, new Variable<string>("E-mail|No PBGUID message ({0} = player name, {1} = player GUID)", "{0} ({1}) has no PBGUID."));
            stringSettings.Add(StringVariableName.PBGUID_INCORRECT_LENGTH, new Variable<string>("E-mail|PBGUID is incorrect length message ({0} = player name, {1} = player GUID)", "{0} ({1}) has incorrect PBGUID length."));
            stringSettings.Add(StringVariableName.PBGUID_INVALID, new Variable<string>("E-mail|PBGUID is invalid message ({0} = player name, {1} = player GUID)", "{0} ({1}) has invalid PBGUID."));
            stringSettings.Add(StringVariableName.BLANK_KILLER, new Variable<string>("E-mail|Blank killer message", "Blank killer detected."));

            intSettings = new Dictionary<IntVariableName, Variable<int>>();
            intSettings.Add(IntVariableName.SMTP_PORT, new Variable<int>("SMTP|Port", 587));

            boolSettings = new Dictionary<BoolVariableName, Variable<bool>>();
            boolSettings.Add(BoolVariableName.SMTP_SSL, new Variable<bool>("SMTP|Use SSL?", true));
        }

        #region InitStuff
        public string GetPluginName()
        {
            return "PB Hack Logger";
        }

        public string GetPluginVersion()
        {
            return "1.0.0.0";
        }

        public string GetPluginAuthor()
        {
            return "Zaeed";
        }

        public string GetPluginWebsite()
        {
            return "www.viridianphotos.com";
        }

        public string GetPluginDescription()
        {
            return @"
<p>If you find my plugins useful, please feel free to donate</p>
<blockquote>
<form action=""https://www.paypal.com/cgi-bin/webscr/"" method=""POST"" target=""_blank"">
<input type=""hidden"" name=""cmd"" value=""_s-xclick"">
<input type=""hidden"" name=""encrypted"" value=""-----BEGIN PKCS7-----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-----END PKCS7-----"">
<input type=""image"" src=""https://www.paypal.com/en_AU/i/btn/btn_donate_LG.gif"" border=""0"" name=""submit"" alt=""PayPal - The safer, easier way to pay online."">
<img alt="""" border=""0"" src=""https://www.paypal.com/en_AU/i/scr/pixel.gif"" width=""1"" height=""1"">
</form>
</blockquote>


<h2>Description</h2>
<p>The Latency Manager plugin gives you two options for removing players that might be causing lag on your server.  The first option is the Country Kicking method.  This allows you to either list countries that are not allowed on your server, or specify only the countrys allowed. 

The second option is to kick based on the players Ping.  Two methods are avialable here; instant kick, and average based kick.  The averaging method samples the players ping over time, and then only kicks if their average ping is above the threshold.

</p>
";
        }

        public void OnPluginLoaded(string strHostName, string strPort, string strPRoConVersion)
        {
            this.strHostName = strHostName;
            this.strPort = strPort;
            this.strPRoConVersion = strPRoConVersion;
            this.RegisterEvents(this.GetType().Name, "OnPunkbusterPlayerInfo", "OnPlayerLeft", "OnPlayerJoin", "OnPlayerAuthenticated", "OnListPlayers", "OnPlayerKilled");
        }

        public void OnPluginEnable()
        {
            this.ExecuteCommand("procon.protected.pluginconsole.write", "^bPB Hack Logger ^2Enabled!");

        }

        public void OnPluginDisable()
        {
            this.ExecuteCommand("procon.protected.pluginconsole.write", "^bPB Hack Logger ^1Disabled");
        }

        public List<CPluginVariable> GetDisplayPluginVariables()
        {
            List<CPluginVariable> variables = new List<CPluginVariable>();

            foreach (StringVariableName name in stringSettings.Keys)
            {
                Variable<string> v = stringSettings[name];

                string value = v.Value;
                if (v.Description.ToLower().Contains("password"))
                    value = Regex.Replace(value, ".", "*");

                variables.Add(new CPluginVariable(v.Description, "string", value));
            }

            foreach (IntVariableName name in intSettings.Keys)
                variables.Add(new CPluginVariable(intSettings[name].Description, "int", string.Concat(intSettings[name].Value)));

            foreach (BoolVariableName name in boolSettings.Keys)
                variables.Add(new CPluginVariable(boolSettings[name].Description, "bool", string.Concat(boolSettings[name].Value)));

            return variables;
        }

        public List<CPluginVariable> GetPluginVariables()
        {
            return GetDisplayPluginVariables();
        }

        public void SetPluginVariable(String strVariable, String strValue)
        {
            foreach (StringVariableName name in stringSettings.Keys)
            {
                Variable<string> v = stringSettings[name];

                if (v.Description.Contains(strVariable))
                {
                    v.Value = strValue;

                    if (v.Description.ToLower().Contains("password"))
                        ConsoleWrite("Value for " + name + " changed.");
                    else
                        ConsoleWrite("Value for " + name + " changed to " + strValue);

                    return;
                }
            }

            foreach (IntVariableName name in intSettings.Keys)
            {
                Variable<int> v = intSettings[name];

                if (v.Description.Contains(strVariable))
                {
                    try
                    {
                        v.Value = int.Parse(strValue);
                    }
                    catch
                    {
                        ConsoleException("Invalid value for " + name + ": " + strValue);
                        return;
                    }

                    ConsoleWrite("Value for " + name + " changed to " + strValue);
                    return;
                }
            }

            foreach (BoolVariableName name in boolSettings.Keys)
            {
                Variable<bool> v = boolSettings[name];

                if (v.Description.Contains(strVariable))
                {
                    try
                    {
                        v.Value = bool.Parse(strValue);
                    }
                    catch
                    {
                        ConsoleException("Invalid value for " + name + ": " + strValue);
                        return;
                    }

                    ConsoleWrite("Value for " + name + " changed to " + strValue);
                    return;
                }
            }
        }
        #endregion

        public override void OnPunkbusterPlayerInfo(CPunkbusterInfo cpbiPlayer)
        {
            if (cpbiPlayer != null)
            {
                if (!this.punkbusterPlayer.ContainsKey(cpbiPlayer.SoldierName))
                {
                    this.punkbusterPlayer.Add(cpbiPlayer.SoldierName, cpbiPlayer);
                }
                else
                {
                    this.punkbusterPlayer[cpbiPlayer.SoldierName] = cpbiPlayer;
                }
                if (!readySetGo) { readySetGo = true; }
            }
        }

        public override void OnPlayerJoin(string strSoldierName)
        {
            if (!this.normalPlayer.ContainsKey(strSoldierName))
            {
                this.normalPlayer.Add(strSoldierName, new CPlayerInfo(strSoldierName, "", 0, 24));
                this.joinDelay.Add(strSoldierName);
                this.ExecuteCommand("procon.protected.tasks.add", "RemoveJoinDelayProtection", "60", "1", "1", "procon.protected.plugins.call", "PBHackDetect", "RemoveProtection", strSoldierName);
            }
        }

        public void RemoveProtection(string strSoldierName)
        {
            if (this.joinDelay.Contains(strSoldierName) == true)
            {
                this.joinDelay.Remove(strSoldierName);
            }
        }

        public override void OnPlayerLeft(CPlayerInfo cpiPlayer)
        {
            if (this.normalPlayer.ContainsKey(cpiPlayer.SoldierName) == true)
            {
                this.normalPlayer.Remove(cpiPlayer.SoldierName);
            }

            if (this.punkbusterPlayer.ContainsKey(cpiPlayer.SoldierName) == true)
            {
                this.punkbusterPlayer.Remove(cpiPlayer.SoldierName);
            }
        }

        //Do all our checks here
        public override void OnPlayerKilled(Kill kKillerVictimDetails)
        {
            if ((kKillerVictimDetails != null) && readySetGo)
            {
                CPlayerInfo killer = kKillerVictimDetails.Killer;
                if ((String.IsNullOrEmpty(killer.SoldierName)) || (this.joinDelay.Contains(killer.SoldierName)))
                {
                    //	WriteLog("Killers name is blank.  They just killed " + kKillerVictimDetails.Victim.SoldierName);
                }
                else
                {
                    if (this.normalPlayer.ContainsKey(killer.SoldierName))
                    {
                        //Pass
                    }
                    else
                    {
                        WriteLog(String.Format(stringSettings[StringVariableName.NO_PB_RECORD].Value, killer.SoldierName, killer.GUID));
                    }

                    if (this.punkbusterPlayer.ContainsKey(killer.SoldierName))
                    {
                        CPunkbusterInfo PBPlayer = this.punkbusterPlayer[killer.SoldierName];

                        if (String.IsNullOrEmpty(PBPlayer.GUID) == true)
                        {
                            WriteLog(String.Format(stringSettings[StringVariableName.NO_PBGUID].Value, killer.SoldierName, killer.GUID));
                        }

                        if (PBPlayer.GUID.Length != 32)
                        {
                            WriteLog(String.Format(stringSettings[StringVariableName.PBGUID_INCORRECT_LENGTH].Value, killer.SoldierName, killer.GUID));
                        }

                        if (!(System.Text.RegularExpressions.Regex.IsMatch(PBPlayer.GUID, @"^[a-zA-Z0-9]+$")))
                        {
                            WriteLog(String.Format(stringSettings[StringVariableName.PBGUID_INVALID].Value, killer.SoldierName, killer.GUID));
                        }

                        if (String.IsNullOrEmpty(PBPlayer.Ip) == true)
                        {
                            WriteLog(String.Format(stringSettings[StringVariableName.NO_IP_ADDRESS].Value, killer.SoldierName, killer.GUID));
                        }
                    }
                    else
                    {
                        WriteLog(String.Format(stringSettings[StringVariableName.NO_PB_RECORD].Value, killer.SoldierName, killer.GUID));
                    }
                }
            }
            else
            {
                WriteLog(stringSettings[StringVariableName.BLANK_KILLER].Value);
            }
        }

        public override void OnListPlayers(List<CPlayerInfo> lstPlayers, CPlayerSubset cpsSubset)
        {
            if (cpsSubset.Subset == CPlayerSubset.PlayerSubsetType.All)
            {
                foreach (CPlayerInfo cpiPlayer in lstPlayers)
                {
                    if (this.normalPlayer.ContainsKey(cpiPlayer.SoldierName))
                    {
                        this.normalPlayer[cpiPlayer.SoldierName] = cpiPlayer;
                    }
                    else
                    {
                        this.normalPlayer.Add(cpiPlayer.SoldierName, cpiPlayer);
                    }
                }
            }
        }

        public void WriteLog(string message)
        {
            ConsoleWrite(message);

            string path = Environment.CurrentDirectory + "\\Plugins\\PBHackLog";
            try
            {
                if (Directory.Exists(path))
                {
                }
                else
                {
                    DirectoryInfo di = Directory.CreateDirectory(path);
                }
            }
            catch (Exception)
            {
            }

            try
            {
                if (File.Exists(path + "\\PBHackLog.txt"))
                {
                    try
                    {
                        using (System.IO.StreamWriter file = new System.IO.StreamWriter(path + "\\PBHackLog.txt", true))
                        {
                            file.WriteLine(message + Environment.NewLine);
                        }
                    }
                    catch (Exception e)
                    {
                        this.ExecuteCommand("procon.protected.pluginconsole.write", "^bPBHackLog: Error appending " + e);
                    }
                }
                else
                {
                    System.IO.File.WriteAllText(path + "\\PBHackLog.txt", message);
                }
            }
            catch (Exception d)
            {
                this.ExecuteCommand("procon.protected.pluginconsole.write", "^bPBHackLog: Error creating new text file " + d);
            }

            SendMail(message);
        }

        public bool SendMail(string body)
        {
            Thread mail_thread = new Thread(new ThreadStart(delegate()
            {
                try
                {
                    string smtp_host = stringSettings[StringVariableName.SMTP_HOST].Value;
                    string smtp_account = stringSettings[StringVariableName.SMTP_ACCOUNT].Value;
                    string smtp_mail = stringSettings[StringVariableName.SMTP_MAIL].Value;
                    string smtp_password = stringSettings[StringVariableName.SMTP_PASSWORD].Value;
                    int smtp_port = intSettings[IntVariableName.SMTP_PORT].Value;
                    bool smtp_ssl = boolSettings[BoolVariableName.SMTP_SSL].Value;

                    MailMessage message = new MailMessage();

                    string address = stringSettings[StringVariableName.TO_ADDRESS].Value;
                    string subject = stringSettings[StringVariableName.SUBJECT].Value;

                    //split at the commas to allow multiple addresses
                    List<String> address_list = new List<string>(address.Split(','));
                    address_list.RemoveAll(delegate(String i) { return i == null || i.Trim().Length == 0; });

                    foreach (String addrs in address_list)
                        message.To.Add(addrs.Trim());

                    message.Subject = subject;
                    message.From = new MailAddress(smtp_mail);
                    message.Body = body;
                    SmtpClient smtp = new SmtpClient(smtp_host, smtp_port);
                    smtp.EnableSsl = smtp_ssl;
                    smtp.Credentials = new NetworkCredential(smtp_account, smtp_password);
                    smtp.Send(message);
                }
                catch (Exception e)
                {
                    ConsoleException("Error sending mail: " + e);
                }
            }));

            mail_thread.Start();

            return true;
        }

        public enum MessageType { Warning, Error, Exception, Normal };

        private string FormatMessage(string msg, MessageType type)
        {
            string prefix = "[^b" + GetPluginName() + "^n] ";

            switch (type)
            {
                case MessageType.Warning:
                    prefix += "^1^bWARNING^0^n: ";
                    break;
                case MessageType.Error:
                    prefix += "^1^bERROR^0^n: ";
                    break;
                case MessageType.Exception:
                    prefix += "^1^bEXCEPTION^0^n: ";
                    break;
            }

            return prefix + msg;
        }

        public void LogWrite(string msg)
        {
            this.ExecuteCommand("procon.protected.pluginconsole.write", msg);
        }

        public void ConsoleWrite(string msg, MessageType type)
        {
            LogWrite(FormatMessage(msg, type));
        }

        public void ConsoleWrite(string msg)
        {
            ConsoleWrite(msg, MessageType.Normal);
        }

        public void ConsoleWarn(string msg)
        {
            ConsoleWrite(msg, MessageType.Warning);
        }

        public void ConsoleError(string msg)
        {
            ConsoleWrite(msg, MessageType.Error);
        }

        public void ConsoleException(string msg)
        {
            ConsoleWrite(msg, MessageType.Exception);
        }
    }
}
