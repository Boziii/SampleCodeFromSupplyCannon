using Microsoft.Extensions.Configuration;
using SendGrid;
using SendGrid.Helpers.Mail;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using System.Web.Hosting;

namespace RAWsupply.ProvisionLocal.Domain.Helpers
{
    public static class PackSizeHelper
    {
        public static List<string> ParsePackSize(string packSize, string UOM, long SupplierID)
        {
            List<string> Parsed = new List<string>();

            string NumSepPattern = @"([0-9#\.]+)|([ Xx&\/@\-]+)";
            MatchCollection NumbersAndSeparators = Regex.Matches(packSize, NumSepPattern);
            List<string> nas = TrimAndStringify(NumbersAndSeparators);

            List<List<string>> SepAndHalfs = GetPotentialSplits(nas);
            int MaxPriorityIndex = FindMaxPrioritySplit(SepAndHalfs, SupplierID, UOM.ToLower());

            Parsed = SplitPackSize(packSize, UOM, MaxPriorityIndex, SepAndHalfs);

            return Parsed;
        }

        private static List<string> TrimAndStringify(MatchCollection NumbersAndSeparators)
        {
            List<string> nas = new List<string>();
            string ns = "";
            foreach (Match NumSep in NumbersAndSeparators)
            {
                ns = NumSep.ToString().Trim().ToLower();
                if (ns == "")
                    ns = " ";
                nas.Add(ns);
            }

            return nas;
        }

        private static List<List<string>> GetPotentialSplits(List<string> nas)
        {
            string firstHalf, secondHalf;
            List<List<string>> SepAndHalfs = new List<List<string>>();
            //List<string> tempList = new List<string>();
            for (int j = 0; j < nas.Count; j++)
            {
                firstHalf = "";
                secondHalf = "";
                if (IsSeparator(nas[j]))
                {
                    for (int i = 0; i < nas.Count; i++)
                    {
                        if (i < j)
                            firstHalf += nas[i];
                        else if (i > j)
                            secondHalf += nas[i];
                    }
                    List<string> tempList = new List<string>() { nas[j], firstHalf, secondHalf };
                    SepAndHalfs.Add(tempList);
                }
            }
            return SepAndHalfs;
        }

        private static bool IsSeparator(string foo)
        {
            string SepPattern = @"([ Xx&\/@\-]+)";
            Match IsSep = Regex.Match(foo, SepPattern);
            return IsSep.Success;
        }

        private static int FindMaxPrioritySplit(List<List<string>> SepAndHalfs, long supplierID, string UOM)
        {
            int priority = 0;
            int Max = 1;
            int MaxPriorityIndex = -1;

            for (int j = 0; j < SepAndHalfs.Count; j++)
            {
                switch(supplierID)
                {
                    case Enums.Supplier.Keany:
                        priority = KeanySeparatorPriority(SepAndHalfs[j][0]);
                        if (priority == 1 && UOM == "pt (u.s.)")
                            priority = -1;
                        break;
                    case Enums.Supplier.PFG:
                        priority = PFGandNewSyscoSeparatorPriority(SepAndHalfs[j][0]);
                        break;
                    case Enums.Supplier.NewSysco:
                        priority = PFGandNewSyscoSeparatorPriority(SepAndHalfs[j][0]);
                        break;
                    default:
                        priority = SeparatorPriority(SepAndHalfs[j][0]);
                        break;
                }

                if(priority == 1 && SepAndHalfs.Count == 1)
                {
                    double num1, num2;
                    num1 = double.Parse(SepAndHalfs[j][1]);

                    string tempString = SepAndHalfs[j][2];
                    tempString = tempString.Replace('#', ' ').Trim();
                    num2 = double.Parse(tempString);
                    if((num1 != num2) && (num1 >= 30) && (num2 >= 30) && (Math.Abs(num1-num2)<=20))
                    {
                        priority = 0;
                    }
                }

                priority = isSingleGrouping(SepAndHalfs, priority, supplierID, UOM);

                if (priority >= Max)
                {
                    Max = priority;
                    MaxPriorityIndex = j;
                }
            }

            return MaxPriorityIndex;
        }

        private static int isSingleGrouping(List<List<string>> SepAndHalfs, int priority, long supplierID, string UnitOfMeasure)
        {
            string UOM = UnitOfMeasure.ToLower();
            if (SepAndHalfs.Count == 1 && (SepAndHalfs[0][0] == "-" || SepAndHalfs[0][0] == "/"))
            {
                if (UOM == "ct" || UOM == "cs")
                {
                    double num1 = double.Parse(SepAndHalfs[0][1]);
                    double num2 = double.Parse(SepAndHalfs[0][2]);
                    if (num1 < num2)
                    {
                        if(supplierID == Enums.Supplier.CoastalSunbelt && priority == 1)
                        {
                            if ((num1 >= 30) && (num2 >= 30) && (Math.Abs(num1 - num2) <= 20))
                            {
                                priority = -1;
                            }
                        }
                        else
                        {
                            priority = -1;
                        }
                    }
                }
            }
            return priority;
        }

        private static int SeparatorPriority(string sep)
        {
            if (sep == "@")
                return 3;
            else if (sep == " ")
                return 2;
            else if (sep == "/")
                return 1;
            else
                return 0;
        }

        private static int KeanySeparatorPriority(string sep)
        {
            if (sep == " ")
                return 3;
            else if (sep == "-")
                return 2;
            else if (sep == "/")
                return 1;
            else
                return 0;
        }

        private static int PFGandNewSyscoSeparatorPriority(string sep)
        {
            if (sep == "@")
                return 2;
            else
                return 0;
        }

        

        private static List<string> SplitPackSize(string packSize, string UOM, int MaxPriorityIndex, List<List<string>> SepAndHalfs)
        {
            List<string> Parsed = new List<string>();

            if (MaxPriorityIndex >= 0)       //indicates a priority greater than 0
            {
                string half1 = SepAndHalfs[MaxPriorityIndex][1] != "" ? SepAndHalfs[MaxPriorityIndex][1] : "1";
                string half2 = SepAndHalfs[MaxPriorityIndex][2] != "" ? SepAndHalfs[MaxPriorityIndex][2] : "1";
                Parsed.Add(half1);
                Parsed.Add(half2);
            }
            else
            {
                Parsed.Add("1");
                Parsed.Add(packSize);
            }

            return Parsed;
        }

        public static double ProcessGrouping(string grouping, bool isPack, string UOM)
        {
            double ProcessedNumber = 1;
            string NumSepPattern = @"([0-9\.]+)|([ Xx&\/\-]+)";

            //in case of size/UOM Combos
            bool tenCan = grouping.Contains("#10");
            bool pound2oz = UOM.Contains("lb2oz");
            bool isDozen = UOM == "dozen";
            if ((tenCan || pound2oz || isDozen) && isPack==false)
            {
                if(pound2oz)
                {
                    var quickConvert = double.Parse(grouping) * 16 + 2;
                    grouping = quickConvert + "";
                }
                if(isDozen)
                {
                    var quickConvert = double.Parse(grouping) * 12;
                    grouping = quickConvert + "";
                }
                grouping = Regex.Replace(grouping, "#10", "1");
            }

            MatchCollection NumbersAndSeparators = Regex.Matches(grouping, NumSepPattern);
            List<string> nas = TrimAndStringify(NumbersAndSeparators);
            

            if (nas.Count == 1)
            {
                if (IsSeparator(nas[0]))
                    nas[0] = "1";
                ProcessedNumber = double.Parse(nas[0]);
            }
            else
            {
                ProcessedNumber = ProcessBasedOnSeparator(nas, isPack);
            }

            return ProcessedNumber;
        }

        private static double ProcessBasedOnSeparator(List<string> nas, bool isPack)
        {
            double ProcessedNumber = 1;

            if (nas.Count == 3 && IsSeparator(nas[1]))
            {
                double num1, num2;
                num1 = double.Parse(nas[0]);
                num2 = double.Parse(nas[2]);

                if (nas[1] == "/")
                    if (num1 >= 30 && num2 >= 30)
                        ProcessedNumber = Math.Max(num1, num2);
                    else if ((num1 > 9 && num2 > 10) || isPack)
                        ProcessedNumber = num1 * num2;
                    else
                        ProcessedNumber = num1 / num2;
                else if (nas[1] == "-")
                    ProcessedNumber = Math.Max(num1, num2);
                else if (nas[1] == "x")
                    ProcessedNumber = Math.Max(num1, num2);
                else
                    ProcessedNumber = Math.Max(num1, num2);
            }
            else
            {
                double max = -1;
                foreach (string ns in nas)
                {
                    if (IsSeparator(ns) == false)
                    {
                        ProcessedNumber = double.Parse(ns);
                        if (ProcessedNumber > max)
                            max = ProcessedNumber;
                    }

                }
            }

            return ProcessedNumber;
        }
    }
}