using Autodesk.AutoCAD.Runtime;
using NLog;

namespace Cad.Plugin
{
    public class TestPluginEntry : IExtensionApplication
    {
        public void Initialize()
        {
            //LoggerSetup.Configure(); 
        }

        public void Terminate()
        {
            LogManager.Flush();
        }
    }
}
