// Copyright (c) Charlie Poole, Rob Prouse and Contributors. MIT License - see LICENSE.txt

using System.Diagnostics;

namespace NUnit.Agents
{
    public class PackageTestRunner : NUnitAgent<PackageTestRunner>
    {
        public static void Main(string[] args)
        {
            Execute(args);
        }
    }
}
