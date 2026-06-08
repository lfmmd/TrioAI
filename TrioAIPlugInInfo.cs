using System;
using System.Collections.Generic;
using System.Resources;
using System.Windows.Documents;
using Trio.SharedLibrary;

namespace TrioAI.MPPlugIn
{
    public class TrioAIPlugInInfo : PlugInInfoBase
    {
        public override IEnumerable<Block> Acknowledgements => null;

        public TrioAIPlugInInfo()
            : base(
                new TrioAIResourceManager(),
                "PlugInFriendlyName",
                "PlugInDescription",
                (Func<IPlugIn>)(() => (IPlugIn)(object)TrioAIPlugIn.Instance),
                new object[0])
        {
        }
    }

    internal class TrioAIResourceManager : ResourceManager
    {
        public override string GetString(string name)
        {
            switch (name)
            {
                case "PlugInFriendlyName": return "TRIO AI助手";
                case "PlugInDescription": return "AI-powered MotionPerfect assistant";
                default: return name;
            }
        }
    }
}
