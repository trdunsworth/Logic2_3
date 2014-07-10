using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.IO;
using System.Linq;
using System.Net.Mail;
using System.Text;
using System.Threading.Tasks;
using Oracle.DataAccess.Client;

namespace Logic2_3
{
    /// <summary>
    /// Hotcalls is, currently, a two-piece program which monitors the JCSOARCH CAD database for events which
    /// reach a collection of triggers. (Location, Call Type and Subtype, or Number of Arrived Units)
    /// The two parts are the poller which queries the IPS created tables and then consolidates that data into 
    /// JCSO created tables for evaluation and the logic which then evaluates the data. 
    /// If one or more of the triggers is matched, the logic program will create and send an email to subscribers
    /// using the county's SMTPRelayOut Exchange Server.
    /// The Poller should be started first and then the Logic started roughly 60 seconds later.
    /// Both programs are run by the Windows Server 2008 Task Scheduler on a 2 minute rotation roughly 60 secs apart.
    /// Both are installed and scheduled via the local Compgrp account.
    /// The poller and logic program files live in the following path:
    /// C:\Users\jcsoadmin\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Poller -or- Logic
    /// If there are errors, they are collected and written to files in the C:\temp folder. 
    /// Both programs also send out emails to Carter Wetherington and Tony Dunsworth for any errors for rapid response.
    /// This version was created in Visual Studio 2013 using the .NET 4.5.1 framework and the C#5.0 language specifications.
    /// Copyright: 2013-2014, Johnson County Sheriff's Office, all rights reserved.
    /// The first run of code in Version 2 in debug mode on 2014-04-09 was successful
    /// </summary>
    // Program: Hot Calls Logic Version 2 Write 2.3
    // Authors: Tony Dunsworth and Carter Wetherington
    // Date Created: 2014-04-09 (Version 2.0.2.3)
    // Current To-Do List 2014-04-09
    // 1. Review Location of Interest integration to ensure it is optimally configured. (Currently works, but minor algorithm adjustments may be made.)
    // 2. Add in an RSS feed and filter system to allow civilian subscription based on location and/or zip code.
    // 3. Discuss other additions and changes as wanted and needed by the administration team.
    // 4. Move the exception handling into new global functions which can be called individually.
    // 5. Work on coding better LINQ queries for better efficiency
    // 6. Look at moving portions of repeated code into classes or interfaces of their own to minimize repition in the code base.
    // 7. Consider removing redundant code which is not being used in the mail code base. 
    // 8. Move from odp.net x86 drivers to x64 drivers for a 64 bit machine when all drivers are compatible. 
    // 9. Move to EF version 6 when ODP.NET supports the same.
    class Program
    {
        public static string hcStr = ConfigurationManager.ConnectionStrings["LogicEntities"].ConnectionString;
        public static DateTime compTime = DateTime.Now.AddMinutes(-60);
        public static int batch = 150;
        public static char[] delimiters = new char[] { ';' };

        static void Main(string[] args)
        {
            // Main function which invokes the timer, scheduled to run every 120 seconds, which runs each individual method
            // Main start and stops with a time stamp for visual verification in debug mode
            // The try/catch block was added to allow any errors not caught elsewhere to be caught and mailed to the admins.
            try
            {
                Console.WriteLine("Evaluator has started at " + DateTime.Now.ToString("G"));
                LoiEval();
                TypeEval();
                CountEval();
                Console.WriteLine("Evaluator has ended at " + DateTime.Now.ToString("G"));
            }
            catch (Exception ex)
            {
                MailMessage errorMsg = new MailMessage();
                errorMsg.From = new MailAddress("jcso.exception@jocogov.org", "Hot Call Program Exception");
                errorMsg.To.Add(new MailAddress("tony.dunsworth@jocogov.org", "Tony Dunsworth"));
                errorMsg.To.Add(new MailAddress("carter.wetherington@jocogov.org", "Carter Wetherington"));
                errorMsg.Subject = "Hot Calls Evaluator Exception";
                errorMsg.Body = "The Hot Calls Evaluator is reporting the following error: " + ex.ToString() + " at " + DateTime.Now.ToString("G");
                SmtpClient errorPost = new SmtpClient();
                errorPost.Send(errorMsg);

                FileStream evalErrorLog = File.Open(@"C:\Temp\logicErrorLog.txt", FileMode.Append, FileAccess.Write);
                StreamWriter evalErrorWrite = new StreamWriter(evalErrorLog);
                evalErrorWrite.WriteLine("The Hot Calls Evaluator is reporting the following error: " + ex.ToString() + " at " + DateTime.Now.ToString("G"));
                evalErrorWrite.Close();
                evalErrorLog.Close();
            }
        }

        private static void LoiEval()
        {
            // This function reads the rows of the jc_hc_curent or curent_dev table and compares the addresses returned 
            // to the jc_hc_loi or loi_dev table for noted locations of interest (Schools, county buildings, hospitals, etc.)
            // A Console.WriteLine is written here for debugging purposes, it is to be commented out in the live version or the running test
            // Console.WriteLine("LoiEval entered at " + DateTime.Now.ToString("G"));
            // The lTime variable is set for 60 minutes prior to the run time.
            // Moved the time variables up with the connection string as a public accessible variable.
            // DateTime lTime = DateTime.Now.AddMinutes(-60);
            // Select the basic fields needed from the jc_hc_curent table to be compared.
            LogicEntities LoiEntities = new LogicEntities();
            var allCalls = from c in LoiEntities.JC_HC_CURENT
                           where c.EFEANME != null
                           select new
                           {
                               c.EID, c.NUM_1, c.TYCOD, c.SUB_TYCOD, c.ESTNUM, c.EDIRPRE, c.EFEANME, c.EFEATYP, c.AG_ID, c.AD_TS, c.ESZ, c.COMMENTS, c.LOI_EVAL, c.LOI_SENT, c.UNIT_COUNT
                           };
            foreach (var call in allCalls) 
            {
                int Eid = call.EID;
                string Num1 = call.NUM_1;
                string Tycod = call.TYCOD;
                string Subcod = call.SUB_TYCOD;
                string Estnum = call.ESTNUM;
                string Edirpre = call.EDIRPRE;
                string Efeanme = call.EFEANME;
                string Efeatyp = call.EFEATYP;
                string Agid = call.AG_ID;
                string Adts = call.AD_TS;
                int? Esz = call.ESZ;
                string Comments = call.COMMENTS;
                string Leval = call.LOI_EVAL;
                string Lsent = call.LOI_SENT;
                int? UnitsArv = call.UNIT_COUNT;

                if (Leval == "F")
                {
                    int lEvalTime = string.CompareOrdinal(Adts.Substring(0, 14), compTime.ToString("yyyyMMddHHmmss"));
                    if (lEvalTime >= 0)
                    {
                        var loiLists = from l in LoiEntities.JC_HC_LOI
                                       where l.ACTIVE == "T"
                                       select new
                                       {
                                           l.ID, l.ESTNUM, l.EDIRPRE, l.EFEANME, l.EFEATYP, l.HNDR_BLCK, l.LOI_GRP_ID
                                       };

                        foreach (var loi in loiLists)
                        {
                            int lId = loi.ID;
                            string lEstnum = loi.ESTNUM;
                            string lEdirpre = loi.EDIRPRE;
                            string lEfeanme = loi.EFEANME;
                            string lEfeatyp = loi.EFEATYP;
                            string lHndrBlck = loi.HNDR_BLCK;
                            string lGrpId = loi.LOI_GRP_ID;

                            if (Estnum == lEstnum && Edirpre == lEdirpre)
                            {
                                try
                                {
                                    string[] callComms = Comments.Split(delimiters);
                                    string dispTime = Adts.Substring(8, 2) + ":" + Adts.Substring(10, 2);
                                    List<string> CivEmails = new List<string>();
                                    List<string> LeoEmails = new List<string>();

                                    var loiUsers = from u in LoiEntities.JC_HC_USERS
                                                   where (from g in LoiEntities.JC_HC_USR_SND
                                                          where g.LOI_ID == lId || g.GRP_ID == lGrpId
                                                          select new { g.USR_ID }).Equals(u.ID) && u.OOF == "F"
                                                   select new { u.EMAIL, u.LEO };

                                    foreach (var lUser in loiUsers)
                                    {
                                        string lEmail = lUser.EMAIL;
                                        string lLeo = lUser.LEO;

                                        if (lLeo == "F")
                                        {
                                            CivEmails.Add(lEmail);
                                        }
                                        else
                                        {
                                            LeoEmails.Add(lEmail);
                                        }

                                        if (CivEmails.Count > 0)
                                        {
                                            for (int i = 0; i < CivEmails.Count; i += batch)
                                            {
                                                using (MailMessage lMsg = new MailMessage())
                                                {
                                                    lMsg.From = new MailAddress("jcso.loicall@jocogov.org", "LOI CALL");
                                                    for (int j = i; (j < (i + batch)) && (j < CivEmails.Count); ++j)
                                                    {
                                                        lMsg.Bcc.Add(new MailAddress(CivEmails[j].Trim()));
                                                    }
                                                    lMsg.Subject = Agid.Trim() + " LOI CALL";
                                                    lMsg.Body = "Location: " + lHndrBlck.Trim() + " Block of " + Edirpre.Trim() + " " + Efeanme.Trim() + " " + Efeatyp.Trim() + "\n";
                                                    lMsg.Body += "Agency: " + Agid.Trim() + "\tUnits Arrived: " + UnitsArv + "\tEvent No: " + Num1.Trim() + "\n";
                                                    lMsg.Body += "Time Dispatched: " + dispTime.Trim() + "\tNature: " + Tycod.Trim() + " / " + Subcod.Trim();
                                                    SmtpClient lPost = new SmtpClient();
                                                    lPost.Send(lMsg);
                                                }
                                            }
                                        }
                                        if (LeoEmails.Count > 0)
                                        {
                                            for (int i = 0; i < LeoEmails.Count; i += batch)
                                            {
                                                using (MailMessage lMsg = new MailMessage())
                                                {
                                                    lMsg.From = new MailAddress("jcso.loicall@jocogov.org", "LOI CALL");
                                                    for (int j = i; (j < (i + batch)) && (j < LeoEmails.Count); ++j )
                                                    {
                                                        lMsg.Bcc.Add(new MailAddress(LeoEmails[j].Trim()));
                                                    }
                                                    lMsg.Subject = Agid.Trim() + " LOI CALL";
                                                    lMsg.Body = "Location: " + Estnum.Trim() + " " + Edirpre.Trim() + " " + Efeanme.Trim() + " " + Efeatyp.Trim() + "\n";
                                                    lMsg.Body += "Agency: " + Agid.Trim() + "\tUnits Arrived: " + UnitsArv + "\tEvent No: " + Num1.Trim() + "\n";
                                                    lMsg.Body += "Time Dispatched: " + dispTime.Trim() + "\tNature: " + Tycod.Trim() + " / " + Subcod.Trim() + "\n\n";
                                                    foreach (string s in callComms)
                                                    {
                                                        lMsg.Body += s.Trim() + "\n";
                                                    }
                                                    SmtpClient lPost = new SmtpClient();
                                                    lPost.Send(lMsg);
                                                }
                                            }
                                        }
                                    }
                                }
                                catch (OracleException ox)
                                {
                                    MailMessage errorMsg = new MailMessage();
                                    errorMsg.From = new MailAddress("jcso.exception@jocogov.org", "Hot Call Oracle Exception");
                                    errorMsg.To.Add(new MailAddress("tony.dunsworth@jocogov.org", "Tony Dunsworth"));
                                    errorMsg.To.Add(new MailAddress("carter.wetherington@jocogov.org", "Carter Wetherington"));
                                    errorMsg.Subject = "Oracle Logic Evaluator Error";
                                    errorMsg.Body = "Oracle is reporting the following error: " + ox.ToString() + " at " + DateTime.Now.ToString("G");
                                    SmtpClient errorPost = new SmtpClient();
                                    errorPost.Send(errorMsg);

                                    FileStream evalErrorLog = File.Open(@"C:\Temp\devLogicError.txt", FileMode.Append, FileAccess.Write);
                                    StreamWriter evalErrorWrite = new StreamWriter(evalErrorLog);
                                    evalErrorWrite.WriteLine("Oracle is reporting the following error: " + ox.ToString() + " at " + DateTime.Now.ToString("G"));
                                    evalErrorWrite.Close();
                                    evalErrorLog.Close();
                                }
                                catch (Exception ex)
                                {
                                    MailMessage errorMsg = new MailMessage();
                                    errorMsg.From = new MailAddress("jcso.exception@jocogov.org", "Hot Call Oracle Exception");
                                    errorMsg.To.Add(new MailAddress("tony.dunsworth@jocogov.org", "Tony Dunsworth"));
                                    errorMsg.To.Add(new MailAddress("carter.wetherington@jocogov.org", "Carter Wetherington"));
                                    errorMsg.Subject = "Hot Calls Logic Evaluator Error";
                                    errorMsg.Body = "The Hot Calls Logic program is reporting the following error: " + ex.ToString() + " at " + DateTime.Now.ToString("G");
                                    SmtpClient errorPost = new SmtpClient();
                                    errorPost.Send(errorMsg);

                                    FileStream evalErrorLog = File.Open(@"C:\Temp\devLogicError.txt", FileMode.Append, FileAccess.Write);
                                    StreamWriter evalErrorWrite = new StreamWriter(evalErrorLog);
                                    evalErrorWrite.WriteLine("The Hot Calls Logic program is reporting the following error: " + ex.ToString() + " at " + DateTime.Now.ToString("G"));
                                    evalErrorWrite.Close();
                                    evalErrorLog.Close();
                                }
                                finally
                                {
                                    JC_HC_CURENT lUpdate = (from u in LoiEntities.JC_HC_CURENT
                                                            where u.EID == Eid && u.NUM_1 == Num1 && u.AD_TS == Adts
                                                            select u).FirstOrDefault();

                                    if (lUpdate != null)
                                    {
                                        lUpdate.LOI_EVAL = "T";
                                        lUpdate.LOI_SENT = "T";
                                    }

                                    JC_HC_SENT lSent = new JC_HC_SENT();
                                    lSent.EID = Eid;
                                    lSent.AG_ID = Agid;
                                    lSent.TYCOD = Tycod;
                                    lSent.SUB_TYCOD = Subcod;
                                    lSent.SENT_DT = DateTime.Now.ToString("yyyyMMddHHmmss");
                                    lSent.EMAIL_SENT = Comments;
                                    lSent.NUM_1 = Num1;

                                    LoiEntities.JC_HC_SENT.Add(lSent);
                                    LoiEntities.SaveChanges();
                                }
                            }
                        }
                    }
                    else
                    {
                        var lDefault = (from d in LoiEntities.JC_HC_CURENT
                                        where d.LOI_EVAL == "F"
                                        select d).First();
                        lDefault.LOI_EVAL = "T";
                        LoiEntities.SaveChanges();
                    }
                }
            }
            // Console.WriteLine("LoiEval exited at " + DateTime.Now.ToString("G"));
            return;
        }

        // This function will compare the Primary and Secondary Call Types to the types or types_dev tables
        // to see if the call is automatically "hot" by the type of call and the agnecy's request.
        // Since many of the things here are of similar structure to the function above, the comments will be a little more sparse.
        // The emailing portion has been duplicated in this and the following function to account for either full addresses
        // or cross streets if the actual address has not been entered into the system at the time this becomes a hot call.
        private static void TypeEval()
        {
            // Write in a Console.Writeline for debugging purposes. This is removed or commented out in live production code so it is 
            // cleaner and more efficient when running.
            // All DateTime strings are being reformatted to more user friendly versions.
            // Console.WriteLine("TypeDevEval has entered at: " + DateTime.Now.ToString("G"));
            // DateTime tTime = DateTime.Now.AddMinutes(-60);
            LogicEntities TypeEntities = new LogicEntities();
            var typeCalls = from c in TypeEntities.JC_HC_CURENT
                            where c.TYCOD != null
                            select new
                            {
                                c.EID,
                                c.NUM_1,
                                c.TYCOD,
                                c.SUB_TYCOD,
                                c.ESTNUM,
                                c.EDIRPRE,
                                c.EFEANME,
                                c.EFEATYP,
                                c.AG_ID,
                                c.AD_TS,
                                c.XSTREET1,
                                c.XSTREET2,
                                c.ESZ,
                                c.COMMENTS,
                                c.TYPE_EVAL,
                                c.HC_SENT,
                                c.UNIT_COUNT
                            };
            foreach (var call in typeCalls)
            {
                int Eid = call.EID;
                string Num1 = call.NUM_1;
                string Tycod = call.TYCOD;
                string Subcod = call.SUB_TYCOD;
                string Estnum = call.ESTNUM;
                string Edirpre = call.EDIRPRE;
                string Efeanme = call.EFEANME;
                string Efeatyp = call.EFEATYP;
                string Agid = call.AG_ID;
                string Adts = call.AD_TS;
                string Xstr1 = call.XSTREET1;
                string Xstr2 = call.XSTREET2;
                int? Esz = call.ESZ;
                string Comments = call.COMMENTS;
                string Teval = call.TYPE_EVAL;
                string Tsent = call.HC_SENT;
                int? UnitsArv = call.UNIT_COUNT;

                if (Teval == "F")
                {
                    int tEvalTime = string.CompareOrdinal(Adts.Substring(0, 14), compTime.ToString("yyyyMMddHHmmss"));
                    if (tEvalTime >= 0)
                    {
                        var typeLists = from t in TypeEntities.JC_HC_TYPES
                                        where t.ALWYS_SND == "T" && t.NEVR_SND == "F"
                                        select new
                                        {
                                            t.ID,
                                            t.TYCOD,
                                            t.SUB_TYCOD,
                                            t.AGENCY
                                        };
                        foreach (var type in typeLists)
                        {
                            int tId = type.ID;
                            string tTycod = type.TYCOD;
                            string tSubcod = type.SUB_TYCOD;
                            string tAgid = type.AGENCY;

                            if (Tycod == tTycod && Agid == tAgid && Subcod == tSubcod)
                            {
                                if (Efeanme != null)
                                {
                                    try
                                    {
                                        string[] callComms = Comments.Split(delimiters);
                                        string dispTime = Adts.Substring(8, 2) + ":" + Adts.Substring(10, 2);
                                        List<string> CivEmails = new List<string>();
                                        List<string> LeoEmails = new List<string>();

                                        var typeUsers = from u in TypeEntities.JC_HC_USERS
                                                        where (from g in TypeEntities.JC_HC_USR_SND
                                                               where (from a in TypeEntities.JC_HC_AGENCY
                                                                      where a.AG_ID == Agid || a.ID == 17
                                                                      select new { a.ID }).Equals(g.AGY_ID)
                                                               select new { g.USR_ID }).Equals(u.ID) && u.OOF == "F"
                                                        select new { u.EMAIL, u.LEO };

                                        foreach (var tUser in typeUsers)
                                        {
                                            string tEmail = tUser.EMAIL;
                                            string tLeo = tUser.LEO;

                                            if (tLeo == "F")
                                            {
                                                CivEmails.Add(tEmail);
                                            }
                                            else
                                            {
                                                LeoEmails.Add(tEmail);
                                            }

                                            if (CivEmails.Count > 0)
                                            {
                                                for (int i = 0; i < CivEmails.Count; i += batch)
                                                {
                                                    using (MailMessage tMsg = new MailMessage())
                                                    {
                                                        tMsg.From = new MailAddress("jcso.hotcalls@jocogov.org", "HOT CALL");
                                                        for (int j = i; (j < (i + batch)) && (j < CivEmails.Count); ++j)
                                                        {
                                                            tMsg.Bcc.Add(new MailAddress(CivEmails[j].Trim()));
                                                        }
                                                        tMsg.Subject = Agid.Trim() + " HOT CALL " + Tycod.Trim() + " " + Subcod.Trim();
                                                        tMsg.Body = "Location: " + Xstr1.Trim() + " / " + Xstr2.Trim() + "\n";
                                                        tMsg.Body += "Agency: " + Agid.Trim() + "\tTime Dispatched: " + dispTime.Trim() + "\tEvent No.: " + Num1.Trim() + "\n";
                                                        tMsg.Body += "Units Arrived: " + UnitsArv + "\tNature: " + Tycod.Trim() + " / " + Subcod.Trim();
                                                        SmtpClient tPost = new SmtpClient();
                                                        tPost.Send(tMsg);
                                                    }
                                                }
                                            }

                                            if (LeoEmails.Count > 0)
                                            {
                                                for (int i = 0; i < LeoEmails.Count; i += batch)
                                                {
                                                    using (MailMessage tMsg = new MailMessage())
                                                    {
                                                        tMsg.From = new MailAddress("jcso.hotcalls@jocogov.org", "HOT CALL");
                                                        for (int j = i; (j < (i + batch)) && (j < LeoEmails.Count); ++j)
                                                        {
                                                            tMsg.Bcc.Add(new MailAddress(LeoEmails[j].Trim()));
                                                        }
                                                        tMsg.Subject = Agid.Trim() + " HOT CALL " + Tycod.Trim() + " " + Subcod.Trim();
                                                        tMsg.Body = "Location: " + Estnum.Trim() + " " + Edirpre.Trim() + " " + Efeanme.Trim() + " " + Efeatyp.Trim() + "\n";
                                                        tMsg.Body += "Agency: " + Agid.Trim() + "\tTime Dispatched: " + dispTime.Trim() + "\tEvent. No.: " + Num1.Trim() + "\n";
                                                        tMsg.Body += "Units Arrived: " + UnitsArv + "\tNature: " + Tycod.Trim() + " / " + Subcod.Trim() + "\n\n";
                                                        foreach (string s in callComms)
                                                        {
                                                            tMsg.Body += s.Trim() + "\n";
                                                        }
                                                        SmtpClient tPost = new SmtpClient();
                                                        tPost.Send(tMsg);
                                                    }
                                                }
                                            }
                                        }
                                    }
                                    catch (OracleException ox)
                                    {
                                        MailMessage errorMsg = new MailMessage();
                                        errorMsg.From = new MailAddress("jcso.exception@jocogov.org", "Hot Call Oracle Exception");
                                        errorMsg.To.Add(new MailAddress("tony.dunsworth@jocogov.org", "Tony Dunsworth"));
                                        errorMsg.To.Add(new MailAddress("carter.wetherington@jocogov.org", "Carter Wetherington"));
                                        errorMsg.Subject = "Oracle Logic Evaluator Error";
                                        errorMsg.Body = "Oracle is reporting the following error: " + ox.ToString() + " at " + DateTime.Now.ToString("G");
                                        SmtpClient errorPost = new SmtpClient();
                                        errorPost.Send(errorMsg);

                                        FileStream evalErrorLog = File.Open(@"C:\Temp\devLogicError.txt", FileMode.Append, FileAccess.Write);
                                        StreamWriter evalErrorWrite = new StreamWriter(evalErrorLog);
                                        evalErrorWrite.WriteLine("Oracle is reporting the following error: " + ox.ToString() + " at " + DateTime.Now.ToString("G"));
                                        evalErrorWrite.Close();
                                        evalErrorLog.Close();
                                    }
                                    catch (Exception ex)
                                    {
                                        MailMessage errorMsg = new MailMessage();
                                        errorMsg.From = new MailAddress("jcso.exception@jocogov.org", "Hot Call Oracle Exception");
                                        errorMsg.To.Add(new MailAddress("tony.dunsworth@jocogov.org", "Tony Dunsworth"));
                                        errorMsg.To.Add(new MailAddress("carter.wetherington@jocogov.org", "Carter Wetherington"));
                                        errorMsg.Subject = "Hot Calls Logic Evaluator Error";
                                        errorMsg.Body = "The Hot Calls Logic program is reporting the following error: " + ex.ToString() + " at " + DateTime.Now.ToString("G");
                                        SmtpClient errorPost = new SmtpClient();
                                        errorPost.Send(errorMsg);

                                        FileStream evalErrorLog = File.Open(@"C:\Temp\devLogicError.txt", FileMode.Append, FileAccess.Write);
                                        StreamWriter evalErrorWrite = new StreamWriter(evalErrorLog);
                                        evalErrorWrite.WriteLine("The Hot Calls Logic program is reporting the following error: " + ex.ToString() + " at " + DateTime.Now.ToString("G"));
                                        evalErrorWrite.Close();
                                        evalErrorLog.Close();
                                    }
                                    finally
                                    {
                                        JC_HC_CURENT tUpdate = (from u in TypeEntities.JC_HC_CURENT
                                                                where u.EID == Eid && u.NUM_1 == Num1 && u.AD_TS == Adts
                                                                select u).FirstOrDefault();

                                        if (tUpdate != null)
                                        {
                                            tUpdate.TYPE_EVAL = "T";
                                            tUpdate.HC_SENT = "T";
                                        }

                                        JC_HC_SENT tSent = new JC_HC_SENT();
                                        tSent.EID = Eid;
                                        tSent.AG_ID = Agid;
                                        tSent.TYCOD = Tycod;
                                        tSent.SUB_TYCOD = Subcod;
                                        tSent.SENT_DT = DateTime.Now.ToString("yyyyMMddHHmmss");
                                        tSent.EMAIL_SENT = Comments;
                                        tSent.NUM_1 = Num1;

                                        TypeEntities.JC_HC_SENT.Add(tSent);
                                        TypeEntities.SaveChanges();
                                    }
                                }
                                else
                                {
                                    try
                                    {
                                        string[] callComms = Comments.Split(delimiters);
                                        string dispTime = Adts.Substring(8, 2) + ":" + Adts.Substring(10, 2);
                                        List<string> CivEmails = new List<string>();
                                        List<string> LeoEmails = new List<string>();

                                        var typeAgencies = from a in TypeEntities.JC_HC_AGENCY
                                                           where a.AG_ID == Agid || a.ID == 17
                                                           select new { a.ID };
                                        foreach (var agency in typeAgencies)
                                        {
                                            int? AgyId = agency.ID;

                                            var typeUsers = from u in TypeEntities.JC_HC_USERS
                                                            where (from g in TypeEntities.JC_HC_USR_SND
                                                                   where g.AGY_ID == AgyId
                                                                   select new { g.USR_ID }).Equals(u.ID) && u.OOF == "F"
                                                            select new { u.EMAIL, u.LEO };

                                            foreach (var tUser in typeUsers)
                                            {
                                                string tEmail = tUser.EMAIL;
                                                string tLeo = tUser.LEO;

                                                if (tLeo == "F")
                                                {
                                                    CivEmails.Add(tEmail);
                                                }
                                                else
                                                {
                                                    LeoEmails.Add(tEmail);
                                                }

                                                if (CivEmails.Count > 0)
                                                {
                                                    for (int i = 0; i < CivEmails.Count; i += batch)
                                                    {
                                                        using (MailMessage tMsg = new MailMessage())
                                                        {
                                                            tMsg.From = new MailAddress("jcso.hotcalls@jocogov.org", "HOT CALL");
                                                            for (int j = i; (j < (i + batch)) && (j < CivEmails.Count); ++j)
                                                            {
                                                                tMsg.Bcc.Add(new MailAddress(CivEmails[j].Trim()));
                                                            }
                                                            tMsg.Subject = Agid.Trim() + " HOT CALL " + Tycod.Trim() + " " + Subcod.Trim();
                                                            tMsg.Body = "Location: " + Xstr1.Trim() + " / " + Xstr2.Trim() + "\n";
                                                            tMsg.Body += "Agency: " + Agid.Trim() + "\tTime Dispatched: " + dispTime.Trim() + "\tEvent No.: " + Num1.Trim() + "\n";
                                                            tMsg.Body += "Units Arrived: " + UnitsArv + "\tNature: " + Tycod.Trim() + " / " + Subcod.Trim();
                                                            SmtpClient tPost = new SmtpClient();
                                                            tPost.Send(tMsg);
                                                        }
                                                    }
                                                }

                                                if (LeoEmails.Count > 0)
                                                {
                                                    for (int i = 0; i < LeoEmails.Count; i += batch)
                                                    {
                                                        using (MailMessage tMsg = new MailMessage())
                                                        {
                                                            tMsg.From = new MailAddress("jcso.hotcalls@jocogov.org", "HOT CALL");
                                                            for (int j = i; (j < (i + batch)) && (j < LeoEmails.Count); ++j)
                                                            {
                                                                tMsg.Bcc.Add(new MailAddress(LeoEmails[j].Trim()));
                                                            }
                                                            tMsg.Subject = Agid.Trim() + " HOT CALL " + Tycod.Trim() + " " + Subcod.Trim();
                                                            tMsg.Body = "Location: " + Xstr1.Trim() + " " + Xstr2.Trim() + "\n";
                                                            tMsg.Body += "Agency: " + Agid.Trim() + "\tTime Dispatched: " + dispTime.Trim() + "\tEvent. No.: " + Num1.Trim() + "\n";
                                                            tMsg.Body += "Units Arrived: " + UnitsArv + "\tNature: " + Tycod.Trim() + " / " + Subcod.Trim() + "\n\n";
                                                            foreach (string s in callComms)
                                                            {
                                                                tMsg.Body += s.Trim() + "\n";
                                                            }
                                                            SmtpClient tPost = new SmtpClient();
                                                            tPost.Send(tMsg);
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                    catch (OracleException ox)
                                    {
                                        MailMessage errorMsg = new MailMessage();
                                        errorMsg.From = new MailAddress("jcso.exception@jocogov.org", "Hot Call Oracle Exception");
                                        errorMsg.To.Add(new MailAddress("tony.dunsworth@jocogov.org", "Tony Dunsworth"));
                                        errorMsg.To.Add(new MailAddress("carter.wetherington@jocogov.org", "Carter Wetherington"));
                                        errorMsg.Subject = "Oracle Logic Evaluator Error";
                                        errorMsg.Body = "Oracle is reporting the following error: " + ox.ToString() + " at " + DateTime.Now.ToString("G");
                                        SmtpClient errorPost = new SmtpClient();
                                        errorPost.Send(errorMsg);

                                        FileStream evalErrorLog = File.Open(@"C:\Temp\devLogicError.txt", FileMode.Append, FileAccess.Write);
                                        StreamWriter evalErrorWrite = new StreamWriter(evalErrorLog);
                                        evalErrorWrite.WriteLine("Oracle is reporting the following error: " + ox.ToString() + " at " + DateTime.Now.ToString("G"));
                                        evalErrorWrite.Close();
                                        evalErrorLog.Close();
                                    }
                                    catch (Exception ex)
                                    {
                                        MailMessage errorMsg = new MailMessage();
                                        errorMsg.From = new MailAddress("jcso.exception@jocogov.org", "Hot Call Oracle Exception");
                                        errorMsg.To.Add(new MailAddress("tony.dunsworth@jocogov.org", "Tony Dunsworth"));
                                        errorMsg.To.Add(new MailAddress("carter.wetherington@jocogov.org", "Carter Wetherington"));
                                        errorMsg.Subject = "Hot Calls Logic Evaluator Error";
                                        errorMsg.Body = "The Hot Calls Logic program is reporting the following error: " + ex.ToString() + " at " + DateTime.Now.ToString("G");
                                        SmtpClient errorPost = new SmtpClient();
                                        errorPost.Send(errorMsg);

                                        FileStream evalErrorLog = File.Open(@"C:\Temp\devLogicError.txt", FileMode.Append, FileAccess.Write);
                                        StreamWriter evalErrorWrite = new StreamWriter(evalErrorLog);
                                        evalErrorWrite.WriteLine("The Hot Calls Logic program is reporting the following error: " + ex.ToString() + " at " + DateTime.Now.ToString("G"));
                                        evalErrorWrite.Close();
                                        evalErrorLog.Close();
                                    }
                                    finally
                                    {
                                        JC_HC_CURENT tUpdate = (from u in TypeEntities.JC_HC_CURENT
                                                                where u.EID == Eid && u.NUM_1 == Num1 && u.AD_TS == Adts
                                                                select u).FirstOrDefault();

                                        if (tUpdate != null)
                                        {
                                            tUpdate.TYPE_EVAL = "T";
                                            tUpdate.HC_SENT = "T";
                                        }

                                        JC_HC_SENT tSent = new JC_HC_SENT();
                                        tSent.EID = Eid;
                                        tSent.AG_ID = Agid;
                                        tSent.TYCOD = Tycod;
                                        tSent.SUB_TYCOD = Subcod;
                                        tSent.SENT_DT = DateTime.Now.ToString("yyyyMMddHHmmss");
                                        tSent.EMAIL_SENT = Comments;
                                        tSent.NUM_1 = Num1;

                                        TypeEntities.JC_HC_SENT.Add(tSent);
                                        TypeEntities.SaveChanges();
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        var tDefault = (from d in TypeEntities.JC_HC_CURENT
                                        where d.TYPE_EVAL == "F"
                                        select d).FirstOrDefault();
                        tDefault.LOI_EVAL = "T";
                        TypeEntities.SaveChanges();
                    }
                }
            }
            // Console.WriteLine("TypeEval exited at " + DateTime.Now.ToString("G"));
            return;
        }

        private static void CountEval()
        {
            // This function looks at the number of units arrived to the call within the first 25 minutes and if that threshhold has been met, the call is sent as a hot call.
            // Invoke a Console.WriteLine() statement for debugging purposes. It is commented out of production code.
            // Console.WriteLine("CountEval entered at " + DateTime.Now.ToString("G"));
            // DateTime cTime = DateTime.Now.AddMinutes(-60);
            LogicEntities UnitEntities = new LogicEntities();
            var unitCalls = from c in UnitEntities.JC_HC_CURENT
                            where c.UNIT_COUNT > 0 && c.HC_SENT == "F"
                            select new
                            {
                                c.EID,
                                c.NUM_1,
                                c.TYCOD,
                                c.SUB_TYCOD,
                                c.ESTNUM,
                                c.EDIRPRE,
                                c.EFEANME,
                                c.EFEATYP,
                                c.AG_ID,
                                c.AD_TS,
                                c.XSTREET1,
                                c.XSTREET2,
                                c.ESZ,
                                c.COMMENTS,
                                c.UNIT_COUNT,
                                c.UNIT_EVAL,
                                c.HC_SENT
                            };

            foreach (var call in unitCalls)
            {
                int Eid = call.EID;
                string Num1 = call.NUM_1;
                string Tycod = call.TYCOD;
                string Subcod = call.SUB_TYCOD;
                string Estnum = call.ESTNUM;
                string Edirpre = call.EDIRPRE;
                string Efeanme = call.EFEANME;
                string Efeatyp = call.EFEATYP;
                string Agid = call.AG_ID;
                string Adts = call.AD_TS;
                string Xstr1 = call.XSTREET1;
                string Xstr2 = call.XSTREET2;
                int? Esz = call.ESZ;
                string Comments = call.COMMENTS;
                int? UnitsArv = call.UNIT_COUNT;
                string Ueval = call.UNIT_EVAL;
                string Usent = call.HC_SENT;

                if (Ueval == "F")
                {
                    int cEvalTime = string.CompareOrdinal(Adts.Substring(8, 2), compTime.ToString("yyyyMMddHHmmss"));
                    if (cEvalTime >= 0)
                    {
                        var unitLists = from u in UnitEntities.JC_HC_AGENCY
                                        select new
                                        {
                                            u.ID,
                                            u.AG_ID,
                                            u.UNITS
                                        };

                        foreach (var unitList in unitLists)
                        {
                            int uId = unitList.ID;
                            string uAgid = unitList.AG_ID;
                            int uArv = unitList.UNITS;

                            if (Agid == uAgid && UnitsArv >= uArv)
                            {
                                if (Efeanme != null)
                                {
                                    try
                                    {
                                        string[] callComms = Comments.Split(delimiters);
                                        string dispTime = Adts.Substring(8, 2) + ":" + Adts.Substring(10, 2);
                                        List<string> CivEmails = new List<string>();
                                        List<string> LeoEmails = new List<string>();

                                        var unitUsers = from u in UnitEntities.JC_HC_USERS
                                                        where (from g in UnitEntities.JC_HC_USR_SND
                                                               where g.AGY_ID == uId || g.AGY_ID == 17
                                                               select new { g.USR_ID }).Equals(u.ID) && u.OOF == "F"
                                                        select new { u.EMAIL, u.LEO };

                                        foreach (var user in unitUsers)
                                        {
                                            string uEmail = user.EMAIL;
                                            string uLeo = user.LEO;

                                            if (uLeo == "F")
                                            {
                                                CivEmails.Add(uEmail);
                                            }
                                            else
                                            {
                                                LeoEmails.Add(uEmail);
                                            }

                                            if (CivEmails.Count > 0)
                                            {
                                                for (int i = 0; i < CivEmails.Count; i += batch)
                                                {
                                                    using (MailMessage cMsg = new MailMessage())
                                                    {
                                                        cMsg.From = new MailAddress("jcso.hotcalls@jocogov.org", "HOT CALL");
                                                        for (int j = i; (j < (i + batch)) && (j < CivEmails.Count); ++j)
                                                        {
                                                            cMsg.Bcc.Add(new MailAddress(CivEmails[j].Trim()));
                                                        }
                                                        cMsg.Subject = Agid.Trim() + " HOT CALL " + Tycod.Trim() + " " + Subcod.Trim();
                                                        cMsg.Body = "Location: " + Xstr1.Trim() + " / " + Xstr2.Trim() + "\n";
                                                        cMsg.Body += "Agency: " + Agid.Trim() + "\tTime Dispatched: " + dispTime.Trim() + "\tEvent No.: " + Num1.Trim() + "\n";
                                                        cMsg.Body += "Units Arrived: " + UnitsArv + "\tNature: " + Tycod.Trim() + " / " + Subcod.Trim();
                                                        SmtpClient cPost = new SmtpClient();
                                                        cPost.Send(cMsg);
                                                    }
                                                }
                                            }
                                            if (LeoEmails.Count > 0)
                                            {
                                                for (int i = 0; i < LeoEmails.Count; i += batch)
                                                {
                                                    using (MailMessage cMsg = new MailMessage())
                                                    {
                                                        cMsg.From = new MailAddress("jcso.hotcalls@jocogov.org", "HOT CALL");
                                                        for (int j = i; (j < (i + batch)) && (j < LeoEmails.Count); ++j)
                                                        {
                                                            cMsg.Bcc.Add(new MailAddress(LeoEmails[j].Trim()));
                                                        }
                                                        cMsg.Subject = Agid.Trim() + " HOT CALL " + Tycod.Trim() + " " + Subcod.Trim();
                                                        cMsg.Body = "Location: " + Estnum.Trim() + " " + Edirpre.Trim() + " " + Efeanme.Trim() + " " + Efeatyp.Trim() + "\n";
                                                        cMsg.Body += "Agency: " + Agid.Trim() + "\tTime Dispatched: " + dispTime.Trim() + "\tEvent No.: " + Num1.Trim() + "\n";
                                                        cMsg.Body += "Units Arrived: " + UnitsArv + "\tNature: " + Tycod.Trim() + " / " + Subcod.Trim() + "\n\n";
                                                        foreach (string s in callComms)
                                                        {
                                                            cMsg.Body += s.Trim() + "\n";
                                                        }
                                                        SmtpClient cPost = new SmtpClient();
                                                        cPost.Send(cMsg);
                                                    }
                                                }
                                            }
                                        }
                                    }
                                    catch (OracleException ox)
                                    {
                                        MailMessage errorMsg = new MailMessage();
                                        errorMsg.From = new MailAddress("jcso.exception@jocogov.org", "Hot Call Oracle Exception");
                                        errorMsg.To.Add(new MailAddress("tony.dunsworth@jocogov.org", "Tony Dunsworth"));
                                        errorMsg.To.Add(new MailAddress("carter.wetherington@jocogov.org", "Carter Wetherington"));
                                        errorMsg.Subject = "Oracle Logic Evaluator Error";
                                        errorMsg.Body = "Oracle is reporting the following error: " + ox.ToString() + " at " + DateTime.Now.ToString("G");
                                        SmtpClient errorPost = new SmtpClient();
                                        errorPost.Send(errorMsg);

                                        FileStream evalErrorLog = File.Open(@"C:\Temp\devLogicError.txt", FileMode.Append, FileAccess.Write);
                                        StreamWriter evalErrorWrite = new StreamWriter(evalErrorLog);
                                        evalErrorWrite.WriteLine("Oracle is reporting the following error: " + ox.ToString() + " at " + DateTime.Now.ToString("G"));
                                        evalErrorWrite.Close();
                                        evalErrorLog.Close();
                                    }
                                    catch (Exception ex)
                                    {
                                        MailMessage errorMsg = new MailMessage();
                                        errorMsg.From = new MailAddress("jcso.exception@jocogov.org", "Hot Call Oracle Exception");
                                        errorMsg.To.Add(new MailAddress("tony.dunsworth@jocogov.org", "Tony Dunsworth"));
                                        errorMsg.To.Add(new MailAddress("carter.wetherington@jocogov.org", "Carter Wetherington"));
                                        errorMsg.Subject = "Hot Calls Logic Evaluator Error";
                                        errorMsg.Body = "The Hot Calls Logic program is reporting the following error: " + ex.ToString() + " at " + DateTime.Now.ToString("G");
                                        SmtpClient errorPost = new SmtpClient();
                                        errorPost.Send(errorMsg);

                                        FileStream evalErrorLog = File.Open(@"C:\Temp\devLogicError.txt", FileMode.Append, FileAccess.Write);
                                        StreamWriter evalErrorWrite = new StreamWriter(evalErrorLog);
                                        evalErrorWrite.WriteLine("The Hot Calls Logic program is reporting the following error: " + ex.ToString() + " at " + DateTime.Now.ToString("G"));
                                        evalErrorWrite.Close();
                                        evalErrorLog.Close();
                                    }
                                    finally
                                    {
                                        var cUpdate = (from u in UnitEntities.JC_HC_CURENT
                                                       where u.EID == Eid && u.NUM_1 == Num1 && u.AD_TS == Adts
                                                       select u).FirstOrDefault();

                                        if (cUpdate != null)
                                        {
                                            cUpdate.UNIT_EVAL = "T";
                                            cUpdate.HC_SENT = "T";
                                        }

                                        JC_HC_SENT cSent = new JC_HC_SENT();
                                        cSent.EID = Eid;
                                        cSent.AG_ID = Agid;
                                        cSent.TYCOD = Tycod;
                                        cSent.SUB_TYCOD = Subcod;
                                        cSent.SENT_DT = DateTime.Now.ToString("yyyyMMddHHmmss");
                                        cSent.EMAIL_SENT = Comments;
                                        cSent.NUM_1 = Num1;

                                        UnitEntities.JC_HC_SENT.Add(cSent);
                                        UnitEntities.SaveChanges();
                                    }
                                }
                                else
                                {
                                    try
                                    {
                                        string[] callComms = Comments.Split(delimiters);
                                        string dispTime = Adts.Substring(8, 2) + ":" + Adts.Substring(10, 2);
                                        List<string> CivEmails = new List<string>();
                                        List<string> LeoEmails = new List<string>();

                                        var unitUsers = from u in UnitEntities.JC_HC_USERS
                                                        where (from g in UnitEntities.JC_HC_USR_SND
                                                               where g.AGY_ID == uId || g.AGY_ID == 17
                                                               select new { g.USR_ID }).Equals(u.ID) && u.OOF == "F"
                                                        select new { u.EMAIL, u.LEO };

                                        foreach (var user in unitUsers)
                                        {
                                            string uEmail = user.EMAIL;
                                            string uLeo = user.LEO;

                                            if (uLeo == "F")
                                            {
                                                CivEmails.Add(uEmail);
                                            }
                                            else
                                            {
                                                LeoEmails.Add(uEmail);
                                            }

                                            if (CivEmails.Count > 0)
                                            {
                                                for (int i = 0; i < CivEmails.Count; i += batch)
                                                {
                                                    using (MailMessage cMsg = new MailMessage())
                                                    {
                                                        cMsg.From = new MailAddress("jcso.hotcalls@jocogov.org", "HOT CALL");
                                                        for (int j = i; (j < (i + batch)) && (j < CivEmails.Count); ++j)
                                                        {
                                                            cMsg.Bcc.Add(new MailAddress(CivEmails[j].Trim()));
                                                        }
                                                        cMsg.Subject = Agid.Trim() + " HOT CALL " + Tycod.Trim() + " " + Subcod.Trim();
                                                        cMsg.Body = "Location: " + Xstr1.Trim() + " / " + Xstr2.Trim() + "\n";
                                                        cMsg.Body += "Agency: " + Agid.Trim() + "\tTime Dispatched: " + dispTime.Trim() + "\tEvent No.: " + Num1.Trim() + "\n";
                                                        cMsg.Body += "Units Arrived: " + UnitsArv + "\tNature: " + Tycod.Trim() + " / " + Subcod.Trim();
                                                        SmtpClient cPost = new SmtpClient();
                                                        cPost.Send(cMsg);
                                                    }
                                                }
                                            }
                                            if (LeoEmails.Count > 0)
                                            {
                                                for (int i = 0; i < LeoEmails.Count; i += batch)
                                                {
                                                    using (MailMessage cMsg = new MailMessage())
                                                    {
                                                        cMsg.From = new MailAddress("jcso.hotcalls@jocogov.org", "HOT CALL");
                                                        for (int j = i; (j < (i + batch)) && (j < LeoEmails.Count); ++j)
                                                        {
                                                            cMsg.Bcc.Add(new MailAddress(LeoEmails[j].Trim()));
                                                        }
                                                        cMsg.Subject = Agid.Trim() + " HOT CALL " + Tycod.Trim() + " " + Subcod.Trim();
                                                        cMsg.Body = "Location: " + Xstr1.Trim() + " / " + Xstr2.Trim() + "\n";
                                                        cMsg.Body += "Agency: " + Agid.Trim() + "\tTime Dispatched: " + dispTime.Trim() + "\tEvent No.: " + Num1.Trim() + "\n";
                                                        cMsg.Body += "Units Arrived: " + UnitsArv + "\tNature: " + Tycod.Trim() + " / " + Subcod.Trim() + "\n\n";
                                                        foreach (string s in callComms)
                                                        {
                                                            cMsg.Body += s.Trim() + "\n";
                                                        }
                                                        SmtpClient cPost = new SmtpClient();
                                                        cPost.Send(cMsg);
                                                    }
                                                }
                                            }
                                        }
                                    }
                                    catch (OracleException ox)
                                    {
                                        MailMessage errorMsg = new MailMessage();
                                        errorMsg.From = new MailAddress("jcso.exception@jocogov.org", "Hot Call Oracle Exception");
                                        errorMsg.To.Add(new MailAddress("tony.dunsworth@jocogov.org", "Tony Dunsworth"));
                                        errorMsg.To.Add(new MailAddress("carter.wetherington@jocogov.org", "Carter Wetherington"));
                                        errorMsg.Subject = "Oracle Logic Evaluator Error";
                                        errorMsg.Body = "Oracle is reporting the following error: " + ox.ToString() + " at " + DateTime.Now.ToString("G");
                                        SmtpClient errorPost = new SmtpClient();
                                        errorPost.Send(errorMsg);

                                        FileStream evalErrorLog = File.Open(@"C:\Temp\devLogicError.txt", FileMode.Append, FileAccess.Write);
                                        StreamWriter evalErrorWrite = new StreamWriter(evalErrorLog);
                                        evalErrorWrite.WriteLine("Oracle is reporting the following error: " + ox.ToString() + " at " + DateTime.Now.ToString("G"));
                                        evalErrorWrite.Close();
                                        evalErrorLog.Close();
                                    }
                                    catch (Exception ex)
                                    {
                                        MailMessage errorMsg = new MailMessage();
                                        errorMsg.From = new MailAddress("jcso.exception@jocogov.org", "Hot Call Oracle Exception");
                                        errorMsg.To.Add(new MailAddress("tony.dunsworth@jocogov.org", "Tony Dunsworth"));
                                        errorMsg.To.Add(new MailAddress("carter.wetherington@jocogov.org", "Carter Wetherington"));
                                        errorMsg.Subject = "Hot Calls Logic Evaluator Error";
                                        errorMsg.Body = "The Hot Calls Logic program is reporting the following error: " + ex.ToString() + " at " + DateTime.Now.ToString("G");
                                        SmtpClient errorPost = new SmtpClient();
                                        errorPost.Send(errorMsg);

                                        FileStream evalErrorLog = File.Open(@"C:\Temp\devLogicError.txt", FileMode.Append, FileAccess.Write);
                                        StreamWriter evalErrorWrite = new StreamWriter(evalErrorLog);
                                        evalErrorWrite.WriteLine("The Hot Calls Logic program is reporting the following error: " + ex.ToString() + " at " + DateTime.Now.ToString("G"));
                                        evalErrorWrite.Close();
                                        evalErrorLog.Close();
                                    }
                                    finally
                                    {
                                        var cUpdate = (from u in UnitEntities.JC_HC_CURENT
                                                       where u.EID == Eid && u.NUM_1 == Num1 && u.AD_TS == Adts
                                                       select u).FirstOrDefault();

                                        if (cUpdate != null)
                                        {
                                            cUpdate.UNIT_EVAL = "T";
                                            cUpdate.HC_SENT = "T";
                                        }

                                        JC_HC_SENT cSent = new JC_HC_SENT();
                                        cSent.EID = Eid;
                                        cSent.AG_ID = Agid;
                                        cSent.TYCOD = Tycod;
                                        cSent.SUB_TYCOD = Subcod;
                                        cSent.SENT_DT = DateTime.Now.ToString("yyyyMMddHHmmss");
                                        cSent.EMAIL_SENT = Comments;
                                        cSent.NUM_1 = Num1;

                                        UnitEntities.JC_HC_SENT.Add(cSent);
                                        UnitEntities.SaveChanges();
                                    }
                                }
                            }
                        }
                    }
                    /*else
                    {
                        var cDefault = (from d in UnitEntities.JC_HC_CURENT
                                        where d.UNIT_EVAL == "F"
                                        select d).First();
                        cDefault.UNIT_EVAL = "T";
                        UnitEntities.SaveChanges();
                    }*/
                }
            }
            // Console.WriteLine("CountEval exited at " + DateTime.Now.ToString("G"));
            return;
        }
    }
    // Change 20130208-1: Added additional columns to the database for up to 6 different agencies to be monitored. Putting in dev code to test for the weekend.
    // Change 20130304-1: Added the loi_grp_id field to the LOI selection information and changed the SQL query for email addresses to better isolate LOI CALLS going out to specific people.
    // Change 20130326-1: Added the Trim() method to the strings in the mailing methods to ensure that all unneeded white space is removed.
    // Change 20130327-1: Moved the SQL query to set the hc_sent flag to 'T' before sending the email.
    // Change 20130402-1: Moved the SQL query to write the sent mail to the jc_hc_sent_dev table before sending the email so OPD hot calls are not lost from the database.
    // Change 20130423-1: Added a if/else statement to the LOI email to account for LOI entries with no recipients.
    // Change 20130424-1: Modified the SQL query to adapt to a new field in the jc_hc_loi database to stop no recipient errors.
    // Change 20130628-1: Rewrote the email generation system to only generate 1 email for every 150 users. This should allow the program to run without additional errors for larger agencies as we will not breach the "too many recipients" threshold.
    // Change 20131029-1: Changed the table usage in the email address collection query to use one standard table jc_hc_usr_snd and ensure that works as designed. The query has been tested independently and brings back the expected numbers.
    // Change 20140226-1: Changed the DateTime formate in the Console.WriteLine and error reporting functions to "G" which is MS Long format. eg. 02/26/2014 4:02:03 PM
    // Change 20140226-2: Added the CivEmails List<T>() generic to aggregate non-LEO email addresses for separate treatment.
    // Change 20140226-3: Changed the email subroutines to enable Civilian and LEO emails to contain different information.
    // Change 20140311-1: Placed a try/catch block in the Main(string[] args) function to trap any errors and abort the process if there are problems.
    // Change 20140313-1: Removed legacy code for the original Main(string[] args) function as the current configuration has proven sufficiently reliable to not merit revisiting the old code.
    // Change 20140313-2: Moved the time comparison variable out of the individual methods and replaced it with a public DateTime variable.
    // Change 20140409-1: Switched code base to ODP.NET and Entity Framework v. 5.0.0
    // Change 20140409-2: Moved all queries to LINQ-To-Entities coding structure for simplicity
    // Change 20140409-3: Changed order of mailing structure to mail first and update tables second as originally designed using try/catch/finally block
    // Change 20140409-4: Added catch code to change evaluated calls from F to T if they do not rise to hot call status.
    // Change 20140409-5: Added where clauses to the LOI and UnitCount queries to use greater efficiency by not having to step through as many rows.
    // Change 20140409-6: First re-write with queries more optimised and removing additional non-used calls.
    // Change 20140411-1: Restructured the build for x64 architecture and x64 version of ODP.NET
    // Change 20140411-2: Reoptimised L2E queries and eliminated unnecessary object creation.
    // Change 20140411-3: Moved the batch size integer and the delimiter char[] out of the methods and made global variables.
    // Change 20140414-1: Re-wrote a TypeEval() LINQ query to bring it into one query not two.
    // Change 20140414-2: Changed the connection string in the app.config file to reflect the full TNSnames.ora entry to prevent ORA-12504 errors.
    // Change 20140418-1: Removed the last clause from the CountEval() method to ensure that calls aren't tossed until the 25 minute mark has passed and the call is closed out.
    // Change 20140418-2: Changed the SENT_DT string to DateTime.Now.ToString("yyyyMMddHHmmss") to better reflect when the call was actually sent out.
}
